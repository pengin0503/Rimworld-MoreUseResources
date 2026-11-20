using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using MoreUseResources.Benchmarks.Running;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Scenarios;

/// <summary>
/// The patched-XML cache file (CachePatchedXml): PatchedXmlStore.Load and Save of one combined
/// Defs document. The game builds that document itself (all mods' Defs merged, patches applied);
/// here it is the dataset's Defs merged without patches, so size and node count approximate a real
/// mod list of the same content. The store's own limits (64 MiB file, 250 000 nodes) apply.
/// </summary>
public sealed class XmlCacheScenario : Scenario
{
    private const int MaxNodes = 250_000;

    public override string Id => "xml-cache";
    public override string Title => "パッチ適用済みXMLキャッシュの保存と読み込み";
    public override string ProductionCode => "PatchedXmlStore.Save/Load, XmlDomCodec";
    public override string NotReproduced => "ゲームによる XML 結合とパッチ適用、キャッシュ復元後の Def 生成";

    public override IReadOnlyList<Condition> Conditions(RunEnvironment env)
    {
        if (!env.Dataset.FilesWithExtension(".xml").Any()) throw new InvalidOperationException("no XML files");
        return Caches(env).Select(c => Condition.Of(Id, ("cache", CacheName(c)))).ToList();
    }

    public override Trial CreateTrial(Condition condition, RunEnvironment env) => new XmlCacheTrial(env);

    private sealed class XmlCacheTrial : Trial
    {
        private readonly RunEnvironment _env;
        private XmlDocument? _document;
        private int[] _origins = Array.Empty<int>();
        private int _sources;
        private string _path = "";
        private const string Key = "benchmark";

        public XmlCacheTrial(RunEnvironment env) => _env = env;

        public override IReadOnlyList<string> CacheTargets => File.Exists(_path) ? new[] { _path } : Array.Empty<string>();

        public override void Prepare(TrialContext ctx)
        {
            var doc = new XmlDocument { XmlResolver = null };
            XmlElement root = doc.CreateElement("Defs");
            doc.AppendChild(root);
            var origins = new List<int>();
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
            int index = 0;
            foreach (string file in _env.Dataset.FilesWithExtension(".xml"))
            {
                ctx.Token.ThrowIfCancellationRequested();
                if (origins.Count >= MaxNodes) break;
                try
                {
                    var source = new XmlDocument { XmlResolver = null };
                    using (XmlReader reader = XmlReader.Create(file, settings)) source.Load(reader);
                    if (source.DocumentElement is not { Name: "Defs" } defs) continue;
                    foreach (XmlNode child in defs.ChildNodes)
                    {
                        if (child is not XmlElement || origins.Count >= MaxNodes) continue;
                        root.AppendChild(doc.ImportNode(child, deep: true));
                        origins.Add(index);
                    }
                    index++;
                }
                catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { }
            }
            _document = doc;
            _origins = origins.ToArray();
            _sources = Math.Max(1, index);
            _path = Path.Combine(ctx.ScratchDirectory, "MoreUseResources-Patches.bin");
            try { PatchedXmlStore.Save(_path, Key, doc, _origins); }
            catch (InvalidDataException ex) { ctx.Note("save failed: " + ex.Message); }
        }

        public override void Execute(TrialContext ctx)
        {
            ctx.Metric("nodes", _origins.Length);
            ctx.Metric("source_files", _sources);
            bool exists = File.Exists(_path);
            if (exists)
            {
                ctx.Metric("cache_mib", new FileInfo(_path).Length / 1048576.0);
                PatchedXml? loaded = null;
                ctx.Measure("load", () => loaded = PatchedXmlStore.Load(_path, Key, _sources));
                ctx.Metric("load_ok", loaded != null && loaded.Origins.Length == _origins.Length ? 1 : 0);
            }
            int saveOk = 0, limit = 0;
            ctx.Measure("save", () =>
            {
                try
                {
                    PatchedXmlStore.Save(_path, Key, _document!, _origins);
                    saveOk = 1;
                }
                catch (InvalidDataException ex)
                {
                    limit = ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    ctx.Note("save refused: " + ex.Message);
                }
            });
            ctx.Metric("save_ok", saveOk);
            ctx.Metric("limit_exceeded", limit);
            ctx.Metric("items", _origins.Length);
            if (!exists && saveOk == 1) ctx.Note("first save in this trial; load measured on the next trial only");
        }
    }
}
