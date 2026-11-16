using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace MoreUseResources.Core
{
    /// <summary>Numbers shown in the status panel. A plain snapshot, safe to read on any thread.</summary>
    public sealed class ControllerStatus
    {
        public ActivationPlan Plan;
        public bool FrameThrottled;
        public double FrameAverageMs;

        public int WorkerThreads;
        public int WorkersActive;
        public bool SyntheticLoad;
        public int PendingJobs;
        public long JobsCompleted;
        public long JobsFailed;
        /// <summary>Share of all logical processors used by this mod's workers, 0..1, averaged over about a second.</summary>
        public double WorkerCpuShare;
        public string? WorkerLastError;
        public ProcessPriorityChoice ProcessPriorityApplied;
        public string? ProcessPriorityError;

        public MemoryGovernorState MemoryState;
        public MemoryStatus Memory;
        public long CacheHeldBytes;
        public long CacheLimitBytes;
        public long ReserveHeldBytes;
        public long PressureEvents;
        public long AllocationFailures;
        public int FileSources;
        public int FileCursor;
        public long FilesRead;
        public long FileBytesRead;
        public long FileErrors;
        public int FilesDeferredForBudget;
        public bool FilePassComplete;
        public string? FileLastError;
        public bool GcLowLatencyApplied;
        public string? GcError;
        public long ProcessWorkingSet;
        public long ProcessPrivateBytes;
        public long ManagedHeapBytes;

        public string? ControlError;
    }

    /// <summary>
    /// Owns every background activity of the mod and the single control thread that starts,
    /// throttles and stops it. The main thread only publishes facts about the game and reads the plan.
    /// </summary>
    public sealed class ResourceController : IDisposable
    {
        private const int TickMs = 50;
        private const int SlowEvery = 5;

        private readonly IMemoryProbe _probe;
        private readonly int _logicalProcessors;
        private readonly Func<bool>? _savingProbe;
        private readonly WorkerPool _pool;
        private readonly NativeBlockStore _cacheStore = new NativeBlockStore();
        private readonly NativeBlockStore _reserveStore = new NativeBlockStore();
        private readonly MemoryGovernor _governor;
        private readonly FileCache _fileCache;
        private readonly GcTuner _gc = new GcTuner();
        private readonly ProcessPriorityTuner _priority = new ProcessPriorityTuner();
        private readonly FrameGuard _frameGuard = new FrameGuard();
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();

        private Thread? _thread;
        private volatile ResourceSettings _settings;
        private RawGameSignals _signals;
        private ActivationPlan _plan;
        private volatile bool _throttled;
        private volatile int _manual;
        private volatile bool _stopping;
        private volatile bool _releaseRequested;
        private bool _disposed;
        private string? _controlError;

        private long _lastBusyTicks;
        private long _lastShareStamp;
        private double _workerShare;
        private long _privateBytes;
        private long _lastPrivateStamp;

        public ResourceController(IMemoryProbe probe, int logicalProcessors, Func<bool>? savingProbe = null, ResourceSettings? initial = null)
        {
            _probe = probe;
            _logicalProcessors = Math.Max(1, logicalProcessors);
            _savingProbe = savingProbe;
            _settings = (initial ?? new ResourceSettings()).Clone();
            _settings.Clamp();
            _pool = new WorkerPool(_logicalProcessors);
            _governor = new MemoryGovernor(probe, _cacheStore, _reserveStore);
            _fileCache = new FileCache(_cacheStore, _pool);
            _signals = new RawGameSignals { Focused = true };
            _plan = ActivationPlanner.Plan(_settings, new PhaseSnapshot(GamePhase.Startup, PhaseModifiers.None), ManualMode.Auto);
        }

        public ManualMode Manual
        {
            get => (ManualMode)_manual;
            set => _manual = (int)value;
        }

        public bool IsRunning => _thread != null && !_stopping;

        public ActivationPlan CurrentPlan
        {
            get { lock (_gate) { return _plan; } }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_thread != null || _disposed) return;
                _thread = new Thread(ControlLoop)
                {
                    IsBackground = true,
                    Name = "MUR-Control",
                    Priority = ThreadPriority.BelowNormal,
                };
                _thread.Start();
            }
        }

        /// <summary>Hands over a private copy of the settings. The copy is clamped here.</summary>
        public void UpdateSettings(ResourceSettings copy)
        {
            copy.Clamp();
            _settings = copy;
        }

        /// <summary>Main thread: latest facts about the game.</summary>
        public void PublishSignals(in RawGameSignals signals)
        {
            lock (_gate) { _signals = signals; }
        }

        /// <summary>Main thread: one frame time sample. Only meaningful phases feed the guard.</summary>
        public void ReportFrame(double frameMs)
        {
            ActivationPlan plan = CurrentPlan;
            GamePhase p = plan.Phase.Phase;
            bool steady = p == GamePhase.MainMenu || p == GamePhase.PlayingNormal || p == GamePhase.PlayingFast || p == GamePhase.Paused;
            if (!steady)
            {
                _frameGuard.Reset();
            }
            else
            {
                _frameGuard.Sample(frameMs, _settings.FrameGuardMs);
            }
            _throttled = _frameGuard.Throttled;
        }

        public bool FrameThrottled => _throttled;
        public double FrameAverageMs => _frameGuard.AverageMs;

        public void SetFileSources(IEnumerable<string> files) => _fileCache.SetSources(files);

        /// <summary>User action: give back all held memory now. Growth resumes by the normal rules.</summary>
        public void RequestReleaseMemory() => _releaseRequested = true;

        /// <summary>Runs one control step synchronously. Used by tests and by the loop.</summary>
        public void ControlTick(bool slow, double slowDtSeconds)
        {
            ResourceSettings s = _settings;
            RawGameSignals signals;
            lock (_gate) { signals = _signals; }
            if (_stopping) signals.Quitting = true;
            if (_savingProbe != null)
            {
                try { signals.Saving |= _savingProbe(); } catch (Exception) { }
            }

            PhaseSnapshot phase = PhaseResolver.Resolve(signals, s.IdleBoost ? s.IdleSeconds : 0);
            ActivationPlan plan = ActivationPlanner.Plan(s, phase, Manual);
            bool throttled = _throttled && s.FrameGuardMs > 0;
            if (throttled && plan.Gpu.Active)
            {
                plan = new ActivationPlan(plan.Phase, plan.Cpu, plan.Ram, ResourceActivation.Off(ActivationReason.FrameGuard));
            }
            lock (_gate) { _plan = plan; }

            // ---- CPU / workers ----
            int maxWorkers = s.EffectiveMaxWorkers(_logicalProcessors);
            int workers = plan.Cpu.Active ? Math.Max(1, (int)Math.Round(maxWorkers * plan.Cpu.Intensity)) : 0;
            bool synthetic = plan.Cpu.Active && s.CpuSyntheticLoad;
            if (synthetic && throttled) workers = Math.Max(1, workers / 2);
            bool fileWork = plan.Ram.Active && s.RamFileCache;
            if (fileWork && workers == 0) workers = 1; // one service worker so the RAM feature works without the CPU feature
            ThreadPriority priority = s.CpuWorkerPriority == WorkerPriority.BelowNormal ? ThreadPriority.BelowNormal : ThreadPriority.Lowest;
            _pool.Configure(workers, fileWork, synthetic, s.CpuSyntheticDutyPercent, priority);

            if (!slow) return;

            // ---- RAM ----
            if (_releaseRequested)
            {
                _releaseRequested = false;
                _cacheStore.ReleaseAll();
                _reserveStore.ReleaseAll();
            }
            _governor.Tick(s, plan.Ram, slowDtSeconds, _shutdownCts.Token);
            _fileCache.Pump(fileWork, s.RamHoldCopies, s.RamHoldCopies ? _governor.CacheLimitBytes : 0);
            _gc.Apply(plan.Ram.Active && s.RamGcLowLatency);
            _priority.Apply(s.Enabled && s.CpuEnabled && phase.Phase != GamePhase.Quitting ? s.ProcessPriority : ProcessPriorityChoice.Unchanged);

            // ---- measurements ----
            long now = Stopwatch.GetTimestamp();
            long busy = _pool.BusyTicks;
            if (_lastShareStamp != 0 && now > _lastShareStamp)
            {
                double share = (busy - _lastBusyTicks) / ((double)(now - _lastShareStamp) * _logicalProcessors);
                _workerShare = _workerShare * 0.75 + Math.Clamp(share, 0, 1) * 0.25;
            }
            _lastBusyTicks = busy;
            _lastShareStamp = now;
        }

        private void ControlLoop()
        {
            int n = 0;
            long lastSlow = Stopwatch.GetTimestamp();
            while (!_stopping)
            {
                try
                {
                    bool slow = ++n % SlowEvery == 0;
                    double dt = 0;
                    if (slow)
                    {
                        long now = Stopwatch.GetTimestamp();
                        dt = Math.Min(2.0, (now - lastSlow) / (double)Stopwatch.Frequency);
                        lastSlow = now;
                    }
                    ControlTick(slow, dt);
                    _controlError = null;
                }
                catch (Exception ex)
                {
                    // Never let the control thread die; with no plan updates everything would keep its last state.
                    _controlError = ex.GetType().Name + ": " + ex.Message;
                    try { _pool.Configure(0, false, false, 1, ThreadPriority.Lowest); } catch (Exception) { }
                }
                Thread.Sleep(TickMs);
            }
        }

        public ControllerStatus GetStatus()
        {
            long now = Environment.TickCount64;
            if (now - _lastPrivateStamp > 1000)
            {
                _lastPrivateStamp = now;
                try
                {
                    using Process p = Process.GetCurrentProcess();
                    _privateBytes = p.PrivateMemorySize64;
                }
                catch (Exception)
                {
                    _privateBytes = 0;
                }
            }
            return new ControllerStatus
            {
                Plan = CurrentPlan,
                FrameThrottled = _throttled,
                FrameAverageMs = _frameGuard.AverageMs,
                WorkerThreads = _pool.MaxThreads,
                WorkersActive = _pool.ActiveWorkers,
                SyntheticLoad = _pool.SyntheticLoad,
                PendingJobs = _pool.PendingJobs,
                JobsCompleted = _pool.JobsCompleted,
                JobsFailed = _pool.JobsFailed,
                WorkerCpuShare = _workerShare,
                WorkerLastError = _pool.LastError,
                ProcessPriorityApplied = _priority.Applied,
                ProcessPriorityError = _priority.LastError,
                MemoryState = _governor.State,
                Memory = _governor.LastStatus,
                CacheHeldBytes = _cacheStore.CommittedBytes,
                CacheLimitBytes = _governor.CacheLimitBytes,
                ReserveHeldBytes = _reserveStore.CommittedBytes,
                PressureEvents = _governor.PressureEvents,
                AllocationFailures = _cacheStore.AllocationFailures + _reserveStore.AllocationFailures,
                FileSources = _fileCache.SourceCount,
                FileCursor = _fileCache.Cursor,
                FilesRead = _fileCache.FilesRead,
                FileBytesRead = _fileCache.BytesRead,
                FileErrors = _fileCache.Errors,
                FilesDeferredForBudget = _fileCache.DeferredForBudget,
                FilePassComplete = _fileCache.PassComplete,
                FileLastError = _fileCache.LastError,
                GcLowLatencyApplied = _gc.Applied,
                GcError = _gc.LastError,
                ProcessWorkingSet = Environment.WorkingSet,
                ProcessPrivateBytes = _privateBytes,
                ManagedHeapBytes = GC.GetTotalMemory(false),
                ControlError = _controlError,
            };
        }

        /// <summary>Stops everything and gives every resource back. Safe to call more than once and from any thread.</summary>
        public void Shutdown()
        {
            Thread? thread;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _stopping = true;
                thread = _thread;
            }
            try { _shutdownCts.Cancel(); } catch (Exception) { }
            if (thread != null && thread != Thread.CurrentThread)
            {
                thread.Join(1500);
            }
            _pool.Dispose();
            _gc.Apply(false);
            _priority.Apply(ProcessPriorityChoice.Unchanged);
            _cacheStore.Dispose();
            _reserveStore.Dispose();
            lock (_gate)
            {
                _plan = ActivationPlanner.Plan(_settings, new PhaseSnapshot(GamePhase.Quitting, PhaseModifiers.None), ManualMode.Auto);
            }
        }

        public void Dispose() => Shutdown();
    }
}
