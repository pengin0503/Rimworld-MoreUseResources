using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MoreUseResources.Benchmarks.Running;

namespace MoreUseResources.Benchmarks.Analysis;

public sealed record MetricComparison(string Scenario, string Condition, string Metric, int NA, int NB, double MedianA, double MedianB, double Change,
    bool Separated, string Verdict);

/// <summary>
/// Compares two result directories condition by condition. A difference is reported as better or
/// worse only when the medians differ by at least 3% and the trial ranges do not overlap at all;
/// with 5 trials per side complete separation corresponds to the strongest result a rank test can
/// give (two-sided p ≈ 0.008). Everything else is "no clear difference". Runs on different input
/// data are not compared.
/// </summary>
public static class Comparer
{
    public const double MinChange = 0.03;
    /// <summary>Trials per side below which no difference is claimed.</summary>
    public const int MinTrials = 3;

    /// <summary>Metric → true when larger is better.</summary>
    public static readonly IReadOnlyDictionary<string, bool> Metrics = new Dictionary<string, bool>
    {
        ["items_per_s"] = true, ["mib_per_s"] = true, ["wall_ms"] = false, ["active_ms"] = false, ["cpu_ms"] = false,
        ["rss_peak_mib"] = false, ["rss_anon_peak_mib"] = false, ["native_peak_mib"] = false, ["alloc_mib_per_s"] = false, ["alloc_mib"] = false,
        ["gc_pause_ms"] = false, ["gc_pause_max_ms"] = false, ["io_storage_read_mib"] = false, ["io_read_mib"] = false,
        ["consumer_ms"] = false, ["time_to_audit_ms"] = false, ["time_to_prefetch_ms"] = false, ["time_to_sources_ms"] = false,
        ["shutdown_ms"] = false, ["drain_ms"] = false, ["release_ms"] = false, ["teardown_ms"] = false, ["lock_contentions_per_s"] = false,
    };

