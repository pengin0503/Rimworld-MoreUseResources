using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MoreUseResources.Benchmarks.Running;

namespace MoreUseResources.Benchmarks.Analysis;

/// <summary>One statement the analysis makes, with the numbers it rests on.</summary>
public sealed record Finding(string Topic, string Severity, string Scenario, string Text, Dictionary<string, double> Evidence);

/// <summary>A worker count's throughput in one series of conditions.</summary>
public sealed record ScalingPoint(int Workers, int N, double Median, double Min, double Max, double P05, double P95, double Speedup, double Efficiency,
    double CpuParallelism, double WorkerBusyPct, double RunQueueWaitPct, string Bottleneck);

public sealed record ScalingSeries(string Scenario, string Series, List<ScalingPoint> Points, int? PlateauWorkers, string Statement);

/// <summary>
/// Reads a result directory and writes analysis.json and analysis.md (Japanese). Every statement is
/// derived from countable trials only and states its evidence; where the data cannot decide
/// (too few trials, overlapping ranges) the analysis says so instead of guessing.
/// </summary>
public static class Analyzer
{
    /// <summary>A gain counts only when it exceeds 10% and the ranges of all trials do not overlap.</summary>
    public const double MinGain = 0.10;

    public sealed class Cond
    {
        public required string Scenario { get; init; }
        public required string Key { get; init; }
        public required IReadOnlyDictionary<string, string> P { get; init; }
        public required List<TrialResult> Trials { get; init; }
        public required Dictionary<string, MetricSummary> M { get; init; }
        public string? Get(string k) => P.TryGetValue(k, out string? v) ? v : null;
        public int Workers => int.TryParse(Get("workers"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ? w : 0;
        public double Med(string metric) => M.TryGetValue(metric, out MetricSummary? s) ? s.Median : double.NaN;
        public MetricSummary? S(string metric) => M.TryGetValue(metric, out MetricSummary? s) ? s : null;
        public int Countable => Trials.Count(t => t.Countable);
    }

    public static List<Cond> Conditions(IReadOnlyList<TrialResult> trials)
    {
        var list = new List<Cond>();
        foreach (var g in trials.Where(t => t.Status != TrialStatus.Skipped).GroupBy(t => (t.Scenario, t.ConditionKey)))
        {
            var countable = g.Where(t => t.Countable).ToList();
            var m = new Dictionary<string, MetricSummary>(StringComparer.Ordinal);
            foreach (string name in countable.SelectMany(t => t.Metrics.Keys).Distinct())
            {
                MetricSummary? s = Statistics.Summarize(countable.Where(t => !(t.Truncated && SummaryBuilder.IsCompletionTime(name)))
                    .Where(t => t.Metrics.ContainsKey(name)).Select(t => t.Metrics[name]));
                if (s != null) m[name] = s;
            }
            list.Add(new Cond { Scenario = g.Key.Scenario, Key = g.Key.ConditionKey, P = g.First().Parameters, Trials = g.ToList(), M = m });
        }
        // Trials run in a shuffled order; the report lists scenarios in run order and conditions by key.
        var scenarioOrder = trials.Select(t => t.Scenario).Distinct().Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
        return list.OrderBy(c => scenarioOrder[c.Scenario]).ThenBy(c => c.Key, KeyOrder).ToList();
    }

    /// <summary>Condition keys with numbers ordered by value, so workers=16 follows workers=4.</summary>
    public static readonly IComparer<string> KeyOrder = Comparer<string>.Create(NaturalCompare);

    public static int NaturalCompare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                int byNumber = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (byNumber != 0) return byNumber;
            }
            else
            {
                int byChar = a[i].CompareTo(b[j]);
                if (byChar != 0) return byChar;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    public static (string Json, string Markdown) Analyze(string directory)
    {
        List<TrialResult> trials = ResultWriter.ReadTrials(directory);
        JsonElement? meta = ReadJson(Path.Combine(directory, "metadata.json"));
        JsonElement? soak = ReadJson(Path.Combine(directory, "soak-summary.json"));
        List<Cond> conds = Conditions(trials);
        int nproc = meta is { } mm && mm.TryGetProperty("machine", out JsonElement mach) && mach.TryGetProperty("logical_processors", out JsonElement lp)
            && lp.TryGetInt32(out int n) ? n : Environment.ProcessorCount;

        var findings = new List<Finding>();
        // Statements about a condition need at least one countable trial; the rest is reported under validity.
        List<Cond> counted = conds.Where(c => c.Countable > 0).ToList();
        var scaling = Scaling(counted, nproc, findings);
        Cadence(counted, findings);
        RamHold(counted, findings);
        MappedPack(counted, findings);
        MemoryUse(counted, findings);
        Contention(counted, scaling, nproc, findings);
        StopResume(counted, findings);
        Pressure(counted, findings);
        Faults(counted, findings);
        MissingCopies(counted, findings);
        Release(counted, trials, findings);
        Coexistence(counted, findings);
        Consistency(conds, meta, findings);
        Correctness(counted, meta, findings);
        Validity(trials, conds, nproc, meta, findings);
        if (soak is { } so) Soak(so, findings);

        var json = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["directory"] = Path.GetFileName(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)),
            ["counts"] = SummaryBuilder.Counts(trials),
            ["logical_processors"] = nproc,
            ["scaling"] = scaling,
            ["findings"] = findings,
        };
        string md = Markdown(directory, meta, soak, trials, conds, scaling, findings, nproc);
        return (JsonSerializer.Serialize(json, ResultWriter.Json), md);
    }

    public static void Write(string directory)
    {
        (string json, string md) = Analyze(directory);
        ResultWriter.WriteTextAtomic(Path.Combine(directory, "analysis.json"), json);
        ResultWriter.WriteTextAtomic(Path.Combine(directory, "analysis.md"), md);
    }

    private static JsonElement? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    // ---------------------------------------------------------------- scaling and bottlenecks

    private static readonly string[] ScalingScenarios = { "audit", "scan", "prefetch", "controller" };

    private static List<ScalingSeries> Scaling(List<Cond> conds, int nproc, List<Finding> findings)
    {
        var all = new List<ScalingSeries>();
        foreach (string scenario in ScalingScenarios)
        {
            var eligible = conds.Where(c => c.Scenario == scenario && c.Workers > 0 && c.Get("variant") is not ("rescan" or "refresh")
                && (scenario != "controller" || (c.Get("preset") == "default" && c.Get("external") == "none"))).ToList();
            foreach (var series in eligible.GroupBy(SeriesKey))
            {
                var points = series.OrderBy(c => c.Workers).Where(c => c.S("items_per_s") != null).ToList();
                if (points.Count < 2) continue;
                Cond? one = points.FirstOrDefault(c => c.Workers == 1);
                double baseRate = one?.Med("items_per_s") ?? double.NaN;
                var sp = new List<ScalingPoint>();
                foreach (Cond c in points)
                {
                    MetricSummary s = c.S("items_per_s")!;
                    double speedup = baseRate > 0 ? s.Median / baseRate : double.NaN;
                    double efficiency = speedup / c.Workers;
                    sp.Add(new ScalingPoint(c.Workers, s.N, s.Median, s.Min, s.Max, s.P05, s.P95, speedup, efficiency,
                        double.IsFinite(c.Med("mod_cpu_parallelism")) ? c.Med("mod_cpu_parallelism") : c.Med("cpu_parallelism"), c.Med("worker_busy_pct"), c.Med("run_queue_wait_pct"),
                        Bottleneck(c, conds, nproc, efficiency)));
                }
                int? plateau = Plateau(sp);
                string statement;
                ScalingPoint best = sp.OrderByDescending(p => p.Median).First();
                if (sp.Any(p => p.N < 3))
                    statement = "試行数が 3 未満の条件があり、スケーリングの判定は保留";
                else if (plateau is { } k && k < sp[^1].Workers)
                {
                    ScalingPoint at = sp.First(p => p.Workers == k);
                    double change = sp[^1].Median / at.Median - 1;
                    statement = change > MinGain
                        ? $"{k}→{sp[^1].Workers} ワーカーで中央値は {Change(change)} だが、全試行の範囲が重なるため増えたとは判定しない"
                        : $"{k} ワーカーを超えても増えない（{k}→{sp[^1].Workers}: {Change(change)}。増加とみなすのは +10% 超かつ全試行の範囲が重ならない場合）";
                    findings.Add(new Finding("scaling", "info", scenario, $"{series.Key}: {statement}",
                        new() { ["plateau_workers"] = k, ["rate_at_plateau"] = at.Median, ["rate_at_max"] = sp[^1].Median, ["max_workers"] = sp[^1].Workers }));
                }
                else
                    statement = $"最大 {sp[^1].Workers} ワーカーまで増加（最高 {F(best.Median)} items/s @ {best.Workers}）";
                all.Add(new ScalingSeries(scenario, series.Key, sp, plateau, statement));
            }
        }
        return all;
    }

