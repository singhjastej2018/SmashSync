# Smash AI experimental branch

This branch turns Ryujinx into a local SSBU reinforcement-learning environment.

## Current automated-training milestone

The current protocol-v2 build has both data directions plus exact game-frame
gating and automatic Training Mode resets:

- SSBU exports structured state through a standalone Skyline plugin.
- Ryujinx reads that state directly from the loaded NRO.
- Python sends controller states for P1/P2/P3.
- Protocol v2 stops SSBU at a stable once-per-game-frame boundary.
- Python releases an exact number of SSBU frames for each environment step.
- Training Mode episodes can reset automatically with L+R+A.
- The host has a 5-second watchdog that releases the frame gate if the trainer
  crashes or disappears.

Smashline is not required.

## Recommended first training setup

For the first automated loop, use Training Mode:

1. Choose the character being trained as P1.
2. Choose an opponent as the Training CPU.
3. Pick the CPU level and set CPU Behavior to Attack/CPU.
4. Pick the stage once.
5. Enter the match.
6. Start the Python trainer/environment.

After that, the Python environment owns P1 input, detects KO/death states, and
uses Training Mode Reset Positions for the next logical episode. You do not
re-select the map or characters between episodes.

A Training reset is intentionally used instead of leaving the match, traversing
menus, and starting another battle. It produces much less emulator/menu
overhead and gives the learner repeated episodes from the same controlled
starting setup.

## CPU opponents versus self-play

A level-9 CPU is useful as one curriculum opponent, but it should not be the
only opponent.

Reinforcement learning here is not behavioral cloning: the policy does not copy
the CPU's button presses. It receives state, chooses its own actions, and gets
reward for dealing damage/taking stocks while being penalized for damage/being
KO'd. However, training only against one CPU level/character can still overfit
to that opponent's habits.

The planned curriculum is:

- early stabilization against several CPU levels and characters,
- then a growing pool of saved policy snapshots,
- self-play against older/current snapshots,
- CPU opponents retained as additional diversity/evaluation opponents.

This is intended to avoid producing a policy that only knows how to exploit
level-9 CPU behavior.

## Exact stepping

Protocol v2 appends a host-writable control tail to the observation block.

The guest exporter waits at a stable frame boundary when the gate is enabled.
The host supplies a positive `step_budget`, then the guest advances exactly that
many exported SSBU game frames and waits again.

Example with `action_repeat=3`:

```
Python chooses action
-> controller override is updated
-> host sets step_budget=3
-> SSBU advances exactly 3 game frames
-> guest stops at next stable frame boundary
-> Python reads state/reward
-> repeat
```

This is different from GUI Pause/Resume and avoids host polling overshoot.

If the trainer stops sending packets for five seconds, Ryujinx automatically
releases the gate so the game is not left permanently frozen.

## Protocol-v2 observation layout

All integers are little-endian.

```
offset size field
0      8    "SSAI0001"
8      4    protocol version = 2
12     4    total block size = 304
16     4    sequence (odd while state is being written)
20     4    fighter count (0..3)
24     8    exporter frame counter
32     4    remaining timer frames
36     4    stage ID
40     4    flags
44     4    reserved
48     240  three 80-byte FighterState records
288    4    gate_enabled
292    4    step_budget
296    4    gate_epoch
300    4    gate_waiting
```

State flags currently include:

- bit 0: in match
- bits 8..10: P1/P2/P3 DEAD
- bits 12..14: P1/P2/P3 REBIRTH
- bits 16..18: P1/P2/P3 ENTRY

The 80-byte fighter state contains character kind, status/situation, stocks,
CPU flag, position, velocity, facing, percent, motion, and jumps.

## Bridge messages

Existing messages:

- `0x01`: controller action
- `0x02`: observation request
- `0x03`: ping
- `0x04`: clear controller override

Protocol-v2 control:

- `0x05`: enable/disable exact frame gate
- `0x06`: exact step budget

## Install

Keep Skyline installed.

The plugin folder should contain the SmashAI exporter and not Smashline:

```
portable/mods/contents/01006A800016E000/romfs/skyline/plugins/
    libsmash_ai_state.nro
```

Protocol-v2 exact stepping requires the matching new Ryujinx host build and the
matching protocol-v2 NRO.

## Basic checks

Launch with:

```bat
Launch-SmashAI.bat
```

Enter the match, then:

```bat
python SmashAI-tools\test_bridge.py --observe --seconds 0
```

Protocol v2 should report a 304-byte observation.

Inspect decoded state:

```bat
python SmashAI-tools\smash_ai_env.py inspect
```

The output should include:

```
protocol=2 size=304
gate_enabled=False
gate_waiting=False
```

## Exact-step test

In Training Mode, with the learner in P1:

```bat
python SmashAI-tools\smash_ai_env.py demo --player 0 --fighter-slot 0 --action neutral --steps 20
```

Each line should normally show:

```
advanced=3 exact=True
```

With exact mode working, `advanced` should not overshoot the requested
`action_repeat`.

## Automatic reset test

Set the Training CPU to Attack/CPU first, then run:

```bat
python SmashAI-tools\smash_ai_env.py autoreset-demo --player 0 --fighter-slot 0 --episodes 3 --action neutral --max-episode-frames 1800
```

The neutral policy is only a test. The CPU should eventually KO it or the
episode should hit the configured virtual-frame limit. The environment then
holds L+R+A for exact frames, releases it, waits for the reset to settle, and
starts the next logical episode without returning to the stage-select screen.

## Python API

```python
from smash_ai_env import BridgeClient, ControllerAction, SmashAiEnv

with BridgeClient(port=24872) as bridge:
    bridge.ping()

    with SmashAiEnv(
        bridge,
        player=0,
        controlled_slot=0,
        action_repeat=3,
        exact_step=True,
        reset_mode="training",
        max_episode_frames=3600,
    ) as env:

        for episode in range(1000):
            obs, info = env.reset()

            while True:
                action = ControllerAction(lx=1.0)
                obs, reward, terminated, truncated, info = env.step(action)

                if terminated or truncated:
                    break
```

The wrapper has no third-party Python dependencies.

## Starter reward

The environment currently gives:

- +0.01 per percent dealt
- -0.01 per percent received
- +1 for an opponent stock loss
- -1 for a controlled-fighter stock loss
- +1 for an exported opponent KO/death state
- -1 for an exported controlled-fighter KO/death state

This is a validation reward, not the final competitive reward.

## What exact stepping does and does not do

Exact stepping gives deterministic environment synchronization, but it does not
by itself make Switch emulation faster.

Accelerated training is the next performance layer: rendering/audio/presentation
pacing can be reduced or disabled and each worker can execute guest frames as
fast as its CPU resources permit while the guest still sees normal SSBU frame
semantics.

Parallel training will use separate portable directories and separate bridge
ports. Each worker runs its own SSBU instance and environment. Rollouts from all
workers are combined into one learner/update process.

## Remaining major training work

- policy action-space quantization/masking,
- DirectML PPO learner,
- self-play opponent snapshot pool,
- parallel worker launcher/orchestration,
- accelerated/headless presentation mode,
- checkpoints/resume,
- ONNX export and runtime inference,
- later standard-Versus rematch/menu automation for stock-match evaluation.
