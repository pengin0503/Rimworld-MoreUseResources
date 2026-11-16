using System;
using MoreUseResources.Core;
using UnityEngine;
using Verse;

namespace MoreUseResources;

public sealed class MoreUseResourcesMod : Mod
{
    private readonly ResourceModSettings settings;
    private readonly ResourceRuntime runtime;
    private Vector2 scroll;
    private float contentHeight = 1580;
    private bool changed;

    public MoreUseResourcesMod(ModContentPack content) : base(content)
    {
        settings = GetSettings<ResourceModSettings>();
        runtime = new ResourceRuntime(settings.Value);
        runtime.Install();
    }

    public override string SettingsCategory() => "MUR_Name".Translate();

    public override void WriteSettings()
    {
        runtime.SettingsChanged(settings.Value);
        base.WriteSettings();
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        changed = false;
        Rect view = new(0, 0, inRect.width - 20, contentHeight);
        Widgets.BeginScrollView(inRect, ref scroll, view);
        var list = new Listing_Standard { maxOneColumn = true };
        list.Begin(view);
        try
        {
            DrawStatus(list);
            DrawGeneral(list, settings.Value);
            DrawPhases(list, settings.Value);
            DrawResources(list, settings.Value);
            contentHeight = Math.Max(inRect.height, list.CurHeight + 15);
        }
        finally
        {
            list.End();
            Widgets.EndScrollView();
        }

        if (changed)
        {
            settings.Value.Clamp();
            runtime.SettingsChanged(settings.Value);
        }
    }

