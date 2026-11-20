using System;

namespace MoreUseResources.Core
{
    public enum ResourceKind
    {
        Cpu = 0,
        Ram = 1,
        Gpu = 2,
    }

    public enum IntensityPreset
    {
        Conservative = 0,
        Standard = 1,
        Aggressive = 2,
        Custom = 3,
    }

    public enum WorkerPriority
    {
        Lowest = 0,
        BelowNormal = 1,
    }

    public enum ProcessPriorityChoice
    {
        Unchanged = 0,
        AboveNormal = 1,
        High = 2,
    }

    /// <summary>Runtime-only override. Never saved, so a restart always returns to automatic control.</summary>
    public enum ManualMode
    {
        Auto = 0,
        ForceRun = 1,
        Paused = 2,
    }

    /// <summary>
    /// All user settings as plain data. The game-side ModSettings class only persists this.
    /// Instances handed to background threads are private copies made with <see cref="Clone"/>.
    /// </summary>
    public sealed class ResourceSettings
    {
        public const int PhaseCount = PhaseResolver.ConfigurablePhaseCount;
        public const long MiB = 1024L * 1024L;

        // ---- global ----
        public bool Enabled = true;
        public IntensityPreset Preset = IntensityPreset.Standard;
        public bool AllowInWorldView = true;
        public bool AllowWhenUnfocused = true;
        public bool IdleBoost = false;
        public int IdleSeconds = 60;
        public bool ReleaseWhenInactive = false;
        /// <summary>Average frame time (ms) above which main-thread work and synthetic load back off. 0 disables the guard.</summary>
        public int FrameGuardMs = 66;
        public bool ShowOverlay = false;
        /// <summary>Time the game's load stages after this mod starts. Read at launch; a change applies on the next start.</summary>
        public bool RecordLoadTiming = true;
        public bool CachePatchedXml = false;
        public bool UncompressedTextureCache = false;
        public bool ExportStartupPacks = false;

        // ---- per phase ----
        public bool[] CpuPhases = new bool[PhaseCount];
        public bool[] RamPhases = new bool[PhaseCount];
        public bool[] GpuPhases = new bool[PhaseCount];
        /// <summary>Percent 5..100 applied to worker count, growth rate and per-frame budget.</summary>
        public int[] PhaseIntensity = new int[PhaseCount];

        // ---- CPU ----
        public bool CpuEnabled = true;
        /// <summary>0 = automatic (logical processors minus two, at least one).</summary>
        public int CpuMaxWorkers = 0;
        public WorkerPriority CpuWorkerPriority = WorkerPriority.Lowest;
        public bool CpuSyntheticLoad = false;
        public bool CpuJitPrewarm = false;
        public bool AdaptiveWorkers = false;
        /// <summary>Fingerprint enabled mods' real files and report exact duplicate content.</summary>
        public bool CpuIndexAssets = true;
        /// <summary>Minutes between full read-only content integrity passes; 0 disables rescans.</summary>
        public int CpuAuditRescanMinutes = 10;
        /// <summary>Periodically re-enumerate mod asset folders for added/deleted files; 0 = startup only.</summary>
        public int AssetDiscoveryRescanMinutes = 10;
        public int CpuSyntheticDutyPercent = 50;
        public ProcessPriorityChoice ProcessPriority = ProcessPriorityChoice.Unchanged;

        // ---- RAM ----
        public bool RamEnabled = true;
        public bool RamFileCache = true;
        /// <summary>Minutes between OS cache rewarming passes of real mod/game assets; zero disables.</summary>
        public int RamPrefetchRefreshMinutes = 5;
        /// <summary>true: keep the bytes in this process. false: read once to warm the OS file cache and drop them.</summary>
        public bool RamHoldCopies = true;
        public int RamFileCacheMiB = 4096;
        public int RamMinFreeMiB = 6144;
        /// <summary>Map the game's read-only Assets.zip packs and keep their pages in this process.</summary>
        public bool RamResidentPacks = false;
        public int RamResidentPackMiB = 2048;
        public bool RamReserve = false;
        public int RamReserveMiB = 4096;
        public bool RamGcLowLatency = false;
        /// <summary>
        /// Ask for the server GC in the game's RimWorld.runtimeconfig.json; applies from the next launch. On by default:
        /// measured on 2026-11-20 it cut GC stops in play from 1.15% to 0.14% of the time for about 115 MiB more memory.
        /// </summary>
        public bool ServerGc = true;

