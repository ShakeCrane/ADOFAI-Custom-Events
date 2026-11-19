#!/usr/bin/env pwsh
# Generate the video acceptance media matrix.
#
# The fixtures are generated rather than checked in: sixteen small clips would be a
# multi-megabyte binary commit, and the only property the tests rely on is which
# container/codec each file holds. Regenerating them from this script makes that
# reproducible on any machine with FFmpeg.
#
# Usage: ./make_video_media.ps1 [-Out <dir>] [-Force]
param(
    [string]$Out = "$PSScriptRoot\..\_probe\media",
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw 'ffmpeg not found on PATH' }

New-Item -ItemType Directory -Force -Path $Out | Out-Null

$src = '-f lavfi -i testsrc2=size=320x180:rate=30:duration=3'
$jobs = @(
    @{ n = 'a-h264.mp4';          a = "$src -c:v libx264 -pix_fmt yuv420p -c:a aac" },
    @{ n = 'b-hevc.mp4';          a = "$src -c:v libx265 -pix_fmt yuv420p -tag:v hvc1" },
    @{ n = 'c-h264.mov';          a = "$src -c:v libx264 -pix_fmt yuv420p" },
    @{ n = 'd-h264.m4v';          a = "$src -c:v libx264 -pix_fmt yuv420p" },
    @{ n = 'e-vp8.webm';          a = "$src -c:v libvpx -pix_fmt yuv420p" },
    @{ n = 'g-vp9.webm';          a = "$src -c:v libvpx-vp9 -pix_fmt yuv420p" },
    @{ n = 'h-av1.mp4';           a = "$src -c:v libsvtav1 -pix_fmt yuv420p" },
    @{ n = 'i-h264.mkv';          a = "$src -c:v libx264 -pix_fmt yuv420p" },
    @{ n = 'j-mpeg4.mp4';         a = "$src -c:v mpeg4 -pix_fmt yuv420p" },
    @{ n = 'k-mjpeg.avi';         a = "$src -c:v mjpeg -pix_fmt yuvj420p" },
    @{ n = 'l-wmv2.wmv';          a = "$src -c:v wmv2 -pix_fmt yuv420p" },
    @{ n = 'm-theora.ogv';        a = "$src -c:v libtheora" },
    @{ n = 'n-prores.mov';        a = "$src -c:v prores -pix_fmt yuv422p10le" },
    @{ n = 'o-h264.ts';           a = "$src -c:v libx264 -pix_fmt yuv420p -f mpegts" },
    @{ n = 'p-hevc.mkv';          a = "$src -c:v libx265 -pix_fmt yuv420p" },
    @{ n = 'q-h264-audio.mp4';    a = "$src -f lavfi -i sine=frequency=440:duration=3 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest" },
    @{ n = 'x-1080p60.mp4';       a = '-f lavfi -i testsrc2=size=1920x1080:rate=60:duration=3 -c:v libx264 -preset ultrafast -pix_fmt yuv420p' },
    @{ n = 'z-4k.mp4';            a = '-f lavfi -i testsrc2=size=3840x2160:rate=30:duration=1 -c:v libx264 -preset ultrafast -pix_fmt yuv420p' },
    # Variable frame rate: the same nominal 30 fps clip whose packets switch to 10 fps
    # halfway through. Seeking by media time has to land on the right picture here, which
    # is exactly what frame-number arithmetic would get wrong.
    @{ n = 'v-vfr.mp4';           a = "-f lavfi -i testsrc2=size=320x180:rate=30:duration=3 -vf `"setpts='if(lt(N,45),N/30,(45/30)+(N-45)/12)/TB'`" -vsync vfr -c:v libx264 -pix_fmt yuv420p" }
)

$made = 0
foreach ($job in $jobs) {
    $target = Join-Path $Out $job.n
    if ((Test-Path -LiteralPath $target) -and -not $Force) { continue }

    $arguments = @('-hide_banner', '-loglevel', 'error', '-y') + ($job.a -split ' ') + @($target)
    # FFmpeg writes progress to stderr; with $ErrorActionPreference='Stop' a native program's
    # stderr output becomes a terminating error, so it is captured instead of piped.
    $ErrorActionPreference = 'Continue'
    try { & $ffmpeg @arguments 2>$null | Out-Null } finally { $ErrorActionPreference = 'Stop' }
    if (-not (Test-Path -LiteralPath $target)) { throw "ffmpeg did not produce $($job.n)" }
    $made++
}

Write-Output "$made file(s) generated in $Out"