    private void DrawStatus(Listing_Standard list)
    {
        ControllerStatus status = runtime.Status;
        if (!runtime.HooksReady || runtime.Faulted)
            list.Label("MUR_HooksUnavailable".Translate());
        if (status.ControlError != null || status.WorkerLastError != null)
            list.Label("MUR_BackgroundError".Translate());

        list.Label("MUR_LoadTimingStatus".Translate(LoadTiming.Recorder.Report()));
        list.Label("MUR_CurrentPhase".Translate(PhaseName(status.Plan.Phase.Phase), ModeName(runtime.Manual)));
        list.Label("MUR_CpuStatus".Translate(Reason(status.Plan.Cpu), status.WorkersActive, status.PendingJobs));
        list.Label("MUR_CpuWorkerUsage".Translate((status.WorkerCpuShare * 100).ToString("F1"), status.JobsCompleted, status.JobsFailed));
        PerformanceSnapshot perf = runtime.Performance;
        list.Label("MUR_PerformanceStatus".Translate(perf.FramesPerSecond.ToString("F1"),
            perf.TicksPerSecond.ToString("F1"), perf.AverageFrameIntervalMs.ToString("F1"),
            perf.WorstFrameIntervalMs.ToString("F1")));
        if (runtime.Gc.HasData)
        {
            GcSnapshot gc = runtime.Gc.Latest;
            list.Label("MUR_GcStatus".Translate(gc.Gen0Collections, gc.Gen1Collections, gc.Gen2Collections,
                gc.PauseMs.ToString("F0"), gc.PausePercent.ToString("F2"), gc.AllocatedMiBPerSecond.ToString("F1"),
                InMiB(gc.HeapBytes), (gc.ServerGc ? "Server" : "Workstation") + "/" + gc.Latency));
        }
        else list.Label("MUR_GcPending".Translate());
        if (runtime.GcCurrent is { } current)
        {
            if (list.ButtonText("MUR_GcSaveBaseline".Translate())) runtime.SaveGcBaseline();
            if (runtime.GcBaseline is { } baseline && current.TryCompare(baseline, out var difference))
                list.Label("MUR_GcComparison".Translate(PhaseName(current.Phase), baseline.ServerGc ? "Server" : "Workstation",
                    baseline.Latency.ToString(), difference.CollectionsPerMinute.ToString("+0.0;-0.0;0.0"),
                    difference.PauseMsPerMinute.ToString("+0.0;-0.0;0.0"),
                    (difference.HeapBytes / (double)ResourceSettings.MiB).ToString("+0.0;-0.0;0.0"),
                    difference.AllocatedMiBPerSecond.ToString("+0.0;-0.0;0.0"),
                    (difference.CommittedBytes / (double)ResourceSettings.MiB).ToString("+0.0;-0.0;0.0")));
        }
        if (runtime.GcBaseline != null && (runtime.GcCurrent == null || runtime.GcCurrent.Phase != runtime.GcBaseline.Phase))
            list.Label("MUR_GcComparisonPending".Translate(PhaseName(runtime.GcBaseline.Phase)));
        if (settings.Value.CpuJitPrewarm && runtime.JitYieldedToWowgag)
            list.Label("MUR_JitWowgag".Translate());
        else if (settings.Value.CpuJitPrewarm)
        {
            JitProgress jit = runtime.Jit;
            list.Label("MUR_JitStatus".Translate(jit.Prepared, jit.Failed, jit.Skipped, jit.WorkMs.ToString("F0"),
                (jit.Complete ? "MUR_JitComplete" : "MUR_JitPending").Translate().ToString()));
        }
        if (settings.Value.CachePatchedXml || PatchedXmlCache.StatusKey != "MUR_PatchedXmlOff")
            list.Label(PatchedXmlCache.StatusKey.Translate());
        list.Label("MUR_RamStatus".Translate(Reason(status.Plan.Ram),
            InMiB(status.CacheHeldBytes), InMiB(status.ReserveHeldBytes)));
        list.Label("MUR_PrefetchStatus".Translate(status.FileCursor, status.FileSources, status.FilesRead,
            InMiB(status.FileBytesRead), status.FileErrors, status.FilesDeferredForBudget));
        list.Label("MUR_PrefetchMaintenanceStatus".Translate(status.FileRefreshPasses));
        list.Label("MUR_FileDiscoveryStatus".Translate(status.FileDiscoveryPasses, status.FileSourceChanges));
        list.Label("MUR_EnginePackStatus".Translate(status.EnginePackFiles));
        list.Label("MUR_ResidentPackStatus".Translate(status.ResidentPackFiles, status.ResidentPackSources,
            InMiB(status.ResidentPackBytes), status.ResidentPackPasses, status.ResidentPacksDeferred, status.ResidentPackErrors));
        list.Label("MUR_AuditStatus".Translate(status.AuditCompleted, status.AuditSources, status.AuditDuplicates,
            InMiB(status.AuditDuplicateBytes), InMiB(status.AuditHashedBytes), status.AuditErrors, status.AuditSkipped));
        list.Label("MUR_AuditRescanStatus".Translate(status.AuditPassesCompleted, status.AuditChangedFiles));
        list.Label("MUR_XmlAuditStatus".Translate(status.XmlFilesChecked, status.XmlDefinitions,
            status.XmlDefinitionCollisions, status.XmlParseFailures, status.XmlFilesSkipped,
            status.XmlKeysOmitted));
        list.Label("MUR_TextureAuditStatus".Translate(status.TextureFilesChecked, status.TextureFilesValid,
            status.TextureFilesMalformed, status.TextureFilesSkipped,
            InMiB(status.TextureRgba8EquivalentBytes), InMiB(status.TextureBytesValidated)));
        if (status.Memory.FromOperatingSystem)
            list.Label("MUR_MemoryStatus".Translate(InMiB(status.ProcessWorkingSet), InMiB(status.Memory.AvailablePhysical),
                ("MUR_Memory" + status.MemoryState).Translate().ToString()));
        else
            list.Label("MUR_NoMemoryProbe".Translate());

        if (settings.Value.AdaptiveWorkers)
            list.Label("MUR_AdaptiveWorkerStatus".Translate(perf.P95FrameIntervalMs, runtime.AdaptiveWorkerPercent));
        TexturePrewarm gpu = runtime.Gpu;
        list.Label("MUR_GpuStatus".Translate(gpu.StatusKey.Translate().ToString(), gpu.Uploaded));
        list.Label("MUR_GpuMaintenanceStatus".Translate(gpu.PassesCompleted));
        list.Label("MUR_GpuQueueStatus".Translate(gpu.DeferredCount, gpu.DroppedDeferred,
            gpu.RejectedTextures, gpu.FailedUploads));
        if (settings.Value.GpuEnabled)
            list.Label("MUR_GpuDemandStatus".Translate(gpu.AtlasSourcesSkipped, gpu.PriorityUploads));
        if (gpu.Memory.DriverReported)
            list.Label("MUR_GpuBudgetStatus".Translate(InMiB(gpu.Memory.UsageBytes), InMiB(gpu.Memory.BudgetBytes)));

        EngineExperiments experiments = runtime.Experiments;
        if (settings.Value.UncompressedTextureCache)
            list.Label((experiments.UncompressedActive ? "MUR_TextureCodecActive" : "MUR_TextureCodecInactive").Translate());
        if (settings.Value.GpuPrepareGraphics)
            list.Label("MUR_GraphicWarmStatus".Translate(experiments.PreparedGraphics, experiments.GraphicsFailures));
        if (settings.Value.GpuAnisotropicMeshes)
            list.Label(experiments.QualityConflict
                ? "MUR_AnisoConflict".Translate()
                : "MUR_AnisoStatus".Translate(experiments.RefinedTextures));

        Rect row = list.GetRect(30);
        float width = (row.width - 18) / 4;
        if (Widgets.ButtonText(new Rect(row.x, row.y, width, row.height), "MUR_Auto".Translate()))
            runtime.Manual = ManualMode.Auto;
        if (Widgets.ButtonText(new Rect(row.x + width + 6, row.y, width, row.height), "MUR_ForceRun".Translate()))
            runtime.Manual = ManualMode.ForceRun;
        if (Widgets.ButtonText(new Rect(row.x + 2 * (width + 6), row.y, width, row.height), "MUR_Paused".Translate()))
            runtime.Manual = ManualMode.Paused;
        if (Widgets.ButtonText(new Rect(row.x + 3 * (width + 6), row.y, width, row.height), "MUR_ReleaseRam".Translate()))
            runtime.ReleaseRam();
        list.Label("MUR_ManualHint".Translate());
        list.GapLine();
    }