    /// <summary>ResourceController's slow tick (TickMs 50 × SlowEvery 5), on which it pumps the audit and the file cache.</summary>
    public const int ControllerSlowTickMs = 250;

    /// <summary>The parameters of a condition other than the worker count: one scaling series.</summary>
    public static string SeriesKey(Cond c) => string.Join(";", c.P.Where(p => p.Key != "workers").Select(p => p.Key + "=" + p.Value));

    /// <summary>The smallest worker count that no larger count beats by more than 10% with separated ranges.</summary>
    public static int? Plateau(IReadOnlyList<ScalingPoint> points)
    {
        for (int i = 0; i < points.Count; i++)
        {
            bool beaten = false;
            for (int j = i + 1; j < points.Count; j++)
                if (points[j].Median > points[i].Median * (1 + MinGain) && points[j].Min > points[i].Max) beaten = true;
            if (!beaten) return points[i].Workers;
        }
        return null;
    }

    /// <summary>
    /// What limits one condition, from its own counters. Several causes can apply. Lock contention counts
    /// as a cause only where the scaling efficiency fell below 0.85: the counter is process-wide and a
    /// high rate alone does not show that it slowed the work.
    /// </summary>
    public static string Bottleneck(Cond c, IReadOnlyList<Cond> all, int nproc, double efficiency = double.NaN)
    {
        var causes = new List<string>();
        int w = Math.Max(1, c.Workers);
        int cores = Math.Min(w, nproc);
        // The mod threads' own CPU when known; process CPU also counts the harness and the runtime.
        double par = double.IsFinite(c.Med("mod_cpu_parallelism")) ? c.Med("mod_cpu_parallelism") : c.Med("cpu_parallelism");
        double busy = c.Med("worker_busy_pct"), wall = c.Med("wall_ms");
        // Run-queue wait of the process's threads per worker, and time a worker spent inside a job but
        // off the CPU (I/O, a lock, a page fault, or waiting for a CPU), both as shares of the wall time.
        double queued = c.Med("run_queue_wait_pct") / 100 / w;
        double offCpu = busy / 100 - par / w;
        bool blocked = busy >= 50 && offCpu - queued >= 0.2 * busy / 100;
        if (c.Get("variant") == "cadence")
        {
            Cond? sat = all.FirstOrDefault(o => o.Scenario == c.Scenario && o.Get("variant") == "saturated" && o.Workers == c.Workers && o.Get("cache") == c.Get("cache"));
            double ratio = sat == null ? double.NaN : c.Med("items_per_s") / sat.Med("items_per_s");
            if (ratio < 0.6) causes.Add($"Pump 間隔律速（saturated の {Ratio(ratio)}）");
        }
        if (c.Scenario == "controller" && c.Med("worker_cap") > 0)
        {
            // The controller pumps the audit on its slow tick with clamp(workers × 2, 1, 32) in flight.
            double cap = Math.Clamp(2 * c.Med("worker_cap"), 1, 32) * 1000.0 / ControllerSlowTickMs;
            if (c.Med("items_per_s") >= 0.85 * cap) causes.Add($"Pump 間隔律速（上限 {F(cap)} items/s）");
        }
        if (par >= 0.75 * cores) causes.Add("CPU 律速");
        else if (w <= nproc && queued >= 0.10) causes.Add($"CPU 競合（実行待ち {F(c.Med("run_queue_wait_pct"))}%）");
        double psi = c.Med("psi_io_some_ms"), ioWait = c.Med("io_wait_ms");
        bool cold = c.Get("cache") == "cold";
        if (cold && ((wall > 0 && psi / wall >= 0.3) || (wall > 0 && ioWait / wall >= 0.3 * w) || blocked))
            causes.Add("I/O 待ち律速");
        else if (!cold && blocked)
            causes.Add("ジョブ中の CPU 外の待ち（ロック・ページフォルトなど）");
        if (w > nproc) causes.Add($"vCPU 超過（実行待ち {F(c.Med("run_queue_wait_pct"))}%）");
        if (c.Med("lock_contentions_per_s") > 1000 && efficiency < 0.85) causes.Add("ロック競合");
        if (c.Med("gc_pause_pct") > 5) causes.Add("GC 停止");
        // One job reads one whole file: with a dominant file the others finish early and wait for it.
        double largest = c.Med("largest_file_share");
        if (causes.Count == 0 && busy < 70 && w > 1 && largest >= 0.5)
            causes.Add($"最大のファイル（{F(c.Med("largest_file_mib"))} MiB、読み取り量の {Ratio(largest)}）を 1 ジョブで読む時間");
        if (causes.Count == 0 && busy < 50) causes.Add("ワーカーが待機中（供給側律速）");
        return causes.Count == 0 ? "特定できず" : string.Join("、", causes);
    }

    /// <summary>One statement per scenario: the 250 ms cadence holds the rate at the in-flight limit × pumps per second.</summary>
    private static void Cadence(List<Cond> conds, List<Finding> findings)
    {
        foreach (string scenario in new[] { "audit", "prefetch" })
        {
            var rows = new List<(Cond Cad, double Rate, double Sat, double Cap)>();
            foreach (Cond c in conds.Where(c => c.Scenario == scenario && c.Get("variant") == "cadence"))
            {
                Cond? sat = conds.FirstOrDefault(o => o.Scenario == scenario && o.Get("variant") == "saturated" && o.Workers == c.Workers && o.Get("cache") == c.Get("cache"));
                if (sat == null) continue;
                double cad = c.Med("items_per_s"), s = sat.Med("items_per_s");
                double limit = c.Med("in_flight_limit"), interval = c.Med("pump_interval_ms");
                if (!(cad > 0 && s > 0 && interval > 0) || cad / s >= 0.6) continue;
                rows.Add((c, cad, s, limit * 1000.0 / interval));
            }
            if (rows.Count == 0) continue;
            double interval0 = rows[0].Cad.Med("pump_interval_ms");
            string perWorkers = string.Join("、", rows.GroupBy(r => r.Cad.Workers).OrderBy(g => g.Key)
                .Select(g => $"{g.Key} ワーカー {Range(g.Min(r => r.Rate), g.Max(r => r.Rate))}（上限 {F(g.First().Cap)}）"));
            var ev = new Dictionary<string, double>();
            foreach (var r in rows)
            {
                ev[r.Cad.Key + ":cadence_items_per_s"] = r.Rate;
                ev[r.Cad.Key + ":saturated_items_per_s"] = r.Sat;
                ev[r.Cad.Key + ":cadence_cap_items_per_s"] = r.Cap;
            }
            findings.Add(new Finding("cadence", "warning", scenario,
                $"Pump 間隔律速（{rows.Count} 条件）: {F(interval0)} ms ごとの Pump では、速度が 同時実行上限（ワーカー × 2）× {F(1000.0 / interval0)} 回/秒 に張り付いた（{perWorkers} items/s）。"
                + $"待ち行列が空くたびに Pump すると同じ条件で {Range(rows.Min(r => r.Sat), rows.Max(r => r.Sat))} items/s 出たので、250 ms 間隔の速度はその {RatioRange(rows.Min(r => r.Rate / r.Sat), rows.Max(r => r.Rate / r.Sat))}。ワーカー数より Pump 間隔が速度を決めている",
                ev));
        }
    }

