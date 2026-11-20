using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoreUseResources.Benchmarks.Running;

/// <summary>
/// Writes results incrementally: each trial is appended and flushed as soon as it ends, so an
/// interrupted run keeps every completed trial. JSON summaries are replaced atomically.
/// </summary>
public sealed class ResultWriter : IDisposable
{
    /// <summary>Columns of trials.csv. Every metric is also in metrics.csv in long form.</summary>
    public static readonly string[] HeadlineMetrics =
    {
        "wall_ms", "items", "bytes_mib", "items_per_s", "mib_per_s", "cpu_ms", "cpu_util_pct", "cpu_parallelism",
        "worker_busy_pct", "rss_peak_mib", "rss_anon_peak_mib", "native_peak_mib", "rss_anon_delta_mib", "rss_file_delta_mib",
        "gc_gen0", "gc_gen1", "gc_gen2", "gc_pause_ms", "gc_pause_max_ms", "gc_pause_p95_ms", "alloc_mib_per_s",
        "io_storage_read_mib", "io_read_mib", "io_wait_ms", "psi_io_some_ms", "sys_iowait_pct", "sys_steal_pct",
        "run_queue_wait_pct", "lock_contentions_per_s", "errors", "cache_resident_before", "teardown_ms",
        "leaked_mod_threads", "leaked_open_files",
    };

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };

    private readonly StreamWriter _trialsCsv;
    private readonly StreamWriter _metricsCsv;
    private readonly StreamWriter _jsonl;
    private readonly object _gate = new();

    public ResultWriter(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        _trialsCsv = Open("trials.csv", "run_id,seq,scenario,condition,round,warmup,status,valid,truncated,cache_requested,cache_verified,"
            + string.Join(",", HeadlineMetrics) + ",invalid_reason,error");
        _metricsCsv = Open("metrics.csv", "run_id,seq,scenario,condition,round,warmup,countable,metric,value");
        _jsonl = new StreamWriter(new FileStream(Path.Combine(directory, "trials.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    }

    public string Directory { get; }

    private StreamWriter Open(string name, string header)
    {
        string path = Path.Combine(Directory, name);
        bool exists = File.Exists(path) && new FileInfo(path).Length > 0;
        var writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        if (!exists)
        {
            writer.WriteLine(header);
            writer.Flush();
        }
        return writer;
    }

    public void Append(TrialResult r)
    {
        lock (_gate)
        {
            var row = new List<string>
            {
                r.RunId, r.Sequence.ToString(CultureInfo.InvariantCulture), r.Scenario, r.ConditionKey,
                r.Round.ToString(CultureInfo.InvariantCulture), r.Warmup ? "1" : "0", r.Status.ToString().ToLowerInvariant(),
                r.Valid ? "1" : "0", r.Truncated ? "1" : "0", r.CacheRequested,
                r.CacheVerified switch { true => "1", false => "0", null => "" },
            };
            foreach (string m in HeadlineMetrics)
                row.Add(r.Metrics.TryGetValue(m, out double v) ? Format(v) : "");
            row.Add(r.InvalidReason ?? "");
            row.Add(r.Error ?? "");
            _trialsCsv.WriteLine(string.Join(",", row.Select(Csv)));
            _trialsCsv.Flush();
            foreach (var pair in r.Metrics.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                _metricsCsv.WriteLine(string.Join(",", new[]
                {
                    r.RunId, r.Sequence.ToString(CultureInfo.InvariantCulture), r.Scenario, r.ConditionKey,
                    r.Round.ToString(CultureInfo.InvariantCulture), r.Warmup ? "1" : "0", r.Countable ? "1" : "0",
                    pair.Key, Format(pair.Value),
                }.Select(Csv)));
            }
            _metricsCsv.Flush();
            _jsonl.WriteLine(JsonSerializer.Serialize(r, JsonLine));
            _jsonl.Flush();
        }
    }

    public static string Format(double v) => v.ToString("G10", CultureInfo.InvariantCulture);

    public static string Csv(string field)
    {
        if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return field;
        return "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public void WriteJson(string name, object value) => WriteJsonAtomic(Path.Combine(Directory, name), value);

    public static void WriteJsonAtomic(string path, object value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void WriteTextAtomic(string path, string text)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static List<TrialResult> ReadTrials(string directory)
    {
        var list = new List<TrialResult>();
        string path = Path.Combine(directory, "trials.jsonl");
        if (!File.Exists(path)) return list;
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                TrialResult? r = JsonSerializer.Deserialize<TrialResult>(line, Json);
                if (r != null) list.Add(r);
            }
            catch (JsonException) { /* a line cut by a hard kill */ }
        }
        return list;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _trialsCsv.Dispose();
            _metricsCsv.Dispose();
            _jsonl.Dispose();
        }
    }
}
