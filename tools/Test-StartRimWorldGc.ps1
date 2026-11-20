# Checks tools/Start-RimWorldGc.ps1 with temporary files only. The launcher is always run with
# -NoLaunch, so no game is started. Run: pwsh -NoProfile -File tools/Test-StartRimWorldGc.ps1
$ErrorActionPreference = 'Stop'
$launcher = Join-Path $PSScriptRoot 'Start-RimWorldGc.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('mur-launcher-' + [Guid]::NewGuid().ToString('N'))
$failures = New-Object System.Collections.Generic.List[string]
$checks = 0

function Check {
    param([string] $Name, [bool] $Condition, [string] $Detail = '')
    $script:checks++
    if (-not $Condition) {
        $script:failures.Add($Name)
        Write-Host ('FAIL  {0}  {1}' -f $Name, $Detail)
    }
}

function New-Case {
    # A fresh folder with a stand-in game executable. Nothing in it is ever executed by the launcher.
    $folder = Join-Path $root ([Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($folder)
    $exe = Join-Path $folder 'RimWorldWin64.exe'
    [IO.File]::WriteAllBytes($exe, [byte[]]@(0))
    return @{ Folder = $folder; Exe = $exe }
}

function Invoke-Launcher {
    param([hashtable] $Case, [hashtable] $Arguments)
    $all = @{ GameExecutable = $Case.Exe; Profile = 'Default'; NoLaunch = $true }
    foreach ($key in $Arguments.Keys) { $all[$key] = $Arguments[$key] }
    return (& $launcher @all *>&1 | Out-String)
}

function New-Settings {
    param([hashtable] $Case, [string] $Name, [string] $Content, [switch] $Bom)
    $path = Join-Path $Case.Folder $Name
    [IO.File]::WriteAllText($path, $Content, (New-Object System.Text.UTF8Encoding ([bool]$Bom)))
    return $path
}

function Get-Sha {
    param([string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-Extras {
    # Backups and temporary files left beside a settings file.
    param([string] $Path)
    return @(Get-ChildItem -LiteralPath (Split-Path -Parent $Path) |
        Where-Object { $_.Name -like ((Split-Path -Leaf $Path) + '.*') })
}

function Check-Unchanged {
    param([string] $Name, [string] $Path, [string] $Before, [string] $Output, [string] $Expect)
    Check ($Name + ': file bytes unchanged') ((Get-Sha $Path) -eq $Before)
    Check ($Name + ': no backup or temporary file') ((Get-Extras $Path).Count -eq 0)
    Check ($Name + ': says why') ($Output -match $Expect) $Output
    Check ($Name + ': game not started') ($Output -match 'was not started')
}

$wowgagName = 'Mod_3797541313_GppMod17.xml'
$wowgag = @'
<?xml version="1.0" encoding="utf-8"?>
<SettingsBlock>
	<ModSettings Class="GUIPerformancePatch.GppSettings">
		<reusePatchResult>True</reusePatchResult>
		<parWorkers>4</parWorkers>
		<parPawnTick>True</parPawnTick>
	</ModSettings>
</SettingsBlock>
'@
$wowgagDefault = $wowgag -replace '\s*<parWorkers>4</parWorkers>', ''
$overclockName = 'Mod_3828406061_OverclockMod.xml'
$overclock = @'
<?xml version="1.0" encoding="utf-8"?>
<SettingsBlock>
	<ModSettings Class="Overclock.OverclockSettings">
		<parallelThreads>3</parallelThreads>
		<other>keep</other>
	</ModSettings>
</SettingsBlock>
'@

try {
    [void][IO.Directory]::CreateDirectory($root)

    # --- External worker settings: nothing is rewritten unless owner, file and count are all given.
    $case = New-Case
    $path = New-Settings $case $wowgagName $wowgag
    $before = Get-Sha $path
    Check-Unchanged 'normal launch' $path $before (Invoke-Launcher $case @{}) 'Preparation finished'
    Check-Unchanged 'owner and count without file' $path $before `
        (Invoke-Launcher $case @{ ExternalWorkerOwner = 'Wowgag'; ExternalWorkerCount = 8 }) 'left unchanged'
    Check-Unchanged 'file and count without owner' $path $before `
        (Invoke-Launcher $case @{ ExternalSettingsPath = $path; ExternalWorkerCount = 8 }) 'left unchanged'
    Check-Unchanged 'owner and file without count' $path $before `
        (Invoke-Launcher $case @{ ExternalWorkerOwner = 'Wowgag'; ExternalSettingsPath = $path }) 'left unchanged'

    # --- Refusals.
    $wowgagArgs = { param($count, $file) @{ ExternalWorkerOwner = 'Wowgag'; ExternalWorkerCount = $count; ExternalSettingsPath = $file } }
    $overclockArgs = { param($count, $file) @{ ExternalWorkerOwner = 'Overclock'; ExternalWorkerCount = $count; ExternalSettingsPath = $file } }
    Check-Unchanged 'WOWGAG above 32' $path $before (Invoke-Launcher $case (& $wowgagArgs 33 $path)) 'accepts 0 \(automatic\) to 32'
    Check-Unchanged 'negative count' $path $before (Invoke-Launcher $case (& $wowgagArgs -1 $path)) 'accepts 0 \(automatic\) to 32'
    Check-Unchanged 'same value' $path $before (Invoke-Launcher $case (& $wowgagArgs 4 $path)) 'already 4'
    Check-Unchanged 'Overclock owner on a WOWGAG file' $path $before (Invoke-Launcher $case (& $overclockArgs 2 $path)) 'not the settings file'
    Check-Unchanged 'missing file' $path $before `
        (Invoke-Launcher $case (& $wowgagArgs 8 (Join-Path $case.Folder 'Mod_none_GppMod17.xml'))) 'left unchanged'

    $other = New-Settings $case 'Mod_3797541313_OtherMod.xml' $wowgag
    Check-Unchanged 'WOWGAG owner on another file name' $other (Get-Sha $other) (Invoke-Launcher $case (& $wowgagArgs 8 $other)) 'not the settings file'

    $refused = [ordered]@{
        'broken XML'        = @('<SettingsBlock><ModSettings', 'not valid XML')
        'unknown root'      = @(($wowgag -replace 'SettingsBlock', 'Settings'), 'does not hold the settings class')
        'unknown class'     = @(($wowgag -replace 'GUIPerformancePatch\.GppSettings', 'Other.GppSettings'), 'does not hold the settings class')
        'two settings'      = @(($wowgag -replace '</SettingsBlock>', '<ModSettings Class="GUIPerformancePatch.GppSettings" /></SettingsBlock>'), 'does not hold the settings class')
        'duplicate value'   = @(($wowgag -replace '<parPawnTick>True</parPawnTick>', '<parWorkers>2</parWorkers>'), 'more than once')
        'not a number'      = @(($wowgag -replace '>4<', '>many<'), 'not a whole number')
        'nested value'      = @(($wowgag -replace '>4<', '><li>4</li><'), 'not a whole number')
    }
    foreach ($name in $refused.Keys) {
        $case = New-Case
        $bad = New-Settings $case $wowgagName $refused[$name][0]
        Check-Unchanged $name $bad (Get-Sha $bad) (Invoke-Launcher $case (& $wowgagArgs 8 $bad)) $refused[$name][1]
    }

    # --- A requested change: one value, everything else kept, backup of the original, no temporary file.
    foreach ($count in 0, 8, 32) {
        $case = New-Case
        $path = New-Settings $case $wowgagName $wowgag -Bom:($count -eq 8)
        $original = [IO.File]::ReadAllBytes($path)
        $output = Invoke-Launcher $case (& $wowgagArgs $count $path)
        $text = [IO.File]::ReadAllText($path)
        $expected = $wowgag -replace '<parWorkers>4</parWorkers>', ('<parWorkers>{0}</parWorkers>' -f $count)
        Check "WOWGAG to ${count}: only the value differs" ($text -eq $expected) $text
        Check "WOWGAG to ${count}: reported" ($output -match "parWorkers changed from 4 to $count") $output
        Check "WOWGAG to ${count}: game not started" ($output -match 'was not started')
        $extras = Get-Extras $path
        Check "WOWGAG to ${count}: exactly one backup" ($extras.Count -eq 1 -and $extras[0].Name -match '\.mur-backup-\d{8}-\d{6}$')
        if ($extras.Count -eq 1) {
            Check "WOWGAG to ${count}: backup holds the original bytes" `
                ([Convert]::ToBase64String([IO.File]::ReadAllBytes($extras[0].FullName)) -eq [Convert]::ToBase64String($original))
        }
        $bytes = [IO.File]::ReadAllBytes($path)
        $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        Check "WOWGAG to ${count}: byte order mark kept as it was" ($hasBom -eq ($count -eq 8))
    }

    # --- WOWGAG does not store the default 0. Asking for 0 is then no change; another value adds the element.
    $case = New-Case
    $path = New-Settings $case $wowgagName $wowgagDefault
    Check-Unchanged 'WOWGAG default asked for 0' $path (Get-Sha $path) (Invoke-Launcher $case (& $wowgagArgs 0 $path)) 'already means 0'
    $output = Invoke-Launcher $case (& $wowgagArgs 6 $path)
    $document = New-Object System.Xml.XmlDocument
    $document.Load($path)
    Check 'WOWGAG default to 6: element added' ($document.SelectNodes('/SettingsBlock/ModSettings/parWorkers').Count -eq 1 -and
        $document.SelectSingleNode('/SettingsBlock/ModSettings/parWorkers').InnerText -eq '6') $output
    Check 'WOWGAG default to 6: other values kept' ($document.SelectSingleNode('/SettingsBlock/ModSettings/reusePatchResult').InnerText -eq 'True' -and
        $document.SelectSingleNode('/SettingsBlock/ModSettings/parPawnTick').InnerText -eq 'True')
    Check 'WOWGAG default to 6: one backup' ((Get-Extras $path).Count -eq 1)

    # --- Overclock: only a value already stored in the file is changed; its range follows the processor count.
    $overclockMax = [Math]::Max(1, [Environment]::ProcessorCount - 1)
    $case = New-Case
    $path = New-Settings $case $overclockName $overclock
    $before = Get-Sha $path
    Check-Unchanged 'Overclock above its range' $path $before (Invoke-Launcher $case (& $overclockArgs ($overclockMax + 1) $path)) 'accepts 0 \(automatic\) to'
    Check-Unchanged 'WOWGAG owner on an Overclock file' $path $before (Invoke-Launcher $case (& $wowgagArgs 2 $path)) 'not the settings file'
    $output = Invoke-Launcher $case (& $overclockArgs 0 $path)
    Check 'Overclock to 0: only the value differs' ([IO.File]::ReadAllText($path) -eq ($overclock -replace '>3<', '>0<')) $output
    Check 'Overclock to 0: one backup' ((Get-Extras $path).Count -eq 1)

    # Overclock does not store the default 0 either.
    $case = New-Case
    $path = New-Settings $case $overclockName ($overclock -replace '\s*<parallelThreads>3</parallelThreads>', '')
    Check-Unchanged 'Overclock default asked for 0' $path (Get-Sha $path) (Invoke-Launcher $case (& $overclockArgs 0 $path)) 'already means 0'
    $output = Invoke-Launcher $case (& $overclockArgs 1 $path)
    $document = New-Object System.Xml.XmlDocument
    $document.Load($path)
    Check 'Overclock default to 1: element added' ($document.SelectNodes('/SettingsBlock/ModSettings/parallelThreads').Count -eq 1 -and
        $document.SelectSingleNode('/SettingsBlock/ModSettings/parallelThreads').InnerText -eq '1') $output
    Check 'Overclock default to 1: other values kept' ($document.SelectSingleNode('/SettingsBlock/ModSettings/other').InnerText -eq 'keep')
    Check 'Overclock default to 1: one backup' ((Get-Extras $path).Count -eq 1)

    $case = New-Case
    $path = New-Settings $case 'Mod_3828406061_OtherMod.xml' $overclock
    Check-Unchanged 'Overclock owner on another file name' $path (Get-Sha $path) (Invoke-Launcher $case (& $overclockArgs 1 $path)) 'not the settings file'
    $path = New-Settings $case $overclockName ($overclock -replace 'Overclock\.OverclockSettings', 'Other.OverclockSettings')
    Check-Unchanged 'Overclock file with another settings class' $path (Get-Sha $path) (Invoke-Launcher $case (& $overclockArgs 1 $path)) 'does not hold the settings class'

    # --- A running game would overwrite the file, so nothing is written. Needs a process whose
    # executable path is the game's path. Some systems ship a multi-call sleep that refuses to run
    # under another name, so a small compiled stand-in is tried next; without either the check is skipped.
    if (-not $IsWindows) {
        $case = New-Case
        $path = New-Settings $case $wowgagName $wowgag
        $standIn = $null
        $builders = @(
            { if (Test-Path -LiteralPath '/bin/sleep') { Copy-Item -LiteralPath '/bin/sleep' -Destination $case.Exe -Force; $true } else { $false } },
            {
                if (-not (Get-Command cc -ErrorAction SilentlyContinue)) { return $false }
                $source = Join-Path $case.Folder 'standin.c'
                [IO.File]::WriteAllText($source, "#include <unistd.h>`nint main(void) { sleep(30); return 0; }`n")
                & cc -o $case.Exe $source 2>$null
                return ($LASTEXITCODE -eq 0)
            })
        foreach ($build in $builders) {
            if (-not (& $build)) { continue }
            & chmod +x $case.Exe
            $candidate = Start-Process -FilePath $case.Exe -ArgumentList '30' -PassThru -RedirectStandardError (Join-Path $case.Folder 'standin.err')
            Start-Sleep -Milliseconds 500
            if (-not $candidate.HasExited) { $standIn = $candidate; break }
        }
        if ($null -eq $standIn) {
            Write-Host 'SKIP  game running: no stand-in process could be started under the game path.'
        } else {
            try {
                Check-Unchanged 'game running' $path (Get-Sha $path) (Invoke-Launcher $case (& $wowgagArgs 8 $path)) 'RimWorld is running'
            } finally {
                Stop-Process -Id $standIn.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }

    # --- Selected save: read up to its own limit, and skipped instead of failing the launch.
    $case = New-Case
    $save = Join-Path $case.Folder 'Colony.rws'
    [IO.File]::WriteAllBytes($save, (New-Object byte[] (3MB)))
    $output = Invoke-Launcher $case @{ WarmSaveFile = $save; WarmMinFreeMiB = 0 }
    Check 'save: read whole' ($output -match 'Save warmup read 3[.,]0 of 3[.,]0 MiB') $output
    $output = Invoke-Launcher $case @{ WarmSaveFile = $save; WarmMinFreeMiB = 0; WarmSaveBudgetMiB = 1 }
    Check 'save: stops at its own limit' ($output -match 'Save warmup read 1[.,]0 of 3[.,]0 MiB') $output
    $output = Invoke-Launcher $case @{ WarmSaveFile = (Join-Path $case.Folder 'Missing.rws'); WarmMinFreeMiB = 0 }
    Check 'save: missing file is skipped' ($output -match 'Save warmup skipped' -and $output -match 'was not started') $output
    $notSave = Join-Path $case.Folder 'Colony.txt'
    [IO.File]::WriteAllBytes($notSave, (New-Object byte[] 16))
    $output = Invoke-Launcher $case @{ WarmSaveFile = $notSave; WarmMinFreeMiB = 0 }
    Check 'save: another file type is skipped' ($output -match 'Save warmup skipped' -and $output -notmatch 'Save warmup read') $output
    $output = Invoke-Launcher $case @{ WarmSaveFile = $case.Folder; WarmMinFreeMiB = 0 }
    Check 'save: a folder is skipped' ($output -match 'Save warmup skipped' -and $output -notmatch 'Save warmup read') $output
    $output = Invoke-Launcher $case @{ WarmSaveFile = $save; WarmMinFreeMiB = 262144 }
    Check 'save: skipped when free memory is short or unknown' ($output -match 'Save warmup skipped' -and $output -notmatch 'Save warmup read') $output

    # --- Shader cache records named by the manifest: only real records of the recorded size in the recorded folder.
    $case = New-Case
    $shaderFolder = Join-Path $case.Folder 'ShaderCache'
    [void][IO.Directory]::CreateDirectory($shaderFolder)
    $good = Join-Path $shaderFolder (('a' * 64) + '.spv-cache')
    $resized = Join-Path $shaderFolder (('b' * 64) + '.spv-cache')
    $misnamed = Join-Path $shaderFolder 'notes.spv-cache'
    $outside = Join-Path $case.Folder (('c' * 64) + '.spv-cache')
    foreach ($file in $good, $resized, $misnamed, $outside) { [IO.File]::WriteAllBytes($file, (New-Object byte[] 2048)) }
    $config = Join-Path $case.Folder 'ModsConfig.xml'
    [IO.File]::WriteAllText($config, '<ModsConfigData />')
    $manifest = [ordered]@{
        Schema = 1; GameDirectory = $case.Folder; ModsConfigPath = $config; ModsConfigSha256 = (Get-Sha $config)
        CacheDirectory = $case.Folder; GpuEncode = $false; Packs = @()
        ShaderCacheDirectory = $shaderFolder
        ShaderCache = @(
            @{ Path = $good; Bytes = 2048 }, @{ Path = $resized; Bytes = 4096 }, @{ Path = $misnamed; Bytes = 2048 },
            @{ Path = $outside; Bytes = 2048 }, @{ Path = (Join-Path $shaderFolder (('d' * 64) + '.spv-cache')); Bytes = 2048 },
            @{ Path = 'relative.spv-cache'; Bytes = 2048 })
    }
    $manifestPath = Join-Path $case.Folder 'MoreUseResources-StartupPacks.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5))
    $output = Invoke-Launcher $case @{ StartupPackManifest = $manifestPath }
    Check 'shader cache: one valid record read' ($output -match 'Shader cache warmup read 1 records') $output

    $manifest.ShaderCacheDirectory = $case.Folder
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5))
    $output = Invoke-Launcher $case @{ StartupPackManifest = $manifestPath }
    Check 'shader cache: another folder name is not read' ($output -notmatch 'Shader cache warmup read') $output

    [IO.File]::WriteAllText($config, '<ModsConfigData><changed /></ModsConfigData>')
    $output = Invoke-Launcher $case @{ StartupPackManifest = $manifestPath }
    Check 'manifest: changed mod list is skipped' ($output -match 'Startup pack warmup skipped' -and $output -match 'was not started') $output
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host ('{0} of {1} launcher checks failed.' -f $failures.Count, $checks)
    exit 1
}
Write-Host ('All {0} launcher checks passed.' -f $checks)