    // ---------------------------------------------------------------- RAM retention

    private static void RamHold(List<Cond> conds, List<Finding> findings)
    {
        var rh = conds.Where(c => c.Scenario == "ram-hold").ToDictionary(c => c.Get("strategy") ?? "", c => c);
        if (rh.Count == 0) return;
        double Consumer(string s) => rh.TryGetValue(s, out Cond? c) ? c.Med("consumer.wall_ms") : double.NaN;
        double Storage(string s) => rh.TryGetValue(s, out Cond? c) ? c.Med("consumer.io_storage_read_mib") : double.NaN;
        double Anon(string s) => rh.TryGetValue(s, out Cond? c) ? c.Med("rss_anon_peak_mib") : double.NaN;
        double Held(string s) => rh.TryGetValue(s, out Cond? c) ? c.Med("native_held_mib") : double.NaN;
        if (rh.ContainsKey("none") && rh.ContainsKey("os-prefetch"))
            findings.Add(new Finding("ram-hold", "info", "ram-hold",
                $"OS キャッシュへの先読みで後続読み込みは {F(Consumer("none"))} ms → {F(Consumer("os-prefetch"))} ms、ストレージ読み取り {F(Storage("none"))} → {F(Storage("os-prefetch"))} MiB",
                new() { ["none_consumer_ms"] = Consumer("none"), ["os_consumer_ms"] = Consumer("os-prefetch"), ["none_storage_mib"] = Storage("none"), ["os_storage_mib"] = Storage("os-prefetch") }));
        if (rh.ContainsKey("os-prefetch") && rh.ContainsKey("hold-copies"))
        {
            string separation = Separation(rh["os-prefetch"].S("consumer.wall_ms"), rh["hold-copies"].S("consumer.wall_ms"));
            findings.Add(new Finding("ram-hold", "info", "ram-hold",
                $"RAM コピー保持は後続読み込みを {F(Consumer("os-prefetch"))} ms → {F(Consumer("hold-copies"))} ms にした（{separation}）一方、匿名メモリのピークは {F(Anon("os-prefetch"))} → {F(Anon("hold-copies"))} MiB（保持 {F(Held("hold-copies"))} MiB）。ゲームはこのコピーを読まないため、効果は OS キャッシュ分に限られる",
                new() { ["os_consumer_ms"] = Consumer("os-prefetch"), ["hold_consumer_ms"] = Consumer("hold-copies"), ["os_anon_peak_mib"] = Anon("os-prefetch"),
                    ["hold_anon_peak_mib"] = Anon("hold-copies"), ["held_mib"] = Held("hold-copies") }));
        }
        if (rh.ContainsKey("hold-evicted"))
        {
            string separation = rh.TryGetValue("none", out Cond? none) ? Separation(none.S("consumer.wall_ms"), rh["hold-evicted"].S("consumer.wall_ms")) : "保持なしの条件がない";
            findings.Add(new Finding("ram-hold", "warning", "ram-hold",
                $"コピーを {F(Held("hold-evicted"))} MiB 保持したまま OS キャッシュが追い出されると、後続読み込みはストレージから {F(Storage("hold-evicted"))} MiB を読んだ（保持なし {F(Storage("none"))} MiB）。"
                + $"時間は {F(Consumer("hold-evicted"))} ms（保持なし {F(Consumer("none"))} ms、{separation}）。ゲームはこのコピーを読まないため、保持はストレージからの読み取りを減らさない",
                new() { ["held_mib"] = Held("hold-evicted"), ["evicted_storage_mib"] = Storage("hold-evicted"), ["none_storage_mib"] = Storage("none"),
                    ["evicted_consumer_ms"] = Consumer("hold-evicted"), ["none_consumer_ms"] = Consumer("none") }));
        }
    }

    private static void MappedPack(List<Cond> conds, List<Finding> findings)
    {
        foreach (var byCache in conds.Where(c => c.Scenario == "mapped-pack").GroupBy(c => c.Get("cache")))
        {
            var d = byCache.ToDictionary(c => c.Get("limit") ?? "", c => c);
            if (d.TryGetValue("half", out Cond? half) && half.Med("deferred") > 0 && !(half.Med("mapped_files") > 0))
                findings.Add(new Finding("mapped-pack", "info", "mapped-pack",
                    $"cache={byCache.Key}: 上限 {F(half.Med("limit_mib"))} MiB がパック {F(half.Med("pack_mib"))} MiB より小さいと、そのパックは一部ではなく丸ごと常駐の対象外になる（見送り {F(half.Med("deferred"))} 件、常駐 0 MiB）",
                    new() { ["limit_mib"] = half.Med("limit_mib"), ["pack_mib"] = half.Med("pack_mib"), ["deferred"] = half.Med("deferred") }));
            if (!d.TryGetValue("none", out Cond? none) || !d.TryGetValue("full", out Cond? full)) continue;
            findings.Add(new Finding("mapped-pack", "info", "mapped-pack",
                $"cache={byCache.Key}: パック常駐なしのランダム読み取り {F(none.Med("consumer.wall_ms"))} ms（ストレージ {F(none.Med("consumer.io_storage_read_mib"))} MiB）、常駐ありで {F(full.Med("consumer.wall_ms"))} ms（{F(full.Med("consumer.io_storage_read_mib"))} MiB）。常駐の準備に {F(full.Med("touch.wall_ms"))} ms、RssFile {F(full.Med("rss_file_held_mib"))} MiB、ReleaseAll で {F(full.Med("rss_file_released_mib"))} MiB が戻った（{F(full.Med("release.wall_ms"))} ms）。上限を下げると {F(full.Med("shrink_files_closed"))} 個すべてのビューを閉じて最初からやり直す",
                new() { ["none_consumer_ms"] = none.Med("consumer.wall_ms"), ["full_consumer_ms"] = full.Med("consumer.wall_ms"), ["touch_ms"] = full.Med("touch.wall_ms"),
                    ["rss_file_held_mib"] = full.Med("rss_file_held_mib"), ["rss_file_released_mib"] = full.Med("rss_file_released_mib"), ["shrink_files_closed"] = full.Med("shrink_files_closed") }));
        }
    }

    // ---------------------------------------------------------------- memory, contention

