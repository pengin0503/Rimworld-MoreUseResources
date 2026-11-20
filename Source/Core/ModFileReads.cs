using System;
using System.Threading;

namespace MoreUseResources.Core;

/// <summary>
/// Bytes every reader of this mod has read from files since the process started: the prefetch, the asset audit
/// and its XML and texture checks, the resident-pack reads, the patched XML cache and the small state files.
/// The process read counter includes these reads, so subtracting this total leaves what the game itself read.
/// Reads through a file stream count the bytes the stream handed out, which can be up to one buffer less
/// than the stream asked the system for.
/// </summary>
public static class ModFileReads
{
    private static long _bytes;

    public static long Bytes => Interlocked.Read(ref _bytes);

    public static void Add(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _bytes, bytes);
    }

    /// <summary>
    /// What a buffered stream with <paramref name="bufferBytes"/> asked the system for, when it handed out
    /// <paramref name="position"/> bytes of a <paramref name="length"/>-byte file from the start.
    /// </summary>
    public static long Buffered(long position, long length, int bufferBytes)
    {
        if (position <= 0 || length <= 0) return 0;
        if (bufferBytes <= 1) return Math.Min(position, length);
        long rounded = (position + bufferBytes - 1) / bufferBytes * bufferBytes;
        return Math.Min(rounded, length);
    }
}
