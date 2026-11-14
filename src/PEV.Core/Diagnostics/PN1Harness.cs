using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ADOFAI;
using HarmonyLib;
using PEV.Registry;
using PEV.Scripting;
using PEV.Storage;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PEV.Diagnostics
{
    /// <summary>
    /// P-N1 harness: prove the noise field renders, animates and is reclaimed.
    ///
    /// The decisive checks are the ones a numeric assertion cannot fake: a screenshot with
    /// the field visible, a frame index that advances while t advances, a UV offset that
    /// actually moves (the one genuinely unknown in the spec), and texture counts returning
    /// to zero on teardown. Inert unless "pn1-test.txt" exists next to the DLL.
    /// </summary>
    public static class PN1Harness
    {
        public static string LevelPath;
        public static bool Enabled;

        private static string _resultPath;
        private static bool _ran;

        public static void Configure(string modDir)
        {
            Enabled = File.Exists(Path.Combine(modDir, "pn1-test.txt"));
            if (!Enabled) return;

            _resultPath = Path.Combine(modDir, "pn1-results.txt");
            LevelPath = ReadOr(Path.Combine(modDir, "pn1-level.txt"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "A Dance of Fire and Ice", "Levels", "PN1Test.adofai"));

            try { File.WriteAllText(_resultPath, ""); } catch { }
            Row("HARNESS", "enabled", LevelPath);
        }

        private static string ReadOr(string file, string fallback)
        {
            try { if (File.Exists(file)) return File.ReadAllText(file).Trim(); } catch { }
            return fallback;
        }

        public static void Row(string key, string value, string note = "")
        {
            string line = $"{key}\t{value}\t{note}";
            Debug.Log("[PEV-PN1] " + line);
            try { File.AppendAllText(_resultPath, line + Environment.NewLine); } catch { }
        }

        /// <summary>Stopwatch ticks as a readable millisecond figure.</summary>
        private static string Ticks(long ticks)
        {
            if (ticks <= 0) return "0ms";
            return (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1") + "ms";
        }

        [HarmonyPatch(typeof(scnLevelSelect), "Start")]
        public static class AutoOpen
        {
            public static void Postfix()
            {
                if (!Enabled || _ran) return;
                try
                {
                    if (!File.Exists(LevelPath)) { Row("AUTOOPEN", "MISSING", LevelPath); return; }
                    scnEditor.levelToOpenOnLoad = LevelPath;
                    ADOBase.loader.LoadScene("scnEditor");
                    Row("AUTOOPEN", "ok", LevelPath);
                }
                catch (Exception e) { Row("AUTOOPEN", "EXCEPTION", e.ToString()); }
            }
        }

        [HarmonyPatch(typeof(scnEditor), "Start")]
        public static class Probe
        {
            public static void Postfix(scnEditor __instance)
            {
                if (!Enabled || _ran) return;
                _ran = true;
                ((MonoBehaviour)__instance).StartCoroutine(Run(__instance));
            }
        }

        /// <summary>
        /// Two decisions depend on facts this build cannot assume:
        /// 1. the decoration route (reuse MoveDecorations instead of our own mover) needs
        ///    scrDecorationManager to survive an unknown decoration type;
        /// 2. the background-warp look needs a grab shader that still exists at runtime.
        /// Both are cheap to measure and expensive to guess wrong.
        /// </summary>
        /// <summary>
        /// The background-refraction look needs a grab pass. Phira's active fill samples a
        /// captured scene texture; without one the fill can only be a flat translucent
        /// colour. Whether that channel exists is a fact this build cannot assume, so it is
        /// measured: the shader must be found AND expose a grab-like property. A shader that
        /// exists but reads nothing is worse than none, because it renders black.
        /// </summary>
        private static void ProbeGrabShader()
        {
            Shader sh = null;
            try { sh = Shader.Find("Hidden/BlendModes/VisualDeco/Grab"); } catch { }
            if (sh == null) { Row("GRAB", "MISSING", "shader not found"); return; }

            Row("GRAB", "FOUND", "instance=" + (sh.isSupported ? "supported" : "UNSUPPORTED"));
            string[] probes =
            {
                "_GrabTexture", "_GrabBlurDistance", "_Distortion", "_Distort",
                "_MainTex", "_Color", "_Opacity", "_Tile", "RepeatX", "RepeatY"
            };

            // Property lookup lives on Material, not Shader: the shader object only answers
            // "is this supported", the exposed uniforms are per-material.
            try
            {
                var mat = new Material(sh);
                var present = new List<string>();
                foreach (var p in probes)
                {
                    try { if (mat.HasProperty(p)) present.Add(p + "(" + mat.GetTag(p, false) + ")"); }
                    catch { }
                }
                Row("GRAB-PROPS", present.Count.ToString(), string.Join(" ", present));
                Row("GRAB-MATERIAL", "OK",
                    "shader=" + mat.shader.name + " pass=" + mat.passCount + " queue=" + mat.renderQueue);
                UnityEngine.Object.Destroy(mat);
            }
            catch (Exception e) { Row("GRAB-MATERIAL", "THREW", e.GetType().Name + ": " + e.Message); }

            // How the game itself drives it: find a live decoration's mesh renderer material.
            try
            {
                var dm = ADOBase.controller != null ? ADOBase.controller.decorationManager : null;
                var all = dm != null ? dm.allDecorations : null;
                if (all != null && all.Count > 0)
                {
                    var first = all[0] as scrVisualDecoration;
                    var mr = first != null ? first.meshRenderer : null;
                    if (mr != null && mr.sharedMaterial != null)
                    {
                        var gm = mr.sharedMaterial;
                        Row("GRAB-LIVE", gm.shader != null ? gm.shader.name : "no-shader",
                            "tex=" + (gm.HasProperty("_MainTex") && gm.GetTexture("_MainTex") != null ? "yes" : "no")
                            + " queue=" + gm.renderQueue);
                    }
                    else Row("GRAB-LIVE", "NO-MESHRENDERER", "first decoration has no mesh renderer");
                }
                else Row("GRAB-LIVE", "NO-DECORATIONS", "");
            }
            catch (Exception e) { Row("GRAB-LIVE", "EXCEPTION", e.Message); }
        }

        private static void ProbePlatform()
        {
            string[] shaders =
            {
                "Hidden/BlendModes/VisualDeco/Grab",
                "Legacy Shaders/Particles/Additive",
                "Sprites/Default"
            };
            foreach (var name in shaders)
            {
                Shader sh = null;
                try { sh = Shader.Find(name); } catch { }
                Row("SHADER", sh != null ? "FOUND" : "MISSING", name);
            }

            ProbeGrabShader();

            try
            {
                var dm = ADOBase.controller != null ? ADOBase.controller.decorationManager : null;
                Row("DECMANAGER", dm != null ? "OK" : "NULL",
                    dm != null ? dm.GetType().Name : "scrDecorationManager instance unavailable");

                if (dm != null)
                {
                    var methods = dm.GetType().GetMethods()
                        .Where(m => m.Name.Contains("Decoration"))
                        .Select(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name)) + ")")
                        .Distinct().Take(12).ToList();
                    Row("DECMANAGER-API", methods.Count.ToString(), string.Join(" | ", methods));

                    // The real entry is CreateDecoration(LevelEvent, out bool, int). Probed by
                    // signature rather than by name: an earlier version looked for a
                    // single-argument overload that never existed and reported a false miss.
                    var deco = dm.GetType().GetMethod("CreateDecoration",
                        new[] { typeof(LevelEvent), typeof(bool).MakeByRefType(), typeof(int) });
                    Row("DECMANAGER-CREATE", deco != null ? "SIGNATURE-OK" : "SIGNATURE-MISSING",
                        deco != null
                            ? "CreateDecoration(LevelEvent, out bool, int)"
                            : "unexpected signature; manager API changed");
                }
            }
            catch (Exception e)
            {
                Row("DECMANAGER", "EXCEPTION", e.Message);
            }
        }

        /// <summary>
        /// Placement is a pure function, so the anchor and scaling rules can be asserted
        /// exactly rather than judged from a screenshot. Anchor signs are the easiest thing
        /// in the whole feature to get backwards, and a screenshot only shows it at the one
        /// camera angle the test happened to use.
        /// </summary>
        private static void RunGeometryAssertions()
        {
            var cameraFrame = new Placement.Input
            {
                RelativeTo = NoiseFieldRelativeTo.Tile,
                Shape = NoiseFieldShape.Rect,
                Size = new Vector2(2f, 2f),
                TileSize = 1f,
                CameraRight = Vector3.right,
                CameraUp = Vector3.up,
                HalfWidth = 9f,
                HalfHeight = 5f,
                BasePosition = Vector3.zero
            };

            // Anchor centre: the quad centre sits on the base point.
            var centre = Input(cameraFrame, NoiseFieldAnchor.Center);
            Row("GEO-anchor-center", Near(Placement.Compute(centre).Center, Vector3.zero) ? "PASS" : "FAIL",
                $"{Placement.Compute(centre).Center}");

            // Top-left anchor: the rectangle's top-left corner touches the base point, so its
            // centre is right and below (Unity y is up, OpenGL-style texture v is up).
            var topLeft = Placement.Compute(Input(cameraFrame, NoiseFieldAnchor.TopLeft)).Center;
            Row("GEO-anchor-topleft", Near(topLeft, new Vector3(1f, -1f, 0f)) ? "PASS" : "FAIL",
                $"expected (1.00,-1.00) got {topLeft}");

            var bottomRight = Placement.Compute(Input(cameraFrame, NoiseFieldAnchor.BottomRight)).Center;
            Row("GEO-anchor-bottomright", Near(bottomRight, new Vector3(-1f, 1f, 0f)) ? "PASS" : "FAIL",
                $"expected (-1.00,1.00) got {bottomRight}");

            // Corner anchors must be mirrors of each other across the base point.
            bool mirrored = Near(topLeft, -bottomRight);
            Row("GEO-anchor-mirror", mirrored ? "PASS" : "FAIL", $"topleft {topLeft} vs bottomright {bottomRight}");

            // size is in tiles: 4 x 3 at tileSize 1.5 is 6 x 4.5 world units.
            var sized = Input(cameraFrame, NoiseFieldAnchor.Center);
            sized.Size = new Vector2(4f, 3f);
            sized.TileSize = 1.5f;
            var frame = Placement.Compute(sized);
            Row("GEO-tiles", Near(frame.Size, new Vector2(6f, 4.5f)) ? "PASS" : "FAIL",
                $"4x3 tiles at tileSize 1.5 => {frame.Size} (expected 6.0,4.5)");

            // CameraRatio: 1.0 spans the viewport, which is 2*halfW by 2*halfH.
            var ratio = Input(cameraFrame, NoiseFieldAnchor.Center);
            ratio.RelativeTo = NoiseFieldRelativeTo.CameraRatio;
            ratio.Size = new Vector2(0.5f, 0.25f);
            var ratioFrame = Placement.Compute(ratio);
            Row("GEO-ratio", Near(ratioFrame.Size, new Vector2(9f, 2.5f)) ? "PASS" : "FAIL",
                $"0.5x0.25 of an 18x10 viewport => {ratioFrame.Size} (expected 9.0,2.5)");

            // lockSize cancels zoom against the default ortho size: at orthoSize 2.5 (2x
            // zoomed in) the world extent halves, keeping the same screen size.
            var locked = Input(cameraFrame, NoiseFieldAnchor.Center);
            locked.LockSize = true;
            locked.HalfHeight = 2.5f;
            var lockedFrame = Placement.Compute(locked);
            Row("GEO-locksize", Near(lockedFrame.Size, new Vector2(1f, 1f)) ? "PASS" : "FAIL",
                $"2x2 tiles at 2x zoom, locked => {lockedFrame.Size} (expected 1.0,1.0)");

            // Circle ignores size and uses the diameter.
            var circle = Input(cameraFrame, NoiseFieldAnchor.Center);
            circle.Shape = NoiseFieldShape.Circle;
            circle.Radius = 3f;
            circle.TileSize = 1f;
            var circleFrame = Placement.Compute(circle);
            Row("GEO-circle", Near(circleFrame.Size, new Vector2(6f, 6f)) ? "PASS" : "FAIL",
                $"radius 3 at tileSize 1 => {circleFrame.Size} (expected 6.0,6.0)");

            // Rotation must rotate the anchor offset with the rectangle.
            var rotated = Input(cameraFrame, NoiseFieldAnchor.TopLeft);
            rotated.Rotation = 90f;
            var rotatedCentre = Placement.Compute(rotated).Center;
            Row("GEO-rotation", Near(rotatedCentre, new Vector3(1f, 1f, 0f)) ? "PASS" : "FAIL",
                $"topleft rotated 90deg => {rotatedCentre} (expected 1.00,1.00)");
        }

        private static Placement.Input Input(Placement.Input template, NoiseFieldAnchor anchor)
        {
            var copy = template;
            copy.Anchor = anchor;
            return copy;
        }

        private static bool Near(Vector2 a, Vector2 b) => Mathf.Abs(a.x - b.x) < 1e-3f && Mathf.Abs(a.y - b.y) < 1e-3f;

        private static bool Near(Vector3 a, Vector3 b) =>
            Mathf.Abs(a.x - b.x) < 1e-3f && Mathf.Abs(a.y - b.y) < 1e-3f && Mathf.Abs(a.z - b.z) < 1e-3f;

        private static IEnumerator Run(scnEditor editor)
        {
            yield return new WaitForSeconds(2f);
            ScriptHost.LogInit = true;

            RunGeometryAssertions();
            ProbePlatform();

            var manifest = EventRegistry.All.FirstOrDefault(m => m.Id == "pev.noise-field");
            if (manifest == null)
            {
                Row("SETUP", "FAIL", "pev.noise-field is not registered");
                yield break;
            }

            var type = (LevelEventType)PEV.Util.StableId.FromEventId(manifest.Id);

            var floors = PickFloors(editor, 3);
            if (floors.Count < 3) { Row("SETUP", "FAIL", "not enough free floors"); yield break; }

            // Three instances with deliberately different geometry. A field is a decoration
            // now, so size, position, rotation, colour, tag and depth are the decoration's own
            // properties and the configurations below set those instead of our parameters.
            var configs = new[]
            {
                new Dictionary<string, object>
                {
                    { "shape", "Rect" }, { "scale", new object[] { 150f, 133f } },
                    { "color", "ff2020" }, { "noiseMoveSpeed", new object[] { 1.5f, 0f } },
                    { "noiseEvolveSpeed", 12f }, { "depth", 2 }
                },
                new Dictionary<string, object>
                {
                    { "shape", "Circle" }, { "scale", new object[] { 120f, 80f } },
                    { "color", "ff8020" }, { "noiseMoveSpeed", new object[] { 0f, 0.8f } },
                    { "noiseEvolveSpeed", 20f }, { "feather", 1f }, { "depth", 2 }
                },
                new Dictionary<string, object>
                {
                    { "shape", "Rect" }, { "relativeTo", "Camera" },
                    { "scale", new object[] { 75f, 50f } }, { "position", new object[] { 0f, -1.5f } },
                    { "color", "ff4060" }, { "noiseMoveSpeed", new object[] { 2.5f, 0f } },
                    { "noiseEvolveSpeed", 30f }, { "depth", 4 }
                },
            };

            // Floors 4 and 8 share depth 2 on purpose: same-depth fields must composite into
            // a single union layer, so three fields should produce two quads, not three.

            for (int i = 0; i < floors.Count; i++)
            {
                EditorTestHelpers.SelectFloor(editor, floors[i]);
                editor.AddEventAtSelected(type);
                yield return null;
            }

            // Placement goes through the decoration path now, so the rows live in
            // level.decorations and never appear in editor.events.
            var ours = Rows(editor);
            int applied = 0;
            foreach (var ev in ours)
            {
                if (!LevelStorage.TryGetData(ev, out var d) || d.Id != "pev.noise-field") continue;
                int idx = floors.IndexOf(ev.floor);
                if (idx < 0 || idx >= configs.Length) continue;

                foreach (var kv in configs[idx])
                {
                    // Coerce through the declared PropertyType. scale/position are Vector2 and
                    // LevelEvent.Encode casts to that type directly, so writing a List makes
                    // every later save throw InvalidCastException — and scrDecoration.Setup
                    // throws the same way when the level plays.
                    object value = kv.Value;
                    if (value is object[] arr) value = new List<object>(arr);

                    ADOFAI.PropertyInfo pi = null;
                    try { ev.info?.propertiesInfo.TryGetValue(kv.Key, out pi); } catch { }
                    try { ev[kv.Key] = ParamCoercion.Coerce(pi, value); } catch { }
                }
                applied++;
            }
            // Tag the first field so a mover can target it, exactly as an author would. The
            // refresh matters: scrDecoration caches its tag set, and ResetDecorations (which
            // Play runs) clears the tag table before asking each decoration to remove itself
            // from its OLD tags. Mutating the event without telling the component leaves it
            // removing a key nobody re-registered, and the lookup throws mid-play.
            var tagged = ours.FirstOrDefault(e =>
                e.floor == floors[0] && LevelStorage.TryGetData(e, out var d) && d.Id == "pev.noise-field");
            if (tagged != null)
            {
                tagged["tag"] = "wave";
                try { editor.UpdateDecorationObject(tagged); } catch { }
            }

            Row("PLACED", applied.ToString(), "floors " + string.Join(", ", floors));
            Row("PLACED-AS-DECORATION", ours.Count.ToString(),
                $"rows in level.decorations with our marker; editor.events holds " +
                $"{editor.events.Count(e => LevelStorage.TryGetData(e, out _))} of ours (expect 0)");

            // Vanilla compatibility: the row has to survive vanilla's own decoder. If any of
            // these three are missing the row is not a decoration, and neither the gizmo nor
            // a tag lookup can find it.
            var probeRow = ours.FirstOrDefault();
            if (probeRow != null)
            {
                Row("ROW-SHAPE",
                    probeRow.eventType == LevelEventType.AddDecoration ? "PASS" : "FAIL",
                    $"eventType={probeRow.eventType} image={probeRow.GetString("decorationImage")} " +
                    $"isDecoration={probeRow.IsDecoration}");
                Row("ROW-PAYLOAD",
                    DecorationCarrier.TryUnpack(probeRow.GetString("maskingTarget"), out var unpacked)
                        ? "PASS" : "FAIL",
                    $"id={unpacked?.Id} uid={unpacked?.Uid} " +
                    $"params={unpacked?.Raw.Count}");
            }

            // The property panel is the thing the author actually touches. Every injected
            // parameter must be visible exactly when the selected row is ours.
            ProbeDecorationPanel();

            // Round trip: encode the level and read the row back. This is the check that
            // catches a payload that only lives in memory.
            ProbeEncodeRoundTrip(editor);

            // The editor experience is part of the acceptance criteria, so it is asserted
            // rather than eyeballed. A decoration has no tile badge — the engine gives it a
            // gizmo and a selectable object instead — so what matters here is that selecting
            // the row opens the decoration inspector rather than nothing at all.
            int selectable = 0;
            foreach (int floor in floors)
            {
                EditorTestHelpers.SelectFloor(editor, floor);
                yield return null;
                var row = ours.FirstOrDefault(x => x.floor == floor);
                var dec = row != null ? DecorationBridge.Find(row) : null;
                if (dec != null) selectable++;
                Row($"OBJECT-floor{floor}", dec != null ? "PASS" : "FAIL",
                    dec == null
                        ? "no scrDecoration built for this row; the gizmo and tag lookup cannot find it"
                        : $"{dec.GetType().Name} pivot={dec.pivotPosVec} scale={dec.scaleVec} tag={row.GetString("tag")}");
            }
            Row("OBJECTS", $"{selectable}/{floors.Count}", "decoration objects built for our rows");

            if (EventRegistry.TryGetByType(type, out _))
            {
                // Resolve through the same helper Publish used. GCS.levelEventsInfo is keyed
                // by whatever GCS.levelEventTypeString holds, which is a name for native
                // events; asking for the numeric form here returned a different (native) entry
                // and reported every label as empty.
                DecorationParams.TryGetNativeInfo(out var gi);
                var injected = DecorationParams.InjectedNames.ToList();
                Row("GROUPS", gi != null && gi.useGroups ? "PASS" : "CHECK",
                    $"AddDecoration useGroups={gi?.useGroups} tabs={string.Join(",", gi?.groups?.Select(g => g.name) ?? new string[0])}");
                Row("PARAMS-INJECTED", injected.Count.ToString(),
                    string.Join(",", injected));
                // The manifest carries Chinese labels; the panel reads one string chosen when
                // the PropertyInfo was built. Report both the language the game is running in
                // and the row text, so a client showing English can be told apart from a
                // package that never shipped a translation.
                var sampleLabel = gi != null && gi.propertiesInfo.TryGetValue("edgeDistance", out var pi)
                    ? pi.customLabel : null;
                Row("LABEL-LANG", RDString.language.ToString(),
                    $"edgeDistance shows \"{sampleLabel}\"");
                // Option captions do not go through customLabel: the dropdown asks the game's
                // localization table for "enum.<type>.<value>" at draw time. Read them back the
                // same way so an entry that landed in the wrong section shows up here.
                Row("LABEL-ENUM", RDString.language.ToString(),
                    EnumLabels.ReadCaptions("PEV.Scripting.NoiseFieldShape, PEV.Core",
                        new[] { "Rect", "Circle" }));
            }

            // Editor preview: a field must exist before playback ever starts, and its
            // parameters must track edits. This is the "placing it shows something" promise.
            yield return new WaitForSeconds(0.8f);
            Row("PREVIEW-idle", NoiseFieldRenderer.LiveCount.ToString(),
                "fields drawn in the editor before pressing play");

            var firstEvent = ours.FirstOrDefault(x =>
                LevelStorage.TryGetData(x, out var d) && d.Id == "pev.noise-field");
            if (firstEvent != null)
            {
                int entryCount = NoiseFieldRenderer.AllEntries().Count;
                firstEvent["noiseEvolveSpeed"] = 44f;
                yield return new WaitForSeconds(0.3f);
                var sample = NoiseFieldRenderer.AllEntries().FirstOrDefault();
                Row("PREVIEW-EDIT", sample != null ? "PASS" : "CHECK",
                    $"entries={entryCount}->{NoiseFieldRenderer.AllEntries().Count}, frame after edit={sample?.FrameIndex}");
                firstEvent["noiseEvolveSpeed"] = 12f;
            }

            yield return CaptureFrame("editor-preview");

            Row("LIVE-before-play", NoiseFieldRenderer.LiveCount.ToString(), "fields before playback");
            Row("TEX-before-play", NoiseFieldRenderer.TextureSets.ToString(), "texture sets before playback");

            editor.Play();

            // Instances start when the song clock reaches their tile. Floor 12 begins a few
            // seconds after floor 4, so waiting for "any field exists" samples too early and
            // reports a partial picture. Wait for all three, with a ceiling so a genuinely
            // missing instance still surfaces as a logged count.
            for (int i = 0; i < 200 && NoiseFieldRenderer.LiveCount < 3; i++)
                yield return new WaitForSeconds(0.05f);

            Row("LIVE-during-play", NoiseFieldRenderer.LiveCount.ToString(),
                $"textureSets={NoiseFieldRenderer.TextureSets}");
            Row("LAYERS", $"{NoiseFieldRenderer.LayerCount}/{NoiseFieldRenderer.LiveCount}",
                NoiseFieldRenderer.LayerCount < NoiseFieldRenderer.LiveCount ? "PASS same-depth union" : "CHECK no merge");
            Row("LAYER-BOXES", NoiseFieldRenderer.LayerReport(), "world extents per composited quad");
            foreach (var e in NoiseFieldRenderer.AllEntries())
                Row("FIELD", $"{e.Key} depth={e.Depth}",
                    $"centre=({e.World.Center.x:F2},{e.World.Center.y:F2}) size=({e.World.Size.x:F2},{e.World.Size.y:F2}) shape={e.Shape}");

            // Tag-driven control: an independent event must be able to move and hide fields
            // it did not create. Asserted against the renderer's own records.
            var before = NoiseFieldRenderer.AllEntries().FirstOrDefault(e => e.Tag == "wave");
            int moved = NoiseFieldRenderer.ApplyOverrides("wave",
                new Vector2(2f, 1f), 15f, 1.5f, 0.5f, 0, false);
            var after = NoiseFieldRenderer.AllEntries().FirstOrDefault(e => e.Tag == "wave");
            Row("TAG-move", moved > 0 && after != null && after.OverrideOffset.x > 1.9f ? "PASS" : "FAIL",
                $"targets={moved} offset=({after?.OverrideOffset.x:F2},{after?.OverrideOffset.y:F2}) " +
                $"rotation={after?.OverrideRotation:F1} scale={after?.OverrideScale:F2}");

            int hidden = NoiseFieldRenderer.ApplyOverrides("wave",
                Vector2.zero, 0f, 1f, 1f, 2, false);
            Row("TAG-state", hidden > 0 ? "PASS" : "FAIL", $"targets={hidden} visibility=forced-hidden");

            // Clearing must restore the untouched appearance, or a finished mover would
            // leave the field permanently displaced.
            NoiseFieldRenderer.ApplyOverrides("wave", Vector2.zero, 0f, 1f, 1f, 0, true);
            var cleared = NoiseFieldRenderer.AllEntries().FirstOrDefault(e => e.Tag == "wave");
            Row("TAG-clear", cleared != null && cleared.OverrideOffset == Vector2.zero
                && cleared.OverrideScale == 1f && cleared.OverrideVisibility == 0 ? "PASS" : "FAIL",
                $"offset={cleared?.OverrideOffset} scale={cleared?.OverrideScale:F2} vis={cleared?.OverrideVisibility}");
            Row("TAG-untagged", NoiseFieldRenderer.ByTag("nothing-has-this").Count == 0 ? "PASS" : "FAIL",
                "an unknown tag must match nothing rather than everything");

            yield return new WaitForSeconds(0.6f);
            yield return CaptureFrame("fields");

            // The screenshot cannot separate our texture from the track and camera over it.
            // This is the generator's own output, so a colour or shape complaint can be
            // attributed to the texture instead of to the compositing.
            try
            {
                string dumpDir = Path.Combine(Path.GetDirectoryName(_resultPath) ?? ".", "pn1-textures");
                int dumped = NoiseFieldRenderer.DumpTextures(dumpDir);
                Row("TEX-DUMP", dumped.ToString(), dumpDir);
            }
            catch (Exception e) { Row("TEX-DUMP", "FAIL", e.Message); }

            // Animation evidence: frame index and UV must change as t advances. If the UV
            // offset does not take effect on Sprites/Default, the visual still steps between
            // frames, so this log distinguishes "not animating" from "not scrolling".
            // Compare the values numerically: two formatted strings can differ by text while
            // the underlying state is frozen.
            var samples = new List<string>();
            int firstFrame = -1, lastFrame = -1;
            float firstUvX = 0f, lastUvX = 0f, firstUvY = 0f, lastUvY = 0f;
            for (int i = 0; i < 8; i++)
            {
                yield return new WaitForSeconds(0.15f);
                var e = FirstEntry();
                if (e == null) continue;
                if (firstFrame < 0) { firstFrame = e.FrameIndex; firstUvX = e.Uv.x; firstUvY = e.Uv.y; }
                lastFrame = e.FrameIndex;
                lastUvX = e.Uv.x;
                lastUvY = e.Uv.y;
                samples.Add($"t{i}: frame={e.FrameIndex} uv=({e.Uv.x:F3},{e.Uv.y:F3}) order={e.SortingOrder} scale={e.GameObject.transform.localScale}");
            }
            foreach (var s in samples) Row("SAMPLE", s, "");

            if (firstFrame >= 0)
            {
                bool frameMoved = firstFrame != lastFrame;
                bool uvMoved = Math.Abs(lastUvX - firstUvX) > 1e-3f || Math.Abs(lastUvY - firstUvY) > 1e-3f;
                Row("ANIMATES", (frameMoved || uvMoved) ? "PASS" : "FAIL",
                    $"frame {firstFrame}->{lastFrame}, uv ({firstUvX:F3},{firstUvY:F3})->({lastUvX:F3},{lastUvY:F3})");
                Row("UV-MOVING", uvMoved ? "PASS" : "FAIL",
                    uvMoved
                        ? $"offset advanced from ({firstUvX:F3},{firstUvY:F3}) to ({lastUvX:F3},{lastUvY:F3})"
                        : "offset did not advance; scrolling is not being applied");
            }
            else
            {
                Row("ANIMATES", "FAIL", "no field existed during the sampling window");
            }

            // Steady-state regeneration: with parameters unchanged the texture cache must
            // not rebuild. A climbing counter is the difference between a smooth scene and
            // the reported stutter, and it is invisible to any check that only looks at
            // "did the effect appear".
            int genStart = NoiseTextureFactory.Generations;
            yield return new WaitForSeconds(2.0f);
            int genEnd = NoiseTextureFactory.Generations;
            Row("REGEN-IDLE", (genEnd - genStart) == 0 ? "PASS" : "FAIL",
                $"texture sets built during 2s of steady playback = {genEnd - genStart} (expect 0)");

            // The bake is the only place this effect can cost a frame, so its wall time is
            // asserted rather than eyeballed. The stage split says which pass to attack when
            // the budget is missed; these are summed across the parallel frame threads, so
            // they add up to more than the wall time by design.
            //
            // The number that matters is the main thread's share, not the bake's total: the
            // pixels are built on a background thread now, so only the upload can cost a frame
            // — and each frame gets a bounded slice of it.
            Row("BAKE", NoiseTextureFactory.WorstBakeMs <= 120f ? "PASS" : "CHECK",
                $"sets={NoiseTextureFactory.Bakes} worstMainThread={NoiseTextureFactory.WorstBakeMs:F1}ms " +
                "(budget 120ms) " +
                $"stages/compose={Ticks(NoiseComposer.ComposeTicks)} " +
                $"effects={Ticks(NoiseComposer.EffectsTicks)} main={Ticks(NoiseComposer.MainTicks)}");
            // One bake's own size and split. The row above sums thirteen bakes, which cannot
            // say whether the ceiling was missed on compute or on upload.
            Row("BAKE-DETAIL", NoiseTextureFactory.LastBakeDetail, "size, payload and stage split of the last bake");

            // Sorting probe: report the range the scene actually uses, so depth mapping can
            // be checked against reality rather than an assumption.
            var orders = TrackSortingOrders();
            Row("SORTING-RANGE", orders, "track/deco renderers in scene");

            // Cleanup: stop playback and make sure fields and textures are reclaimed. Run
            // twice to prove teardown is not one-shot.
            for (int cycle = 0; cycle < 2; cycle++)
            {
                editor.Play();
                yield return new WaitForSeconds(0.8f);
                int during = NoiseFieldRenderer.LiveCount;
                RuntimeHost.Ensure().Teardown();
                yield return null;
                Row($"CLEANUP-{cycle}", (NoiseFieldRenderer.LiveCount == 0) ? "PASS" : "FAIL",
                    $"during={during} after={NoiseFieldRenderer.LiveCount} texSets={NoiseFieldRenderer.TextureSets}");
            }

            Row("DONE", "ok", "p-n1 harness finished");
        }

        private static NoiseFieldRenderer.Entry FirstEntry()
        {
            var list = NoiseFieldRenderer.AllEntries();
            return list.Count > 0 ? list[0] : null;
        }

        /// <summary>
        /// Our rows in the editor's decoration list.
        ///
        /// LevelStorage knows them by attachment, which survives undo because the uid travels
        /// inside the row; scanning decorations rather than events is the whole point of the
        /// move to decoration-backed storage.
        /// </summary>
        private static List<LevelEvent> Rows(scnEditor editor)
        {
            var result = new List<LevelEvent>();
            try
            {
                var level = editor != null ? editor.levelData : null;
                var decorations = level != null ? level.decorations : null;
                if (decorations == null) return result;

                foreach (var row in decorations)
                    if (row != null && LevelStorage.TryGetData(row, out _)) result.Add(row);
            }
            catch (Exception e) { Row("ROWS", "EXCEPTION", e.Message); }
            return result;
        }

        /// <summary>
        /// Asserts that the injected parameters show up for our rows and stay hidden for
        /// native ones.
        ///
        /// PropertyInfo.CheckIfShown is what the panel calls, so calling it here tests the
        /// exact predicate the UI uses. A parameter that leaks onto native decorations would
        /// add empty rows to every hand-placed image in the game — a far worse outcome than
        /// the parameters not appearing at all.
        /// </summary>
        private static void ProbeDecorationPanel()
        {
            var rows = new List<LevelEvent>();
            try
            {
                var level = ADOBase.editor != null ? ADOBase.editor.levelData : null;
                if (level != null && level.decorations != null) rows.AddRange(level.decorations);
            }
            catch { }

            var ours = rows.FirstOrDefault(r => r != null && LevelStorage.TryGetData(r, out _));
            var native = rows.FirstOrDefault(r =>
            {
                if (r == null) return false;
                if (LevelStorage.TryGetData(r, out _)) return false;
                string img = null;
                try { img = r.GetString("decorationImage"); } catch { }
                return !string.IsNullOrEmpty(img);
            });

            if (!DecorationParams.TryGetNativeInfo(out var info))
            {
                Row("PANEL", "FAIL", "AddDecoration metadata missing");
                return;
            }

            // A parameter with a condition of its own (feather, shown only for the circle
            // shape) is legitimately hidden on a rectangle. Counting it as a failure would
            // make the assertion wrong about correct behaviour, so unconditional rows are
            // required to show and conditional ones are only required not to leak.
            int shownOurs = 0, requiredOurs = 0, shownNative = 0, probed = 0;
            foreach (var name in DecorationParams.InjectedNames)
            {
                if (!info.propertiesInfo.TryGetValue(name, out var pi)) continue;
                probed++;

                bool conditional = DecorationParams.IsConditional(name);
                if (!conditional) requiredOurs++;

                if (ours != null && pi.CheckIfShown(ours, pi.groupName))
                {
                    shownOurs++;
                    if (conditional) requiredOurs++;
                }
                if (native != null && pi.CheckIfShown(native, pi.groupName)) shownNative++;
            }

            Row("PANEL-OURS", shownOurs >= requiredOurs ? "PASS" : "FAIL",
                $"{shownOurs}/{probed} injected rows visible on our decoration " +
                $"(required {requiredOurs}; the rest are shape-conditional)");
            Row("PANEL-NATIVE", shownNative == 0 ? "PASS" : "FAIL",
                $"{shownNative}/{probed} leaked onto a native decoration" +
                (native == null ? " (no native decoration present to test against)" : ""));

            // Native properties must still be there: reusing them is what makes the field
            // movable and recolourable by the engine's own tools.
            var expected = new[] { "scale", "rotation", "color", "opacity", "depth", "tag", "decorationImage" };
            var missing = expected.Where(n => !info.propertiesInfo.ContainsKey(n)).ToList();
            Row("PANEL-NATIVE-KEPT", missing.Count == 0 ? "PASS" : "FAIL",
                missing.Count == 0 ? string.Join(",", expected) : "missing " + string.Join(",", missing));
        }

        /// <summary>
        /// Encodes the level and reads our rows back out of the produced dictionary.
        ///
        /// Everything else in this harness inspects live objects. This is the only check that
        /// proves the payload reaches the file: a field whose parameters live only in memory
        /// would look perfect here and come back with defaults after a save and reopen.
        /// </summary>
        private static void ProbeEncodeRoundTrip(scnEditor editor)
        {
            try
            {
                var level = editor != null ? editor.levelData : null;
                if (level == null) { Row("ENCODE", "FAIL", "no level data"); return; }

                var dict = level.EncodeToDictionary();
                if (dict == null || !dict.TryGetValue("decorations", out var raw))
                {
                    Row("ENCODE", "FAIL", "encoded dictionary has no decorations array");
                    return;
                }

                var list = raw as System.Collections.IEnumerable;
                if (list == null) { Row("ENCODE", "FAIL", "decorations is not a list"); return; }

                int ours = 0, withPayload = 0, intact = 0;
                string firstId = null, firstKeys = null;
                foreach (var item in list)
                {
                    var row = item as Dictionary<string, object>;
                    if (row == null) continue;

                    if (!row.TryGetValue("decorationImage", out var img)) continue;
                    if (!DecorationCarrier.IsOurs(img as string)) continue;
                    ours++;

                    string payload = row.TryGetValue("maskingTarget", out var mt) ? mt as string : null;
                    if (string.IsNullOrEmpty(payload)) continue;
                    withPayload++;

                    if (DecorationCarrier.TryUnpack(payload, out var data))
                    {
                        intact++;
                        if (firstId == null)
                        {
                            firstId = data.Id;
                            firstKeys = data.Raw != null ? string.Join(",", data.Raw.Keys) : "";
                        }
                    }
                }

                Row("ENCODE", withPayload == ours && intact == ours ? "PASS" : "FAIL",
                    $"rows={ours} withPayload={withPayload} readable={intact}");

                if (firstId != null)
                    Row("ENCODE-PAYLOAD", firstId, "keys=" + firstKeys);
            }
            catch (Exception e)
            {
                Row("ENCODE", "EXCEPTION", e.Message);
            }
        }

        private static string TrackSortingOrders()
        {
            try
            {
                var renders = UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
                if (renders == null || renders.Length == 0) return "none";
                int min = int.MaxValue, max = int.MinValue;
                foreach (var r in renders)
                {
                    if (r == null) continue;
                    if (r.sortingOrder < min) min = r.sortingOrder;
                    if (r.sortingOrder > max) max = r.sortingOrder;
                }
                return $"sprites={renders.Length} min={min} max={max}";
            }
            catch (Exception e) { return "probe failed: " + e.Message; }
        }

        private static List<int> PickFloors(scnEditor editor, int count)
        {
            var result = new List<int>();
            int max = editor.floors.Count - 1;
            for (int i = 4; i <= max && result.Count < count; i += 4)
            {
                // Our rows live in decorations now, so a floor is "free" only if neither list
                // is already occupied. Checking editor.events alone would stack every field on
                // the same tile and report three fields where one exists.
                bool taken = editor.events.Any(e => e.floor == i && LevelStorage.TryGetData(e, out _));
                if (!taken)
                {
                    try
                    {
                        var decorations = editor.levelData != null ? editor.levelData.decorations : null;
                        taken = decorations != null && decorations.Any(d =>
                            d != null && d.floor == i && LevelStorage.TryGetData(d, out _));
                    }
                    catch { }
                }
                if (!taken) result.Add(i);
            }
            return result;
        }

        private static IEnumerator CaptureFrame(string tag)
        {
            yield return new WaitForEndOfFrame();

            string dir = Path.GetDirectoryName(_resultPath);
            if (string.IsNullOrEmpty(dir)) yield break;

            string path = Path.Combine(dir, $"pn1-{tag}.png");
            bool issued = false;
            try
            {
                var type = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule");
                var method = type?.GetMethod("CaptureScreenshot", new[] { typeof(string) });
                if (method == null) { Row("CAPTURE", "UNAVAILABLE", tag); yield break; }

                method.Invoke(null, new object[] { path });
                Row("CAPTURE", tag, path);
                issued = true;
            }
            catch (Exception e) { Row("CAPTURE", "EXCEPTION", tag + ": " + e.Message); }

            if (issued) yield return new WaitForSeconds(0.4f);
        }
    }
}
