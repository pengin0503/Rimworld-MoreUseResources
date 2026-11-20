using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Xml;
using HarmonyLib;
using MoreUseResources.Core;
using Verse;

namespace MoreUseResources;

internal static class PatchedXmlCache
{
    private static readonly Type[] SupportedOperations =
    {
        typeof(PatchOperationAdd), typeof(PatchOperationRemove), typeof(PatchOperationReplace),
        typeof(PatchOperationTest), typeof(PatchOperationSequence), typeof(PatchOperationConditional),
        typeof(PatchOperationFindMod), typeof(PatchOperationInsert), typeof(PatchOperationSetName),
        typeof(PatchOperationAddModExtension), typeof(PatchOperationAttributeAdd),
        typeof(PatchOperationAttributeRemove), typeof(PatchOperationAttributeSet),
    };
    private static readonly FieldInfo? NeverSucceeded = AccessTools.DeclaredField(typeof(PatchOperation), "neverSucceeded");
    private static readonly FieldInfo? TranslationNodes = AccessTools.DeclaredField(typeof(TKeySystem), "keys");
    private static readonly FieldInfo? TranslationLists = AccessTools.DeclaredField(typeof(TKeySystem), "treatAsList");
    private static readonly ConditionalWeakTable<XmlDocument, Replay> Replays = new();
    private static int attempted;
    internal static string StatusKey { get; private set; } = "MUR_PatchedXmlOff";

    private sealed record Capture(string Path, string Key, Dictionary<XmlNode, int> SourceIndexes,
        PatchOperation[] Operations);
    private sealed record Replay(XmlDocument Document, Dictionary<XmlNode, LoadableXmlAsset> Sources);

    internal static void Install(Harmony harmony)
    {
        if (!TexturePrewarm.SupportedBuild || NeverSucceeded?.FieldType != typeof(bool)
            || TranslationNodes == null || TranslationLists == null)
        {
            StatusKey = "MUR_PatchedXmlBypass";
            return;
        }
        var loader = typeof(LoadedModManager);
        // The consumer must be installed before the prefix is allowed to skip any operation.
        harmony.Patch(AccessTools.DeclaredMethod(loader, nameof(LoadedModManager.ParseAndProcessXML)),
            prefix: new HarmonyMethod(typeof(PatchedXmlCache), nameof(BeforeParse)) { priority = Priority.First });
        harmony.Patch(AccessTools.DeclaredMethod(loader, nameof(LoadedModManager.ApplyPatches)),
            prefix: new HarmonyMethod(typeof(PatchedXmlCache), nameof(BeforeApply)) { priority = Priority.Last },
            postfix: new HarmonyMethod(typeof(PatchedXmlCache), nameof(AfterApply)) { priority = Priority.Last });
        StatusKey = "MUR_PatchedXmlWaiting";
    }

