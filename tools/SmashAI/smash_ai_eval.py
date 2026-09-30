#!/usr/bin/env python3
"""Evaluate a SmashSync policy against the currently selected SSBU opponent."""

from __future__ import annotations

import argparse
import time
from pathlib import Path

from smash_ai_env import BridgeClient, BridgeOffline, SmashAiEnv
from smash_ai_policy import (
    action_from_index,
    encode_observation,
    import_torch,
    load_checkpoint,
    select_device,
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
    raise RuntimeError(f"Ryujinx bridge did not appear on UDP {port}") from last_error


def choose_action(model, observation, slot: int, device) -> int:
    torch = import_torch()
    encoded = encode_observation(observation, slot)
    tensor = torch.tensor(encoded, dtype=torch.float32, device=device).unsqueeze(0)
    with torch.no_grad():
        logits, _ = model(tensor)
    return int(torch.argmax(logits, dim=-1).item())


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Evaluate a trained SmashSync model in Training Mode"
    )
    parser.add_argument("--model", required=True)
    parser.add_argument("--episodes", type=int, default=20)
    parser.add_argument("--port", type=int, default=24872)
    parser.add_argument("--player", type=int, default=0)
    parser.add_argument("--fighter-slot", type=int, default=0)
    parser.add_argument("--max-episode-frames", type=int, default=3600)
    parser.add_argument(
        "--device",
        choices=("auto", "directml", "cpu", "cuda"),
        default="auto",
    )
    parser.add_argument("--wait-bridge", type=float, default=600.0)
    parser.add_argument("--wait-match", type=float, default=600.0)
    parser.add_argument(
        "--normal-speed",
        action="store_true",
        help="leave normal presentation pacing enabled while evaluating",
    )
    args = parser.parse_args()

    if args.episodes < 1:
        parser.error("--episodes must be positive")

    try:
        device, device_name = select_device(args.device)
        model, checkpoint = load_checkpoint(args.model, device=device)
        metadata = checkpoint["metadata"]
        action_repeat = int(metadata.get("action_repeat", 3))
        model.eval()

        print(f"model: {Path(args.model).resolve()}", flush=True)
        print(f"device: {device_name}", flush=True)
        print(
            "waiting for Training Mode match; evaluation uses deterministic actions...",
            flush=True,
        )

        bridge = wait_for_bridge(args.port, args.wait_bridge)
        env = SmashAiEnv(
            bridge,
            player=args.player,
            controlled_slot=args.fighter_slot,
            action_repeat=action_repeat,
            exact_step=True,
            reset_mode="training",
            max_episode_frames=args.max_episode_frames,
        )

        wins = 0
        losses = 0
        draws = 0
        returns = []

        try:
            observation, _ = env.reset(timeout=args.wait_match)
            if not args.normal_speed:
                bridge.set_fast_mode(True)
                bridge.set_presentation_enabled(False)

            expected_kind = metadata.get("fighter_kind")
            actual_kind = observation.fighters[args.fighter_slot].fighter_kind
            if expected_kind is not None and expected_kind != actual_kind:
                print(
                    f"warning: checkpoint fighter_kind={expected_kind}, "
                    f"current fighter_kind={actual_kind}",
                    flush=True,
                )

            for episode in range(1, args.episodes + 1):
                episode_return = 0.0
                terminal_observation = observation
                terminal_info = {"episode_frames": 0}

                while True:
                    action_index = choose_action(
                        model,
                        observation,
                        args.fighter_slot,
                        device,
                    )
                    observation, reward, terminated, truncated, info = env.step(
                        action_from_index(action_index)
                    )
                    episode_return += reward
                    terminal_observation = observation
                    terminal_info = info

                    if terminated or truncated:
                        controlled_dead = terminal_observation.fighter_dead(
                            args.fighter_slot
                        )
                        opponent_dead = any(
                            slot != args.fighter_slot
                            and fighter.present
                            and terminal_observation.fighter_dead(slot)
                            for slot, fighter in enumerate(
                                terminal_observation.fighters
                            )
                        )

                        if terminated and opponent_dead and not controlled_dead:
                            wins += 1
                            outcome = "win"
                        elif terminated and controlled_dead and not opponent_dead:
                            losses += 1
                            outcome = "loss"
                        else:
                            draws += 1
                            outcome = "draw/timeout"

                        returns.append(episode_return)
                        print(
                            f"eval_episode={episode}/{args.episodes} "
                            f"outcome={outcome} return={episode_return:+.3f} "
                            f"virtual_frames={terminal_info['episode_frames']}",
                            flush=True,
                        )
                        break

                if episode < args.episodes:
                    observation, _ = env.reset(timeout=args.wait_match)

            decisive = wins + losses
            decisive_win_rate = wins / decisive if decisive else 0.0
            mean_return = sum(returns) / len(returns) if returns else 0.0
            print(
                f"EVAL summary episodes={args.episodes} wins={wins} losses={losses} "
                f"draws={draws} decisive_win_rate={decisive_win_rate * 100.0:.1f}% "
                f"mean_return={mean_return:+.3f}",
                flush=True,
            )
            return 0
        finally:
            try:
                bridge.set_presentation_enabled(True)
            except Exception:
                pass
            try:
                bridge.set_fast_mode(False)
            except Exception:
                pass
            env.close()
            bridge.close()

    except KeyboardInterrupt:
        print("evaluation stopped by user", flush=True)
        return 130
    except Exception as exc:
        print(f"evaluation error: {exc}", flush=True)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
