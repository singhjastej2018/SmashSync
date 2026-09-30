#!/usr/bin/env python3
"""Gym-like client for the SmashSync/Ryujinx Smash AI bridge.

Protocol v2 adds an exact SSBU game-frame gate. When enabled, the Skyline
exporter stops the game's once-per-frame thread at a stable frame boundary.
The host can then supply an exact positive step budget. This makes
``env.step(action)`` advance exactly ``action_repeat`` exported game frames
instead of racing a free-running emulator.

The environment also supports automatic Training Mode episode resets using
Ultimate's Reset Positions shortcut (L+R+A). You still choose the character,
opponent, CPU level/behavior, and stage once before starting the trainer.
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
SUPPORTED_PROTOCOLS = (1, 2)
DEFAULT_PORT = 24872
MAX_FIGHTERS = 3
PROTOCOL_V1_SIZE = 288
PROTOCOL_V2_SIZE = 304

MSG_ACTION = 0x01
MSG_OBSERVE = 0x02
MSG_PING = 0x03
MSG_CLEAR = 0x04
MSG_FRAME_GATE = 0x05
MSG_STEP_FRAMES = 0x06

MSG_OBSERVATION_RESPONSE = 0x82
MSG_PONG = 0x83
MSG_FRAME_GATE_RESPONSE = 0x85
MSG_STEP_FRAMES_RESPONSE = 0x86

FLAG_IN_MATCH = 1 << 0
FLAG_DEAD_BASE = 8
FLAG_REBIRTH_BASE = 12
FLAG_ENTRY_BASE = 16

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
_CONTROL_V2 = struct.Struct("<IIII")
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
    gate_enabled: bool = False
    step_budget: int = 0
    gate_epoch: int = 0
    gate_waiting: bool = False

    @property
    def in_match(self) -> bool:
        return bool(self.flags & FLAG_IN_MATCH)

    @property
    def exact_step_available(self) -> bool:
        return self.protocol_version >= 2 and self.total_size >= PROTOCOL_V2_SIZE

    def fighter_dead(self, slot: int) -> bool:
        return bool(self.flags & (1 << (FLAG_DEAD_BASE + slot)))

    def fighter_rebirthing(self, slot: int) -> bool:
        return bool(self.flags & (1 << (FLAG_REBIRTH_BASE + slot)))

    def fighter_entering(self, slot: int) -> bool:
        return bool(self.flags & (1 << (FLAG_ENTRY_BASE + slot)))

    @classmethod
    def parse(cls, data: bytes) -> "SmashObservation":
        if len(data) < 48:
            raise ProtocolError(f"observation is too short: {len(data)} bytes")

        magic, protocol_version, total_size, sequence, fighter_count = _SHARED_HEADER.unpack_from(data, 0)
        if magic != STATE_MAGIC:
            raise ProtocolError(f"bad state magic: {magic!r}")
        if protocol_version not in SUPPORTED_PROTOCOLS:
            raise ProtocolError(f"unsupported state protocol {protocol_version}")
        if total_size < PROTOCOL_V1_SIZE or total_size > len(data):
            raise ProtocolError(f"invalid state size {total_size} for {len(data)}-byte payload")
        if sequence & 1:
            raise ProtocolError("received an observation while its sequence was odd")
        if fighter_count > MAX_FIGHTERS:
            raise ProtocolError(f"invalid fighter count {fighter_count}")

        frame, remaining_frames, stage_id, flags, _reserved = _SHARED_META.unpack_from(data, 24)
        fighters = tuple(
            FighterState.parse_from(data, 48 + index * _FIGHTER.size)
            for index in range(MAX_FIGHTERS)
        )

        gate_enabled = False
        step_budget = 0
        gate_epoch = 0
        gate_waiting = False
        if protocol_version >= 2:
            if total_size < PROTOCOL_V2_SIZE:
                raise ProtocolError(
                    f"protocol-v2 block is {total_size} bytes; expected at least {PROTOCOL_V2_SIZE}"
                )
            enabled, budget, epoch, waiting = _CONTROL_V2.unpack_from(data, PROTOCOL_V1_SIZE)
            gate_enabled = bool(enabled)
            step_budget = budget
            gate_epoch = epoch
            gate_waiting = bool(waiting)

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
            gate_enabled=gate_enabled,
            step_budget=step_budget,
            gate_epoch=gate_epoch,
            gate_waiting=gate_waiting,
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
        gate = ""
        if self.exact_step_available:
            gate = (
                f" gate_enabled={self.gate_enabled} gate_waiting={self.gate_waiting} "
                f"gate_epoch={self.gate_epoch} budget={self.step_budget}"
            )
        lines = [
            f"protocol={self.protocol_version} size={self.total_size} frame={self.frame} "
            f"in_match={self.in_match} fighters={self.fighter_count} "
            f"stage={self.stage_id} timer_frames={self.remaining_frames}{gate}"
        ]
        for index, fighter in enumerate(self.fighters):
            if not fighter.present:
                lines.append(f"P{index + 1}: not present")
                continue
            lifecycle = []
            if self.fighter_dead(index):
                lifecycle.append("DEAD")
            if self.fighter_rebirthing(index):
                lifecycle.append("REBIRTH")
            if self.fighter_entering(index):
                lifecycle.append("ENTRY")
            suffix = "" if not lifecycle else " flags=" + ",".join(lifecycle)
            lines.append(
                f"P{index + 1}: kind={fighter.fighter_kind} stocks={fighter.stocks} "
                f"percent={fighter.percent:.1f} pos=({fighter.x:.2f},{fighter.y:.2f}) "
                f"speed=({fighter.speed_x:.2f},{fighter.speed_y:.2f}) "
                f"status={fighter.status_kind} motion=0x{fighter.motion_kind:x} "
                f"motion_frame={fighter.motion_frame:.1f} jumps={fighter.jumps_used}/{fighter.jumps_max}"
                f"{suffix}"
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

    def _request_status(self, packet: bytes, expected_type: int, operation: str) -> None:
        response = self._request(packet, expected_type)
        if len(response) < 6 or response[5] != 0:
            raise ProtocolError(f"{operation} was rejected by the Ryujinx bridge")

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

    def set_frame_gate(self, enabled: bool) -> None:
        packet = BRIDGE_MAGIC + bytes([MSG_FRAME_GATE, 1 if enabled else 0])
        self._request_status(packet, MSG_FRAME_GATE_RESPONSE, "frame gate change")

    def step_frames(self, frames: int) -> None:
        if frames < 1 or frames > 6000:
            raise ValueError("frames must be 1..6000")
        packet = BRIDGE_MAGIC + bytes([MSG_STEP_FRAMES]) + struct.pack("<I", frames)
        self._request_status(packet, MSG_STEP_FRAMES_RESPONSE, "exact frame step")


@dataclass(frozen=True)
class RewardConfig:
    damage_scale: float = 0.01
    stock_scale: float = 1.0
    ko_scale: float = 1.0


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
    opponent_ko = False
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
        opponent_ko = opponent_ko or current.fighter_dead(slot)

    self_ko = current.fighter_dead(controlled_slot)

    return (
        config.damage_scale * (opponent_damage - self_damage)
        + config.stock_scale * (opponent_stock_losses - self_stock_losses)
        + config.ko_scale * (float(opponent_ko) - float(self_ko))
    )


class SmashAiEnv:
    """Gym-like wrapper around one Ryujinx/SSBU instance.

    For the first automated training setup, use Training Mode with the learning
    agent as P1 and the built-in CPU as P2. Choose fighter, CPU level/behavior,
    and stage once, then call reset()/step() repeatedly.
    """

    def __init__(
        self,
        bridge: BridgeClient,
        player: int = 0,
        controlled_slot: Optional[int] = None,
        action_repeat: int = 3,
        poll_interval: float = 0.001,
        step_timeout: float = 2.0,
        reward_config: RewardConfig = RewardConfig(),
        exact_step: bool = True,
        reset_mode: str = "training",
        training_reset_player: int = 0,
        reset_hold_frames: int = 2,
        reset_settle_frames: int = 20,
        max_episode_frames: int = 3600,
    ) -> None:
        if not 0 <= player <= 2:
            raise ValueError("SmashAI training currently supports player 0, 1, or 2")
        if action_repeat < 1:
            raise ValueError("action_repeat must be at least 1")
        if reset_mode not in ("training", "manual"):
            raise ValueError("reset_mode must be 'training' or 'manual'")

        self.bridge = bridge
        self.player = player
        self.controlled_slot = player if controlled_slot is None else controlled_slot
        if not 0 <= self.controlled_slot < MAX_FIGHTERS:
            raise ValueError("controlled_slot must be 0, 1, or 2")
        self.action_repeat = int(action_repeat)
        self.poll_interval = max(0.0, float(poll_interval))
        self.step_timeout = max(0.05, float(step_timeout))
        self.reward_config = reward_config
        self.exact_step = bool(exact_step)
        self.reset_mode = reset_mode
        self.training_reset_player = training_reset_player
        self.reset_hold_frames = max(1, int(reset_hold_frames))
        self.reset_settle_frames = max(1, int(reset_settle_frames))
        self.max_episode_frames = max(0, int(max_episode_frames))

        self.last_observation: Optional[SmashObservation] = None
        self.episode_start_frame: Optional[int] = None
        self._episode_started = False
        self._gate_owned = False

    def _sleep_poll(self) -> None:
        if self.poll_interval:
            time.sleep(self.poll_interval)

    def _wait_for_match(self, timeout: float) -> SmashObservation:
        deadline = time.monotonic() + max(0.1, timeout)
        last_error: Optional[Exception] = None
        while time.monotonic() < deadline:
            try:
                observation = self.bridge.observe()
                if (
                    observation.in_match
                    and observation.fighters[self.controlled_slot].present
                ):
                    return observation
            except (ObservationUnavailable, BridgeOffline) as exc:
                last_error = exc
            time.sleep(max(self.poll_interval, 0.005))
        raise StepTimeout(
            "timed out waiting for an active match; enter Training Mode/the match first"
        ) from last_error

    def _wait_for_gate(self, timeout: Optional[float] = None) -> SmashObservation:
        deadline = time.monotonic() + (self.step_timeout if timeout is None else timeout)
        last: Optional[SmashObservation] = None
        while time.monotonic() < deadline:
            current = self.bridge.observe()
            last = current
            if current.in_match and current.gate_enabled and current.gate_waiting:
                return current
            if not current.in_match:
                return current
            self._sleep_poll()
        last_frame = "none" if last is None else str(last.frame)
        raise StepTimeout(f"timed out waiting for exact-step frame boundary (last frame {last_frame})")

    def _ensure_exact_gate(self, observation: SmashObservation) -> SmashObservation:
        if not self.exact_step:
            return observation
        if not observation.exact_step_available:
            raise ProtocolError(
                "exact stepping requires the protocol-v2 Ryujinx build and protocol-v2 NRO"
            )
        if observation.gate_enabled and observation.gate_waiting:
            self._gate_owned = True
            return observation
        self.bridge.set_frame_gate(True)
        self._gate_owned = True
        return self._wait_for_gate(timeout=max(self.step_timeout, 3.0))

    def _advance_exact(self, frames: int) -> SmashObservation:
        before = self.bridge.observe()
        if not before.in_match:
            return before
        if not (before.gate_enabled and before.gate_waiting):
            before = self._ensure_exact_gate(before)

        start_frame = before.frame
        self.bridge.step_frames(frames)

        deadline = time.monotonic() + self.step_timeout
        last = before
        while time.monotonic() < deadline:
            current = self.bridge.observe()
            last = current
            if not current.in_match:
                return current
            if current.gate_enabled and current.gate_waiting and current.frame >= start_frame + frames:
                advanced = current.frame - start_frame
                if advanced != frames:
                    raise ProtocolError(
                        f"exact gate advanced {advanced} frames; requested {frames}"
                    )
                return current
            self._sleep_poll()

        raise StepTimeout(
            f"exact gate did not complete {frames} frames within {self.step_timeout:.2f}s "
            f"(last frame {last.frame})"
        )

    def _training_reset(self) -> SmashObservation:
        if not self.exact_step:
            raise ProtocolError("automatic Training Mode reset requires exact-step protocol v2")

        current = self._ensure_exact_gate(self.bridge.observe())
        if not current.in_match:
            raise StepTimeout("Training Mode reset requested while no active match is exported")

        reset_action = ControllerAction.from_buttons("a", "l", "r")
        self.bridge.set_action(self.training_reset_player, reset_action)
        current = self._advance_exact(self.reset_hold_frames)

        self.bridge.set_action(self.training_reset_player, ControllerAction())
        current = self._advance_exact(self.reset_settle_frames)

        # If the reset controller is not the learning controller, release the
        # temporary override so its normal configured source/CPU path can resume.
        if self.training_reset_player != self.player:
            self.bridge.clear(self.training_reset_player)

        return current

    def reset(
        self,
        *,
        wait_for_match: bool = True,
        timeout: float = 15.0,
    ) -> Tuple[SmashObservation, dict]:
        """Start a new logical episode.

        On the first call, attach to the match the user already opened. Later
        calls in reset_mode='training' press L+R+A for an automatic Reset
        Positions, then settle for a fixed number of exact game frames.
        """
        observation = self._wait_for_match(timeout) if wait_for_match else self.bridge.observe()
        observation = self._ensure_exact_gate(observation)

        did_auto_reset = False
        if self._episode_started:
            if self.reset_mode == "training":
                observation = self._training_reset()
                did_auto_reset = True
            else:
                self.bridge.set_action(self.player, ControllerAction())

        observation = self._ensure_exact_gate(observation)
        self.bridge.set_action(self.player, ControllerAction())

        self.last_observation = observation
        self.episode_start_frame = observation.frame
        self._episode_started = True

        return observation, {
            "frame": observation.frame,
            "auto_reset": did_auto_reset,
            "exact_step": self.exact_step,
            "gate_epoch": observation.gate_epoch,
        }

    def step(self, action: ControllerAction) -> Tuple[SmashObservation, float, bool, bool, dict]:
        if self.last_observation is None:
            raise SmashAiError("call reset() before step()")

        previous = self.last_observation
        self.bridge.set_action(self.player, action)

        if self.exact_step:
            current = self._advance_exact(self.action_repeat)
        else:
            target_frame = previous.frame + self.action_repeat
            deadline = time.monotonic() + self.step_timeout
            current = previous
            while time.monotonic() < deadline:
                current = self.bridge.observe()
                if current.frame >= target_frame or (previous.in_match and not current.in_match):
                    break
                self._sleep_poll()
            else:
                self.bridge.clear(self.player)
                raise StepTimeout(
                    f"emulator did not advance to frame {target_frame} within {self.step_timeout:.2f}s"
                )

        reward = compute_reward(
            previous,
            current,
            self.controlled_slot,
            self.reward_config,
        )
        terminated = self._is_terminated(previous, current)
        truncated = self._is_truncated(current)

        if terminated or truncated:
            self.bridge.set_action(self.player, ControllerAction())

        self.last_observation = current
        return current, reward, terminated, truncated, {
            "requested_frames": self.action_repeat,
            "advanced_frames": max(0, current.frame - previous.frame),
            "exact_step": self.exact_step,
            "gate_epoch": current.gate_epoch,
            "episode_frames": (
                0
                if self.episode_start_frame is None
                else max(0, current.frame - self.episode_start_frame)
            ),
        }

    def _is_terminated(self, previous: SmashObservation, current: SmashObservation) -> bool:
        if previous.in_match and not current.in_match:
            return True

        if current.fighter_dead(self.controlled_slot):
            return True

        for slot, fighter in enumerate(current.fighters):
            if slot != self.controlled_slot and fighter.present and current.fighter_dead(slot):
                return True

        previous_fighter = previous.fighters[self.controlled_slot]
        fighter = current.fighters[self.controlled_slot]
        if previous.in_match and not fighter.present:
            return True
        if previous_fighter.stocks > 0 and fighter.stocks <= 0:
            return True

        stock_tracked_opponents = [
            current.fighters[index]
            for index in range(MAX_FIGHTERS)
            if index != self.controlled_slot
            and previous.fighters[index].present
            and current.fighters[index].present
            and previous.fighters[index].stocks > 0
        ]
        if stock_tracked_opponents and all(f.stocks <= 0 for f in stock_tracked_opponents):
            return True

        return False

    def _is_truncated(self, current: SmashObservation) -> bool:
        return (
            self.max_episode_frames > 0
            and self.episode_start_frame is not None
            and current.frame - self.episode_start_frame >= self.max_episode_frames
        )

    def close(self) -> None:
        try:
            self.bridge.set_action(self.player, ControllerAction())
            if self.training_reset_player != self.player:
                self.bridge.clear(self.training_reset_player)
            if self._gate_owned:
                self.bridge.set_frame_gate(False)
        finally:
            self.bridge.clear(self.player)
            self._gate_owned = False

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


def _make_env(args, bridge: BridgeClient) -> SmashAiEnv:
    return SmashAiEnv(
        bridge,
        player=args.player,
        controlled_slot=args.fighter_slot,
        action_repeat=args.action_repeat,
        exact_step=not args.free_running,
        reset_mode="training",
        max_episode_frames=args.max_episode_frames,
    )


def _main() -> int:
    parser = argparse.ArgumentParser(description="SmashSync Gym-like environment client")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("inspect", help="decode and print one live SSAI0001 observation")

    demo = sub.add_parser("demo", help="run a short exact-step controller demo")
    demo.add_argument("--player", type=int, default=0, help="0=P1, 1=P2, 2=P3")
    demo.add_argument("--fighter-slot", type=int, default=None)
    demo.add_argument("--action-repeat", type=int, default=3)
    demo.add_argument("--steps", type=int, default=20)
    demo.add_argument("--action", choices=sorted(_PRESET_ACTIONS), default="neutral")
    demo.add_argument("--max-episode-frames", type=int, default=3600)
    demo.add_argument("--free-running", action="store_true")

    auto = sub.add_parser(
        "autoreset-demo",
        help="run repeated Training Mode episodes and reset automatically",
    )
    auto.add_argument("--player", type=int, default=0, help="normally P1 in Training Mode")
    auto.add_argument("--fighter-slot", type=int, default=None)
    auto.add_argument("--action-repeat", type=int, default=3)
    auto.add_argument("--episodes", type=int, default=3)
    auto.add_argument("--action", choices=sorted(_PRESET_ACTIONS), default="neutral")
    auto.add_argument("--max-episode-frames", type=int, default=1800)
    auto.add_argument("--free-running", action="store_true")

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

            action = _PRESET_ACTIONS[args.action]
            with _make_env(args, bridge) as env:
                if args.command == "demo":
                    observation, info = env.reset()
                    print("attached:")
                    print(observation.summary())
                    print(f"reset_info={info}")

                    total_reward = 0.0
                    for step_index in range(args.steps):
                        observation, reward, terminated, truncated, info = env.step(action)
                        total_reward += reward
                        fighter = observation.fighters[env.controlled_slot]
                        print(
                            f"step={step_index + 1:03d} frame={observation.frame} "
                            f"advanced={info['advanced_frames']} exact={info['exact_step']} "
                            f"reward={reward:+.3f} total={total_reward:+.3f} "
                            f"percent={fighter.percent:.1f}"
                        )
                        if terminated or truncated:
                            print(
                                f"episode ended: terminated={terminated} truncated={truncated}; "
                                "run autoreset-demo to test automatic Training resets"
                            )
                            break
                    return 0

                for episode in range(args.episodes):
                    observation, info = env.reset()
                    print(
                        f"episode={episode + 1}/{args.episodes} reset "
                        f"frame={observation.frame} auto_reset={info['auto_reset']}"
                    )
                    total_reward = 0.0
                    steps = 0

                    while True:
                        observation, reward, terminated, truncated, info = env.step(action)
                        total_reward += reward
                        steps += 1
                        if steps % 100 == 0 or terminated or truncated:
                            fighter = observation.fighters[env.controlled_slot]
                            print(
                                f"  step={steps} frame={observation.frame} "
                                f"episode_frames={info['episode_frames']} "
                                f"reward={total_reward:+.3f} percent={fighter.percent:.1f} "
                                f"terminated={terminated} truncated={truncated}"
                            )
                        if terminated or truncated:
                            break

                print("automatic episode/reset test complete")
                return 0

    except (SmashAiError, ValueError) as exc:
        print(f"error: {exc}")
        return 2


if __name__ == "__main__":
    raise SystemExit(_main())
