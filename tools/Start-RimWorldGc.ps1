param(
    [Parameter(Mandatory = $true)]
    [string] $GameExecutable,

    [ValidateSet('Default', 'Workstation', 'Server')]
    [string] $Profile = 'Workstation',

    [ValidateRange(0, 64)]
    [int] $ServerHeapCount = 0,

    [switch] $GpuTextureEncoding,

    [string] $StartupPackManifest,

    [ValidateRange(64, 16384)]
    [int] $WarmPackBudgetMiB = 2048,

    [ValidateRange(1, 60)]
    [int] $WarmTimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'
if ($ServerHeapCount -gt 0 -and $Profile -ne 'Server') {
    throw 'ServerHeapCount requires the Server profile.'
}

$game = Get-Item -LiteralPath $GameExecutable
if ($game.PSIsContainer -or $game.Extension -ne '.exe') {
    throw 'Select the RimWorld game executable (.exe).'
}

$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $game.FullName
$start.WorkingDirectory = $game.DirectoryName
$start.UseShellExecute = $false

# The inherited block is copied before editing. Only the launched game sees these values.
if ($Profile -ne 'Default') {
    foreach ($prefix in @('DOTNET_', 'COMPlus_')) {
        $start.EnvironmentVariables.Remove($prefix + 'GCHeapCount')
        $start.EnvironmentVariables[$prefix + 'gcServer'] = [string][int]($Profile -eq 'Server')
        $start.EnvironmentVariables[$prefix + 'gcConcurrent'] = '1'
        if ($ServerHeapCount -gt 0) {
            # Documented .NET GC numeric environment variables use hexadecimal.
            $start.EnvironmentVariables[$prefix + 'GCHeapCount'] = $ServerHeapCount.ToString('X')
        }
    }
}

if ($GpuTextureEncoding) {
    $start.EnvironmentVariables['WAYLITH_LOAD_ENCODE'] = 'gpu'
}

function Warm-StartupPacks {
    param([string] $ManifestPath)
    $manifestFile = Get-Item -LiteralPath $ManifestPath
    if ($manifestFile.PSIsContainer -or $manifestFile.Length -gt 1MB) {
        throw 'Invalid startup pack manifest.'
    }
    $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
    if ($manifest.Schema -ne 1 -or
        -not [string]::Equals([IO.Path]::GetFullPath($manifest.GameDirectory), $game.DirectoryName,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The startup pack manifest belongs to another game installation or format.'
    }

    $config = Get-Item -LiteralPath $manifest.ModsConfigPath
    if ($config.PSIsContainer -or $config.Length -gt 4MB -or
        $manifest.ModsConfigSha256 -ne (Get-FileHash -LiteralPath $config.FullName -Algorithm SHA256).Hash) {
        throw 'ModsConfig.xml changed since the startup pack manifest was recorded.'
    }

    $gpuMode = $start.EnvironmentVariables['WAYLITH_LOAD_ENCODE'] -in @('gpu', 'verify')
    $matchingEncoding = $gpuMode -eq [bool]$manifest.GpuEncode
    $cacheDirectory = [IO.Path]::GetFullPath($manifest.CacheDirectory)
    $buffer = New-Object byte[] (1MB)
    $remaining = [long]$WarmPackBudgetMiB * 1MB
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $read = 0L
    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete

    foreach ($entry in ($manifest.Packs | Select-Object -First 512)) {
        if ($remaining -le 0 -or $clock.Elapsed.TotalSeconds -ge $WarmTimeoutSeconds) { break }
        try {
            if (-not [IO.Path]::IsPathRooted($entry.Path)) { continue }
            $file = Get-Item -LiteralPath $entry.Path
            if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                [long]$entry.Bytes -ne $file.Length -or [string]$entry.LastWriteUtcTicks -ne [string]$file.LastWriteTimeUtc.Ticks) {
                continue
            }
            if ($entry.MountedAssetPack -ne $true -and $file.Name -ne 'Assets.zip' -and
                (-not $matchingEncoding -or
                 -not [string]::Equals($file.DirectoryName, $cacheDirectory, [StringComparison]::OrdinalIgnoreCase) -or
                 $file.Name -notmatch '^(pages\.|.+-)[0-9a-f]{16}\.zip$')) {
                continue
            }
            if ($entry.MountedAssetPack -eq $true -and $file.Extension -ne '.zip') { continue }
            $inputStream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
            try {
                while ($remaining -gt 0 -and $clock.Elapsed.TotalSeconds -lt $WarmTimeoutSeconds) {
                    $count = $inputStream.Read($buffer, 0, [int][Math]::Min([long]$buffer.Length, $remaining))
                    if ($count -eq 0) { break }
                    $read += $count
                    $remaining -= $count
                }
            } finally {
                $inputStream.Dispose()
            }
        } catch {
            continue
        }
    }
    Write-Host ('Startup pack warmup read {0:N1} MiB in {1:N2} s.' -f ($read / 1MB), $clock.Elapsed.TotalSeconds)
}

if ($StartupPackManifest) {
    try { Warm-StartupPacks -ManifestPath $StartupPackManifest }
    catch { Write-Warning ('Startup pack warmup skipped. ' + $_.Exception.Message) }
}

$process = [System.Diagnostics.Process]::Start($start)
Write-Host ('RimWorld PID {0} started with {1} GC requested.' -f $process.Id, $Profile)
$process.Dispose()
