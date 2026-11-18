using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MoreUseResources.Benchmarks.Measurement;

namespace MoreUseResources.Benchmarks.Running;

/// <summary>Readings the guard compares with its limits. Replaceable in tests.</summary>
public interface ISafetyProbe
{
    long ProcessRssBytes();
    long? AvailableMemoryBytes();
    long? FreeDiskBytes(string path);
    long DirectorySizeBytes(string path);
}

public sealed class SystemSafetyProbe : ISafetyProbe
{
    public long ProcessRssBytes()
    {
        if (ProcFs.TryRead("/proc/self/status") is { } s && ProcFs.ParseStatus(s).TryGetValue("VmRSS", out long rss)) return rss;
        return Environment.WorkingSet;
    }

    public long? AvailableMemoryBytes() =>
        ProcFs.TryRead("/proc/meminfo") is { } m && ProcFs.ParseStatus(m).TryGetValue("MemAvailable", out long a) ? a : null;

    public long? FreeDiskBytes(string path)
    {
        try { return new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public long DirectorySizeBytes(string path)
    {
        long total = 0;
        try
        {
            foreach (string f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                try { total += new FileInfo(f).Length; }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }
}

/// <summary>
/// Stops the run before it can hurt the machine: process RSS above the limit, the machine's free
/// memory below the floor, the scratch area above its size, the disk nearly full, or the total run
/// time exceeded. A stop cancels the run token; the runner then keeps everything measured so far.
/// </summary>
public sealed class SafetyGuard : IDisposable
{
    private readonly BenchOptions _options;
    private readonly ISafetyProbe _probe;
    private readonly string _outDir;
    private readonly string _scratchDir;
    private readonly CancellationTokenSource _cts;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _intervalMs;
    private Thread? _thread;
    private volatile bool _stop;
    private int _diskCheckCountdown;

    public SafetyGuard(BenchOptions options, string outDir, string scratchDir, CancellationTokenSource cts,
        ISafetyProbe? probe = null, int intervalMs = 500)
    {
        _options = options;
        _outDir = outDir;
        _scratchDir = scratchDir;
        _cts = cts;
        _probe = probe ?? new SystemSafetyProbe();
        _intervalMs = intervalMs;
    }

    /// <summary>Why the guard stopped the run, or null.</summary>
    public string? StopReason { get; private set; }

    public void Start()
    {
        _thread = new Thread(() =>
        {
            while (!_stop && !_cts.IsCancellationRequested)
            {
                CheckOnce();
                Thread.Sleep(_intervalMs);
            }
        }) { IsBackground = true, Name = "Bench-Safety" };
        _thread.Start();
    }

    /// <summary>Runs all checks once. Returns the reason when a limit was crossed.</summary>
    public string? CheckOnce()
    {
        string? reason = null;
        long rss = _probe.ProcessRssBytes();
        if (rss > _options.MaxRssMiB * 1048576L)
            reason = "process RSS " + rss / 1048576 + " MiB exceeded --max-rss-mib " + _options.MaxRssMiB;
        long? available = _probe.AvailableMemoryBytes();
        if (reason == null && available.HasValue && available.Value < _options.MinFreeMemoryMiB * 1048576L)
            reason = "machine free memory " + available.Value / 1048576 + " MiB fell below --min-free-mib " + _options.MinFreeMemoryMiB;
        if (reason == null && _clock.Elapsed.TotalMinutes > _options.MaxRuntimeMinutes)
            reason = "run time exceeded --max-runtime-min " + _options.MaxRuntimeMinutes;
        if (reason == null && --_diskCheckCountdown <= 0)
        {
            _diskCheckCountdown = 10; // directory sizes are more expensive: every ~5 s
            long? free = _probe.FreeDiskBytes(_outDir);
            if (free.HasValue && free.Value < _options.MinFreeDiskMiB * 1048576L)
                reason = "free disk " + free.Value / 1048576 + " MiB fell below --min-free-disk-mib " + _options.MinFreeDiskMiB;
            else if (Directory.Exists(_scratchDir) && _probe.DirectorySizeBytes(_scratchDir) > _options.MaxDiskMiB * 1048576L)
                reason = "scratch data exceeded --max-disk-mib " + _options.MaxDiskMiB;
        }
        if (reason != null && StopReason == null)
        {
            StopReason = reason;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        return reason;
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(2000);
    }
}
