param(
    [Parameter(Mandatory = $true)]
    [string] $GameExecutable,

    [ValidateSet('Workstation', 'Server')]
    [string] $Profile = 'Workstation',

    [ValidateRange(0, 64)]
    [int] $ServerHeapCount = 0
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
foreach ($prefix in @('DOTNET_', 'COMPlus_')) {
    $start.EnvironmentVariables.Remove($prefix + 'GCHeapCount')
    $start.EnvironmentVariables[$prefix + 'gcServer'] = [string][int]($Profile -eq 'Server')
    $start.EnvironmentVariables[$prefix + 'gcConcurrent'] = '1'
    if ($ServerHeapCount -gt 0) {
        # Documented .NET GC numeric environment variables use hexadecimal.
        $start.EnvironmentVariables[$prefix + 'GCHeapCount'] = $ServerHeapCount.ToString('X')
    }
}

$process = [System.Diagnostics.Process]::Start($start)
Write-Host ('RimWorld PID {0} started with {1} GC requested.' -f $process.Id, $Profile)
$process.Dispose()
