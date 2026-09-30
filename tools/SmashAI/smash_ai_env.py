#!/usr/bin/env python3
"""Gym-like client for the SmashSync/Ryujinx Smash AI bridge.

This module has no third-party dependencies. It parses the 288-byte SSAI0001
observation block, sends controller overrides, and offers a best-effort
``reset``/``step`` API keyed to the exporter's emulated-frame counter.

The emulator is currently free-running: ``step`` waits until at least
``action_repeat`` exported game frames have elapsed. It does not pause or
single-step Ryujinx, so a busy host may advance more than the requested count.
"""

from __future__ import annotations

import argparse
import socket
import struct
import time
from dataclasses import dataclass
from typing import Iterable, Optional, Tuple

BRIDGE_MAGIC = b"SAI1"
STATE_MAGIC = b"SSAI0001"
PROTOCOL_VERSION = 1
DEFAULT_PORT = 24872
MAX_FIGHTERS = 3
EXPECTED_STATE_SIZE = 288

MSG_ACTION = 0x01
MSG_OBSERVE = 0x02
MSG_PING = 0x03
MSG_CLEAR = 0x04
MSG_OBSERVATION_RESPONSE = 0x82
MSG_PONG = 0x83

BUTTONS = {
    "a": 1 << 0,
    "b": 1 << 1,
    "x": 1 << 2,
    "y": 1 << 3,
    "lstick": 1 << 4,
    "rstick": 1 << 5,
    "l": 1 << 6,
    "r": 1 << 7,
    "zl": 1 << 8,
    "zr": 1 << 9,
    "plus": 1 << 10,
    "minus": 1 << 11,
    "dpad_left": 1 << 12,
    "dpad_up": 1 << 13,
    "dpad_right": 1 << 14,
    "dpad_down": 1 << 15,
}

_SHARED_HEADER = struct.Struct("<8sIIII")
_SHARED_META = struct.Struct("<QI iII")
_FIGHTER = struct.Struct("<QIiiiiIffffffQffii")
_ACTION = struct.Struct("<4sBBHqhhhh")


class SmashAiError(RuntimeError):
    pass


class BridgeOffline(SmashAiError):
    pass


class ObservationUnavailable(SmashAiError):
    pass


class ProtocolError(SmashAiError):
    pass


class StepTimeout(SmashAiError):
    pass


def _clamp_axis(value: float) -> int:
    value = max(-1.0, min(1.0, float(value)))
    return round(value * (32767 if value >= 0 else 32768))


def _button_mask(buttons: Iterable[str]) -> int:
    mask = 0
    for button in buttons:
        key = button.lower()
        if key not in BUTTONS:
            raise ValueError(f"unknown button {button!r}; choices: {', '.join(sorted(BUTTONS))}")
        mask |= BUTTONS[key]
    return mask


@dataclass(frozen=True)
class ControllerAction:
    buttons: int = 0
    lx: float = 0.0
    ly: float = 0.0
    rx: float = 0.0
    ry: float = 0.0

    @classmethod
    def from_buttons(
        cls,
        *buttons: str,
        lx: float = 0.0,
        ly: float = 0.0,
        rx: float = 0.0,
        ry: float = 0.0,
    ) -> "ControllerAction":
        return cls(_button_mask(buttons), lx, ly, rx, ry)


