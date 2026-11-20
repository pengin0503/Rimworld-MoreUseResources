using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Measurement;

/// <summary>Per-thread scheduler and I/O delay counters, keyed by thread id. <paramref name="Mod"/>: the thread is named MUR-*.</summary>
public readonly record struct ThreadCounters(long RunNs, long WaitNs, long BlockIoTicks, long VoluntarySwitches,
    long InvoluntarySwitches, int Nice, bool Mod = false);

/// <summary>
/// One point-in-time reading of this process and the machine. Fields that the platform does not
/// provide stay null; deltas computed from them are omitted rather than reported as zero.
/// </summary>
public sealed class ResourceSnapshot
{
    public long Timestamp { get; init; }
    public DateTime Utc { get; init; }
    public double CpuUserMs { get; init; }
    public double CpuSystemMs { get; init; }
    public long? RssBytes { get; init; }
    public long? RssAnonBytes { get; init; }
    public long? RssFileBytes { get; init; }
    public long? RssShmemBytes { get; init; }
    public long? PeakRssBytes { get; init; }
    public long? VmDataBytes { get; init; }
    public int Threads { get; init; }
    public int? OpenFiles { get; init; }
    public IoCounters? Io { get; init; }
    public IReadOnlyDictionary<int, ThreadCounters>? ThreadStats { get; init; }
    public SystemCpuTicks? SystemCpu { get; init; }
    public PressureTotals? CpuPressure { get; init; }
    public PressureTotals? IoPressure { get; init; }
    public PressureTotals? MemoryPressure { get; init; }
    public long? MemAvailableBytes { get; init; }
    public long? PageCacheBytes { get; init; }
    public GcCounters Gc { get; init; }
    public long AllocatedBytesPrecise { get; init; }
    public long GcHeapSizeBytes { get; init; }
    public long GcFragmentedBytes { get; init; }
    public long GcPromotedBytes { get; init; }
    public long LockContentions { get; init; }
    public long JitMethods { get; init; }
    public double JitMs { get; init; }

    public double CpuTotalMs => CpuUserMs + CpuSystemMs;
}

/// <summary>Reads <see cref="ResourceSnapshot"/>s. Linux counters come from procfs; other platforms get the portable subset.</summary>
public static class ProcessProbe
{
    private static readonly Lazy<long> ClockTicks = new(() =>
    {
        try { long v = sysconf(2 /* _SC_CLK_TCK */); return v > 0 ? v : 100; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return 100; }
    });

    [DllImport("libc")]
    private static extern long sysconf(int name);

    public static long ClockTicksPerSecond => OperatingSystem.IsLinux() ? ClockTicks.Value : 100;

    /// <summary>true when the kernel accounts per-task block I/O delay (<c>kernel.task_delayacct</c>).</summary>
    public static bool BlockIoDelayAccounting =>
        OperatingSystem.IsLinux() && ProcFs.TryRead("/proc/sys/kernel/task_delayacct")?.Trim() == "1";

    /// <summary>Resets the kernel's peak RSS of this process (Linux 4.0+). Returns false when unsupported.</summary>
    public static bool ResetPeakRss()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            File.WriteAllText("/proc/self/clear_refs", "5");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static ResourceSnapshot Capture(bool includeThreads = true)
    {
        using Process self = Process.GetCurrentProcess();
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        Dictionary<string, long>? status = null;
        IoCounters? io = null;
        SystemCpuTicks? cpu = null;
        PressureTotals? psiCpu = null, psiIo = null, psiMem = null;
        long? memAvailable = null, cached = null;
        int? fds = null;
        IReadOnlyDictionary<int, ThreadCounters>? threads = null;
        if (OperatingSystem.IsLinux())
        {
            string? text = ProcFs.TryRead("/proc/self/status");
            if (text != null) status = ProcFs.ParseStatus(text);
            string? ioText = ProcFs.TryRead("/proc/self/io");
            if (ioText != null) io = ProcFs.ParseIo(ioText);
            string? stat = ProcFs.TryRead("/proc/stat");
            if (stat != null) cpu = ProcFs.ParseSystemCpu(stat);
            psiCpu = ReadPressure("cpu");
            psiIo = ReadPressure("io");
            psiMem = ReadPressure("memory");
            string? meminfo = ProcFs.TryRead("/proc/meminfo");
            if (meminfo != null)
            {
                var m = ProcFs.ParseStatus(meminfo);
                if (m.TryGetValue("MemAvailable", out long a)) memAvailable = a;
                if (m.TryGetValue("Cached", out long c)) cached = c;
            }
            fds = CountEntries("/proc/self/fd");
            if (includeThreads) threads = ReadThreads();
        }
        long? S(string key) => status != null && status.TryGetValue(key, out long v) ? v : null;
        return new ResourceSnapshot
        {
            Timestamp = Stopwatch.GetTimestamp(),
            Utc = DateTime.UtcNow,
            CpuUserMs = self.UserProcessorTime.TotalMilliseconds,
            CpuSystemMs = self.PrivilegedProcessorTime.TotalMilliseconds,
            RssBytes = S("VmRSS") ?? self.WorkingSet64,
            RssAnonBytes = S("RssAnon"),
            RssFileBytes = S("RssFile"),
            RssShmemBytes = S("RssShmem"),
            PeakRssBytes = S("VmHWM") ?? self.PeakWorkingSet64,
            VmDataBytes = S("VmData"),
            Threads = (int)(S("Threads") ?? self.Threads.Count),
            OpenFiles = fds,
            Io = io,
            ThreadStats = threads,
            SystemCpu = cpu,
            CpuPressure = psiCpu,
            IoPressure = psiIo,
            MemoryPressure = psiMem,
            MemAvailableBytes = memAvailable,
            PageCacheBytes = cached,
            Gc = GcCounters.ReadProcess(),
            AllocatedBytesPrecise = GC.GetTotalAllocatedBytes(precise: true),
            GcHeapSizeBytes = info.HeapSizeBytes,
            GcFragmentedBytes = info.FragmentedBytes,
            GcPromotedBytes = info.PromotedBytes,
            LockContentions = Monitor.LockContentionCount,
            JitMethods = System.Runtime.JitInfo.GetCompiledMethodCount(),
            JitMs = System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds,
        };
    }

