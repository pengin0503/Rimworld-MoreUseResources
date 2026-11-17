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
    [int] $WarmTimeoutSeconds = 10,

    # One save (.rws) to read into the OS file cache before the game starts. Load it from the game's own menu.
    [string] $WarmSaveFile,

    [ValidateRange(1, 4096)]
    [int] $WarmSaveBudgetMiB = 256,

    [ValidateRange(1, 60)]
    [int] $WarmSaveTimeoutSeconds = 5,

    # The save is only read while at least this much physical RAM is free. 0 turns the check off.
    [ValidateRange(0, 262144)]
    [int] $WarmMinFreeMiB = 6144,

    # All three must be given to change another mod's existing worker setting. Nothing is changed otherwise.
    [ValidateSet('Wowgag', 'Overclock')]
    [string] $ExternalWorkerOwner,

    [int] $ExternalWorkerCount = -1,

    [string] $ExternalSettingsPath,

    # Do everything except starting the game.
    [switch] $NoLaunch
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

# One byte and time budget covers the selected save, the packs and the shader records together.
$warmBuffer = New-Object byte[] (1MB)
$warmRemaining = [long]$WarmPackBudgetMiB * 1MB
$warmClock = [Diagnostics.Stopwatch]::StartNew()
$warmShare = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete

function Read-ForWarmup {
    # Reads a file and discards the bytes so that the operating system keeps them in its file cache.
    param([string] $Path, [long] $ByteLimit, [double] $DeadlineSeconds)
    $read = 0L
    $inputStream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $script:warmShare)
    try {
        while ($read -lt $ByteLimit -and $script:warmRemaining -gt 0 -and
               $script:warmClock.Elapsed.TotalSeconds -lt $DeadlineSeconds) {
            $want = [int][Math]::Min([long]$script:warmBuffer.Length, [Math]::Min($ByteLimit - $read, $script:warmRemaining))
            $count = $inputStream.Read($script:warmBuffer, 0, $want)
            if ($count -eq 0) { break }
            $read += $count
            $script:warmRemaining -= $count
        }
    } finally {
        $inputStream.Dispose()
    }
    return $read
}

function Get-FreePhysicalMiB {
    # Returns $null when the operating system does not report it.
    try {
        if (Get-Command Get-CimInstance -ErrorAction SilentlyContinue) {
            return [long]((Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).FreePhysicalMemory / 1024)
        }
    } catch { }
    return $null
}

function Warm-SaveFile {
    param([string] $SavePath)
    $file = Get-Item -LiteralPath $SavePath
    if ($file.PSIsContainer -or $file.Extension -ne '.rws' -or
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Select one RimWorld save file (.rws).'
    }
    if ($WarmMinFreeMiB -gt 0) {
        $free = Get-FreePhysicalMiB
        if ($null -eq $free) { throw 'Free physical memory could not be read.' }
        if ($free -lt $WarmMinFreeMiB) { throw ('Only {0:N0} MiB of physical memory is free.' -f $free) }
    }
    $started = $script:warmClock.Elapsed.TotalSeconds
    $deadline = [Math]::Min($started + $WarmSaveTimeoutSeconds, [double]$WarmTimeoutSeconds)
    $read = Read-ForWarmup -Path $file.FullName -ByteLimit ([long]$WarmSaveBudgetMiB * 1MB) -DeadlineSeconds $deadline
    Write-Host ('Save warmup read {0:N1} of {1:N1} MiB in {2:N2} s.' -f
        ($read / 1MB), ($file.Length / 1MB), ($script:warmClock.Elapsed.TotalSeconds - $started))
}

