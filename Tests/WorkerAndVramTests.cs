using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    public class WorkerPoolTests
    {
        private static void WaitFor(Func<bool> cond, int ms = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (!cond() && sw.ElapsedMilliseconds < ms) Thread.Sleep(5);
            Assert.True(cond());
        }

        [Fact]
        public void Runs_jobs_and_survives_failures()
        {
            using var pool = new WorkerPool(4);
            int ran = 0;
            pool.Configure(2, true, false, 50, ThreadPriority.Lowest);
            for (int i = 0; i < 50; i++) pool.Enqueue(_ => Interlocked.Increment(ref ran));
            pool.Enqueue(_ => throw new InvalidOperationException("boom"));
            pool.Enqueue(_ => Interlocked.Increment(ref ran));
            WaitFor(() => ran == 51 && pool.JobsFailed == 1);
            Assert.Contains("boom", pool.LastError);
            Assert.Equal(0, pool.PendingJobs);
        }

        [Fact]
        public void Zero_active_workers_means_nothing_runs()
        {
            using var pool = new WorkerPool(2);
            int ran = 0;
            pool.Configure(1, true, false, 50, ThreadPriority.Lowest);
            pool.Enqueue(_ => Interlocked.Increment(ref ran));
            WaitFor(() => ran == 1);
            pool.Configure(0, true, true, 100, ThreadPriority.Lowest);
            Thread.Sleep(150);
            long busy = pool.BusyTicks;
            pool.Enqueue(_ => Interlocked.Increment(ref ran));
            Thread.Sleep(250);
            Assert.Equal(1, ran);
            Assert.Equal(busy, pool.BusyTicks);
            pool.Configure(1, true, false, 50, ThreadPriority.Lowest);
            WaitFor(() => ran == 2);
        }

        [Fact]
        public void Jobs_can_be_held_back_while_synthetic_load_runs()
        {
            using var pool = new WorkerPool(2);
            int ran = 0;
            pool.Configure(1, false, true, 20, ThreadPriority.Lowest);
            pool.Enqueue(_ => Interlocked.Increment(ref ran));
            Thread.Sleep(200);
            Assert.Equal(0, ran);
            Assert.True(pool.BusyTicks > 0);
            pool.Configure(1, true, false, 20, ThreadPriority.Lowest);
            WaitFor(() => ran == 1);
        }

        [Fact]
        public void Synthetic_load_respects_duty_cycle_roughly()
        {
            using var pool = new WorkerPool(1);
            pool.Configure(1, false, true, 25, ThreadPriority.Lowest);
            Thread.Sleep(100);
            long b0 = pool.BusyTicks; long t0 = Stopwatch.GetTimestamp();
            Thread.Sleep(600);
            double share = (pool.BusyTicks - b0) / (double)(Stopwatch.GetTimestamp() - t0);
            Assert.InRange(share, 0.05, 0.45);
        }

        [Fact]
        public void Dispose_stops_quickly_and_cancels_running_jobs()
        {
            var pool = new WorkerPool(3);
            bool cancelled = false;
            pool.Configure(3, true, true, 100, ThreadPriority.Lowest);
            pool.Enqueue(t => { while (!t.IsCancellationRequested) Thread.Sleep(5); cancelled = true; });
            Thread.Sleep(100);
            var sw = Stopwatch.StartNew();
            pool.Dispose();
            Assert.True(sw.ElapsedMilliseconds < 2500);
            Assert.True(cancelled);
            pool.Enqueue(_ => throw new Exception("must not run"));
            pool.Configure(3, true, true, 100, ThreadPriority.Lowest);
            Thread.Sleep(100);
            Assert.Equal(0, pool.JobsFailed);
            pool.Dispose();
        }
    }

    internal sealed class Tex
    {
        public long Size = 10L << 20;
        public bool Resident, Usable = true, CanUpload = true, Throws;
    }

    internal sealed class FakeVram : IVramBackend<Tex>, IVramReserveBackend
    {
        public List<Tex> All = new List<Tex>();
        public long Budget = 16L << 30;
        public long OtherUsage = 1L << 30;
        public bool Available = true;
        public int EnsureCalls, Evictions, Allocs, Frees;
        public long ReserveHeld;
        public bool FailAlloc;

        public long Usage
        {
            get { long u = OtherUsage + ReserveHeld; foreach (Tex t in All) if (t.Resident) u += t.Size; return u; }
        }

        public bool TryGetMemory(out VramInfo info) { info = new VramInfo(Usage, Budget, true); return Available; }
        public void CollectTextures(List<Tex> into) => into.AddRange(All);
        public bool IsUsable(Tex t) => t.Usable;
        public bool IsResident(Tex t) => t.Resident;
        public bool EnsureResident(Tex t)
        {
            EnsureCalls++;
            if (t.Throws) throw new InvalidOperationException("driver");
            if (!t.CanUpload) return false;
            t.Resident = true;
            return true;
        }
        public void Evict(Tex t) { t.Resident = false; Evictions++; }

        public long ChunkBytes => 16L << 20;
        public object? AllocateChunk() { if (FailAlloc) return null; Allocs++; ReserveHeld += ChunkBytes; return new object(); }
        public void FreeChunk(object chunk) { Frees++; ReserveHeld -= ChunkBytes; }
    }

    public class VramPrewarmerTests
    {
        private const long MiB = 1L << 20;

        private static FakeVram Backend(int count)
        {
            var b = new FakeVram();
            for (int i = 0; i < count; i++) b.All.Add(new Tex());
            return b;
        }

        private static void Run(VramPrewarmer<Tex> w, int steps, int pct = 60, long keepFree = 2048 * MiB)
        {
            for (int i = 0; i < steps; i++) w.Step(0.016, pct, keepFree, 50);
        }

        [Fact]
        public void Uploads_everything_when_there_is_room()
        {
            FakeVram b = Backend(100);
            b.All[3].Usable = false; b.All[4].CanUpload = false; b.All[5].Resident = true;
            var w = new VramPrewarmer<Tex>(b);
            Run(w, 20);
            Assert.Equal(VramState.Complete, w.State);
            Assert.Equal(97, w.Uploaded);
            Assert.Equal(1, w.NotUploadable);
            Assert.False(b.All[3].Resident);
            Assert.Equal(1, w.PassesCompleted);
        }

        [Fact]
        public void Stops_at_the_cap_and_resumes_only_after_hysteresis()
        {
            FakeVram b = Backend(2000);           // 20 GiB of textures, 16 GiB budget
            var w = new VramPrewarmer<Tex>(b);
            Run(w, 400);
            long cap = new VramInfo(0, b.Budget, true).CapBytes(60, 2048 * MiB);
            Assert.Equal(VramState.AtTarget, w.State);
            Assert.InRange(b.Usage, cap, cap + 8 * 10 * MiB);
            long uploaded = w.Uploaded;
            b.OtherUsage -= 100 * MiB;            // small drop: still inside hysteresis
            Run(w, 50);
            Assert.Equal(uploaded, w.Uploaded);
            b.OtherUsage -= 400 * MiB;
            Run(w, 50);
            Assert.True(w.Uploaded > uploaded);
        }

        [Fact]
        public void Keep_free_margin_wins_over_percentage()
        {
            var info = new VramInfo(0, 4096 * MiB, true);
            Assert.Equal(2048 * MiB, info.CapBytes(75, 2048 * MiB));
            Assert.Equal(0, info.CapBytes(75, 8192 * MiB));
            Assert.Equal(4096 * MiB / 100 * 75, info.CapBytes(99, 0));
        }

        [Theory]
        [InlineData(true, 1024, 6000, true)]
        [InlineData(false, 1024, 6000, false)]
        [InlineData(true, 2048, 6000, false)]
        [InlineData(true, 1024, 6130, false)]
        public void Live_texture_upload_needs_driver_headroom(bool driverReported, int width, int usageMiB, bool expected)
        {
            var info = new VramInfo(usageMiB * MiB, 10_240 * MiB, driverReported);
            Assert.Equal(expected, info.CanPrewarmTexture(width, 1024, 60, 2048 * MiB));
        }

        [Fact]
        public void Gpu_admission_defers_large_texture_but_allows_smaller_one()
        {
            const long MiB = 1L << 20;
            var info = new VramInfo(6144 * MiB - 39 * MiB, 10240 * MiB, true);
            Assert.Equal(PrewarmAdmission.Defer,
                PrewarmAdmissionPolicy.Evaluate(info, 1024, 1024, 60, 2048 * MiB));
            Assert.Equal(PrewarmAdmission.Upload,
                PrewarmAdmissionPolicy.Evaluate(info, 128, 128, 60, 2048 * MiB));
        }

        [Fact]
        public void Gpu_admission_rejects_impossible_or_invalid_candidates()
        {
            const long MiB = 1L << 20;
            var info = new VramInfo(0, 128 * MiB, true);
            Assert.Equal(PrewarmAdmission.Reject,
                PrewarmAdmissionPolicy.Evaluate(info, 64, 64, 10, 0));
            Assert.Equal(PrewarmAdmission.Reject,
                PrewarmAdmissionPolicy.Evaluate(info, 2048, 2048, 60, 0));
            Assert.Equal(PrewarmAdmission.Defer,
                PrewarmAdmissionPolicy.Evaluate(new VramInfo(0, 10240 * MiB, false),
                    64, 64, 60, 0));
        }

        [Fact]
        public void Gpu_backlog_is_bounded_and_fifo()
        {
            var backlog = new PrewarmDeferredQueue<Tex>(2);
            Tex large = new Tex(); Tex small = new Tex(); Tex extra = new Tex();
            Assert.True(backlog.Defer(large));
            Assert.True(backlog.Defer(small));
            Assert.False(backlog.Defer(extra));
            Assert.Equal(1, backlog.Dropped);
            Assert.Equal(2, backlog.Count);
            Assert.Same(large, backlog.Take());
            Assert.Same(small, backlog.Take());
            Assert.Null(backlog.Take());
            backlog.Defer(extra);
            backlog.Clear();
            Assert.Equal(0, backlog.Count);
        }

        [Fact]
        public void Time_budget_limits_work_per_frame()
        {
            FakeVram b = Backend(5000);
            var w = new VramPrewarmer<Tex>(b);
            w.Step(0.016, 60, 0, 50);             // scan
            w.Step(0.016, 60, 0, 0);              // zero budget: nothing
            Assert.Equal(0, w.Uploaded);
        }

        [Fact]
        public void Repeated_engine_errors_switch_the_feature_off()
        {
            FakeVram b = Backend(10);
            foreach (Tex t in b.All) t.Throws = true;
            var w = new VramPrewarmer<Tex>(b);
            Run(w, 10);
            Assert.True(w.Faulted);
            Assert.Contains("driver", w.FaultMessage);
            int calls = b.EnsureCalls;
            Run(w, 10);
            Assert.Equal(calls, b.EnsureCalls);
            Assert.Equal(3, calls);
            w.ClearFault();
            Assert.False(w.Faulted);
        }

        [Fact]
        public void Unavailable_backend_does_nothing()
        {
            FakeVram b = Backend(10);
            b.Available = false;
            var w = new VramPrewarmer<Tex>(b);
            Run(w, 5);
            Assert.Equal(VramState.Unavailable, w.State);
            Assert.Equal(0, b.EnsureCalls);
        }

        [Fact]
        public void Release_evicts_only_what_this_mod_uploaded()
        {
            FakeVram b = Backend(50);
            b.All[0].Resident = true;
            var w = new VramPrewarmer<Tex>(b);
            Run(w, 10);
            Assert.Equal(49, w.TrackedOurs);
            w.BeginRelease();
            do { w.Step(0.016, 60, 2048 * MiB, 50); } while (w.State == VramState.Releasing);
            Assert.Equal(49, b.Evictions);
            Assert.True(b.All[0].Resident);
            Assert.Equal(0, w.TrackedOurs);
            Assert.Equal(VramState.Inactive, w.State);
        }

        [Fact]
        public void Maintenance_pass_restores_evicted_textures()
        {
            FakeVram b = Backend(20);
            var w = new VramPrewarmer<Tex>(b);
            Run(w, 10);
            b.All[7].Resident = false;
            for (int i = 0; i < 12; i++) w.Step(1.0, 60, 0, 50);
            Assert.True(b.All[7].Resident);
            Assert.Equal(20, w.TrackedOurs);      // no duplicates
        }
    }

    public class TextureRefreshScheduleTests
    {
        [Fact]
        public void Refresh_only_after_completed_pass_and_elapsed_interval()
        {
            var schedule = new TextureRefreshSchedule();
            Assert.False(schedule.ShouldRefresh(10_000, 30));
            schedule.MarkPassCompleted(10_000);
            Assert.Equal(1, schedule.PassesCompleted);
            Assert.False(schedule.ShouldRefresh(39_999, 30));
            Assert.True(schedule.ShouldRefresh(40_000, 30));
            schedule.BeginNextPass();
            Assert.False(schedule.ShouldRefresh(60_000, 30));
            schedule.MarkPassCompleted(60_000);
            Assert.Equal(2, schedule.PassesCompleted);
            Assert.False(schedule.ShouldRefresh(89_999, 30));
            Assert.True(schedule.ShouldRefresh(90_000, 30));
        }

        [Fact]
        public void Refresh_can_be_disabled_and_survives_clock_regression()
        {
            var schedule = new TextureRefreshSchedule();
            schedule.MarkPassCompleted(100_000);
            Assert.False(schedule.ShouldRefresh(999_999, 0));
            Assert.False(schedule.ShouldRefresh(5_000, 30));
            Assert.False(schedule.ShouldRefresh(34_999, 30));
            Assert.True(schedule.ShouldRefresh(35_000, 30));
        }

        [Fact]
        public void Invalidating_scan_does_not_reset_lifetime_pass_count()
        {
            var schedule = new TextureRefreshSchedule();
            schedule.MarkPassCompleted(0);
            schedule.BeginNextPass();
            schedule.MarkPassCompleted(50_000);
            Assert.Equal(2, schedule.PassesCompleted);
            schedule.BeginNextPass();
            Assert.False(schedule.ShouldRefresh(100_000, 30));
            schedule.MarkPassCompleted(101_000);
            Assert.Equal(3, schedule.PassesCompleted);
        }
    }

    public class VramReserveTests
    {
        private const long MiB = 1L << 20;

        [Fact]
        public void Grows_one_chunk_per_step_up_to_target_and_cap()
        {
            var b = new FakeVram();
            var r = new VramReserve(b);
            for (int i = 0; i < 3; i++) { b.TryGetMemory(out VramInfo info); r.Step(true, 64 * MiB, info, 60, 0); }
            Assert.Equal(3, r.ChunkCount);
            for (int i = 0; i < 10; i++) { b.TryGetMemory(out VramInfo info); r.Step(true, 64 * MiB, info, 60, 0); }
            Assert.Equal(64 * MiB, r.HeldBytes);

            b.OtherUsage = 14L << 30;             // game now needs the memory
            for (int i = 0; i < 3; i++) { b.TryGetMemory(out VramInfo info); r.Step(true, 64 * MiB, info, 60, 0); }
            Assert.Equal(0, r.HeldBytes);
            Assert.Equal(b.Allocs, b.Frees);
        }

        [Fact]
        public void Does_not_grow_when_told_to_hold_and_shrinks_to_lower_target()
        {
            var b = new FakeVram();
            var r = new VramReserve(b);
            for (int i = 0; i < 8; i++) { b.TryGetMemory(out VramInfo info); r.Step(true, 128 * MiB, info, 60, 0); }
            Assert.Equal(128 * MiB, r.HeldBytes);
            { b.TryGetMemory(out VramInfo info); r.Step(false, 32 * MiB, info, 60, 0); }
            { b.TryGetMemory(out VramInfo info); r.Step(false, 32 * MiB, info, 60, 0); }
            Assert.Equal(32 * MiB, r.HeldBytes);
            r.ReleaseAll();
            Assert.Equal(0, b.ReserveHeld);
        }

        [Fact]
        public void Allocation_failures_fault_the_feature()
        {
            var b = new FakeVram { FailAlloc = true };
            var r = new VramReserve(b);
            for (int i = 0; i < 5; i++) { b.TryGetMemory(out VramInfo info); r.Step(true, 64 * MiB, info, 60, 0); }
            Assert.True(r.Faulted);
            Assert.Equal(0, r.ChunkCount);
        }
    }

    public class ControllerTests
    {
        private sealed class CallbackProbe : IMemoryProbe
        {
            public Action? DuringSample;

            public MemoryStatus Read()
            {
                DuringSample?.Invoke();
                return new MemoryStatus(32L << 30, 20L << 30, true);
            }
        }

        [Fact]
        public void Release_during_a_control_tick_does_not_leave_new_reservations()
        {
            var probe = new CallbackProbe();
            var settings = new ResourceSettings { RamReserve = true, RamReserveMiB = 128 };
            using var controller = new ResourceController(probe, 4, initial: settings);
            controller.ControlTick(true, 0.25);
            Assert.True(controller.GetStatus().ReserveHeldBytes > 0);
            probe.DuringSample = () =>
            {
                controller.Manual = ManualMode.Paused;
                controller.RequestReleaseMemory();
            };

            controller.ControlTick(true, 0.25);

            Assert.Equal(0, controller.GetStatus().ReserveHeldBytes);
        }

        [Fact]
        public void A_save_starting_after_the_phase_sample_blocks_reservation_growth()
        {
            var probe = new CallbackProbe();
            bool saving = false;
            var settings = new ResourceSettings { RamReserve = true, RamReserveMiB = 128 };
            using var controller = new ResourceController(probe, 4, () => saving, settings);
            probe.DuringSample = () => saving = true;

            controller.ControlTick(true, 0.25);

            Assert.Equal(0, controller.GetStatus().ReserveHeldBytes);
        }

        [Fact]
        public void End_to_end_phase_changes_start_and_stop_work()
        {
            var probe = new FakeProbe();
            bool saving = false;
            var settings = new ResourceSettings { CpuSyntheticLoad = true, CpuMaxWorkers = 2, RamFileCache = false };
            using var c = new ResourceController(probe, 4, () => saving, settings);
            var playing = new RawGameSignals { PlayDataLoaded = true, Stage = ProgramStage.Playing, Focused = true };
            c.PublishSignals(playing);
            c.ControlTick(true, 0.25);
            Assert.Equal(GamePhase.PlayingNormal, c.CurrentPlan.Phase.Phase);
            ControllerStatus st = c.GetStatus();
            Assert.Equal(1, st.WorkersActive);    // 2 workers at 50 %
            Assert.True(st.SyntheticLoad);

            saving = true;                         // observed by the probe, not by the main thread
            c.ControlTick(false, 0);
            Assert.Equal(GamePhase.Saving, c.CurrentPlan.Phase.Phase);
            Assert.Equal(0, c.GetStatus().WorkersActive);
            saving = false;

            playing.Paused = true;
            c.PublishSignals(playing);
            c.ControlTick(false, 0);
            Assert.Equal(2, c.GetStatus().WorkersActive);

            c.Manual = ManualMode.Paused;
            c.ControlTick(false, 0);
            Assert.Equal(0, c.GetStatus().WorkersActive);
            Assert.Equal(ActivationReason.PausedByUser, c.CurrentPlan.Cpu.Reason);
        }

        [Fact]
        public void Frame_guard_turns_gpu_off_and_halves_synthetic_load()
        {
            var settings = new ResourceSettings { CpuSyntheticLoad = true, CpuMaxWorkers = 4, RamFileCache = false };
            using var c = new ResourceController(new FakeProbe(), 8, null, settings);
            var s = new RawGameSignals { PlayDataLoaded = true, Stage = ProgramStage.Playing, Paused = true, Focused = true };
            c.PublishSignals(s);
            c.ControlTick(false, 0);
            Assert.True(c.CurrentPlan.Gpu.Active);
            for (int i = 0; i < 300; i++) c.ReportFrame(200);
            c.ControlTick(false, 0);
            Assert.Equal(ActivationReason.FrameGuard, c.CurrentPlan.Gpu.Reason);
            Assert.Equal(2, c.GetStatus().WorkersActive);
        }

        [Fact]
        public void Shutdown_releases_everything_and_is_idempotent()
        {
            var settings = new ResourceSettings { RamReserve = true, RamReserveMiB = 64, CpuSyntheticLoad = true };
            var c = new ResourceController(new FakeProbe(), 4, null, settings);
            c.PublishSignals(new RawGameSignals { PlayDataLoaded = true, Stage = ProgramStage.Playing, Paused = true, Focused = true });
            c.Start();
            var sw = Stopwatch.StartNew();
            while (c.GetStatus().ReserveHeldBytes < (64L << 20) && sw.ElapsedMilliseconds < 5000) Thread.Sleep(20);
            Assert.Equal(64L << 20, c.GetStatus().ReserveHeldBytes);
            Assert.True(c.GetStatus().WorkersActive > 0);
            c.Shutdown();
            c.Shutdown();
            ControllerStatus st = c.GetStatus();
            Assert.Equal(0, st.ReserveHeldBytes);
            Assert.Equal(0, st.WorkersActive);
            Assert.False(st.Plan.Cpu.Active);
        }

        [Fact]
        public void Service_worker_reads_files_when_cpu_feature_is_off()
        {
            var settings = new ResourceSettings { CpuEnabled = false };
            using var c = new ResourceController(new FakeProbe(), 4, null, settings);
            c.PublishSignals(new RawGameSignals { PlayDataLoaded = true, Stage = ProgramStage.Entry, Focused = true });
            c.ControlTick(false, 0);
            ControllerStatus st = c.GetStatus();
            Assert.Equal(1, st.WorkersActive);
            Assert.False(st.SyntheticLoad);
            Assert.False(st.Plan.Cpu.Active);
        }

        [Fact]
        public void Meminfo_parser()
        {
            Assert.True(SystemMemoryProbe.TryParseMeminfo("MemTotal:       16000000 kB\nMemFree: 1 kB\nMemAvailable:    8000000 kB\n", out long t, out long a));
            Assert.Equal(16000000L * 1024, t);
            Assert.Equal(8000000L * 1024, a);
            Assert.False(SystemMemoryProbe.TryParseMeminfo("garbage", out _, out _));
            MemoryStatus real = new SystemMemoryProbe().Read();
            Assert.True(real.TotalPhysical > 0 && real.AvailablePhysical > 0 && real.AvailablePhysical <= real.TotalPhysical);
        }
    }
}