    private static void MemoryUse(List<Cond> conds, List<Finding> findings)
    {
        foreach (Cond c in conds.Where(c => c.Scenario is "audit" or "scan" or "prefetch" && c.Get("variant") != "rescan"))
        {
            double items = c.Med("items"), alloc = c.Med("alloc_mib"), bytes = c.Med("bytes_mib"), read = c.Med("io_read_mib");
            if (!(items > 0)) continue;
            c.M["alloc_kib_per_item"] = Single(alloc * 1024 / items, c.Countable);
            if (bytes > 0 && read > 0) c.M["read_amplification"] = Single(read / bytes, c.Countable);
        }
        Cond? auditWarm = conds.Where(c => c.Scenario == "audit" && c.Get("variant") == "saturated" && c.Get("cache") == "warm").OrderByDescending(c => c.Workers).FirstOrDefault();
        if (auditWarm != null && auditWarm.Med("read_amplification") > 1.3)
            findings.Add(new Finding("memory", "info", "audit",
                $"索引化は対象 {F(auditWarm.Med("bytes_mib"))} MiB に対し {F(auditWarm.Med("io_read_mib"))} MiB を読んだ（{F(auditWarm.Med("read_amplification"))} 倍）。XML と PNG/DDS はハッシュ用と検査用に 2 回開いて読む",
                new() { ["bytes_mib"] = auditWarm.Med("bytes_mib"), ["io_read_mib"] = auditWarm.Med("io_read_mib"), ["amplification"] = auditWarm.Med("read_amplification") }));
        foreach (string scenario in new[] { "audit", "scan", "prefetch" })
        {
            Cond? c = conds.Where(x => x.Scenario == scenario && x.Get("cache") == "warm" && x.Get("variant") != "rescan" && x.Get("variant") != "refresh")
                .OrderByDescending(x => x.Workers).FirstOrDefault();
            if (c == null || !(c.Med("alloc_kib_per_item") > 0)) continue;
            findings.Add(new Finding("memory", "info", scenario,
                $"{c.Key}: 1 件あたり {F(c.Med("alloc_kib_per_item"))} KiB を管理ヒープに確保（{F(c.Med("alloc_mib_per_s"))} MiB/s）、GC 停止合計 {F(c.Med("gc_pause_ms"))} ms、最大 {F(c.Med("gc_pause_max_ms"))} ms、匿名 RSS ピーク {F(c.Med("rss_anon_peak_mib"))} MiB",
                new() { ["alloc_kib_per_item"] = c.Med("alloc_kib_per_item"), ["alloc_mib_per_s"] = c.Med("alloc_mib_per_s"), ["gc_pause_ms"] = c.Med("gc_pause_ms"),
                    ["gc_pause_max_ms"] = c.Med("gc_pause_max_ms"), ["rss_anon_peak_mib"] = c.Med("rss_anon_peak_mib") }));
        }
    }

    private static MetricSummary Single(double v, int n) => new(n, v, v, v, 0, 0, v, v, v, 0);

    /// <summary>
    /// Monitor contention above 1000/s, one statement per scenario. Counted as slowing the work only where
    /// the same condition's scaling efficiency fell below 0.85.
    /// </summary>
    private static void Contention(List<Cond> conds, List<ScalingSeries> scaling, int nproc, List<Finding> findings)
    {
        double Eff(Cond c) => scaling.FirstOrDefault(s => s.Scenario == c.Scenario && s.Series == SeriesKey(c))?.Points
            .FirstOrDefault(p => p.Workers == c.Workers)?.Efficiency ?? double.NaN;
        foreach (var g in conds.Where(c => c.Med("lock_contentions_per_s") > 1000).GroupBy(c => c.Scenario))
        {
            var list = g.ToList();
            var perItem = list.Select(c => c.Med("lock_contentions") / c.Med("items")).Where(double.IsFinite).ToList();
            var effs = list.Select(Eff).Where(double.IsFinite).ToList();
            var slow = list.Where(c => Eff(c) < 0.85).ToList();
            string text = $"Monitor のロック競合（プロセス全体の数）が {(list.Count == 1 ? list[0].Key + " " : list.Count + " 条件（" + string.Join("、", list.Select(c => c.Key)) + "）")}で "
                + $"{Range(list.Min(c => c.Med("lock_contentions_per_s")), list.Max(c => c.Med("lock_contentions_per_s")))} 回/秒"
                + (perItem.Count > 0 ? $"、1 件あたり {Range(perItem.Min(), perItem.Max())} 回" : "") + "。"
                + (slow.Count > 0 ? $"効率が 0.85 を下回った {string.Join("、", slow.Select(c => $"{c.Key}（効率 {F(Eff(c))}）"))} では律速要因の候補"
                    : effs.Count > 0 ? $"同じ条件の効率は {F(effs.Min())} 以上で、この競合が速度を落とした形跡はない"
                    : "スケーリング系列のない条件のため、速度への影響は判定しない")
                + (g.Key is "audit" or "prefetch" && list.Any(c => c.Get("variant") == "saturated")
                    ? "。saturated では Pump が数ジョブごとにワーカー上で同じロックを取るため、250 ms ごとのコントローラより競合が起きやすい" : "");
            var ev = list.ToDictionary(c => c.Key + ":lock_contentions_per_s", c => c.Med("lock_contentions_per_s"));
            findings.Add(new Finding("contention", slow.Count > 0 ? "warning" : "info", g.Key, text, ev));
        }
        foreach (Cond c in conds.Where(c => c.Workers > nproc && c.Med("run_queue_wait_pct") > 50))
            findings.Add(new Finding("contention", "info", c.Scenario,
                $"{c.Key}: {c.Workers} ワーカー > {nproc} vCPU で実行待ちが {F(c.Med("run_queue_wait_pct"))}%（スレッド合計の待ち時間/経過時間）。この条件は {c.Workers} コアのマシンの性能ではなく過剰割当を示す",
                new() { ["workers"] = c.Workers, ["nproc"] = nproc, ["run_queue_wait_pct"] = c.Med("run_queue_wait_pct") }));
    }

    // ---------------------------------------------------------------- stop/resume, pressure, faults, release

    private static void StopResume(List<Cond> conds, List<Finding> findings)
    {
        foreach (Cond c in conds.Where(c => c.Scenario == "cancel-resume"))
        {
            string action = c.Get("action") ?? "";
            var ev = new Dictionary<string, double>();
            foreach (string k in new[] { "drain_ms", "drain_max_ms", "paused_progress", "paused_read_mib", "paused_cpu_ms", "resume_ms", "completed", "stalled",
                         "reconfigure_ms", "hold_off_release_ms", "parked_jobs", "dropped_jobs", "rate_before_items_per_s", "rate_reduced_items_per_s", "rate_restored_items_per_s" })
                if (double.IsFinite(c.Med(k))) ev[k] = c.Med(k);
            bool stalled = c.Med("stalled") > 0;
            string text = action switch
            {
                "suspend" => $"{c.Key}: 停止要求から {F(c.Med("drain_ms"))} ms（最大 {F(c.Med("drain_max_ms"))} ms）で実行中のジョブが空になり、停止中の進捗 {F(c.Med("paused_progress"))} 件・読み取り {F(c.Med("paused_read_mib"))} MiB、再開まで {F(c.Med("resume_ms"))} ms",
                "reconfigure" => $"{c.Key}: ワーカー数の変更中も完走={F(c.Med("completed"))}、速度 {F(c.Med("rate_before_items_per_s"))} → {F(c.Med("rate_reduced_items_per_s"))} → {F(c.Med("rate_restored_items_per_s"))} items/s",
                "cancel-jobs" => stalled
                    ? $"{c.Key}: WorkerPool.CancelJobs で待機中のジョブ {F(c.Med("dropped_jobs"))} 件が捨てられ、AssetAudit の実行中カウントが減らず処理が止まった（現行 MOD では Dispose 時のみ呼ばれるため実害はないが、途中キャンセルに使うと停止する）"
                    : $"{c.Key}: CancelJobs 後も完走（捨てられたジョブ {F(c.Med("dropped_jobs"))} 件）",
                "manual-pause" => $"{c.Key}: 手動一時停止から {F(c.Med("drain_ms"))} ms で稼働ワーカー 0、停止中の進捗 {F(c.Med("paused_progress"))} 件・読み取り {F(c.Med("paused_read_mib"))} MiB、"
                    + (c.Med("parked_jobs") > 0 ? $"待機ジョブ {F(c.Med("parked_jobs"))} 件はキューに残り再開時に実行、" : "キューに残ったジョブなし、")
                    + $"再開まで {F(c.Med("resume_ms"))} ms",
                "settings-change" => $"{c.Key}: 上限 1 への変更が {F(c.Med("reconfigure_ms"))} ms で反映、RamHoldCopies をオフにすると {F(c.Med("hold_off_release_ms"))} ms で保持 {F(c.Med("held_before_toggle_mib"))} MiB を解放し先読みを最初からやり直す",
                _ => c.Key,
            };
            findings.Add(new Finding("stop-resume", stalled ? "warning" : "info", "cancel-resume", text, ev));
        }
    }

