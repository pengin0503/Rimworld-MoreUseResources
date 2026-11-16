using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace MoreUseResources.Core
{
    /// <summary>
    /// Owns blocks of native (non-GC) memory. Native memory is used on purpose: it adds nothing to
    /// the managed heap, so the game's garbage collector neither scans nor moves it.
    /// A block is invisible to <see cref="Release"/> until it is committed, so a thread that is
    /// still filling a block can never have it freed underneath it.
    /// </summary>
    public sealed unsafe class NativeBlockStore : IDisposable
    {
        private struct Block
        {
            public IntPtr Ptr;
            public long Size;
            public string? Key;
        }

        private readonly object _gate = new object();
        private readonly List<Block> _blocks = new List<Block>();
        private long _committed;
        private long _pending;
        private bool _disposed;
        private long _allocFailures;

        /// <summary>Raised (outside the lock) with the key of each released block that had one.</summary>
        public event Action<string>? BlockReleased;

        public long CommittedBytes => Interlocked.Read(ref _committed);
        public long PendingBytes => Interlocked.Read(ref _pending);
        public long TotalBytes => CommittedBytes + PendingBytes;
        public long AllocationFailures => Interlocked.Read(ref _allocFailures);

        public int BlockCount
        {
            get { lock (_gate) { return _blocks.Count; } }
        }

        /// <summary>
        /// Allocates a block if committed + pending + size stays within <paramref name="limitBytes"/>.
        /// The caller must finish with <see cref="Commit"/> or <see cref="Abort"/>.
        /// </summary>
        public bool TryBegin(long size, long limitBytes, out IntPtr ptr)
        {
            ptr = IntPtr.Zero;
            if (size <= 0) return false;
            lock (_gate)
            {
                if (_disposed) return false;
                if (_committed + _pending + size > limitBytes) return false;
                _pending += size;
            }
            try
            {
                ptr = (IntPtr)NativeMemory.Alloc((nuint)size);
            }
            catch (OutOfMemoryException)
            {
                ptr = IntPtr.Zero;
            }
            if (ptr == IntPtr.Zero)
            {
                Interlocked.Increment(ref _allocFailures);
                lock (_gate) { _pending -= size; }
                return false;
            }
            return true;
        }

        public void Commit(IntPtr ptr, long size, string? key)
        {
            bool freeNow = false;
            lock (_gate)
            {
                _pending -= size;
                if (_disposed)
                {
                    freeNow = true;
                }
                else
                {
                    _blocks.Add(new Block { Ptr = ptr, Size = size, Key = key });
                    _committed += size;
                }
            }
            if (freeNow)
            {
                NativeMemory.Free((void*)ptr);
            }
        }

        public void Abort(IntPtr ptr, long size)
        {
            lock (_gate) { _pending -= size; }
            NativeMemory.Free((void*)ptr);
        }

        /// <summary>Writes one byte per page so the operating system really backs the block with RAM.</summary>
        public static void Touch(IntPtr ptr, long size, CancellationToken token = default)
        {
            byte* p = (byte*)ptr;
            const int Page = 4096;
            const int CheckEvery = 4096; // pages, i.e. every 16 MiB
            long pages = (size + Page - 1) / Page;
            for (long i = 0; i < pages; i++)
            {
                p[i * Page] = (byte)i;
                if ((i % CheckEvery) == CheckEvery - 1 && token.IsCancellationRequested) return;
            }
        }

        /// <summary>Releases newest blocks first until at least <paramref name="bytes"/> are freed. Returns bytes freed.</summary>
        public long Release(long bytes)
        {
            if (bytes <= 0) return 0;
            List<Block> freed = new List<Block>();
            long total = 0;
            lock (_gate)
            {
                while (total < bytes && _blocks.Count > 0)
                {
                    Block b = _blocks[_blocks.Count - 1];
                    _blocks.RemoveAt(_blocks.Count - 1);
                    _committed -= b.Size;
                    total += b.Size;
                    freed.Add(b);
                }
            }
            FreeBlocks(freed);
            return total;
        }

        public long ReleaseAll() => Release(long.MaxValue);

        private void FreeBlocks(List<Block> freed)
        {
            foreach (Block b in freed)
            {
                NativeMemory.Free((void*)b.Ptr);
            }
            Action<string>? handler = BlockReleased;
            if (handler != null)
            {
                foreach (Block b in freed)
                {
                    if (b.Key != null)
                    {
                        try { handler(b.Key); } catch (Exception) { }
                    }
                }
            }
        }

        public void Dispose()
        {
            List<Block> freed;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                freed = new List<Block>(_blocks);
                _blocks.Clear();
                _committed = 0;
            }
            FreeBlocks(freed);
        }
    }
}
