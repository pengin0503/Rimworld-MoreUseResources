using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MoreUseResources.Benchmarks.Analysis;
using MoreUseResources.Benchmarks.Measurement;
using MoreUseResources.Benchmarks.Running;
using Xunit;

// The runner and the redactor use process-wide state (signal handlers, GC event listeners, thread
// counts by name prefix), so the tests run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MoreUseResources.Benchmarks.Tests;

public class ResultWriterTests
{
    private static TrialResult Sample(int seq, string note) => new()
    {
        RunId = "r1", Sequence = seq, Scenario = "audit", ConditionKey = "cache=warm;variant=cadence;workers=2",
        Parameters = new Dictionary<string, string> { ["workers"] = "2", ["cache"] = "warm", ["variant"] = "cadence" },
        Round = 0, Warmup = false, StartedUtc = new DateTime(2026, 11, 18, 1, 2, 3, DateTimeKind.Utc),
        Metrics = new Dictionary<string, double>(StringComparer.Ordinal) { ["items_per_s"] = 12.5, ["wall_ms"] = 1000, ["odd"] = double.NaN },
        Notes = { note }, Truncated = true, CacheVerified = true, CacheRequested = "warm",
    };

    [Fact]
    public void TrialsRoundTripAndCsvIsEscaped()
    {
        using var dir = new TempDir();
        using (var w = new ResultWriter(dir.Path))
        {
            w.Append(Sample(1, "plain"));
            var failed = Sample(2, "with, comma and \"quotes\"");
            failed.Status = TrialStatus.Failed;
            failed.Error = "IOException: a,b\nnext line";
            w.Append(failed);
        }
        List<TrialResult> back = ResultWriter.ReadTrials(dir.Path);
        Assert.Equal(2, back.Count);
        Assert.Equal(12.5, back[0].Metrics["items_per_s"]);
        Assert.True(double.IsNaN(back[0].Metrics["odd"]));
        Assert.True(back[0].Truncated);
        Assert.Equal(TrialStatus.Failed, back[1].Status);
        Assert.Equal("with, comma and \"quotes\"", back[1].Notes[0]);
        Assert.Equal("cadence", back[1].Parameters["variant"]);

        string[] csv = File.ReadAllLines(Path.Combine(dir.Path, "trials.csv"));
        Assert.StartsWith("run_id,seq,scenario,condition,", csv[0], StringComparison.Ordinal);
        int columns = csv[0].Split(',').Length;
        Assert.Equal(columns, ResultWriter.HeadlineMetrics.Length + 13);
        Assert.Contains("\"IOException: a,b", string.Join("\n", csv), StringComparison.Ordinal);
        Assert.Equal("\"a,b\"", ResultWriter.Csv("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", ResultWriter.Csv("say \"hi\""));
        Assert.Equal("plain", ResultWriter.Csv("plain"));

        string[] metrics = File.ReadAllLines(Path.Combine(dir.Path, "metrics.csv"));
        Assert.Equal(1 + 2 * 3, metrics.Length); // header + 3 metrics per trial
    }

    [Fact]
    public void AppendingKeepsOneHeaderAndACutLastLineIsIgnored()
    {
        using var dir = new TempDir();
        using (var w = new ResultWriter(dir.Path)) w.Append(Sample(1, "a"));
        using (var w = new ResultWriter(dir.Path)) w.Append(Sample(2, "b"));
        Assert.Single(File.ReadLines(Path.Combine(dir.Path, "trials.csv")), l => l.StartsWith("run_id,", StringComparison.Ordinal));
        // A hard kill can leave half a JSON line.
        File.AppendAllText(Path.Combine(dir.Path, "trials.jsonl"), "{\"RunId\":\"r1\",\"Seq");
        Assert.Equal(2, ResultWriter.ReadTrials(dir.Path).Count);
    }

    [Fact]
    public void AtomicWritesLeaveNoTemporaryFiles()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "summary.json");
        ResultWriter.WriteJsonAtomic(path, new Dictionary<string, object?> { ["x"] = 1, ["nan"] = double.NaN });
        ResultWriter.WriteJsonAtomic(path, new Dictionary<string, object?> { ["x"] = 2 });
        Assert.Equal(2, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("x").GetInt32());
        Assert.Single(Directory.GetFiles(dir.Path));
    }
}