    private static void Pressure(List<Cond> conds, List<Finding> findings)
    {
        foreach (Cond c in conds.Where(c => c.Scenario == "memory-pressure"))
        {
            string target = c.Get("target") ?? "";
            findings.Add(new Finding("memory-pressure", c.Med("released") < 1 ? "warning" : "info", "memory-pressure",
                $"target={target}: 空き不足の通知から {F(c.Med("release_ms"))} ms で保持 {F(c.Med("held_before_mib"))} MiB を解放（解放={F(c.Med("released"))}）、不足中の読み取り {F(c.Med("read_during_shortage_mib"))} MiB、OS に戻った匿名 RSS {F(c.Med("rss_anon_returned_mib"))} MiB・ファイル RSS {F(c.Med("rss_file_returned_mib"))} MiB、回復後の再取得 {(c.Med("regrown") > 0 ? F(c.Med("regrow_ms")) + " ms" : F(c.Med("regrow_wait_s")) + " 秒以内になし" + (target == "copies" ? "（FileCache は解放後 30 秒待つ）" : ""))}",
                new() { ["release_ms"] = c.Med("release_ms"), ["held_before_mib"] = c.Med("held_before_mib"), ["read_during_shortage_mib"] = c.Med("read_during_shortage_mib"),
                    ["rss_anon_returned_mib"] = c.Med("rss_anon_returned_mib"), ["regrow_ms"] = c.Med("regrow_ms") }));
            double held = c.Med("held_before_mib"), returned = c.Med("rss_anon_returned_mib"), trimmed = c.Med("rss_anon_returned_after_trim_mib");
            if (target == "copies" && held > 0 && returned < 0.5 * held)
                findings.Add(new Finding("memory-pressure", "warning", "memory-pressure",
                    double.IsFinite(trimmed) && trimmed >= 0.5 * held
                        ? $"解放したネイティブコピー {F(held)} MiB のうち、解放直後に匿名 RSS として OS に戻ったのは {F(returned)} MiB。malloc_trim の後は {F(trimmed)} MiB 戻った（コピー以外に解放済みだった領域を含む）ので、残りは使用中ではなく glibc の malloc が解放済みとして抱えていた分（Windows のヒープは別の挙動で、ここでは確かめられない）"
                        : $"解放したネイティブコピー {F(held)} MiB のうち匿名 RSS として OS に戻ったのは {F(returned)} MiB（malloc_trim 後 {F(trimmed)} MiB）",
                    new() { ["held_before_mib"] = held, ["rss_anon_returned_mib"] = returned, ["rss_anon_returned_after_trim_mib"] = trimmed }));
        }
    }

    private static void Faults(List<Cond> conds, List<Finding> findings)
    {
        foreach (Cond c in conds.Where(c => c.Scenario == "io-faults"))
        {
            double expected = c.Med("expected_changed_files"), changed = c.Med("rescan_changed_files");
            // Out-of-order completion can let a fault land just before its file is indexed, so a small shortfall is tolerated.
            bool missed = expected > 0 && !(changed >= Math.Floor(expected * 0.8));
            bool bad = c.Med("completed") < 1 || c.Med("stalled") > 0 || c.Med("jobs_failed") > 0 || c.Med("rescan_complete") < 1 || missed;
            findings.Add(new Finding("io-faults", bad ? "warning" : "info", "io-faults",
                $"fault={c.Get("fault")}: 障害 {F(c.Med("faults_applied"))} 件（パス前 {F(c.Med("faults_before_pass"))}・索引済みファイルへ {F(c.Med("faults_after_indexed") + c.Med("faults_after_pass"))}）で完走={F(c.Med("completed"))}、停止={F(c.Med("stalled"))}、プールに漏れた例外 {F(c.Med("jobs_failed"))}、索引エラー {F(c.Med("audit_errors"))}・先読みエラー {F(c.Med("prefetch_errors"))}、"
                + $"2 回目のパス完了={F(c.Med("rescan_complete"))}・エラー {F(c.Med("rescan_errors"))}・内容変化の検出 {F(changed)} 件（期待 {F(expected)} 件）、古いコピーの解放 {F(c.Med("stale_copies_released_mib"))} MiB",
                new() { ["completed"] = c.Med("completed"), ["stalled"] = c.Med("stalled"), ["jobs_failed"] = c.Med("jobs_failed"), ["audit_errors"] = c.Med("audit_errors"),
                    ["prefetch_errors"] = c.Med("prefetch_errors"), ["rescan_changed_files"] = changed, ["expected_changed_files"] = expected,
                    ["rescan_complete"] = c.Med("rescan_complete"), ["stale_copies_released_mib"] = c.Med("stale_copies_released_mib") }));
        }
    }

    /// <summary>Copies the file cache keeps for files that were deleted, renamed or replaced.</summary>
    private static void MissingCopies(List<Cond> conds, List<Finding> findings)
    {
        var list = conds.Where(c => c.Scenario == "io-faults" && c.Med("copies_of_missing_files") > 0).ToList();
        if (list.Count == 0) return;
        findings.Add(new Finding("memory", "warning", "io-faults",
            $"削除・改名・置換で開けなくなったファイルのコピーが 2 回目のパス（リフレッシュ）後も RAM に残った（{string.Join("、", list.Select(c => $"{c.Get("fault")} {F(c.Med("copies_of_missing_files"))} 件・{F(c.Med("copies_of_missing_files_mib"))} MiB"))}）。"
            + "FileCache は開けなくなったファイルを訪問済みとして扱いコピーを解放しない。コントローラではファイル一覧の再走査で一覧の変化が見つかると全コピーを解放して読み直すため、それまで（または設定変更・メモリ不足・停止まで）保持される",
            list.ToDictionary(c => c.Get("fault") + ":copies_of_missing_files_mib", c => c.Med("copies_of_missing_files_mib"))));
    }

    private static void Release(List<Cond> conds, List<TrialResult> trials, List<Finding> findings)
    {
        foreach (var g in trials.Where(t => t.Status == TrialStatus.Ok && t.Metrics.GetValueOrDefault("leaked_mod_threads") > 0).GroupBy(t => t.Scenario + " " + t.ConditionKey))
            findings.Add(new Finding("release", "warning", g.First().Scenario,
                $"{g.Key}: 試行後 3 秒経っても MUR- スレッドが最大 {g.Max(t => t.Metrics["leaked_mod_threads"])} 本残った（{g.Count()} 試行）",
                new() { ["leaked_mod_threads_max"] = g.Max(t => t.Metrics["leaked_mod_threads"]), ["trials"] = g.Count() }));
        foreach (var g in trials.Where(t => t.Status == TrialStatus.Ok && t.Metrics.GetValueOrDefault("leaked_open_files") > 0).GroupBy(t => t.Scenario + " " + t.ConditionKey))
            findings.Add(new Finding("release", "warning", g.First().Scenario,
                $"{g.Key}: 試行後にファイル記述子が最大 {g.Max(t => t.Metrics["leaked_open_files"])} 個増えたまま（{g.Count()} 試行）",
                new() { ["leaked_open_files_max"] = g.Max(t => t.Metrics["leaked_open_files"]) }));
        foreach (Cond c in conds.Where(c => c.Scenario == "lifecycle"))
        {
            bool leak = c.Med("leaked_threads_max") > 0 || c.Med("open_files_slope_per_cycle") > 0.2 || c.Med("heap_slope_kib_per_cycle") > 64;
            findings.Add(new Finding("release", leak ? "warning" : "info", "lifecycle",
                $"kind={c.Get("kind")}: {F(c.Med("cycles"))} 回の生成・停止で残存スレッド最大 {F(c.Med("leaked_threads_max"))}、停止時間 中央値 {F(c.Med("shutdown_median_ms"))} ms・最大 {F(c.Med("shutdown_max_ms"))} ms、1 回あたりの増加 匿名 RSS {F(c.Med("rss_anon_slope_kib_per_cycle"))} KiB・管理ヒープ {F(c.Med("heap_slope_kib_per_cycle"))} KiB・FD {F(c.Med("open_files_slope_per_cycle"))}",
                new() { ["leaked_threads_max"] = c.Med("leaked_threads_max"), ["shutdown_median_ms"] = c.Med("shutdown_median_ms"), ["rss_anon_slope_kib_per_cycle"] = c.Med("rss_anon_slope_kib_per_cycle"),
                    ["heap_slope_kib_per_cycle"] = c.Med("heap_slope_kib_per_cycle"), ["open_files_slope_per_cycle"] = c.Med("open_files_slope_per_cycle") }));
        }
    }

