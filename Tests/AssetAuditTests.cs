using System;
using System.IO;
using System.Threading;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    public sealed class AssetAuditTests
    {
        [Fact]
        public void Hashes_real_mod_assets_and_counts_exact_duplicates()
        {
            using var directory = new TemporaryDirectory();
            string first = directory.Write("Defs/one.xml", "<Defs><ThingDef Name=\"A\" /></Defs>");
            string copy = directory.Write("Patches/copy.xml", "<Defs><ThingDef Name=\"A\" /></Defs>");
            string other = directory.Write("Languages/other.xml", "<LanguageData />");
            using var pool = new WorkerPool(3);
            pool.Configure(3, true, false, 1, ThreadPriority.Lowest);
            var audit = new AssetAudit(pool);
            audit.SetSources(new[] { first, copy, other });
            Assert.True(SpinWait.SpinUntil(() =>
            {
                audit.Pump(true, 3);
                Thread.Sleep(5);
                return audit.Snapshot.Complete;
            }, TimeSpan.FromSeconds(10)));
            AssetAuditSnapshot status = audit.Snapshot;
            Assert.Equal(3, status.Sources);
            Assert.Equal(3, status.Completed);
            Assert.Equal(1, status.Duplicates);
            Assert.Equal(new FileInfo(first).Length, status.DuplicateBytes);
            Assert.Equal(new FileInfo(first).Length * 2 + new FileInfo(other).Length, status.HashedBytes);
            Assert.Equal(0, status.Errors);
        }

        [Fact]
        public void Paused_audit_does_not_schedule_jobs_and_can_resume()
        {
            using var directory = new TemporaryDirectory();
            string path = directory.Write("Defs/thing.xml", "<Defs />");
            using var pool = new WorkerPool(1);
            pool.Configure(1, true, false, 1, ThreadPriority.Lowest);
            var audit = new AssetAudit(pool);
            audit.SetSources(new[] { path });
            audit.Pump(false, 1);
            Assert.Equal(0, audit.Snapshot.Completed);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                audit.Pump(true, 1);
                Thread.Sleep(5);
                return audit.Snapshot.Complete;
            }, TimeSpan.FromSeconds(10)));
            Assert.Equal(1, audit.Snapshot.Completed);
        }

        [Fact]
        public void Missing_file_is_counted_without_stopping_other_work()
        {
            using var directory = new TemporaryDirectory();
            string present = directory.Write("Defs/ok.xml", "<Defs />");
            string missing = Path.Combine(directory.Path, "Defs", "absent.xml");
            using var pool = new WorkerPool(2);
            pool.Configure(2, true, false, 1, ThreadPriority.Lowest);
            var audit = new AssetAudit(pool);
            audit.SetSources(new[] { missing, present });
            Assert.True(SpinWait.SpinUntil(() =>
            {
                audit.Pump(true, 2);
                Thread.Sleep(5);
                return audit.Snapshot.Complete;
            }, TimeSpan.FromSeconds(10)));
            Assert.Equal(1, audit.Snapshot.Errors);
            Assert.Equal(2, audit.Snapshot.Completed);
        }


        [Fact]
        public void Periodic_pass_rehashes_real_assets_and_detects_same_size_modifications()
        {
            using var directory = new TemporaryDirectory();
            string path = directory.Write("Defs/one.xml", "<Defs><One/></Defs>");
            long now = 1000;
            using var pool = new WorkerPool(2);
            pool.Configure(2, true, false, 1, ThreadPriority.Lowest);
            var audit = new AssetAudit(pool, clockMs: () => Interlocked.Read(ref now));
            audit.SetSources(new[] { path });
            Assert.True(SpinWait.SpinUntil(() =>
            {
                audit.Pump(true, 2, rescanIntervalMs: 1000);
                Thread.Sleep(5);
                return audit.Snapshot.PassesCompleted == 1;
            }, TimeSpan.FromSeconds(10)));
            Assert.Equal(0, audit.Snapshot.ChangedFiles);

            directory.Write("Defs/one.xml", "<Defs><Two/></Defs>");
            Interlocked.Exchange(ref now, 2100);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                audit.Pump(true, 2, rescanIntervalMs: 1000);
                Thread.Sleep(5);
                return audit.Snapshot.PassesCompleted == 2;
            }, TimeSpan.FromSeconds(10)));
            AssetAuditSnapshot status = audit.Snapshot;
            Assert.Equal(1, status.ChangedFiles);
            Assert.Equal(1, status.Completed);
            Assert.Equal(2, status.PassesCompleted);
        }

        [Fact]
        public void Rescan_disabled_keeps_completed_pass_without_more_io()
        {
            using var directory = new TemporaryDirectory();
            string path = directory.Write("Defs/one.xml", "<Defs />");
            long now = 1000;
            using var pool = new WorkerPool(1);
            pool.Configure(1, true, false, 1, ThreadPriority.Lowest);
            var audit = new AssetAudit(pool, clockMs: () => Interlocked.Read(ref now));
            audit.SetSources(new[] { path });
            Assert.True(SpinWait.SpinUntil(() =>
            {
                audit.Pump(true, 1, rescanIntervalMs: 0);
                Thread.Sleep(5);
                return audit.Snapshot.PassesCompleted == 1;
            }, TimeSpan.FromSeconds(10)));
            directory.Write("Defs/one.xml", "<Defs2/>");
            Interlocked.Exchange(ref now, 100000);
            for (int i = 0; i < 10; i++) audit.Pump(true, 1, rescanIntervalMs: 0);
            Assert.Equal(1, audit.Snapshot.PassesCompleted);
            Assert.Equal(0, audit.Snapshot.ChangedFiles);
        }

        private sealed class TemporaryDirectory : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mur-audit-" + Guid.NewGuid().ToString("N"));
            public TemporaryDirectory() => Directory.CreateDirectory(Path);
            public string Write(string name, string content)
            {
                string path = System.IO.Path.Combine(Path, name);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
                return path;
            }
            public void Dispose() => Directory.Delete(Path, recursive: true);
        }
    }
}
