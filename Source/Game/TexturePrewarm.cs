using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Brutal.VulkanApi;
using MoreUseResources.Core;
using RimWorld;
using UnityEngine;
using Verse;
using Waylith;
using Waylith.Graphics;
using Waylith.Rendering;
using Texture2D = UnityEngine.Texture2D;

namespace MoreUseResources;

internal sealed class TexturePrewarm
{
    private const int MaxCandidatesPerFrame = 48;
    private const int SampleIntervalMs = 250;
    private IEnumerator<Texture2D>? textures;
    private int packIndex;
    private Texture2D? pending;
    private long nextWorkMs;
    private bool complete;

    internal string StatusKey { get; private set; } = "MUR_GpuDisabled";
    internal VramInfo Memory { get; private set; }
    internal long Uploaded { get; private set; }

    internal static bool SupportedBuild => OperatingSystem.IsWindows()
        && VersionControl.CurrentMajor == 1 && VersionControl.CurrentMinor == 7
        && VersionControl.CurrentBuild == 5011 && VersionControl.CurrentRevision == 24;

    internal void Update(GamePhase phase, ResourceActivation activation, ResourceSettings settings, bool throttled, bool ready)
    {
        if (!settings.GpuEnabled)
        {
            ResetScan();
            StatusKey = "MUR_GpuDisabled";
            return;
        }
        if (!SupportedBuild)
        {
            StatusKey = "MUR_GpuUnsupported";
            return;
        }
        if (StatusKey == "MUR_GpuFault") return;
        if (!ready || phase is GamePhase.Startup or GamePhase.Loading or GamePhase.Saving or GamePhase.Quitting)
        {
            ResetScan();
            StatusKey = "MUR_GpuWaiting";
            return;
        }
        if (!activation.Active || throttled)
        {
            StatusKey = throttled ? "MUR_ReasonFrameGuard" : "MUR_Reason" + activation.Reason;
            return;
        }

        try
        {
            Step(settings, activation.Intensity);
        }
        catch (Exception ex)
        {
            ResetScan();
            StatusKey = "MUR_GpuFault";
            Log.Warning("[More Use Resources] Texture prewarming suspended. " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Step(ResourceSettings settings, float intensity)
    {
        if (complete)
        {
            StatusKey = "MUR_GpuComplete";
            return;
        }
        long now = Environment.TickCount64;
        if (now < nextWorkMs) return;

        var device = EngineApplication.Instance?.GraphicsDevice;
        if (device == null || !device.MemoryBudgetEnabled || !TextureResidency.OffloadSupported
            || device.PhysicalDevice.GetProperties().DeviceType != VkPhysicalDeviceType.DiscreteGpu
            || !TryReadTextureHeap(out VramInfo info))
        {
            Memory = default;
            StatusKey = "MUR_GpuNoBudget";
            nextWorkMs = now + 1000;
            return;
        }
        Memory = info;

        long keepFree = settings.GpuKeepFreeMiB * ResourceSettings.MiB;
        if (info.UsageBytes >= info.CapBytes(settings.GpuTargetPercent, keepFree))
        {
            StatusKey = "MUR_GpuAtTarget";
            nextWorkMs = now + 1000;
            return;
        }
        StatusKey = "MUR_GpuWorking";
        var budget = Stopwatch.StartNew();
        double frameBudgetMs = settings.GpuFrameBudgetTenthMs * 0.1 * intensity;
        for (int i = 0; i < MaxCandidatesPerFrame && budget.Elapsed.TotalMilliseconds < frameBudgetMs; i++)
        {
            Texture2D? texture = pending ?? NextTexture();
            if (complete) break;
            if (texture == null) continue;
            var core = texture.CoreTexture;
            if (core == null || core.GpuResource != null || !core.CanRehydrate || core.SourcePath == null
                || core.width is < 1 or > 1024 || core.height is < 1 or > 1024)
            {
                pending = null;
                continue;
            }
            if (!info.CanPrewarmTexture(core.width, core.height, settings.GpuTargetPercent, keepFree))
            {
                pending = texture;
                StatusKey = "MUR_GpuAtTarget";
                nextWorkMs = now + 1000;
                return;
            }
            pending = null;
            if (texture.EnsureGpuResident()) Uploaded++;
            // The engine owns the image. Refresh the driver budget before the next upload.
            int backoffMs = budget.Elapsed.TotalMilliseconds > frameBudgetMs ? SampleIntervalMs * 4 : SampleIntervalMs;
            nextWorkMs = now + (int)(backoffMs / intensity);
            return;
        }
        nextWorkMs = now + Math.Max(25, (int)(50 / intensity));
    }

    private static bool TryReadTextureHeap(out VramInfo memory)
    {
        memory = default;
        if (!GpuMemoryReport.TryGetSummary(out GpuMemorySummary report) || !report.DriverReported)
            return false;

        // Match Waylith's texture heap only when the reported size identifies it unambiguously.
        foreach (GpuHeapInfo heap in report.Heaps)
        {
            if (!heap.DeviceLocal || heap.TotalBytes != TextureResidency.DeviceLocalHeapBytes) continue;
            if (memory.DriverReported || heap.BudgetBytes <= 0 || heap.DriverUsageBytes < 0) return false;
            memory = new VramInfo(Math.Max(heap.DriverUsageBytes, heap.VmaBlockBytes),
                Math.Min(heap.BudgetBytes, heap.TotalBytes), true);
        }
        return memory.DriverReported;
    }

    private Texture2D? NextTexture()
    {
        if (textures != null)
        {
            if (textures.MoveNext()) return textures.Current;
            textures.Dispose();
            textures = null;
        }
        var packs = LoadedModManager.RunningModsListForReading;
        if (packIndex >= packs.Count)
        {
            complete = true;
            return null;
        }
        textures = packs[packIndex++].GetContentHolder<Texture2D>()?.contentList.Values.GetEnumerator();
        return null;
    }

    private void ResetScan()
    {
        textures?.Dispose();
        textures = null;
        pending = null;
        packIndex = 0;
        complete = false;
        nextWorkMs = 0;
        Memory = default;
    }
}
