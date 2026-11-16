# Texture asset integrity and decoded-size inventory (2026-11-16)

## Scope
Add useful CPU work from real loaded-mod texture assets without changing game simulation, introducing Harmony patches, or copying third-party code.

## Decision
Use the current AssetAudit low-priority worker, cancellation and memory/phase safeguards to inspect:
- PNG: validate PNG signature, IHDR geometry and CRC of all chunks (bounded streaming; IDAT required). Report malformed/truncated chunks, CRC errors and bounds violations.
- DDS: validate DDS header signature, header sizes and geometry; do not pretend to validate compressed payload.
- Convert width x height x 4 to *RGBA8-equivalent base-level bytes* for diagnostic purposes; never claim this is measured VRAM use.
- Track files checked, valid/invalid, oversized/skipped files, pixels/estimated RGBA8 bytes and PNG payload bytes processed. This is independent of the GPU prewarmer.
- Use a bounded maximum image size and dimensions to avoid runaway work. Respect cancellation in every streaming iteration.
- Accept older/unknown formats by not indexing them rather than guessing.
- Re-evaluate the image inventory on the existing user-configurable audit interval, never retain full image byte arrays.
- Display a read-only summary in Japanese/English.

## Alternatives
- Direct GPU decoding/compression: could consume GPU but involves Vulkan/Waylith APIs and drivers, is not safe from a background worker, and does not necessarily help the game.
- Caching decoded image data in process RAM: creates a second copy without an engine consumer. Deliberately deferred.
- Patching XML or game texture loader for dedup: changes engine semantics and creates compatibility risk with WOWGAG / Overclock / Axen. Requires separate API review and in-game tests.

## TDD and verification
1. Tests: valid/invalid PNG, CRC failure, oversized, valid/invalid DDS, cancellation; audit integration and periodic reset.
2. Implement parser, integration, UI and docs.
3. GitHub Actions tests, Release build, update committed game DLL, verify DLL identical, package artifact, review diff.
4. Keep PR #2 open; do not publish to Release or Workshop.
