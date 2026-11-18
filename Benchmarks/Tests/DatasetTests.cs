using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MoreUseResources.Benchmarks.Data;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Benchmarks.Tests;

/// <summary>A temporary directory removed after the test.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mur-bench-test-" + Guid.NewGuid().ToString("N"));
    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>One generated data set shared by the tests of a class (generation takes about a second).</summary>
public sealed class GeneratedDataFixture : IDisposable
{
    private readonly TempDir _dir = new();

    public GeneratedDataFixture()
    {
        Root = _dir.Sub("gen");
        Truth = DatasetGenerator.EnsureGenerated(Root, 8, 7);
        Data = Dataset.Load(new[] { Root }, Array.Empty<string>(), int.MaxValue, long.MaxValue, fingerprintContent: true, kind: "generated");
    }

    public string Root { get; }
    public GeneratedTruth Truth { get; }
    public Dataset Data { get; }

    public void Dispose() => _dir.Dispose();
}

public class DatasetTests : IClassFixture<GeneratedDataFixture>
{
    private readonly GeneratedDataFixture _f;

    public DatasetTests(GeneratedDataFixture f) => _f = f;

    [Fact]
    public void GeneratorIsDeterministic()
    {
        using var other = new TempDir();
        GeneratedTruth again = DatasetGenerator.EnsureGenerated(other.Sub("gen"), 8, 7);
        Assert.Equal(_f.Truth, again);
        Dataset d = Dataset.Load(new[] { other.Sub("gen") }, Array.Empty<string>(), int.MaxValue, long.MaxValue, true, "generated");
        Assert.Equal(_f.Data.ShapeFingerprint, d.ShapeFingerprint);
        Assert.Equal(_f.Data.ContentFingerprint, d.ContentFingerprint);
    }

    [Fact]
    public void GeneratorRefusesForeignDirectoriesAndReusesItsOwn()
    {
        using var dir = new TempDir();
        string foreign = dir.Sub("foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "user data");
        Assert.Throws<IOException>(() => DatasetGenerator.EnsureGenerated(foreign, 8, 7));
        Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));