    private void DrawGeneral(Listing_Standard list, ResourceSettings s)
    {
        Toggle(list, "MUR_Enabled", ref s.Enabled);
        if (list.ButtonText("MUR_AssetBoost".Translate()))
        {
            s.ApplyAssetBoost();
            runtime.Manual = ManualMode.Auto;
            changed = true;
        }
        Toggle(list, "MUR_WorldAllowed", ref s.AllowInWorldView);
        Toggle(list, "MUR_UnfocusedAllowed", ref s.AllowWhenUnfocused);
        Toggle(list, "MUR_ReleaseInactive", ref s.ReleaseWhenInactive);
        Toggle(list, "MUR_IdleBoost", ref s.IdleBoost);
        Slider(list, "MUR_IdleSeconds", ref s.IdleSeconds, 10, 3600, 10);
        Slider(list, "MUR_FrameGuard", ref s.FrameGuardMs, 0, 500, 10);
        Toggle(list, "MUR_RecordLoadTiming", ref s.RecordLoadTiming);
        if (TexturePrewarm.SupportedBuild)
        {
            Toggle(list, "MUR_StartupPackExport", ref s.ExportStartupPacks);
            Toggle(list, "MUR_CachePatchedXml", ref s.CachePatchedXml);
            Toggle(list, "MUR_UncompressedTextureCache", ref s.UncompressedTextureCache);
        }
    }

