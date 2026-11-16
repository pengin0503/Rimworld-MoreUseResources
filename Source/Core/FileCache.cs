using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MoreUseResources.Core
{
    /// <summary>
    /// Reads real game files (asset pack, texture cache, mod textures) on worker threads.
    /// Reading warms the operating system's file cache, which the game benefits from when it loads
    /// those files later. In "hold" mode the bytes are additionally kept in this process, which is
    /// what makes the usage visible as RimWorld's own memory; the game does not read those copies.
    /// Files are opened with full sharing and closed right after the read, so the game, Steam and
    /// other mods can still replace or delete them.
    /// </summary>
    public sealed unsafe class FileCache
    {
        public const long MaxFileBytes = 1L << 30;
        private const int ChunkBytes = 1 << 20;
        private const long RetryGrowthBytes = 64L << 20;
        private const long ReleaseCooldownMs = 30_000;
        private const long RetryCooldownMs = 5_000;

        private readonly NativeBlockStore _store;
        private readonly WorkerPool _pool;
        private readonly Func<long> _clockMs;
        private readonly object _gate = new object();
        private readonly HashSet<string> _done = new HashSet<string>(StringComparer.Ordinal);

        private List<string> _files = new List<string>();
        private int _cursor;
        private int _inFlight;
        private int _deferredForBudget;
        private long _limitAtPassStart;
        private long _cooldownUntilMs;
        private bool _needsPass;
        private long _limit;
        private volatile bool _hold = true;
        private volatile bool _paused;
        private int _generation;

        private long _bytesRead;
        private long _filesRead;
        private long _errors;
        private long _skippedLarge;
        private string? _lastError;

        public FileCache(NativeBlockStore store, WorkerPool pool, Func<long>? clockMs = null)
        {
            _store = store;
            _pool = pool;
            _clockMs = clockMs ?? (() => Environment.TickCount64);
            _store.BlockReleased += OnBlockReleased;
        }

        public long BytesRead => Interlocked.Read(ref _bytesRead);
        public long FilesRead => Interlocked.Read(ref _filesRead);
        public long Errors => Interlocked.Read(ref _errors);
        public long SkippedLarge => Interlocked.Read(ref _skippedLarge);
        public string? LastError => Volatile.Read(ref _lastError);
        public int InFlight => Volatile.Read(ref _inFlight);

        public int SourceCount { get { lock (_gate) { return _files.Count; } } }
        public int Cursor { get { lock (_gate) { return _cursor; } } }
        public int DeferredForBudget { get { lock (_gate) { return _deferredForBudget; } } }

        /// <summary>true when every source file was visited and nothing is being read.</summary>
        public bool PassComplete
        {
            get { lock (_gate) { return _cursor >= _files.Count && _inFlight == 0; } }
        }

        /// <summary>Replaces the list of candidate files (highest priority first) and restarts the pass.</summary>
        public void SetSources(IEnumerable<string> files)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                if (!string.IsNullOrEmpty(f) && seen.Add(f)) list.Add(f);
            }
            lock (_gate)
            {
                _files = list;
                _cursor = 0;
                _deferredForBudget = 0;
                _needsPass = false;
                _generation++;
            }
        }

        /// <summary>Forgets what was read, so the next pass reads everything again.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _done.Clear();
                _cursor = 0;
                _deferredForBudget = 0;
                _generation++;
            }
        }

        /// <summary>
        /// Schedules more reads. Called periodically from the control thread.
        /// <paramref name="limitBytes"/> is the most the store may hold right now.
        /// </summary>
        public void Pump(bool active, bool hold, long limitBytes)
        {
            Volatile.Write(ref _limit, limitBytes);
            if (_hold != hold)
            {
                _hold = hold;
                Reset();
            }
            _paused = !active;
            if (!active) return;

            long now = _clockMs();
            int maxInFlight = Math.Max(2, _pool.ActiveWorkers * 2);
            while (true)
            {
                string path;
                int generation;
                lock (_gate)
                {
                    if (_cursor >= _files.Count)
                    {
                        if (_inFlight == 0 && now >= _cooldownUntilMs
                            && (_needsPass || (_deferredForBudget > 0 && limitBytes >= _limitAtPassStart + RetryGrowthBytes)))
                        {
                            _cursor = 0;
                            _deferredForBudget = 0;
                            _needsPass = false;
                            _limitAtPassStart = limitBytes;
                            _cooldownUntilMs = now + RetryCooldownMs;
                            continue;
                        }
                        return;
                    }
                    if (_inFlight >= maxInFlight) return;
                    if (_cursor == 0) _limitAtPassStart = limitBytes;
                    path = _files[_cursor++];
                    if (_done.Contains(path)) continue;
                    _inFlight++;
                    generation = _generation;
                }
                _pool.Enqueue(token => ReadOne(path, generation, token));
            }
        }

        private void OnBlockReleased(string key)
        {
            lock (_gate)
            {
                _done.Remove(key);
                _needsPass = true;
                _cooldownUntilMs = Math.Max(_cooldownUntilMs, _clockMs() + ReleaseCooldownMs);
            }
        }

        private void ReadOne(string path, int generation, CancellationToken token)
        {
            bool completed = false;
            bool deferred = false;
            try
            {
                if (token.IsCancellationRequested) return;
                using SafeFileHandle handle = File.OpenHandle(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
                long length = RandomAccess.GetLength(handle);
                if (length <= 0)
                {
                    completed = true;
                    return;
                }
                if (_hold)
                {
                    if (length > MaxFileBytes)
                    {
                        Interlocked.Increment(ref _skippedLarge);
                        completed = true;
                        return;
                    }
                    if (!_store.TryBegin(length, Volatile.Read(ref _limit), out IntPtr ptr))
                    {
                        deferred = true;
                        return;
                    }
                    bool ok = false;
                    try
                    {
                        ok = ReadInto(handle, (byte*)ptr, length, token);
                    }
                    finally
                    {
                        if (ok) _store.Commit(ptr, length, path);
                        else _store.Abort(ptr, length);
                    }
                    completed = ok;
                }
                else
                {
                    completed = ReadAndDiscard(handle, length, token);
                }
                if (completed)
                {
                    Interlocked.Increment(ref _filesRead);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException)
            {
                // Missing, locked or vanished files are normal: mark as visited and move on.
                Interlocked.Increment(ref _errors);
                Volatile.Write(ref _lastError, ex.GetType().Name + ": " + Path.GetFileName(path));
                completed = true;
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                    if (generation == _generation)
                    {
                        if (completed) _done.Add(path);
                        if (deferred) _deferredForBudget++;
                        // Interrupted (paused or cancelled): make sure a later pass picks it up again.
                        if (!completed && !deferred) _needsPass = true;
                    }
                }
            }
        }

        private bool ReadInto(SafeFileHandle handle, byte* dest, long length, CancellationToken token)
        {
            long offset = 0;
            while (offset < length)
            {
                if (token.IsCancellationRequested || _paused) return false;
                int want = (int)Math.Min(ChunkBytes, length - offset);
                int got = RandomAccess.Read(handle, new Span<byte>(dest + offset, want), offset);
                if (got <= 0) return false; // file shrank while reading
                offset += got;
                Interlocked.Add(ref _bytesRead, got);
            }
            return true;
        }

        private bool ReadAndDiscard(SafeFileHandle handle, long length, CancellationToken token)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
            try
            {
                long offset = 0;
                while (offset < length)
                {
                    if (token.IsCancellationRequested || _paused) return false;
                    int got = RandomAccess.Read(handle, buffer.AsSpan(0, ChunkBytes), offset);
                    if (got <= 0) break;
                    offset += got;
                    Interlocked.Add(ref _bytesRead, got);
                }
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
