# Smash AI guest state exporter

Skyline plugin for SSBU. It writes a fixed C-compatible observation block into the plugin's writable data segment. The modified Ryujinx build discovers the `SSAI0001` header directly in guest memory; there is no per-frame TCP/JSON overhead.

This build uses a direct Skyline game-frame hook and does **not** require Smashline. The hook target is located with a runtime byte signature rather than a fixed SSBU text offset.

Current exported state:
- up to 3 fighters
- character kind
- status and situation
- stocks / CPU flag
- position and velocity
- facing
- percent
- motion kind/frame/end-frame
- jumps used/max
- stage ID
- remaining timer frames
- in-match flag
- per-fighter sample frame

Install only the compiled `libsmash_ai_state.nro` under the normal Skyline plugin directory for SSBU. Do not install Smashline for SmashAI; this exporter is standalone on top of Skyline.
