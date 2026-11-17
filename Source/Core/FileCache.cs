using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
        private readonly Func<bool>? _mayRead;
        private readonly Func<bool>? _mayCopy;
        private readonly object _gate = new object();
        private readonly HashSet<string> _done = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _warmed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _copyHashes = new Dictionary<string, string>(StringComparer.Ordinal);

        private List<string> _files = new List<string>();
        private HashSet<string> _prefetchOnly = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _oversize = new HashSet<string>(StringComparer.Ordinal);
        private int _cursor;
        private int _inFlight;
        private int _deferredForBudget;
        private long _limitAtPassStart;
        private long _cooldownUntilMs;
        private bool _needsPass;
        private long _limit;
        private bool _admissionOpen;
        private bool _hold = true;
        private bool _readCopies = true;
        private volatile bool _paused;
        private int _generation;
        // A maintenance pass only warms the OS page cache. Already-held native copies
        // remain untouched because RimWorld cannot read the MOD's private buffers.
        private bool _maintenancePass;
        private bool _passRecorded;
        private long _passCompletedAtMs;
        private int _periodicRefreshes;

        private long _bytesRead;
        private long _filesRead;
        private long _errors;
        private long _skippedLarge;
        private long _budgetFallbackWarms;
        private string? _lastError;

        public FileCache(NativeBlockStore store, WorkerPool pool, Func<long>? clockMs = null, Func<bool>? mayRead = null, Func<bool>? mayCopy = null)
        {
            _store = store;
            _pool = pool;
            _mayRead = mayRead;
            _mayCopy = mayCopy;
            _clockMs = clockMs ?? (() => Environment.TickCount64);
            _store.BlockReleased += OnBlockReleased;
        }

        public long BytesRead => Interlocked.Read(ref _bytesRead);
        public long FilesRead => Interlocked.Read(ref _filesRead);
        public long Errors => Interlocked.Read(ref _errors);
        public long SkippedLarge => Interlocked.Read(ref _skippedLarge);
        /// <summary>Files read into the OS cache because the native-copy budget had no room for them.</summary>
        public long BudgetFallbackWarms => Interlocked.Read(ref _budgetFallbackWarms);
        public string? LastError => Volatile.Read(ref _lastError);
        public int InFlight => Volatile.Read(ref _inFlight);

        public int SourceCount { get { lock (_gate) { return _files.Count; } } }
        public int Cursor { get { lock (_gate) { return _cursor; } } }
        public int DeferredForBudget { get { lock (_gate) { return _deferredForBudget; } } }
        public int PeriodicRefreshes { get { lock (_gate) { return _periodicRefreshes; } } }

        /// <summary>true when every source file was visited and nothing is being read.</summary>
        public bool PassComplete
        {
            get { lock (_gate) { return _cursor >= _files.Count && _inFlight == 0; } }
        }

        /// <summary>Replaces the list of candidate files (highest priority first) and restarts the pass.</summary>
        public void SetSources(IEnumerable<string> files, IEnumerable<string>? prefetchOnly = null)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                if (!string.IsNullOrEmpty(f) && seen.Add(f)) list.Add(f);
            }
            var osOnly = new HashSet<string>(StringComparer.Ordinal);
            if (prefetchOnly != null)
            {
                foreach (string f in prefetchOnly)
                    if (seen.Contains(f)) osOnly.Add(f);
            }
            lock (_gate)
            {
                _files = list;
                _prefetchOnly = osOnly;
                Reset();
                _needsPass = false;
                _cooldownUntilMs = 0;
            }
        }

        /// <summary>Forgets what was read, so the next pass reads everything again.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _done.Clear();
                _warmed.Clear();
                _copyHashes.Clear();
                _oversize.Clear();
                // Reset means a fresh live-file snapshot: retained native copies must
                // be released before the same paths can be committed again.
                _store.ReleaseAll();
                RestartPass();
            }
        }

        private void RestartPass()
        {
            _cursor = 0;
            _deferredForBudget = 0;
            _needsPass = false;
            _maintenancePass = false;
            _passRecorded = false;
            Interlocked.Increment(ref _generation);
        }

        /// <summary>Interrupts active reads at their next chunk and prevents old copies from committing.</summary>
        public void Suspend() => _paused = true;

        /// <summary>Revoke the last physical memory allowance while the governor measures and reallocates it.</summary>
        public void CloseAdmission()
        {
            lock (_gate) { _admissionOpen = false; }
        }

        /// <summary>
        /// Schedules more reads. Called periodically from the control thread.
        /// <paramref name="limitBytes"/> is the most the store may hold right now.
        /// </summary>
        public void Pump(bool active, bool hold, long limitBytes, bool allowCopies = true,
            long refreshIntervalMs = 0)
        {
            lock (_gate)
            {
                _limit = limitBytes;
                _admissionOpen = active && limitBytes > 0;
                if (_hold != hold)
                {
                    _hold = hold;
                    Reset();
                }
                bool readCopies = hold && allowCopies;
                if (_readCopies != readCopies)
                {
                    _readCopies = readCopies;
                    RestartPass();
                }
                _paused = !active;
            }
            if (!active) return;

            long now = _clockMs();
            int maxInFlight = Math.Max(2, _pool.ActiveWorkers * 2);
            while (true)
            {
                string path;
                int generation;
                bool copy;
                lock (_gate)
                {
                    if (_cursor >= _files.Count)
                    {
                        if (_inFlight == 0)
                        {
                            RecordPassFinishedLocked(now);
                            // Memory release / formerly deferred copies take precedence over
                            // optional OS-only maintenance. Preserve the existing cooldown.
                            if (now >= _cooldownUntilMs
                                && (_needsPass || (_deferredForBudget > 0
                                    && limitBytes >= _limitAtPassStart + RetryGrowthBytes)))
                            {
                                _cursor = 0;
                                _deferredForBudget = 0;
                                _needsPass = false;
                                _maintenancePass = false;
                                _passRecorded = false;
                                _limitAtPassStart = limitBytes;
                                _cooldownUntilMs = now + RetryCooldownMs;
                                continue;
                            }
                            if (!_needsPass && refreshIntervalMs > 0 && _passRecorded
                                && now >= _passCompletedAtMs
                                && now - _passCompletedAtMs >= refreshIntervalMs)
                            {
                                // Re-read the real assets into the OS file cache, including
                                // already-held files, without allocating a second native copy.
                                _cursor = 0;
                                _warmed.Clear();
                                _maintenancePass = true;
                                _passRecorded = false;
                                continue;
                            }
                        }
                        return;
                    }
                    if (_inFlight >= maxInFlight) return;
                    if (_cursor == 0) _limitAtPassStart = limitBytes;
                    path = _files[_cursor++];
                    copy = !_maintenancePass && _readCopies
                        && !_prefetchOnly.Contains(path) && !_oversize.Contains(path);
                    if (copy ? _done.Contains(path) : _warmed.Contains(path)) continue;
                    _inFlight++;
                    generation = _generation;
                }
                _pool.Enqueue(token => ReadOne(path, generation, copy, token));
            }
        }

        /// <summary>All callers hold _gate; completed work is counted once per pass.</summary>
        private void RecordPassFinishedLocked(long now)
        {
            if (_passRecorded || _inFlight != 0 || _cursor < _files.Count) return;
            if (_maintenancePass)
            {
                _maintenancePass = false;
                _periodicRefreshes++;
            }
            // A stale copy eviction can request another pass. Count the completed
            // OS refresh first, then obey the 30-second memory-release cooldown.
            if (_needsPass) return;
            _passRecorded = true;
            _passCompletedAtMs = now;
        }

        private void OnBlockReleased(string key)
        {
            lock (_gate)
            {
                _done.Remove(key);
                _copyHashes.Remove(key);
                _needsPass = true;
                _cooldownUntilMs = Math.Max(_cooldownUntilMs, _clockMs() + ReleaseCooldownMs);
            }
        }

        private void ReadOne(string path, int generation, bool copy, CancellationToken token)
        {
            bool completed = false;
            bool deferred = false;
            bool copied = false;
            bool warmedWithoutCopy = false;
            try
            {
                if (!MayRead(generation, token)) return;
                using SafeFileHandle handle = File.OpenHandle(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
                long length = RandomAccess.GetLength(handle);
                if (length <= 0)
                {
                    completed = true;
                    return;
                }
                if (copy && length > MaxFileBytes)
                {
                    // The copy is too large, but the file still needs to warm the OS cache.
                    Interlocked.Increment(ref _skippedLarge);
                    lock (_gate)
                    {
                        if (generation == _generation) _oversize.Add(path);
                    }
                    copy = false;
                }
                if (copy)
                {
                    if (!TryBeginCopy(length, generation, token, out IntPtr ptr, out deferred))
                    {
                        if (!deferred) return;
                        // Only the private copy lacks budget. The read permission still holds, so the
                        // real file is warmed in the OS cache now and the copy is retried later.
                        bool alreadyWarm;
                        lock (_gate) { alreadyWarm = generation == _generation && _warmed.Contains(path); }
                        if (alreadyWarm) return;
                        if (ReadAndDiscard(handle, length, generation, token, false, out _))
                        {
                            warmedWithoutCopy = true;
                            Interlocked.Increment(ref _filesRead);
                            Interlocked.Increment(ref _budgetFallbackWarms);
                        }
                        else deferred = false; // interrupted: let a later pass visit the file again
                        return;
                    }
                    bool ok = false;
                    string? fingerprint = null;
                    try
                    {
                        ok = ReadInto(handle, (byte*)ptr, length, generation, token, out fingerprint);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            completed = ok && MayCopy(generation, token) && _readCopies && _store.TotalBytes <= _limit;
                            if (completed)
                            {
                                _store.Commit(ptr, length, path);
                                if (fingerprint != null) _copyHashes[path] = fingerprint;
                                _done.Add(path);
                                _warmed.Add(path);
                                copied = true;
                            }
                            else _store.Abort(ptr, length);
                        }
                    }
                }
                else
                {
                    string? previousHash;
                    lock (_gate) { _copyHashes.TryGetValue(path, out previousHash); }
                    completed = ReadAndDiscard(handle, length, generation, token,
                        previousHash != null, out string? currentHash);
                    if (completed && previousHash != null
                        && currentHash != null
                        && !StringComparer.Ordinal.Equals(previousHash, currentHash))
                    {
                        lock (_gate)
                        {
                            // Do not evict a replacement that another worker has just
                            // committed. Only the original hash is eligible.
                            if (MayRead(generation, token)
                                && _copyHashes.TryGetValue(path, out string? latest)
                                && StringComparer.Ordinal.Equals(latest, previousHash))
                                _store.ReleaseKey(path);
                        }
                    }
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
                        // A copy may have been reclaimed between commit and this finally block.
                        if (completed && !copied)
                        {
                            if (copy) _done.Add(path);
                            else _warmed.Add(path);
                        }
                        if (warmedWithoutCopy) _warmed.Add(path);
                        if (deferred) _deferredForBudget++;
                        // Interrupted (paused or cancelled): make sure a later pass picks it up again.
                        if (!completed && !deferred) _needsPass = true;
                        RecordPassFinishedLocked(_clockMs());
                    }
                }
            }
        }

        private bool MayRead(int generation, CancellationToken token) =>
            !_paused && generation == Volatile.Read(ref _generation) && !token.IsCancellationRequested
            && (_mayRead?.Invoke() ?? true);

        private bool MayCopy(int generation, CancellationToken token) =>
            MayRead(generation, token) && (_mayCopy?.Invoke() ?? true);

        private bool TryBeginCopy(long length, int generation, CancellationToken token, out IntPtr ptr, out bool deferred)
        {
            lock (_gate)
            {
                ptr = IntPtr.Zero;
                deferred = false;
                if (!MayCopy(generation, token)) return false;
                // The gate stays closed only for the governor sample or when the limit is zero.
                if (!_admissionOpen && _limit > 0) return false;
                bool allocated = _store.TryBegin(length, _admissionOpen ? _limit : 0, out ptr);
                deferred = !allocated;
                return allocated;
            }
        }

        private bool ReadInto(SafeFileHandle handle, byte* dest, long length, int generation,
            CancellationToken token, out string? fingerprint)
        {
            fingerprint = null;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (offset < length)
            {
                if (!MayCopy(generation, token)) return false;
                int want = (int)Math.Min(ChunkBytes, length - offset);
                int got = RandomAccess.Read(handle, new Span<byte>(dest + offset, want), offset);
                if (got <= 0) return false; // file shrank while reading
                hash.AppendData(new ReadOnlySpan<byte>(dest + offset, got));
                offset += got;
                Interlocked.Add(ref _bytesRead, got);
            }
            fingerprint = Convert.ToHexString(hash.GetHashAndReset());
            return true;
        }

        private bool ReadAndDiscard(SafeFileHandle handle, long length, int generation,
            CancellationToken token, bool calculateHash, out string? fingerprint)
        {
            fingerprint = null;
            using IncrementalHash? hash = calculateHash
                ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
            try
            {
                long offset = 0;
                while (offset < length)
                {
                    if (!MayRead(generation, token)) return false;
                    int got = RandomAccess.Read(handle,
                        buffer.AsSpan(0, (int)Math.Min(ChunkBytes, length - offset)), offset);
                    if (got <= 0) return false;
                    hash?.AppendData(buffer, 0, got);
                    offset += got;
                    Interlocked.Add(ref _bytesRead, got);
                }
                if (hash != null)
                    fingerprint = Convert.ToHexString(hash.GetHashAndReset());
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
