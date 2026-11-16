using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    internal sealed class FakeProbe : IMemoryProbe
    {
        public long Total = 32L << 30;
        public long Available = 20L << 30;
        public MemoryStatus Read() => new MemoryStatus(Total, Available, true);
    }

    public class NativeBlockStoreTests
    {
        [Fact]
        public void Limit_counts_pending_blocks()
        {
            using var store = new NativeBlockStore();
            Assert.True(store.TryBegin(600, 1000, out IntPtr a));
            Assert.False(store.TryBegin(600, 1000, out _));
            Assert.Equal(600, store.PendingBytes);
            store.Commit(a, 600, "a");
            Assert.Equal(600, store.CommittedBytes);
            Assert.Equal(0, store.PendingBytes);
            Assert.True(store.TryBegin(400, 1000, out IntPtr b));
            store.Abort(b, 400);
            Assert.Equal(600, store.TotalBytes);
        }

        [Fact]
        public void Release_is_newest_first_and_reports_keys()
        {
            using var store = new NativeBlockStore();
            var released = new List<string>();
            store.BlockReleased += released.Add;
            foreach (string k in new[] { "a", "b", "c" })
            {
                Assert.True(store.TryBegin(100, long.MaxValue, out IntPtr p));
                NativeBlockStore.Touch(p, 100);
                store.Commit(p, 100, k);
            }
            Assert.Equal(200, store.Release(150));
            Assert.Equal(new[] { "c", "b" }, released);
            Assert.Equal(100, store.CommittedBytes);
            Assert.Equal(100, store.ReleaseAll());
            Assert.Equal(0, store.BlockCount);
        }

        [Fact]
        public void Commit_after_dispose_frees_instead_of_leaking()
        {
            var store = new NativeBlockStore();
            Assert.True(store.TryBegin(4096, long.MaxValue, out IntPtr p));
            store.Dispose();
            store.Commit(p, 4096, "late");
            Assert.Equal(0, store.TotalBytes);
            Assert.False(store.TryBegin(1, long.MaxValue, out _));
        }
    }

    public class MemoryGovernorTests
    {
        private static readonly ResourceActivation On = new ResourceActivation(true, 1f, ActivationReason.Active);
        private const long MiB = 1L << 20;

        private static (MemoryGovernor g, FakeProbe p, NativeBlockStore cache, NativeBlockStore reserve, long[] clock) Make()
        {
            var probe = new FakeProbe();
            var cache = new NativeBlockStore();
            var reserve = new NativeBlockStore();
            long[] clock = { 0 };
            return (new MemoryGovernor(probe, cache, reserve, () => clock[0]), probe, cache, reserve, clock);
        }

        [Fact]
        public void Reserve_grows_to_target_at_limited_rate()
        {
            var (g, _, _, reserve, _) = Make();
            var s = new ResourceSettings { RamReserve = true, RamReserveMiB = 128, RamFileCache = false };
            g.Tick(s, On, 0.25);
            Assert.InRange(reserve.CommittedBytes, 1, 128 * MiB);
            for (int i = 0; i < 10; i++) g.Tick(s, On, 0.25);
            Assert.Equal(128 * MiB, reserve.CommittedBytes);
            Assert.Equal(MemoryGovernorState.AtTarget, g.State);
            reserve.Dispose();
        }

        [Fact]
        public void Never_grows_into_the_free_memory_floor()
        {
            var (g, probe, _, reserve, _) = Make();
            var s = new ResourceSettings { RamReserve = true, RamReserveMiB = 1024, RamMinFreeMiB = 6144, RamFileCache = false };
            probe.Available = 6144 * MiB + 100 * MiB; // less than margin + chunk above the floor
            for (int i = 0; i < 5; i++) g.Tick(s, On, 0.25);
            Assert.Equal(0, reserve.CommittedBytes);
            Assert.Equal(MemoryGovernorState.LimitedByFreeMemory, g.State);
        }

        [Fact]
        public void File_copies_and_reserve_share_one_headroom_budget()
        {
            var (g, probe, cache, reserve, _) = Make();
            using (cache)
            using (reserve)
            {
                var settings = new ResourceSettings { RamReserve = true, RamReserveMiB = 64, RamFileCacheMiB = 512 };
                long headroom = 96 * MiB;
                probe.Available = settings.RamMinFreeMiB * MiB + MemoryGovernor.MarginBytes + headroom;

                g.Tick(settings, On, 0.25);

                Assert.Equal(64 * MiB, reserve.TotalBytes);
                Assert.Equal(headroom - reserve.TotalBytes, g.CacheLimitBytes);
            }
        }

        [Fact]
        public void Pressure_releases_reserve_before_cache_and_pauses_growth()
        {
            var (g, probe, cache, reserve, clock) = Make();
            var s = new ResourceSettings { RamReserve = true, RamReserveMiB = 64, RamFileCacheMiB = 1024 };
            for (int i = 0; i < 6; i++) g.Tick(s, On, 0.25);
            Assert.Equal(64 * MiB, reserve.CommittedBytes);
            Assert.True(cache.TryBegin(48 * MiB, long.MaxValue, out IntPtr p));
            cache.Commit(p, 48 * MiB, "file");

            probe.Available = s.RamMinFreeMiB * MiB - 80 * MiB; // 80 MiB below the floor
            g.Tick(s, On, 0.25);
            Assert.Equal(MemoryGovernorState.Pressure, g.State);
            Assert.Equal(0, reserve.CommittedBytes);          // 64 MiB of reserve went first
            Assert.Equal(0, cache.CommittedBytes);            // remaining 16 MiB took the cache block
            Assert.Equal(1, g.PressureEvents);

            probe.Available = 20L << 30;
            g.Tick(s, On, 0.25);
            Assert.Equal(0, reserve.CommittedBytes);          // growth is held for a while after pressure
            Assert.Equal(0, g.CacheLimitBytes);
            clock[0] += 11_000;
            g.Tick(s, On, 0.25);
            Assert.True(reserve.CommittedBytes > 0);
            Assert.Equal(1024 * MiB, g.CacheLimitBytes);
            reserve.Dispose(); cache.Dispose();
        }

        [Fact]
        public void Disabling_or_lowering_releases()
        {
            var (g, _, cache, reserve, _) = Make();
            var s = new ResourceSettings { RamReserve = true, RamReserveMiB = 96 };
            for (int i = 0; i < 8; i++) g.Tick(s, On, 0.25);
            Assert.Equal(96 * MiB, reserve.CommittedBytes);
            s.RamReserveMiB = 64;
            g.Tick(s, On, 0.25);
            Assert.True(reserve.CommittedBytes <= 64 * MiB);
            s.RamReserve = false;
            g.Tick(s, On, 0.25);
            Assert.Equal(0, reserve.CommittedBytes);

            Assert.True(cache.TryBegin(MiB, long.MaxValue, out IntPtr p));
            cache.Commit(p, MiB, "f");
            s.RamEnabled = false;
            g.Tick(s, ResourceActivation.Off(ActivationReason.ResourceOff), 0.25);
            Assert.Equal(0, cache.CommittedBytes);
        }

        [Fact]
        public void Inactive_phase_holds_unless_release_option_is_set()
        {
            var (g, _, _, reserve, _) = Make();
            var s = new ResourceSettings { RamReserve = true, RamReserveMiB = 64 };
            for (int i = 0; i < 6; i++) g.Tick(s, On, 0.25);
            ResourceActivation off = ResourceActivation.Off(ActivationReason.PhaseOff);
            g.Tick(s, off, 0.25);
            Assert.Equal(64 * MiB, reserve.CommittedBytes);
            Assert.Equal(reserve.CommittedBytes == 0 ? 0 : g.CacheLimitBytes, g.CacheLimitBytes);
            s.ReleaseWhenInactive = true;
            g.Tick(s, off, 0.25);
            Assert.Equal(0, reserve.CommittedBytes);
        }
    }

    public class FileCacheTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mur-tests-" + Guid.NewGuid().ToString("N"));

        public FileCacheTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private string MakeFile(string name, int size)
        {
            string path = Path.Combine(_dir, name);
            var data = new byte[size];
            new Random(size).NextBytes(data);
            File.WriteAllBytes(path, data);
            return path;
        }

        private static void PumpUntil(FileCache cache, Func<bool> done, bool hold, long limit)
        {
            for (int i = 0; i < 400 && !done(); i++)
            {
                cache.Pump(true, hold, limit);
                Thread.Sleep(10);
            }
            Assert.True(done(), "file cache did not finish in time");
        }

        [Fact]
        public void Holds_files_within_budget_and_defers_the_rest()
        {
            string a = MakeFile("a.bin", 300_000), b = MakeFile("b.bin", 500_000), c = MakeFile("c.bin", 400_000);
            using var pool = new WorkerPool(2);
            using var store = new NativeBlockStore();
            pool.Configure(1, true, false, 50, ThreadPriority.Lowest);
            var cache = new FileCache(store, pool);
            cache.SetSources(new[] { a, b, c, Path.Combine(_dir, "missing.bin") });
            PumpUntil(cache, () => cache.PassComplete, true, 1_000_000);
            Assert.Equal(800_000, store.CommittedBytes);     // a + b fit, c does not
            Assert.Equal(1, cache.DeferredForBudget);
            Assert.Equal(1, cache.Errors);                   // the missing file
            Assert.Equal(2, cache.FilesRead);
        }

        [Fact]
        public void Replacing_sources_releases_old_copies_and_rereads_shared_paths()
        {
            string path = MakeFile("asset.bin", 1000);
            using var pool = new WorkerPool(1);
            using var store = new NativeBlockStore();
            pool.Configure(1, true, false, 50, ThreadPriority.Lowest);
            var cache = new FileCache(store, pool);
            cache.SetSources(new[] { path });
            PumpUntil(cache, () => cache.PassComplete, true, 4096);

            File.WriteAllBytes(path, new byte[2000]);
            cache.SetSources(new[] { path });

            Assert.Equal(0, store.TotalBytes);
            PumpUntil(cache, () => cache.PassComplete, true, 4096);
            Assert.Equal(2000, store.TotalBytes);
            Assert.Equal(2, cache.FilesRead);
        }

        [Fact]
        public void Asset_discovery_limits_reads_to_known_content_directories_and_extensions()
        {
            Directory.CreateDirectory(Path.Combine(_dir, "Defs"));
            Directory.CreateDirectory(Path.Combine(_dir, "Saves"));
            string def = MakeFile(Path.Combine("Defs", "Thing.xml"), 8);
            MakeFile(Path.Combine("Saves", "Player.xml"), 8);
            MakeFile(Path.Combine("Defs", "Save.rws"), 8);
            List<string> paths = GameAssetFiles.Discover(new[] { _dir, _dir }, CancellationToken.None, () => true);
            Assert.Equal(new[] { def }, paths);
        }

        [Fact]
        public void Cpu_only_prefetch_can_warm_files_without_growing_or_releasing_existing_copies()
        {
            string first = MakeFile("first.bin", 1000);
            string second = MakeFile("second.bin", 1000);
            using var pool = new WorkerPool(1);
            using var store = new NativeBlockStore();
            pool.Configure(1, true, false, 50, ThreadPriority.Lowest);
            var cache = new FileCache(store, pool);
            cache.SetSources(new[] { first, second });
            PumpUntil(cache, () => cache.PassComplete, true, 1000);
            Assert.Equal(1000, store.TotalBytes);
            Assert.Equal(1, cache.FilesRead);

            cache.Pump(true, true, 0, allowCopies: false);
            Assert.True(SpinWait.SpinUntil(() => cache.PassComplete, 3000));

            Assert.Equal(1000, store.TotalBytes);
            Assert.Equal(2, cache.FilesRead);
            cache.Pump(true, true, 2000);
            PumpUntil(cache, () => cache.PassComplete, true, 2000);
            Assert.Equal(2000, store.TotalBytes);
        }

        [Fact]
        public void Files_stay_replaceable_while_cached()
        {
            string a = MakeFile("a.bin", 100_000);
            using var pool = new WorkerPool(1);
            using var store = new NativeBlockStore();
            pool.Configure(1, true, false, 50, ThreadPriority.Lowest);
            var cache = new FileCache(store, pool);
            cache.SetSources(new[] { a });
            PumpUntil(cache, () => cache.PassComplete, true, long.MaxValue);
            File.WriteAllBytes(a, new byte[10]); // would throw if a handle or mapping were kept
            File.Delete(a);
            Assert.Equal(100_000, store.CommittedBytes);
        }

        [Fact]
        public void Warm_only_mode_reads_without_holding()
        {
            string a = MakeFile("a.bin", 2_500_000);
            using var pool = new WorkerPool(1);
            using var store = new NativeBlockStore();
            pool.Configure(1, true, false, 50, ThreadPriority.Lowest);
            var cache = new FileCache(store, pool);
            cache.SetSources(new[] { a });
            PumpUntil(cache, () => cache.PassComplete, false, 0);
            Assert.Equal(0, store.TotalBytes);
            Assert.Equal(2_500_000, cache.BytesRead);
            Assert.Equal(1, cache.FilesRead);
        }

        [Fact]
        public void Periodic_OS_cache_refresh_reads_real_assets_without_duplicating_native_copies()
        {
            string path = MakeFile("real-texture.png", 250_000);
            long now = 1000;
            using var pool = new WorkerPool(1);
            using var store = new NativeBlockStore();
            pool.Configure(1, true, false, 50, ThreadPriority.Lowest);
            var cache = new FileCache(store, pool, clockMs: () => Interlocked.Read(ref now));
            cache.SetSources(new[] { path });
            PumpUntil(cache, () => cache.PassComplete && cache.FilesRead == 1, true, 1_000_000);
            Assert.Equal(250_000, store.CommittedBytes);
            Assert.Equal(0, cache.PeriodicRefreshes);

            // Disk image changes after initial copy. A refresh must read the real new file
            // into the operating system's cache, never duplicate the held native buffer.
            File.WriteAllBytes(path, new byte[300_000]);
            Interlocked.Exchange(ref now, 1000 + 5 * 60_000 - 1);
            for (int i = 0; i < 10; i++)
                cache.Pump(true, true, 1_000_000, refreshIntervalMs: 5 * 60_000);
            Assert.Equal(1, cache.FilesRead);

            Interlocked.Increment(ref now);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                cache.Pump(true, true, 1_000_000, refreshIntervalMs: 5 * 60_000);
                Thread.Sleep(5);
                return cache.PeriodicRefreshes == 1;
            }, TimeSpan.FromSeconds(10)));
            Assert.Equal(2, cache.FilesRead);
            Assert.Equal(250_000, store.CommittedBytes);
            Assert.Equal(550_000, cache.BytesRead);
            Assert.True(cache.PassComplete);
        }

        [Fact]
        public void Warm_only_mode_reloads_at_each_interval_without_retaining_RimWorld_RAM()
        {
            string path = MakeFile("asset.xml", 64_000);
            long now = 1000;
            using var pool = new WorkerPool(2);
            using var store = new NativeBlockStore();
            pool.Configure(2, true, false, 50, ThreadPriority.Lowest);
            var cache = new FileCache(store, pool, clockMs: () => Interlocked.Read(ref now));
            cache.SetSources(new[] { path });
            PumpUntil(cache, () => cache.PassComplete && cache.FilesRead == 1, false, 0);
            Interlocked.Exchange(ref now, 1000 + 60_000);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                cache.Pump(true, false, 0, refreshIntervalMs: 60_000);
                Thread.Sleep(5);
                return cache.PeriodicRefreshes == 1;
            }, TimeSpan.FromSeconds(10)));
            Assert.Equal(2, cache.FilesRead);
            Assert.Equal(128_000, cache.BytesRead);
            Assert.Equal(0, store.TotalBytes);
        }

        [Fact]
        public void Zero_refresh_interval_never_requeues_completed_file_reads()
        {
            string path = MakeFile("disabled.bin", 64_000);
            long now = 1000;
            using var pool = new WorkerPool(1);
            using var store = new NativeBlockStore();
            pool.Configure(1, true, false, 50, ThreadPriority.Lowest);
            var cache = new FileCache(store, pool, clockMs: () => Interlocked.Read(ref now));
            cache.SetSources(new[] { path });
            PumpUntil(cache, () => cache.PassComplete && cache.FilesRead == 1, false, 0);
            Interlocked.Exchange(ref now, 100_000_000);
            for (int i = 0; i < 10; i++)
                cache.Pump(true, false, 0, refreshIntervalMs: 0);
            Assert.Equal(1, cache.FilesRead);
            Assert.Equal(0, cache.PeriodicRefreshes);
        }

        [Fact]
        public void Periodic_maintenance_waits_until_resource_activation_resumes()
        {
            string path = MakeFile("paused-asset.xml", 30_000);
            long now = 0;
            using var pool = new WorkerPool(1);
            using var store = new NativeBlockStore();
            pool.Configure(1, true, false, 50, ThreadPriority.Lowest);
            var cache = new FileCache(store, pool, clockMs: () => Interlocked.Read(ref now));
            cache.SetSources(new[] { path });
            PumpUntil(cache, () => cache.PassComplete && cache.FilesRead == 1, false, 0);
            Interlocked.Exchange(ref now, 65_000);
            for (int i = 0; i < 10; i++)
                cache.Pump(false, false, 0, refreshIntervalMs: 60_000);
            Assert.Equal(1, cache.FilesRead);
            Assert.Equal(0, cache.PeriodicRefreshes);

            Assert.True(SpinWait.SpinUntil(() =>
            {
                cache.Pump(true, false, 0, refreshIntervalMs: 60_000);
                Thread.Sleep(5);
                return cache.PeriodicRefreshes == 1;
            }, TimeSpan.FromSeconds(10)));
            Assert.Equal(2, cache.FilesRead);
        }

        [Fact]
        public void Memory_release_cooldown_takes_priority_over_periodic_refresh()
        {
            string path = MakeFile("released.bin", 150_000);
            long now = 0;
            using var pool = new WorkerPool(1);
            using var store = new NativeBlockStore();
            pool.Configure(1, true, false, 50, ThreadPriority.Lowest);
            var cache = new FileCache(store, pool, clockMs: () => Interlocked.Read(ref now));
            cache.SetSources(new[] { path });
            PumpUntil(cache, () => cache.PassComplete && cache.FilesRead == 1, true, 1_000_000);
            store.ReleaseAll();
            Interlocked.Exchange(ref now, 10_000);
            for (int i = 0; i < 10; i++)
                cache.Pump(true, true, 1_000_000, refreshIntervalMs: 1000);
            Assert.Equal(1, cache.FilesRead);
            Assert.Equal(0, store.CommittedBytes);
            Assert.Equal(0, cache.PeriodicRefreshes);

            Interlocked.Exchange(ref now, 31_000);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                cache.Pump(true, true, 1_000_000, refreshIntervalMs: 1000);
                Thread.Sleep(5);
                return cache.FilesRead >= 2 && store.CommittedBytes == 150_000;
            }, TimeSpan.FromSeconds(10)));
        }

        [Fact]
        public void Released_files_are_read_again_only_after_cooldown()
        {
            string a = MakeFile("a.bin", 200_000);
            long[] clock = { 0 };
            using var pool = new WorkerPool(1);
            using var store = new NativeBlockStore();
            pool.Configure(1, true, false, 50, ThreadPriority.Lowest);
            var cache = new FileCache(store, pool, () => clock[0]);
            cache.SetSources(new[] { a });
            PumpUntil(cache, () => cache.PassComplete, true, long.MaxValue);
            store.ReleaseAll();
            for (int i = 0; i < 10; i++) { cache.Pump(true, true, long.MaxValue); Thread.Sleep(5); }
            Assert.Equal(0, store.TotalBytes);
            clock[0] += 31_000;
            PumpUntil(cache, () => store.CommittedBytes == 200_000, true, long.MaxValue);
        }
    }
}