public class SafetyGuardTests
{
    private sealed class FakeProbe : ISafetyProbe
    {
        public long Rss = 100L << 20;
        public long? Available = 8000L << 20;
        public long? FreeDisk = 50_000L << 20;
        public long Scratch;
        public long ProcessRssBytes() => Rss;
        public long? AvailableMemoryBytes() => Available;
        public long? FreeDiskBytes(string path) => FreeDisk;
        public long DirectorySizeBytes(string path) => Scratch;
    }

    private static BenchOptions Options() => BenchOptions.Parse(new[]
    {
        "run", "--max-rss-mib", "2000", "--ram-budget-mib", "256", "--min-free-mib", "1000",
        "--max-disk-mib", "500", "--min-free-disk-mib", "2000", "--max-runtime-min", "60",
    });

    [Theory]
    [InlineData("rss", "process RSS")]
    [InlineData("free-memory", "machine free memory")]
    [InlineData("disk", "free disk")]
    [InlineData("scratch", "scratch data")]
    public void EachLimitStopsTheRunOnce(string limit, string expected)
    {
        using var dir = new TempDir();
        var probe = new FakeProbe();
        using var cts = new CancellationTokenSource();
        using var guard = new SafetyGuard(Options(), dir.Path, dir.Path, cts, probe);
        Assert.Null(guard.CheckOnce());
        Assert.False(cts.IsCancellationRequested);
        switch (limit)
        {
            case "rss": probe.Rss = 2001L << 20; break;
            case "free-memory": probe.Available = 999L << 20; break;
            case "disk": probe.FreeDisk = 1999L << 20; break;
            case "scratch": probe.Scratch = 501L << 20; break;
        }
        string? reason = null;
        for (int i = 0; i < 11 && reason == null; i++) reason = guard.CheckOnce(); // disk checks run every 10th time
        Assert.NotNull(reason);
        Assert.StartsWith(expected, reason, StringComparison.Ordinal);
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(reason, guard.StopReason);
        probe.Rss = 3000L << 20; // a later, different reason does not replace the first
        guard.CheckOnce();
        Assert.Equal(reason, guard.StopReason);
    }

    [Fact]
    public void MissingReadingsDoNotStopTheRun()
    {
        using var dir = new TempDir();
        var probe = new FakeProbe { Available = null, FreeDisk = null };
        using var cts = new CancellationTokenSource();
        using var guard = new SafetyGuard(Options(), dir.Path, dir.Path, cts, probe);
        for (int i = 0; i < 12; i++) Assert.Null(guard.CheckOnce());
    }
}

public class MeasurementTests
{
    [Fact]
    public void PageCacheSeesAFileThatWasJustRead()
    {
        if (!PageCache.Supported) return;
        using var dir = new TempDir();
        string file = dir.Sub("data.bin");
        File.WriteAllBytes(file, new byte[4 << 20]);
        Assert.Equal(4L << 20, PageCache.Warm(new[] { file }));
        Residency r = PageCache.Measure(new[] { file, dir.Sub("missing.bin") });
        Assert.Equal(1, r.Unreadable);
        Assert.Equal(1.0, r.Fraction, 3);
        // Eviction is advisory (tmpfs and overlay filesystems may keep the pages); it must not fail.
        PageCache.Evict(new[] { file });
        Assert.InRange(PageCache.Measure(new[] { file }).Fraction, 0.0, 1.0);
    }

