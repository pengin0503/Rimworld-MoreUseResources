using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace MoreUseResources.Core;

/// <summary>CPU time one OS thread of this process has used since it started.</summary>
/// <param name="Id">The OS thread id.</param>
/// <param name="Started">When the thread started, in the platform's units. With <paramref name="Id"/> it names one thread even after the id is reused.</param>
/// <param name="CpuMs">CPU time used since the thread started.</param>
/// <param name="Group">"main" for the thread named as main when reading, otherwise the thread's name without its trailing number.</param>
public readonly record struct ThreadTime(long Id, long Started, double CpuMs, string Group);

/// <summary>Every thread of this process, and the hard page faults of the process, at one moment.</summary>
/// <param name="HardFaults">Page faults the process waited on storage for since it started (memory-mapped files, paged-out memory), or null when unknown.</param>
public sealed record ThreadSnapshot(IReadOnlyList<ThreadTime> Threads, long? HardFaults);

/// <summary>CPU time of one group of threads, and how many threads the group had.</summary>
public readonly record struct GroupCpu(double CpuMs, int Threads);

/// <summary>
/// Reads the CPU time of every thread of this process, grouped by thread name, so it is known which
/// threads are busy (the main thread, the render thread, the engine's job workers, the runtime, this mod)
/// and which have room. Reading changes nothing in the game.
/// Windows: one NtQuerySystemInformation call for times and hard faults, and the thread description for
/// names (asked once per thread). Linux: /proc/self/task. Other platforms read nothing.
/// Any thread may read; reads take turns. The Windows call lists every process of the machine and takes a few
/// milliseconds on a desktop, so a caller that runs every frame reads on a background thread and names the main thread.
/// </summary>
public sealed class ThreadSampler : IDisposable
{
    public const string MainGroup = "main";
    public const string UnnamedGroup = "(unnamed)";
    private const int MaxGroupNameChars = 40;

    private readonly object _gate = new();
    private Dictionary<(long Id, long Started), string> _names = new();
    private IntPtr _buffer;
    private int _bufferBytes;
    private bool _namesUnavailable;
    private bool _disposed;
    private readonly Func<long, string> _windowsName;

    public ThreadSampler() => _windowsName = WindowsName;

    /// <summary>A snapshot that reports the calling thread as "main", or null when the platform gives none or the read failed.</summary>
    public ThreadSnapshot? Read() => Read(CurrentThreadId());

