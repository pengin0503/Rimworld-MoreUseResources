using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MoreUseResources.Benchmarks.Measurement;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// The mod's reaction to a memory shortage. The controller runs with a probe that reports the real
/// machine until the scenario makes it report 512 MiB less available memory than RamMinFreeMiB.
/// Measured: how fast held copies (target=copies) or mapped packs (target=packs) are given back,
/// whether reads stop while the shortage lasts, how much RSS really returns to the OS, and when
/// growth resumes after the shortage ends (the governor holds growth 10 s, the file cache waits
/// 30 s after a release). No memory is actually exhausted: a real out-of-memory state is not tested.
/// </summary>
public sealed class MemoryPressureScenario : Scenario
{
    private const long DeficitBytes = 512L << 20;

    public override string Id => "memory-pressure";
    public override string Title => "メモリ不足時の解放・読み込み停止・回復";
    public override string ProductionCode => "MemoryGovernor.Tick, ResourceController.ControlTick (memoryOk, ReleaseAll), FileCache cooldown, NativeBlockStore";
    public override string NotReproduced =>
        "実際のメモリ枯渇（OS のページアウト・OOM）、Windows の空き物理メモリの算出方法、glibc と Windows ヒープで異なる解放後の RSS";

    public override IReadOnlyList<Condition> Conditions(RunEnvironment env)
    {
        if (env.Dataset.Files.Count == 0) throw new InvalidOperationException("no input files");
        var list = new List<Condition> { Condition.Of(Id, ("target", "copies"), ("workers", Common.DefaultWorkers)) };
        if (env.Dataset.Packs.Any(p => p.EndsWith(EnginePackFiles.AssetPackFileName, StringComparison.OrdinalIgnoreCase)))
            list.Add(Condition.Of(Id, ("target", "packs"), ("workers", Common.DefaultWorkers)));
        return list;
    }

    public override Trial CreateTrial(Condition condition, RunEnvironment env) => new PressureTrial(condition, env);

    private sealed class PressureTrial : Trial
    {
        private readonly Condition _c;
        private readonly RunEnvironment _env;
        private ScriptedMemoryProbe? _probe;
        private ResourceController? _controller;
        private ResourceSettings? _settings;

        public PressureTrial(Condition c, RunEnvironment env)
        {
            _c = c;
            _env = env;
        }

        private bool Packs => _c.Get("target") == "packs";

        public override void Prepare(TrialContext ctx)
        {
            var notes = new List<string>();
            _settings = ControllerHarness.Settings(_env.Options, Packs ? "asset-boost" : "default", 0, notes);
            foreach (string n in notes) ctx.Note(n);
            _probe = new ScriptedMemoryProbe(new SystemMemoryProbe());
            _controller = ControllerHarness.Create(_env.Dataset, _settings, Environment.ProcessorCount, _probe);
            ctx.Timeout = TimeSpan.FromSeconds(Math.Max(ctx.Timeout.TotalSeconds, _env.Options.Mode == RunMode.Smoke ? 40 : 120));
        }

        private long Held(ControllerStatus s) => Packs ? s.ResidentPackBytes : s.CacheHeldBytes;

