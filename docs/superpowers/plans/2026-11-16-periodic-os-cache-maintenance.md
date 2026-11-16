# 2026-11-16 — Periodic OS file cache maintenance (PR #2)

## Problem / observed behavior
`FileCache.Pump` permanently records successful non-copy reads in `_warmed` and never revisits them except on a manual reset, block release, source change or copy-mode transition. The file cache may be evicted by the operating system while the game runs, so a single pass cannot continually warm assets. Holding native copies changes RimWorld's visible RAM usage, but those copies are **not read by the game**; repeated copies of already-held files would be wasteful.

## Selected design
- Extend `FileCache.Pump` with a configurable, periodic **OS-only maintenance pass** on existing game/mod files after the initial pass completes.
- During periodic maintenance, call the existing `ReadAndDiscard` path to warm the OS file cache. Do **not** allocate any additional native RAM or duplicate already-held copies.
- Preserve `_done` (native copy ownership), the governor's RAM budget and existing deferred-for-budget handling. Clear only OS warmed marks when maintenance begins.
- Preserve the original active-phase, cancellation, shutdown, memory-pressure and cooldown policies; period 0 disables maintenance. Bound the interval to 0–120 minutes, default 5 minutes, configurable and persisted in mod settings.
- Expose maintenance pass count and cumulative file bytes read through existing diagnostics. No game hooks or new GPU operations.
- Limit one new pass to the existing source list (up to 50,000 known asset files), never scan saves, never open files for writing.
- Complement tests with a fake clock to verify: no early refresh, interval boundary, repeated read without native growth, disabled setting, release cooldown still honored.

## Rejected options
- Duplicate in-process RAM buffers: no consumer in RimWorld, so it would not warm the engine's file cache any better.
- Loader interception / `XmlDocument` cache: requires verified 1.7 API and mod interoperability, not justified for a file cache maintenance fix.
- Continuous zero-delay busy loop: unbounded disk I/O can reduce game performance.
- Periodically reenumerating load folders: may accidentally race changes to mod loader state and exceed the existing bounded file list.

## Verification and delivery
Follow tests-first flow. Run GitHub Actions .NET 10 tests and game adapter Release build; update the checked-in reproducible DLL; confirm ZIP artifact and CI success; leave PR open. No Workshop or GitHub release. In-game FPS/TPS improvements are unverified.
