using System;
using System.IO;
using System.Threading;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    public sealed class MappedPackResidencyTests
    {
        private static WorkerPool Pool()
        {
            var pool = new WorkerPool(2);
            pool.Configure(2, true, false, 1, ThreadPriority.Lowest);
            return pool;
        }

        private static bool PumpUntil(MappedPackResidency residency, Func<PackResidencySnapshot, bool> done,
            long limit = 1L << 30, long refreshMs = 0, bool allowed = true)
        {
            return SpinWait.SpinUntil(() =>
            {
                residency.Pump(allowed, limit, refreshMs);
                Thread.Sleep(2);
                return done(residency.Snapshot);
            }, TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void Maps_every_source_and_counts_one_pass()
        {
            using var temp = new TemporaryDirectory();
            string a = temp.Write("a/Assets.zip", 3 * 1024 * 1024 + 123);
            string b = temp.Write("b/Assets.zip", 4096);
            using var pool = Pool();
            using var residency = new MappedPackResidency(pool);
            residency.SetSources(new[] { a, b, a });

            // Exactly the aggregate file size: a leaked reservation would wrongly
            // defer the second file even though both committed views fit the budget.
            long exactLimit = 3 * 1024 * 1024 + 123 + 4096;
            Assert.True(PumpUntil(residency, s => s.Passes == 1, limit: exactLimit));
            PackResidencySnapshot status = residency.Snapshot;
            Assert.Equal(2, status.Sources);
            Assert.Equal(2, status.MappedFiles);
            Assert.Equal(3 * 1024 * 1024 + 123 + 4096, status.MappedBytes);
            Assert.Equal(0, status.Errors);
            Assert.Equal(0, status.Deferred);
        }

        [Fact]
        public void Does_not_map_beyond_the_byte_limit()
        {
            using var temp = new TemporaryDirectory();
            string small = temp.Write("a/Assets.zip", 8192);
            string large = temp.Write("b/Assets.zip", 64 * 1024);
            using var pool = Pool();
            using var residency = new MappedPackResidency(pool);
            residency.SetSources(new[] { small, large });

            Assert.True(PumpUntil(residency, s => s.Passes == 1, limit: 16 * 1024));
            PackResidencySnapshot status = residency.Snapshot;
            Assert.Equal(1, status.MappedFiles);
            Assert.Equal(8192, status.MappedBytes);
            Assert.Equal(1, status.Deferred);
        }

        [Fact]
        public void Missing_and_empty_files_do_not_stop_the_pass()
        {
            using var temp = new TemporaryDirectory();
            string missing = Path.Combine(temp.Path, "none", "Assets.zip");
            string empty = temp.Write("e/Assets.zip", 0);
            string good = temp.Write("g/Assets.zip", 4096);
            using var pool = Pool();
            using var residency = new MappedPackResidency(pool);
            residency.SetSources(new[] { missing, empty, good });

            Assert.True(PumpUntil(residency, s => s.Passes == 1));
            PackResidencySnapshot status = residency.Snapshot;
            Assert.Equal(1, status.MappedFiles);
            Assert.Equal(4096, status.MappedBytes);
            Assert.Equal(1, status.Errors);
            Assert.NotNull(status.LastError);
        }

        [Fact]
        public void Disallowed_pump_starts_nothing_and_keeps_existing_views()
        {
            using var temp = new TemporaryDirectory();
            string a = temp.Write("a/Assets.zip", 4096);
            using var pool = Pool();
            using var residency = new MappedPackResidency(pool);
            residency.SetSources(new[] { a });

            for (int i = 0; i < 20; i++)
            {
                residency.Pump(false, 1L << 30, 0);
                Thread.Sleep(2);
            }
            Assert.Equal(0, residency.Snapshot.MappedFiles);

            Assert.True(PumpUntil(residency, s => s.Passes == 1));
            residency.Pump(false, 1L << 30, 0);
            Assert.Equal(1, residency.Snapshot.MappedFiles);
        }

        [Fact]
        public void Source_replacement_drops_inflight_obsolete_maps_and_preserves_new_budget()
        {
            using var temp = new TemporaryDirectory();
            string old = temp.Write("old/Assets.zip", 12 * 1024 * 1024);
            string current = temp.Write("current/Assets.zip", 4096);
            using var pool = Pool();
            using var residency = new MappedPackResidency(pool);
            residency.SetSources(new[] { old });

            // Kick off an asynchronous read, then immediately replace its source snapshot.
            residency.Pump(true, 16L * 1024 * 1024, 0);
            residency.SetSources(new[] { current });
            Assert.True(PumpUntil(residency, s => s.Passes >= 1 && s.MappedFiles == 1,
                limit: 16L * 1024 * 1024));

            PackResidencySnapshot result = residency.Snapshot;
            Assert.Equal(1, result.Sources);
            Assert.Equal(1, result.MappedFiles);
            Assert.Equal(4096, result.MappedBytes);
            Assert.Equal(0, result.Deferred);
        }

        [Fact]
        public void Release_closes_views_so_the_file_can_be_replaced_and_mapped_again()
        {
            using var temp = new TemporaryDirectory();
            string a = temp.Write("a/Assets.zip", 4096);
            using var pool = Pool();
            using var residency = new MappedPackResidency(pool);
            residency.SetSources(new[] { a });
            Assert.True(PumpUntil(residency, s => s.Passes == 1));

            residency.ReleaseAll();
            PackResidencySnapshot released = residency.Snapshot;
            Assert.Equal(0, released.MappedFiles);
            Assert.Equal(0, released.MappedBytes);
            File.Delete(a);
            temp.Write("a/Assets.zip", 8192);

            Assert.True(PumpUntil(residency, s => s.MappedBytes == 8192));
        }

        [Fact]
        public void Removing_a_source_closes_only_its_view()
        {
            using var temp = new TemporaryDirectory();
            string a = temp.Write("a/Assets.zip", 4096);
            string b = temp.Write("b/Assets.zip", 8192);
            using var pool = Pool();
            using var residency = new MappedPackResidency(pool);
            residency.SetSources(new[] { a, b });
            Assert.True(PumpUntil(residency, s => s.Passes == 1));

            residency.SetSources(new[] { b });

            PackResidencySnapshot status = residency.Snapshot;
            Assert.Equal(1, status.MappedFiles);
            Assert.Equal(8192, status.MappedBytes);
        }

        [Fact]
        public void Refresh_interval_starts_another_pass_and_zero_disables_it()
        {
            using var temp = new TemporaryDirectory();
            string a = temp.Write("a/Assets.zip", 4096);
            long now = 0;
            using var pool = Pool();
            using var residency = new MappedPackResidency(pool, () => Interlocked.Read(ref now));
            residency.SetSources(new[] { a });
            Assert.True(PumpUntil(residency, s => s.Passes == 1, refreshMs: 60_000));

            for (int i = 0; i < 20; i++)
            {
                residency.Pump(true, 1L << 30, 0);
                residency.Pump(true, 1L << 30, 60_000);
                Thread.Sleep(2);
            }
            Assert.Equal(1, residency.Snapshot.Passes);

            Interlocked.Exchange(ref now, 60_000);
            Assert.True(PumpUntil(residency, s => s.Passes == 2, refreshMs: 60_000));
            Assert.Equal(1, residency.Snapshot.MappedFiles);
        }

        [Fact]
        public void Controller_maps_only_asset_packs_and_releases_on_request_and_pressure()
        {
            using var temp = new TemporaryDirectory();
            string assets = temp.Write("mod/Assets.zip", 4096);
            string cachePack = temp.Write("cache/Core-0123456789abcdef.zip", 4096);
            var probe = new FakeProbe();
            var settings = new ResourceSettings { RamResidentPacks = true };
            using var controller = new ResourceController(probe, 2, initial: settings);
            controller.PublishSignals(new RawGameSignals { Focused = true, PlayDataLoaded = true });
            controller.SetFileSources(Array.Empty<string>(), new[] { cachePack, assets });
            Assert.Equal(1, controller.GetStatus().ResidentPackSources);

            Assert.True(SpinWait.SpinUntil(() =>
            {
                controller.ControlTick(true, 0.25);
                Thread.Sleep(2);
                return controller.GetStatus().ResidentPackPasses >= 1;
            }, TimeSpan.FromSeconds(10)));
            Assert.Equal(1, controller.GetStatus().ResidentPackFiles);
            Assert.Equal(4096, controller.GetStatus().ResidentPackBytes);

            probe.Available = 1L << 30; // below the keep-free floor
            controller.ControlTick(true, 0.25);
            Assert.Equal(0, controller.GetStatus().ResidentPackFiles);

            probe.Available = 20L << 30;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                controller.ControlTick(true, 0.25);
                Thread.Sleep(2);
                return controller.GetStatus().ResidentPackFiles == 1;
            }, TimeSpan.FromSeconds(10)));

            controller.Manual = ManualMode.Paused;
            controller.RequestReleaseMemory();
            controller.ControlTick(true, 0.25);
            Assert.Equal(0, controller.GetStatus().ResidentPackFiles);
        }

        [Fact]
        public void Controller_leaves_packs_unmapped_while_the_option_is_off()
        {
            using var temp = new TemporaryDirectory();
            string assets = temp.Write("mod/Assets.zip", 4096);
            using var controller = new ResourceController(new FakeProbe(), 2, initial: new ResourceSettings());
            controller.PublishSignals(new RawGameSignals { Focused = true, PlayDataLoaded = true });
            controller.SetFileSources(Array.Empty<string>(), new[] { assets });
            for (int i = 0; i < 20; i++)
            {
                controller.ControlTick(true, 0.25);
                Thread.Sleep(2);
            }
            Assert.Equal(0, controller.GetStatus().ResidentPackFiles);
            Assert.False(new ResourceSettings().RamResidentPacks);
        }

        private sealed class TemporaryDirectory : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mur-resident-" + Guid.NewGuid().ToString("N"));
            public TemporaryDirectory() => Directory.CreateDirectory(Path);
            public string Write(string name, int bytes)
            {
                string path = System.IO.Path.Combine(Path, name);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                var data = new byte[bytes];
                new Random(bytes).NextBytes(data);
                File.WriteAllBytes(path, data);
                return path;
            }
            public void Dispose() => Directory.Delete(Path, recursive: true);
        }
    }
}
