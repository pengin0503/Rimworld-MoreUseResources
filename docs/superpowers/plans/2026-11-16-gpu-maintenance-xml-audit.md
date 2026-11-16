# 2026-11-16 GPU maintenance and XML manifest audit

## Goal

Use otherwise idle CPU/GPU capacity for real RimWorld/loaded-mod assets, not synthetic compute. Preserve vanilla behavior and the existing PR #2.

## Approaches considered

1. Harmony patch the game's XML loader and reuse cached parsed documents directly: potentially faster but depends on exact 1.7 loader behavior, threading, and mod patch order. **Deferred** until exact API and live integration checks can be completed.
2. Read-only XML manifest analysis on background workers + a bounded periodically refreshed Waylith texture prewarm: independent of game state/Harmony, useful for identifying duplicate Def names and keeping discarded textures warm. **Selected** for this change.
3. Pure CPU/GPU load generation: higher usage but no reusable game-data output. Rejected under the requirement to perform actual work.

## Tasks

1. GPU maintenance schedule
   - Add deterministic pure-core schedule tracking that emits a refresh only after a completed scan and interval.
   - Extend TexturePrewarm to perform periodic rescans of real game textures without resetting lifetime counters; reset on loading and disabled phases.
   - Expose interval (0 disables) and pass count in the game UI, persist settings, and add English/Japanese translations.
   - Tests first, including 0 interval, exact cutoff, clock rollback, and refresh after completion.
2. XML manifest scanner
   - Introduce a small independent XML reader (DTD and external entities disabled, size/depth bounds, cancellation).
   - Parse files under loaded mods' Defs/Patches/Languages etc. Only direct children of `<Defs>` with explicit direct `<defName>` participate in collision detection. Equal `defName` under equal Def type is a *candidate*, not proven actual conflict.
   - Integrate into AssetAudit existing real-file worker path, gated by new setting, cumulative counters and bounded persistent registry. Release/reset on source changes.
   - Tests first: valid/malformed XML, no external entities, nested `defName` ignored, collisions, cancellation.
3. Verification
   - Run full .NET 10 tests and game adapter build in GitHub Actions, regenerate checked-in DLL, verify reproducible binary and ZIP packaging.
   - Inspect diff for concurrency, invalidation, and no unnecessary Harmony patches.
   - Leave PR open for review; no Release or Workshop upload.

## Guardrails

No background access to RimWorld game objects; parse only disk XML using safe .NET APIs. Keep existing worker priority and memory-pressure gating. Do not copy third-party mod code. A successful CI build is not an in-game compatibility test.
