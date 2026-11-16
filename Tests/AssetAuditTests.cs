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

        [Fact]
        public void Xml_scanner_reads_direct_Def_names_not_nested_fields()
        {
            using var directory = new TemporaryDirectory();
            string path = directory.Write("Defs/example.xml",
                "<Defs><ThingDef><defName>Chair</defName><comps><li><defName>Nested</defName></li></comps></ThingDef>" +
                "<ThingDef><defName>Table</defName></ThingDef><ThingDef Abstract='True'><defName>AbstractOnly</defName></ThingDef></Defs>");
            XmlManifestReport report = XmlManifestScanner.Scan(path, CancellationToken.None);
            Assert.False(report.ParseFailed);
            Assert.False(report.Skipped);
            Assert.Equal(3, report.DefinitionElements);
            Assert.Contains("ThingDef:Chair", report.DefinitionKeys);
            Assert.Contains("ThingDef:Table", report.DefinitionKeys);
            Assert.DoesNotContain("ThingDef:Nested", report.DefinitionKeys);
            Assert.DoesNotContain("ThingDef:AbstractOnly", report.DefinitionKeys);
        }

        [Fact]
        public void Xml_scanner_rejects_external_entities_and_malformed_xml_without_reading_any_external_file()
        {
            using var directory = new TemporaryDirectory();
            string xml = "<!DOCTYPE Defs [<!ENTITY external SYSTEM 'file:///etc/passwd'>]>" +
                "<Defs><ThingDef><defName>&external;</defName></ThingDef></Defs>";
            string path = directory.Write("Defs/entity.xml", xml);
            XmlManifestReport result = XmlManifestScanner.Scan(path, CancellationToken.None);
            Assert.True(result.ParseFailed);
            Assert.Empty(result.DefinitionKeys);

            string broken = directory.Write("Defs/broken.xml", "<Defs><ThingDef>");
            Assert.True(XmlManifestScanner.Scan(broken, CancellationToken.None).ParseFailed);
        }

        [Fact]
        public void Xml_audit_counts_actual_Def_collisions_and_parser_errors()
        {
            using var directory = new TemporaryDirectory();
            string first = directory.Write("Defs/a.xml", "<Defs><ThingDef><defName>Chair</defName></ThingDef></Defs>");
            string duplicate = directory.Write("Defs/b.xml", "<Defs><ThingDef><defName>Chair</defName></ThingDef></Defs>");
            string invalid = directory.Write("Defs/c.xml", "<Defs><ThingDef>");
            using var pool = new WorkerPool(3);
            pool.Configure(3, true, false, 1, ThreadPriority.Lowest);
            var audit = new AssetAudit(pool);
            audit.SetSources(new[] { first, duplicate, invalid });
            Assert.True(SpinWait.SpinUntil(() =>
            {
                audit.Pump(true, 3);
                Thread.Sleep(5);
                return audit.Snapshot.Complete;
            }, TimeSpan.FromSeconds(10)));

            Assert.Equal(3, audit.Snapshot.XmlFilesChecked);
            Assert.Equal(1, audit.Snapshot.XmlParseFailures);
            Assert.Equal(2, audit.Snapshot.XmlDefinitions);
            Assert.Equal(1, audit.Snapshot.XmlDefinitionCollisions);
        }

        [Fact]
        public void Xml_index_is_rebuilt_from_current_files_on_each_periodic_pass()
        {
            using var directory = new TemporaryDirectory();
            string first = directory.Write("Defs/a.xml", "<Defs><ThingDef><defName>Chair</defName></ThingDef></Defs>");
            string second = directory.Write("Defs/b.xml", "<Defs><ThingDef><defName>Chair</defName></ThingDef></Defs>");
            long now = 1000;
            using var pool = new WorkerPool(2);
            pool.Configure(2, true, false, 1, ThreadPriority.Lowest);
            var audit = new AssetAudit(pool, clockMs: () => Interlocked.Read(ref now));
            audit.SetSources(new[] { first, second });

            Assert.True(SpinWait.SpinUntil(() =>
            {
                audit.Pump(true, 2, 1000);
                Thread.Sleep(5);
                return audit.Snapshot.PassesCompleted == 1;
            }, TimeSpan.FromSeconds(10)));
            Assert.Equal(1, audit.Snapshot.XmlDefinitionCollisions);

            directory.Write("Defs/b.xml", "<Defs><ThingDef><defName>Bench</defName></ThingDef></Defs>");
            Interlocked.Exchange(ref now, 2100);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                audit.Pump(true, 2, 1000);
                Thread.Sleep(5);
                return audit.Snapshot.PassesCompleted == 2;
            }, TimeSpan.FromSeconds(10)));
            Assert.Equal(2, audit.Snapshot.XmlDefinitions);
            Assert.Equal(0, audit.Snapshot.XmlDefinitionCollisions);
            Assert.Equal(1, audit.Snapshot.ChangedFiles);
        }

        [Fact]
        public void Xml_scanner_does_not_ignore_cancellation()
        {
            using var directory = new TemporaryDirectory();
            string path = directory.Write("Defs/a.xml", "<Defs><ThingDef><defName>Chair</defName></ThingDef></Defs>");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => XmlManifestScanner.Scan(path, cts.Token));
        }

        [Fact]
        public void Image_audit_reports_real_PNG_and_DDS_data_and_repairs_on_rescan()
        {
            const string png64 = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAYAAAC56t6BAAAAC0lEQVR4nGNgwAkAABsAAco8Sg0AAAAASUVORK5CYII=";
            byte[] validPng = Convert.FromBase64String(png64);
            byte[] brokenPng = (byte[])validPng.Clone();
            brokenPng[20] ^= 1;
            using var png = new TextureAssetInspectorTests.TempAsset(".png", validPng);
            using var dds = new TextureAssetInspectorTests.TempAsset(".dds", TextureAssetInspectorTests.Dds(128, 64));
            using var bad = new TextureAssetInspectorTests.TempAsset(".png", brokenPng);
            long now = 1000;
            using var pool = new WorkerPool(3);
            pool.Configure(3, true, false, 1, ThreadPriority.Lowest);
            var audit = new AssetAudit(pool, clockMs: () => Interlocked.Read(ref now));
            audit.SetSources(new[] { png.Path, dds.Path, bad.Path });
            Assert.True(SpinWait.SpinUntil(() =>
            {
                audit.Pump(true, 3, 1000);
                Thread.Sleep(5);
                return audit.Snapshot.PassesCompleted == 1;
            }, TimeSpan.FromSeconds(10)));
            Assert.Equal(3, audit.Snapshot.TextureFilesChecked);
            Assert.Equal(2, audit.Snapshot.TextureFilesValid);
            Assert.Equal(1, audit.Snapshot.TextureFilesMalformed);
            Assert.Equal(24L + 32768, audit.Snapshot.TextureRgba8EquivalentBytes);
            Assert.Equal(validPng.Length + 128, audit.Snapshot.TextureBytesValidated);

            File.WriteAllBytes(bad.Path, validPng);
            Interlocked.Exchange(ref now, 2100);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                audit.Pump(true, 3, 1000);
                Thread.Sleep(5);
                return audit.Snapshot.PassesCompleted == 2;
            }, TimeSpan.FromSeconds(10)));
            Assert.Equal(3, audit.Snapshot.TextureFilesChecked);
            Assert.Equal(3, audit.Snapshot.TextureFilesValid);
            Assert.Equal(0, audit.Snapshot.TextureFilesMalformed);
            Assert.Equal(48L + 32768, audit.Snapshot.TextureRgba8EquivalentBytes);
            Assert.Equal(1, audit.Snapshot.ChangedFiles);
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