    /// <summary>A snapshot that reports the thread with OS id <paramref name="mainThreadId"/> as "main" (see <see cref="CurrentThreadId"/>).</summary>
    public ThreadSnapshot? Read(long mainThreadId)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            try
            {
                if (OperatingSystem.IsWindows()) return ReadWindows(mainThreadId);
                if (OperatingSystem.IsLinux()) return ReadLinux(mainThreadId);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException
                or UnauthorizedAccessException or FormatException or OverflowException or OutOfMemoryException)
            {
            }
            return null;
        }
    }

    /// <summary>The OS id of the calling thread, as the snapshots name threads, or -1 where it is unknown.</summary>
    public static long CurrentThreadId()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return GetCurrentThreadId();
            if (OperatingSystem.IsLinux()) return LinuxCurrentThreadId();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
        return -1;
    }

    /// <summary>"WaylithJob 3" and "WaylithJob 12" are one group "WaylithJob"; a thread without a name is "(unnamed)".</summary>
    public static string GroupOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return UnnamedGroup;
        string trimmed = name.Trim();
        int end = trimmed.Length;
        while (end > 0 && (char.IsAsciiDigit(trimmed[end - 1]) || trimmed[end - 1] is ' ' or '-' or '_' or '#')) end--;
        string group = end == 0 ? trimmed : trimmed[..end];
        return group.Length > MaxGroupNameChars ? group[..MaxGroupNameChars] : group;
    }

    /// <summary>
    /// CPU time per group between two snapshots. A thread that started after <paramref name="start"/> counts all
    /// of its time. A thread that ended before <paramref name="end"/> is missing from both sums, so a group's
    /// value is at most the true one.
    /// </summary>
    public static Dictionary<string, GroupCpu> Delta(ThreadSnapshot start, ThreadSnapshot end)
    {
        var before = new Dictionary<(long, long), double>(start.Threads.Count);
        foreach (ThreadTime thread in start.Threads) before[(thread.Id, thread.Started)] = thread.CpuMs;
        var groups = new Dictionary<string, GroupCpu>(StringComparer.Ordinal);
        foreach (ThreadTime thread in end.Threads)
        {
            double used = before.TryGetValue((thread.Id, thread.Started), out double was) ? thread.CpuMs - was : thread.CpuMs;
            groups.TryGetValue(thread.Group, out GroupCpu sum);
            groups[thread.Group] = new GroupCpu(sum.CpuMs + Math.Max(0, used), sum.Threads + 1);
        }
        return groups;
    }

    /// <summary>CPU time per group since each thread started; threads that already ended are not included.</summary>
    public static Dictionary<string, GroupCpu> Totals(ThreadSnapshot snapshot) =>
        Delta(new ThreadSnapshot(Array.Empty<ThreadTime>(), null), snapshot);

    /// <summary>
    /// "main 0.95, RenderThread 0.40, WaylithJob (15) 2.10, 3 more groups 0.02": the busiest groups first,
    /// each as CPU time divided by <paramref name="divisorMs"/> (the wall time gives cores, 1000 gives seconds),
    /// with the thread count when a group has more than one thread.
    /// </summary>
    public static string Format(IEnumerable<KeyValuePair<string, GroupCpu>> groups, double divisorMs, int shown = 8)
    {
        if (divisorMs <= 0) return "n/a";
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        int rest = 0;
        double restMs = 0;
        foreach (KeyValuePair<string, GroupCpu> pair in groups.OrderByDescending(p => p.Value.CpuMs).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            if (shown-- <= 0)
            {
                rest++;
                restMs += pair.Value.CpuMs;
                continue;
            }
            if (text.Length > 0) text.Append(", ");
            text.Append(pair.Key);
            if (pair.Value.Threads > 1) text.Append(c, $" ({pair.Value.Threads})");
            text.Append(c, $" {pair.Value.CpuMs / divisorMs:F2}");
        }
        if (rest > 0) text.Append(c, $", {rest} more groups {restMs / divisorMs:F2}");
        return text.Length == 0 ? "n/a" : text.ToString();
    }

    private unsafe ThreadSnapshot? ReadWindows(long mainThreadId)
    {
        // The layouts below are those of 64-bit Windows, the only Windows build of the game.
        if (IntPtr.Size != 8) return null;
        int length = QueryProcesses();
        if (length <= 0) return null;
        var names = new Dictionary<(long, long), string>();
        ThreadSnapshot? snapshot = ParseWindowsProcesses(new ReadOnlySpan<byte>((void*)_buffer, length), Environment.ProcessId,
            mainThreadId, (id, started) => GroupOf(Name(names, id, started, _windowsName)));
        if (snapshot != null) _names = names;
        return snapshot;
    }

    /// <summary>
    /// Finds <paramref name="processId"/> in the output of NtQuerySystemInformation(SystemProcessInformation) and reads
    /// its threads and hard page faults. <paramref name="groupOf"/> names the group of every thread but the main one.
    /// </summary>
    internal static ThreadSnapshot? ParseWindowsProcesses(ReadOnlySpan<byte> data, long processId, long mainThreadId,
        Func<long, long, string> groupOf)
    {
        long offset = 0;
        while (offset + ProcessEntryBytes <= data.Length)
        {
            ReadOnlySpan<byte> process = data[(int)offset..];
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(process);
            if (BinaryPrimitives.ReadInt64LittleEndian(process[80..]) == processId)
            {
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(process[4..]);
                if (ProcessEntryBytes + (long)count * ThreadEntryBytes > process.Length) return null;
                long hardFaults = BinaryPrimitives.ReadUInt32LittleEndian(process[16..]);
                var threads = new List<ThreadTime>((int)count);
                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> thread = process.Slice(ProcessEntryBytes + i * ThreadEntryBytes, ThreadEntryBytes);
                    long kernel = BinaryPrimitives.ReadInt64LittleEndian(thread);
                    long user = BinaryPrimitives.ReadInt64LittleEndian(thread[8..]);
                    long started = BinaryPrimitives.ReadInt64LittleEndian(thread[16..]);
                    long id = BinaryPrimitives.ReadInt64LittleEndian(thread[48..]);
                    threads.Add(new ThreadTime(id, started, (kernel + user) / 10_000.0, id == mainThreadId ? MainGroup : groupOf(id, started)));
                }
                return new ThreadSnapshot(threads, hardFaults);
            }
            if (next == 0) break;
            offset += next;
        }
        return null;
    }

    /// <summary>Fills the buffer with every process and thread of the machine; returns the bytes written, or 0.</summary>
    private int QueryProcesses()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (_buffer == IntPtr.Zero)
            {
                _bufferBytes = Math.Max(_bufferBytes, 512 * 1024);
                _buffer = Marshal.AllocHGlobal(_bufferBytes);
            }
            int status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferBytes, out int needed);
            if (status >= 0) return Math.Min(needed > 0 ? needed : _bufferBytes, _bufferBytes);
            if (status != StatusInfoLengthMismatch) return 0;
            int grown = Math.Max(_bufferBytes * 2, needed + 256 * 1024);
            if (grown > MaxBufferBytes) return 0;
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferBytes = grown;
        }
        return 0;
    }

    /// <summary>A thread's name, asked again while it is still empty, since a thread can be named after it starts.</summary>
    private string Name(Dictionary<(long, long), string> names, long id, long started, Func<long, string> read)
    {
        if (!_names.TryGetValue((id, started), out string? name) || name.Length == 0) name = read(id);
        names[(id, started)] = name;
        return name;
    }

    private string WindowsName(long id)
    {
        if (_namesUnavailable) return string.Empty;
        IntPtr handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)id);
        if (handle == IntPtr.Zero) return string.Empty;
        try
        {
            if (GetThreadDescription(handle, out IntPtr description) < 0 || description == IntPtr.Zero) return string.Empty;
            try { return Marshal.PtrToStringUni(description) ?? string.Empty; }
            finally { LocalFree(description); }
        }
        catch (EntryPointNotFoundException)
        {
            // Windows before 10 version 1607 has no thread descriptions; group every thread as unnamed.
            _namesUnavailable = true;
            return string.Empty;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private ThreadSnapshot ReadLinux(long main)
    {
        var threads = new List<ThreadTime>();
        var names = new Dictionary<(long, long), string>();
        foreach (string directory in Directory.EnumerateDirectories("/proc/self/task"))
        {
            if (!long.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out long id)) continue;
            string stat;
            try { stat = File.ReadAllText(Path.Combine(directory, "stat")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; } // the thread ended
            if (!TryParseLinuxStat(stat, out long cpuTicks, out long started, out _)) continue;
            string group = id == main ? MainGroup : GroupOf(Name(names, id, started, _ => LinuxName(directory)));
            // Linux reports thread times in USER_HZ, which is 100 per second.
            threads.Add(new ThreadTime(id, started, cpuTicks * 10.0, group));
        }
        _names = names;
        long? faults = TryParseLinuxStat(File.ReadAllText("/proc/self/stat"), out _, out _, out long majorFaults) ? majorFaults : null;
        return new ThreadSnapshot(threads, faults);
    }

    private static string LinuxName(string directory)
    {
        try { return File.ReadAllText(Path.Combine(directory, "comm")).TrimEnd('\n'); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    private static long LinuxCurrentThreadId()
    {
        // "/proc/thread-self" links to "<pid>/task/<tid>" for the calling thread.
        string? target = new FileInfo("/proc/thread-self").LinkTarget;
        return target != null && long.TryParse(Path.GetFileName(target), NumberStyles.None, CultureInfo.InvariantCulture, out long id)
            ? id : -1;
    }

    /// <summary>Reads user plus system time, the start time and the major faults from a /proc stat line.</summary>
    internal static bool TryParseLinuxStat(string stat, out long cpuTicks, out long started, out long majorFaults)
    {
        cpuTicks = started = majorFaults = 0;
        // The name in parentheses can hold spaces, so the fields are counted from the last ')'.
        int close = stat.LastIndexOf(')');
        if (close < 0 || close + 2 >= stat.Length) return false;
        string[] fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Field n of proc(5) is fields[n - 3]: majflt 12, utime 14, stime 15, starttime 22.
        if (fields.Length < 20) return false;
        var c = CultureInfo.InvariantCulture;
        if (!long.TryParse(fields[9], NumberStyles.None, c, out majorFaults)
            || !long.TryParse(fields[11], NumberStyles.None, c, out long user)
            || !long.TryParse(fields[12], NumberStyles.None, c, out long system)
            || !long.TryParse(fields[19], NumberStyles.None, c, out started))
            return false;
        cpuTicks = user + system;
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    // SYSTEM_PROCESS_INFORMATION and SYSTEM_THREAD_INFORMATION on 64-bit Windows, as System.Diagnostics.Process reads them:
    // process +0 NextEntryOffset, +4 NumberOfThreads, +16 HardFaultCount, +80 UniqueProcessId, threads follow at +256;
    // thread +0 KernelTime, +8 UserTime, +16 CreateTime (100 ns units), +48 ClientId.UniqueThread.
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int ProcessEntryBytes = 256;
    private const int ThreadEntryBytes = 80;
    private const int MaxBufferBytes = 64 * 1024 * 1024;
    private const uint ThreadQueryLimitedInformation = 0x0800;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

    [DllImport("kernel32.dll")]
    private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