    private void DrawPhases(Listing_Standard list, ResourceSettings s)
    {
        list.GapLine();
        list.Label("MUR_Phases".Translate());
        Rect presets = list.GetRect(28);
        float width = (presets.width - 12) / 3;
        for (int i = 0; i < 3; i++)
        {
            var preset = (IntensityPreset)i;
            string label = ("MUR_Preset" + preset).Translate();
            if (s.Preset == preset) label = "● " + label;
            if (Widgets.ButtonText(new Rect(presets.x + i * (width + 6), presets.y, width, presets.height), label))
            {
                s.ApplyPresetIntensity(preset);
                changed = true;
            }
        }

        Rect header = list.GetRect(26);
        float phaseWidth = header.width * 0.31f;
        const float toggleWidth = 38;
        Widgets.Label(new Rect(header.x, header.y, phaseWidth, header.height), "MUR_PhaseHeading".Translate());
        for (int col = 0; col < 3; col++)
        {
            Rect cell = new(header.x + phaseWidth + col * toggleWidth, header.y, toggleWidth, header.height);
            Widgets.Label(cell, ((ResourceKind)col).ToString().ToUpperInvariant());
        }
        float intensityX = header.x + phaseWidth + 3 * toggleWidth + 8;
        Widgets.Label(new Rect(intensityX, header.y, header.xMax - intensityX, header.height), "MUR_Intensity".Translate());
        for (int i = 0; i < ResourceSettings.PhaseCount; i++)
        {
            var phase = (GamePhase)i;
            Rect row = list.GetRect(30);
            Widgets.Label(new Rect(row.x, row.y, phaseWidth, row.height), PhaseName(phase));
            for (int col = 0; col < 3; col++)
            {
                var kind = (ResourceKind)col;
                Rect box = new(row.x + phaseWidth + col * toggleWidth, row.y, toggleWidth, row.height);
                bool allowed = kind != ResourceKind.Gpu || (TexturePrewarm.SupportedBuild
                    && phase is not (GamePhase.Startup or GamePhase.Loading or GamePhase.Saving));
                if (allowed)
                {
                    bool[] phases = s.PhasesFor(kind);
                    bool value = phases[i];
                    Widgets.Checkbox(new Vector2(box.x, box.y + 3), ref value, 22);
                    TooltipHandler.TipRegion(box, kind.ToString());
                    if (value != phases[i])
                    {
                        phases[i] = value;
                        changed = true;
                    }
                }
                else Widgets.Label(box, "–");
            }
            float sliderX = row.x + phaseWidth + 3 * toggleWidth + 8;
            Rect slider = new(sliderX, row.y, row.xMax - sliderX - 48, row.height);
            int intensity = (int)Math.Round(Widgets.HorizontalSlider(slider, s.PhaseIntensity[i], 5, 100, true, roundTo: 5));
            if (intensity != s.PhaseIntensity[i])
            {
                s.PhaseIntensity[i] = intensity;
                s.Preset = IntensityPreset.Custom;
                changed = true;
            }
            Widgets.Label(new Rect(row.xMax - 44, row.y, 44, row.height), s.PhaseIntensity[i] + "%");
        }
    }

