#!/usr/bin/env python3
"""Shared policy/action definitions for SmashSync training and inference.

The module intentionally avoids importing PyTorch at import time so the launcher
and lightweight tests still work before ML dependencies are installed.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Sequence, Tuple

from smash_ai_env import ControllerAction, MAX_FIGHTERS, SmashObservation

ENCODER_VERSION = 1
ACTION_TABLE_VERSION = 1


@dataclass(frozen=True)
class PolicyAction:
    name: str
    control: ControllerAction


def _make_action_table() -> Tuple[PolicyAction, ...]:
    actions: List[PolicyAction] = []

    def add(name: str, *, buttons=(), lx=0.0, ly=0.0, rx=0.0, ry=0.0) -> None:
        actions.append(
            PolicyAction(
                name,
                ControllerAction.from_buttons(
                    *buttons,
                    lx=lx,
                    ly=ly,
                    rx=rx,
                    ry=ry,
                ),
            )
        )

    # Neutral and movement.
    add("neutral")
    directions = [
        ("left", -1.0, 0.0),
        ("right", 1.0, 0.0),
        ("up", 0.0, 1.0),
        ("down", 0.0, -1.0),
        ("up_left", -0.707, 0.707),
        ("up_right", 0.707, 0.707),
        ("down_left", -0.707, -0.707),
        ("down_right", 0.707, -0.707),
    ]
    for name, x, y in directions:
        add(f"move_{name}", lx=x, ly=y)

    # A and B with neutral/cardinal/diagonal stick directions.
    for button in ("a", "b"):
        add(button, buttons=(button,))
        for name, x, y in directions:
            add(f"{button}_{name}", buttons=(button,), lx=x, ly=y)

    # Jump, aerial drift, shield/defensive movement, and shoulder input.
    add("jump", buttons=("x",))
    add("jump_left", buttons=("x",), lx=-1.0)
    add("jump_right", buttons=("x",), lx=1.0)
    add("jump_up", buttons=("x",), ly=1.0)

    add("shield", buttons=("zr",))
    add("shield_left", buttons=("zr",), lx=-1.0)
    add("shield_right", buttons=("zr",), lx=1.0)

    add("shoulder_l", buttons=("l",))
    add("shoulder_r", buttons=("r",))

    # Default right-stick attacks, useful even if the player changes left-stick
    # mappings. These remain controller primitives; SSBU's control profile
    # decides the exact in-game interpretation.
    add("rstick_left", rx=-1.0)
    add("rstick_right", rx=1.0)
    add("rstick_up", ry=1.0)
    add("rstick_down", ry=-1.0)

    return tuple(actions)


ACTION_TABLE = _make_action_table()
ACTION_NAMES = tuple(action.name for action in ACTION_TABLE)


def action_from_index(index: int) -> ControllerAction:
    if not 0 <= index < len(ACTION_TABLE):
        raise IndexError(f"action index {index} is outside 0..{len(ACTION_TABLE)-1}")
    return ACTION_TABLE[index].control


def _clamp(value: float, lo: float = -1.0, hi: float = 1.0) -> float:
    return max(lo, min(hi, float(value)))


def encode_observation(
    observation: SmashObservation,
    controlled_slot: int,
) -> Tuple[float, ...]:
    """Return a stable normalized policy vector.

    The controlled fighter is always encoded first, followed by the other
    fighter slots. This lets one policy control P1, P2, or P3 with the same
    self/opponent ordering.
    """
    if not 0 <= controlled_slot < MAX_FIGHTERS:
        raise ValueError("controlled_slot must be 0, 1, or 2")

    features: List[float] = [
        1.0 if observation.in_match else 0.0,
        _clamp(observation.fighter_count / 3.0, 0.0, 1.0),
        _clamp(observation.remaining_frames / 36000.0, 0.0, 1.0),
        _clamp(observation.stage_id / 200.0),
    ]

    self_fighter = observation.fighters[controlled_slot]
    self_x = self_fighter.x if self_fighter.present else 0.0

    order = [controlled_slot] + [
        slot for slot in range(MAX_FIGHTERS) if slot != controlled_slot
    ]

    for slot in order:
        fighter = observation.fighters[slot]
        if not fighter.present:
            features.extend([0.0] * 17)
            continue

        motion_progress = 0.0
        if fighter.motion_end_frame > 0.001:
            motion_progress = fighter.motion_frame / fighter.motion_end_frame

        features.extend(
            [
                1.0,
                _clamp(fighter.fighter_kind / 100.0),
                _clamp(fighter.status_kind / 600.0),
                _clamp(fighter.situation_kind / 5.0),
                _clamp(fighter.stocks / 8.0, 0.0, 1.0),
                1.0 if fighter.is_cpu else 0.0,
                _clamp((fighter.x - self_x) / 200.0),
                _clamp(fighter.y / 150.0),
                _clamp(fighter.speed_x / 12.0),
                _clamp(fighter.speed_y / 12.0),
                _clamp(fighter.facing),
                _clamp(fighter.percent / 250.0, 0.0, 2.0),
                _clamp(motion_progress, 0.0, 1.5),
                _clamp(fighter.motion_end_frame / 180.0, 0.0, 2.0),
                _clamp(fighter.jumps_used / 3.0, 0.0, 2.0),
                _clamp(fighter.jumps_max / 3.0, 0.0, 2.0),
                1.0 if observation.fighter_dead(slot) else 0.0,
            ]
        )

    return tuple(features)


OBSERVATION_DIM = 4 + 17 * MAX_FIGHTERS


def import_torch():
    try:
        import torch
    except ImportError as exc:
        raise RuntimeError(
            "PyTorch is not installed. Run Install-SmashAI-ML.bat first."
        ) from exc
    return torch


def select_device(preference: str = "auto"):
    torch = import_torch()
    preference = preference.lower()

    if preference in ("auto", "directml"):
        try:
            import torch_directml

            device = torch_directml.device()
            return device, "DirectML"
        except Exception:
            if preference == "directml":
                raise RuntimeError(
                    "DirectML was requested but torch-directml is not available. "
                    "Run Install-SmashAI-ML.bat."
                )

    if preference in ("auto", "cuda") and torch.cuda.is_available():
        return torch.device("cuda"), f"CUDA ({torch.cuda.get_device_name(0)})"

    if preference == "cuda":
        raise RuntimeError("CUDA was requested but no CUDA device is available.")

    return torch.device("cpu"), "CPU"


def build_actor_critic(
    observation_dim: int = OBSERVATION_DIM,
    action_dim: int = len(ACTION_TABLE),
):
    torch = import_torch()
    nn = torch.nn

    class ActorCritic(nn.Module):
        def __init__(self):
            super().__init__()
            self.body = nn.Sequential(
                nn.Linear(observation_dim, 256),
                nn.Tanh(),
                nn.Linear(256, 256),
                nn.Tanh(),
            )
            self.policy = nn.Linear(256, action_dim)
            self.value = nn.Linear(256, 1)

            for module in self.modules():
                if isinstance(module, nn.Linear):
                    nn.init.orthogonal_(module.weight, math.sqrt(2.0))
                    nn.init.zeros_(module.bias)
            nn.init.orthogonal_(self.policy.weight, 0.01)
            nn.init.orthogonal_(self.value.weight, 1.0)

        def forward(self, observation_tensor):
            hidden = self.body(observation_tensor)
            return self.policy(hidden), self.value(hidden).squeeze(-1)

    return ActorCritic()


def checkpoint_metadata(
    *,
    fighter_kind: int | None = None,
    controlled_slot: int = 0,
    action_repeat: int = 3,
) -> Dict[str, Any]:
    return {
        "format": "SmashSync-PPO",
        "format_version": 1,
        "encoder_version": ENCODER_VERSION,
        "action_table_version": ACTION_TABLE_VERSION,
        "observation_dim": OBSERVATION_DIM,
        "action_dim": len(ACTION_TABLE),
        "action_names": list(ACTION_NAMES),
        "fighter_kind": fighter_kind,
        "controlled_slot": controlled_slot,
        "action_repeat": action_repeat,
    }


def validate_checkpoint_metadata(metadata: Dict[str, Any]) -> None:
    if metadata.get("format") != "SmashSync-PPO":
        raise RuntimeError("This is not a SmashSync PPO checkpoint.")
    if metadata.get("encoder_version") != ENCODER_VERSION:
        raise RuntimeError(
            f"checkpoint encoder version {metadata.get('encoder_version')} "
            f"does not match runtime version {ENCODER_VERSION}"
        )
    if metadata.get("action_table_version") != ACTION_TABLE_VERSION:
        raise RuntimeError(
            f"checkpoint action table version {metadata.get('action_table_version')} "
            f"does not match runtime version {ACTION_TABLE_VERSION}"
        )
    if metadata.get("observation_dim") != OBSERVATION_DIM:
        raise RuntimeError("checkpoint observation size does not match this runtime")
    if metadata.get("action_dim") != len(ACTION_TABLE):
        raise RuntimeError("checkpoint action size does not match this runtime")


def save_checkpoint(
    path: str | Path,
    *,
    model,
    optimizer=None,
    metadata: Dict[str, Any],
    update: int,
    environment_steps: int,
    extra: Dict[str, Any] | None = None,
) -> None:
    torch = import_torch()
    payload = {
        "metadata": dict(metadata),
        "model_state": model.state_dict(),
        "optimizer_state": None if optimizer is None else optimizer.state_dict(),
        "update": int(update),
        "environment_steps": int(environment_steps),
        "extra": {} if extra is None else dict(extra),
    }
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    torch.save(payload, temp)
    temp.replace(path)


def load_checkpoint(path: str | Path, device="cpu"):
    torch = import_torch()
    checkpoint = torch.load(Path(path), map_location="cpu", weights_only=False)
    metadata = checkpoint.get("metadata") or {}
    validate_checkpoint_metadata(metadata)
    model = build_actor_critic()
    model.load_state_dict(checkpoint["model_state"])
    model.to(device)
    return model, checkpoint
