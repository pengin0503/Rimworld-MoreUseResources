using System;
using System.IO;
using System.Text;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    public sealed class RuntimeConfigGcTests : IDisposable
    {
        // Laid out like the game's own RimWorld.runtimeconfig.json (1.7.5011): CRLF, two-space indent, no final newline.
        private static readonly string Game = string.Join("\r\n",
            "{",
            "  \"runtimeOptions\": {",
            "    \"tfm\": \"net10.0\",",
            "    \"includedFrameworks\": [",
            "      {",
            "        \"name\": \"Microsoft.NETCore.App\",",
            "        \"version\": \"10.0.15-waylithclr.dev\"",
            "      }",
            "    ],",
            "    \"configProperties\": {",
            "      \"WaylithClr.AssemblyNameRedirects\": \"UnityEngine*=UnityStubs;Unity.Newtonsoft.Json=;Unity.*=UnityStubs\",",
            "      \"WaylithClr.FieldInjection\": true,",
            "      \"WaylithClr.FieldInjectionGCDescReserve\": 4,",
            "      \"WaylithClr.JitPatchableAssemblies\": \"Assembly-CSharp\",",
            "      \"System.Reflection.Metadata.MetadataUpdater.IsSupported\": false",
            "    }",
            "  }",
            "}");

        private static readonly string GameWithServer = Game.Replace(
            "\"System.Reflection.Metadata.MetadataUpdater.IsSupported\": false\r\n",
            "\"System.Reflection.Metadata.MetadataUpdater.IsSupported\": false,\r\n"
            + "      \"System.GC.Server\": true,\r\n"
            + "      \"MoreUseResources.AddedServerGc\": true\r\n", StringComparison.Ordinal);

        private readonly string folder = Path.Combine(Path.GetTempPath(), "mur-runtimeconfig-" + Guid.NewGuid().ToString("N"));

        public RuntimeConfigGcTests() => Directory.CreateDirectory(folder);

        public void Dispose()
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }

        [Fact]
        public void Adds_only_the_two_keys_and_keeps_the_rest_of_the_text_as_it_was()
        {
            ServerGcFileResult result = RuntimeConfigGc.Edit(Game, serverGc: true, out string? updated);
            Assert.Equal(ServerGcFileState.Added, result.State);
            Assert.Equal(GameWithServer, updated);
        }

        [Fact]
        public void Turning_it_off_gives_back_the_original_text()
        {
            ServerGcFileResult result = RuntimeConfigGc.Edit(GameWithServer, serverGc: false, out string? updated);
            Assert.Equal(ServerGcFileState.Removed, result.State);
            Assert.Equal(Game, updated);
        }

        [Fact]
        public void Leaves_an_entry_it_did_not_write()
        {
            string byGame = GameWithServer.Replace(",\r\n      \"MoreUseResources.AddedServerGc\": true", "", StringComparison.Ordinal);
            Assert.Equal(ServerGcFileState.SetByOthers, RuntimeConfigGc.Edit(byGame, true, out string? a).State);
            Assert.Null(a);
            Assert.Equal(ServerGcFileState.SetByOthers, RuntimeConfigGc.Edit(byGame, false, out string? b).State);
            Assert.Null(b);

            string workstation = byGame.Replace("\"System.GC.Server\": true", "\"System.GC.Server\": false", StringComparison.Ordinal);
            Assert.Equal(ServerGcFileState.LeftAsIs, RuntimeConfigGc.Edit(workstation, true, out string? c).State);
            Assert.Null(c);
            Assert.Equal(ServerGcFileState.NotSet, RuntimeConfigGc.Edit(workstation, false, out string? d).State);
            Assert.Null(d);

            // The runtime takes only exactly "true" as on, so the string "True" is not the server GC.
            string text = byGame.Replace("\"System.GC.Server\": true", "\"System.GC.Server\": \"True\"", StringComparison.Ordinal);
            Assert.Equal(ServerGcFileState.LeftAsIs, RuntimeConfigGc.Edit(text, true, out _).State);
        }

        [Fact]
        public void Keeps_its_own_entry_and_does_nothing_when_off_and_absent()
        {
            Assert.Equal(ServerGcFileState.Kept, RuntimeConfigGc.Edit(GameWithServer, true, out string? kept).State);
            Assert.Null(kept);
            Assert.Equal(ServerGcFileState.NotSet, RuntimeConfigGc.Edit(Game, false, out string? off).State);
            Assert.Null(off);
        }

        [Fact]
        public void Creates_config_properties_and_keeps_lf_and_the_final_newline()
        {
            string text = "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0\"\n  }\n}\n";
            Assert.Equal(ServerGcFileState.Added, RuntimeConfigGc.Edit(text, true, out string? updated).State);
            Assert.Equal("{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0\",\n    \"configProperties\": {\n"
                + "      \"System.GC.Server\": true,\n      \"MoreUseResources.AddedServerGc\": true\n    }\n  }\n}\n", updated);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("{\"runtimeOptions\": 3}")]
        [InlineData("{\"runtimeOptions\": {\"configProperties\": []}}")]
        [InlineData("{\"runtimeOptions\": {\"configProperties\": {\"System.GC.Server\": false, \"System.GC.Server\": true}}}")]
        public void Does_not_touch_a_file_it_cannot_read(string text)
        {
            Assert.Equal(ServerGcFileState.Unreadable, RuntimeConfigGc.Edit(text, true, out string? updated).State);
            Assert.Null(updated);
        }

        [Fact]
        public void Applies_to_a_file_once_and_keeps_the_byte_order_mark()
        {
            string path = Path.Combine(folder, "RimWorld.runtimeconfig.json");
            Assert.Equal(ServerGcFileState.Missing, RuntimeConfigGc.Apply(path, true).State);

            byte[] bom = { 0xEF, 0xBB, 0xBF };
            File.WriteAllBytes(path, Concat(bom, Encoding.UTF8.GetBytes(Game)));
            Assert.Equal(ServerGcFileState.Added, RuntimeConfigGc.Apply(path, true).State);
            Assert.Equal(Concat(bom, Encoding.UTF8.GetBytes(GameWithServer)), File.ReadAllBytes(path));

            DateTime written = File.GetLastWriteTimeUtc(path);
            Assert.Equal(ServerGcFileState.Kept, RuntimeConfigGc.Apply(path, true).State);
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));

            Assert.Equal(ServerGcFileState.Removed, RuntimeConfigGc.Apply(path, false).State);
            Assert.Equal(Concat(bom, Encoding.UTF8.GetBytes(Game)), File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(folder));
        }

        [Fact]
        public void A_file_it_cannot_read_stays_as_it_was()
        {
            string path = Path.Combine(folder, "RimWorld.runtimeconfig.json");
            File.WriteAllText(path, "{ broken");
            ServerGcFileResult result = RuntimeConfigGc.Apply(path, true);
            Assert.Equal(ServerGcFileState.Unreadable, result.State);
            Assert.NotNull(result.Error);
            Assert.Equal("{ broken", File.ReadAllText(path));
        }

        private static byte[] Concat(byte[] first, byte[] second)
        {
            var all = new byte[first.Length + second.Length];
            first.CopyTo(all, 0);
            second.CopyTo(all, first.Length);
            return all;
        }
    }
}
