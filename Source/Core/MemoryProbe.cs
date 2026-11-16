using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace MoreUseResources.Core
{
    public readonly struct MemoryStatus
    {
        public readonly long TotalPhysical;
        public readonly long AvailablePhysical;
        /// <summary>false when the numbers are an estimate from the garbage collector instead of the OS.</summary>
        public readonly bool FromOperatingSystem;

        public MemoryStatus(long total, long available, bool fromOs)
        {
            TotalPhysical = total;
            AvailablePhysical = available;
            FromOperatingSystem = fromOs;
        }
    }

    public interface IMemoryProbe
    {
        MemoryStatus Read();
    }

    /// <summary>Reads physical memory totals from Windows or Linux, with a runtime-based fallback.</summary>
    public sealed class SystemMemoryProbe : IMemoryProbe
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        private bool _nativeFailed;

        public MemoryStatus Read()
        {
            if (!_nativeFailed)
            {
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                        if (GlobalMemoryStatusEx(ref m) && m.ullTotalPhys > 0)
                        {
                            return new MemoryStatus((long)m.ullTotalPhys, (long)m.ullAvailPhys, true);
                        }
                    }
                    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        if (TryReadProcMeminfo(out MemoryStatus status)) return status;
                    }
                }
                catch (Exception)
                {
                    _nativeFailed = true;
                }
            }
            return ReadFromGc();
        }

        internal static bool TryParseMeminfo(string text, out long totalBytes, out long availableBytes)
        {
            totalBytes = -1;
            availableBytes = -1;
            foreach (string line in text.Split('\n'))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) totalBytes = ParseKb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) availableBytes = ParseKb(line);
            }
            return totalBytes > 0 && availableBytes >= 0;
        }

        private static long ParseKb(string line)
        {
            int colon = line.IndexOf(':');
            string rest = line.Substring(colon + 1).Trim();
            int space = rest.IndexOf(' ');
            if (space > 0) rest = rest.Substring(0, space);
            return long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb) ? kb * 1024L : -1;
        }

        private static bool TryReadProcMeminfo(out MemoryStatus status)
        {
            status = default;
            if (!File.Exists("/proc/meminfo")) return false;
            if (!TryParseMeminfo(File.ReadAllText("/proc/meminfo"), out long total, out long avail)) return false;
            status = new MemoryStatus(total, avail, true);
            return true;
        }

        private static MemoryStatus ReadFromGc()
        {
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            long total = info.TotalAvailableMemoryBytes;
            long avail = Math.Max(0, total - info.MemoryLoadBytes);
            return new MemoryStatus(total, avail, false);
        }
    }
}
