# ADOFAI Programmable Events

A Unity Mod Manager mod for *A Dance of Fire and Ice* that lets chart authors place
custom events in the official editor, and lets anyone ship those events as independent
`.event` packages with their own scripts, icons and parameters.

**A custom-event chart still opens in the vanilla game.** Custom events are stored as
ordinary editor comments, so a player without the mod sees a normal level rather than a
corrupt one.

---

## What it does

- **In the editor** — custom events appear in the palette next to vanilla events, with a
  category, name and icon. You place, copy, delete and edit them the same way, using the
  game's own property controls.
- **In game** — each placed event runs its package's JavaScript, driven by the song clock.
  Instances are fully isolated: two copies of one event never share state.
- **Without the mod** — the level opens normally. If a package is missing, the event shows
  as a read-only *Missing Event* placeholder and its data is preserved byte-for-byte, so
  installing the package later restores it.

---

## Install

1. Install [Unity Mod Manager](https://www.nexusmods.com/site/mods/21) if you have not.
2. Download `PEV.Core-<version>.zip` from [Releases](../../releases).
3. In UMM, use **Mods → Install Mod** and pick the zip.
4. Place the independent `NoiseField.event` next to the `.adofai` chart that uses it, then open the chart.

The Core package contains no events. Each chart loads `.event` files from its own
directory only. Missing declared packages produce a notification and preserve event data;
the game's chart export includes declared packages.

---

## Write an event

An `.event` is a ZIP containing a manifest and a script:

```
MyEvent.event
├── event.json
├── main.js
└── icon.png
```

`event.json`:

```json
{
  "schemaVersion": 1,
  "id": "yourname.my-event",
  "version": "1.0.0",
  "entry": "main.js",
  "icon": "icon.png",
  "category": "VisualFx",
  "name": { "en": "My Event" },
  "params": [
    { "name": "strength", "type": "Float", "default": 0.8, "min": 0, "max": 2 }
  ]
}
```

`main.js`:

```js
function create(ctx, params) {
  var fired = false;
  return {
    update: function (t) {
      if (t < 0 || fired) return;
      fired = true;
      fx.flash({ color: "ffffff", duration: 0.2 });
    },
    dispose: function () { fired = false; }
  };
}
```

Validate and pack:

```bash
python tools/pack_event.py --check MyEvent.event
python tools/pack_event.py MyEvent.event
```

The full reference — parameter types, the effect API, sandbox limits and a
troubleshooting table — is in **[docs/EVENT-SDK.md](docs/EVENT-SDK.md)**. It is written to
be usable by both people and AI agents.

---

## Example events

Install event packages separately alongside the chart. The Core distribution
does not include event packages:

| Event | Demonstrates |
|---|---|
| `CameraWarp` | One-shot effect driven by parameters |
| `ScreenShake` | Two instances of one event keeping separate state |
| `Pulse` | Continuous per-frame animation via `update(t)` |
| `NoiseField` | Stored as a real decoration: place, drag, resize and retag it with the editor's own tools; MoveDecorations drives it by tag |
| `NoiseFieldState` | Controls matching noise fields by tag, including warning and activation |

---

## Build from source

Requires .NET SDK and a local ADOFAI install (the project references the game's own
assemblies).

```bash
python tools/build_release.py --version 0.1.0
```

That builds `src/PEV.Core` and writes the core-only `dist/PEV.Core-<version>.zip`
alongside a separate `NoiseField.event` containing the field and its state control.
See [the boundary audit](docs/CORE-BOUNDARY-AUDIT.md) for the implementation boundary.

The game path is set at the top of `src/PEV.Core/PEV.Core.csproj`.

---

## How it works

```
Editor palette click
  → AddEvent hook attaches instance data
  → author edits values in the native property panel
  → copy / delete hooks clone or detach that data
  → EncodeToDictionary rewrites every instance as an @PEV1 editor comment
  → saved to disk
  → Decode reads them back into virtual events
  → runtime drives each instance's script from the song clock
```

Design notes and the reasoning behind each decision are in
[ARCHITECTURE-v1.md](ARCHITECTURE-v1.md). Verification results for every stage are in
`STAGE0-RESULTS.md`, `P0-RESULTS.md`, `P1-RESULTS.md`, `P2-RESULTS.md`,
`P3-RESULTS.md`, `P4-RESULTS.md`.

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
