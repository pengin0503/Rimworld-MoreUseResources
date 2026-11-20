namespace MoreUseResources.Core;

/// <summary>
/// Which game versions run the engine-facing code (GPU prewarm, VRAM and engine counters, the texture cache codec,
/// the patched XML cache). Every RimWorld 1.7 build does: limiting it to checked build numbers only switched the
/// features off after each game update. A build whose engine lacks a member stops only the feature that uses it.
/// </summary>
public static class SupportedGame
{
    public static bool Contains(int major, int minor) => major == 1 && minor == 7;
}
