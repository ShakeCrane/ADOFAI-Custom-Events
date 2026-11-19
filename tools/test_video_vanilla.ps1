# Verify the game still starts with the mod removed, and that a video chart uses only
# vanilla-legal constructs.
#
# Two separate facts are checked, because they answer two different questions:
#
#   1. The mod is not required for the game to run: with `Mods/PEV.Core` moved out of the
#      load path, the game reaches its menu with no PEV.Core line in the log.
#   2. The chart's video row is ordinary vanilla data: a real AddDecoration row whose every
#      key is a native property, with the event body inside `maskingTarget`. That is what
#      makes "the chart still opens without the mod" true rather than hoped for.
#
# Opening that chart in the vanilla *editor* is not automated here: the game exposes no
# way to open a level from the command line, and driving its file dialog is outside what
# this harness can do reliably. The row shape is the part that decides it.
param(
    [string]$Chart = "$PSScriptRoot\..\_probe\chart\level.adofai",
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$gameDir = 'D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice'
if ($env:ADOFAI_GAME_DIR) { $gameDir = $env:ADOFAI_GAME_DIR }
$gameExe = Join-Path $gameDir 'A Dance of Fire and Ice.exe'
$modDir = Join-Path $gameDir 'Mods\PEV.Core'
$parkedMod = Join-Path $gameDir 'Mods-off\PEV.Core.vanilla-check'
$log = Join-Path $env:USERPROFILE 'AppData\LocalLow\7th Beat Games\A Dance of Fire and Ice\Player.log'

# ---- data shape ----------------------------------------------------------------
# The native AddDecoration property names this game build defines. A row carrying a key
# outside this set would be data vanilla cannot decode. Checked in Python because Windows
# PowerShell 5.1's ConvertFrom-Json has no hashtable mode and silently gives PSCustomObject.
$shape = @'
import json, sys
path = sys.argv[1]
native = {
    'floor','eventType','decorationImage','maskingType','maskingTarget','maskingFrontDepth',
    'maskingBackDepth','position','relativeTo','pivotOffset','cropPivotOffset','scale',
    'scaleMultiplier','rotation','lockRotation','lockScale','color','opacity','depth',
    'parallax','parallaxOffset','tag','imageSmoothing','blendMode','stickToFloor',
    'originalSize','hitbox','hitboxType','hitboxTargetPlanet','hitboxDecoTag','hitboxEventTag',
    'hitboxDetectTarget','hitboxTriggerType','hitboxRepeatInterval','hitboxFailHitbox','components',
}
doc = json.load(open(path, encoding='utf-8'))
rows = doc.get('decorations') or []
if not rows:
    print('CHART-SHAPE fail: chart has no decoration row'); sys.exit(1)
problems = []
for row in rows:
    if row.get('eventType') != 'AddDecoration':
        problems.append('row eventType=%r is not AddDecoration' % row.get('eventType'))
        continue
    for key in row:
        if key not in native:
            problems.append("row carries non-native key %r" % key)
    payload = row.get('maskingTarget') or ''
    if not payload.startswith('pev1:'):
        problems.append('row carries no pev1 payload')
    if row.get('decorationImage') != 'pev/field':
        problems.append('unexpected marker %r' % row.get('decorationImage'))
if problems:
    print('CHART-SHAPE fail: ' + '; '.join(problems)); sys.exit(1)
print('CHART-SHAPE ok (%d row(s), native keys only, pev1 payload present)' % len(rows))
'@

$shapeFile = Join-Path ([IO.Path]::GetTempPath()) 'pev-video-shape-check.py'
[IO.File]::WriteAllText($shapeFile, $shape, [Text.UTF8Encoding]::new($false))
try {
    & python $shapeFile $Chart
    if ($LASTEXITCODE -ne 0) { throw 'CHART-SHAPE failed; the chart is not vanilla-decodable' }
} finally { Remove-Item -LiteralPath $shapeFile -Force -ErrorAction SilentlyContinue }

# ---- vanilla boot --------------------------------------------------------------
# Steam allows one instance of the game at a time, and another checkout may be running its
# own copy. Launching into that contention produces a spurious exit code, so it is reported
# as a skip rather than as a failure of the mod.
$running = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like '*A Dance of Fire and Ice.exe' })
if ($running.Count -gt 0) {
    $paths = ($running | ForEach-Object { $_.ExecutablePath }) -join '; '
    Write-Output "VANILLA-BOOT skip (another instance is running: $paths)"
    exit 0
}

$moved = $false
$process = $null
try {
    if (Test-Path -LiteralPath $modDir) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $parkedMod) | Out-Null
        Move-Item -LiteralPath $modDir -Destination $parkedMod -Force
        $moved = $true
    }

    # The game truncates Player.log on each launch, so a byte offset from before the launch
    # is meaningless — the new file is shorter than the old one. The launch time is the
    # signal instead: the menu line only counts if the log was written after it.
    $launch = Get-Date
    $process = Start-Process -FilePath $gameExe -WorkingDirectory $gameDir -WindowStyle Hidden -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $reachedMenu = $false
    do {
        Start-Sleep -Milliseconds 500
        if ($process.HasExited) { break }
        if (Test-Path -LiteralPath $log) {
            $info = Get-Item -LiteralPath $log
            if ($info.LastWriteTime -ge $launch) {
                # The game holds the log open for writing; a shared-read stream is the only
                # way to read it while the process is alive.
                $tail = ''
                try {
                    $stream = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
                    try {
                        $reader = New-Object IO.StreamReader($stream)
                        $tail = $reader.ReadToEnd()
                    } finally { $stream.Dispose() }
                } catch { }
                if ($tail -match 'Go to Menu' -or $tail -match 'Mods detected') { $reachedMenu = $true; break }
            }
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($process.HasExited) { throw "Game exited after the mod was removed (exit $($process.ExitCode))" }
    if (-not $reachedMenu) { throw "Game did not reach the menu within ${TimeoutSeconds}s" }

    # The live process holds the log open, so it is released before the file is examined.
    Stop-Process -Id $process.Id -Force
    $process.WaitForExit()
    Start-Sleep -Milliseconds 300

    $text = [IO.File]::ReadAllText($log)
    if ($text -match '\[PEV\.Core\] Version') { throw 'PEV.Core was still loaded; the vanilla condition was not met' }

    Write-Output 'VANILLA-BOOT ok (game reaches menu with the mod absent)'
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    if ($moved -and (Test-Path -LiteralPath $parkedMod)) {
        Move-Item -LiteralPath $parkedMod -Destination $modDir -Force
    }
}