    [Fact]
    public void GcPausesAreRecordedWithUtcTimes()
    {
        using var listener = new GcPauseListener();
        var wait = System.Diagnostics.Stopwatch.StartNew();
        GC.Collect();
        while (listener.EventsSeen == 0 && wait.ElapsedMilliseconds < 3000) Thread.Sleep(20);
        if (listener.EventsSeen == 0) return; // runtime events disabled in this environment
        DateTime start = DateTime.UtcNow;
        for (int i = 0; i < 3; i++) GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        DateTime end = DateTime.UtcNow;
        List<GcPause> pauses = listener.Between(start, end, expected: 3);
        Assert.True(pauses.Count >= 3, "pauses seen: " + pauses.Count);
        Assert.All(pauses, p =>
        {
            Assert.Equal(DateTimeKind.Utc, p.EndUtc.Kind);
            Assert.InRange(p.EndUtc, start.AddSeconds(-1), end.AddSeconds(1));
            Assert.True(p.Milliseconds >= 0);
        });
    }

    [Fact]
    public void SnapshotsAndDeltasReportTheProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        ResourceSnapshot a = ProcessProbe.Capture();
        byte[] keep = new byte[8 << 20];
        new Random(1).NextBytes(keep);
        Thread.Sleep(20);
        ResourceSnapshot b = ProcessProbe.Capture();
        var m = new Dictionary<string, double>();
        ResourceDelta.Add(m, a, b, Environment.ProcessorCount, peakRssReset: false);
        Assert.True(m["wall_ms"] >= 15);
        Assert.True(m["alloc_mib"] >= 7.9, "alloc_mib " + m["alloc_mib"]);
        Assert.True(m.ContainsKey("cpu_ms"));
        Assert.True(b.OpenFiles > 0);
        GC.KeepAlive(keep);
    }

    [Fact]
    public void DescriptorClassificationSeparatesRuntimeFromFiles()
    {
        Assert.True(ProcessProbe.IsFileDescriptor("/data/Mods/A/Textures/a.png"));
        Assert.False(ProcessProbe.IsFileDescriptor("pipe:[1234]"));
        Assert.False(ProcessProbe.IsFileDescriptor("anon_inode:[eventpoll]"));
        Assert.False(ProcessProbe.IsFileDescriptor("/dev/null"));
        Assert.False(ProcessProbe.IsFileDescriptor("/usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.15/System.IO.MemoryMappedFiles.dll"));
        Assert.False(ProcessProbe.IsFileDescriptor("/usr/lib/x86_64-linux-gnu/libssl.so.3"));
        if (!OperatingSystem.IsLinux()) return;
        using var dir = new TempDir();
        string file = dir.Sub("open.txt");
        File.WriteAllText(file, "x");
        using var handle = File.OpenHandle(file);
        Assert.Contains(ProcessProbe.OpenDescriptors().Values, t => t == file);
    }
}

public class FormattingTests
{
    [Theory]
    [InlineData(48.0, "48")]
    [InlineData(1.0, "1")]
    [InlineData(0.0, "0")]
    [InlineData(7.987, "7.99")]
    [InlineData(123.45, "123.5")]
    [InlineData(2585.4, "2585")]
    [InlineData(0.00848, "0.00848")]
    [InlineData(-1.79, "-1.79")]
    [InlineData(double.NaN, "-")]
    public void NumbersAreShortAndWholeNumbersHaveNoDecimals(double v, string expected) => Assert.Equal(expected, Analyzer.F(v));

