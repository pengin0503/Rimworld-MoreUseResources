using System;

namespace MoreUseResources.Core;

/// <summary>
/// The Windows game builds the engine-facing code (GPU prewarm, VRAM and engine counters, the texture cache codec,
/// the patched XML cache) was checked against. Every other build leaves that code off.
/// </summary>
public static class VerifiedBuilds
{
    // 1.7.5011 rev24: checked against the game's own DLLs (R2 Game-Windows).
    // 1.7.5014 rev1195: every engine and game type the mod calls, patches or reads by reflection declares the same members,
    // private ones included, as in 5011 (Krafs.Rimworld.Ref 1.7.5014-unstable), and the mod builds against it.
    // Method bodies were not compared: the reference package has none.
    private static readonly (int Build, int Revision)[] Windows17 = { (5011, 24), (5014, 1195) };

    public static bool Contains(int major, int minor, int build, int revision) =>
        major == 1 && minor == 7 && Array.IndexOf(Windows17, (build, revision)) >= 0;
}
