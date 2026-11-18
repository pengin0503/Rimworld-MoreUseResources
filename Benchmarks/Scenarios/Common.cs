using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>Helpers shared by the scenarios.</summary>
internal static class Common
{
    /// <summary>Thread name prefix of component pools. Counted as mod threads by the leak check.</summary>
    public const string PoolPrefix = "MUR-Bench";

    /// <summary>How the scenario calls the component's Pump.</summary>
    public const string Cadence = "cadence";
    /// <summary>
    /// Pump called again each time the workers have taken every queued job (<see cref="Drive"/>), so the
    /// in-flight limit (workers × 2), not a timer, decides how much work waits for the workers: the
    /// capacity of the work itself. A fixed 1 ms interval is not enough for this: with small files it still
    /// capped the rate at the in-flight limit × about 900 pumps/s. worker_busy_pct shows whether workers
    /// still waited for jobs.
    /// </summary>
    public const string Saturated = "saturated";

    /// <summary>
    /// Polling interval of scenarios that measure behaviour rather than capacity (faults, stop and resume,
    /// RAM retention) and of passes outside the measured window.
    /// </summary>
    public const int FastPumpMs = 1;

    /// <summary>
    /// A worker pool configured like the controller configures it for file work: jobs allowed, no
    /// synthetic load, the default Lowest priority (a no-op for unprivileged Linux processes).
    /// </summary>
    public static WorkerPool Pool(int workers)
    {
        var pool = new WorkerPool(workers, PoolPrefix);
        pool.Configure(workers, jobsAllowed: true, synthetic: false, dutyPercent: 50, System.Threading.ThreadPriority.Lowest);
        return pool;
    }

    /// <summary>Pump interval of a condition in ms: the controller's for cadence, 0 (saturated) for <see cref="Drive"/>'s pump job.</summary>
    public static int PumpInterval(Condition c, BenchOptions o) => c.Get("variant") == Saturated ? 0 : o.PumpIntervalMs;

    /// <summary>How a component was driven: finished before the timeout, pump calls, pump jobs run by the pool.</summary>
    public readonly record struct DriveResult(bool Done, long Pumps, long PumpJobs);