        // ---- GPU ----
        public bool GpuEnabled = true;
        public bool GpuPrewarm = true;
        public bool GpuResidencyBoost = false;
        public bool GpuDemandPriority = false;
        public bool GpuPrepareGraphics = false;
        public bool GpuAnisotropicMeshes = false;
        /// <summary>While paused, rebuild changed off-screen sections of the current map with the game's own method.</summary>
        public bool GpuPrepareSections = false;
        /// <summary>Upper bound for device-local usage as percent of the driver budget. Capped at 75 to stay under the engine's own eviction watermark (about 80%).</summary>
        public int GpuTargetPercent = 60;
        public int GpuKeepFreeMiB = 2048;
        /// <summary>Main-thread milliseconds per frame, in tenths (20 = 2.0 ms).</summary>
        public int GpuFrameBudgetTenthMs = 20;
        /// <summary>How often to recheck Waylith's real textures after a full pass; 0 disables maintenance.</summary>
        public int GpuRefreshSeconds = 60;
        /// <summary>Largest actual game texture edge to prewarm; increase at your own risk.</summary>
        public int GpuMaxTextureDimension = 1024;
        public bool GpuReserve = false;
        public int GpuReserveMiB = 2048;

        public const int MaxGpuTargetPercent = 75;

        public ResourceSettings()
        {
            ResetPhaseDefaults();
        }

        public void ApplyAssetBoost()
        {
            ResetPhaseDefaults();
            ApplyPresetIntensity(IntensityPreset.Aggressive);
            Enabled = CpuEnabled = RamEnabled = GpuEnabled = true;
            CpuMaxWorkers = 0;
            CpuIndexAssets = AdaptiveWorkers = true;
            RamFileCache = RamResidentPacks = ExportStartupPacks = true;
            CpuSyntheticLoad = RamReserve = RamHoldCopies = false;
            RamResidentPackMiB = Math.Max(RamResidentPackMiB, 4096);
            RamMinFreeMiB = Math.Max(RamMinFreeMiB, 6144);
            GpuResidencyBoost = GpuDemandPriority = true;
            GpuTargetPercent = Math.Max(GpuTargetPercent, 65);
            GpuKeepFreeMiB = Math.Max(GpuKeepFreeMiB, 2048);
            GpuFrameBudgetTenthMs = Math.Max(GpuFrameBudgetTenthMs, 20);
            GpuMaxTextureDimension = Math.Max(GpuMaxTextureDimension, 2048);
            Clamp();
        }

        public void ResetPhaseDefaults()
        {
            for (int i = 0; i < PhaseCount; i++)
            {
                bool on = i != (int)GamePhase.Saving;
                CpuPhases[i] = on;
                RamPhases[i] = on;
                // GPU work runs on the main thread, so it stays off while the game is loading by default.
                GpuPhases[i] = on && i != (int)GamePhase.Startup && i != (int)GamePhase.Loading;
            }
            ApplyPresetIntensity(IntensityPreset.Standard);
        }

        /// <summary>Fills the per-phase intensity table for a preset. Custom leaves the table untouched.</summary>
        public void ApplyPresetIntensity(IntensityPreset preset)
        {
            int[]? table = preset switch
            {
                //                         Startup Menu Load Normal Fast Paused Saving
                IntensityPreset.Conservative => new[] { 50, 50, 25, 25, 15, 50, 10 },
                IntensityPreset.Standard => new[] { 100, 100, 50, 50, 30, 100, 25 },
                IntensityPreset.Aggressive => new[] { 100, 100, 100, 80, 60, 100, 50 },
                _ => null,
            };
            Preset = preset;
            if (table != null)
            {
                Array.Copy(table, PhaseIntensity, PhaseCount);
            }
        }

        public bool[] PhasesFor(ResourceKind kind)
        {
            switch (kind)
            {
                case ResourceKind.Cpu: return CpuPhases;
                case ResourceKind.Ram: return RamPhases;
                default: return GpuPhases;
            }
        }

        public bool ResourceEnabled(ResourceKind kind)
        {
            switch (kind)
            {
                case ResourceKind.Cpu: return CpuEnabled;
                case ResourceKind.Ram: return RamEnabled;
                default: return GpuEnabled;
            }
        }

