using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MoreUseResources.Benchmarks.Measurement;

/// <summary>Bytes the process moved through the kernel (from <c>/proc/self/io</c>).</summary>
public readonly record struct IoCounters(long ReadChars, long WriteChars, long ReadSyscalls, long WriteSyscalls,
    long StorageReadBytes, long StorageWriteBytes, long CancelledWriteBytes);

/// <summary>Cumulative system CPU time in clock ticks (first line of <c>/proc/stat</c>).</summary>
public readonly record struct SystemCpuTicks(long User, long Nice, long System, long Idle, long IoWait,
    long Irq, long SoftIrq, long Steal)
{
    public long Total => User + Nice + System + Idle + IoWait + Irq + SoftIrq + Steal;
}

/// <summary>Cumulative pressure-stall time in microseconds (<c>/proc/pressure/*</c>).</summary>
public readonly record struct PressureTotals(long SomeMicros, long FullMicros);

/// <summary>Fields of one task's <c>stat</c> line that the benchmark uses.</summary>
public readonly record struct TaskStat(long UserTicks, long SystemTicks, int Priority, int Nice, long BlockIoDelayTicks);

/// <summary>Scheduler statistics of one task: time on CPU and time waiting on a run queue.</summary>
public readonly record struct SchedStat(long RunNanoseconds, long WaitNanoseconds, long Timeslices);

/// <summary>
/// Pure parsers for Linux procfs text. They take the file content so they can be tested with fixed
/// strings; reading is done by <see cref="ProcessProbe"/>. Unknown or missing fields are reported as
/// absent instead of zero so that unavailable counters are never mistaken for measured zeros.
/// </summary>
public static class ProcFs
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary><c>/proc/[pid]/status</c>: values with a "kB" unit are returned in bytes.</summary>
    public static Dictionary<string, long> ParseStatus(string text)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            string key = raw.Substring(0, colon);
            string rest = raw.Substring(colon + 1).Trim();
            if (rest.Length == 0) continue;
            int space = rest.IndexOf(' ');
            string number = space > 0 ? rest.Substring(0, space) : rest;
            if (!long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)) continue;
            bool kib = rest.EndsWith("kB", StringComparison.Ordinal);
            result[key] = kib ? value * 1024L : value;
        }
        return result;
    }

    public static IoCounters? ParseIo(string text)
    {
        var map = ParseStatus(text);
        if (!map.TryGetValue("rchar", out long rchar) || !map.TryGetValue("read_bytes", out long readBytes)) return null;
        return new IoCounters(rchar, Get(map, "wchar"), Get(map, "syscr"), Get(map, "syscw"), readBytes,
            Get(map, "write_bytes"), Get(map, "cancelled_write_bytes"));
    }

    public static SystemCpuTicks? ParseSystemCpu(string procStat)
    {
        foreach (string line in procStat.Split('\n'))
        {
            if (!line.StartsWith("cpu ", StringComparison.Ordinal)) continue;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 9) return null;
            long F(int i) => long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
            return new SystemCpuTicks(F(1), F(2), F(3), F(4), F(5), F(6), F(7), F(8));
        }
        return null;
    }

    public static PressureTotals? ParsePressure(string text)
    {
        long? some = null, full = null;
        foreach (string line in text.Split('\n'))
        {
            int at = line.IndexOf("total=", StringComparison.Ordinal);
            if (at < 0) continue;
            string number = line.Substring(at + 6).Trim();
            if (!long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)) continue;
            if (line.StartsWith("some", StringComparison.Ordinal)) some = value;
            else if (line.StartsWith("full", StringComparison.Ordinal)) full = value;
        }
        return some.HasValue ? new PressureTotals(some.Value, full ?? 0) : null;
    }

    /// <summary>
    /// <c>/proc/[pid]/task/[tid]/stat</c>. The command name is in parentheses and may contain spaces
    /// or parentheses itself, so fields are counted from the last ')'.
    /// </summary>
    public static TaskStat? ParseTaskStat(string text)
    {
        int close = text.LastIndexOf(')');
        if (close < 0) return null;
        string[] f = text.Substring(close + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // f[0] is field 3 (state); field n is f[n - 3].
        if (f.Length < 17) return null;
        long L(int field) => field - 3 < f.Length && long.TryParse(f[field - 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
        long blkio = f.Length >= 40 ? L(42) : 0;
        return new TaskStat(L(14), L(15), (int)L(18), (int)L(19), blkio);
    }

    public static SchedStat? ParseSchedStat(string text)
    {
        string[] parts = text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long run)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long wait)
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long slices)) return null;
        return new SchedStat(run, wait, slices);
    }

    /// <summary>Reads a small procfs or sysfs file; null when it does not exist or cannot be read.</summary>
    public static string? TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    private static long Get(Dictionary<string, long> map, string key) => map.TryGetValue(key, out long v) ? v : 0;
}
