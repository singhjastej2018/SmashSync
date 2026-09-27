# SmashSync pre-game netplay

SmashSync is opt-in. Without a `smashsync.json` file in the SmashSync data directory, input behaves like normal Ryujinx.

The current protocol has two separate layers:

1. **Pre-game lobby/control:** TCP on the configured SmashSync port. This exists while the Ryujinx UI is open and carries only SmashSync control messages such as request, accept, heartbeat, and disconnect.
2. **Gameplay input:** fixed-size UDP controller packets on the same numeric port. Keyboard, mouse, game files, save data, video, and audio are never sent by SmashSync.

The default port is `27888`, so Windows Firewall must allow both **TCP 27888** and **UDP 27888** on both PCs.

## Canonical players

Player identity is fixed on both machines:

- P1 is always Player 1.
- P2 is always Player 2.

After the pre-game handshake is accepted, the remote peer appears as a Ryujinx gamepad named similar to:

`SmashSync Remote P2 — 100.x.x.x`

or

`SmashSync Remote P1 — 100.x.x.x`

Configure controllers before launching SSBU:

### P1 machine

- Ryujinx P1: local physical controller
- Ryujinx P2: `SmashSync Remote P2`

### P2 machine

- Ryujinx P1: `SmashSync Remote P1`
- Ryujinx P2: local physical controller

The old `PhysicalPlayer` remapping remains only as a fallback for legacy/chord-based sessions. After an accepted pre-game lobby connection, SmashSync reads the local controller from its canonical `LocalPlayer` slot.

## Netplay configuration

Player 1:

```json
{
  "Mode": "Netplay",
  "LocalPlayer": 1,
  "PhysicalPlayer": 1,
  "PeerAddress": "100.x.x.x",
  "LocalPort": 27888,
  "PeerPort": 27888,
  "SyncHz": 60,
  "InputDelayTicks": 1,
  "Redundancy": 3,
  "RequireReadyChord": false,
  "ConfigureTwoPlayers": true
}
```

Player 2:

```json
{
  "Mode": "Netplay",
  "LocalPlayer": 2,
  "PhysicalPlayer": 2,
  "PeerAddress": "100.x.x.x",
  "LocalPort": 27888,
  "PeerPort": 27888,
  "SyncHz": 60,
  "InputDelayTicks": 1,
  "Redundancy": 3,
  "RequireReadyChord": false,
  "ConfigureTwoPlayers": true
}
```

Use the other PC's Tailscale IPv4 address as `PeerAddress`.

## Pre-game handshake

Launch SmashSync on both PCs before launching SSBU.

The bottom status bar shows the configured peer address and lobby state.

1. One player clicks **Request**.
2. The other player sees an incoming request and clicks **Accept**.
3. Both sides show **connected**.
4. The remote SmashSync controller becomes available in Ryujinx input devices.
5. Configure the canonical P1/P2 controller slots as described above.

A game launch is blocked while Netplay mode is enabled but the pre-game peer handshake is not connected.

## Launch and automatic game barrier

Both PCs still run SSBU locally. SmashSync does not remotely execute programs on the other PC.

Either player may launch SSBU first. The first instance automatically enters a SmashSync-owned pause once the game-side input session is initialized and waits for the other machine to launch. When both game-side sessions are present:

- both exchange READY packets over UDP;
- P1 creates the gameplay session ID;
- P2 acknowledges it;
- both reset the shared logical input tick to 0;
- both resume automatically.

The old `L + R + Plus + Minus` chord remains only as a fallback when no accepted pre-game lobby exists.

## Runtime input synchronization

The shared logical input tick runs at 60 Hz by default. Each input packet carries:

- protocol/session ID;
- shared logical tick;
- sequence number;
- canonical player ID;
- buttons and both sticks;
- up to three recent input ticks for UDP loss redundancy.

Before consuming a tick that requires remote input, SmashSync checks whether that exact remote tick has arrived. If not, it pauses before the HID update while networking remains active, then resumes when the missing input arrives.

This is buffered lockstep, not rollback. The recommended low-latency baseline is one input-delay tick (~16.7 ms at 60 Hz); zero-tick mode is allowed but can stall every frame when network one-way delay exceeds the current frame budget.

## Current limitation

The shared input tick is still a SmashSync HID clock, not a verified SSBU internal simulation-frame counter. The new pre-game handshake and automatic launch barrier make startup substantially more deterministic, but they do not prove that two independent SSBU processes have identical hidden state.

Matching input digests prove that both sides consumed the same controller stream. A future milestone is guest/game state hashing at a verified simulation boundary.

## Determinism baseline

For initial testing, keep these identical on both machines:

- exact SmashSync build/commit;
- SSBU base game/update version;
- installed DLC set;
- system firmware;
- gameplay-affecting emulator settings;
- match rules, stage, fighters, and other gameplay-affecting options;
- mods/cheats disabled unless intentionally identical.

Equivalent save data is useful for eliminating differences in unlocks, rulesets, and settings. Shader caches do not need to match.


## Canonical input routing

During an accepted SmashSync session, only the canonical local player's physical Ryujinx controller is sampled as local gameplay input. The opposite canonical player slot is supplied exclusively by the UDP network stream. The visible `SmashSync Remote P1/P2` device remains available for identification/configuration UI, but it is not polled again by the gameplay path. This prevents remote input from being selected as a local source and echoed back to the peer.

## Synchronized release

For accepted pre-game sessions, the application main thread is suspended with the emulator's normal process-pause flag before guest execution begins. Both peers remain suspended through READY/START. SmashSync measures UDP RTT while paused, then P1 sends a RELEASE barrier with a future lead time. P2 compensates approximately half its measured RTT before scheduling its resume, reducing the one-way START/ACK head-start that previously allowed one guest to begin several milliseconds before the other.


## Strict start-state and HID sequencing

Before releasing an accepted netplay session, SmashSync computes a 64-bit SHA-256-derived fingerprint over the active title/version, language/region/docked mode, and the active title's account save-data contents. Only the fingerprint crosses the network. Save files are not transferred.

If the fingerprints differ, the guest remains paused and the session log reports `START STATE MISMATCH`. This prevents two already-different SSBU menu/battle states from silently continuing with identical input packets.

During synchronized netplay, host input polling no longer writes a fresh guest Npad sample on every UI-loop iteration. A guest HID sample is committed only when the shared SmashSync logical sequence advances. Thus one logical sequence corresponds to one P1/P2 HID sample on both machines, reducing drift caused by different host-loop sampling counts.