        /// <summary>Forces every value into its safe range. Called after loading and before use.</summary>
        public void Clamp()
        {
            CpuPhases = FixLength(CpuPhases);
            RamPhases = FixLength(RamPhases);
            GpuPhases = FixLength(GpuPhases);
            if (PhaseIntensity == null || PhaseIntensity.Length != PhaseCount)
            {
                PhaseIntensity = new int[PhaseCount];
                ApplyPresetIntensity(Preset == IntensityPreset.Custom ? IntensityPreset.Standard : Preset);
            }
            for (int i = 0; i < PhaseCount; i++)
            {
                PhaseIntensity[i] = Math.Clamp(PhaseIntensity[i], 5, 100);
            }
            // The mod is not constructed early enough to do main-thread GPU work during startup.
            GpuPhases[(int)GamePhase.Startup] = false;

            if (!Enum.IsDefined(typeof(IntensityPreset), Preset)) Preset = IntensityPreset.Standard;
            if (!Enum.IsDefined(typeof(WorkerPriority), CpuWorkerPriority)) CpuWorkerPriority = WorkerPriority.Lowest;
            if (!Enum.IsDefined(typeof(ProcessPriorityChoice), ProcessPriority)) ProcessPriority = ProcessPriorityChoice.Unchanged;

            IdleSeconds = Math.Clamp(IdleSeconds, 10, 3600);
            FrameGuardMs = FrameGuardMs <= 0 ? 0 : Math.Clamp(FrameGuardMs, 20, 500);
            CpuMaxWorkers = Math.Clamp(CpuMaxWorkers, 0, 256);
            CpuSyntheticDutyPercent = Math.Clamp(CpuSyntheticDutyPercent, 5, 100);
            CpuAuditRescanMinutes = Math.Clamp(CpuAuditRescanMinutes, 0, 120);
            AssetDiscoveryRescanMinutes = Math.Clamp(AssetDiscoveryRescanMinutes, 0, 120);
            RamFileCacheMiB = Math.Clamp(RamFileCacheMiB, 64, 262144);
            RamPrefetchRefreshMinutes = Math.Clamp(RamPrefetchRefreshMinutes, 0, 120);
            RamMinFreeMiB = Math.Clamp(RamMinFreeMiB, 1024, 262144);
            RamReserveMiB = Math.Clamp(RamReserveMiB, 64, 262144);
            RamResidentPackMiB = Math.Clamp(RamResidentPackMiB, 64, 65536);
            GpuTargetPercent = Math.Clamp(GpuTargetPercent, 10, MaxGpuTargetPercent);
            GpuKeepFreeMiB = Math.Clamp(GpuKeepFreeMiB, 512, 65536);
            GpuFrameBudgetTenthMs = Math.Clamp(GpuFrameBudgetTenthMs, 2, 80);
            GpuRefreshSeconds = Math.Clamp(GpuRefreshSeconds, 0, 600);
            GpuMaxTextureDimension = Math.Clamp(GpuMaxTextureDimension, 1024, 4096);
            GpuReserveMiB = Math.Clamp(GpuReserveMiB, 64, 65536);
        }

        private static bool[] FixLength(bool[]? src)
        {
            if (src != null && src.Length == PhaseCount)
            {
                return src;
            }
            var fixedArr = new bool[PhaseCount];
            if (src != null)
            {
                Array.Copy(src, fixedArr, Math.Min(src.Length, PhaseCount));
            }
            return fixedArr;
        }

        public ResourceSettings Clone()
        {
            var c = (ResourceSettings)MemberwiseClone();
            c.CpuPhases = (bool[])CpuPhases.Clone();
            c.RamPhases = (bool[])RamPhases.Clone();
            c.GpuPhases = (bool[])GpuPhases.Clone();
            c.PhaseIntensity = (int[])PhaseIntensity.Clone();
            return c;
        }

        /// <summary>Every setting as name=value in declaration order, for a pasted status report.</summary>
        public string Describe()
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (System.Reflection.FieldInfo field in typeof(ResourceSettings).GetFields(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                object? value = field.GetValue(this);
                string text = value is Array array
                    ? "[" + string.Join(",", System.Linq.Enumerable.Cast<object>(array)) + "]"
                    : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                parts.Add(field.Name + "=" + text);
            }
            return string.Join(" ", parts);
        }

        /// <summary>Resolves the automatic worker count for a machine.</summary>
        public int EffectiveMaxWorkers(int logicalProcessors)
        {
            int auto = Math.Max(1, logicalProcessors - 2);
            int limit = Math.Max(1, logicalProcessors);
            return CpuMaxWorkers <= 0 ? auto : Math.Min(CpuMaxWorkers, limit);
        }
    }
}
