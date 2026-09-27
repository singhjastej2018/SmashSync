# SmashSync

SmashSync is an experimental Ryubing/Ryujinx-derived build for low-latency, input-only synchronized multiplayer research.

## Development plan

1. Give the build a distinct executable/app identity and isolated user-data directory.
2. Add deterministic, indexed input replay for two-machine determinism testing.
3. Add timing and state diagnostics to locate the first divergent input/update tick.
4. Add fixed-size binary UDP controller transport with sequence numbers and redundant recent inputs.
5. Validate determinism before attempting prediction/rollback.

## User data

SmashSync intentionally uses its own data directory instead of writing into the normal Ryujinx profile. Copy the data you want to test from your existing Ryujinx profile into the SmashSync profile after backing it up.

On Windows the default profile will be under:

`%APPDATA%\SmashSync`

A `portable` directory placed next to the SmashSync executable overrides the profile directory, matching Ryujinx's portable-mode behavior.

Keep your normal Ryujinx installation and backups separate while this fork is experimental.
