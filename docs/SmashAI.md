# Smash AI experimental branch

This branch is the first host-side milestone for accelerated SSBU reinforcement learning.

## Character policy

Start with one trained policy per controlled character. The controlled fighter stays fixed while its opponents can vary across characters. This reduces the learning problem substantially because movement, recovery, frame data, specials, resources, and available actions differ by fighter.

A later universal policy can add fighter ID to the observation and share a backbone across characters, but it should be a second-stage experiment rather than the initial trainer.

## Implemented in milestone 0

- Localhost-only UDP trainer bridge.
- Direct Npad controller override for P1/P2/P3 (and other configured controller slots).
- No Parsec/video/control virtualization in the input path.
- Host-side reader for a versioned guest observation block.
- Seqlock-style observation reads so the trainer does not consume a half-written frame.
- A smoke-test Python client.
- Windows launcher that enables the bridge on UDP 24872.

The bridge is disabled during normal Ryujinx use. Set `SMASH_AI_PORT` to enable it.

## Controller packet

All integers are little-endian.

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

Message 0x04 with byte 5 set to the player index clears that override.

## Observation contract

The companion SSBU plugin will publish a block in writable mod memory:

```
offset size field
0      8    "SSAI0001"
8      4    protocol version = 1
12     4    total block size
16     4    sequence (odd while writing, even when stable)
20     4    fighter count
24     ...  observation payload
```

Ryujinx scans writable mod-code memory for the header, caches its address, and verifies the sequence before/after each copy.

The next milestone is the guest SSBU exporter that fills this structure with fighter positions, velocities, percent, stocks, status/action state, grounded/airborne state, hitstun, shield state, jumps, and match termination. Until that plugin is installed, observation requests return "unavailable"; controller injection already works.

## Smoke test

1. Start the included `Launch-SmashAI.bat`.
2. Configure a normal controller/keyboard slot for P2 in Ryujinx.
3. Start SSBU.
4. From a Python 3 prompt in `SmashAI-tools`:

```
python test_bridge.py --player 1 --lx 1 --seconds 2
python test_bridge.py --player 1 --button a --seconds 0.2
python test_bridge.py --observe
```

The first command should hold P2's left stick fully right for two seconds. The second presses A. The third reports whether the guest state exporter has been detected.