    private void DrawResources(Listing_Standard list, ResourceSettings s)
    {
        list.GapLine();
        Toggle(list, "MUR_CpuEnabled", ref s.CpuEnabled);
        Toggle(list, "MUR_CpuIndexAssets", ref s.CpuIndexAssets);
        Toggle(list, "MUR_CpuJitPrewarm", ref s.CpuJitPrewarm);
        Toggle(list, "MUR_AdaptiveWorkers", ref s.AdaptiveWorkers);
        Slider(list, "MUR_CpuAuditInterval", ref s.CpuAuditRescanMinutes, 0, 120, 5);
        Slider(list, "MUR_FileDiscoveryInterval", ref s.AssetDiscoveryRescanMinutes, 0, 120, 5);
        Slider(list, "MUR_Workers", ref s.CpuMaxWorkers, 0, Math.Min(256, Math.Max(Environment.ProcessorCount, 4)), 1);
        Toggle(list, "MUR_Synthetic", ref s.CpuSyntheticLoad);
        Slider(list, "MUR_Duty", ref s.CpuSyntheticDutyPercent, 5, 100, 5);

        list.GapLine();
        Toggle(list, "MUR_PrefetchEnabled", ref s.RamFileCache);
        Slider(list, "MUR_PrefetchRefreshInterval", ref s.RamPrefetchRefreshMinutes, 0, 120, 5);
        Toggle(list, "MUR_RamEnabled", ref s.RamEnabled);
        Toggle(list, "MUR_RamGcLowLatency", ref s.RamGcLowLatency);
        Toggle(list, "MUR_HoldCopies", ref s.RamHoldCopies);
        Slider(list, "MUR_CacheLimit", ref s.RamFileCacheMiB, 64, 8192, 64);
        Toggle(list, "MUR_ResidentPacks", ref s.RamResidentPacks);
        Slider(list, "MUR_ResidentPackLimit", ref s.RamResidentPackMiB, 64, 16384, 64);
        Toggle(list, "MUR_RamReserve", ref s.RamReserve);
        Slider(list, "MUR_RamReserveLimit", ref s.RamReserveMiB, 64, 8192, 64);
        Slider(list, "MUR_RamKeepFree", ref s.RamMinFreeMiB, 1024, 32768, 256);

        list.GapLine();
        if (TexturePrewarm.SupportedBuild)
        {
            Toggle(list, "MUR_GpuEnabled", ref s.GpuEnabled);
            Toggle(list, "MUR_GpuResidencyBoost", ref s.GpuResidencyBoost);
            Toggle(list, "MUR_GpuDemandPriority", ref s.GpuDemandPriority);
            Toggle(list, "MUR_GpuPrepareGraphics", ref s.GpuPrepareGraphics);
            Toggle(list, "MUR_GpuAnisotropicMeshes", ref s.GpuAnisotropicMeshes);
            Slider(list, "MUR_GpuTarget", ref s.GpuTargetPercent, 10, ResourceSettings.MaxGpuTargetPercent, 1);
            Slider(list, "MUR_GpuKeepFree", ref s.GpuKeepFreeMiB, 512, 8192, 256);
            Slider(list, "MUR_GpuFrameBudget", ref s.GpuFrameBudgetTenthMs, 2, 80, 1);
            Slider(list, "MUR_GpuRefreshInterval", ref s.GpuRefreshSeconds, 0, 600, 10);
            Slider(list, "MUR_GpuMaxTextureDimension", ref s.GpuMaxTextureDimension, 1024, 4096, 1024);
        }
        else list.Label("MUR_GpuUnsupported".Translate());
        list.Label("MUR_GpuHint".Translate());
    }

    private void Toggle(Listing_Standard list, string key, ref bool value)
    {
        bool before = value;
        list.CheckboxLabeled(key.Translate(), ref value);
        changed |= value != before;
    }

    private void Slider(Listing_Standard list, string key, ref int value, int min, int max, int step)
    {
        list.Label(key.Translate(value));
        int next = (int)Math.Round(Widgets.HorizontalSlider(list.GetRect(20), value, min, max, true, roundTo: step));
        if (next == value) return;
        value = next;
        changed = true;
    }

    private static string PhaseName(GamePhase phase) => ("MUR_Phase" + phase).Translate();
    private static string Reason(ResourceActivation activation) => ("MUR_Reason" + activation.Reason).Translate();
    private static string ModeName(ManualMode mode) => ("MUR_" + mode).Translate();
    private static string InMiB(long bytes) => (bytes / (double)ResourceSettings.MiB).ToString("N1");
}
