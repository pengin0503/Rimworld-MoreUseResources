using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MoreUseResources.Benchmarks.Measurement;

/// <summary>How many pages of a set of files the operating system currently keeps in its file cache.</summary>
public readonly record struct Residency(long ResidentPages, long TotalPages, int Files, int Unreadable)
{
    public double Fraction => TotalPages == 0 ? double.NaN : ResidentPages / (double)TotalPages;
}

/// <summary>
/// Controls and verifies the Linux page cache for benchmark input files. Cold and warm states are
/// produced with per-file <c>posix_fadvise(DONTNEED)</c> (no privileges needed) or a global
/// <c>drop_caches</c> (root only), and are always verified with <c>mincore</c>. A state that cannot be
/// verified is reported as such and never counted as measured.
/// </summary>
public static unsafe class PageCache
{
    private const int ProtRead = 1;
    private const int MapShared = 1;
    private const int PosixFadvDontNeed = 4;
    private static readonly IntPtr MapFailed = new IntPtr(-1);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr addr, nuint length, int prot, int flags, int fd, long offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(IntPtr addr, nuint length);

    [DllImport("libc", SetLastError = true)]
    private static extern int mincore(IntPtr addr, nuint length, byte* vec);

    [DllImport("libc")]
    private static extern int posix_fadvise(int fd, long offset, long len, int advice);

    [DllImport("libc", SetLastError = true)]
    private static extern int fdatasync(int fd);

    [DllImport("libc")]
    private static extern void sync();

    public static bool Supported => OperatingSystem.IsLinux();

    /// <summary>Residency of all files; files that cannot be opened or mapped are counted as unreadable.</summary>
    public static Residency Measure(IReadOnlyList<string> files, CancellationToken token = default)
    {
        if (!Supported) return new Residency(0, 0, files.Count, files.Count);
        long resident = 0, total = 0;
        int unreadable = 0;
        int pageSize = Environment.SystemPageSize;
        foreach (string path in files)
        {
            token.ThrowIfCancellationRequested();
            if (!TryMeasureFile(path, pageSize, out long r, out long t)) { unreadable++; continue; }
            resident += r;
            total += t;
        }
        return new Residency(resident, total, files.Count, unreadable);
    }

    public static bool TryMeasureFile(string path, int pageSize, out long resident, out long total)
    {
        resident = total = 0;
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = RandomAccess.GetLength(handle);
            if (length <= 0) return true;
            int fd = (int)handle.DangerousGetHandle();
            IntPtr address = mmap(IntPtr.Zero, (nuint)length, ProtRead, MapShared, fd, 0);
            if (address == MapFailed) return false;
            try
            {
                long pages = (length + pageSize - 1) / pageSize;
                byte[] vector = ArrayPool<byte>.Shared.Rent((int)Math.Min(pages, int.MaxValue));
                try
                {
                    fixed (byte* v = vector)
                    {
                        if (mincore(address, (nuint)length, v) != 0) return false;
                    }
                    for (long i = 0; i < pages; i++)
                        if ((vector[i] & 1) != 0) resident++;
                    total = pages;
                    return true;
                }
                finally { ArrayPool<byte>.Shared.Return(vector); }
            }
            finally { munmap(address, (nuint)length); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Writes back and drops cached pages of each file. Returns how many files were advised.</summary>
    public static int Evict(IReadOnlyList<string> files, CancellationToken token = default)
    {
        if (!Supported) return 0;
        int advised = 0;
        foreach (string path in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                int fd = (int)handle.DangerousGetHandle();
                // Dirty pages are not dropped by DONTNEED; write them back first.
                fdatasync(fd);
                if (posix_fadvise(fd, 0, 0, PosixFadvDontNeed) == 0) advised++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return advised;
    }

    /// <summary>
    /// Drops the whole page cache, and with <paramref name="includeMetadata"/> also dentries and inodes.
    /// Needs root; returns false with the reason otherwise.
    /// </summary>
    public static bool DropAll(bool includeMetadata, out string? error)
    {
        error = null;
        if (!Supported) { error = "not Linux"; return false; }
        try
        {
            sync();
            File.WriteAllText("/proc/sys/vm/drop_caches", includeMetadata ? "3" : "1");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>Reads every file once so its pages enter the cache. Returns the bytes read.</summary>
    public static long Warm(IReadOnlyList<string> files, CancellationToken token = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        long bytes = 0;
        try
        {
            foreach (string path in files)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
                    long offset = 0;
                    int read;
                    while ((read = RandomAccess.Read(handle, buffer, offset)) > 0)
                    {
                        offset += read;
                        bytes += read;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return bytes;
    }
}