@dataclass(frozen=True)
class FighterState:
    sample_frame: int
    present: bool
    fighter_kind: int
    status_kind: int
    situation_kind: int
    stocks: int
    is_cpu: bool
    x: float
    y: float
    speed_x: float
    speed_y: float
    facing: float
    percent: float
    motion_kind: int
    motion_frame: float
    motion_end_frame: float
    jumps_used: int
    jumps_max: int

    @classmethod
    def parse_from(cls, data: bytes, offset: int) -> "FighterState":
        values = _FIGHTER.unpack_from(data, offset)
        return cls(
            sample_frame=values[0],
            present=bool(values[1]),
            fighter_kind=values[2],
            status_kind=values[3],
            situation_kind=values[4],
            stocks=values[5],
            is_cpu=bool(values[6]),
            x=values[7],
            y=values[8],
            speed_x=values[9],
            speed_y=values[10],
            facing=values[11],
            percent=values[12],
            motion_kind=values[13],
            motion_frame=values[14],
            motion_end_frame=values[15],
            jumps_used=values[16],
            jumps_max=values[17],
        )

    def vector(self) -> Tuple[float, ...]:
        """Simple numeric representation suitable for early ML experiments."""
        return (
            float(self.present),
            float(self.fighter_kind),
            float(self.status_kind),
            float(self.situation_kind),
            float(self.stocks),
            float(self.is_cpu),
            self.x,
            self.y,
            self.speed_x,
            self.speed_y,
            self.facing,
            self.percent,
            float(self.motion_kind),
            self.motion_frame,
            self.motion_end_frame,
            float(self.jumps_used),
            float(self.jumps_max),
        )


@dataclass(frozen=True)
class SmashObservation:
    protocol_version: int
    total_size: int
    sequence: int
    fighter_count: int
    frame: int
    remaining_frames: int
    stage_id: int
    flags: int
    fighters: Tuple[FighterState, FighterState, FighterState]

    @property
    def in_match(self) -> bool:
        return bool(self.flags & 1)

    @classmethod
    def parse(cls, data: bytes) -> "SmashObservation":
        if len(data) < 48:
            raise ProtocolError(f"observation is too short: {len(data)} bytes")

        magic, protocol_version, total_size, sequence, fighter_count = _SHARED_HEADER.unpack_from(data, 0)
        if magic != STATE_MAGIC:
            raise ProtocolError(f"bad state magic: {magic!r}")
        if protocol_version != PROTOCOL_VERSION:
            raise ProtocolError(f"unsupported state protocol {protocol_version}")
        if total_size < 48 or total_size > len(data):
            raise ProtocolError(f"invalid state size {total_size} for {len(data)}-byte payload")
        if sequence & 1:
            raise ProtocolError("received an observation while its sequence was odd")
        if fighter_count > MAX_FIGHTERS:
            raise ProtocolError(f"invalid fighter count {fighter_count}")
        if total_size < 48 + MAX_FIGHTERS * _FIGHTER.size:
            raise ProtocolError(
                f"state block {total_size} is smaller than protocol-v1 layout "
                f"({48 + MAX_FIGHTERS * _FIGHTER.size})"
            )

        frame, remaining_frames, stage_id, flags, _reserved = _SHARED_META.unpack_from(data, 24)
        fighters = tuple(
            FighterState.parse_from(data, 48 + index * _FIGHTER.size)
            for index in range(MAX_FIGHTERS)
        )
        return cls(
            protocol_version=protocol_version,
            total_size=total_size,
            sequence=sequence,
            fighter_count=fighter_count,
            frame=frame,
            remaining_frames=remaining_frames,
            stage_id=stage_id,
            flags=flags,
            fighters=fighters,  # type: ignore[arg-type]
        )

    def vector(self) -> Tuple[float, ...]:
        values = [
            float(self.in_match),
            float(self.fighter_count),
            float(self.remaining_frames),
            float(self.stage_id),
        ]
        for fighter in self.fighters:
            values.extend(fighter.vector())
        return tuple(values)

    def summary(self) -> str:
        lines = [
            f"frame={self.frame} in_match={self.in_match} fighters={self.fighter_count} "
            f"stage={self.stage_id} timer_frames={self.remaining_frames}"
        ]
        for index, fighter in enumerate(self.fighters):
            if not fighter.present:
                lines.append(f"P{index + 1}: not present")
                continue
            lines.append(
                f"P{index + 1}: kind={fighter.fighter_kind} stocks={fighter.stocks} "
                f"percent={fighter.percent:.1f} pos=({fighter.x:.2f},{fighter.y:.2f}) "
                f"speed=({fighter.speed_x:.2f},{fighter.speed_y:.2f}) "
                f"status={fighter.status_kind} motion=0x{fighter.motion_kind:x} "
                f"motion_frame={fighter.motion_frame:.1f} jumps={fighter.jumps_used}/{fighter.jumps_max}"
            )
        return "\n".join(lines)