    private static void Coexistence(List<Cond> conds, List<Finding> findings)
    {
        Cond? none = conds.FirstOrDefault(c => c.Scenario == "controller" && c.Get("external") == "none" && c.Get("workers") == "auto" && c.Get("preset") == "default" && c.Get("cache") == "warm");
        Cond? all = conds.FirstOrDefault(c => c.Scenario == "controller" && c.Get("external") == "all-active");
        if (none == null || all == null) return;
        bool same = none.S("workers_active_max")?.Max == all.S("workers_active_max")?.Max && none.Med("worker_cap") == all.Med("worker_cap");
        findings.Add(new Finding("coexistence", same ? "info" : "warning", "controller",
            same
                ? $"WOWGAG・Overclock・Axen を稼働中として通知しても、ワーカー上限 {F(all.Med("worker_cap"))}・最大稼働 {F(all.Med("workers_active_max"))} は通知なしと同じ（他MODを理由に上限を下げない設計どおり）"
                : $"他MOD稼働の通知でワーカー数が変わった: 上限 {F(none.Med("worker_cap"))} → {F(all.Med("worker_cap"))}、最大稼働 {F(none.Med("workers_active_max"))} → {F(all.Med("workers_active_max"))}",
            new() { ["cap_none"] = none.Med("worker_cap"), ["cap_all_active"] = all.Med("worker_cap"), ["active_none"] = none.Med("workers_active_max"), ["active_all_active"] = all.Med("workers_active_max") }));
    }

    private static void Consistency(List<Cond> conds, JsonElement? meta, List<Finding> findings)
    {
        void Check(string label, IEnumerable<TrialResult> trials)
        {
            // Only finished passes: a trial cut by its timeout has a partial result by design.
            var hashes = trials.Where(t => t.Countable && !t.Truncated && t.Metrics.ContainsKey("result_hash")).Select(t => t.Metrics["result_hash"]).Distinct().ToList();
            if (hashes.Count == 0) return;
            findings.Add(new Finding("consistency", hashes.Count == 1 ? "info" : "warning", label.Split(' ')[0],
                hashes.Count == 1 ? $"{label}: 全試行・全条件で結果（件数・重複・XML/テクスチャ集計）が一致" : $"{label}: 結果が {hashes.Count} 通りに分かれた（条件間で一致しない）",
                new() { ["distinct_results"] = hashes.Count }));
        }
        var all = conds.SelectMany(c => c.Trials).ToList();
        Check("audit", all.Where(t => t.Scenario == "audit" || (t.Scenario == "cancel-resume" && t.Parameters.GetValueOrDefault("level") == "component"
            && t.Parameters.GetValueOrDefault("action") != "cancel-jobs")));
        Check("scan xml", all.Where(t => t.Scenario == "scan" && t.Parameters.GetValueOrDefault("kind") == "xml"));
        Check("scan texture", all.Where(t => t.Scenario == "scan" && t.Parameters.GetValueOrDefault("kind") == "texture"));
        Check("controller", all.Where(t => (t.Scenario == "controller" && t.Parameters.GetValueOrDefault("preset") == "default")
            || (t.Scenario == "cancel-resume" && t.Parameters.GetValueOrDefault("level") == "controller")));
        Check("discovery assets", all.Where(t => t.Scenario == "discovery" && t.Parameters.GetValueOrDefault("target") == "assets"));
    }