    /// <summary>
    /// Calls <paramref name="pump"/> every <paramref name="intervalMs"/> ms until <paramref name="done"/> is true.
    /// With 0 (saturated) a pump job queued in <paramref name="pool"/> behind the component's jobs calls it
    /// and queues itself again: the queue is first in, first out, so the job runs as soon as the workers
    /// have taken every job before it, and the in-flight limit, not a timer, decides how much work waits.
    /// No extra thread spins (a spinning driver took one CPU from the workers once they filled the vCPUs).
    /// The chain stops when a pump leaves nothing queued; the calling thread then checks every millisecond
    /// and pumps itself, restarting the chain if that queued work. Pump calls never overlap.
    /// </summary>
    public static DriveResult Drive(TrialContext ctx, WorkerPool pool, int intervalMs, Action pump, Func<bool> done, TimeSpan? limit = null)
    {
        long pumps = 0, pumpJobs = 0;
        if (intervalMs > 0)
        {
            bool ok = ctx.WaitUntil(() =>
            {
                pumps++;
                pump();
                return done();
            }, intervalMs, limit: limit);
            return new DriveResult(ok, pumps, 0);
        }
        var gate = new object();
        int chained = 0; // 1 while a pump job is queued or running
        bool stop = false;
        var chainEnded = new ManualResetEventSlim(false); // never disposed: a pump job may still set it
        void Step()
        {
            lock (gate)
            {
                pumps++;
                pump();
            }
        }
        void Arm()
        {
            if (pool.PendingJobs > 0 && Interlocked.CompareExchange(ref chained, 1, 0) == 0) pool.Enqueue(PumpJob);
        }
        void PumpJob(CancellationToken token)
        {
            Interlocked.Increment(ref pumpJobs);
            bool again = false;
            try
            {
                if (!Volatile.Read(ref stop) && !token.IsCancellationRequested)
                {
                    Step();
                    again = pool.PendingJobs > 0 && !Volatile.Read(ref stop);
                }
            }
            finally
            {
                // A failed pump ends the chain; the calling thread takes over.
                if (again) pool.Enqueue(PumpJob);
                else
                {
                    chainEnded.Set();
                    Volatile.Write(ref chained, 0);
                }
            }
        }
        bool finished;
        try
        {
            var clock = Stopwatch.StartNew();
            TimeSpan max = limit ?? (ctx.Timeout - ctx.Elapsed);
            long closeUntil = 0;
            Step();
            Arm();
            while (true)
            {
                ctx.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref chained) == 0)
                {
                    Step();
                    Arm();
                }
                if (done())
                {
                    finished = true;
                    break;
                }
                if (clock.Elapsed >= max)
                {
                    ctx.Truncated = true;
                    finished = false;
                    break;
                }
                if (Volatile.Read(ref chained) != 0)
                {
                    // Woken when the chain ends, which is when only the last jobs are left.
                    closeUntil = 0;
                    chainEnded.Wait(FastPumpMs);
                    chainEnded.Reset();
                    continue;
                }
                // The last jobs are running: check closely for 2 ms so the end of a short pass is not
                // rounded up to the next millisecond, then every millisecond.
                long now = Stopwatch.GetTimestamp();
                if (closeUntil == 0) closeUntil = now + Stopwatch.Frequency / 500;
                if (now < closeUntil) Thread.Yield();
                else Thread.Sleep(FastPumpMs);
            }
        }
        finally
        {
            // No pump may run after this returns (the caller suspends the component next).
            Volatile.Write(ref stop, true);
            var wait = Stopwatch.StartNew();
            while (Volatile.Read(ref chained) != 0 && wait.ElapsedMilliseconds < 2000) Thread.Sleep(1);
        }
        return new DriveResult(finished, Interlocked.Read(ref pumps), Interlocked.Read(ref pumpJobs));
    }

    /// <summary>Records how a measured pass was driven.</summary>
    public static void DriveMetrics(TrialContext ctx, DriveResult r, int intervalMs, TimeSpan elapsed)
    {
        ctx.Metric("pump_interval_ms", intervalMs);
        ctx.Metric("pumps_per_s", r.Pumps / Math.Max(elapsed.TotalSeconds, 1e-9));
        ctx.Metric("pump_jobs", r.PumpJobs);
    }

    /// <summary>Busy share of the configured workers over the measured time, in percent.
    /// <paramref name="pumpJobs"/>: jobs <see cref="Drive"/> queued itself, left out of jobs_completed (their few µs stay in the busy time).</summary>
    public static void WorkerBusy(TrialContext ctx, WorkerPool pool, long busyBefore, int workers, long pumpJobs = 0)
    {
        double busyMs = (pool.BusyTicks - busyBefore) * 1000.0 / Stopwatch.Frequency;
        ctx.Metric("worker_busy_ms", busyMs);
        double wall = ctx.Elapsed.TotalMilliseconds;
        if (wall > 0 && workers > 0) ctx.Metric("worker_busy_pct", 100.0 * busyMs / (wall * workers));
        ctx.Metric("jobs_completed", pool.JobsCompleted - pumpJobs);
        ctx.Metric("jobs_failed", pool.JobsFailed);
        if (pool.LastError != null) ctx.Note("worker error: " + pool.LastError);
    }

    /// <summary>Limits a cadence-bound trial to the rate window (times <paramref name="factor"/>).</summary>
    public static void RateWindow(TrialContext ctx, double factor = 1)
    {
        double s = Math.Min(ctx.Options.TrialTimeoutSeconds, ctx.Options.RateWindowSeconds * factor);
        ctx.Timeout = TimeSpan.FromSeconds(s);
        ctx.Metric("rate_window_s", s);
    }

    /// <summary>Time a full pass would take at the measured rate; meaningful when the trial was cut by its timeout.</summary>
    public static void ProjectPass(TrialContext ctx, long sources, long done)
    {
        double s = ctx.Elapsed.TotalSeconds;
        if (done > 0 && s > 0) ctx.Metric("projected_pass_s", sources / (done / s));
    }

    /// <summary>Marks conditions with more workers than logical processors.</summary>
    public static void NoteOversubscription(TrialContext ctx, int workers)
    {
        int nproc = Environment.ProcessorCount;
        ctx.Metric("oversubscribed", workers > nproc ? 1 : 0);
        if (workers > nproc) ctx.Note("workers (" + workers + ") exceed logical processors (" + nproc + ")");
    }

    /// <summary>Folds numbers into a stable 52-bit value that fits a double exactly; equal results give equal values.</summary>
    public static double ResultHash(params long[] values)
    {
        unchecked
        {
            ulong h = 14695981039346656037UL;
            foreach (long v in values)
            {
                for (int i = 0; i < 8; i++)
                {
                    h ^= (byte)(v >> (8 * i));
                    h *= 1099511628211UL;
                }
            }
            return h & ((1UL << 52) - 1);
        }
    }

    public static double AuditHash(AssetAuditSnapshot s) => ResultHash(s.Sources, s.Completed, s.Duplicates, s.DuplicateBytes,
        s.HashedBytes, s.Errors, s.Skipped, s.XmlFilesChecked, s.XmlParseFailures, s.XmlDefinitions, s.XmlDefinitionCollisions,
        s.XmlFilesSkipped, s.TextureFilesChecked, s.TextureFilesValid, s.TextureFilesMalformed, s.TextureRgba8EquivalentBytes);

    public static void AuditMetrics(TrialContext ctx, AssetAuditSnapshot s, string prefix = "")
    {
        ctx.Metric(prefix + "items", s.Completed);
        ctx.Metric(prefix + "bytes_mib", s.HashedBytes / 1048576.0);
        ctx.Metric(prefix + "duplicates", s.Duplicates);
        ctx.Metric(prefix + "duplicate_mib", s.DuplicateBytes / 1048576.0);
        ctx.Metric(prefix + "errors", s.Errors);
        ctx.Metric(prefix + "skipped", s.Skipped);
        ctx.Metric(prefix + "xml_checked", s.XmlFilesChecked);
        ctx.Metric(prefix + "xml_parse_failures", s.XmlParseFailures);
        ctx.Metric(prefix + "xml_definitions", s.XmlDefinitions);
        ctx.Metric(prefix + "xml_collisions", s.XmlDefinitionCollisions);
        ctx.Metric(prefix + "textures_valid", s.TextureFilesValid);
        ctx.Metric(prefix + "textures_malformed", s.TextureFilesMalformed);
        ctx.Metric(prefix + "texture_rgba8_mib", s.TextureRgba8EquivalentBytes / 1048576.0);
        ctx.Metric(prefix + "result_hash", AuditHash(s));
    }

    /// <summary>Copies files to a scratch directory, keeping relative paths. Returns the copies.</summary>
    public static List<string> CopyFiles(IEnumerable<string> files, Func<string, string> relative, string destination, long maxBytes, CancellationToken token)
    {
        var copies = new List<string>();
        long total = 0;
        foreach (string f in files)
        {
            token.ThrowIfCancellationRequested();
            long len;
            try { len = new FileInfo(f).Length; }
            catch (IOException) { continue; }
            if (total + len > maxBytes) continue;
            string rel = relative(f).Replace('/', Path.DirectorySeparatorChar);
            string target = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
            copies.Add(target);
            total += len;
        }
        return copies;
    }

    /// <summary>
    /// Reads files sequentially on one thread with a 64 KiB buffer, like a loader reading assets one
    /// by one. Returns bytes read. Errors are counted, not thrown.
    /// </summary>
    public static long ConsumeSequential(IReadOnlyList<string> files, CancellationToken token, out int errors)
    {
        errors = 0;
        long total = 0;
        byte[] buffer = new byte[64 * 1024];
        foreach (string f in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0) total += n;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
        }
        return total;
    }

    /// <summary>The worker count the mod chooses by default on this machine (logical processors - 2, at least 1).</summary>
    public static int DefaultWorkers => new ResourceSettings().EffectiveMaxWorkers(Environment.ProcessorCount);

    /// <summary>The largest requested worker count, or the default when none was requested.</summary>
    public static int MaxWorkers(BenchOptions o) => o.Workers.Length == 0 ? DefaultWorkers : o.Workers.Max();

    /// <summary>The requested worker counts that do not exceed the logical processors, or the smallest requested.</summary>
    public static int[] FittingWorkers(BenchOptions o)
    {
        int[] fit = o.Workers.Where(w => w <= Environment.ProcessorCount).ToArray();
        return fit.Length > 0 ? fit : new[] { o.Workers.DefaultIfEmpty(1).Min() };
    }
}

