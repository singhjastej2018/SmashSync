#!/usr/bin/env python3
"""Single-worker PPO trainer for SmashSync.

This is the first real learner milestone: it trains P1 in SSBU Training Mode
against the built-in opponent, uses protocol-v2 exact stepping, automatically
resets episodes, and writes portable PyTorch checkpoints that can also be used
by smash_ai_play.py.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import random
import threading
import time
from pathlib import Path
from typing import Dict, List, Tuple

from smash_ai_env import BridgeClient, BridgeOffline, ObservationUnavailable, SmashAiEnv
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
    validate_checkpoint_metadata,
)


def wait_for_bridge(port: int, timeout: float) -> BridgeClient:
    deadline = time.monotonic() + timeout
    last_error = None
    while time.monotonic() < deadline:
        bridge = BridgeClient(port=port, timeout=0.5, retries=1)
        try:
            bridge.ping()
            return bridge
        except BridgeOffline as exc:
            last_error = exc
            bridge.close()
            time.sleep(0.5)
    raise RuntimeError(
        f"Ryujinx bridge did not appear on UDP {port} within {timeout:.0f}s"
    ) from last_error


def start_bridge_keepalive(port: int):
    stop = threading.Event()

    def loop() -> None:
        bridge = BridgeClient(port=port, timeout=0.5, retries=1)
        try:
            while not stop.wait(1.0):
                try:
                    bridge.ping()
                except Exception:
                    pass
        finally:
            bridge.close()

    thread = threading.Thread(target=loop, name="SmashAI.KeepAlive", daemon=True)
    thread.start()
    return stop, thread


def move_optimizer_to_device(optimizer, device) -> None:
    torch = import_torch()
    for state in optimizer.state.values():
        for key, value in list(state.items()):
            if torch.is_tensor(value):
                state[key] = value.to(device)


def evaluate(model, encoded, device):
    torch = import_torch()
    tensor = torch.tensor(encoded, dtype=torch.float32, device=device).unsqueeze(0)
    with torch.no_grad():
        logits, value = model(tensor)
    return logits.squeeze(0), value.squeeze(0)


def save_metadata_json(model_dir: Path, metadata: Dict, update: int, environment_steps: int) -> None:
    payload = dict(metadata)
    payload.update(
        {
            "latest_update": int(update),
            "environment_steps": int(environment_steps),
            "latest_checkpoint": "latest.pt",
        }
    )
    (model_dir / "model.json").write_text(
        json.dumps(payload, indent=2, sort_keys=True),
        encoding="utf-8",
    )


def train(args) -> int:
    torch = import_torch()
    torch.manual_seed(args.seed)
    random.seed(args.seed)

    device, device_name = select_device(args.device)
    print(f"device: {device_name}", flush=True)
    print(f"actions: {len(ACTION_TABLE)}  observation_dim: {OBSERVATION_DIM}", flush=True)

    model_dir = Path(args.model_dir).resolve()
    model_dir.mkdir(parents=True, exist_ok=True)

    print(
        f"waiting for Ryujinx bridge on UDP {args.port}; "
        "enter Training Mode after Ryujinx opens...",
        flush=True,
    )
    bridge = wait_for_bridge(args.port, args.wait_bridge)
    keepalive_stop, keepalive_thread = start_bridge_keepalive(args.port)
    print("bridge: online; waiting for active Training Mode match...", flush=True)

    env = SmashAiEnv(
        bridge,
        player=args.player,
        controlled_slot=args.fighter_slot,
        action_repeat=args.action_repeat,
        exact_step=True,
        reset_mode="training",
        max_episode_frames=args.max_episode_frames,
    )

    optimizer = None
    update = 0
    environment_steps = 0
    metadata = None

    try:
        observation, reset_info = env.reset(timeout=args.wait_match)
        bridge.set_fast_mode(True)
        fighter_kind = observation.fighters[args.fighter_slot].fighter_kind
        print(
            f"training match detected: fighter_kind={fighter_kind} "
            f"stage={observation.stage_id} frame={observation.frame}",
            flush=True,
        )

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
                    f"checkpoint fighter_kind={previous_kind} but current P"
                    f"{args.fighter_slot + 1} kind={fighter_kind}"
                )
            optimizer = torch.optim.Adam(model.parameters(), lr=args.learning_rate, eps=1e-5)
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
            optimizer = torch.optim.Adam(model.parameters(), lr=args.learning_rate, eps=1e-5)
            metadata = checkpoint_metadata(
                fighter_kind=fighter_kind,
                controlled_slot=args.fighter_slot,
                action_repeat=args.action_repeat,
            )

        model.train()
        encoded = encode_observation(observation, args.fighter_slot)

        episode_reward = 0.0
        episode_steps = 0
        episode_count = 0
        recent_returns: List[float] = []
        recent_outcomes: List[int] = []
        simulated_frames = 0
        started = time.monotonic()

        while args.updates <= 0 or update < args.updates:
            obs_buffer: List[Tuple[float, ...]] = []
            action_buffer: List[int] = []
            logprob_buffer: List[float] = []
            reward_buffer: List[float] = []
            done_buffer: List[float] = []
            value_buffer: List[float] = []

            for _ in range(args.rollout_steps):
                obs_tensor = torch.tensor(
                    encoded,
                    dtype=torch.float32,
                    device=device,
                ).unsqueeze(0)
                with torch.no_grad():
                    logits, value = model(obs_tensor)
                    distribution = torch.distributions.Categorical(logits=logits)
                    action_tensor = distribution.sample()
                    logprob_tensor = distribution.log_prob(action_tensor)

                action_index = int(action_tensor.item())
                action = action_from_index(action_index)

                next_observation, reward, terminated, truncated, info = env.step(action)
                done = terminated or truncated

                obs_buffer.append(encoded)
                action_buffer.append(action_index)
                logprob_buffer.append(float(logprob_tensor.item()))
                reward_buffer.append(float(reward))
                done_buffer.append(1.0 if done else 0.0)
                value_buffer.append(float(value.item()))

                environment_steps += 1
                simulated_frames += int(info.get("advanced_frames", args.action_repeat))
                episode_steps += 1
                episode_reward += reward

                if done:
                    episode_count += 1
                    recent_returns.append(episode_reward)
                    recent_returns = recent_returns[-50:]

                    controlled_dead = next_observation.fighter_dead(args.fighter_slot)
                    opponent_dead = any(
                        slot != args.fighter_slot
                        and fighter.present
                        and next_observation.fighter_dead(slot)
                        for slot, fighter in enumerate(next_observation.fighters)
                    )
                    outcome = 0
                    if terminated:
                        if opponent_dead and not controlled_dead:
                            outcome = 1
                        elif controlled_dead and not opponent_dead:
                            outcome = -1
                    recent_outcomes.append(outcome)
                    recent_outcomes = recent_outcomes[-50:]

                    outcome_name = "win" if outcome > 0 else "loss" if outcome < 0 else "draw/timeout"
                    print(
                        f"episode={episode_count} outcome={outcome_name} "
                        f"return={episode_reward:+.3f} steps={episode_steps} "
                        f"virtual_frames={info['episode_frames']} "
                        f"terminated={terminated} truncated={truncated}",
                        flush=True,
                    )
                    next_observation, _ = env.reset(timeout=args.wait_match)
                    episode_reward = 0.0
                    episode_steps = 0

                encoded = encode_observation(next_observation, args.fighter_slot)

            with torch.no_grad():
                last_tensor = torch.tensor(
                    encoded,
                    dtype=torch.float32,
                    device=device,
                ).unsqueeze(0)
                _, last_value_tensor = model(last_tensor)
                last_value = float(last_value_tensor.item())

            advantages = [0.0] * args.rollout_steps
            returns = [0.0] * args.rollout_steps
            gae = 0.0

            for index in reversed(range(args.rollout_steps)):
                if index == args.rollout_steps - 1:
                    next_value = last_value
                else:
                    next_value = value_buffer[index + 1]

                nonterminal = 1.0 - done_buffer[index]
                delta = (
                    reward_buffer[index]
                    + args.gamma * next_value * nonterminal
                    - value_buffer[index]
                )
                gae = delta + args.gamma * args.gae_lambda * nonterminal * gae
                advantages[index] = gae
                returns[index] = gae + value_buffer[index]

            obs_batch = torch.tensor(obs_buffer, dtype=torch.float32, device=device)
            action_batch = torch.tensor(action_buffer, dtype=torch.long, device=device)
            old_logprob_batch = torch.tensor(logprob_buffer, dtype=torch.float32, device=device)
            advantage_batch = torch.tensor(advantages, dtype=torch.float32, device=device)
            return_batch = torch.tensor(returns, dtype=torch.float32, device=device)

            advantage_batch = (
                advantage_batch - advantage_batch.mean()
            ) / (advantage_batch.std(unbiased=False) + 1e-8)

            batch_size = args.rollout_steps
            policy_losses = []
            value_losses = []
            entropy_values = []

            for _epoch in range(args.ppo_epochs):
                permutation = torch.randperm(batch_size, device="cpu").tolist()
                for start in range(0, batch_size, args.minibatch_size):
                    ids = permutation[start : start + args.minibatch_size]
                    ids_tensor = torch.tensor(ids, dtype=torch.long, device=device)

                    logits, values = model(obs_batch.index_select(0, ids_tensor))
                    distribution = torch.distributions.Categorical(logits=logits)
                    new_logprob = distribution.log_prob(action_batch.index_select(0, ids_tensor))
                    entropy = distribution.entropy().mean()

                    ratio = (new_logprob - old_logprob_batch.index_select(0, ids_tensor)).exp()
                    minibatch_advantage = advantage_batch.index_select(0, ids_tensor)
                    unclipped = ratio * minibatch_advantage
                    clipped = torch.clamp(
                        ratio,
                        1.0 - args.clip_range,
                        1.0 + args.clip_range,
                    ) * minibatch_advantage
                    policy_loss = -torch.minimum(unclipped, clipped).mean()

                    minibatch_returns = return_batch.index_select(0, ids_tensor)
                    value_loss = 0.5 * (minibatch_returns - values).pow(2).mean()

                    loss = (
                        policy_loss
                        + args.value_coef * value_loss
                        - args.entropy_coef * entropy
                    )

                    optimizer.zero_grad(set_to_none=True)
                    loss.backward()
                    torch.nn.utils.clip_grad_norm_(model.parameters(), args.max_grad_norm)
                    optimizer.step()

                    policy_losses.append(float(policy_loss.item()))
                    value_losses.append(float(value_loss.item()))
                    entropy_values.append(float(entropy.item()))

            update += 1
            elapsed = max(0.001, time.monotonic() - started)
            steps_per_second = environment_steps / elapsed
            simulated_fps = simulated_frames / elapsed
            realtime_multiple = simulated_fps / 60.0
            mean_return = (
                sum(recent_returns) / len(recent_returns)
                if recent_returns
                else 0.0
            )

            decisive = [value for value in recent_outcomes if value != 0]
            win_rate = (
                sum(1 for value in decisive if value > 0) / len(decisive)
                if decisive
                else 0.0
            )

            print(
                f"update={update} env_steps={environment_steps} "
                f"steps_s={steps_per_second:.1f} sim_fps={simulated_fps:.1f} "
                f"speed={realtime_multiple:.2f}x "
                f"win50={win_rate * 100.0:.1f}% "
                f"mean_return_50={mean_return:+.3f} "
                f"policy_loss={sum(policy_losses)/max(1,len(policy_losses)):+.4f} "
                f"value_loss={sum(value_losses)/max(1,len(value_losses)):.4f} "
                f"entropy={sum(entropy_values)/max(1,len(entropy_values)):.3f}",
                flush=True,
            )

            if update % args.save_every == 0 or (args.updates > 0 and update >= args.updates):
                extra = {
                    "episodes": episode_count,
                    "recent_returns": recent_returns,
                    "recent_outcomes": recent_outcomes,
                    "rolling_win_rate": win_rate,
                    "simulated_fps": simulated_fps,
                    "realtime_multiple": realtime_multiple,
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
                # model.pt is the smaller shareable runtime weights file; latest.pt
                # additionally keeps optimizer state for resume.
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
                save_metadata_json(model_dir, metadata, update, environment_steps)
                print(f"checkpoint: {latest}", flush=True)

        return 0

    finally:
        try:
            bridge.set_fast_mode(False)
        except Exception:
            pass
        try:
            keepalive_stop.set()
            keepalive_thread.join(timeout=2.0)
        except Exception:
            pass
        env.close()
        bridge.close()


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Train a SmashSync PPO policy")
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--resume")
    parser.add_argument("--port", type=int, default=24872)
    parser.add_argument("--player", type=int, default=0)
    parser.add_argument("--fighter-slot", type=int, default=0)
    parser.add_argument("--action-repeat", type=int, default=3)
    parser.add_argument("--max-episode-frames", type=int, default=3600)

    parser.add_argument("--device", choices=("auto", "directml", "cpu", "cuda"), default="auto")
    parser.add_argument("--updates", type=int, default=0, help="0 means train until stopped")
    parser.add_argument("--rollout-steps", type=int, default=1024)
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
    parser.add_argument("--device-check", action="store_true")
    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()

    if args.device_check:
        try:
            _, name = select_device(args.device)
            print(f"device: {name}")
            return 0
        except Exception as exc:
            print(f"device error: {exc}")
            return 2

    if args.rollout_steps < 32:
        parser.error("--rollout-steps must be at least 32")
    if args.minibatch_size < 1:
        parser.error("--minibatch-size must be positive")

    try:
        return train(args)
    except KeyboardInterrupt:
        print("training stopped by user", flush=True)
        return 130
    except Exception as exc:
        print(f"training error: {exc}", flush=True)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
