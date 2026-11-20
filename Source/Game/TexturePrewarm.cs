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
    // Last session's ranking, resolved against the textures loaded now. Consumed before everything else.
    private Queue<Texture2D>? history;
    private IReadOnlyList<TextureDemandSample>? offeredHistory;
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
    private bool suspended;

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
    internal int HistoryCandidates { get; private set; }
    internal long HistoryUploads { get; private set; }
    /// <summary>This session's observed ranking as stable keys; replaced after each completed scan.</summary>
    internal IReadOnlyList<TextureDemandSample>? DemandSamples { get; private set; }
    internal int DemandSamplesVersion { get; private set; }

    /// <summary>Any thread: hands over a stored ranking. It is resolved on the main thread.</summary>
    internal void OfferHistory(IReadOnlyList<TextureDemandSample> samples) =>
        System.Threading.Volatile.Write(ref offeredHistory, samples);

    internal static bool SupportedBuild => SupportedGame.Contains(VersionControl.CurrentMajor, VersionControl.CurrentMinor);

    /// <summary>Stops prewarming for this session after an exception outside <see cref="Step"/>, such as an engine member this build lacks.</summary>
    internal void Suspend(Exception ex)
    {
        if (suspended) return;
        suspended = true;
        StatusKey = "MUR_GpuFault";
        try
        {
            ResetScan();
            DisableTracking();
        }
        catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
        {
        }
        Log.Warning("[More Use Resources] Texture prewarming suspended. " + ex.GetType().Name + ": " + ex.Message);
    }

    internal void Update(GamePhase phase, ResourceActivation activation, ResourceSettings settings, bool throttled, bool ready)
    {
        if (suspended) return;
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
        if (System.Threading.Interlocked.Exchange(ref offeredHistory, null) is { } stored && settings.GpuDemandPriority)
            ResolveHistory(stored);

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
                    CaptureDemandSamples();
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
            bool fromHistory = history != null && history.TryDequeue(out priorityCandidate);
            bool prioritized = fromHistory
                || (demandTracking && priority != null && priority.TryDequeue(out priorityCandidate));
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
            if (fromHistory) HistoryUploads++;
            else if (prioritized) PriorityUploads++;
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
            CaptureDemandSamples();
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

    [MethodImpl(MethodImplOptions.NoInlining)]
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

    /// <summary>Keeps only stored keys that name a texture loaded in this session, in stored order.</summary>
    private void ResolveHistory(IReadOnlyList<TextureDemandSample> stored)
    {
        var packs = new Dictionary<string, ModContentPack>(StringComparer.OrdinalIgnoreCase);
        foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
            if (!string.IsNullOrEmpty(pack.PackageIdPlayerFacing)) packs.TryAdd(pack.PackageIdPlayerFacing, pack);
        var resolved = new Queue<Texture2D>();
        foreach (TextureDemandSample sample in stored)
        {
            if (!packs.TryGetValue(sample.Key.PackageId, out ModContentPack? pack)) continue;
            var holder = pack.GetContentHolder<Texture2D>();
            if (holder != null && holder.contentList.TryGetValue(sample.Key.ContentKey, out Texture2D? texture) && texture != null)
                resolved.Enqueue(texture);
        }
        HistoryCandidates = resolved.Count;
        history = resolved.Count > 0 ? resolved : null;
    }

    /// <summary>Names the ranked textures by mod and content key so the ranking survives a restart.</summary>
    private void CaptureDemandSamples()
    {
        List<(Texture2D Item, int Count)> ranked = demand.PeekRanked();
        if (ranked.Count == 0) return;
        var counts = new Dictionary<Texture2D, int>(ranked.Count, ReferenceEqualityComparer.Instance);
        foreach ((Texture2D item, int count) in ranked) counts[item] = count;
        var samples = new List<TextureDemandSample>(ranked.Count);
        foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
        {
            var holder = pack.GetContentHolder<Texture2D>();
            if (holder == null || string.IsNullOrEmpty(pack.PackageIdPlayerFacing)) continue;
            foreach (KeyValuePair<string, Texture2D> entry in holder.contentList)
                if (entry.Value != null && counts.TryGetValue(entry.Value, out int count))
                    samples.Add(new TextureDemandSample(new TextureDemandKey(pack.PackageIdPlayerFacing, entry.Key), count));
        }
        DemandSamples = TextureDemandHistory.Normalize(samples);
        DemandSamplesVersion++;
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
        DropHistory();
        ResetScan();
    }

    /// <summary>Definitions were reloaded: texture objects may have been replaced.</summary>
    internal void InvalidateScan()
    {
        DropHistory();
        ResetScan();
    }

    // Waiting phases (loading, saving) restart the scan but keep the resolved history:
    // its texture objects stay valid until definitions are reloaded.
    private void DropHistory()
    {
        history = null;
        HistoryCandidates = 0;
        DemandSamples = null;
        System.Threading.Volatile.Write(ref offeredHistory, null);
    }

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
