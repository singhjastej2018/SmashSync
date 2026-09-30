#!/usr/bin/env python3
"""Parallel multi-emulator PPO trainer for SmashSync.

One central learner controls multiple independent Ryujinx/SSBU workers. Each
worker has its own localhost bridge port and exact-step environment. Rollouts
are collected concurrently, then combined into one PPO update.
"""

from __future__ import annotations

import argparse
import random
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import List, Tuple

from smash_ai_env import SmashAiEnv
from smash_ai_policy import (
    ACTION_TABLE,
    OBSERVATION_DIM,
    action_from_index,
    build_actor_critic,
    checkpoint_metadata,
    encode_observation,
    import_torch,
    load_checkpoint,
    save_checkpoint,
    select_device,
)
from smash_ai_train import (
    move_optimizer_to_device,
    save_metadata_json,
    start_bridge_keepalive,
    wait_for_bridge,
)


def parse_ports(value: str) -> List[int]:
    ports = []
    for part in value.split(","):
        part = part.strip()
        if not part:
            continue
        port = int(part)
        if not 1024 <= port <= 65535:
            raise ValueError(f"invalid bridge port {port}")
        ports.append(port)
    if not ports:
        raise ValueError("at least one bridge port is required")
    if len(set(ports)) != len(ports):
        raise ValueError("bridge ports must be unique")
    return ports