    [Fact]
    public void DifferencesNeedTrialsAndSeparatedRanges()
    {
        MetricSummary S(double min, double max, int n) => new(n, min, max, (min + max) / 2, 0, 0, (min + max) / 2, min, max, 0);
        Assert.Contains("判定しない", Analyzer.Separation(S(1, 2, 1), S(5, 6, 1)));
        Assert.Equal("範囲は分離", Analyzer.Separation(S(1, 2, 3), S(5, 6, 3)));
        Assert.Contains("重なり", Analyzer.Separation(S(1, 5, 3), S(4, 6, 3)));
        Assert.Equal("+5%", Analyzer.Change(0.05));
        Assert.Equal("35%", Analyzer.Ratio(0.35));
        Assert.Equal("4.5%", Analyzer.Ratio(0.045));
        Assert.Equal("0.45%", Analyzer.Ratio(0.0045));
        Assert.Equal("0%", Analyzer.Ratio(0));
        Assert.Equal("7.99〜8.01", Analyzer.Range(7.99, 8.01));
        Assert.Equal("16", Analyzer.Range(16.0, 16.0));
    }

    [Fact]
    public void ConditionKeysSortNumbersByValue()
    {
        var keys = new List<string> { "workers=16;x=a", "workers=2;x=a", "workers=4;x=a", "workers=1;x=b", "workers=1;x=a" };
        keys.Sort(Analyzer.KeyOrder);
        Assert.Equal(new[] { "workers=1;x=a", "workers=1;x=b", "workers=2;x=a", "workers=4;x=a", "workers=16;x=a" }, keys);
    }

    private static Analyzer.Cond Cond(string scenario, Dictionary<string, string> p, Dictionary<string, double> m) => new()
    {
        Scenario = scenario, Key = string.Join(";", p.Select(x => x.Key + "=" + x.Value)), P = p, Trials = new List<TrialResult>(),
        M = m.ToDictionary(x => x.Key, x => new MetricSummary(3, x.Value, x.Value, x.Value, 0, 0, x.Value, x.Value, x.Value, 0)),
    };

    [Fact]
    public void BottleneckNamesWhatTheCountersShow()
    {
        var p4 = new Dictionary<string, string> { ["cache"] = "warm", ["variant"] = "saturated", ["workers"] = "4" };
        var idle = Cond("audit", p4, new() { ["worker_busy_pct"] = 25, ["mod_cpu_parallelism"] = 0.7, ["run_queue_wait_pct"] = 5, ["lock_contentions_per_s"] = 1400 });
        // A high contention count with near-linear scaling is not a bottleneck.
        Assert.Equal("ワーカーが待機中（供給側律速）", Analyzer.Bottleneck(idle, new[] { idle }, 4, efficiency: 0.98));
        Assert.Contains("ロック競合", Analyzer.Bottleneck(idle, new[] { idle }, 4, efficiency: 0.6));
        // Workers busy, CPU below their count and the run queue full: they wait for a CPU.
        var queued = Cond("scan", new() { ["cache"] = "warm", ["workers"] = "4" }, new() { ["worker_busy_pct"] = 78, ["mod_cpu_parallelism"] = 2.58, ["run_queue_wait_pct"] = 50 });
        Assert.Contains("CPU 競合", Analyzer.Bottleneck(queued, new[] { queued }, 4));
        // Busy but mostly off the CPU, with an empty run queue, on a cold cache: I/O.
        var io = Cond("scan", new() { ["cache"] = "cold", ["workers"] = "1" }, new() { ["worker_busy_pct"] = 94, ["mod_cpu_parallelism"] = 0.61, ["run_queue_wait_pct"] = 0.2 });
        Assert.Equal("I/O 待ち律速", Analyzer.Bottleneck(io, new[] { io }, 4));
        var cpu = Cond("scan", new() { ["cache"] = "warm", ["workers"] = "2" }, new() { ["worker_busy_pct"] = 90, ["mod_cpu_parallelism"] = 1.7, ["run_queue_wait_pct"] = 1 });
        Assert.Equal("CPU 律速", Analyzer.Bottleneck(cpu, new[] { cpu }, 4));
        // A pass waiting for one dominant file read by a single job.
        var pack = Cond("prefetch", new() { ["cache"] = "warm", ["variant"] = "saturated", ["workers"] = "2" },
            new() { ["worker_busy_pct"] = 58, ["mod_cpu_parallelism"] = 1.1, ["run_queue_wait_pct"] = 0, ["largest_file_share"] = 0.96, ["largest_file_mib"] = 338.8 });
        Assert.StartsWith("最大のファイル（338.8 MiB", Analyzer.Bottleneck(pack, new[] { pack }, 4));
    }

