# Run the video acceptance harness inside the real game.
#
# Installs the diagnostics build, parks every other harness trigger so two auto-open
# patches cannot fight over the scene, waits for the DONE marker, and restores whatever
# was there before. The result file is deleted first: reading a previous run's output as
# this run's evidence is the one failure mode that silently "passes" everything.
param(
    [string]$Chart = "$PSScriptRoot\..\_probe\chart\level.adofai",
    [string]$Media = "$PSScriptRoot\..\_probe\media",
    [int]$TimeoutSeconds = 240,
    # "editor" drives the editor acceptance suite; "play" loads the chart through scnGame and
    # checks the video renders in real gameplay. They are separate runs because each takes
    # over the scene at startup.
    [ValidateSet('editor', 'play')][string]$Mode = 'editor'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$gameDir = 'D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice'
if ($env:ADOFAI_GAME_DIR) { $gameDir = $env:ADOFAI_GAME_DIR }
$gameExe = Join-Path $gameDir 'A Dance of Fire and Ice.exe'
$modDir = Join-Path $gameDir 'Mods\PEV.Core'
$dllSrc = Join-Path $root 'src\PEV.Core\bin\Release-diagnostics\PEV.Core.dll'

if (-not (Test-Path -LiteralPath $dllSrc)) { throw "Diagnostics build missing: $dllSrc" }
if (Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $gameExe }) {
    throw 'Game is already running'
}

if ($Mode -eq 'play') { $resultName = 'video-play-results.txt'; $triggerName = 'video-play-test.txt' } else { $resultName = 'video-results.txt'; $triggerName = 'video-test.txt' }
$resultPath = Join-Path $modDir $resultName
$triggerPath = Join-Path $modDir $triggerName

$parked = @{}
Get-ChildItem -LiteralPath $modDir -Filter '*-test.txt' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne $triggerName } |
    ForEach-Object { $parked[$_.FullName] = [IO.File]::ReadAllBytes($_.FullName); Remove-Item -LiteralPath $_.FullName -Force }

$savedTrigger = Test-Path -LiteralPath $triggerPath
$process = $null
try {
    # A stale result file would be read as this run's evidence the moment the loop starts.
    if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath -Force }
    [IO.File]::WriteAllText($triggerPath, '')
    Copy-Item -LiteralPath $dllSrc -Destination (Join-Path $modDir 'PEV.Core.dll') -Force

    # UnityModManager caches assemblies beside the DLL; a stale copy can shadow a new build.
    Get-ChildItem -LiteralPath $modDir -Filter 'PEV.Core.dll.*.cache' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force

    $env:PEV_VIDEO_TEST_CHART = (Resolve-Path -LiteralPath $Chart).Path
    $env:PEV_VIDEO_TEST_MEDIA = (Resolve-Path -LiteralPath $Media).Path

    $process = Start-Process -FilePath $gameExe -WorkingDirectory $gameDir -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $output = ''
    do {
        Start-Sleep -Milliseconds 400
        $output = if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath -Raw } else { '' }
        if ($output -match '(?m)^DONE') { break }
        if ($process.HasExited) { throw "Game exited before the harness finished`n$output" }
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($output -notmatch '(?m)^DONE') { throw "Harness did not finish in ${TimeoutSeconds}s`n$output" }
    Write-Output $output
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    if (-not $savedTrigger -and (Test-Path -LiteralPath $triggerPath)) { Remove-Item -LiteralPath $triggerPath -Force }
    foreach ($path in $parked.Keys) { [IO.File]::WriteAllBytes($path, $parked[$path]) }
    Remove-Item Env:PEV_VIDEO_TEST_CHART -ErrorAction SilentlyContinue
    Remove-Item Env:PEV_VIDEO_TEST_MEDIA -ErrorAction SilentlyContinue
}
