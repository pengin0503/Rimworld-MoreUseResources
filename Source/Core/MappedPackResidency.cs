using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MoreUseResources.Core
{
    public readonly struct PackResidencySnapshot
    {
        public PackResidencySnapshot(int sources, int mappedFiles, long mappedBytes, int passes, int deferred, long errors, string? lastError)
        {
            Sources = sources;
            MappedFiles = mappedFiles;
            MappedBytes = mappedBytes;
            Passes = passes;
            Deferred = deferred;
            Errors = errors;
            LastError = lastError;
        }

        public int Sources { get; }
        public int MappedFiles { get; }
        public long MappedBytes { get; }
        /// <summary>Completed passes over all sources (first mapping pass included).</summary>
        public int Passes { get; }
        /// <summary>Sources left unmapped in the last pass because of the byte limit.</summary>
        public int Deferred { get; }
        public long Errors { get; }
        public string? LastError { get; }
    }

    /// <summary>
    /// Keeps the game's read-only asset packs (Assets.zip) resident in this process.
    /// The engine itself maps these files read-only, so a second read-only view in the same
    /// process shares the same physical pages: touching them here puts real game data into
    /// RimWorld's working set, where the engine's own view finds it without disk access.
    /// Nothing is copied and nothing is written. Each chunk is first read through the file
    /// handle, so that disk errors surface as ordinary exceptions before the view is touched.
    /// Only files that nobody rewrites in place may be given to this class: a mapped file cannot
    /// be truncated or replaced on Windows. The engine's texture-cache packs are therefore excluded.
    /// </summary>
    public sealed unsafe class MappedPackResidency : IDisposable
    {
        public const long MaxFileBytes = 8L << 30;
        private const int PageBytes = 4096;
        private const int ChunkBytes = 1 << 20;

        private sealed class Mapping
        {
            public readonly object Sync = new object();
            public SafeFileHandle? Handle;
            public MemoryMappedFile? Map;
            public MemoryMappedViewAccessor? View;
            public byte* Pointer;
            public long Length;
            public bool Closed;

            public void Close()
            {
                lock (Sync)
                {
                    if (Closed) return;
                    Closed = true;
                    if (Pointer != null)
                    {
                        View!.SafeMemoryMappedViewHandle.ReleasePointer();
                        Pointer = null;
                    }
                    View?.Dispose();
                    Map?.Dispose();
                    Handle?.Dispose();
                }
            }
        }

        private readonly WorkerPool _pool;
        private readonly Func<long> _clockMs;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Mapping> _maps = new Dictionary<string, Mapping>(StringComparer.Ordinal);
        private readonly HashSet<string> _failed = new HashSet<string>(StringComparer.Ordinal);
        private List<string> _sources = new List<string>();
        private int _cursor;
        private int _inFlight;
        private int _generation;
        private long _mappedBytes;
        private long _limit;
        private int _passes;
        private int _deferred;
        private int _deferredThisPass;
        private bool _passOpen = true;
        private long _passCompletedAtMs;
        private long _errors;
        private string? _lastError;
        private volatile bool _allowed;
        private bool _disposed;

        public MappedPackResidency(WorkerPool pool, Func<long>? clockMs = null)
        {
            _pool = pool;
            _clockMs = clockMs ?? (() => Environment.TickCount64);
        }

        public PackResidencySnapshot Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return new PackResidencySnapshot(_sources.Count, _maps.Count, _mappedBytes, _passes,
                        _deferred, _errors, _lastError);
                }
            }
        }

        /// <summary>Replaces the candidate list. Views of files no longer listed are closed.</summary>
        public void SetSources(IEnumerable<string> paths)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                if (!string.IsNullOrEmpty(path) && seen.Add(path)) list.Add(path);
            }
            List<Mapping> closing = new List<Mapping>();
            lock (_gate)
            {
                if (_disposed) return;
                var stale = new List<string>();
                foreach (string key in _maps.Keys)
                {
                    if (!seen.Contains(key)) stale.Add(key);
                }
                foreach (string key in stale)
                {
                    closing.Add(_maps[key]);
                    _mappedBytes -= _maps[key].Length;
                    _maps.Remove(key);
                }
                _sources = list;
                _failed.Clear();
                _cursor = 0;
                _deferredThisPass = 0;
                _passOpen = true;
            }
            foreach (Mapping mapping in closing) mapping.Close();
        }

        /// <summary>
        /// Schedules at most one file per call. <paramref name="allowed"/> false stops new work
        /// but keeps existing views; use <see cref="ReleaseAll"/> to give the memory back.
        /// </summary>
        public void Pump(bool allowed, long limitBytes, long refreshIntervalMs)
        {
            _allowed = allowed;
            if (!allowed) return;
            string path;
            int generation;
            lock (_gate)
            {
                if (_disposed || _inFlight != 0) return;
                _limit = Math.Max(0, limitBytes);
                if (!_passOpen)
                {
                    long now = _clockMs();
                    if (refreshIntervalMs <= 0 || now < _passCompletedAtMs || now - _passCompletedAtMs < refreshIntervalMs)
                        return;
                    _cursor = 0;
                    _deferredThisPass = 0;
                    _passOpen = true;
                }
                while (_cursor < _sources.Count && _failed.Contains(_sources[_cursor])) _cursor++;
                if (_cursor >= _sources.Count)
                {
                    FinishPassLocked();
                    return;
                }
                path = _sources[_cursor++];
                generation = _generation;
                _inFlight++;
            }
            _pool.Enqueue(token =>
            {
                bool finished = true;
                try
                {
                    finished = MapAndTouch(path, generation, token);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                {
                    Fail(path, ex);
                }
                finally
                {
                    lock (_gate)
                    {
                        _inFlight--;
                        // An interrupted file is visited again instead of being counted as done.
                        if (!finished && generation == _generation && _cursor > 0) _cursor--;
                        if (_cursor >= _sources.Count && _inFlight == 0 && _passOpen && generation == _generation)
                            FinishPassLocked();
                    }
                }
            });
        }

        /// <summary>Closes every view. A later <see cref="Pump"/> starts again from the first source.</summary>
        public void ReleaseAll()
        {
            List<Mapping> closing;
            lock (_gate)
            {
                _generation++;
                closing = new List<Mapping>(_maps.Values);
                _maps.Clear();
                _mappedBytes = 0;
                _failed.Clear();
                _cursor = 0;
                _deferredThisPass = 0;
                _deferred = 0;
                _passOpen = true;
            }
            foreach (Mapping mapping in closing) mapping.Close();
        }

        public void Dispose()
        {
            lock (_gate) { _disposed = true; }
            _allowed = false;
            ReleaseAll();
        }

        private void FinishPassLocked()
        {
            _passOpen = false;
            _passes++;
            _deferred = _deferredThisPass;
            _passCompletedAtMs = _clockMs();
        }

        private void Fail(string path, Exception ex)
        {
            Mapping? broken = null;
            lock (_gate)
            {
                _errors++;
                _lastError = ex.GetType().Name + ": " + ex.Message;
                _failed.Add(path);
                if (_maps.TryGetValue(path, out broken))
                {
                    _mappedBytes -= broken.Length;
                    _maps.Remove(path);
                }
            }
            broken?.Close();
        }

        private bool Live(int generation, CancellationToken token)
        {
            return _allowed && !token.IsCancellationRequested && generation == Volatile.Read(ref _generation);
        }

        private bool MapAndTouch(string path, int generation, CancellationToken token)
        {
            if (!Live(generation, token)) return false;
            Mapping? mapping;
            lock (_gate) { _maps.TryGetValue(path, out mapping); }
            if (mapping == null)
            {
                mapping = Open(path, generation, out bool skipped);
                if (mapping == null) return skipped;
            }

            byte[] buffer = new byte[ChunkBytes];
            long offset = 0;
            while (offset < mapping.Length)
            {
                if (!Live(generation, token)) return false;
                lock (mapping.Sync)
                {
                    if (mapping.Closed) return false;
                    int want = (int)Math.Min(ChunkBytes, mapping.Length - offset);
                    // An ordinary read first: a failing disk raises IOException here instead of
                    // faulting inside the mapped view below.
                    int read = RandomAccess.Read(mapping.Handle!, buffer.AsSpan(0, want), offset);
                    if (read < want) throw new IOException("Asset pack became shorter while it was mapped.");
                    byte sink = 0;
                    byte* start = mapping.Pointer + offset;
                    for (int i = 0; i < want; i += PageBytes) sink ^= start[i];
                    GC.KeepAlive(sink);
                    offset += want;
                }
            }
            return true;
        }

        /// <param name="skipped">true when the file was deliberately left unmapped (size or byte limit).</param>
        private Mapping? Open(string path, int generation, out bool skipped)
        {
            skipped = false;
            var mapping = new Mapping();
            bool published = false;
            try
            {
                // Same access and sharing as the engine's own handle on this file.
                mapping.Handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
                long length = RandomAccess.GetLength(mapping.Handle);
                if (length <= 0 || length > MaxFileBytes)
                {
                    lock (_gate) { _failed.Add(path); }
                    skipped = true;
                    return null;
                }
                lock (_gate)
                {
                    if (generation != _generation || _disposed) return null;
                    if (_mappedBytes + length > _limit)
                    {
                        _deferredThisPass++;
                        skipped = true;
                        return null;
                    }
                    // Reserve the bytes before mapping so that parallel passes cannot overshoot.
                    _mappedBytes += length;
                }
                mapping.Length = length;
                try
                {
                    mapping.Map = MemoryMappedFile.CreateFromFile(mapping.Handle, null, 0, MemoryMappedFileAccess.Read,
                        HandleInheritability.None, leaveOpen: true);
                    mapping.View = mapping.Map.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
                    byte* pointer = null;
                    mapping.View.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                    mapping.Pointer = pointer + mapping.View.PointerOffset;
                    lock (_gate)
                    {
                        if (generation == _generation && !_disposed)
                        {
                            _maps[path] = mapping;
                            published = true;
                        }
                    }
                }
                finally
                {
                    if (!published)
                    {
                        lock (_gate)
                        {
                            // ReleaseAll already zeroed the counter for an older generation.
                            if (generation == _generation) _mappedBytes -= length;
                        }
                    }
                }
                return published ? mapping : null;
            }
            finally
            {
                if (!published) mapping.Close();
            }
        }
    }
}
