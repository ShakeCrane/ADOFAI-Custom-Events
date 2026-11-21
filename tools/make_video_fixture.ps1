#!/usr/bin/env pwsh
# Build a video test chart directory: the chart, the packed event, and its media.
#
# The chart is generated rather than checked in: a binary .adofai would be the only
# machine-readable fixture, and its content is a handful of tiles plus one decoration row.
param(
    [string]$Out = "$PSScriptRoot\..\_probe\chart",
    [string]$Media = "$PSScriptRoot\..\_probe\media",
    [string]$VideoFile = "a-h264.mp4"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$eventSource = Join-Path $root 'src\Events\Video.event'
$manifest = Get-Content -LiteralPath (Join-Path $eventSource 'event.json') -Raw | ConvertFrom-Json

New-Item -ItemType Directory -Force -Path $Out | Out-Null

# A short, straight chart. Tile count decides the song length; 120 BPM with 4 tiles per
# beat gives 32 seconds for 128 tiles, enough for the 3-second test clips.
$tiles = 128
$angleData = @()
for ($i = 0; $i -lt $tiles - 1; $i++) { $angleData += 0.0 }

# The decoration row that carries the event. Values mirror the manifest defaults, then the
# test overrides the file so it points at a real clip.
$payload = @{
    id      = $manifest.id
    v       = $manifest.version
    uid     = 'videotest01'
    p       = @{
        file      = $VideoFile
        start     = 0
        trimStart = 0
        trimEnd   = 0
        speed     = 1
        loop      = $false
        holdEnd   = $true
        mode      = 'Fit'
        volume    = 100
        muted     = $true
    }
} | ConvertTo-Json -Depth 8 -Compress

$chart = [ordered]@{
    angleData   = $angleData
    settings    = [ordered]@{
        version                 = 19
        artist                  = 'Video Test'
        song                    = 'Barrage'
        author                  = 'VideoHarness'
        specialArtistType       = 'None'
        artistPermission        = ''
        songFilename            = ''
        bpm                     = 120.0
        volume                  = 100
        previewImage            = ''
        previewIcon             = ''
        previewIconColor        = 'ffffff'
        previewSongStart        = 0.0
        previewSongDuration     = 10.0
        songDesc                = ''
        separateCountdownTime   = $false
        levelDesc               = 'Video event harness chart'
        levelTags               = ''
        levelVersion            = 0
        requiredMods            = @()
        difficulty              = 1
        hitsound                = 'Kick'
        hitsoundVolume          = 100
        multiplier              = 1
        legacyFlash             = $false
        legacyCamRelativeTo     = $false
        legacySpriteTiles       = $false
        legacyTween             = $false
        disableV15Features      = $false
    }
    actions     = @()
    decorations = @(
        [ordered]@{
            floor           = 1
            eventType       = 'AddDecoration'
            decorationImage = 'pev/video'
            maskingTarget   = "pev1:$payload"
            position        = @(0.0, 0.0)
            relativeTo      = 'Tile'
            pivotOffset     = @(0.5, 0.5)
            scale           = @(100.0, 100.0)
            rotation        = 0.0
            color           = 'ffffff'
            opacity         = 100.0
            depth           = -1
            parallax        = @(0.0, 0.0)
            parallaxOffset  = @(0.0, 0.0)
            tag             = 'pev'
        }
    )
    pevEvents   = @(@{ id = $manifest.id; version = $manifest.version; file = 'Video.event' })
}

$chartPath = Join-Path $Out 'level.adofai'
$json = ConvertTo-Json -InputObject $chart -Depth 32
[IO.File]::WriteAllText($chartPath, $json, [Text.UTF8Encoding]::new($false))

# Copy the packed event next to the chart; the chart only loads packages from its own folder.
$eventZip = Join-Path $Out 'Video.event'
& python (Join-Path $root 'tools\pack_event.py') $eventSource -o $eventZip
if ($LASTEXITCODE -ne 0) { throw "Failed to pack the video fixture" }

# Copy the media the chart references, so the fixture is self-contained and portable.
$sourceVideo = Join-Path $Media $VideoFile
if (Test-Path $sourceVideo) {
    Copy-Item $sourceVideo (Join-Path $Out $VideoFile) -Force
} else {
    throw "media not found: $sourceVideo"
}

Write-Output "chart: $chartPath"
Write-Output "event: $eventZip"
Write-Output "media: $(Join-Path $Out $VideoFile)"
