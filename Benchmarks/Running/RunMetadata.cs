using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MoreUseResources.Benchmarks.Measurement;
using MoreUseResources.Core;

namespace MoreUseResources.Benchmarks.Running;

/// <summary>
/// Everything needed to decide whether two result sets are comparable: code, runtime, machine,
/// kernel features, dataset and options. Host names, user names and absolute home paths are not
/// recorded, so result files can be attached to GitHub as they are.
/// </summary>
public static class RunMetadata
{
    public static Dictionary<string, object?> Collect(BenchOptions options, Data.Dataset? dataset, string runId)
    {
        var meta = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["run_id"] = runId,
            ["started_utc"] = DateTime.UtcNow.ToString("o"),
            ["options"] = options.Describe(),
            ["code"] = Code(),
            ["runtime"] = Runtime(),
            ["machine"] = Machine(options),
            ["kernel_features"] = KernelFeatures(),
            ["mod_defaults"] = ModDefaults(),
        };
        if (dataset != null) meta["dataset"] = dataset.Describe();
        return meta;
    }

    public static Dictionary<string, object?> Code()
    {
        string? repo = FindRepositoryRoot();
        string? sha = Environment.GetEnvironmentVariable("MUR_BENCH_COMMIT");
        bool? dirty = null;
        string? branch = null;
        if (repo != null)
        {
            sha ??= Git(repo, "rev-parse HEAD");
            branch = Git(repo, "rev-parse --abbrev-ref HEAD");
            string? status = Git(repo, "status --porcelain --untracked-files=no");
            dirty = status == null ? null : status.Length > 0;
        }
        Assembly core = typeof(ResourceController).Assembly;
        string? modDll = repo == null ? null : Path.Combine(repo, "Assemblies", "MoreUseResources.dll");
        return new Dictionary<string, object?>
        {
            ["commit"] = sha ?? "unknown",
            ["branch"] = branch,
            ["dirty_tracked_files"] = dirty,
            ["core_assembly_sha256"] = HashFile(core.Location),
            ["core_assembly_mvid"] = core.ManifestModule.ModuleVersionId.ToString(),
            ["mod_dll_sha256"] = modDll != null && File.Exists(modDll) ? HashFile(modDll) : null,
            ["benchmark_assembly_mvid"] = typeof(RunMetadata).Assembly.ManifestModule.ModuleVersionId.ToString(),
        };
    }

    public static Dictionary<string, object?> Runtime()
    {
        var gcConfig = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in GC.GetConfigurationVariables()) gcConfig[pair.Key] = pair.Value;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException) { }
        var env = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            string key = (string)e.Key;
            // Only runtime tuning variables; nothing that could hold credentials or paths of the user.
            if (key.StartsWith("DOTNET_gc", StringComparison.OrdinalIgnoreCase) || key.StartsWith("DOTNET_GC", StringComparison.Ordinal)
                || key.StartsWith("DOTNET_Tiered", StringComparison.Ordinal) || key.StartsWith("DOTNET_TC_", StringComparison.Ordinal)
                || key.StartsWith("DOTNET_ReadyToRun", StringComparison.Ordinal) || key.StartsWith("COMPlus_gc", StringComparison.OrdinalIgnoreCase))
                env[key] = (string?)e.Value ?? "";
        }
        return new Dictionary<string, object?>
        {
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["rid"] = RuntimeInformation.RuntimeIdentifier,
            ["os_description"] = RuntimeInformation.OSDescription,
            ["process_architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["server_gc"] = GCSettings.IsServerGC,
            ["gc_latency_mode"] = GCSettings.LatencyMode.ToString(),
            ["gc_configuration"] = gcConfig,
            ["runtime_environment"] = env,
            ["game_runtime_note"] = "RimWorld 1.7.5011 uses WaylithCLR 10.0.15 with no GC settings in RimWorld.runtimeconfig.json; this run uses the stock .NET runtime above.",
        };
    }

    public static Dictionary<string, object?> Machine(BenchOptions options)
    {
        var result = new Dictionary<string, object?>
        {
            ["logical_processors"] = Environment.ProcessorCount,
            ["os_pretty_name"] = OsRelease("PRETTY_NAME"),
            ["kernel"] = ProcFs.TryRead("/proc/sys/kernel/osrelease")?.Trim(),
            ["cpu_model"] = CpuModel(),
            ["cpu_flags_sha_ni"] = CpuHasFlag("sha_ni"),
            ["cgroup_cpu_max"] = ProcFs.TryRead("/sys/fs/cgroup/cpu.max")?.Trim(),
            ["cgroup_memory_max"] = ProcFs.TryRead("/sys/fs/cgroup/memory.max")?.Trim(),
            ["transparent_hugepage"] = ProcFs.TryRead("/sys/kernel/mm/transparent_hugepage/enabled")?.Trim(),
            ["dmi_product"] = ProcFs.TryRead("/sys/class/dmi/id/product_name")?.Trim(),
            ["dmi_vendor"] = ProcFs.TryRead("/sys/class/dmi/id/sys_vendor")?.Trim(),
        };
        if (ProcFs.TryRead("/proc/meminfo") is { } meminfo)
        {
            var m = ProcFs.ParseStatus(meminfo);
            result["mem_total_mib"] = m.TryGetValue("MemTotal", out long t) ? t / 1048576 : null;
            result["swap_total_mib"] = m.TryGetValue("SwapTotal", out long s) ? s / 1048576 : null;
            result["mem_available_at_start_mib"] = m.TryGetValue("MemAvailable", out long a) ? a / 1048576 : null;
        }
        result["output_filesystem"] = FileSystemOf(options.OutputDirectory);
        if ((result["dmi_product"] as string)?.Contains("Google", StringComparison.OrdinalIgnoreCase) == true)
            result["gce"] = GoogleComputeEngine();
        return result;
    }

    public static Dictionary<string, object?> KernelFeatures()
    {
        string? delay = ProcFs.TryRead("/proc/sys/kernel/task_delayacct")?.Trim();
        return new Dictionary<string, object?>
        {
            ["procfs"] = OperatingSystem.IsLinux() && Directory.Exists("/proc/self"),
            ["task_delayacct"] = delay,
            ["per_thread_io_wait"] = delay == "1",
            ["pressure_stall_information"] = File.Exists("/proc/pressure/io"),
            ["mincore_and_fadvise"] = PageCache.Supported,
            ["peak_rss_reset"] = ProcessProbe.ResetPeakRss(),
            ["io_accounting"] = ProcFs.TryRead("/proc/self/io") != null,
            ["clock_ticks_per_s"] = ProcessProbe.ClockTicksPerSecond,
            ["thread_priority_lowest_nice"] = ObservedLowestPriorityNice(),
        };
    }

    /// <summary>The mod's default settings, so a later change of a default is visible in comparisons.</summary>
    public static Dictionary<string, object?> ModDefaults()
    {
        var s = new ResourceSettings();
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (FieldInfo f in typeof(ResourceSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object? v = f.GetValue(s);
            result[f.Name] = v is Array a ? a.Cast<object>().Select(x => x.ToString()).ToArray() : v?.ToString();
        }
        return new Dictionary<string, object?>(result);
    }

    /// <summary>
    /// The mod runs its workers at ThreadPriority.Lowest. On Linux the runtime does not change the
    /// scheduling priority for that, which this check records from the thread's real nice value.
    /// </summary>
    public static int? ObservedLowestPriorityNice()
    {
        if (!OperatingSystem.IsLinux()) return null;
        int? nice = null;
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            ready.Set();
            release.Wait(2000);
        }) { IsBackground = true, Name = "Bench-NiceProbe", Priority = ThreadPriority.Lowest };
        t.Start();
        ready.Wait(2000);
        foreach (int id in ProcessProbe.ThreadIds())
        {
            if (ProcessProbe.ThreadName(id) != "Bench-NiceProbe") continue;
            if (ProcFs.TryRead("/proc/self/task/" + id + "/stat") is { } stat && ProcFs.ParseTaskStat(stat) is { } parsed)
                nice = parsed.Nice;
        }
        release.Set();
        t.Join(2000);
        return nice;
    }

    public static string? FindRepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        return null;
    }

    private static string? Git(string repo, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "-C \"" + repo + "\" " + arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? p = Process.Start(psi);
            if (p == null) return null;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public static string? HashFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string? OsRelease(string key)
    {
        string? text = ProcFs.TryRead("/etc/os-release");
        if (text == null) return null;
        foreach (string line in text.Split('\n'))
            if (line.StartsWith(key + "=", StringComparison.Ordinal)) return line.Substring(key.Length + 1).Trim('"');
        return null;
    }

    private static string? CpuModel()
    {
        string? text = ProcFs.TryRead("/proc/cpuinfo");
        if (text == null) return null;
        foreach (string line in text.Split('\n'))
            if (line.StartsWith("model name", StringComparison.Ordinal)) return line.Substring(line.IndexOf(':') + 1).Trim();
        return null;
    }

    private static bool? CpuHasFlag(string flag)
    {
        string? text = ProcFs.TryRead("/proc/cpuinfo");
        if (text == null) return null;
        foreach (string line in text.Split('\n'))
            if (line.StartsWith("flags", StringComparison.Ordinal))
                return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(flag);
        return null;
    }

    /// <summary>Mount point and file system type of the longest matching mount (from /proc/self/mounts).</summary>
    public static Dictionary<string, object?>? FileSystemOf(string path)
    {
        string? mounts = ProcFs.TryRead("/proc/self/mounts");
        if (mounts == null) return null;
        string full = Path.GetFullPath(path);
        string? bestMount = null, bestType = null, bestDevice = null;
        foreach (string line in mounts.Split('\n'))
        {
            string[] f = line.Split(' ');
            if (f.Length < 3) continue;
            string mount = f[1].Replace("\\040", " ", StringComparison.Ordinal);
            bool inside = full == mount || full.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal) || mount == "/";
            if (inside && (bestMount == null || mount.Length > bestMount.Length))
            {
                bestMount = mount;
                bestType = f[2];
                bestDevice = f[0];
            }
        }
        long? free = null;
        try
        {
            Directory.CreateDirectory(full);
            free = new DriveInfo(full).AvailableFreeSpace / 1048576;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new Dictionary<string, object?>
        {
            ["type"] = bestType,
            ["device"] = bestDevice != null && bestDevice.StartsWith("/dev/", StringComparison.Ordinal) ? bestDevice : bestType,
            ["free_mib_at_start"] = free,
        };
    }

    /// <summary>Machine type, zone and CPU platform from the GCE metadata server (instance-local, 1 s timeout).</summary>
    private static Dictionary<string, object?> GoogleComputeEngine()
    {
        var result = new Dictionary<string, object?>();
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
            http.DefaultRequestHeaders.Add("Metadata-Flavor", "Google");
            foreach (string item in new[] { "machine-type", "zone", "cpu-platform" })
            {
                string value = http.GetStringAsync("http://metadata.google.internal/computeMetadata/v1/instance/" + item).GetAwaiter().GetResult();
                // machine-type and zone come as projects/<number>/...; keep only the last segment.
                result[item.Replace('-', '_')] = value.Contains('/') ? value.Substring(value.LastIndexOf('/') + 1) : value;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            result["error"] = ex.GetType().Name;
        }
        return result;
    }
}