def train(args) -> int:
    torch = import_torch()
    torch.manual_seed(args.seed)
    random.seed(args.seed)

    ports = parse_ports(args.ports)
    worker_count = len(ports)
    device, device_name = select_device(args.device)

    print(
        f"device: {device_name}  workers={worker_count} "
        f"actions={len(ACTION_TABLE)} observation_dim={OBSERVATION_DIM}",
        flush=True,
    )
    print(f"ports: {', '.join(map(str, ports))}", flush=True)

    model_dir = Path(args.model_dir).resolve()
    model_dir.mkdir(parents=True, exist_ok=True)

    bridges = []
    envs = []
    keepalives = []
    executor = ThreadPoolExecutor(max_workers=worker_count)

    try:
        for index, port in enumerate(ports):
            print(f"worker {index + 1}: waiting for bridge UDP {port}...", flush=True)
            bridge = wait_for_bridge(port, args.wait_bridge)
            bridges.append(bridge)
            keepalives.append(start_bridge_keepalive(port))
            envs.append(
                SmashAiEnv(
                    bridge,
                    player=args.player,
                    controlled_slot=args.fighter_slot,
                    action_repeat=args.action_repeat,
                    exact_step=True,
                    reset_mode="training",
                    max_episode_frames=args.max_episode_frames,
                )
            )

        print(
            "all bridges online; enter Training Mode on every worker. "
            "Use the same P1 trainee; CPU character/stage may differ.",
            flush=True,
        )

        observations = [None] * worker_count
        reset_futures = {
            executor.submit(env.reset, timeout=args.wait_match): index
            for index, env in enumerate(envs)
        }
        for future, index in [(f, i) for f, i in reset_futures.items()]:
            observation, _ = future.result()
            observations[index] = observation
            bridges[index].set_fast_mode(True)
            bridges[index].set_presentation_enabled(False)
            print(
                f"worker {index + 1}: match ready fighter_kind="
                f"{observation.fighters[args.fighter_slot].fighter_kind} "
                f"stage={observation.stage_id} frame={observation.frame}",
                flush=True,
            )

        fighter_kinds = {
            observation.fighters[args.fighter_slot].fighter_kind
            for observation in observations
        }
        if len(fighter_kinds) != 1:
            raise RuntimeError(
                "all parallel workers must currently train the same P1 fighter; "
                f"got fighter kinds {sorted(fighter_kinds)}"
            )
        fighter_kind = next(iter(fighter_kinds))

        resume_path = args.resume
        if not resume_path and (model_dir / "latest.pt").exists():
            resume_path = str(model_dir / "latest.pt")
            print(f"auto-resume: {resume_path}", flush=True)

        if resume_path:
            model, checkpoint = load_checkpoint(resume_path, device=device)
            metadata = dict(checkpoint["metadata"])
            previous_kind = metadata.get("fighter_kind")
            if previous_kind is not None and previous_kind != fighter_kind:
                raise RuntimeError(
                    f"checkpoint fighter_kind={previous_kind} but workers use {fighter_kind}"
                )
            optimizer = torch.optim.Adam(
                model.parameters(), lr=args.learning_rate, eps=1e-5
            )
            if checkpoint.get("optimizer_state"):
                optimizer.load_state_dict(checkpoint["optimizer_state"])
                move_optimizer_to_device(optimizer, device)
            update = int(checkpoint.get("update", 0))
            environment_steps = int(checkpoint.get("environment_steps", 0))
            print(
                f"resumed: update={update} env_steps={environment_steps} "
                f"from {Path(resume_path).name}",
                flush=True,
            )
        else:
            model = build_actor_critic()
            model.to(device)
            optimizer = torch.optim.Adam(
                model.parameters(), lr=args.learning_rate, eps=1e-5
            )
            metadata = checkpoint_metadata(
                fighter_kind=fighter_kind,
                controlled_slot=args.fighter_slot,
                action_repeat=args.action_repeat,
            )
            update = 0
            environment_steps = 0

        model.train()
        encoded = [
            encode_observation(observation, args.fighter_slot)
            for observation in observations
        ]

        episode_rewards = [0.0] * worker_count
        episode_steps = [0] * worker_count
        worker_episodes = [0] * worker_count
        recent_returns: List[float] = []
        recent_outcomes: List[int] = []
        simulated_frames = 0
        total_episodes = 0
        started = time.monotonic()

        # rollout_steps is a target total batch size. Round upward so every
        # worker contributes equally.
        steps_per_worker = max(1, (args.rollout_steps + worker_count - 1) // worker_count)
        actual_rollout_steps = steps_per_worker * worker_count
        print(
            f"parallel rollout batch: {steps_per_worker} steps/worker "
            f"({actual_rollout_steps} total transitions/update)",
            flush=True,
        )

        while args.updates <= 0 or update < args.updates:
            buffers = [
                {
                    "obs": [],
                    "actions": [],
                    "logprobs": [],
                    "rewards": [],
                    "dones": [],
                    "values": [],
                }
                for _ in range(worker_count)
            ]

            for _round in range(steps_per_worker):
                obs_tensor = torch.tensor(
                    encoded, dtype=torch.float32, device=device
                )
                with torch.no_grad():
                    logits, values = model(obs_tensor)
                    distribution = torch.distributions.Categorical(logits=logits)
                    actions_tensor = distribution.sample()
                    logprobs_tensor = distribution.log_prob(actions_tensor)

                action_indices = [
                    int(actions_tensor[index].item())
                    for index in range(worker_count)
                ]

                futures = [
                    executor.submit(envs[index].step, action_from_index(action_indices[index]))
                    for index in range(worker_count)
                ]
                results = [future.result() for future in futures]

                done_indices = []
                for index, result in enumerate(results):
                    next_observation, reward, terminated, truncated, info = result
                    done = terminated or truncated
                    buffer = buffers[index]

                    buffer["obs"].append(encoded[index])
                    buffer["actions"].append(action_indices[index])
                    buffer["logprobs"].append(float(logprobs_tensor[index].item()))
                    buffer["rewards"].append(float(reward))
                    buffer["dones"].append(1.0 if done else 0.0)
                    buffer["values"].append(float(values[index].item()))

                    environment_steps += 1
                    simulated_frames += int(
                        info.get("advanced_frames", args.action_repeat)
                    )
                    episode_steps[index] += 1
                    episode_rewards[index] += reward

                    if done:
                        total_episodes += 1
                        worker_episodes[index] += 1
                        recent_returns.append(episode_rewards[index])
                        recent_returns = recent_returns[-50:]

                        controlled_dead = next_observation.fighter_dead(
                            args.fighter_slot
                        )
                        opponent_dead = any(
                            slot != args.fighter_slot
                            and fighter.present
                            and next_observation.fighter_dead(slot)
                            for slot, fighter in enumerate(
                                next_observation.fighters
                            )
                        )
                        outcome = 0
                        if terminated:
                            if opponent_dead and not controlled_dead:
                                outcome = 1
                            elif controlled_dead and not opponent_dead:
                                outcome = -1
                        recent_outcomes.append(outcome)
                        recent_outcomes = recent_outcomes[-50:]

                        outcome_name = (
                            "win"
                            if outcome > 0
                            else "loss"
                            if outcome < 0
                            else "draw/timeout"
                        )
                        print(
                            f"worker={index + 1} episode={worker_episodes[index]} "
                            f"outcome={outcome_name} "
                            f"return={episode_rewards[index]:+.3f} "
                            f"steps={episode_steps[index]} "
                            f"virtual_frames={info['episode_frames']}",
                            flush=True,
                        )
                        done_indices.append(index)
                    else:
                        observations[index] = next_observation
                        encoded[index] = encode_observation(
                            next_observation, args.fighter_slot
                        )

                if done_indices:
                    reset_jobs = {
                        index: executor.submit(
                            envs[index].reset, timeout=args.wait_match
                        )
                        for index in done_indices
                    }
                    for index, future in reset_jobs.items():
                        observation, _ = future.result()
                        observations[index] = observation
                        encoded[index] = encode_observation(
                            observation, args.fighter_slot
                        )
                        episode_rewards[index] = 0.0
                        episode_steps[index] = 0

            with torch.no_grad():
                last_tensor = torch.tensor(
                    encoded, dtype=torch.float32, device=device
                )
                _, last_values_tensor = model(last_tensor)
                last_values = [
                    float(last_values_tensor[index].item())
                    for index in range(worker_count)
                ]

            flat_obs: List[Tuple[float, ...]] = []
            flat_actions: List[int] = []
            flat_logprobs: List[float] = []
            flat_advantages: List[float] = []
            flat_returns: List[float] = []

            for worker_index, buffer in enumerate(buffers):
                count = len(buffer["rewards"])
                advantages = [0.0] * count
                returns = [0.0] * count
                gae = 0.0

                for index in reversed(range(count)):
                    next_value = (
                        last_values[worker_index]
                        if index == count - 1
                        else buffer["values"][index + 1]
                    )
                    nonterminal = 1.0 - buffer["dones"][index]
                    delta = (
                        buffer["rewards"][index]
                        + args.gamma * next_value * nonterminal
                        - buffer["values"][index]
                    )
                    gae = (
                        delta
                        + args.gamma
                        * args.gae_lambda
                        * nonterminal
                        * gae
                    )
                    advantages[index] = gae
                    returns[index] = gae + buffer["values"][index]

                flat_obs.extend(buffer["obs"])
                flat_actions.extend(buffer["actions"])
                flat_logprobs.extend(buffer["logprobs"])
                flat_advantages.extend(advantages)
                flat_returns.extend(returns)

            obs_batch = torch.tensor(
                flat_obs, dtype=torch.float32, device=device
            )
            action_batch = torch.tensor(
                flat_actions, dtype=torch.long, device=device
            )
            old_logprob_batch = torch.tensor(
                flat_logprobs, dtype=torch.float32, device=device
            )
            advantage_batch = torch.tensor(
                flat_advantages, dtype=torch.float32, device=device
            )
            return_batch = torch.tensor(
                flat_returns, dtype=torch.float32, device=device
            )

            advantage_batch = (
                advantage_batch - advantage_batch.mean()
            ) / (advantage_batch.std(unbiased=False) + 1e-8)

            batch_size = len(flat_actions)
            policy_losses = []
            value_losses = []
            entropy_values = []

            for _epoch in range(args.ppo_epochs):
                permutation = torch.randperm(
                    batch_size, device="cpu"
                ).tolist()
                for start in range(
                    0, batch_size, args.minibatch_size
                ):
                    ids = permutation[
                        start : start + args.minibatch_size
                    ]
                    ids_tensor = torch.tensor(
                        ids, dtype=torch.long, device=device
                    )

                    logits, values = model(
                        obs_batch.index_select(0, ids_tensor)
                    )
                    distribution = torch.distributions.Categorical(
                        logits=logits
                    )
                    new_logprob = distribution.log_prob(
                        action_batch.index_select(0, ids_tensor)
                    )
                    entropy = distribution.entropy().mean()

                    ratio = (
                        new_logprob
                        - old_logprob_batch.index_select(
                            0, ids_tensor
                        )
                    ).exp()
                    minibatch_advantage = advantage_batch.index_select(
                        0, ids_tensor
                    )
                    unclipped = ratio * minibatch_advantage
                    clipped = torch.clamp(
                        ratio,
                        1.0 - args.clip_range,
                        1.0 + args.clip_range,
                    ) * minibatch_advantage
                    policy_loss = -torch.minimum(
                        unclipped, clipped
                    ).mean()

                    minibatch_returns = return_batch.index_select(
                        0, ids_tensor
                    )
                    value_loss = 0.5 * (
                        minibatch_returns - values
                    ).pow(2).mean()

                    loss = (
                        policy_loss
                        + args.value_coef * value_loss
                        - args.entropy_coef * entropy
                    )

                    optimizer.zero_grad(set_to_none=True)
                    loss.backward()
                    torch.nn.utils.clip_grad_norm_(
                        model.parameters(), args.max_grad_norm
                    )
                    optimizer.step()

                    policy_losses.append(float(policy_loss.item()))
                    value_losses.append(float(value_loss.item()))
                    entropy_values.append(float(entropy.item()))

            update += 1
            elapsed = max(0.001, time.monotonic() - started)
            transitions_per_second = environment_steps / elapsed
            aggregate_sim_fps = simulated_frames / elapsed
            aggregate_speed = aggregate_sim_fps / 60.0
            per_worker_speed = aggregate_speed / worker_count

            mean_return = (
                sum(recent_returns) / len(recent_returns)
                if recent_returns
                else 0.0
            )
            decisive = [
                value for value in recent_outcomes if value != 0
            ]
            win_rate = (
                sum(1 for value in decisive if value > 0)
                / len(decisive)
                if decisive
                else 0.0
            )

            print(
                f"update={update} workers={worker_count} "
                f"env_steps={environment_steps} "
                f"transitions_s={transitions_per_second:.1f} "
                f"sim_fps={aggregate_sim_fps:.1f} "
                f"aggregate_speed={aggregate_speed:.2f}x "
                f"avg_worker_speed={per_worker_speed:.2f}x "
                f"win50={win_rate * 100.0:.1f}% "
                f"mean_return_50={mean_return:+.3f} "
                f"policy_loss="
                f"{sum(policy_losses)/max(1,len(policy_losses)):+.4f} "
                f"value_loss="
                f"{sum(value_losses)/max(1,len(value_losses)):.4f} "
                f"entropy="
                f"{sum(entropy_values)/max(1,len(entropy_values)):.3f}",
                flush=True,
            )

            if (
                update % args.save_every == 0
                or (args.updates > 0 and update >= args.updates)
            ):
                extra = {
                    "episodes": total_episodes,
                    "worker_count": worker_count,
                    "worker_episodes": worker_episodes,
                    "recent_returns": recent_returns,
                    "recent_outcomes": recent_outcomes,
                    "rolling_win_rate": win_rate,
                    "aggregate_simulated_fps": aggregate_sim_fps,
                    "aggregate_realtime_multiple": aggregate_speed,
                    "average_worker_realtime_multiple": per_worker_speed,
                    "device_name": device_name,
                }

                latest = model_dir / "latest.pt"
                save_checkpoint(
                    latest,
                    model=model,
                    optimizer=optimizer,
                    metadata=metadata,
                    update=update,
                    environment_steps=environment_steps,
                    extra=extra,
                )
                save_checkpoint(
                    model_dir / "model.pt",
                    model=model,
                    optimizer=None,
                    metadata=metadata,
                    update=update,
                    environment_steps=environment_steps,
                    extra=extra,
                )
                if update % args.snapshot_every == 0:
                    save_checkpoint(
                        model_dir / f"snapshot_{update:06d}.pt",
                        model=model,
                        optimizer=None,
                        metadata=metadata,
                        update=update,
                        environment_steps=environment_steps,
                        extra=extra,
                    )

                save_metadata_json(
                    model_dir, metadata, update, environment_steps
                )
                print(f"checkpoint: {latest}", flush=True)

        return 0

    finally:
        for bridge in bridges:
            try:
                bridge.set_presentation_enabled(True)
            except Exception:
                pass
        for bridge in bridges:
            try:
                bridge.set_fast_mode(False)
            except Exception:
                pass
        for stop, thread in keepalives:
            try:
                stop.set()
                thread.join(timeout=2.0)
            except Exception:
                pass
        for env in envs:
            try:
                env.close()
            except Exception:
                pass
        for bridge in bridges:
            try:
                bridge.close()
            except Exception:
                pass
        executor.shutdown(wait=False, cancel_futures=True)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Train one SmashSync PPO policy from multiple Ryujinx workers"
    )
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--resume")
    parser.add_argument("--ports", required=True)
    parser.add_argument("--player", type=int, default=0)
    parser.add_argument("--fighter-slot", type=int, default=0)
    parser.add_argument("--action-repeat", type=int, default=3)
    parser.add_argument("--max-episode-frames", type=int, default=3600)

    parser.add_argument(
        "--device",
        choices=("auto", "directml", "cpu", "cuda"),
        default="auto",
    )
    parser.add_argument(
        "--updates", type=int, default=0,
        help="0 means train until stopped"
    )
    parser.add_argument("--rollout-steps", type=int, default=2048)
    parser.add_argument("--minibatch-size", type=int, default=256)
    parser.add_argument("--ppo-epochs", type=int, default=4)
    parser.add_argument("--learning-rate", type=float, default=3e-4)
    parser.add_argument("--gamma", type=float, default=0.995)
    parser.add_argument("--gae-lambda", type=float, default=0.95)
    parser.add_argument("--clip-range", type=float, default=0.2)
    parser.add_argument("--value-coef", type=float, default=0.5)
    parser.add_argument("--entropy-coef", type=float, default=0.01)
    parser.add_argument("--max-grad-norm", type=float, default=0.5)

    parser.add_argument("--save-every", type=int, default=1)
    parser.add_argument("--snapshot-every", type=int, default=25)
    parser.add_argument("--wait-bridge", type=float, default=600.0)
    parser.add_argument("--wait-match", type=float, default=600.0)
    parser.add_argument("--seed", type=int, default=1)
    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()

    try:
        if args.rollout_steps < 32:
            parser.error("--rollout-steps must be at least 32")
        if args.minibatch_size < 1:
            parser.error("--minibatch-size must be positive")
        return train(args)
    except KeyboardInterrupt:
        print("parallel training stopped by user", flush=True)
        return 130
    except Exception as exc:
        print(f"parallel training error: {exc}", flush=True)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