function Set-ExternalWorkerCount {
    # Changes one verified worker setting of another mod. Every other value in the file is kept.
    param([string] $Owner, [int] $Count, [string] $SettingsPath)
    $known = @{
        # WOWGAG's Performance Patch, 1.7 assembly: GppSettings.ExposeData writes "parWorkers" (default 0,
        # omitted from the file while 0). The settings window offers 0..32 and the worker pool is rebuilt
        # whenever the value differs, so the edit applies at the next launch.
        Wowgag    = @{ Field = 'parWorkers'; Class = '^GUIPerformancePatch\.GppSettings$'; File = '^Mod_.+_GppMod17\.xml$'
                       Max = 32; AddWhenMissing = $true; Screen = "WOWGAG's Performance Patch" }
        # Overclock: the stored name is checked against the file itself, because a value left at its
        # default is not written by the game. Its settings window offers 0..(logical processors - 1).
        Overclock = @{ Field = 'parallelThreads'; Class = '(^|\.)OverclockSettings$'; File = '^Mod_.+\.xml$'
                       Max = [Math]::Max(1, [Environment]::ProcessorCount - 1); AddWhenMissing = $false; Screen = 'Overclock' }
    }[$Owner]
    $guide = 'Change the worker count in the mod settings window of {0} instead.' -f $known.Screen

    if ($Count -lt 0 -or $Count -gt $known.Max) {
        throw ('{0} accepts 0 (automatic) to {1} for {2}. {3}' -f $Owner, $known.Max, $known.Field, $guide)
    }
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [string]::Equals($_.Path, $game.FullName, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
    if ($running.Count -gt 0) { throw 'RimWorld is running; it would overwrite the file when its settings are saved.' }

    $file = Get-Item -LiteralPath $SettingsPath
    if ($file.PSIsContainer -or $file.Length -gt 4MB -or $file.Name -notmatch $known.File -or
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw ('This is not the settings file of {0}. {1}' -f $Owner, $guide)
    }
    $stamp = $file.LastWriteTimeUtc.Ticks
    $length = $file.Length
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $document = New-Object System.Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.XmlResolver = $null
    $stream = New-Object IO.MemoryStream (, $bytes)
    try { $document.Load($stream) } catch { throw ('The settings file is not valid XML. {0}' -f $guide) } finally { $stream.Dispose() }

    $root = $document.DocumentElement
    $blocks = @($root.ChildNodes | Where-Object { $_.NodeType -eq 'Element' })
    if ($root.Name -ne 'SettingsBlock' -or $blocks.Count -ne 1 -or $blocks[0].Name -ne 'ModSettings' -or
        $blocks[0].GetAttribute('Class') -notmatch $known.Class) {
        throw ('The file does not hold the settings class this script knows for {0}. {1}' -f $Owner, $guide)
    }
    $settings = $blocks[0]
    $fields = @($settings.ChildNodes | Where-Object { $_.NodeType -eq 'Element' -and $_.Name -eq $known.Field })
    if ($fields.Count -gt 1) { throw ('The file holds {0} more than once. {1}' -f $known.Field, $guide) }
    $current = 0
    if ($fields.Count -eq 1) {
        if (@($fields[0].ChildNodes | Where-Object { $_.NodeType -eq 'Element' }).Count -gt 0 -or
            -not [int]::TryParse($fields[0].InnerText.Trim(), [Globalization.NumberStyles]::Integer,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$current)) {
            throw ('{0} is not a whole number in this file. {1}' -f $known.Field, $guide)
        }
        if ($current -eq $Count) {
            Write-Host ('{0} {1} is already {2}; the file was not changed.' -f $Owner, $known.Field, $Count)
            return
        }
        $fields[0].InnerText = $Count.ToString([Globalization.CultureInfo]::InvariantCulture)
    } elseif (-not $known.AddWhenMissing) {
        throw ('{0} is not stored in this file, so its format cannot be confirmed. {1}' -f $known.Field, $guide)
    } elseif ($Count -eq 0) {
        Write-Host ('{0} {1} is not stored, which already means 0 (automatic); the file was not changed.' -f $Owner, $known.Field)
        return
    } else {
        $node = $document.CreateElement($known.Field)
        $node.InnerText = $Count.ToString([Globalization.CultureInfo]::InvariantCulture)
        [void]$settings.AppendChild($node)
    }

    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $temporary = Join-Path $file.DirectoryName ($file.Name + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $backup = $file.FullName + '.mur-backup-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    try {
        $writerSettings = New-Object System.Xml.XmlWriterSettings
        $writerSettings.Encoding = New-Object System.Text.UTF8Encoding $hasBom
        $writer = [System.Xml.XmlWriter]::Create($temporary, $writerSettings)
        try { $document.Save($writer) } finally { $writer.Dispose() }

        $file.Refresh()
        if (-not $file.Exists -or $file.LastWriteTimeUtc.Ticks -ne $stamp -or $file.Length -ne $length) {
            throw 'The settings file changed while it was being prepared; it was left as it is.'
        }
        [IO.File]::Copy($file.FullName, $backup, $false)
        # Same folder, so the replacement stays on one volume. File.Replace also exists in Windows PowerShell 5.1.
        [IO.File]::Replace($temporary, $file.FullName, [NullString]::Value)
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
    Write-Host ('{0} {1} changed from {2} to {3} in {4}.' -f $Owner, $known.Field, $current, $Count, $file.FullName)
    Write-Host ('This is a permanent change to that mod''s own settings and applies from this launch. Backup: {0}' -f $backup)
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
    $started = $script:warmClock.Elapsed.TotalSeconds
    $read = 0L

    foreach ($entry in ($manifest.Packs | Select-Object -First 512)) {
        if ($script:warmRemaining -le 0 -or $script:warmClock.Elapsed.TotalSeconds -ge $WarmTimeoutSeconds) { break }
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
            $read += Read-ForWarmup -Path $file.FullName -ByteLimit ([long]::MaxValue) -DeadlineSeconds $WarmTimeoutSeconds
        } catch {
            continue
        }
    }
    Write-Host ('Startup pack warmup read {0:N1} MiB in {1:N2} s.' -f
        ($read / 1MB), ($script:warmClock.Elapsed.TotalSeconds - $started))

    # Compiled shader records: <cache>\ShaderCache\<64 hex>.spv-cache, read whole by the engine.
    if (-not $manifest.ShaderCacheDirectory -or -not $manifest.ShaderCache) { return }
    $shaderDirectory = [IO.Path]::GetFullPath($manifest.ShaderCacheDirectory)
    if ((Split-Path -Leaf $shaderDirectory) -ne 'ShaderCache') { return }
    $shaderRead = 0L
    $shaderFiles = 0
    foreach ($entry in ($manifest.ShaderCache | Select-Object -First 512)) {
        if ($script:warmRemaining -le 0 -or $script:warmClock.Elapsed.TotalSeconds -ge $WarmTimeoutSeconds) { break }
        try {
            if (-not [IO.Path]::IsPathRooted($entry.Path)) { continue }
            $file = Get-Item -LiteralPath $entry.Path
            # The engine renews the timestamp of a record it used, so only name, folder and size are compared.
            if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                [long]$entry.Bytes -ne $file.Length -or $file.Name -cnotmatch '^[0-9a-f]{64}\.spv-cache$' -or
                -not [string]::Equals($file.DirectoryName, $shaderDirectory, [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            $shaderRead += Read-ForWarmup -Path $file.FullName -ByteLimit ([long]::MaxValue) -DeadlineSeconds $WarmTimeoutSeconds
            $shaderFiles++
        } catch {
            continue
        }
    }
    Write-Host ('Shader cache warmup read {0} records, {1:N1} MiB.' -f $shaderFiles, ($shaderRead / 1MB))
}

$externalGiven = @($PSBoundParameters.ContainsKey('ExternalWorkerOwner'),
    $PSBoundParameters.ContainsKey('ExternalWorkerCount'), $PSBoundParameters.ContainsKey('ExternalSettingsPath'))
if ($externalGiven -notcontains $false) {
    try { Set-ExternalWorkerCount -Owner $ExternalWorkerOwner -Count $ExternalWorkerCount -SettingsPath $ExternalSettingsPath }
    catch { Write-Warning ('External worker setting left unchanged. ' + $_.Exception.Message) }
} elseif ($externalGiven -contains $true) {
    Write-Warning ('External worker setting left unchanged. Give -ExternalWorkerOwner, -ExternalWorkerCount and ' +
        '-ExternalSettingsPath together, or change the count in that mod''s settings window.')
}

# The selected save goes first inside the shared budget; packs and shader records use what is left.
if ($WarmSaveFile) {
    try { Warm-SaveFile -SavePath $WarmSaveFile }
    catch { Write-Warning ('Save warmup skipped. ' + $_.Exception.Message) }
}

if ($StartupPackManifest) {
    try { Warm-StartupPacks -ManifestPath $StartupPackManifest }
    catch { Write-Warning ('Startup pack warmup skipped. ' + $_.Exception.Message) }
}

if ($NoLaunch) {
    Write-Host 'Preparation finished. The game was not started (-NoLaunch).'
    return
}

$process = [System.Diagnostics.Process]::Start($start)
Write-Host ('RimWorld PID {0} started with {1} GC requested.' -f $process.Id, $Profile)
$process.Dispose()