    public static (string Json, string Markdown, bool Comparable) Compare(string dirA, string dirB)
    {
        var a = Analyzer.Conditions(ResultWriter.ReadTrials(dirA));
        var b = Analyzer.Conditions(ResultWriter.ReadTrials(dirB));
        JsonElement? ma = Read(Path.Combine(dirA, "metadata.json"));
        JsonElement? mb = Read(Path.Combine(dirB, "metadata.json"));
        var warnings = new List<string>();
        bool comparable = true;
        void Check(string label, bool blocking, params string[] path)
        {
            string? va = ma is { } x ? Get(x, path) : null, vb = mb is { } y ? Get(y, path) : null;
            if (va == vb) return;
            warnings.Add($"{label} が異なる: {va ?? "-"} / {vb ?? "-"}");
            if (blocking) comparable = false;
        }
        Check("入力データの構成指紋", true, "dataset", "shape_fingerprint");
        Check("入力データの内容指紋", false, "dataset", "content_fingerprint");
        Check("マシンタイプ", false, "machine", "gce", "machine_type");
        Check("CPU", false, "machine", "cpu_model");
        Check("vCPU 数", false, "machine", "logical_processors");
        Check("メモリ", false, "machine", "mem_total_mib");
        Check("カーネル", false, "machine", "kernel");
        Check(".NET", false, "runtime", "framework");
        Check("GC 設定", false, "runtime", "server_gc");
        Check("GC 環境変数", false, "runtime", "runtime_environment");
        Check("モード", false, "options", "mode");
        Check("試行数", false, "options", "trials");
        Check("RAM 予算", false, "options", "ram_budget_mib");
        Check("cold の作り方", false, "options", "cold_method");

        var rows = new List<MetricComparison>();
        if (comparable)
        {
            foreach (var ca in a)
            {
                var cb = b.FirstOrDefault(x => x.Scenario == ca.Scenario && x.Key == ca.Key);
                if (cb == null) continue;
                foreach (var (metric, higherBetter) in Metrics)
                {
                    MetricSummary? sa = ca.S(metric), sb = cb.S(metric);
                    if (sa == null || sb == null) continue;
                    rows.Add(Row(ca.Scenario, ca.Key, metric, sa, sb, higherBetter));
                }
            }
        }
        var json = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["a"] = Path.GetFileName(Path.GetFullPath(dirA).TrimEnd(Path.DirectorySeparatorChar)),
            ["b"] = Path.GetFileName(Path.GetFullPath(dirB).TrimEnd(Path.DirectorySeparatorChar)),
            ["comparable"] = comparable,
            ["warnings"] = warnings,
            ["rule"] = "better/worse: |median change| >= 3% and min..max ranges do not overlap",
            ["rows"] = rows,
        };
        return (JsonSerializer.Serialize(json, ResultWriter.Json), Markdown(dirA, dirB, ma, mb, warnings, comparable, rows), comparable);
    }

    public static MetricComparison Row(string scenario, string condition, string metric, MetricSummary a, MetricSummary b, bool higherBetter)
    {
        double change = Math.Abs(a.Median) > 1e-12 ? b.Median / a.Median - 1 : (b.Median == a.Median ? 0 : double.PositiveInfinity);
        bool separated = a.Max < b.Min || b.Max < a.Min;
        string verdict;
        if (a.N < MinTrials || b.N < MinTrials) verdict = "試行不足";
        else if (!separated || Math.Abs(change) < MinChange) verdict = "差なし";
        else verdict = (change > 0) == higherBetter ? "改善" : "悪化";
        return new MetricComparison(scenario, condition, metric, a.N, b.N, a.Median, b.Median, change, separated, verdict);
    }

    private static string Markdown(string dirA, string dirB, JsonElement? ma, JsonElement? mb, List<string> warnings, bool comparable, List<MetricComparison> rows)
    {
        var md = new StringBuilder();
        md.AppendLine("# ベンチマーク比較");
        md.AppendLine();
        md.AppendLine($"- A: `{Path.GetFileName(Path.GetFullPath(dirA).TrimEnd(Path.DirectorySeparatorChar))}`（コミット {(ma is { } x ? Get(x, "code", "commit") : "-")}）");
        md.AppendLine($"- B: `{Path.GetFileName(Path.GetFullPath(dirB).TrimEnd(Path.DirectorySeparatorChar))}`（コミット {(mb is { } y ? Get(y, "code", "commit") : "-")}）");
        md.AppendLine("- 判定: 中央値の差が 3% 以上かつ全試行の範囲（最小〜最大）が重ならない場合だけ「改善」「悪化」。それ以外は「差なし」。");
        md.AppendLine();
        if (warnings.Count > 0)
        {
            md.AppendLine("## 条件の違い");
            md.AppendLine();
            foreach (string w in warnings) md.AppendLine("- " + w);
            md.AppendLine();
        }
        if (!comparable)
        {
            md.AppendLine("入力データが異なるため比較しない。同じデータ（同じ `--data` または同じ `--generate` の指定）で測り直すこと。");
            return md.ToString();
        }
        var changed = rows.Where(r => r.Verdict is "改善" or "悪化").ToList();
        md.AppendLine($"## 差があった指標（{changed.Count} / {rows.Count}）");
        md.AppendLine();
        if (changed.Count == 0) md.AppendLine("判定基準を満たす差はなかった。");
        else
        {
            md.AppendLine("| シナリオ | 条件 | 指標 | A 中央値 | B 中央値 | 変化 | 判定 |");
            md.AppendLine("|---|---|---|---:|---:|---:|---|");
            foreach (var r in changed.OrderBy(r => r.Scenario, StringComparer.Ordinal).ThenBy(r => r.Condition, StringComparer.Ordinal))
                md.AppendLine($"| {r.Scenario} | `{r.Condition}` | {r.Metric} | {Analyzer.F(r.MedianA)} | {Analyzer.F(r.MedianB)} | {Analyzer.Change(r.Change)} | {r.Verdict} |");
        }
        md.AppendLine();
        int insufficient = rows.Count(r => r.Verdict == "試行不足");
        if (insufficient > 0) md.AppendLine($"試行が 3 未満で判定しなかった指標: {insufficient}");
        return md.ToString();
    }

    private static JsonElement? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
            return d.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    private static string? Get(JsonElement e, params string[] path)
    {
        JsonElement cur = e;
        foreach (string p in path)
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return null;
        return cur.ValueKind == JsonValueKind.String ? cur.GetString() : cur.ValueKind == JsonValueKind.Null ? null : cur.GetRawText();
    }
}