/// <summary>
/// A memory probe that reports the real machine but can be told to report a shortage. Only the
/// mod's view changes: no memory is actually consumed, so this tests the mod's reaction, not the OS.
/// </summary>
internal sealed class ScriptedMemoryProbe : IMemoryProbe
{
    private readonly IMemoryProbe _real;
    private long _forcedAvailable = -1;

    public ScriptedMemoryProbe(IMemoryProbe real) => _real = real;

    public long Reads;

    public void ForceAvailable(long bytes) => Interlocked.Exchange(ref _forcedAvailable, bytes);
    public void Clear() => Interlocked.Exchange(ref _forcedAvailable, -1);
    public bool Forced => Interlocked.Read(ref _forcedAvailable) >= 0;

    public MemoryStatus Read()
    {
        Interlocked.Increment(ref Reads);
        MemoryStatus s = _real.Read();
        long forced = Interlocked.Read(ref _forcedAvailable);
        return forced >= 0 ? new MemoryStatus(s.TotalPhysical, forced, s.FromOperatingSystem) : s;
    }
}

/// <summary>A clock the scenario advances by hand, for the components' rescan and refresh intervals.</summary>
internal sealed class ManualClock
{
    private long _now = 1_000_000;
    public long Now => Interlocked.Read(ref _now);
    public void Advance(long ms) => Interlocked.Add(ref _now, ms);
}
