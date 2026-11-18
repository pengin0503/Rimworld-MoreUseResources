using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MoreUseResources.Benchmarks.Running;
using Xunit;

namespace MoreUseResources.Benchmarks.Tests;

/// <summary>
/// A short run of the real command line over generated data: every scenario chosen here must finish,
/// the result files must be written and readable, and the analysis must check the planted truth.
/// </summary>
public class EndToEndTests
{
    [Fact]
    public void ShortRunWritesResultsAndAnalysis()
    {
        using var dir = new TempDir();
        string output = dir.Sub("out");
        int code = Program.Main(new[]
        {
            "run", "--mode", "smoke", "--workers", "1,2", "--trials", "1", "--warmup", "0",
            "--scenarios", "audit,scan,io-faults,xml-cache", "--rate-window-s", "1", "--trial-timeout-s", "10",
            "--generate", dir.Sub("gen"), "--generate-mib", "8", "--out", output, "--max-rss-mib", "100000",
        });
        Assert.Equal(0, code);
        string run = Directory.GetDirectories(output).Single(d => Path.GetFileName(d) != "generated-data");
        foreach (string f in new[] { "metadata.json", "trials.csv", "trials.jsonl", "metrics.csv", "summary.json", "analysis.json", "analysis.md", "run.log" })
            Assert.True(File.Exists(Path.Combine(run, f)), f + " missing");
        Assert.False(Directory.Exists(Path.Combine(run, "scratch")));

        var trials = ResultWriter.ReadTrials(run);
        Assert.NotEmpty(trials);
        Assert.DoesNotContain(trials, t => t.Status != TrialStatus.Ok);
        Assert.All(new[] { "audit", "scan", "io-faults", "xml-cache" }, s => Assert.Contains(trials, t => t.Scenario == s && t.Countable));
        Assert.All(trials.Where(t => t.Countable), t => Assert.Equal(0, t.Metrics.GetValueOrDefault("leaked_mod_threads")));
        // Cold trials either verified the cold cache or were excluded with a reason.
        Assert.All(trials.Where(t => t.CacheRequested == "cold"), t => Assert.True(t.CacheVerified == true || !t.Countable));

        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "metadata.json")));
        JsonElement root = meta.RootElement;
        Assert.Equal("generated", root.GetProperty("dataset").GetProperty("kind").GetString());
        Assert.True(root.GetProperty("generated_truth").GetProperty("XmlFiles").GetInt32() > 0);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("machine").GetProperty("kernel").GetString()));
        Assert.Equal(1, root.GetProperty("options").GetProperty("trials").GetInt32());

        string all = string.Join("\n", Directory.GetFiles(run).Select(File.ReadAllText));
        Assert.DoesNotContain(dir.Path, all, StringComparison.Ordinal); // paths are redacted
        string md = File.ReadAllText(Path.Combine(run, "analysis.md"));
        Assert.Contains("## 主な所見", md, StringComparison.Ordinal);
        Assert.Contains("生成データ", md, StringComparison.Ordinal); // the planted truth was checked
        Assert.Contains("## このベンチマークで再現できないこと", md, StringComparison.Ordinal);

        // A comparison of the run with itself finds no differences and is comparable.
        Assert.Equal(0, Program.Main(new[] { "compare", run, run, "--out", dir.Sub("cmp"), "--max-rss-mib", "100000" }));
        string cmp = File.ReadAllText(Path.Combine(dir.Sub("cmp"), "comparison.md"));
        Assert.DoesNotContain("| 改善 |", cmp, StringComparison.Ordinal);
        Assert.DoesNotContain("| 悪化 |", cmp, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageErrorsReturnExitCodeTwo()
    {
        Assert.Equal(2, Program.Main(new[] { "run", "--workers", "0" }));
        Assert.Equal(0, Program.Main(new[] { "help" }));
    }
}