    private static PressureTotals? ReadPressure(string kind)
    {
        string? text = ProcFs.TryRead("/proc/pressure/" + kind);
        return text == null ? null : ProcFs.ParsePressure(text);
    }

    private static int? CountEntries(string directory)
    {
        try
        {
            int n = 0;
            foreach (string _ in Directory.EnumerateFileSystemEntries(directory)) n++;
            return n;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Thread ids of this process (Linux).</summary>
    public static List<int> ThreadIds()
    {
        var ids = new List<int>();
        try
        {
            foreach (string dir in Directory.EnumerateDirectories("/proc/self/task"))
                if (int.TryParse(Path.GetFileName(dir), out int tid)) ids.Add(tid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return ids;
    }

    /// <summary>
    /// Open descriptors of this process and what they point to (Linux; empty elsewhere). Targets are
    /// paths for files, or kernel names such as "pipe:[123]" and "anon_inode:[eventpoll]".
    /// </summary>
    public static Dictionary<int, string> OpenDescriptors()
    {
        var result = new Dictionary<int, string>();
        if (!OperatingSystem.IsLinux()) return result;
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
            {
                if (!int.TryParse(Path.GetFileName(entry), out int fd)) continue;
                try { result[fd] = new FileInfo(entry).LinkTarget ?? "?"; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // closed while listing
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    private static readonly string RuntimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();

    /// <summary>
    /// A descriptor for a file the code under test opened, as opposed to what the runtime keeps open:
    /// pipes, sockets, events, devices and assemblies or native libraries it loaded on first use.
    /// </summary>
    public static bool IsFileDescriptor(string target) =>
        target.StartsWith('/') && !target.StartsWith("/dev/", StringComparison.Ordinal) && !target.StartsWith("/proc/", StringComparison.Ordinal)
        && !target.StartsWith(RuntimeDirectory, StringComparison.Ordinal) && !target.StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal)
        && !target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !target.Contains(".so", StringComparison.Ordinal);

    /// <summary>Scheduler counters of this process's threads (Linux), or null elsewhere.</summary>
    public static IReadOnlyDictionary<int, ThreadCounters>? Threads() => OperatingSystem.IsLinux() ? ReadThreads() : null;

    /// <summary>Thread name (comm) of a task, or null.</summary>
    public static string? ThreadName(int tid) => ProcFs.TryRead("/proc/self/task/" + tid + "/comm")?.Trim();

    private static Dictionary<int, ThreadCounters> ReadThreads()
    {
        var result = new Dictionary<int, ThreadCounters>();
        foreach (int tid in ThreadIds())
        {
            string root = "/proc/self/task/" + tid + "/";
            string? statText = ProcFs.TryRead(root + "stat");
            if (statText == null) continue; // the thread exited while we were reading
            TaskStat? stat = ProcFs.ParseTaskStat(statText);
            SchedStat? sched = ProcFs.TryRead(root + "schedstat") is { } s ? ProcFs.ParseSchedStat(s) : null;
            long voluntary = 0, involuntary = 0;
            if (ProcFs.TryRead(root + "status") is { } st)
            {
                var map = ProcFs.ParseStatus(st);
                map.TryGetValue("voluntary_ctxt_switches", out voluntary);
                map.TryGetValue("nonvoluntary_ctxt_switches", out involuntary);
            }
            bool mod = ProcFs.TryRead(root + "comm")?.StartsWith("MUR-", StringComparison.Ordinal) == true;
            result[tid] = new ThreadCounters(sched?.RunNanoseconds ?? 0, sched?.WaitNanoseconds ?? 0,
                stat?.BlockIoDelayTicks ?? 0, voluntary, involuntary, stat?.Nice ?? 0, mod);
        }
        return result;
    }
}
