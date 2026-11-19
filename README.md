# ADOFAI Programmable Events

A Unity Mod Manager mod for *A Dance of Fire and Ice* that lets chart authors place
custom events in the official editor, and lets anyone ship those events as independent
`.event` packages with their own scripts, icons and parameters.

**A custom-event chart still opens in the vanilla game.** Core stores event data in
vanilla editor comments or native decoration rows.

---

## What it does

- **In the editor** — custom events appear in the palette next to vanilla events, with a
  category, name and icon. You place, copy, delete and edit them the same way, using the
  game's own property controls.
- **In game** — each placed event runs its package's JavaScript, driven by the song clock.
  Each instance has its own script state.
- **Without the mod** — the level opens in the vanilla game. With Core enabled, a missing
  package leaves its event data in the chart so that reinstalling the package can restore it.

---

## Install

1. Install [Unity Mod Manager](https://www.nexusmods.com/site/mods/21) if you have not.
2. Download `PEV.Core-<version>.zip` from the [latest GitHub Release](https://github.com/ShakeCrane/ADOFAI-Custom-Events/releases/latest).
3. In UMM, use **Mods → Install Mod** and pick the zip.
4. To use the noise field, place [`NoiseField.event`](https://github.com/ShakeCrane/ADOFAI-Custom-Events/raw/refs/heads/main/template-events/NoiseField.event) next to the `.adofai` chart, then open the chart.
5. To add a video, place `Video.event` next to the chart and put the video file beside it too.

Video needs no extra software for MP4/MOV/M4V (H.264, HEVC, AV1), AVI, WMV and VP8 WebM.
Other containers — MKV, FLV, TS, Ogg, ProRes, VP9 WebM — are converted once into
`Mods/PEV.Core/video-cache/`, which needs `ffmpeg.exe` beside the mod, beside the chart, or
on `PATH`. Without it those files report the exact reason instead of failing silently.

The Core package contains no events. Each chart loads `.event` files from its own
directory only. Missing declared packages produce a notification and preserve event data;
the game's chart export includes declared packages.

---

## Write an event

Start with the **[.event authoring guide](docs/EVENT-SDK.md)**. It includes a working starter
package, the manifest and JavaScript reference, and an editor and playback test checklist.
Run these commands from the repository root to build the `Pulse` example:

```bash
python tools/pack_event.py --check src/Events/Pulse.event
python tools/pack_event.py src/Events/Pulse.event -o Pulse.event
```

Place the resulting `Pulse.event` next to a saved `.adofai` chart and reopen it with Core enabled.

---

## Example events

Download ready-to-use packages from [`template-events/`](template-events/) and place them beside the chart. `NoiseField.event` contains both the noise field and its state control. The table also lists examples you can build from source.

| Event | Demonstrates |
|---|---|
| `CameraWarp` | One-shot flash and shake driven by parameters |
| `ScreenShake` | One-shot camera shake with a per-instance trigger |
| `Pulse` | Continuous per-frame animation via `update(t)` |
| `NoiseField` | Stored as a real decoration: place, drag, resize and retag it with the editor's own tools; MoveDecorations drives it by tag. [Download](https://github.com/ShakeCrane/ADOFAI-Custom-Events/raw/refs/heads/main/template-events/NoiseField.event) |
| `NoiseFieldState` | Controls matching noise fields by tag, including warning and activation |
| `Video` | Plays a video file on a real decoration, locked to the song clock. Drag, resize, rotate, depth and tag it like any decoration. Widely supported formats play directly; the rest are converted once to a cache. [Download](https://github.com/ShakeCrane/ADOFAI-Custom-Events/raw/refs/heads/main/template-events/Video.event) |

---

## Build from source

Requires Python 3, the .NET SDK with .NET Framework 4.8 build support, and your own ADOFAI
installation with Unity Mod Manager. Set `ADOFAI_GAME_DIR` to the game directory and restore
the pinned script dependencies before building. For example, in PowerShell:

```powershell
$env:ADOFAI_GAME_DIR = 'X:\SteamLibrary\steamapps\common\A Dance of Fire and Ice'
python tools/restore_dependencies.py
python tools/build_release.py --version 0.6.2
```

That builds `src/PEV.Core` and writes the core-only `dist/PEV.Core-<version>.zip`
alongside a separate `NoiseField.event` containing the field and its state control.
See [the boundary audit](docs/CORE-BOUNDARY-AUDIT.md) for the implementation boundary.

The build uses assemblies from your local game installation. The checked-in shader bundle
is sufficient for packaging. To edit or rebuild shaders, start with the [shader source notes](src/UnityShaders/Reference/README.md).

---

## How it works

```
Editor palette click
  → AddEvent hook attaches instance data
  → author edits values in the native property panel
  → copy / delete hooks clone or detach that data
  → the chart saves its package dependencies in pevEvents
  → event data travels in @PEV1 comments or native AddDecoration rows
  → Core finds the chart's .event files and restores the instances on load
  → runtime drives each instance's script from the song clock
```

See the [technical baseline](docs/TECHNICAL-BASELINE.md) for the architecture and the
[release validation record](docs/NOISEFIELD-RELEASE-VALIDATION.md) for test results.

---

## Status

Working, but not yet battle-tested against real community charts.

Verified on game build `r150` (2026-11-03), Unity `6000.3.25f1`, Mono backend,
UMM `0.32.5.0`:

- custom events save, reload and survive vanilla open/save
- undo/redo, copy, delete keep instance data consistent
- script sandbox blocks CLR access, `eval`, runaway loops and recursion
- continuous effects run per frame and clean up after themselves

The GPU noise field has been exercised in the game, including depth, capture modes,
states and inversion. Testing used synthetic charts; real community charts, different
GPUs and full frame-by-frame comparison in Unity remain to be verified. See
[noise field validation](docs/NOISEFIELD-RELEASE-VALIDATION.md).

---

## Licence

The project is distributed under [AGPL-3.0](LICENSE). The independent noise-field
package includes assets and shader work derived from Phira-Pro; see the notices in
the package and the [reference record](docs/NOISEFIELD-REFERENCE.md).
