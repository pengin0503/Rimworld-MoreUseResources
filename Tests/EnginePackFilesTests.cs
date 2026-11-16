using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    public sealed class EnginePackFilesTests
    {
        private const string Hash = "0123456789abcdef";

        [Theory]
        [InlineData("3012345678-0123456789abcdef.zip", true, "3012345678")]
        [InlineData("My_Mod-With-Dash-0123456789abcdef.zip", true, "My_Mod-With-Dash")]
        [InlineData("Core-0123456789ABCDEF.zip", false, "")]
        [InlineData("Core-0123456789abcde.zip", false, "")]
        [InlineData("Core_0123456789abcdef.zip", false, "")]
        [InlineData("-0123456789abcdef.zip", false, "")]
        [InlineData("Core-0123456789abcdef.bin", false, "")]
        public void Parses_only_the_engine_pack_name_shape(string fileName, bool expected, string root)
        {
            Assert.Equal(expected, EnginePackFiles.TryGetPackRootName(fileName, out string actual));
            Assert.Equal(root, actual);
        }

        [Fact]
        public void Sanitizes_spaces_like_the_engine()
        {
            Assert.Equal("My_Mod_Name", EnginePackFiles.SanitizeRootName("My Mod Name"));
            Assert.Equal("root", EnginePackFiles.SanitizeRootName(""));
        }

        [Fact]
        public void Recognizes_the_keyed_atlas_page_pack()
        {
            Assert.True(EnginePackFiles.IsKeyedPagesPack("pages." + Hash + ".zip"));
            Assert.False(EnginePackFiles.IsKeyedPagesPack("pages.zip"));
            Assert.False(EnginePackFiles.IsKeyedPagesPack("pagesX" + Hash + ".zip"));
        }

        [Fact]
        public void Returns_cache_packs_of_active_mods_first_then_asset_packs()
        {
            using var temp = new TemporaryDirectory();
            string cache = temp.Dir("cache");
            string variant = Path.Combine(cache, "TextureCache", "v47", "bc7");
            string active = temp.Write(Path.Combine(variant, "My_Mod-" + Hash + ".zip"));
            string pages = temp.Write(Path.Combine(variant, "pages." + Hash + ".zip"));
            temp.Write(Path.Combine(variant, "Inactive-" + Hash + ".zip"));
            temp.Write(Path.Combine(variant, "My_Mod-" + Hash + ".tmp"));
            string modRoot = temp.Dir(Path.Combine("mods", "My Mod"));
            string loadFolder = temp.Dir(Path.Combine("mods", "My Mod", "1.7"));
            string assetPack = temp.Write(Path.Combine(loadFolder, "Assets.zip"));
            string extra = temp.Write(Path.Combine(temp.Path, "game", "Assets.zip"));
            string missing = Path.Combine(temp.Path, "game", "Missing.zip");

            List<string> found = EnginePackFiles.Discover(cache, new[] { modRoot + Path.DirectorySeparatorChar },
                new[] { loadFolder, modRoot }, new[] { extra, missing, extra }, CancellationToken.None);

            Assert.Equal(new[] { active, pages }.OrderBy(p => p, StringComparer.Ordinal), found.Take(2));
            Assert.Equal(new[] { assetPack, extra }, found.Skip(2));
        }

        [Fact]
        public void Keeps_only_the_newest_generation_of_a_pack()
        {
            using var temp = new TemporaryDirectory();
            string cache = temp.Dir("cache");
            string name = "Core-" + Hash + ".zip";
            string stale = temp.Write(Path.Combine(cache, "TextureCache", "v46", "bc7", name));
            string current = temp.Write(Path.Combine(cache, "TextureCache", "v47", "bc7", name));
            File.SetLastWriteTimeUtc(stale, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(current, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

            List<string> found = EnginePackFiles.Discover(cache, new[] { Path.Combine(temp.Path, "Data", "Core") },
                Array.Empty<string>(), null, CancellationToken.None);

            Assert.Equal(new[] { current }, found);
        }

        [Fact]
        public void Missing_cache_directory_or_cancellation_yields_no_cache_packs()
        {
            using var temp = new TemporaryDirectory();
            Assert.Empty(EnginePackFiles.Discover(Path.Combine(temp.Path, "none"), new[] { "Core" },
                Array.Empty<string>(), null, CancellationToken.None));
            Assert.Empty(EnginePackFiles.Discover(null, new[] { "Core" }, Array.Empty<string>(), null, CancellationToken.None));

            string cache = temp.Dir("cache");
            temp.Write(Path.Combine(cache, "TextureCache", "v47", "bc7", "Core-" + Hash + ".zip"));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Empty(EnginePackFiles.Discover(cache, new[] { "Core" }, Array.Empty<string>(), null, cancelled.Token));
        }

        [Fact]
        public void Controller_prefetches_packs_without_auditing_them()
        {
            using var temp = new TemporaryDirectory();
            string loose = temp.Write(Path.Combine(temp.Path, "Defs", "a.xml"), "<Defs />");
            string pack = temp.Write(Path.Combine(temp.Path, "Core-" + Hash + ".zip"));
            string pack2 = temp.Write(Path.Combine(temp.Path, "Other-" + Hash + ".zip"));
            using var controller = new ResourceController(new NoProbe(), 2);

            controller.SetFileSources(new[] { loose }, new[] { pack, pack, loose });
            ControllerStatus status = controller.GetStatus();
            Assert.Equal(1, status.EnginePackFiles);
            Assert.Equal(2, status.FileSources);
            Assert.Equal(1, status.AuditSources);
            Assert.Equal(1, status.FileSourceChanges);

            // The same lists again are not a change.
            controller.SetFileSources(new[] { loose }, new[] { pack });
            Assert.Equal(1, controller.GetStatus().FileSourceChanges);

            // A new pack restarts prefetch but leaves the loose-asset audit list alone.
            controller.SetFileSources(new[] { loose }, new[] { pack, pack2 });
            status = controller.GetStatus();
            Assert.Equal(2, status.EnginePackFiles);
            Assert.Equal(3, status.FileSources);
            Assert.Equal(1, status.AuditSources);
            Assert.Equal(2, status.FileSourceChanges);
        }

        private sealed class NoProbe : IMemoryProbe
        {
            public MemoryStatus Read() => default;
        }

        private sealed class TemporaryDirectory : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mur-packs-" + Guid.NewGuid().ToString("N"));
            public TemporaryDirectory() => Directory.CreateDirectory(Path);
            public string Dir(string relative)
            {
                string path = System.IO.Path.Combine(Path, relative);
                Directory.CreateDirectory(path);
                return path;
            }
            public string Write(string path, string content = "pack")
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
                return path;
            }
            public void Dispose() => Directory.Delete(Path, recursive: true);
        }
    }
}
