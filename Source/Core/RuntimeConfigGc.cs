using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MoreUseResources.Core
{
    public enum ServerGcFileState
    {
        NotChecked = 0,
        /// <summary>This mod wrote <see cref="RuntimeConfigGc.ServerKey"/>; the GC changes at the next launch.</summary>
        Added = 1,
        /// <summary>The entry this mod wrote earlier is still there.</summary>
        Kept = 2,
        /// <summary>The file asks for the server GC without this mod's marker, so it is not this mod's to change.</summary>
        SetByOthers = 3,
        /// <summary>The file sets the GC kind to something other than server, and this mod did not write it.</summary>
        LeftAsIs = 4,
        /// <summary>The setting is off and this mod removed its own entry; the GC changes at the next launch.</summary>
        Removed = 5,
        /// <summary>The setting is off and the file holds nothing of this mod.</summary>
        NotSet = 6,
        Missing = 7,
        /// <summary>The file is not the JSON this mod expects, so it was not changed.</summary>
        Unreadable = 8,
        WriteFailed = 9,
    }

    public readonly record struct ServerGcFileResult(ServerGcFileState State, string? Error = null);

    /// <summary>
    /// Turns the server garbage collector on or off in the game's <c>RimWorld.runtimeconfig.json</c>. The runtime reads
    /// that file once at start-up, so a change applies from the next launch, and an environment variable
    /// <c>DOTNET_gcServer</c> still wins over it. Only the two keys below are added or removed: everything else in the
    /// file is written back as it was, and an entry this mod did not write is never changed or removed.
    /// </summary>
    public static class RuntimeConfigGc
    {
        public const string ServerKey = "System.GC.Server";
        /// <summary>Marks <see cref="ServerKey"/> as written by this mod, so turning the setting off removes only that.</summary>
        public const string MarkerKey = "MoreUseResources.AddedServerGc";

        private static readonly JsonDocumentOptions ReadOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>Works out the new text for one file; <paramref name="updated"/> is null when nothing is to be written.</summary>
        public static ServerGcFileResult Edit(string text, bool serverGc, out string? updated)
        {
            updated = null;
            try { return EditParsed(text, serverGc, ref updated); }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                // A duplicate property name, for one, only shows when the object is read.
                updated = null;
                return new(ServerGcFileState.Unreadable, ex.Message);
            }
        }

        private static ServerGcFileResult EditParsed(string text, bool serverGc, ref string? updated)
        {
            JsonNode? root = JsonNode.Parse(text, null, ReadOptions);
            if (root is not JsonObject top || top["runtimeOptions"] is not JsonObject options)
                return new(ServerGcFileState.Unreadable, "no runtimeOptions object");

            JsonNode? propertiesNode = options["configProperties"];
            if (propertiesNode != null && propertiesNode is not JsonObject)
                return new(ServerGcFileState.Unreadable, "configProperties is not an object");
            var properties = propertiesNode as JsonObject;
            bool ours = properties != null && IsTrue(properties[MarkerKey]);
            bool present = properties != null && properties.ContainsKey(ServerKey);
            bool server = present && IsTrue(properties![ServerKey]);

            ServerGcFileState state;
            if (serverGc)
            {
                if (server) return new(ours ? ServerGcFileState.Kept : ServerGcFileState.SetByOthers);
                if (present) return new(ServerGcFileState.LeftAsIs);
                if (properties == null)
                {
                    properties = new JsonObject();
                    options["configProperties"] = properties;
                }
                properties[ServerKey] = true;
                properties[MarkerKey] = true;
                state = ServerGcFileState.Added;
            }
            else
            {
                if (!ours) return new(server ? ServerGcFileState.SetByOthers : ServerGcFileState.NotSet);
                properties!.Remove(MarkerKey);
                if (server) properties.Remove(ServerKey);
                state = ServerGcFileState.Removed;
            }

            string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string written = top.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                NewLine = newline,
                // Keeps strings such as the assembly redirects ("UnityEngine*=UnityStubs;...") exactly as they were.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            if (text.EndsWith('\n')) written += newline;
            if (!JsonNode.DeepEquals(JsonNode.Parse(written, null, ReadOptions), top))
                return new(ServerGcFileState.Unreadable, "the new text did not read back the same");
            updated = written;
            return new(state);
        }

        /// <summary>
        /// Brings the file at <paramref name="path"/> in line with <paramref name="serverGc"/>. The new text goes to a
        /// file next to it first, is flushed to disk and then renamed over the old one, so the game never sees half a file.
        /// </summary>
        public static ServerGcFileResult Apply(string path, bool serverGc)
        {
            byte[] bytes;
            try
            {
                if (!File.Exists(path)) return new(ServerGcFileState.Missing);
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(ServerGcFileState.Unreadable, ex.Message);
            }

            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string text;
            try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); }
            catch (DecoderFallbackException ex) { return new(ServerGcFileState.Unreadable, ex.Message); }

            ServerGcFileResult result = Edit(text, serverGc, out string? updated);
            if (updated == null) return result;

            byte[] body = Encoding.UTF8.GetBytes(updated);
            byte[] output = bom ? Concat(new byte[] { 0xEF, 0xBB, 0xBF }, body) : body;
            string temp = path + ".MoreUseResources.tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(output, 0, output.Length);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
                byte[] back = File.ReadAllBytes(path);
                if (!back.AsSpan().SequenceEqual(output))
                    return new(ServerGcFileState.WriteFailed, "the file read back differently");
                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(ServerGcFileState.WriteFailed, ex.Message);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// The host hands each property to the runtime as text, JSON <c>true</c> as "true", and the runtime takes only
        /// exactly "true" as on.
        /// </summary>
        private static bool IsTrue(JsonNode? node)
        {
            if (node is not JsonValue value) return false;
            if (value.TryGetValue(out bool flag)) return flag;
            return value.TryGetValue(out string? s) && s == "true";
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