    private static bool BeforeApply(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup,
        out Capture? __state)
    {
        __state = null;
        // The first supported call is the startup load. All subsequent reload paths use the normal pipeline.
        if (Interlocked.Exchange(ref attempted, 1) != 0) return true;
        StatusKey = "MUR_PatchedXmlBypass";
        try
        {
            if (xmlDoc.DocumentElement is not { Name: "Defs" } root || HasForeignPatches()
                || TranslationNodes?.GetValue(null) is not IList { Count: 0 }
                || TranslationLists?.GetValue(null) is not HashSet<XmlNode> { Count: 0 })
                return true;

            var sources = new List<LoadableXmlAsset?>();
            var sourceIndexes = new Dictionary<XmlNode, int>();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Add(hash, typeof(LoadedModManager).Module.ModuleVersionId.ToString());
            Add(hash, typeof(PatchedXmlCache).Module.ModuleVersionId.ToString());
            Add(hash, System.Globalization.CultureInfo.CurrentCulture.Name);
            if (xmlDoc.ChildNodes.Count != 1) return true;
            Add(hash, xmlDoc.PreserveWhitespace ? "preserve" : "normalize");
            AddXml(hash, root);
            foreach (XmlNode child in root.ChildNodes)
            {
                sourceIndexes.Add(child, sources.Count);
                assetlookup.TryGetValue(child, out LoadableXmlAsset? source);
                sources.Add(source);
                Add(hash, source?.mod?.PackageId);
                Add(hash, source?.FullFilePath);
            }

            var operations = new List<PatchOperation>();
            foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
            {
                Add(hash, pack.PackageId);
                Add(hash, pack.Name); // FindMod checks the visible name in this build.
                Add(hash, pack.RootDir);
                foreach (Assembly assembly in pack.assemblies.loadedAssemblies)
                    Add(hash, assembly.ManifestModule.ModuleVersionId.ToString());
                foreach (PatchOperation operation in pack.Patches)
                {
                    if (NeverSucceeded!.GetValue(operation) is not true || !AddOperation(hash, operation, 0)) return true;
                    operations.Add(operation);
                }
                Add(hash, null); // end of this mod
            }
            if (operations.Count == 0 || !AddConfiguration(hash)) return true;
            string key = Convert.ToHexString(hash.GetHashAndReset());
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "MoreUseResources-Patches.bin");
            PatchedXml? cached = PatchedXmlStore.Load(path, key, sources.Count);
            if (cached != null && !HasForeignPatches())
            {
                var restored = new Dictionary<XmlNode, LoadableXmlAsset>();
                int index = 0;
                foreach (XmlNode node in cached.Document.DocumentElement!.ChildNodes)
                {
                    int origin = cached.Origins[index++];
                    if (origin >= 0 && sources[origin] is { } source) restored.Add(node, source);
                }

                // The vanilla document stays untouched until its parser receives both replacement arguments.
                // Completion sees the same top-level success flags it would see after a successful apply.
                int marked = 0;
                Replays.Add(xmlDoc, new Replay(cached.Document, restored));
                try
                {
                    for (; marked < operations.Count; marked++) NeverSucceeded!.SetValue(operations[marked], false);
                }
                catch
                {
                    Replays.Remove(xmlDoc);
                    for (int i = 0; i < marked; i++) NeverSucceeded!.SetValue(operations[i], true);
                    throw;
                }
                StatusKey = "MUR_PatchedXmlHit";
                return false;
            }

            __state = new Capture(path, key, sourceIndexes, operations.ToArray());
            StatusKey = "MUR_PatchedXmlMiss";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning("[More Use Resources] Patched XML cache bypassed. " + ex.Message);
        }
        return true;
    }

    private static void BeforeParse(ref XmlDocument xmlDoc, ref Dictionary<XmlNode, LoadableXmlAsset> assetlookup,
        bool hotReload)
    {
        if (hotReload || !Replays.TryGetValue(xmlDoc, out Replay? replay)) return;
        Replays.Remove(xmlDoc);
        xmlDoc = replay.Document;
        assetlookup = replay.Sources;
    }

    private static void AfterApply(XmlDocument xmlDoc, Capture? __state)
    {
        if (__state == null) return;
        try
        {
            if (xmlDoc.DocumentElement is not { Name: "Defs" } root
                || __state.Operations.Any(operation => NeverSucceeded!.GetValue(operation) is not false)
                || HasForeignPatches())
                return;
            var origins = new int[root.ChildNodes.Count];
            int index = 0;
            foreach (XmlNode node in root.ChildNodes)
                origins[index++] = __state.SourceIndexes.TryGetValue(node, out int origin) ? origin : -1;
            PatchedXmlStore.Save(__state.Path, __state.Key, xmlDoc, origins);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning("[More Use Resources] Patched XML cache could not be written. " + ex.Message);
        }
    }

    private static bool AddOperation(IncrementalHash hash, PatchOperation? operation, int depth)
    {
        if (operation == null)
        {
            Add(hash, null);
            return true;
        }
        Type type = operation.GetType();
        if (depth >= 32 || Array.IndexOf(SupportedOperations, type) < 0) return false;
        Add(hash, type.FullName);
        for (Type? current = type; current != typeof(object) && current != null; current = current.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (FieldInfo field in current.GetFields(flags).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                if (field == NeverSucceeded || field.Name == "lastFailedOperation") continue;
                Add(hash, field.Name);
                switch (field.GetValue(operation))
                {
                    case null:
                        Add(hash, null);
                        break;
                    case string text:
                        Add(hash, text);
                        break;
                    case Enum value:
                        Add(hash, value.ToString());
                        break;
                    case XmlContainer xml when xml.GetType() == typeof(XmlContainer):
                        if (xml.node == null) return false;
                        AddXml(hash, xml.node);
                        break;
                    case List<string> names:
                        foreach (string name in names)
                        {
                            Add(hash, name);
                            if (type == typeof(PatchOperationFindMod) && field.Name == "mods")
                                Add(hash, ModLister.HasActiveModWithName(name) ? "active" : "inactive");
                        }
                        Add(hash, null);
                        break;
                    case PatchOperation child:
                        if (!AddOperation(hash, child, depth + 1)) return false;
                        break;
                    case List<PatchOperation> children:
                        foreach (PatchOperation child in children)
                            if (child == null || !AddOperation(hash, child, depth + 1)) return false;
                        Add(hash, null);
                        break;
                    default:
                        return false;
                }
            }
        }
        return true;
    }

    private static bool HasForeignPatches()
    {
        foreach (MethodBase method in Harmony.GetAllPatchedMethods())
        {
            Type? type = method.DeclaringType;
            if (type == null) continue;
            bool pipeline = type == typeof(LoadedModManager) || type == typeof(TKeySystem)
                || type == typeof(ModLister) || type == typeof(ModContentPack)
                || typeof(PatchOperation).IsAssignableFrom(type);
            if (pipeline && Harmony.GetPatchInfo(method)?.Owners.Any(owner => owner != ResourceRuntime.PackageId) == true)
                return true;
        }
        return false;
    }

    private static void AddXml(IncrementalHash hash, XmlNode node)
    {
        using SHA256 digest = SHA256.Create();
        using var output = new CryptoStream(Stream.Null, digest, CryptoStreamMode.Write);
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            XmlDomCodec.Write(writer, node);
        output.FlushFinalBlock();
        hash.AppendData(digest.Hash!);
    }

    private static bool AddConfiguration(IncrementalHash hash)
    {
        const long maxBytes = 32L * 1024 * 1024;
        long total = 0;
        foreach (string path in Directory.EnumerateFiles(GenFilePaths.ConfigFolderPath, "*.xml").Order(StringComparer.Ordinal))
        {
            var before = new FileInfo(path);
            using var stream = File.OpenRead(path);
            long length = stream.Length;
            total += length;
            if (total > maxBytes || length != before.Length) return false;
            Add(hash, Path.GetFileName(path));
            hash.AppendData(SHA256.HashData(stream));
            var after = new FileInfo(path);
            if (after.Length != length || after.LastWriteTimeUtc != before.LastWriteTimeUtc) return false;
        }
        return true;
    }

    private static void Add(IncrementalHash hash, string? text)
    {
        if (text == null)
        {
            hash.AppendData(BitConverter.GetBytes(-1));
            return;
        }
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        hash.AppendData(BitConverter.GetBytes(utf8.Length));
        hash.AppendData(utf8);
    }
}
