using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MoreUseResources.Core;

/// <summary>
/// Cumulative counters of this process read at one moment: CPU time of the process and of the calling thread,
/// busy time of the whole machine, JIT work and bytes read from files. Values the platform does not report are null.
/// Reading them changes nothing; they show where the game spends CPU and disk, so it is known where more can be used.
/// </summary>
public readonly record struct ProcessCounters(double WallMs, double CpuMs, double? ThreadCpuMs, double? SystemBusyMs,
    double? SystemTotalMs, double JitMs, long JitMethods, long? ReadBytes)
{
    /// <summary>Call on the thread whose CPU time should be <see cref="ThreadCpuMs"/> (the main thread).</summary>
    public static ProcessCounters ReadCurrent()
    {
        using Process process = Process.GetCurrentProcess();
        double cpu = process.TotalProcessorTime.TotalMilliseconds;
        double? thread = null, busy = null, total = null;
        long? read = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user))
                    thread = (kernel + user) / 10_000.0;
                if (GetSystemTimes(out long idle, out long systemKernel, out long systemUser))
                {
                    // The kernel time of the whole machine includes its idle time.
                    total = (systemKernel + systemUser) / 10_000.0;
                    busy = (systemKernel + systemUser - idle) / 10_000.0;
                }
                if (GetProcessIoCounters(process.Handle, out IoCounters io))
                    read = (long)io.ReadTransferCount;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }
        return new ProcessCounters(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency, cpu, thread, busy, total,
            System.Runtime.JitInfo.GetCompilationTime(false).TotalMilliseconds, System.Runtime.JitInfo.GetCompiledMethodCount(false), read);
    }

    public ProcessCounters Since(ProcessCounters start) => new(WallMs - start.WallMs, CpuMs - start.CpuMs,
        ThreadCpuMs - start.ThreadCpuMs, SystemBusyMs - start.SystemBusyMs, SystemTotalMs - start.SystemTotalMs,
        JitMs - start.JitMs, JitMethods - start.JitMethods, ReadBytes - start.ReadBytes);

    /// <summary>
    /// Everything the process did from its start until the game finished loading, as one line.
    /// <paramref name="wallMs"/> is the time since the process started; the counters here are totals since then.
    /// </summary>
    public static string FormatStartup(double wallMs, ProcessCounters totals, GcCounters gc, long modReadBytes)
    {
        static string Seconds(double ms) => (ms / 1000).ToString("F1", CultureInfo.InvariantCulture);
        static string MiB(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F0", CultureInfo.InvariantCulture);
        var c = CultureInfo.InvariantCulture;
        string text = string.Create(c, $"{Seconds(wallMs)} s, process CPU {Seconds(totals.CpuMs)} s ({(wallMs > 0 ? totals.CpuMs / wallMs : 0):F1} cores on average)");
        if (totals.ThreadCpuMs is { } main)
            text += string.Create(c, $", main thread busy {Seconds(main)} s ({(wallMs > 0 ? main / wallMs * 100 : 0):F0}%)");
        int gen2 = gc.Gen2, gen1 = Math.Max(0, gc.Gen1 - gen2), gen0 = Math.Max(0, gc.Gen0 - gc.Gen1);
        text += string.Create(c, $", JIT {Seconds(totals.JitMs)} s ({totals.JitMethods} methods), GC stopped {Seconds(gc.TotalPauseMs)} s (gen0/1/2 {gen0}/{gen1}/{gen2}), allocated {MiB(gc.AllocatedBytes)} MiB");
        text += totals.ReadBytes is { } read
            ? string.Create(c, $", I/O read {MiB(read)} MiB (this mod {MiB(modReadBytes)} MiB)")
            : string.Create(c, $", I/O read n/a (this mod {MiB(modReadBytes)} MiB)");
        return text;
    }

    /// <summary>
    /// Committed private memory of this process (Task Manager's "commit size"). Windows reads it with GetProcessMemoryInfo,
    /// because <see cref="Process.PrivateMemorySize64"/> there lists every process of the machine to find this one,
    /// which takes milliseconds on a desktop.
    /// </summary>
    public static long PrivateBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var counters = new MemoryCountersEx { Size = (uint)Marshal.SizeOf<MemoryCountersEx>() };
                if (K32GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size)) return (long)counters.PrivateUsage;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }
        using Process process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    /// <summary>PROCESS_MEMORY_COUNTERS_EX.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCountersEx
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref MemoryCountersEx counters, uint size);
}
