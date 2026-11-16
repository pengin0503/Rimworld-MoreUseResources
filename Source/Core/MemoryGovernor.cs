using System;
using System.Threading;

namespace MoreUseResources.Core
{
    public enum MemoryGovernorState
    {
        Idle = 0,
        Growing = 1,
        AtTarget = 2,
        LimitedByFreeMemory = 3,
        Pressure = 4,
    }

    /// <summary>
    /// Decides how much native memory the mod may hold. The rule that always wins:
    /// the operating system must keep at least the configured amount of free physical memory.
    /// When it does not, the mod gives memory back first (reservation, then file copies).
    /// </summary>
    public sealed class MemoryGovernor
    {
        public const long ChunkBytes = 32L << 20;
        public const long MarginBytes = 256L << 20;
        public const long ReserveRateBytesPerSecond = 512L << 20;
        private const int MaxChunksPerTick = 4;
        private const long PressureHoldMs = 10_000;

        private readonly IMemoryProbe _probe;
        private readonly NativeBlockStore _cache;
        private readonly NativeBlockStore _reserve;
        private readonly Func<long> _clockMs;

        private double _reserveAllowance;
        private long _holdGrowthUntilMs;
        private long _pressureEvents;
        private long _cacheLimit;

        public MemoryGovernor(IMemoryProbe probe, NativeBlockStore cache, NativeBlockStore reserve, Func<long>? clockMs = null)
        {
            _probe = probe;
            _cache = cache;
            _reserve = reserve;
            _clockMs = clockMs ?? (() => Environment.TickCount64);
        }

        public MemoryStatus LastStatus { get; private set; }
        public MemoryGovernorState State { get; private set; }
        public long PressureEvents => Interlocked.Read(ref _pressureEvents);
        /// <summary>Most bytes the file cache store may hold right now.</summary>
        public long CacheLimitBytes => Interlocked.Read(ref _cacheLimit);

        public void Tick(ResourceSettings s, ResourceActivation ram, double dtSeconds, CancellationToken token = default, Func<bool>? mayReserve = null)
        {
            long pendingAtSample = _cache.PendingBytes;
            MemoryStatus st = _probe.Read();
            LastStatus = st;
            long now = _clockMs();

            bool featureOn = s.Enabled && s.RamEnabled;
            bool wantCache = featureOn && s.RamFileCache && s.RamHoldCopies;
            bool wantReserve = featureOn && s.RamReserve;
            bool releaseInactive = !ram.Active && s.ReleaseWhenInactive;
            long cacheBudget = s.RamFileCacheMiB * ResourceSettings.MiB;
            long reserveTarget = s.RamReserveMiB * ResourceSettings.MiB;

            // 1. Settings-driven release.
            if (!wantCache || releaseInactive) _cache.ReleaseAll();
            else if (_cache.CommittedBytes > cacheBudget) _cache.Release(_cache.CommittedBytes - cacheBudget);

            if (!wantReserve || releaseInactive) _reserve.ReleaseAll();
            else if (_reserve.CommittedBytes > reserveTarget) _reserve.Release(_reserve.CommittedBytes - reserveTarget);

            // 2. Pressure-driven release. Synthetic reservation goes first, real data second.
            long minFree = s.RamMinFreeMiB * ResourceSettings.MiB;
            long headroom = st.FromOperatingSystem ? st.AvailablePhysical - minFree : -long.MaxValue;
            bool pressure = false;
            if (headroom < 0 && (_reserve.CommittedBytes > 0 || _cache.CommittedBytes > 0))
            {
                long deficit = -headroom;
                long freed = _reserve.Release(deficit);
                if (freed < deficit) freed += _cache.Release(deficit - freed);
                if (freed > 0)
                {
                    Interlocked.Increment(ref _pressureEvents);
                    _holdGrowthUntilMs = now + PressureHoldMs;
                    pressure = true;
                    headroom = 0; // wait for the next OS sample before treating freed bytes as available
                }
            }

            bool mayGrow = ram.Active && st.FromOperatingSystem && now >= _holdGrowthUntilMs;
            long growRoom = Math.Max(0, headroom - MarginBytes);

            // An in-flight allocation can still touch physical pages after the OS sample.
            growRoom = Math.Max(0, growRoom - pendingAtSample);

            // Reserve and file copies share this single physical headroom budget.
            bool reserveGrew = false;
            bool reserveBlocked = false;
            if (wantReserve && mayGrow)
            {
                _reserveAllowance = Math.Min(
                    _reserveAllowance + ReserveRateBytesPerSecond * Math.Max(0.0, dtSeconds) * ram.Intensity,
                    ChunkBytes * (double)MaxChunksPerTick);
                int chunks = 0;
                while (chunks < MaxChunksPerTick && _reserveAllowance >= ChunkBytes && !token.IsCancellationRequested
                    && (mayReserve?.Invoke() ?? true))
                {
                    long held = _reserve.TotalBytes;
                    if (held >= reserveTarget) break;
                    long size = Math.Min(ChunkBytes, reserveTarget - held);
                    if (growRoom < size)
                    {
                        reserveBlocked = true;
                        break;
                    }
                    if (!_reserve.TryBegin(size, reserveTarget, out IntPtr ptr))
                    {
                        reserveBlocked = true;
                        break;
                    }
                    NativeBlockStore.Touch(ptr, size, token);
                    if (token.IsCancellationRequested || !(mayReserve?.Invoke() ?? true))
                    {
                        _reserve.Abort(ptr, size);
                        break;
                    }
                    _reserve.Commit(ptr, size, null);
                    growRoom -= size;
                    _reserveAllowance -= size;
                    chunks++;
                    reserveGrew = true;
                }
            }
            else
            {
                _reserveAllowance = 0;
            }

            long cacheHeld = _cache.TotalBytes;
            long cacheLimit = wantCache && mayGrow ? Math.Min(cacheBudget, cacheHeld + growRoom) : 0;
            Interlocked.Exchange(ref _cacheLimit, cacheLimit);

            // State for diagnostics.
            bool cacheWantsMore = wantCache && cacheHeld < cacheBudget;
            bool reserveWantsMore = wantReserve && _reserve.TotalBytes < reserveTarget;
            if (pressure) State = MemoryGovernorState.Pressure;
            else if (!wantCache && !wantReserve) State = MemoryGovernorState.Idle;
            else if (!ram.Active) State = MemoryGovernorState.Idle;
            else if (reserveGrew) State = MemoryGovernorState.Growing;
            else if (!st.FromOperatingSystem || reserveBlocked || (cacheWantsMore && cacheLimit <= cacheHeld) || now < _holdGrowthUntilMs)
                State = (cacheWantsMore || reserveWantsMore) ? MemoryGovernorState.LimitedByFreeMemory : MemoryGovernorState.AtTarget;
            else if (cacheWantsMore || reserveWantsMore) State = MemoryGovernorState.Growing;
            else State = MemoryGovernorState.AtTarget;
        }
    }
}
