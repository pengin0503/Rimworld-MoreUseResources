using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests;

public sealed class TextureDemandHistoryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "mur-demand-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(dir, TextureDemandHistory.FileName);

    public TextureDemandHistoryTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private static TextureDemandSample Sample(string mod, string key, int count) => new(new TextureDemandKey(mod, key), count);

    [Fact]
    public void RankingRoundTripsInDemandOrder()
    {
        var ranked = new[]
        {
            Sample("ludeon.rimworld", "Things/Building/Wall", 3),
            Sample("author.mod", "Things/Pawn/Body", 9),
            Sample("author.mod", "UI/Icon", 9),
        };
        TextureDemandHistory.Save(FilePath, "fp", ranked);
        IReadOnlyList<TextureDemandSample> loaded = TextureDemandHistory.Load(FilePath, "fp");
        Assert.Equal(new[] { ranked[1], ranked[2], ranked[0] }, loaded);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void SameKeyFromTwoModsStaysTwoEntriesAndDuplicatesCollapse()
    {
        var ranked = new[]
        {
            Sample("mod.a", "Things/Shared", 5),
            Sample("mod.b", "Things/Shared", 4),
            Sample("mod.a", "Things/Shared", 99),   // duplicate key: the first one stands
            Sample("mod.c", "Things/Zero", 0),
            Sample("mod.c", "Things/Negative", -3),
        };
        TextureDemandHistory.Save(FilePath, "fp", ranked);
        Assert.Equal(new[] { ranked[0], ranked[1] }, TextureDemandHistory.Load(FilePath, "fp"));
    }

    [Fact]
    public void ShortSessionKeepsEarlierHistoryAtHalfWeightUntilItFadesOut()
    {
        var previous = new[] { Sample("m", "old-hot", 40), Sample("m", "both", 9), Sample("m", "old-once", 1) };
        var session = new[] { Sample("m", "both", 3), Sample("m", "new", 5) };

        IReadOnlyList<TextureDemandSample> merged = TextureDemandHistory.Merge(session, previous);
        // Seen again: this session's count stands. Not seen: half the stored count; a count of 1 fades out.
        Assert.Equal(new[] { Sample("m", "old-hot", 20), Sample("m", "new", 5), Sample("m", "both", 3) }, merged);
        Assert.Equal(TextureDemandHistory.Normalize(session), TextureDemandHistory.Merge(session, null));
        Assert.Equal(TextureDemandHistory.MaxEntries, TextureDemandHistory.Merge(
            Enumerable.Range(0, 400).Select(i => Sample("m", "s" + i, 10)).ToList(),
            Enumerable.Range(0, 400).Select(i => Sample("m", "p" + i, 10)).ToList()).Count);
    }

    [Fact]
    public void EntryAndFileLimitsHold()
    {
        var many = Enumerable.Range(0, 2000).Select(i => Sample("mod.x", "Tex/" + i, 5000 - i)).ToList();
        TextureDemandHistory.Save(FilePath, "fp", many);
        IReadOnlyList<TextureDemandSample> loaded = TextureDemandHistory.Load(FilePath, "fp");
        Assert.Equal(TextureDemandHistory.MaxEntries, loaded.Count);
        Assert.Equal(many[0], loaded[0]);
        Assert.Equal(many[511], loaded[511]);

        string longKey = "T/" + new string('k', 500);
        var wide = Enumerable.Range(0, 512).Select(i => Sample(new string('m', 500) + i, longKey, 1000 - i)).ToList();
        TextureDemandHistory.Save(FilePath, "fp", wide);
        Assert.True(new FileInfo(FilePath).Length <= TextureDemandHistory.MaxFileBytes);

        File.WriteAllBytes(FilePath, new byte[TextureDemandHistory.MaxFileBytes + 1]);
        Assert.Empty(TextureDemandHistory.Load(FilePath, "fp"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Schema\":1,\"Fingerprint\":\"fp\"}")]
    [InlineData("{\"Schema\":2,\"Fingerprint\":\"fp\",\"Entries\":[{\"PackageId\":\"m\",\"ContentKey\":\"k\",\"Count\":1}]}")]
    [InlineData("{\"Schema\":1,\"Fingerprint\":\"other\",\"Entries\":[{\"PackageId\":\"m\",\"ContentKey\":\"k\",\"Count\":1}]}")]
    [InlineData("{\"Schema\":1,\"Fingerprint\":\"fp\",\"Entries\":[null,{\"PackageId\":null,\"ContentKey\":\"k\",\"Count\":1}]}")]
    [InlineData("{\"Schema\":1,\"Fingerprint\":\"fp\",\"Entries\":[{\"PackageId\":\"m\",\"ContentKey\":\"k\",\"Count\":\"many\"}]}")]
    public void UnreadableOrForeignHistoryMeansNoHistory(string content)
    {
        File.WriteAllText(FilePath, content);
        Assert.Empty(TextureDemandHistory.Load(FilePath, "fp"));
        Assert.Empty(TextureDemandHistory.Load(Path.Combine(dir, "missing.json"), "fp"));
    }

    [Theory]
    [InlineData("mod.a", "C:\\Games\\Textures\\Wall")]
    [InlineData("mod.a", "/home/user/Textures/Wall")]
    [InlineData("mod.a", "\\\\server\\share\\Wall")]
    [InlineData("mod.a", "Things/../../Wall")]
    [InlineData("C:\\mods\\a", "Things/Wall")]
    [InlineData("mod/a", "Things/Wall")]
    [InlineData("", "Things/Wall")]
    [InlineData("mod.a", " ")]
    [InlineData("mod.a", "Things/\u0001Wall")]
    public void PathsAreRefusedAsKeys(string packageId, string contentKey)
    {
        Assert.False(TextureDemandHistory.IsValidKey(new TextureDemandKey(packageId, contentKey)));
        TextureDemandHistory.Save(FilePath, "fp", new[] { Sample(packageId, contentKey, 5), Sample("ok.mod", "Things/Wall", 1) });
        Assert.Equal(new[] { Sample("ok.mod", "Things/Wall", 1) }, TextureDemandHistory.Load(FilePath, "fp"));
        // The refused key is not written at all.
        Assert.Equal(1, System.Text.Json.JsonDocument.Parse(File.ReadAllText(FilePath)).RootElement
            .GetProperty("Entries").GetArrayLength());
    }

    [Fact]
    public void FingerprintFollowsModOrderBuildCacheLayoutAndPackContentButNotPackOrder()
    {
        var mods = new[] { "core|a1", "mod.one|b2", "mod.two|c3" };
        var packs = new (string, long, long)[] { ("p1:Core.zip", 100, 5), ("p2:Assets.zip", 900, 7) };
        string baseline = TextureDemandHistory.Fingerprint(mods, "1.7.5011 rev24", "v47/bc7|id", packs);

        Assert.Equal(baseline, TextureDemandHistory.Fingerprint(mods, "1.7.5011 rev24", "v47/bc7|id",
            new[] { packs[1], packs[0] }));
        Assert.NotEqual(baseline, TextureDemandHistory.Fingerprint(
            new[] { "core|a1", "mod.two|c3", "mod.one|b2" }, "1.7.5011 rev24", "v47/bc7|id", packs));
        Assert.NotEqual(baseline, TextureDemandHistory.Fingerprint(mods, "1.7.5012 rev1", "v47/bc7|id", packs));
        Assert.NotEqual(baseline, TextureDemandHistory.Fingerprint(mods, "1.7.5011 rev24", "v48/bc7|id", packs));
        Assert.NotEqual(baseline, TextureDemandHistory.Fingerprint(mods, "1.7.5011 rev24", "v47/bc7|id",
            new (string, long, long)[] { ("p1:Core.zip", 100, 6), ("p2:Assets.zip", 900, 7) }));
        Assert.NotEqual(baseline, TextureDemandHistory.Fingerprint(mods, "1.7.5011 rev24", "v47/bc7|id",
            new (string, long, long)[] { ("p1:Core.zip", 101, 5), ("p2:Assets.zip", 900, 7) }));
    }

    [Fact]
    public void PathIdentityHidesTheFolderButSeparatesFiles()
    {
        string a = TextureDemandHistory.PathIdentity(Path.Combine(dir, "one", "Assets.zip"));
        string b = TextureDemandHistory.PathIdentity(Path.Combine(dir, "two", "Assets.zip"));
        Assert.NotEqual(a, b);
        Assert.EndsWith(":Assets.zip", a);
        Assert.DoesNotContain(dir, a);
        Assert.Equal(a, TextureDemandHistory.PathIdentity(Path.Combine(dir, "one", "Assets.zip")));
    }

    [Fact]
    public void SavesOnlyChangedRankingsOnceAMinuteAndOneAtATime()
    {
        var schedule = new TextureDemandSaveSchedule();
        var first = new[] { Sample("m", "a", 2) };
        var second = new[] { Sample("m", "a", 3) };

        Assert.False(schedule.TryBegin(0, Array.Empty<TextureDemandSample>()));   // no real demand observed
        Assert.True(schedule.TryBegin(1_000, first));
        Assert.False(schedule.TryBegin(200_000, second));                          // previous write still running
        schedule.Complete(written: true);
        Assert.False(schedule.TryBegin(30_000, second));                           // less than a minute later
        Assert.False(schedule.TryBegin(70_000, first));                            // unchanged ranking
        Assert.True(schedule.TryBegin(70_000, second));
        schedule.Complete(written: false);                                         // a failed write may be repeated
        Assert.True(schedule.TryBegin(140_000, second));
    }

    [Fact]
    public void RankedDemandKeepsCountsWithoutConsumingTheQueue()
    {
        var demand = new TextureDemand<string>(3);
        demand.Observe("low", total: 3, own: 1);
        demand.Observe("high", total: 20, own: 2);
        demand.Observe("mine", total: 7, own: 7);
        Assert.Equal(new[] { ("high", 18), ("low", 2) }, demand.PeekRanked());
        Assert.Equal(new[] { "high", "low" }, demand.TakeRanked());
    }
}