class BridgeClient:
    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = DEFAULT_PORT,
        timeout: float = 0.5,
        retries: int = 3,
    ) -> None:
        self.address = (host, int(port))
        self.timeout = float(timeout)
        self.retries = max(1, int(retries))
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.socket.settimeout(self.timeout)

    def close(self) -> None:
        self.socket.close()

    def __enter__(self) -> "BridgeClient":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()

    def _request(self, packet: bytes, expected_type: int) -> bytes:
        last_timeout: Optional[socket.timeout] = None
        for _ in range(self.retries):
            self.socket.sendto(packet, self.address)
            try:
                data, remote = self.socket.recvfrom(65535)
            except socket.timeout as exc:
                last_timeout = exc
                continue

            if remote[0] not in ("127.0.0.1", "::1"):
                continue
            if len(data) < 5 or data[:4] != BRIDGE_MAGIC or data[4] != expected_type:
                raise ProtocolError(f"unexpected bridge response: {data[:16]!r}")
            return data

        raise BridgeOffline(
            f"bridge did not answer on {self.address[0]}:{self.address[1]}; "
            "launch Ryujinx with Launch-SmashAI.bat"
        ) from last_timeout

    def ping(self) -> None:
        self._request(BRIDGE_MAGIC + bytes([MSG_PING]), MSG_PONG)

    def observe_raw(self) -> bytes:
        response = self._request(BRIDGE_MAGIC + bytes([MSG_OBSERVE]), MSG_OBSERVATION_RESPONSE)
        if len(response) < 8:
            raise ProtocolError("short observation response")
        status = response[5]
        length = struct.unpack_from("<H", response, 6)[0]
        if status != 0:
            raise ObservationUnavailable("SSAI0001 exporter state is not available")
        if len(response) != 8 + length:
            raise ProtocolError(
                f"observation response length mismatch: header={length}, packet={len(response) - 8}"
            )
        return response[8:]

    def observe(self) -> SmashObservation:
        return SmashObservation.parse(self.observe_raw())

    def set_action(self, player: int, action: ControllerAction) -> None:
        if player < 0 or player > 8:
            raise ValueError("player must be 0..8 (0=P1, 1=P2, 2=P3)")
        packet = _ACTION.pack(
            BRIDGE_MAGIC,
            MSG_ACTION,
            player,
            0,
            int(action.buttons),
            _clamp_axis(action.lx),
            _clamp_axis(action.ly),
            _clamp_axis(action.rx),
            _clamp_axis(action.ry),
        )
        self.socket.sendto(packet, self.address)

    def clear(self, player: int) -> None:
        if player < 0 or player > 8:
            raise ValueError("player must be 0..8")
        self.socket.sendto(BRIDGE_MAGIC + bytes([MSG_CLEAR, player]), self.address)


@dataclass(frozen=True)
class RewardConfig:
    damage_scale: float = 0.01
    stock_scale: float = 1.0


def compute_reward(
    previous: SmashObservation,
    current: SmashObservation,
    controlled_slot: int,
    config: RewardConfig = RewardConfig(),
) -> float:
    if not 0 <= controlled_slot < MAX_FIGHTERS:
        raise ValueError("controlled_slot must be 0, 1, or 2")

    prev_self = previous.fighters[controlled_slot]
    cur_self = current.fighters[controlled_slot]
    if not prev_self.present or not cur_self.present:
        return 0.0

    self_damage = 0.0
    if prev_self.stocks == cur_self.stocks:
        self_damage = max(0.0, cur_self.percent - prev_self.percent)
    self_stock_losses = max(0, prev_self.stocks - cur_self.stocks)

    opponent_damage = 0.0
    opponent_stock_losses = 0
    for slot in range(MAX_FIGHTERS):
        if slot == controlled_slot:
            continue
        prev_opponent = previous.fighters[slot]
        cur_opponent = current.fighters[slot]
        if not prev_opponent.present or not cur_opponent.present:
            continue
        if prev_opponent.stocks == cur_opponent.stocks:
            opponent_damage += max(0.0, cur_opponent.percent - prev_opponent.percent)
        opponent_stock_losses += max(0, prev_opponent.stocks - cur_opponent.stocks)

    return (
        config.damage_scale * (opponent_damage - self_damage)
        + config.stock_scale * (opponent_stock_losses - self_stock_losses)
    )