    /// <summary>
    /// With generated input the expected audit result is known. Compares finished audit passes over the
    /// whole data set with what the generator planted.
    /// </summary>
    private static void Correctness(List<Cond> conds, JsonElement? meta, List<Finding> findings)
    {
        if (meta is not { } m || !m.TryGetProperty("generated_truth", out JsonElement truth) || truth.ValueKind != JsonValueKind.Object) return;
        if (Str(m, "dataset", "selected_files") != Str(m, "dataset", "discovered_files")) return; // a subset has no known totals
        TrialResult? run = conds.Where(c => c.Scenario == "audit").SelectMany(c => c.Trials)
            .FirstOrDefault(t => t.Countable && !t.Truncated && t.Metrics.GetValueOrDefault("items") > 0 && t.Metrics.ContainsKey("xml_checked"));
        if (run == null) return;
        int T(string name) => truth.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int i) ? i : 0;
        double M(string name) => run.Metrics.GetValueOrDefault(name, double.NaN);
        int after = T("MalformedXmlAfterDefName");
        var checks = new List<(string Label, double Expected, double Actual)>
        {
            ("XML ファイル", T("XmlFiles"), M("xml_checked")),
            ("壊れた XML", T("MalformedXml"), M("xml_parse_failures")),
            ("Def 定義数", T("Definitions"), M("xml_definitions")),
            ("defName の衝突", T("CollidingDefinitions"), M("xml_collisions")),
            ("正常なテクスチャ", T("ValidPng") + T("Dds"), M("textures_valid")),
            ("壊れた PNG", T("MalformedPng"), M("textures_malformed")),
            ("重複ファイル", T("DuplicateFiles"), M("duplicates")),
        };
        var wrong = checks.Where(c => c.Expected != c.Actual).ToList();
        var ev = checks.ToDictionary(c => c.Label, c => c.Actual);
        if (wrong.Count == 0)
        {
            findings.Add(new Finding("correctness", "info", "audit", "生成データに仕込んだ件数（XML・Def・衝突・テクスチャ・重複）と索引化の結果がすべて一致", ev));
            return;
        }
        string detail = string.Join("、", wrong.Select(w => $"{w.Label} 期待 {F(w.Expected)} → 結果 {F(w.Actual)}"));
        bool knownXmlGap = after > 0 && M("xml_parse_failures") == T("MalformedXml") - after && M("xml_definitions") == T("Definitions") + after
            && wrong.All(w => w.Label is "壊れた XML" or "Def 定義数");
        findings.Add(new Finding("correctness", "warning", "audit",
            knownXmlGap
                ? $"生成データとの照合: {detail}。defName の後で途切れた XML {after} 件を壊れていないと数え、その Def を数に含めた。XmlManifestScanner は defName を読むと ReadSubtree を途中で閉じ、残りを読み飛ばす際の構文エラーで外側のリーダーが Error 状態になっても Read() が false を返すだけで成功扱いになる（MOD の状態表示に出る統計値だけの問題で、ゲーム自身の XML 読み込みには関与しない）"
                : $"生成データとの照合で不一致: {detail}",
            ev));
    }

    private static void Validity(List<TrialResult> trials, List<Cond> conds, int nproc, JsonElement? meta, List<Finding> findings)
    {
        JsonElement m0 = meta ?? default;
        foreach (var g in trials.Where(t => t.Status == TrialStatus.Ok && !t.Valid && !t.Warmup).GroupBy(t => t.InvalidReason))
            findings.Add(new Finding("validity", "warning", g.First().Scenario, $"集計から除外 {g.Count()} 試行: {g.Key}", new() { ["trials"] = g.Count() }));
        foreach (var g in trials.Where(t => t.Status == TrialStatus.Failed).GroupBy(t => t.Scenario + ": " + t.Error))
            findings.Add(new Finding("validity", "warning", g.First().Scenario, $"失敗 {g.Count()} 試行 ({g.Key})", new() { ["trials"] = g.Count() }));
        double steal = Statistics.Percentile(trials.Where(t => t.Countable && t.Metrics.ContainsKey("sys_steal_pct")).Select(t => t.Metrics["sys_steal_pct"]), 0.5);
        if (steal > 5)
            findings.Add(new Finding("validity", "warning", "", $"VM のスティール時間の中央値が {F(steal)}%。他テナントの影響で値がぶれている可能性がある", new() { ["steal_pct_median"] = steal }));
        if (Str(m0, "dataset", "discovery_limit_reached") == "true")
            findings.Add(new Finding("validity", "warning", "", $"入力ファイルが MOD の発見上限 {Data.Dataset.DiscoveryLimit:N0} 件に達した。それ以降のファイルは MOD の先読み・索引の対象外で、結果にも含まれない", new()));
        if (int.TryParse(Str(m0, "dataset", "mods_with_older_version_folders_only"), out int older) && older > 0)
            findings.Add(new Finding("validity", "info", "", $"1.7 用フォルダがなく旧バージョン用フォルダだけを持つ MOD が {older} 個ある。ここではルートと Common だけを読み込み対象にした（ゲームが旧フォルダへ代替するかは未確認）", new() { ["mods"] = older }));
        var jit = trials.Where(t => t.Countable && t.Metrics.TryGetValue("jit_ms", out double j) && t.Metrics.TryGetValue("wall_ms", out double wall) && wall > 0 && j >= 0.05 * wall).ToList();
        if (jit.Count > 0)
            findings.Add(new Finding("validity", "info", "",
                $"{jit.Count} 試行で JIT コンパイルの時間が経過時間の 5% 以上（{string.Join("、", jit.GroupBy(t => t.Scenario).Select(g => g.Key + " " + g.Count()))}）。段階コンパイルが測定中に走ったため、短い試行の速度は低めに出ている可能性がある",
                new() { ["trials"] = jit.Count }));
        int truncated = trials.Count(t => t.Countable && t.Truncated);
        if (truncated > 0)
            findings.Add(new Finding("validity", "info", "", $"{truncated} 試行が試行時間の上限で打ち切られた（速度は有効、完了時間は集計しない）", new() { ["truncated"] = truncated }));
        if (conds.Any(c => c.Countable > 0 && c.Countable < 20))
            findings.Add(new Finding("validity", "info", "", "条件あたりの有効試行が 20 未満のため P95 は最大値に近く、参考値", new()));
    }

    private static void Soak(JsonElement so, List<Finding> findings)
    {
        if (so.TryGetProperty("leaks", out JsonElement leaks))
            foreach (JsonElement l in leaks.EnumerateArray())
            {
                string verdict = l.GetProperty("Verdict").GetString() ?? "";
                findings.Add(new Finding("soak", verdict == "suspect" ? "warning" : "info", "soak",
                    $"{l.GetProperty("Resource").GetString()}: {verdict}（{l.GetProperty("Reason").GetString()}、{l.GetProperty("Points").GetInt32()} 点、{l.GetProperty("SpanHours").GetDouble():F2} 時間）",
                    new() { ["slope_per_hour"] = l.GetProperty("SlopePerHour").GetDouble(), ["growth"] = l.GetProperty("Growth").GetDouble() }));
            }
        if (so.TryGetProperty("stall_suspected", out JsonElement st) && st.ValueKind == JsonValueKind.True)
            findings.Add(new Finding("soak", "warning", "soak", $"作業が続くはずの段階で {so.GetProperty("max_no_progress_active_s").GetDouble():F0} 秒進捗がなかった", new()));
    }

    // ---------------------------------------------------------------- report

    private static string Markdown(string dir, JsonElement? meta, JsonElement? soak, List<TrialResult> trials, List<Cond> conds,
        List<ScalingSeries> scaling, List<Finding> findings, int nproc)
    {
        var md = new StringBuilder();
        md.AppendLine("# MoreUseResources ベンチマーク分析");
        md.AppendLine();
        md.AppendLine("この分析は Linux 上でゲームなしに MOD のコードを直接動かした測定から機械的に作られている。FPS・TPS・ロード時間の改善は測っていない。各数値は有効試行の中央値で、範囲は全試行の最小〜最大。");
        md.AppendLine();
        md.AppendLine("## 実行条件");
        md.AppendLine();
        md.AppendLine("| 項目 | 値 |");
        md.AppendLine("|---|---|");
        void Row(string k, string? v) => md.AppendLine($"| {k} | {Esc(v ?? "-")} |");
        Row("結果ディレクトリ", Path.GetFileName(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar)));
        if (meta is { } m)
        {
            string? dirty = Str(m, "code", "dirty_tracked_files");
            Row("コミット", Str(m, "code", "commit") + (dirty is "true" ? "（未コミットの変更あり: 結果はこのコミットそのものではない）" : ""));
            Row("ブランチ", Str(m, "code", "branch"));
            Row("開始 (UTC)", Str(m, "started_utc"));
            Row("マシン", $"{NonEmpty(Str(m, "machine", "gce", "machine_type")) ?? NonEmpty(Str(m, "machine", "dmi_product")) ?? "種別不明"} / {Str(m, "machine", "cpu_model")} / vCPU {Str(m, "machine", "logical_processors")} / メモリ {Str(m, "machine", "mem_total_mib")} MiB");
            Row("OS・カーネル", $"{Str(m, "machine", "os_pretty_name")} / {Str(m, "machine", "kernel")}");
            Row(".NET", $"{Str(m, "runtime", "framework")}（Server GC={Str(m, "runtime", "server_gc")}、{Str(m, "runtime", "gc_latency_mode")}）");
            Row("モード・試行", $"{Str(m, "options", "mode")}、試行 {Str(m, "options", "trials")}、ウォームアップ {Str(m, "options", "warmup")}、ワーカー {Join(m, "options", "workers")}、キャッシュ {Join(m, "options", "caches")}（cold は {Str(m, "options", "cold_method")}）");
            Row("入力データ", $"{Str(m, "dataset", "kind")}: {Str(m, "dataset", "selected_files")}/{Str(m, "dataset", "discovered_files")} ファイル {Str(m, "dataset", "selected_mib")} MiB、パック {Str(m, "dataset", "packs")} 個 {Str(m, "dataset", "pack_mib")} MiB、構成指紋 {Str(m, "dataset", "shape_fingerprint")}");
            Row("計測機能", $"PSI={Str(m, "kernel_features", "pressure_stall_information")}、スレッド別 I/O 待ち={Str(m, "kernel_features", "per_thread_io_wait")}、mincore={Str(m, "kernel_features", "mincore_and_fadvise")}");
        }
        var counts = SummaryBuilder.Counts(trials);
        Row("試行数", $"合計 {counts["total"]}、集計対象 {counts["countable"]}、ウォームアップ {counts["warmup"]}、除外 {counts["not_counted"]}、失敗 {counts["failed"]}、中断 {counts["interrupted"]}、スキップ {counts["skipped"]}");
        md.AppendLine();

        md.AppendLine("## 主な所見");
        md.AppendLine();
        foreach (var f in findings.Where(f => f.Topic != "validity").OrderBy(f => f.Severity == "warning" ? 0 : 1))
            md.AppendLine($"- {(f.Severity == "warning" ? "**[注意]** " : "")}[{f.Scenario}] {f.Text}");
        md.AppendLine();
        md.AppendLine("## データの有効性");
        md.AppendLine();
        var validity = findings.Where(f => f.Topic == "validity").ToList();
        if (validity.Count == 0) md.AppendLine("- 除外・失敗した試行はない。");
        foreach (var f in validity) md.AppendLine($"- {f.Text}");
        md.AppendLine();

        md.AppendLine("## ワーカー数とスループット");
        md.AppendLine();
        md.AppendLine("速度比は 1 ワーカー比、効率は速度比 ÷ ワーカー数。`cpu並列度` は MOD のスレッド（MUR-*）の CPU 時間 ÷ 経過時間（取れない場合はプロセス全体）。`稼働率` はワーカーがジョブを実行していた割合。`実行待ち%` はプロセスの全スレッドが CPU の空きを待った時間の合計 ÷ 経過時間。"
            + "`variant=cadence` はコントローラと同じ 250 ms ごと、`variant=saturated` はワーカーが待ち行列のジョブを取り終えるたびに Pump を呼ぶ（プールの待ち行列に Pump 用のジョブを入れて回す。作業自体の処理能力で、ゲーム内の速度ではない）。");
        md.AppendLine();
        foreach (var s in scaling)
        {
            md.AppendLine($"### {s.Scenario} `{s.Series}`");
            md.AppendLine();
            md.AppendLine(s.Statement);
            md.AppendLine();
            md.AppendLine("| ワーカー | n | items/s 中央値 | 範囲 | 速度比 | 効率 | cpu並列度 | 稼働率% | 実行待ち% | 律速要因 |");
            md.AppendLine("|---:|---:|---:|---|---:|---:|---:|---:|---:|---|");
            foreach (var p in s.Points)
                md.AppendLine($"| {p.Workers}{(p.Workers > nproc ? "*" : "")} | {p.N} | {F(p.Median)} | {F(p.Min)}–{F(p.Max)} | {F(p.Speedup)} | {F(p.Efficiency)} | {F(p.CpuParallelism)} | {F(p.WorkerBusyPct)} | {F(p.RunQueueWaitPct)} | {p.Bottleneck} |");
            if (s.Points.Any(p => p.Workers > nproc)) md.AppendLine($"\n\\* vCPU 数（{nproc}）を超える条件。{nproc} vCPU 上での過剰割当の結果で、より多いコアのマシンの性能ではない。");
            md.AppendLine();
        }

        md.AppendLine("## シナリオ別の主要値");
        md.AppendLine();
        string[] headline = { "items_per_s", "mib_per_s", "wall_ms", "cpu_parallelism", "rss_anon_peak_mib", "alloc_mib_per_s", "gc_pause_max_ms", "io_storage_read_mib" };
        foreach (var group in conds.GroupBy(c => c.Scenario))
        {
            md.AppendLine($"### {group.Key}");
            md.AppendLine();
            md.AppendLine("| 条件 | n | " + string.Join(" | ", headline) + " |");
            md.AppendLine("|---|---:|" + string.Join("|", headline.Select(_ => "---:")) + "|");
            foreach (Cond c in group)
                md.AppendLine($"| `{c.Key}` | {c.Countable} | " + string.Join(" | ", headline.Select(h => c.S(h) is { } s ? $"{F(s.Median)} (CV {F(s.CoefficientOfVariation * 100)}%)" : "-")) + " |");
            md.AppendLine();
        }

        if (soak is { } so)
        {
            md.AppendLine("## 長時間実行");
            md.AppendLine();
            md.AppendLine($"- 実行 {Num(so, "elapsed_minutes"):F1} 分（予定 {Num(so, "planned_minutes"):F1} 分）、中断={Str(so, "interrupted")}、コントローラ再生成 {Str(so, "recycles")} 回、制御エラー {Str(so, "control_errors")} 回、停止後に残ったスレッド最大 {Str(so, "leaked_mod_threads_max")}");
            md.AppendLine($"- {Str(so, "accelerated_intervals")}");
            md.AppendLine();
        }

        md.AppendLine("## このベンチマークで再現できないこと");
        md.AppendLine();
        md.AppendLine("- ゲーム本体（Unity/Waylith のメインスレッド、フレーム時間、GPU、テクスチャ転送）。FPS・TPS・ゲーム内ロード時間は測っていない。");
        md.AppendLine("- Windows の I/O・メモリ管理（NTFS、スタンバイリスト、ウイルス対策ソフト）。ゲームの実行環境は Windows 版 1.7.5011（WaylithCLR 10.0.15）で、ここは標準の .NET ランタイム。");
        md.AppendLine("- 他MOD（WOWGAG's Performance Patch、Axen's Performance Enhancements、Overclock）の実際の並列処理。ここでは稼働中という通知だけを与えている。");
        foreach (var g in conds.GroupBy(c => c.Scenario))
        {
            string? text = BenchmarkRunner.Catalog().FirstOrDefault(s => s.Id == g.Key)?.NotReproduced;
            if (!string.IsNullOrEmpty(text)) md.AppendLine($"- {g.Key}: {text}");
        }
        return md.ToString();
    }

    private static bool Overlap(MetricSummary? a, MetricSummary? b) => a == null || b == null || !(a.Max < b.Min || b.Max < a.Min);

    /// <summary>Whether two conditions differ by the comparison rule: at least 3 trials each and separated ranges.</summary>
    public static string Separation(MetricSummary? a, MetricSummary? b) =>
        a == null || b == null || a.N < Comparer.MinTrials || b.N < Comparer.MinTrials ? $"各 {Comparer.MinTrials} 試行未満のため差は判定しない"
        : Overlap(a, b) ? "範囲が重なり差は判定できない" : "範囲は分離";

    private static string? Str(JsonElement e, params string[] path)
    {
        JsonElement cur = e;
        foreach (string p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return null;
        }
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => cur.GetRawText(),
        };
    }

    private static string Join(JsonElement e, params string[] path)
    {
        JsonElement cur = e;
        foreach (string p in path)
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return "-";
        return cur.ValueKind == JsonValueKind.Array ? string.Join(",", cur.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())) : cur.GetRawText();
    }

    private static double Num(JsonElement e, string key) => e.TryGetProperty(key, out JsonElement v) && v.TryGetDouble(out double d) ? d : double.NaN;

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string Esc(string s) => s.Replace("|", "\\|", StringComparison.Ordinal);

    public static string F(double v)
    {
        if (!double.IsFinite(v)) return "-";
        double a = Math.Abs(v);
        // Counts and other whole numbers print without decimals.
        string fmt = a >= 1000 || (a < 1e12 && v == Math.Floor(v)) ? "F0" : a >= 100 ? "F1" : a >= 1 ? "F2" : "G3";
        return v.ToString(fmt, CultureInfo.InvariantCulture);
    }

    /// <summary>A share, e.g. 0.35 → "35%", 0.0045 → "0.45%".</summary>
    public static string Ratio(double ratio)
    {
        if (!double.IsFinite(ratio)) return "-";
        double pct = ratio * 100;
        return pct.ToString(Math.Abs(pct) >= 10 || pct == 0 ? "F0" : Math.Abs(pct) >= 1 ? "F1" : "G2", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>"a〜b", or one value when both print the same.</summary>
    public static string Range(double lo, double hi) => F(lo) == F(hi) ? F(lo) : F(lo) + "〜" + F(hi);

    private static string RatioRange(double lo, double hi) => Ratio(lo) == Ratio(hi) ? Ratio(lo) : Ratio(lo) + "〜" + Ratio(hi);

    /// <summary>A relative change, e.g. 0.05 → "+5%".</summary>
    public static string Change(double change) => double.IsFinite(change) ? (change * 100).ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%" : "-";
}
