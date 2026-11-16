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
    private const int MaxUploadsPerFrame = 4;
    private const int SampleIntervalMs = 250;
    private const int MaxDeferredCandidates = 2048;
    private const int MaxOwnedCounters = 16384;
    private readonly TextureDemand<Texture2D> demand = new(512);
    private Queue<Texture2D>? priority;
    private readonly Dictionary<string, int> ownRehydrates = new(StringComparer.Ordinal);
    private readonly OwnedSetting<bool> tracking = new(
        () => GpuTextureService.TrackRehydration, value => GpuTextureService.TrackRehydration = value);
    private bool demandTracking;
    private bool demandTrackingExhausted;
    private readonly TextureScanCursor<Texture2D> uploadScan = new(EnumerateTextures);
    private readonly TextureScanCursor<Texture2D> observationScan = new(EnumerateTextures);
    private readonly PrewarmDeferredQueue<Texture2D> deferred = new(MaxDeferredCandidates);
    private readonly TextureRefreshSchedule refreshSchedule = new();
    private long nextWorkMs;
    private bool scanComplete => uploadScan.Completed;
    private bool demandPublished;

    internal string StatusKey { get; private set; } = "MUR_GpuDisabled";
    internal VramInfo Memory { get; private set; }
    internal long Uploaded { get; private set; }
    internal int PassesCompleted => refreshSchedule.PassesCompleted;
    internal int DeferredCount => deferred.Count;
    internal long DroppedDeferred => deferred.Dropped;
    internal long RejectedTextures { get; private set; }
    internal long FailedUploads { get; private set; }
    internal long AtlasSourcesSkipped { get; private set; }
    internal long PriorityUploads { get; private set; }

    internal static bool SupportedBuild => OperatingSystem.IsWindows()
        && VersionControl.CurrentMajor == 1 && VersionControl.CurrentMinor == 7
        && VersionControl.CurrentBuild == 5011 && VersionControl.CurrentRevision == 24;

    internal void Update(GamePhase phase, ResourceActivation activation, ResourceSettings settings, bool throttled, bool ready)
    {
        if (!settings.Enabled || !settings.GpuEnabled || !settings.GpuDemandPriority) DisableTracking();
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
            DisableTracking();
            StatusKey = "MUR_GpuFault";
            Log.Warning("[More Use Resources] Texture prewarming suspended. " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Step(ResourceSettings settings, float intensity)
    {
        long now = Environment.TickCount64;
        if (scanComplete && (demandTracking || deferred.Count == 0))
        {
            refreshSchedule.MarkPassCompleted(now);
            if (refreshSchedule.ShouldRefresh(now, settings.GpuRefreshSeconds))
                ResetScan(preserveDemand: true);
            else if (deferred.Count == 0 && (priority == null || priority.Count == 0))
            {
                StatusKey = "MUR_GpuComplete";
                return;
            }
        }
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
        if (settings.GpuDemandPriority && !demandTrackingExhausted)
            demandTracking = tracking.Apply(true);

        long keepFree = settings.GpuKeepFreeMiB * ResourceSettings.MiB;
        bool adaptive = settings.GpuResidencyBoost && TextureResidency.Policy == TextureResidencyPolicy.OffloadUnderPressure;
        int target = HardwareWork.TextureTarget(info, settings.GpuTargetPercent, keepFree, adaptive, true);
        bool canUpload = info.UsageBytes < info.CapBytes(target, keepFree);
        double frameBudgetMs = settings.GpuFrameBudgetTenthMs * 0.1 * intensity;
        if (!canUpload)
        {
            if (demandTracking && !observationScan.Completed)
            {
                ObserveWhileCapped(settings.GpuMaxTextureDimension, frameBudgetMs);
                if (observationScan.Completed)
                {
                    Queue<Texture2D> observed = demand.TakeRanked();
                    if (observed.Count > 0) priority = observed;
                }
            }
            StatusKey = "MUR_GpuAtTarget";
            nextWorkMs = now + (demandTracking && !observationScan.Completed ? SampleIntervalMs : 1000);
            return;
        }
        StatusKey = "MUR_GpuWorking";
        var timer = Stopwatch.StartNew();
        int retriesAvailable = deferred.Count;
        int retriesMade = 0;
        int uploadsThisFrame = 0;
        for (int i = 0; i < MaxCandidatesPerFrame && uploadsThisFrame < MaxUploadsPerFrame
            && timer.Elapsed.TotalMilliseconds < frameBudgetMs; i++)
        {
            Texture2D? texture;
            Texture2D? priorityCandidate = null;
            bool prioritized = demandTracking && priority != null && priority.TryDequeue(out priorityCandidate);
            if (prioritized) texture = priorityCandidate;
            else if (!scanComplete)
            {
                // Always scan new assets before retrying budget-blocked assets:
                // a large image must never block smaller images behind it.
                texture = uploadScan.Take(canUpload);
                if (texture == null) continue;
            }
            else
            {
                // Retry each previously deferred image at most once per frame.
                if (retriesMade++ >= retriesAvailable) break;
                texture = deferred.Take();
                if (texture == null) break;
            }

            if (texture == null) continue;
            var core = texture.CoreTexture;
            if (core == null || !core.CanRehydrate || core.SourcePath == null
                || core.width < 1 || core.width > settings.GpuMaxTextureDimension
                || core.height < 1 || core.height > settings.GpuMaxTextureDimension)
                continue;
            if (GlobalTextureAtlasManager.LeavesSourceEvicted(core))
            {
                AtlasSourcesSkipped++;
                continue;
            }
            string? demandKey = demandTracking ? $"{core.width}x{core.height} {core.SourcePath}" : null;
            int observed = demandKey == null ? 0 : ReadRehydrates(demandKey);
            if (demandKey != null)
                demand.Observe(texture, observed, ownRehydrates.GetValueOrDefault(demandKey));
            if (core.GpuResource != null) continue;

            PrewarmAdmission admission = PrewarmAdmissionPolicy.Evaluate(
                info, core.width, core.height, target, keepFree,
                settings.GpuMaxTextureDimension);
            if (admission == PrewarmAdmission.Reject)
            {
                RejectedTextures++;
                continue;
            }
            if (admission == PrewarmAdmission.Defer)
            {
                deferred.Defer(texture);
                continue;
            }
            bool uploaded = texture.EnsureGpuResident();
            if (demandKey != null && ReadRehydrates(demandKey) > observed)
            {
                if (ownRehydrates.ContainsKey(demandKey) || ownRehydrates.Count < MaxOwnedCounters)
                    ownRehydrates[demandKey] = ownRehydrates.GetValueOrDefault(demandKey) + 1;
                else
                {
                    demandTrackingExhausted = true;
                    DisableTracking();
                }
            }
            if (!uploaded)
            {
                FailedUploads++;
                continue;
            }

            Uploaded++;
            if (prioritized) PriorityUploads++;
            uploadsThisFrame++;
            // A live driver sample after each successful upload prevents overspending a
            // single snapshot when several game textures are uploaded within one frame.
            if (!TryReadTextureHeap(out info))
            {
                Memory = default;
                StatusKey = "MUR_GpuNoBudget";
                nextWorkMs = now + 1000;
                return;
            }
            Memory = info;
            if (info.UsageBytes >= info.CapBytes(target, keepFree))
            {
                StatusKey = "MUR_GpuAtTarget";
                break;
            }
        }

        if (scanComplete && demandTracking && !demandPublished)
        {
            Queue<Texture2D> observed = demand.TakeRanked();
            if (observed.Count > 0) priority = observed;
            demandPublished = true;
        }
        if (scanComplete && (demandTracking || deferred.Count == 0))
        {
            refreshSchedule.MarkPassCompleted(now);
            if (deferred.Count == 0 && (priority == null || priority.Count == 0))
            {
                StatusKey = "MUR_GpuComplete";
                return;
            }
        }

        if (scanComplete && retriesAvailable > 0 && uploadsThisFrame == 0)
            StatusKey = "MUR_GpuAtTarget";
        int delayMs = uploadsThisFrame > 0 ? Math.Max(16, (int)(SampleIntervalMs / (4 * intensity)))
            : scanComplete ? 1000 : Math.Max(25, (int)(50 / intensity));
        if (timer.Elapsed.TotalMilliseconds > frameBudgetMs)
            delayMs = Math.Max(delayMs, SampleIntervalMs * 4);
        nextWorkMs = now + delayMs;
    }

    internal static bool TryReadTextureHeap(out VramInfo memory)
    {
        memory = default;
        if (!GpuMemoryReport.TryGetSummary(out GpuMemorySummary report) || !report.DriverReported)
            return false;

        var selection = new TextureHeapSelection(TextureResidency.DeviceLocalHeapBytes);
        foreach (GpuHeapInfo heap in report.Heaps)
            selection.Add(heap.DeviceLocal, heap.TotalBytes, heap.BudgetBytes, heap.DriverUsageBytes, heap.VmaBlockBytes);
        return selection.TryGet(out memory);
    }

    private static IEnumerable<Texture2D?> EnumerateTextures()
    {
        var packs = LoadedModManager.RunningModsListForReading;
        for (int i = 0; i < packs.Count; i++)
        {
            // Leave a point to check the frame budget between packs, even when a holder is empty.
            yield return null;
            var holder = packs[i].GetContentHolder<Texture2D>();
            if (holder == null) continue;
            foreach (Texture2D texture in holder.contentList.Values)
                yield return texture;
        }
    }

    private void ObserveWhileCapped(int maxDimension, double frameBudgetMs)
    {
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < MaxCandidatesPerFrame && !observationScan.Completed
            && timer.Elapsed.TotalMilliseconds < frameBudgetMs; i++)
        {
            Texture2D? texture = observationScan.Take(true);
            if (texture == null) continue;
            var core = texture.CoreTexture;
            if (core == null || !core.CanRehydrate || core.SourcePath == null
                || core.width < 1 || core.width > maxDimension || core.height < 1 || core.height > maxDimension
                || GlobalTextureAtlasManager.LeavesSourceEvicted(core))
                continue;

            string key = $"{core.width}x{core.height} {core.SourcePath}";
            demand.Observe(texture, ReadRehydrates(key), ownRehydrates.GetValueOrDefault(key));
        }
    }

    private static int ReadRehydrates(string key) =>
        GpuTextureService.RehydratedTextures.TryGetValue(key, out int total) ? total : 0;

    private void DisableTracking()
    {
        if (!demandTracking) return;
        tracking.Restore();
        observationScan.Reset();
        demandTracking = false;
        priority = null;
        demand.Clear();
    }

    internal void Stop()
    {
        DisableTracking();
        ResetScan();
    }

    internal void InvalidateScan() => ResetScan();

    private void ResetScan(bool preserveDemand = false)
    {
        if (!preserveDemand)
        {
            priority = null;
            demand.Clear();
        }
        uploadScan.Reset();
        observationScan.Reset();
        deferred.Clear();
        refreshSchedule.BeginNextPass();
        demandPublished = false;
        nextWorkMs = 0;
        Memory = default;
    }
}
