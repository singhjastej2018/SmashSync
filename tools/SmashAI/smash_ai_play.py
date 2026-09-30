#!/usr/bin/env python3
"""Run a trained SmashSync checkpoint as a live SSBU controller.

This mode intentionally keeps exact-frame gating disabled. The user can navigate
menus and character select normally; the policy only takes over the requested
player after the exporter reports an active match.
"""

from __future__ import annotations

import argparse
import time
from pathlib import Path

from smash_ai_env import BridgeClient, BridgeOffline, ObservationUnavailable, ProtocolError
from smash_ai_policy import action_from_index, encode_observation, import_torch, load_checkpoint, select_device


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


def choose_action(model, observation, slot: int, device, deterministic: bool) -> int:
    torch = import_torch()
    encoded = encode_observation(observation, slot)
    tensor = torch.tensor(encoded, dtype=torch.float32, device=device).unsqueeze(0)
    with torch.no_grad():
        logits, _ = model(tensor)
        if deterministic:
            return int(torch.argmax(logits, dim=-1).item())
        distribution = torch.distributions.Categorical(logits=logits)
        return int(distribution.sample().item())


def main() -> int:
    parser = argparse.ArgumentParser(description="Play SSBU against a trained SmashSync policy")
    parser.add_argument("--model", required=True)
    parser.add_argument("--port", type=int, default=24872)
    parser.add_argument("--player", type=int, default=1, help="0=P1, 1=P2, 2=P3")
    parser.add_argument("--fighter-slot", type=int, default=None)
    parser.add_argument("--action-repeat", type=int, default=None)
    parser.add_argument("--device", choices=("auto", "directml", "cpu", "cuda"), default="auto")
    parser.add_argument("--stochastic", action="store_true")
    parser.add_argument("--wait-bridge", type=float, default=600.0)
    args = parser.parse_args()

    slot = args.player if args.fighter_slot is None else args.fighter_slot
    if not 0 <= args.player <= 2 or not 0 <= slot <= 2:
        parser.error("player/fighter-slot must be 0, 1, or 2")

    try:
        device, device_name = select_device(args.device)
        model, checkpoint = load_checkpoint(args.model, device=device)
        metadata = checkpoint["metadata"]
        action_repeat = (
            int(metadata.get("action_repeat", 3))
            if args.action_repeat is None
            else args.action_repeat
        )
        if action_repeat < 1:
            raise RuntimeError("action_repeat must be positive")

        model.eval()
        print(f"model: {Path(args.model).resolve()}", flush=True)
        print(f"device: {device_name}", flush=True)
        print(
            f"waiting for match; policy will take over P{args.player + 1} "
            f"when in_match becomes true",
            flush=True,
        )

        bridge = wait_for_bridge(args.port, args.wait_bridge)
        try:
            # Live play must never leave the game behind the training frame gate.
            try:
                bridge.set_frame_gate(False)
            except ProtocolError:
                pass

            active = False
            last_decision_frame = None
            expected_kind = metadata.get("fighter_kind")
            warned_kind = False

            while True:
                try:
                    observation = bridge.observe()
                except ObservationUnavailable:
                    time.sleep(0.002)
                    continue

                fighter = observation.fighters[slot]
                if not observation.in_match or not fighter.present:
                    if active:
                        bridge.clear(args.player)
                        print("match ended; waiting for next match...", flush=True)
                        active = False
                    last_decision_frame = None
                    time.sleep(0.01)
                    continue

                if not active:
                    active = True
                    print(
                        f"match detected: P{args.player + 1} fighter_kind={fighter.fighter_kind}; "
                        "AI control active",
                        flush=True,
                    )

                if (
                    expected_kind is not None
                    and fighter.fighter_kind != expected_kind
                    and not warned_kind
                ):
                    print(
                        f"warning: checkpoint was trained on fighter_kind={expected_kind}, "
                        f"but P{slot + 1} is fighter_kind={fighter.fighter_kind}",
                        flush=True,
                    )
                    warned_kind = True

                if (
                    last_decision_frame is None
                    or observation.frame - last_decision_frame >= action_repeat
                ):
                    action_index = choose_action(
                        model,
                        observation,
                        slot,
                        device,
                        deterministic=not args.stochastic,
                    )
                    bridge.set_action(args.player, action_from_index(action_index))
                    last_decision_frame = observation.frame

                time.sleep(0.001)

        finally:
            try:
                bridge.clear(args.player)
                bridge.set_frame_gate(False)
            except Exception:
                pass
            bridge.close()

    except KeyboardInterrupt:
        print("AI control stopped", flush=True)
        return 130
    except Exception as exc:
        print(f"play error: {exc}", flush=True)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
