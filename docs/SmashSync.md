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

1. **P1** clicks **Request**.
2. **P2** sees the incoming request and clicks **Accept**.
3. Both sides show **connected**.
4. The remote SmashSync controller becomes available in Ryujinx input devices.
5. Configure the canonical P1/P2 controller slots as described above.

A game launch is blocked while Netplay mode is enabled but the pre-game peer handshake is not connected.

## Launch and automatic game barrier

Both PCs still run SSBU locally. SmashSync does not remotely execute programs on the other PC.

Either player may launch SSBU first. The first instance automatically enters a SmashSync-owned pause once the game-side input session is initialized and waits for the other machine to launch. There is no fixed game-loading deadline for an accepted lobby session: the faster machine remains paused until the slower peer is ready, or until the lobby actually disconnects.

P1 is authoritative for persistent SSBU save data during the session. While both guests are still paused, P1 packages its active SSBU account save and transfers it over the TCP lobby connection. P2 temporarily installs that copy before its application main thread is released. P2's original local save is backed up and restored when the SmashSync session ends; interrupted-session recovery is also supported.

When both game-side sessions are present:

- both exchange lobby-bound READY packets over UDP;
- P1 and P2 perform a four-timestamp monotonic-clock synchronization;
- P1 creates a cryptographically random gameplay session ID;
- P2 acknowledges it;
- P1 schedules a future shared epoch;
- P2 acknowledges the epoch;
- P1 sends the final RELEASE and P2 acknowledges it;
- both reset the shared logical input tick to 0 and resume at the shared epoch.

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

P2 does not need to manually copy P1's SSBU save before a session; SmashSync transfers P1's active save automatically and restores P2's local save afterward. Shader caches do not need to match.


## Canonical input routing

During an accepted SmashSync session, only the canonical local player's physical Ryujinx controller is sampled as local gameplay input. The opposite canonical player slot is supplied exclusively by the UDP network stream. The visible `SmashSync Remote P1/P2` device remains available for identification/configuration UI, but it is not polled again by the gameplay path. This prevents remote input from being selected as a local source and echoed back to the peer.

## Synchronized release

For accepted pre-game sessions, the guest tick source is frozen before kernel initialization and reset to zero. The application main thread is then suspended with the emulator's normal process-pause flag before guest execution begins. Both peers remain suspended through READY/START.

SmashSync uses a four-timestamp clock exchange to estimate P2-minus-P1 monotonic-clock offset. P1 schedules a future P1-authoritative epoch, P2 converts it into its local monotonic-clock domain, and a RELEASE/RELEASE_ACK commit is required before either side arms the resume. Gameplay synchronization after launch is based on shared logical tick numbers rather than continuously sharing a wall clock.


## Strict start-state and HID sequencing

Before releasing an accepted netplay session, SmashSync computes a 64-bit SHA-256-derived fingerprint over the active title/version, firmware version, guest-visible language/region/docked/memory/timing settings, configured dirty hacks, and the active title's account save-data contents. Because P2 temporarily boots from P1's authoritative save, both peers should produce the same persistent-state fingerprint after transfer.

If the fingerprints differ, the guest remains paused and the session log reports `START STATE MISMATCH`. This fingerprint verifies the synchronized launch inputs and persistent state; it is not a whole-emulator savestate or proof that every hidden runtime subsystem is identical.

During synchronized netplay, host input polling no longer writes a fresh guest Npad sample on every UI-loop iteration. A guest HID sample is committed only when the shared SmashSync logical sequence advances. Thus one logical sequence corresponds to one P1/P2 HID sample on both machines, reducing drift caused by different host-loop sampling counts.


## Reliability hardening

Gameplay input ticks are immutable once assigned. Retransmission never resamples a logical tick, and a conflicting duplicate from the peer is treated as a deterministic protocol failure rather than silently overwriting history.

Incoming ticks are bounded to a small window around the current logical tick, startup/control packets are bound to the accepted TCP lobby nonce, runtime lockstep stalls honor `LockstepTimeoutMs`, and SmashSync never takes ownership of an unrelated manual emulator pause.

The guest tick source is instance-local and internally synchronized so concurrent guest-time reads cannot mutate shared static timing state.


## Runtime pacing diagnostics

Every 60 logical ticks, netplay logging includes the negotiated delay, measured RTT, effective logical tick rate, number of lockstep stalls, and cumulative stall milliseconds. A healthy 60 Hz session should converge near `effectiveHz=60` with stall time remaining close to zero. If network latency or jitter exceeds the startup buffer, SmashSync still pauses rather than advancing either peer on a different input stream.


## Canonical HID timeline

For synchronized netplay, P1 and P2 controller ring-buffer sampling numbers are derived from the shared SmashSync logical tick rather than each machine's pre-existing local HID history. The P1/P2 button-stick and six-axis ring histories are cleared while the application is paused at the startup barrier, then rebuilt from the same shared sample numbers on both peers.

Every 60 logical ticks, both peers also exchange the cumulative canonical P1+P2 input digest. A mismatch stops the session with `INPUT STREAM MISMATCH` instead of allowing different controller streams to silently drive the two guests apart. This verifies the complete input stream; it still does not by itself prove that all internal SSBU runtime state is identical.