        public override void Execute(TrialContext ctx)
        {
            ResourceController controller = _controller!;
            ScriptedMemoryProbe probe = _probe!;
            ResourceSettings s = _settings!;
            controller.Start();
            long totalBytes = _env.Dataset.DiscoveredFiles.Sum(f => new System.IO.FileInfo(f).Length);
            long want = Packs ? 1 : Math.Max(1, Math.Min(64L << 20, Math.Min(s.RamFileCacheMiB * ResourceSettings.MiB, totalBytes) / 2));
            bool grown = ctx.WaitUntil(() =>
            {
                ControllerStatus st = controller.GetStatus();
                return Packs ? st.ResidentPackPasses > 0 && st.ResidentPackBytes > 0 : st.CacheHeldBytes >= want || (st.FileSources > 0 && st.FilePassComplete && st.CacheHeldBytes > 0);
            }, 20, limit: TimeSpan.FromSeconds(Math.Max(10, ctx.Timeout.TotalSeconds / 3)));
            ControllerStatus before = controller.GetStatus();
            ctx.Metric("held_before_mib", Held(before) / 1048576.0);
            ctx.Metric("held_target_reached", grown ? 1 : 0);
            // Copies grow at the controller's pump cadence, so the target may not be reached in time;
            // any held amount is enough to measure the release.
            if (!grown && Held(before) > 0)
                ctx.Note("held " + (Held(before) / 1048576.0).ToString("F1") + " MiB before the shortage (target " + (want / 1048576.0).ToString("F1") + " MiB not reached in time)");
            if (Held(before) == 0)
            {
                ctx.Invalidate("nothing was held before the shortage (" + (Packs ? "pack residency" : "native copies") + " did not grow)");
                ctx.Metric("shutdown_ms", ControllerHarness.Shutdown(controller));
                return;
            }
            ResourceSnapshot rssBefore = ProcessProbe.Capture(includeThreads: false);

            // Shortage: the OS reports 512 MiB less than the configured floor.
            long floor = s.RamMinFreeMiB * ResourceSettings.MiB;
            long pressureEvents0 = before.PressureEvents;
            var release = Stopwatch.StartNew();
            probe.ForceAvailable(Math.Max(0, floor - DeficitBytes));
            bool released = ctx.WaitUntil(() => Held(controller.GetStatus()) == 0, 5, limit: TimeSpan.FromSeconds(10));
            ctx.Metric("release_ms", released ? release.Elapsed.TotalMilliseconds : double.NaN);
            ctx.Metric("released", released ? 1 : 0);
            ControllerStatus during = controller.GetStatus();
            ctx.Metric("pressure_events", during.PressureEvents - pressureEvents0);
            ctx.Metric("memory_state_pressure", during.MemoryState == MemoryGovernorState.Pressure ? 1 : 0);
            // Reads while the shortage lasts: none may start.
            long read0 = during.FileBytesRead + during.AuditHashedBytes;
            ctx.WaitUntil(() => false, 20, limit: TimeSpan.FromMilliseconds(1000));
            ctx.Truncated = false;
            ControllerStatus later = controller.GetStatus();
            ctx.Metric("read_during_shortage_mib", (later.FileBytesRead + later.AuditHashedBytes - read0) / 1048576.0);
            ctx.Metric("held_during_shortage_mib", Held(later) / 1048576.0);
            ResourceSnapshot rssAfter = ProcessProbe.Capture(includeThreads: false);
            if (rssBefore.RssAnonBytes.HasValue && rssAfter.RssAnonBytes.HasValue)
                ctx.Metric("rss_anon_returned_mib", (rssBefore.RssAnonBytes.Value - rssAfter.RssAnonBytes.Value) / 1048576.0);
            if (rssBefore.RssFileBytes.HasValue && rssAfter.RssFileBytes.HasValue)
                ctx.Metric("rss_file_returned_mib", (rssBefore.RssFileBytes.Value - rssAfter.RssFileBytes.Value) / 1048576.0);
            // Whether the C allocator kept the freed copies: glibc's malloc_trim gives free heap pages back.
            if (!Packs && rssBefore.RssAnonBytes.HasValue && NativeAllocator.Trim())
            {
                ResourceSnapshot trimmed = ProcessProbe.Capture(includeThreads: false);
                if (trimmed.RssAnonBytes.HasValue)
                    ctx.Metric("rss_anon_returned_after_trim_mib", (rssBefore.RssAnonBytes.Value - trimmed.RssAnonBytes.Value) / 1048576.0);
            }

            // Recovery.
            var regrow = Stopwatch.StartNew();
            probe.Clear();
            double limitSeconds = Math.Min(Math.Max(1, ctx.Timeout.TotalSeconds - ctx.Elapsed.TotalSeconds - 2), _env.Options.Mode == RunMode.Smoke ? 8 : 45);
            bool regrown = ctx.WaitUntil(() => Held(controller.GetStatus()) > 0, 20, limit: TimeSpan.FromSeconds(limitSeconds));
            ctx.Truncated = false;
            ctx.Metric("regrow_wait_s", limitSeconds);
            if (regrown) ctx.Metric("regrow_ms", regrow.Elapsed.TotalMilliseconds);
            else ctx.Note("growth did not resume within " + limitSeconds.ToString("F0") + " s after the shortage (file cache waits 30 s after a release)");
            ctx.Metric("regrown", regrown ? 1 : 0);
            ControllerStatus end = controller.GetStatus();
            ControllerHarness.StatusMetrics(ctx, end);
            ctx.Metric("allocation_failures", end.AllocationFailures);
            ctx.Metric("probe_reads", probe.Reads);
            ctx.Metric("shutdown_ms", ControllerHarness.Shutdown(controller));
        }

        public override void Dispose() => _controller?.Shutdown();
    }
}

/// <summary>glibc's malloc_trim, used only to tell allocator retention apart from memory still in use.</summary>
internal static class NativeAllocator
{
    private delegate int MallocTrimFn(nuint pad);

    /// <summary>Returns false where there is no glibc (Windows, macOS, musl).</summary>
    public static bool Trim()
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("libc.so.6", out IntPtr lib)
            || !System.Runtime.InteropServices.NativeLibrary.TryGetExport(lib, "malloc_trim", out IntPtr fn)) return false;
        System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<MallocTrimFn>(fn)(0);
        return true;
    }
}
