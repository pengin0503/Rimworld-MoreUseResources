using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace MoreUseResources.Core
{
    /// <summary>
    /// Dedicated low-priority background threads. They never touch game objects: jobs are plain
    /// delegates supplied by this mod (file reads) or a pure arithmetic loop (synthetic load).
    /// The game's own thread pool and job system are left alone.
    /// </summary>
    public sealed class WorkerPool : IDisposable
    {
        private const int SliceMs = 10;

        private readonly Thread?[] _threads;
        private readonly ConcurrentQueue<Action<CancellationToken>> _jobs = new ConcurrentQueue<Action<CancellationToken>>();
        private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);
        private readonly object _gate = new object();
        private readonly string _namePrefix;
        private readonly Func<bool>? _mayBurn;

        private CancellationTokenSource _jobCts = new CancellationTokenSource();
        private volatile bool _stop;
        private volatile int _activeWorkers;
        private volatile bool _synthetic;
        private volatile int _dutyPercent = 50;
        private volatile bool _jobsAllowed = true;
        private ThreadPriority _priority = ThreadPriority.Lowest;

        private long _busyTicks;
        private long _jobsCompleted;
        private long _jobsFailed;
        private int _pending;
        private int _runningJobs;
        private string? _lastError;

        public WorkerPool(int maxThreads, string namePrefix = "MUR-Worker", Func<bool>? mayBurn = null)
        {
            if (maxThreads < 1) maxThreads = 1;
            if (maxThreads > 256) maxThreads = 256;
            _threads = new Thread?[maxThreads];
            _namePrefix = namePrefix;
            _mayBurn = mayBurn;
        }

        public int MaxThreads => _threads.Length;
        public int ActiveWorkers => _activeWorkers;
        public bool SyntheticLoad => _synthetic;
        public int PendingJobs => Volatile.Read(ref _pending);
        public int RunningJobs => Volatile.Read(ref _runningJobs);
        public long JobsCompleted => Interlocked.Read(ref _jobsCompleted);
        public long JobsFailed => Interlocked.Read(ref _jobsFailed);
        public string? LastError => Volatile.Read(ref _lastError);
        /// <summary>Total time workers spent running jobs or synthetic load, in Stopwatch ticks.</summary>
        public long BusyTicks => Interlocked.Read(ref _busyTicks);

        /// <summary>Applies a new operating point. Safe to call from any thread, takes effect within one slice.</summary>
        public void Configure(int activeWorkers, bool jobsAllowed, bool synthetic, int dutyPercent, ThreadPriority priority)
        {
            if (_stop) return;
            activeWorkers = Math.Clamp(activeWorkers, 0, _threads.Length);
            lock (_gate)
            {
                if (_stop) return;
                if (priority != _priority)
                {
                    _priority = priority;
                    foreach (Thread? t in _threads)
                    {
                        if (t != null)
                        {
                            try { t.Priority = priority; } catch (ThreadStateException) { }
                        }
                    }
                }
                for (int i = 0; i < activeWorkers; i++)
                {
                    if (_threads[i] == null)
                    {
                        int index = i;
                        var t = new Thread(() => WorkerLoop(index))
                        {
                            IsBackground = true,
                            Name = _namePrefix + "-" + index,
                            Priority = priority,
                        };
                        _threads[i] = t;
                        t.Start();
                    }
                }
            }
            _dutyPercent = Math.Clamp(dutyPercent, 1, 100);
            _synthetic = synthetic;
            _jobsAllowed = jobsAllowed;
            _activeWorkers = activeWorkers;
            _wake.Set();
        }

        public void Enqueue(Action<CancellationToken> job)
        {
            if (_stop || job == null) return;
            Interlocked.Increment(ref _pending);
            _jobs.Enqueue(job);
            _wake.Set();
        }

        /// <summary>Drops queued jobs and signals cancellation to running ones.</summary>
        public void CancelJobs()
        {
            CancellationTokenSource old;
            lock (_gate)
            {
                old = _jobCts;
                _jobCts = new CancellationTokenSource();
            }
            try { old.Cancel(); } catch (ObjectDisposedException) { }
            while (_jobs.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _pending);
            }
        }

        private void WorkerLoop(int index)
        {
            var burn = new SyntheticBurn((uint)(index + 1));
            while (!_stop)
            {
                try
                {
                    if (index >= _activeWorkers)
                    {
                        Thread.Sleep(SliceMs);
                        continue;
                    }
                    if (_jobsAllowed && _jobs.TryDequeue(out Action<CancellationToken>? job))
                    {
                        Interlocked.Decrement(ref _pending);
                        RunJob(job);
                        continue;
                    }
                    if (_synthetic && (_mayBurn?.Invoke() ?? true))
                    {
                        BurnSlice(ref burn);
                        continue;
                    }
                    _wake.Reset();
                    if (_jobsAllowed && !_jobs.IsEmpty) continue;
                    _wake.Wait(100);
                }
                catch (ThreadInterruptedException)
                {
                    // fall through to the stop check
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _lastError, ex.GetType().Name + ": " + ex.Message);
                    Thread.Sleep(200);
                }
            }
        }

        private void RunJob(Action<CancellationToken> job)
        {
            CancellationToken token;
            lock (_gate) { token = _jobCts.Token; }
            long start = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _runningJobs);
            try
            {
                if (!token.IsCancellationRequested)
                {
                    job(token);
                    Interlocked.Increment(ref _jobsCompleted);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _jobsFailed);
                Volatile.Write(ref _lastError, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _runningJobs);
                Interlocked.Add(ref _busyTicks, Stopwatch.GetTimestamp() - start);
            }
        }

        private void BurnSlice(ref SyntheticBurn burn)
        {
            int duty = _dutyPercent;
            long sliceTicks = Stopwatch.Frequency * SliceMs / 1000;
            long workTicks = sliceTicks * duty / 100;
            long start = Stopwatch.GetTimestamp();
            long end = start + workTicks;
            while (Stopwatch.GetTimestamp() < end)
            {
                if (_stop || !_synthetic || !(_mayBurn?.Invoke() ?? true)) break;
                burn.Spin(2048);
            }
            long now = Stopwatch.GetTimestamp();
            Interlocked.Add(ref _busyTicks, now - start);
            int restMs = (int)((sliceTicks - (now - start)) * 1000 / Stopwatch.Frequency);
            if (restMs > 0)
            {
                Thread.Sleep(restMs);
            }
            else
            {
                // Even at 100% duty, give equal-priority threads a chance once per slice.
                Thread.Yield();
            }
        }

        public void Dispose()
        {
            Thread?[] snapshot;
            lock (_gate)
            {
                if (_stop) return;
                _stop = true;
                snapshot = (Thread?[])_threads.Clone();
            }
            _activeWorkers = 0;
            _synthetic = false;
            CancelJobs();
            _wake.Set();
            Thread current = Thread.CurrentThread;
            long deadline = Environment.TickCount64 + 1500;
            foreach (Thread? t in snapshot)
            {
                if (t != null && t != current)
                {
                    // Workers poll the stop flag at least every 100 ms; a job stuck in file IO is
                    // abandoned after the timeout (threads are background threads).
                    t.Join((int)Math.Max(0, deadline - Environment.TickCount64));
                }
            }
        }

        /// <summary>Allocation-free integer mixing. It has no purpose besides consuming CPU time.</summary>
        private struct SyntheticBurn
        {
            private ulong _state;

            public SyntheticBurn(uint seed)
            {
                _state = 0x9E3779B97F4A7C15UL * seed + 1;
            }

            public void Spin(int rounds)
            {
                ulong x = _state;
                for (int i = 0; i < rounds; i++)
                {
                    x ^= x >> 33;
                    x *= 0xFF51AFD7ED558CCDUL;
                    x ^= x >> 29;
                    x += 0x9E3779B97F4A7C15UL;
                }
                _state = x;
            }
        }
    }
}
