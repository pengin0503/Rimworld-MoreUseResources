using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MoreUseResources.Core
{
    public readonly struct VramInfo
    {
        public readonly long UsageBytes;
        public readonly long BudgetBytes;
        public readonly bool DriverReported;

        public VramInfo(long usage, long budget, bool driverReported)
        {
            UsageBytes = usage;
            BudgetBytes = budget;
            DriverReported = driverReported;
        }

        /// <summary>Highest device-local usage this mod will push towards.</summary>
        public long CapBytes(int targetPercent, long keepFreeBytes)
        {
            long byPercent = BudgetBytes / 100 * Math.Clamp(targetPercent, 0, ResourceSettings.MaxGpuTargetPercent);
            long byFree = BudgetBytes - keepFreeBytes;
            return Math.Max(0, Math.Min(byPercent, byFree));
        }

        internal bool CanPrewarmTexture(int width, int height, int targetPercent, long keepFreeBytes, int maxDimension = 1024)
        {
            if (!DriverReported || UsageBytes < 0 || BudgetBytes <= 0 || width < 1 || height < 1
                || maxDimension is < 1 or > 4096 || width > maxDimension || height > maxDimension)
                return false;

            // Conservative allowance for the image, mip chain, alignment and driver block growth.
            long estimate = (long)width * height * 16 + 32 * ResourceSettings.MiB;
            return CapBytes(targetPercent, keepFreeBytes) - UsageBytes >= estimate;
        }
    }

    public enum PrewarmAdmission
    {
        Upload = 0,
        Defer = 1,
        Reject = 2,
    }

    /// <summary>
    /// Decide candidate admission without touching the graphics engine. Budget pressure is
    /// temporary (defer), whereas a texture too large to ever fit under the cap is rejected.
    /// </summary>
    public static class PrewarmAdmissionPolicy
    {
        public static PrewarmAdmission Evaluate(VramInfo info, int width, int height,
            int targetPercent, long keepFreeBytes, int maxDimension = 1024)
        {
            if (maxDimension is < 1 or > 4096 || width < 1 || height < 1
                || width > maxDimension || height > maxDimension)
                return PrewarmAdmission.Reject;
            if (!info.DriverReported || info.BudgetBytes <= 0)
                return PrewarmAdmission.Defer;
            // The same conservative allowance is used both for the theoretical cap and
            // the live driver usage, so a permanently oversized texture cannot stall scans.
            var empty = new VramInfo(0, info.BudgetBytes, true);
            if (!empty.CanPrewarmTexture(width, height, targetPercent, keepFreeBytes, maxDimension))
                return PrewarmAdmission.Reject;
            return info.CanPrewarmTexture(width, height, targetPercent, keepFreeBytes, maxDimension)
                ? PrewarmAdmission.Upload : PrewarmAdmission.Defer;
        }
    }

    /// <summary>Bounded FIFO of assets postponed until driver memory is available.</summary>
    public sealed class PrewarmDeferredQueue<T> where T : class
    {
        private readonly Queue<T> _items = new Queue<T>();
        private readonly int _capacity;

        public PrewarmDeferredQueue(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        public int Count => _items.Count;
        public long Dropped { get; private set; }

        public bool Defer(T item)
        {
            if (_items.Count >= _capacity)
            {
                Dropped++;
                return false;
            }
            _items.Enqueue(item);
            return true;
        }

        public T? Take() => _items.Count == 0 ? null : _items.Dequeue();

        public void Clear()
        {
            _items.Clear();
            Dropped = 0;
        }
    }

    /// <summary>Everything the VRAM features need from the engine. Implemented game-side; faked in tests.</summary>
    public interface IVramBackend<T> where T : class
    {
        bool TryGetMemory(out VramInfo info);
        void CollectTextures(List<T> into);
        bool IsUsable(T texture);
        bool IsResident(T texture);
        /// <summary>Asks the engine to upload the texture exactly as it would when first drawing it.</summary>
        bool EnsureResident(T texture);
        void Evict(T texture);
    }

    public enum VramState
    {
        Inactive = 0,
        Scanning = 1,
        Uploading = 2,
        AtTarget = 3,
        Complete = 4,
        Unavailable = 5,
        Faulted = 6,
        Releasing = 7,
    }

    /// <summary>
    /// Uploads game textures to VRAM ahead of their first use, a few per frame, on the main thread.
    /// It only ever asks the engine to do what the engine does by itself on first draw, and it stops
    /// below the engine's own eviction watermark so the two never fight.
    /// </summary>
    public sealed class VramPrewarmer<T> where T : class
    {
        public const long HysteresisBytes = 256L << 20;
        private const int MemoryCheckEvery = 8;
        private const int MaxSkipsPerFrame = 4000;
        private const int MaxConsecutiveFaults = 3;
        private const double RescanSeconds = 30;
        private const double MaintenancePauseSeconds = 10;

        private readonly IVramBackend<T> _backend;
        private readonly List<T> _textures = new List<T>();
        private readonly HashSet<T> _ours = new HashSet<T>(ReferenceEqualityComparer.Instance);
        private readonly List<T> _releaseQueue = new List<T>();
        private readonly Stopwatch _sw = new Stopwatch();

        private int _cursor;
        private bool _scanned;
        private double _sinceScan;
        private double _pause;
        private bool _atTarget;
        private int _consecutiveFaults;
        private bool _releasing;

        public VramPrewarmer(IVramBackend<T> backend)
        {
            _backend = backend;
        }

        public VramState State { get; private set; }
        public string? FaultMessage { get; private set; }
        public VramInfo LastInfo { get; private set; }
        public int KnownTextures => _textures.Count;
        public int Cursor => _cursor;
        public long Uploaded { get; private set; }
        public long NotUploadable { get; private set; }
        public long PassesCompleted { get; private set; }
        public int TrackedOurs => _ours.Count + _releaseQueue.Count;
        public bool Faulted => State == VramState.Faulted;

        /// <summary>Clears a fault so the feature may try again (user action).</summary>
        public void ClearFault()
        {
            if (State == VramState.Faulted)
            {
                State = VramState.Inactive;
                FaultMessage = null;
                _consecutiveFaults = 0;
            }
        }

        /// <summary>Forces a fresh texture list on the next step (after a load or mod content reload).</summary>
        public void Invalidate()
        {
            _scanned = false;
            _textures.Clear();
            _cursor = 0;
        }

        /// <summary>Starts evicting, a few per frame, every texture this mod uploaded.</summary>
        public void BeginRelease()
        {
            if (_ours.Count == 0) return;
            _releaseQueue.AddRange(_ours);
            _ours.Clear();
            _releasing = true;
        }

        public void NoteInactive()
        {
            if (State != VramState.Faulted && State != VramState.Unavailable && !_releasing)
            {
                State = VramState.Inactive;
            }
        }

        /// <summary>Runs at most <paramref name="budgetMs"/> of work. Returns the number of uploads done.</summary>
        public int Step(double dtSeconds, int targetPercent, long keepFreeBytes, double budgetMs)
        {
            if (State == VramState.Faulted) return 0;
            _sw.Restart();
            try
            {
                if (_releasing)
                {
                    StepRelease(budgetMs);
                    return 0;
                }
                if (!_backend.TryGetMemory(out VramInfo info) || info.BudgetBytes <= 0)
                {
                    State = VramState.Unavailable;
                    return 0;
                }
                LastInfo = info;

                _sinceScan += dtSeconds;
                if (!_scanned || (_cursor >= _textures.Count && _sinceScan >= RescanSeconds))
                {
                    _textures.Clear();
                    _backend.CollectTextures(_textures);
                    _scanned = true;
                    _sinceScan = 0;
                    _cursor = 0;
                    State = VramState.Scanning;
                    return 0;
                }

                long cap = info.CapBytes(targetPercent, keepFreeBytes);
                if (_atTarget)
                {
                    if (info.UsageBytes < cap - HysteresisBytes) _atTarget = false;
                }
                else if (info.UsageBytes >= cap)
                {
                    _atTarget = true;
                }
                if (_atTarget)
                {
                    State = VramState.AtTarget;
                    return 0;
                }

                if (_cursor >= _textures.Count)
                {
                    _pause += dtSeconds;
                    State = VramState.Complete;
                    if (_pause < MaintenancePauseSeconds) return 0;
                    // Maintenance pass: re-upload what the engine evicted meanwhile.
                    _pause = 0;
                    _cursor = 0;
                    if (_textures.Count == 0) return 0;
                }

                State = VramState.Uploading;
                int uploads = 0;
                int skips = 0;
                while (_cursor < _textures.Count)
                {
                    if (_sw.Elapsed.TotalMilliseconds >= budgetMs) break;
                    T tex = _textures[_cursor++];
                    if (tex == null || !_backend.IsUsable(tex) || _backend.IsResident(tex))
                    {
                        if (++skips >= MaxSkipsPerFrame) break;
                        continue;
                    }
                    if (_backend.EnsureResident(tex))
                    {
                        uploads++;
                        Uploaded++;
                        _ours.Add(tex);
                        if (uploads % MemoryCheckEvery == 0)
                        {
                            if (!_backend.TryGetMemory(out info)) break;
                            LastInfo = info;
                            if (info.UsageBytes >= cap)
                            {
                                _atTarget = true;
                                break;
                            }
                        }
                    }
                    else
                    {
                        NotUploadable++;
                    }
                }
                if (_cursor >= _textures.Count)
                {
                    PassesCompleted++;
                    _pause = 0;
                }
                _consecutiveFaults = 0;
                return uploads;
            }
            catch (Exception ex)
            {
                // Skip the offending texture; repeated failures switch the feature off for good.
                if (++_consecutiveFaults >= MaxConsecutiveFaults)
                {
                    State = VramState.Faulted;
                    FaultMessage = ex.GetType().Name + ": " + ex.Message;
                }
                return 0;
            }
        }

        private void StepRelease(double budgetMs)
        {
            State = VramState.Releasing;
            while (_releaseQueue.Count > 0)
            {
                if (_sw.Elapsed.TotalMilliseconds >= budgetMs) return;
                T tex = _releaseQueue[_releaseQueue.Count - 1];
                _releaseQueue.RemoveAt(_releaseQueue.Count - 1);
                if (tex != null && _backend.IsUsable(tex) && _backend.IsResident(tex))
                {
                    _backend.Evict(tex);
                }
            }
            _releasing = false;
            _cursor = _textures.Count;
            _pause = 0;
            State = VramState.Inactive;
        }
    }

    /// <summary>Engine-side operations for the optional VRAM reservation.</summary>
    public interface IVramReserveBackend
    {
        long ChunkBytes { get; }
        object? AllocateChunk();
        void FreeChunk(object chunk);
    }

    /// <summary>
    /// Optional, synthetic: holds empty GPU images so that VRAM usage rises to a chosen level.
    /// It is useless to the game by design. It allocates at most one chunk per frame and gives
    /// everything back as soon as usage passes the cap, so game textures always take precedence.
    /// </summary>
    public sealed class VramReserve
    {
        private const int MaxConsecutiveFaults = 2;
        private readonly IVramReserveBackend _backend;
        private readonly List<object> _chunks = new List<object>();
        private int _faults;

        public VramReserve(IVramReserveBackend backend)
        {
            _backend = backend;
        }

        public long HeldBytes => _chunks.Count * _backend.ChunkBytes;
        public int ChunkCount => _chunks.Count;
        public bool Faulted { get; private set; }
        public string? FaultMessage { get; private set; }

        public void ClearFault()
        {
            Faulted = false;
            FaultMessage = null;
            _faults = 0;
        }

        /// <summary>One step per frame. <paramref name="grow"/> false only holds or shrinks.</summary>
        public void Step(bool grow, long targetBytes, VramInfo info, int targetPercent, long keepFreeBytes)
        {
            long chunk = _backend.ChunkBytes;
            long cap = info.CapBytes(targetPercent, keepFreeBytes);
            try
            {
                // Over the cap: the game needs the room. Give back up to four chunks per frame.
                if (info.UsageBytes > cap + chunk)
                {
                    long excess = info.UsageBytes - cap;
                    int n = (int)Math.Min(4, Math.Min(_chunks.Count, (excess + chunk - 1) / chunk));
                    Release(n);
                    return;
                }
                if (HeldBytes > targetBytes)
                {
                    Release((int)Math.Min(4, (HeldBytes - targetBytes + chunk - 1) / chunk));
                    return;
                }
                if (!grow || Faulted) return;
                if (HeldBytes + chunk > targetBytes) return;
                if (info.UsageBytes + chunk > cap) return;
                object? c = _backend.AllocateChunk();
                if (c == null)
                {
                    Fault("allocation returned nothing");
                    return;
                }
                _chunks.Add(c);
                _faults = 0;
            }
            catch (Exception ex)
            {
                Fault(ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Fault(string message)
        {
            if (++_faults >= MaxConsecutiveFaults)
            {
                Faulted = true;
                FaultMessage = message;
            }
        }

        public void Release(int count)
        {
            for (int i = 0; i < count && _chunks.Count > 0; i++)
            {
                object c = _chunks[_chunks.Count - 1];
                _chunks.RemoveAt(_chunks.Count - 1);
                try { _backend.FreeChunk(c); } catch (Exception) { }
            }
        }

        public void ReleaseAll() => Release(_chunks.Count);
    }
}