class SmashAiEnv:
    """Small Gym-like wrapper around a free-running Ryujinx instance.

    ``player`` selects the controller override (0=P1, 1=P2, 2=P3).
    ``controlled_slot`` selects which exported fighter receives self-reward. In
    ordinary local matches it normally matches ``player``.
    """

    def __init__(
        self,
        bridge: BridgeClient,
        player: int = 1,
        controlled_slot: Optional[int] = None,
        action_repeat: int = 3,
        poll_interval: float = 0.001,
        step_timeout: float = 2.0,
        reward_config: RewardConfig = RewardConfig(),
    ) -> None:
        if not 0 <= player <= 2:
            raise ValueError("SmashAI training currently supports player 0, 1, or 2")
        if action_repeat < 1:
            raise ValueError("action_repeat must be at least 1")

        self.bridge = bridge
        self.player = player
        self.controlled_slot = player if controlled_slot is None else controlled_slot
        if not 0 <= self.controlled_slot < MAX_FIGHTERS:
            raise ValueError("controlled_slot must be 0, 1, or 2")
        self.action_repeat = int(action_repeat)
        self.poll_interval = max(0.0, float(poll_interval))
        self.step_timeout = max(0.05, float(step_timeout))
        self.reward_config = reward_config
        self.last_observation: Optional[SmashObservation] = None

    def reset(
        self,
        *,
        wait_for_match: bool = True,
        timeout: float = 15.0,
    ) -> Tuple[SmashObservation, dict]:
        """Attach to the current match and establish a reward baseline.

        This does not navigate SSBU menus or restart a match yet. Start/restart
        the match in SSBU, then call reset. If ``wait_for_match`` is true, this
        waits until the exporter reports an active match and the controlled
        fighter is present.
        """
        self.bridge.clear(self.player)
        deadline = time.monotonic() + max(0.1, timeout)
        last_error: Optional[Exception] = None

        while True:
            try:
                observation = self.bridge.observe()
                ready = (
                    not wait_for_match
                    or (
                        observation.in_match
                        and observation.fighters[self.controlled_slot].present
                    )
                )
                if ready:
                    self.bridge.set_action(self.player, ControllerAction())
                    self.last_observation = observation
                    return observation, {
                        "frame": observation.frame,
                        "manual_match_reset": True,
                    }
            except (ObservationUnavailable, BridgeOffline) as exc:
                last_error = exc

            if time.monotonic() >= deadline:
                raise StepTimeout(
                    "timed out waiting for an active exported match; enter the match in SSBU first"
                ) from last_error
            time.sleep(max(self.poll_interval, 0.005))

    def step(self, action: ControllerAction) -> Tuple[SmashObservation, float, bool, bool, dict]:
        if self.last_observation is None:
            raise SmashAiError("call reset() before step()")

        previous = self.last_observation
        target_frame = previous.frame + self.action_repeat
        self.bridge.set_action(self.player, action)
        deadline = time.monotonic() + self.step_timeout
        last_current: Optional[SmashObservation] = None

        try:
            while time.monotonic() < deadline:
                current = self.bridge.observe()
                last_current = current
                if current.frame >= target_frame or (previous.in_match and not current.in_match):
                    reward = compute_reward(
                        previous,
                        current,
                        self.controlled_slot,
                        self.reward_config,
                    )
                    terminated = self._is_terminated(previous, current)
                    self.last_observation = current
                    return current, reward, terminated, False, {
                        "requested_frames": self.action_repeat,
                        "advanced_frames": max(0, current.frame - previous.frame),
                        "target_frame": target_frame,
                    }
                if self.poll_interval:
                    time.sleep(self.poll_interval)
        except Exception:
            self.bridge.clear(self.player)
            raise

        self.bridge.clear(self.player)
        observed = "none" if last_current is None else str(last_current.frame)
        raise StepTimeout(
            f"emulator did not advance to frame {target_frame} within {self.step_timeout:.2f}s "
            f"(last observed frame {observed})"
        )

    def _is_terminated(self, previous: SmashObservation, current: SmashObservation) -> bool:
        if previous.in_match and not current.in_match:
            return True

        previous_fighter = previous.fighters[self.controlled_slot]
        fighter = current.fighters[self.controlled_slot]
        if previous.in_match and not fighter.present:
            return True
        if previous.in_match and previous_fighter.stocks > 0 and fighter.stocks <= 0:
            return True

        opponent_pairs = [
            (previous.fighters[index], current.fighters[index])
            for index in range(MAX_FIGHTERS)
            if index != self.controlled_slot
            and previous.fighters[index].present
            and current.fighters[index].present
        ]
        stock_tracked_opponents = [
            current_fighter
            for previous_fighter, current_fighter in opponent_pairs
            if previous_fighter.stocks > 0
        ]
        if (
            previous.in_match
            and stock_tracked_opponents
            and all(f.stocks <= 0 for f in stock_tracked_opponents)
        ):
            return True

        return False

    def close(self) -> None:
        self.bridge.clear(self.player)

    def __enter__(self) -> "SmashAiEnv":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()


