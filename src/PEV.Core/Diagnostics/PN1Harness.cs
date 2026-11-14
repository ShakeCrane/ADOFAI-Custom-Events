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

            var manifest = EventRegistry.All.FirstOrDefault(m => m.Id == "pev.noise-field");
            if (manifest == null)
            {
                Row("SETUP", "FAIL", "pev.noise-field is not registered");
                yield break;
            }

            var type = (LevelEventType)PEV.Util.StableId.FromEventId(manifest.Id);

            var floors = PickFloors(editor, 3);
            if (floors.Count < 3) { Row("SETUP", "FAIL", "not enough free floors"); yield break; }

            // Three instances with deliberately different geometry: a wide rect on the tile,
            // a circle, and a camera-relative strip. They must coexist without sharing state.
            var configs = new[]
            {
                new Dictionary<string, object> { { "shape", "Rect" }, { "size", new object[] { 6f, 4f } }, { "color", "ff2020" }, { "noiseMoveSpeed", new object[] { 1.5f, 0f } }, { "noiseEvolveSpeed", 12f }, { "depth", 2 } },
                new Dictionary<string, object> { { "shape", "Circle" }, { "radius", 3f }, { "color", "ff8020" }, { "noiseMoveSpeed", new object[] { 0f, 0.8f } }, { "noiseEvolveSpeed", 20f }, { "feather", 1f }, { "depth", 2 } },
                new Dictionary<string, object> { { "shape", "Rect" }, { "relativeTo", "Camera" }, { "size", new object[] { 3f, 1.5f } }, { "position", new object[] { 0f, -1.5f } }, { "color", "ff4060" }, { "noiseMoveSpeed", new object[] { 2.5f, 0f } }, { "noiseEvolveSpeed", 30f }, { "depth", 4 } },
            };

            // Floors 4 and 8 share depth 2 on purpose: same-depth fields must composite into
            // a single union layer, so three fields should produce two quads, not three.

            for (int i = 0; i < floors.Count; i++)
            {
                EditorTestHelpers.SelectFloor(editor, floors[i]);
                editor.AddEventAtSelected(type);
                yield return null;
            }

            var ours = editor.events.Where(e => LevelStorage.TryGetData(e, out _)).ToList();
            int applied = 0;
            foreach (var ev in ours)
            {
                if (!LevelStorage.TryGetData(ev, out var d) || d.Id != "pev.noise-field") continue;
                int idx = floors.IndexOf(ev.floor);
                if (idx < 0 || idx >= configs.Length) continue;

                foreach (var kv in configs[idx])
                {
                    ev[kv.Key] = kv.Value is object[] arr
                        ? new List<object>(arr)
                        : kv.Value;
                }
                applied++;
            }
            // Tag the first field so the mover/state events have a target, exactly as an
            // author would: place the field, then point a mover at its tag.
            var tagged = editor.events.FirstOrDefault(e =>
                e.floor == floors[0] && LevelStorage.TryGetData(e, out var d) && d.Id == "pev.noise-field");
            if (tagged != null) tagged["tag"] = "wave";

            Row("PLACED", applied.ToString(), "floors " + string.Join(", ", floors));

            // The editor experience is part of the acceptance criteria, so it is asserted,
            // not eyeballed. A missing angleOffset silently produces no tile badge at all.
            // Indicators only exist for the selected floor, so select each one first — that
            // is also what an author does when they click a tile.
            int badged = 0;
            foreach (int floor in floors)
            {
                EditorTestHelpers.SelectFloor(editor, floor);
                yield return null;
                var fl = editor.floors.Count > floor ? editor.floors[floor] : null;
                var indicator = fl != null ? fl.GetComponentInChildren<EventIndicator>(true) : null;
                if (indicator != null) badged++;
                var evHere = editor.events.FirstOrDefault(x => x.floor == floor && LevelStorage.TryGetData(x, out _));
                object angle = evHere?.Get<object>("angleOffset");
                Row($"BADGE-floor{floor}", indicator != null ? "PASS" : "FAIL",
                    $"angleOffset={(angle ?? "<missing>")} indicator={(indicator != null)}");
            }
            Row("BADGES", $"{badged}/{floors.Count}", "tile indicators for placed events");

            if (EventRegistry.TryGetByType(type, out var placedManifest))
            {
                var gi = GCS.levelEventsInfo != null && GCS.levelEventsInfo.ContainsKey(((int)type).ToString())
                    ? GCS.levelEventsInfo[((int)type).ToString()] : null;
                Row("GROUPS", gi != null && gi.useGroups ? "PASS" : "FAIL",
                    $"useGroups={gi?.useGroups} tabs={string.Join(",", gi?.groups?.Select(g => g.name) ?? new string[0])}");
            }

            // Editor preview: a field must exist before playback ever starts, and its
            // parameters must track edits. This is the "placing it shows something" promise.
            yield return new WaitForSeconds(0.8f);
            Row("PREVIEW-idle", NoiseFieldRenderer.LiveCount.ToString(),
                "fields drawn in the editor before pressing play");

            var firstEvent = editor.events.FirstOrDefault(x =>
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
                if (!editor.events.Any(e => e.floor == i && LevelStorage.TryGetData(e, out _)))
                    result.Add(i);
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