        string own = dir.Sub("own");
        DatasetGenerator.EnsureGenerated(own, 8, 7);
        string probe = Path.Combine(own, "Game", "Assets.zip");
        DateTime written = File.GetLastWriteTimeUtc(probe);
        DatasetGenerator.EnsureGenerated(own, 8, 7);
        Assert.Equal(written, File.GetLastWriteTimeUtc(probe)); // same parameters: not regenerated
    }

    [Fact]
    public void DataSetHasTheLayoutTheModDiscovers()
    {
        Dataset d = _f.Data;
        Assert.Equal(_f.Truth.Mods, d.Mods.Count);
        Assert.Contains(d.Mods, m => m.Official && m.Path.EndsWith(Path.Combine("Data", "Core"), StringComparison.Ordinal));
        Assert.Single(d.Packs);
        Assert.EndsWith(EnginePackFiles.AssetPackFileName, d.Packs[0], StringComparison.Ordinal);
        Assert.Equal(_f.Truth.PackBytes, d.PackBytes);
        Assert.Equal(d.DiscoveredFiles.Count, d.Files.Count);
        Assert.Equal(_f.Truth.XmlFiles, d.FilesWithExtension(".xml").Count());
        Assert.Equal(_f.Truth.ValidPng + _f.Truth.MalformedPng, d.FilesWithExtension(".png").Count());
        Assert.Equal(_f.Truth.Dds, d.FilesWithExtension(".dds").Count());
        Assert.Equal(_f.Truth.Sounds, d.FilesWithExtension(".wav").Count());
    }

    [Fact]
    public void AssetAuditFindsWhatTheGeneratorPlanted()
    {
        using var pool = new WorkerPool(2, "MUR-Test");
        pool.Configure(2, jobsAllowed: true, synthetic: false, dutyPercent: 50, ThreadPriority.Normal);
        var audit = new AssetAudit(pool);
        audit.SetSources(_f.Data.Files);
        var clock = Stopwatch.StartNew();
        while (!audit.Snapshot.Complete && clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            audit.Pump(true, 2);
            Thread.Sleep(1);
        }
        audit.Suspend();
        AssetAuditSnapshot s = audit.Snapshot;
        GeneratedTruth t = _f.Truth;
        Assert.True(s.Complete);
        Assert.Equal(_f.Data.Files.Count, s.Completed);
        Assert.Equal(0, s.Errors);
        Assert.Equal(t.XmlFiles, s.XmlFilesChecked);
        // Known gap in XmlManifestScanner (see XmlScannerKnownGapTests): a syntax error after the first
        // defName is not reported, and that Def is counted. The generated data has both kinds.
        Assert.True(t.MalformedXml > t.MalformedXmlAfterDefName && t.MalformedXmlAfterDefName > 0);
        Assert.Equal(t.MalformedXml - t.MalformedXmlAfterDefName, s.XmlParseFailures);
        Assert.Equal(t.Definitions + t.MalformedXmlAfterDefName, s.XmlDefinitions);
        Assert.Equal(t.CollidingDefinitions, s.XmlDefinitionCollisions);
        Assert.Equal(t.ValidPng + t.Dds, s.TextureFilesValid);
        Assert.Equal(t.MalformedPng, s.TextureFilesMalformed);
        Assert.Equal(t.DuplicateFiles, s.Duplicates);
        Assert.Equal(t.DuplicateBytes, s.DuplicateBytes);
    }

    [Fact]
    public void SubsetIsStableBoundedAndKeepsDiscoveryOrder()
    {
        IReadOnlyList<string> all = _f.Data.DiscoveredFiles;
        long limit = _f.Data.FileBytes / 3;
        List<string> a = Dataset.SelectSubset(all, new[] { _f.Root }, 50, limit);
        List<string> b = Dataset.SelectSubset(all, new[] { _f.Root }, 50, limit);
        Assert.Equal(a, b);
        Assert.True(a.Count <= 50);
        Assert.True(a.Sum(Dataset.SafeLength) <= limit);
        var position = all.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        Assert.Equal(a.OrderBy(p => position[p]), a);
        // Spread over mods and types, not only the files discovery visits first.
        Assert.True(a.Select(Path.GetExtension).Distinct().Count() >= 2);
        Assert.True(a.Max(p => position[p]) > all.Count / 2);
        Assert.Equal(all, Dataset.SelectSubset(all, new[] { _f.Root }, int.MaxValue, long.MaxValue));
    }

    [Fact]
    public void LoadFoldersFollowLoadFoldersXmlOrTheDefaultLayout()
    {
        using var dir = new TempDir();
        string mod = dir.Sub("ModA");
        foreach (string sub in new[] { "1.7", "Common", "Extra" }) Directory.CreateDirectory(Path.Combine(mod, sub));
        Assert.Equal(new[] { Path.Combine(mod, "1.7"), Path.Combine(mod, "Common"), mod }, Dataset.ResolveLoadFolders(mod));

        File.WriteAllText(Path.Combine(mod, "loadFolders.xml"), "<loadFolders><v1.6><li>Old</li></v1.6><v1.7><li>/</li><li>Extra</li><li>Missing</li></v1.7></loadFolders>");
        // Listed in ascending priority; the mod captures them highest first. Missing folders are dropped.
        Assert.Equal(new[] { Path.Combine(mod, "Extra"), mod }, Dataset.ResolveLoadFolders(mod));

        File.WriteAllText(Path.Combine(mod, "loadFolders.xml"), "<loadFolders><v1.7>");
        Assert.Equal(new[] { Path.Combine(mod, "1.7"), Path.Combine(mod, "Common"), mod }, Dataset.ResolveLoadFolders(mod));
    }

    [Fact]
    public void RelativePathsHideTheDataRoot()
    {
        string file = _f.Data.Files[0];
        string rel = _f.Data.Relative(file);
        Assert.StartsWith("d0/", rel, StringComparison.Ordinal);
        Assert.DoesNotContain(_f.Root, rel, StringComparison.Ordinal);
    }
}

/// <summary>
/// Documents a defect of the shipped XmlManifestScanner found by this benchmark: after it reads a
/// Def's defName it closes the subtree reader early; the syntax error met while skipping the rest
/// leaves the outer reader in ReadState.Error, Read() then returns false and the scan is reported as
/// a success. The benchmark does not change the mod; when the scanner is fixed these expectations
/// flip and this test must be updated together with AssetAuditFindsWhatTheGeneratorPlanted.
/// </summary>
public class XmlScannerKnownGapTests
{
    [Theory]
    [InlineData("<Defs>\n  <ThingDef>\n    <defName>A</defName>\n  <ThingDef>\n", false)]
    [InlineData("<Defs><ThingDef><defName>A</defName></ThingDef><ThingDef><defName>B</defName><x></ThingDef></Defs>", false)]
    [InlineData("<Defs>\n  <ThingDef>\n    <label>cut", true)]
    [InlineData("<Defs>\n  <ThingDef>\n    <defName>A</defName>\n  </ThingDef>\n", true)]
    public void ParseFailureIsReportedOnlyWhenTheErrorIsNotInsideASkippedDef(string xml, bool reported)
    {
        using var dir = new TempDir();
        string path = dir.Sub("f.xml");
        File.WriteAllText(path, xml);
        Assert.Throws<System.Xml.XmlException>(() => new System.Xml.XmlDocument().LoadXml(xml)); // all are malformed
        Assert.Equal(reported, XmlManifestScanner.Scan(path, CancellationToken.None).ParseFailed);
    }
}