_PRESET_ACTIONS = {
    "neutral": ControllerAction(),
    "left": ControllerAction(lx=-1.0),
    "right": ControllerAction(lx=1.0),
    "up": ControllerAction(ly=1.0),
    "down": ControllerAction(ly=-1.0),
    "a": ControllerAction.from_buttons("a"),
    "b": ControllerAction.from_buttons("b"),
    "jump": ControllerAction.from_buttons("x"),
    "shield": ControllerAction.from_buttons("r"),
}


def _main() -> int:
    parser = argparse.ArgumentParser(description="SmashSync Gym-like environment client")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("inspect", help="decode and print one live SSAI0001 observation")

    demo = sub.add_parser("demo", help="run a short controller/environment step demo")
    demo.add_argument("--player", type=int, default=1, help="0=P1, 1=P2, 2=P3")
    demo.add_argument("--fighter-slot", type=int, default=None)
    demo.add_argument("--action-repeat", type=int, default=3)
    demo.add_argument("--steps", type=int, default=20)
    demo.add_argument("--action", choices=sorted(_PRESET_ACTIONS), default="neutral")

    args = parser.parse_args()

    try:
        with BridgeClient(port=args.port) as bridge:
            bridge.ping()
            print("bridge: online")

            if args.command == "inspect":
                observation = bridge.observe()
                print(observation.summary())
                print(f"vector_length={len(observation.vector())}")
                return 0

            with SmashAiEnv(
                bridge,
                player=args.player,
                controlled_slot=args.fighter_slot,
                action_repeat=args.action_repeat,
            ) as env:
                observation, _ = env.reset()
                print("attached:")
                print(observation.summary())
                action = _PRESET_ACTIONS[args.action]

                total_reward = 0.0
                for step_index in range(args.steps):
                    observation, reward, terminated, truncated, info = env.step(action)
                    total_reward += reward
                    fighter = observation.fighters[env.controlled_slot]
                    print(
                        f"step={step_index + 1:03d} frame={observation.frame} "
                        f"advanced={info['advanced_frames']} reward={reward:+.3f} "
                        f"total={total_reward:+.3f} percent={fighter.percent:.1f} "
                        f"stocks={fighter.stocks}"
                    )
                    if terminated or truncated:
                        print("episode ended; restart/enter the next match manually, then call reset()")
                        break
                return 0
    except SmashAiError as exc:
        print(f"error: {exc}")
        return 2


if __name__ == "__main__":
    raise SystemExit(_main())
