# Smash AI experimental branch

This branch turns Ryujinx into a local SSBU reinforcement-learning environment.

## Current working path

The current build has both directions working:

- SSBU exports a structured `SSAI0001` state block through a standalone Skyline plugin.
- Ryujinx reads that state block directly from the loaded NRO.
- Python can request observations over localhost UDP.
- Python can override P1, P2, or P3 controller input.
- `tools/SmashAI/smash_ai_env.py` provides a small Gym-like `reset()` / `step()` wrapper.

Smashline is not required. Keep Skyline installed, and install only
`libsmash_ai_state.nro` in `romfs/skyline/plugins`.

## Controller setup

For a normal 1v1 training setup:

- P1: physical controller
- P2: configured keyboard/controller slot for the AI bridge to override
- P3-P8: disabled

For 1v1v1, configure P3 as well. The bridge player indices are zero-based:
`0=P1`, `1=P2`, `2=P3`.

Avoid leaving old controller slots enabled. A stale P4 configuration can make one
physical keyboard/control source appear to drive an extra fighter.

## Launcher

Start the included `Launch-SmashAI.bat`. It sets:

```
SMASH_AI_PORT=24872
```

and launches the adjacent `Ryujinx.exe`.

## Observation contract

All integers are little-endian. Protocol v1 is currently 288 bytes.

Shared header and metadata:

```
offset size field
0      8    "SSAI0001"
8      4    protocol version = 1
12     4    total block size = 288
16     4    sequence (odd while writing, even when stable)
20     4    fighter count (0..3)
24     8    exporter frame counter
32     4    remaining timer frames
36     4    stage ID
40     4    flags (bit 0 = in match)
44     4    reserved
48     ...  three FighterState records
```

Each FighterState record is 80 bytes and contains:

- sample frame
- present flag
- fighter/character kind
- status kind
- situation kind
- stocks
- CPU flag
- x/y position
- x/y velocity
- facing
- percent
- motion kind
- motion frame and end frame
- jumps used/max

The Python wrapper converts the full state into a 55-value numeric tuple with
`observation.vector()`.

## Controller packet

```
offset size field
0      4    "SAI1"
4      1    message type = 0x01
5      1    player index (0=P1, 1=P2, 2=P3)
6      2    reserved
8      8    ControllerKeys bitmask
16     2    left stick X  (-32768..32767)
18     2    left stick Y
20     2    right stick X
22     2    right stick Y
```

Message `0x04` with byte 5 set to the player index clears that override.

## First checks

Enter an actual SSBU match before testing.

Raw bridge/state check:

```bat
python SmashAI-tools\test_bridge.py --observe --seconds 0
```

Expected:

```
bridge: online
observation: 288 bytes, magic=b'SSAI0001'
```

Decode the live state:

```bat
python SmashAI-tools\smash_ai_env.py inspect
```

This prints frame, match flag, fighter count, stage/timer, and each fighter's
kind, percent, stocks, position, velocity, status, motion, and jumps.

## Gym-like environment demo

With P2 as the AI-controlled fighter:

```bat
python SmashAI-tools\smash_ai_env.py demo --player 1 --fighter-slot 1 --action neutral --steps 20
```

To visibly test movement:

```bat
python SmashAI-tools\smash_ai_env.py demo --player 1 --fighter-slot 1 --action right --steps 20
```

Preset demo actions are `neutral`, `left`, `right`, `up`, `down`,
`a`, `b`, `jump`, and `shield`.

## Using the wrapper from Python

```python
from smash_ai_env import BridgeClient, ControllerAction, SmashAiEnv

with BridgeClient(port=24872) as bridge:
    bridge.ping()

    with SmashAiEnv(
        bridge,
        player=1,          # P2 controller override
        controlled_slot=1,# P2 exported fighter
        action_repeat=3,
    ) as env:
        observation, info = env.reset()

        while True:
            action = ControllerAction(lx=1.0)
            observation, reward, terminated, truncated, info = env.step(action)

            vector = observation.vector()
            print(observation.frame, reward, vector)

            if terminated or truncated:
                break
```

There are no third-party Python dependencies for this wrapper.

## Reward currently implemented

The starter reward is deliberately simple:

- +0.01 per percent dealt
- -0.01 per percent received
- +1.0 per opponent stock lost
- -1.0 per controlled fighter stock lost

For 1v1v1, damage/stock rewards are summed across the other present fighters.

This is only a baseline reward for environment validation. It is not intended to
be the final competitive training reward.

## Important step/reset limitation

The current emulator is still free-running.

`env.step(action)` sends an action and waits until at least
`action_repeat` exported SSBU frames have elapsed. It does not pause Ryujinx or
advance exactly N guest frames, so the observed frame advance can be greater
than requested if the host is busy.

Likewise, `env.reset()` currently attaches to an already-started match and
establishes a reward baseline. It does not navigate menus, restart a match, or
reset Training Mode automatically yet.

The returned `info` dictionary reports both `requested_frames` and
`advanced_frames` so training code can detect overshoot.

## What is next

The next trainer milestone is:

1. define/quantize the policy action space,
2. add automatic match/episode reset,
3. add deterministic or gated guest-frame stepping for accelerated training,
4. connect PPO/self-play,
5. add DirectML device selection/checkpointing,
6. run parallel emulator workers on separate ports/directories,
7. export final inference weights (for example ONNX) plus metadata.
