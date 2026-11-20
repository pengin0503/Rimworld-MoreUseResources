using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Waylith.Assets;
using Waylith.Diagnostics;
using Waylith.Graphics;
using Waylith.Rendering;

namespace MoreUseResources;

/// <summary>
/// Reads counters and reports the Waylith engine of RimWorld 1.7 already keeps. Nothing here turns on extra tracking.
/// Every member is only called when <see cref="TexturePrewarm.SupportedBuild"/> holds and is not inlined,
/// so a game build without these members cannot break the caller.
/// </summary>
internal static class EngineCounters
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static (int Offloaded, long PackDecodeTicks) ReadWindow() =>
        (TextureResidency.OffloadedCount, Volatile.Read(ref AssetPack.DecodeTicks));

    /// <summary>The same numbers the engine writes only to its verbose log.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string AssetPacks()
    {
        double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        long first = Volatile.Read(ref AssetPack.DecodeFirstTicks), last = Volatile.Read(ref AssetPack.DecodeLastTicks);
        return string.Create(CultureInfo.InvariantCulture,
            $"asset packs {Volatile.Read(ref GpuTextureService.PackTextureHits)} texture hits, {Volatile.Read(ref GpuTextureService.PackTextureMisses)} misses, "
            + $"unpacked {Volatile.Read(ref AssetPack.DecodedBytes) / (1024.0 * 1024.0):F0} MiB in {ms(Volatile.Read(ref AssetPack.DecodeTicks)):F0} ms CPU "
            + $"over {(first > 0 && last > first ? ms(last - first) : 0):F0} ms wall; texture policy {TextureResidency.Policy}, "
            + $"{TextureResidency.OffloadedCount} textures ({TextureResidency.OffloadedBytes / (1024.0 * 1024.0):F0} MiB) offloaded to RAM");
    }

    /// <summary>
    /// The frame cap the engine applies and where it comes from, with the Vulkan present mode,
    /// as the engine resolves it each frame. FPS at the cap means the main thread waited, not that it was busy.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string FrameCap() => Waylith.EngineApplication.FrameRateCapDescription;

    /// <summary>The engine's own RAM and VRAM breakdowns, as its crash reports and out-of-memory messages print them.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void AppendMemoryReports(StringBuilder text)
    {
        text.Append(GpuMemoryReport.BuildCompactSummary());
        text.Append(ProcessMemoryReport.BuildReport());
    }
}
