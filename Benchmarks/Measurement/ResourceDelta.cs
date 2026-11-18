using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MoreUseResources.Benchmarks.Measurement;

/// <summary>
/// Turns two snapshots into named metrics. The suffix of each name is its unit
/// (_ms, _mib, _pct, _per_s or a plain count). Counters the platform did not provide are omitted.
/// </summary>
public static class ResourceDelta
{
    public const double MiB = 1024.0 * 1024.0;

    public static void Add(IDictionary<string, double> metrics, ResourceSnapshot a, ResourceSnapshot b,
        int logicalProcessors, string prefix = "", bool peakRssReset = false)
    {
        void Put(string name, double value)
        {
            if (double.IsFinite(value)) metrics[prefix + name] = value;
        }

        double wallMs = (b.Timestamp - a.Timestamp) * 1000.0 / Stopwatch.Frequency;
        double seconds = Math.Max(wallMs / 1000.0, 1e-9);
        Put("wall_ms", wallMs);

        double cpu = b.CpuTotalMs - a.CpuTotalMs;
        Put("cpu_ms", cpu);
        Put("cpu_user_ms", b.CpuUserMs - a.CpuUserMs);
        Put("cpu_sys_ms", b.CpuSystemMs - a.CpuSystemMs);
        if (wallMs > 0)
        {
            Put("cpu_parallelism", cpu / wallMs);
            Put("cpu_util_pct", 100.0 * cpu / (wallMs * Math.Max(1, logicalProcessors)));
        }

        if (a.RssBytes.HasValue && b.RssBytes.HasValue)
        {
            Put("rss_end_mib", b.RssBytes.Value / MiB);
            Put("rss_delta_mib", (b.RssBytes.Value - a.RssBytes.Value) / MiB);
        }
        if (a.RssAnonBytes.HasValue && b.RssAnonBytes.HasValue)
            Put("rss_anon_delta_mib", (b.RssAnonBytes.Value - a.RssAnonBytes.Value) / MiB);
        if (a.RssFileBytes.HasValue && b.RssFileBytes.HasValue)
            Put("rss_file_delta_mib", (b.RssFileBytes.Value - a.RssFileBytes.Value) / MiB);
        if (peakRssReset && b.PeakRssBytes.HasValue) Put("rss_peak_mib", b.PeakRssBytes.Value / MiB);
        Put("threads_end", b.Threads);
        if (a.OpenFiles.HasValue && b.OpenFiles.HasValue) Put("open_files_delta", b.OpenFiles.Value - a.OpenFiles.Value);

        if (a.Io.HasValue && b.Io.HasValue)
        {
            IoCounters x = a.Io.Value, y = b.Io.Value;
            Put("io_read_mib", (y.ReadChars - x.ReadChars) / MiB);
            Put("io_storage_read_mib", (y.StorageReadBytes - x.StorageReadBytes) / MiB);
            Put("io_read_calls", y.ReadSyscalls - x.ReadSyscalls);
            Put("io_storage_write_mib", (y.StorageWriteBytes - x.StorageWriteBytes) / MiB);
        }

        if (a.ThreadStats != null && b.ThreadStats != null)
        {
            long waitNs = 0, blkio = 0, involuntary = 0, voluntary = 0;
            int exited = 0, modExited = 0;
            foreach (var pair in b.ThreadStats)
            {
                a.ThreadStats.TryGetValue(pair.Key, out ThreadCounters before);
                waitNs += pair.Value.WaitNs - before.WaitNs;
                blkio += pair.Value.BlockIoTicks - before.BlockIoTicks;
                involuntary += pair.Value.InvoluntarySwitches - before.InvoluntarySwitches;
                voluntary += pair.Value.VoluntarySwitches - before.VoluntarySwitches;
            }
            foreach (var pair in a.ThreadStats)
                if (!b.ThreadStats.ContainsKey(pair.Key))
                {
                    exited++;
                    if (pair.Value.Mod) modExited++;
                }
            Put("mod_threads_exited_during", modExited);
            if (ModRunNs(a.ThreadStats, b.ThreadStats) is { } modRunNs)
            {
                Put("mod_cpu_ms", modRunNs / 1e6);
                if (wallMs > 0) Put("mod_cpu_parallelism", modRunNs / 1e6 / wallMs);
            }
            Put("run_queue_wait_ms", waitNs / 1e6);
            Put("run_queue_wait_pct", 100.0 * (waitNs / 1e6) / Math.Max(wallMs, 1e-9));
            Put("context_switches_involuntary", involuntary);
            Put("context_switches_voluntary", voluntary);
            Put("threads_exited_during", exited);
            if (ProcessProbe.BlockIoDelayAccounting)
                Put("io_wait_ms", blkio * 1000.0 / ProcessProbe.ClockTicksPerSecond);
        }

        if (a.SystemCpu.HasValue && b.SystemCpu.HasValue)
        {
            SystemCpuTicks x = a.SystemCpu.Value, y = b.SystemCpu.Value;
            double total = y.Total - x.Total;
            if (total > 0)
            {
                Put("sys_iowait_pct", 100.0 * (y.IoWait - x.IoWait) / total);
                Put("sys_steal_pct", 100.0 * (y.Steal - x.Steal) / total);
                Put("sys_busy_pct", 100.0 * (total - (y.Idle - x.Idle) - (y.IoWait - x.IoWait)) / total);
            }
        }
        if (a.IoPressure.HasValue && b.IoPressure.HasValue)
        {
            Put("psi_io_some_ms", (b.IoPressure.Value.SomeMicros - a.IoPressure.Value.SomeMicros) / 1000.0);
            Put("psi_io_full_ms", (b.IoPressure.Value.FullMicros - a.IoPressure.Value.FullMicros) / 1000.0);
        }
        if (a.CpuPressure.HasValue && b.CpuPressure.HasValue)
            Put("psi_cpu_some_ms", (b.CpuPressure.Value.SomeMicros - a.CpuPressure.Value.SomeMicros) / 1000.0);
        if (a.MemoryPressure.HasValue && b.MemoryPressure.HasValue)
            Put("psi_memory_some_ms", (b.MemoryPressure.Value.SomeMicros - a.MemoryPressure.Value.SomeMicros) / 1000.0);
        if (b.MemAvailableBytes.HasValue) Put("mem_available_end_mib", b.MemAvailableBytes.Value / MiB);
        if (a.PageCacheBytes.HasValue && b.PageCacheBytes.HasValue)
            Put("page_cache_delta_mib", (b.PageCacheBytes.Value - a.PageCacheBytes.Value) / MiB);

        // Collector: CollectionCount(n) includes collections of higher generations, as in GcMonitor.
        int gen2 = Math.Max(0, b.Gc.Gen2 - a.Gc.Gen2);
        int gen1 = Math.Max(0, b.Gc.Gen1 - a.Gc.Gen1 - gen2);
        int gen0 = Math.Max(0, b.Gc.Gen0 - a.Gc.Gen0 - gen1 - gen2);
        Put("gc_gen0", gen0);
        Put("gc_gen1", gen1);
        Put("gc_gen2", gen2);
        double pause = Math.Max(0, b.Gc.TotalPauseMs - a.Gc.TotalPauseMs);
        Put("gc_pause_ms", pause);
        Put("gc_pause_pct", 100.0 * pause / Math.Max(wallMs, 1e-9));
        double allocated = Math.Max(0, b.AllocatedBytesPrecise - a.AllocatedBytesPrecise) / MiB;
        Put("alloc_mib", allocated);
        Put("alloc_mib_per_s", allocated / seconds);
        Put("gc_heap_end_mib", b.GcHeapSizeBytes / MiB);
        Put("gc_committed_end_mib", b.Gc.CommittedBytes / MiB);
        Put("gc_fragmented_end_mib", b.GcFragmentedBytes / MiB);

        long contentions = b.LockContentions - a.LockContentions;
        Put("lock_contentions", contentions);
        Put("lock_contentions_per_s", contentions / seconds);
        Put("jit_methods", b.JitMethods - a.JitMethods);
        Put("jit_ms", b.JitMs - a.JitMs);
    }

    /// <summary>
    /// CPU time of the mod's own threads (MUR-*) between two thread snapshots in ns, or null when it cannot
    /// be known: a mod thread of the first snapshot exited (its counters left with it), or no mod thread is
    /// alive in the second (threads that started and ended in between are invisible, so 0 would be wrong).
    /// Threads started in between count from zero.
    /// </summary>
    public static long? ModRunNs(IReadOnlyDictionary<int, ThreadCounters> a, IReadOnlyDictionary<int, ThreadCounters> b)
    {
        foreach (var pair in a)
            if (pair.Value.Mod && !b.ContainsKey(pair.Key)) return null;
        long run = 0;
        bool any = false;
        foreach (var pair in b)
        {
            if (!pair.Value.Mod) continue;
            any = true;
            a.TryGetValue(pair.Key, out ThreadCounters before);
            long d = pair.Value.RunNs - before.RunNs;
            run += d >= 0 ? d : pair.Value.RunNs; // a reused thread id: a new thread
        }
        return any ? run : null;
    }
}