    [Fact]
    public void SaturatedPumpingIsNotHeldToOnePumpPerMillisecond()
    {
        var dataset = new Data.Dataset
        {
            Kind = "test", DataRoots = Array.Empty<string>(), Mods = Array.Empty<Data.ModRoot>(), LoadFolders = Array.Empty<string>(),
            DiscoveredFiles = Array.Empty<string>(), Files = Array.Empty<string>(), Packs = Array.Empty<string>(), FileBytes = 0, PackBytes = 0,
            ByExtension = new Dictionary<string, (int, long)>(), ShapeFingerprint = "",
        };
        var env = new RunEnvironment { Options = new BenchOptions(), Dataset = dataset, ScratchRoot = Path.GetTempPath(), LogicalProcessors = Environment.ProcessorCount };
        var ctx = new TrialContext(Condition.Of("test"), env, Path.GetTempPath(), CancellationToken.None) { Timeout = TimeSpan.FromSeconds(30) };
        using var pool = new MoreUseResources.Core.WorkerPool(1, "MUR-Test");
        pool.Configure(1, jobsAllowed: true, synthetic: false, dutyPercent: 50, ThreadPriority.Normal);
        const int jobs = 3000;
        int inFlight = 0, queued = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Like the mod's components: at most 2 jobs in flight per worker, topped up on each pump.
        Scenarios.Common.DriveResult r = Scenarios.Common.Drive(ctx, pool, 0, () =>
        {
            while (Volatile.Read(ref inFlight) < 2 && queued < jobs)
            {
                Interlocked.Increment(ref inFlight);
                queued++;
                pool.Enqueue(_ => Interlocked.Decrement(ref inFlight));
            }
        }, () => queued == jobs && Volatile.Read(ref inFlight) == 0 && pool.PendingJobs == 0);
        Assert.True(r.Done);
        Assert.Equal(jobs, pool.JobsCompleted - r.PumpJobs);
        // The pool's own queue re-ran the pump: no timer, no spinning thread.
        Assert.True(r.PumpJobs >= jobs / 2 - 1, "pump jobs " + r.PumpJobs);
        // One pump per millisecond with 2 in flight would need at least 1.5 s for 3000 jobs.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.2), "took " + clock.Elapsed);
    }

    [Fact]
    public void ModThreadCpuIsUnknownWhenTheThreadsAreGone()
    {
        ThreadCounters Mod(long run) => new(run, 0, 0, 0, 0, 0, Mod: true);
        ThreadCounters Other(long run) => new(run, 0, 0, 0, 0, 0);
        // Started in between: counts from zero.
        Assert.Equal(30L, ResourceDelta.ModRunNs(new Dictionary<int, ThreadCounters> { [1] = Other(5) },
            new Dictionary<int, ThreadCounters> { [1] = Other(9), [2] = Mod(30) }));
        // A mod thread of the first snapshot exited: its time is lost.
        Assert.Null(ResourceDelta.ModRunNs(new Dictionary<int, ThreadCounters> { [2] = Mod(10) }, new Dictionary<int, ThreadCounters> { [1] = Other(9) }));
        // No mod thread at the end: threads that started and stopped in between are invisible, so not 0.
        Assert.Null(ResourceDelta.ModRunNs(new Dictionary<int, ThreadCounters> { [1] = Other(5) }, new Dictionary<int, ThreadCounters> { [1] = Other(9) }));
        Assert.Equal(15L, ResourceDelta.ModRunNs(new Dictionary<int, ThreadCounters> { [2] = Mod(10) }, new Dictionary<int, ThreadCounters> { [2] = Mod(25) }));
    }
}
