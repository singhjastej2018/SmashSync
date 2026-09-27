# SmashSync v0

SmashSync is opt-in. Without a `smashsync.json` file in the SmashSync data directory, input behaves like normal Ryujinx.

The v0 protocol uses a **shared logical input tick** at a configured rate (60 Hz by default). Local Ryujinx poll counters and wall-clock timestamps are not gameplay authority. Timestamps are used only for pacing and RTT diagnostics.

## Windows x64 first

The first supported test target is Windows x64 on both machines, using the same SmashSync commit/build and matching SSBU version, update/DLC, firmware, and relevant emulator settings.

## Record

```json
{
  "Mode": "Record",
  "RecordFile": "smashsync-record.jsonl"
}
```

This writes the exact P1/P2 input stream indexed by SmashSync shared logical input tick.

## Replay

```json
{
  "Mode": "Replay",
  "ReplayFile": "smashsync-record.jsonl",
  "ConfigureTwoPlayers": true
}
```

Replay replaces P1/P2 controller state with the recorded stream and forces P1/P2 motion neutral. The session log prints a rolling input digest every 60 ticks.

## Netplay

Player 1 machine:

```json
{
  "Mode": "Netplay",
  "LocalPlayer": 1,
  "PhysicalPlayer": 1,
  "PeerAddress": "100.x.x.x",
  "LocalPort": 27888,
  "PeerPort": 27888,
  "SyncHz": 60,
  "InputDelayTicks": 2,
  "Redundancy": 3,
  "RequireReadyChord": true,
  "ConfigureTwoPlayers": true
}
```

Player 2 machine can keep its physical controller configured in Ryujinx as P1 and remap it to canonical P2:

```json
{
  "Mode": "Netplay",
  "LocalPlayer": 2,
  "PhysicalPlayer": 1,
  "PeerAddress": "100.x.x.x",
  "LocalPort": 27888,
  "PeerPort": 27888,
  "SyncHz": 60,
  "InputDelayTicks": 2,
  "Redundancy": 3,
  "RequireReadyChord": true,
  "ConfigureTwoPlayers": true
}
```

Use each other’s Tailscale IPv4 address for `PeerAddress`.

## Start synchronization

When both players reach the desired in-match state, each holds:

`L + R + Plus + Minus`

SmashSync removes that chord from game input and requests an emulator pause. Both instances remain paused while they exchange READY/START messages. P1 creates the session ID. When the barrier completes, both reset their shared logical input counter to tick 0 and resume.

They do **not** need matching local emulator frame numbers or clocks.

## Runtime lag

Before advancing a shared tick that requires remote input, SmashSync checks whether that exact remote tick has arrived. If it has not, SmashSync pauses the emulator before the HID update, keeps receiving UDP packets while paused, and resumes when the input is available.

This is buffered lockstep, not rollback. If one machine cannot sustain full-speed emulation, the session will stall to the slower machine.

## Packet model

Input packets contain:

- protocol/session ID
- shared logical tick
- sequence number
- canonical player ID
- buttons and both sticks
- up to 3 recent input ticks for loss redundancy

Ping/pong timestamps are diagnostics only.

## Current limitation

The v0 shared tick is paced independently at 60 Hz and applied through Ryujinx HID; it is not yet a verified SSBU internal simulation-frame counter. Matching input digests prove both instances consumed the same controller stream; they do not by themselves prove whole-game determinism. State hashing is the next milestone before rollback.


## Determinism baseline

For the first two-PC validation, keep these identical on both machines:

- the exact SmashSync build/commit
- SSBU base game version and update
- installed DLC set
- system firmware
- relevant emulator settings and graphics backend
- match rules, stage, fighters, and other gameplay-affecting options
- mods/cheats disabled unless they are intentionally identical

Normal SSBU save data does not have to remain identical forever, but using equivalent or copied save data for the first validation removes differences in unlocks, rulesets, and settings.

Shader caches do not need to match and should remain local to each machine. Pre-warming caches can reduce one-sided shader-compilation stalls, but the cache is not part of SmashSync's shared logical state.

SmashSync v0 does not depend on emulator save-state/snapshot functionality.
