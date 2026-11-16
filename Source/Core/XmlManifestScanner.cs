using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Xml;

namespace MoreUseResources.Core
{
    /// <summary>
    /// Read-only XML inventory. Definition keys represent possible duplicate identifiers,
    /// not proof that the game will load two active definitions.
    /// </summary>
    public readonly struct XmlManifestReport
    {
        public readonly bool ParseFailed;
        public readonly bool Skipped;
        public readonly int DefinitionElements;
        public readonly IReadOnlyList<string> DefinitionKeys;

        internal XmlManifestReport(bool parseFailed, bool skipped, int definitionElements, IReadOnlyList<string> keys)
        {
            ParseFailed = parseFailed;
            Skipped = skipped;
            DefinitionElements = definitionElements;
            DefinitionKeys = keys;
        }
    }

    /// <summary>
    /// Inspects real XML mod files without evaluating any game hooks, resolving external
    /// entities, or retaining a full DOM. Only direct named children of &lt;Defs&gt; are indexed.
    /// </summary>
    public static class XmlManifestScanner
    {
        public const long MaxXmlBytes = 8L * 1024 * 1024;
        private const int MaxDefinitionKeysPerFile = 8192;
        private const int MaxDefNameChars = 256;
        private const int MaxDepth = 64;

        public static XmlManifestReport Scan(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length > MaxXmlBytes)
                return new XmlManifestReport(false, true, 0, Array.Empty<string>());
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxXmlBytes * 2,
                MaxCharactersFromEntities = 0,
                IgnoreWhitespace = true,
                IgnoreComments = true,
            };
            var keys = new List<string>();
            int definitionCount = 0;
            bool rootIsDefs = false;

            try
            {
                using XmlReader reader = XmlReader.Create(stream, settings);
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.Depth > MaxDepth) throw new XmlException("XML depth limit exceeded");
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.Depth == 0)
                    {
                        rootIsDefs = reader.LocalName == "Defs";
                        continue;
                    }
                    if (!rootIsDefs || reader.Depth != 1) continue;
                    definitionCount++;
                    if (keys.Count >= MaxDefinitionKeysPerFile
                        || string.Equals(reader.GetAttribute("Abstract"), "True", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string type = reader.LocalName;
                    using XmlReader subtree = reader.ReadSubtree();
                    subtree.Read();
                    while (subtree.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (subtree.Depth > MaxDepth) throw new XmlException("XML depth limit exceeded");
                        if (subtree.NodeType != XmlNodeType.Element || subtree.Depth != 1
                            || subtree.LocalName != "defName") continue;
                        string name = subtree.ReadElementContentAsString().Trim();
                        if (name.Length is > 0 and <= MaxDefNameChars)
                            keys.Add(type + ":" + name);
                        // Only one direct defName is meaningful for this Def.
                        break;
                    }
                }
                return new XmlManifestReport(false, false, definitionCount, keys);
            }
            catch (XmlException)
            {
                return new XmlManifestReport(true, false, 0, Array.Empty<string>());
            }
            catch (InvalidOperationException)
            {
                // For example, a non-text-only <defName> element is malformed for this inventory.
                return new XmlManifestReport(true, false, 0, Array.Empty<string>());
            }
        }
    }
}
