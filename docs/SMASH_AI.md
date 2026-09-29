# Smash AI branch

This branch is the first implementation slice for an SSBU reinforcement-learning environment.

## What works in this build

- Direct controller injection into Ryubing's Npad/HID path.
- Binary UDP control over localhost only.
- P1-P8 controller override support.
- Turbo toggle through the bridge.
- Host-side reading of the active game's guest memory.
- Discovery and reading of a future `SSAI_STATE_V1` state-export block.
- A Python diagnostic client in `tools/smash_ai_client.py`.

The game-state exporter itself is the next component. Until the SSBU-side exporter is installed, `find-state` will intentionally report that no state block exists.

## Launch

Windows PowerShell:

```powershell
$env:RYUJINX_SMASH_AI_PORT="24842"
.\Ryujinx.exe
```

Use a different port for each parallel emulator instance.

For this first build, configure the player slots you want the AI to control as normal Pro Controllers in Ryubing. The bridge overrides the sampled Npad state after the normal device poll, so no UI-thread or SDL timing is involved in the AI action itself.

## Test controller injection

```powershell
python .\tools\smash_ai_client.py --port 24842 ping
python .\tools\smash_ai_client.py --port 24842 input --player 2 --lx 32767
python .\tools\smash_ai_client.py --port 24842 input --player 2 --button a
python .\tools\smash_ai_client.py --port 24842 clear --player 2
```

Axis values are signed 16-bit values from -32768 to 32767.

## State ABI

The SSBU-side plugin will expose a fixed binary block beginning with exactly 16 bytes:

```text
SSAI_STATE_V1\0\0\0
```

Bytes 16..19 are a little-endian signed 32-bit total block size. The block is bounded to 64 KiB. Ryubing scans readable mutable guest mappings once, caches the address, and can then return the block directly to the trainer without screenshots or OCR.

## Character training

The system does not fundamentally require one character per model. The initial trainer will support a fixed-character policy first because it reduces the learning problem and makes evaluation clearer. Later, a character ID/embedding can condition one shared policy, or separate per-character checkpoints can be kept.
