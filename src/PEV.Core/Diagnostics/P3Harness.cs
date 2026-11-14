using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ADOFAI;
using HarmonyLib;
using PEV.Registry;
using PEV.Storage;
using PEV.Scripting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PEV.Diagnostics
{
    /// <summary>
    /// P3 harness. Two jobs:
    ///   1. prove that undo/redo does not silently drop custom events
    ///   2. confirm the Missing Event path keeps data intact
    /// Inert unless "p3-test.txt" exists next to the DLL.
    /// </summary>
    public static class P3Harness
    {
        public static string LevelPath;
        public static bool Enabled;

        private static string _resultPath;
        private static bool _ran;

        public static void Configure(string modDir)
        {
            Enabled = File.Exists(Path.Combine(modDir, "p3-test.txt"));
            if (!Enabled) return;

            _resultPath = Path.Combine(modDir, "p3-results.txt");
            LevelPath = ReadOr(Path.Combine(modDir, "p3-level.txt"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "A Dance of Fire and Ice", "Levels", "P3Test.adofai"));

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
            Debug.Log("[PEV-P3] " + line);
            try { File.AppendAllText(_resultPath, line + Environment.NewLine); } catch { }
        }

        [HarmonyPatch(typeof(scnLevelSelect), "Start")]
        public static class AutoOpen
        {
            public static void Postfix()
            {
                if (!Enabled) return;
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

        private static IEnumerator Run(scnEditor editor)
        {
            yield return new WaitForSeconds(2.5f);

            var cameraWarp = FindType("pev.camera-warp");
            if (cameraWarp == null) { Row("SETUP", "FAIL", "camera-warp not registered"); yield break; }

            // ---------- Undo / redo ----------
            EditorTestHelpers.Row = Row;
            int startCount = CountAttached(editor);
            Row("UNDO-baseline", startCount.ToString(), "attached events at load");

            int floor = EditorTestHelpers.PickFreeFloor(editor);
            if (floor < 0) { Row("UNDO", "SKIP", "no free floor"); yield break; }

            EditorTestHelpers.SelectFloor(editor, floor);
            editor.AddEventAtSelected(cameraWarp.Value);
            yield return null;
            int afterPlace = CountAttached(editor);
            Row("UNDO-after-place", afterPlace.ToString(), $"floor {floor}");
            Row("UNDO-stack", DescribeUndoState(editor, "after place"), "");

            // The editor snapshots inside AddEventAtSelected, but its undo pops one state
            // and applies the one before it, so a single call may not land on the
            // pre-placement snapshot. Drive it until the count actually drops, and report
            // how many steps that took rather than assuming one.
            int afterUndo = afterPlace;
            int steps = 0;
            for (int i = 0; i < 5 && afterUndo >= afterPlace && editor.undoStates.Count > 0; i++)
            {
                editor.Undo();
                yield return null;
                steps++;
                afterUndo = CountAttached(editor);
                Row("UNDO-step", afterUndo.ToString(),
                    $"after undo #{steps}: undo={editor.undoStates.Count}, redo={editor.redoStates.Count}, " +
                    $"rawEvents={editor.events.Count}");
            }

            Row("UNDO", afterUndo == startCount ? "PASS" : "FAIL",
                $"attached {afterPlace} -> {afterUndo} (expected {startCount}) after {steps} undo step(s)");
            Row("UNDO-objects", DescribeObjects(editor), "event objects after undo");

            editor.Redo();
            yield return null;
            int afterRedo = CountAttached(editor);
            Row("REDO", afterRedo > afterUndo ? "PASS" : "FAIL",
                $"attached {afterUndo} -> {afterRedo} (placement should reappear)");

            // ---------- The real risk: save after undo ----------
            // Undo rebuilds LevelData through Copy(), which replaces every LevelEvent
            // object. If the binding were reference-keyed, this encode would silently
            // write zero custom events.
            editor.Undo();
            yield return null;
            editor.Redo();
            yield return null;

            var dict = editor.levelData.EncodeToDictionary();
            var actions = dict["actions"] as List<Dictionary<string, object>>;

            // Count what we wrote, not every @PEV1 comment in the file. Comments whose
            // definition is not installed are deliberately preserved verbatim, so they
            // are present but are not instances.
            var liveUids = new HashSet<string>();
            foreach (var e in editor.events)
                if (LevelStorage.TryGetData(e, out var dd) && !string.IsNullOrEmpty(dd.Uid))
                    liveUids.Add(dd.Uid);

            int written = 0, preserved = 0;
            foreach (var a in actions)
            {
                if (Str(a, "eventType") != "EditorComment") continue;
                string c = Str(a, "comment");
                if (!EventInstanceData.LooksLikeOurs(c)) continue;

                var parsed = EventInstanceData.Parse(c, out _);
                if (parsed != null && liveUids.Contains(parsed.Uid ?? "")) written++;
                else preserved++;
            }

            Row("SAVE-AFTER-UNDO", written == afterRedo ? "PASS" : "FAIL",
                $"written={written}, attached instances={afterRedo}, preserved(no definition)={preserved}");

            foreach (var a in actions)
            {
                string c = Str(a, "comment");
                if (c != null && EventInstanceData.LooksLikeOurs(c))
                    Row("PAYLOAD", c.Substring(0, Math.Min(110, c.Length)),
                        $"floor {(a.TryGetValue("floor", out var fv) ? fv : null)}");
            }

            // ---------- Missing definition must stay intact ----------
            VerifyMissingDefinition(editor);

            Row("DONE", "ok", "p3 harness finished");
        }

        private static void VerifyMissingDefinition(scnEditor editor)
        {
            try
            {
                // Missing packages become visible placeholders, but the bytes on disk must
                // not change. Encode a level that carries such a payload and compare.
                string payload = "@PEV1 {\"id\":\"pev.not-installed\",\"v\":\"1.0.0\",\"uid\":\"miss01\",\"p\":{\"x\":1}}";
                var ev = new LevelEvent(5, LevelEventType.EditorComment);
                ev["comment"] = payload;
                editor.events.Add(ev);

                var dict = editor.levelData.EncodeToDictionary();
                var actions = dict["actions"] as List<Dictionary<string, object>>;
                bool preserved = actions.Any(a => (Str(a, "comment") ?? "") == payload);
                Row("MISSING-preserved", preserved ? "PASS" : "FAIL",
                    "payload with no installed definition must round-trip byte-identical");

                editor.events.Remove(ev);
            }
            catch (Exception e) { Row("MISSING-preserved", "EXCEPTION", e.ToString()); }

            try
            {
                // Now confirm unresolved payloads from the actual load became placeholders.
                var placeholders = editor.events
                    .Where(e => LevelStorage.TryGetData(e, out var d) && d.IsPlaceholder)
                    .ToList();

                Row("PLACEHOLDER-count", placeholders.Count.ToString(), "visible placeholders");

                foreach (var ph in placeholders)
                {
                    LevelStorage.TryGetData(ph, out var d);
                    Row("PLACEHOLDER-item",
                        $"{d.Id} uid={d.Uid}",
                        $"floor={ph.floor} type={(int)ph.eventType} reason={d.PlaceholderReason}");
                }

                bool registered = EventRegistry.TryGetByType(
                    (LevelEventType)PEV.Util.StableId.FromEventId(PEV.Registry.PlaceholderEvent.Id), out _);
                Row("PLACEHOLDER-registered", registered ? "PASS" : "FAIL",
                    "pev.missing is a registered editor type");

                // Placeholders must not be armed for execution. Compare by identity rather
                // than counting: with other events in the level a count tells us nothing.
                var runtime = RuntimeHost.Ensure();
                var field = typeof(RuntimeHost).GetField("_instances",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var armed = field?.GetValue(runtime) as System.Collections.IList;

                int armedPlaceholders = 0;
                if (armed != null)
                {
                    foreach (var item in armed)
                    {
                        var inst = item as PEV.Scripting.EventInstance;
                        if (inst != null && inst.Data != null && inst.Data.IsPlaceholder)
                            armedPlaceholders++;
                    }
                }
                Row("PLACEHOLDER-not-armed", armedPlaceholders == 0 ? "PASS" : "FAIL",
                    $"placeholder instances armed for execution={armedPlaceholders}, total armed={armed?.Count ?? -1}");

                // The panel must open for a placeholder, and every field must come back
                // disabled: these values describe the problem, they are not editable state.
                var first = placeholders.FirstOrDefault();
                if (first == null) { Row("PLACEHOLDER-panel", "SKIP", "no placeholder to select"); return; }

                try
                {
                    var floor = editor.floors.FirstOrDefault(f => f.seqID == first.floor);
                    if (floor != null)
                    {
                        editor.selectedFloors.Clear();
                        editor.selectedFloors.Add(floor);
                    }

                    editor.levelEventsPanel.ShowPanel(first.eventType, 0);
                    Row("PLACEHOLDER-panel", "PASS", $"ShowPanel opened for floor {first.floor}");

                    var panel = editor.levelEventsPanel.panelsList
                        .FirstOrDefault(p => p.levelEventType == first.eventType);
                    if (panel == null) { Row("PLACEHOLDER-readonly", "SKIP", "no panel instance"); return; }

                    int total = 0, disabled = 0;
                    foreach (var kv in panel.properties)
                    {
                        var ctrl = kv.Value != null ? kv.Value.control : null;
                        if (ctrl == null) continue;
                        total++;
                        if (!ctrl.propertyInfo.isEnabled) disabled++;
                    }
                    Row("PLACEHOLDER-readonly", total > 0 && disabled == total ? "PASS" : "FAIL",
                        $"fields disabled {disabled}/{total}");
                }
                catch (Exception e) { Row("PLACEHOLDER-panel", "EXCEPTION", e.ToString()); }
            }
            catch (Exception e) { Row("PLACEHOLDER-count", "EXCEPTION", e.ToString()); }
        }

        private static string DescribeUndoState(scnEditor editor, string phase)
        {
            try
            {
                return $"{phase}: undo={editor.undoStates.Count}, redo={editor.redoStates.Count}, " +
                       $"initialized={editor.initialized}, changingState={editor.changingState}, " +
                       $"events={editor.events.Count}";
            }
            catch (Exception e) { return phase + ": " + e.Message; }
        }

        private static int CountAttached(scnEditor editor)
        {
            return editor.events.Count(e => LevelStorage.TryGetData(e, out _));
        }

        private static string DescribeObjects(scnEditor editor)
        {
            var ours = editor.events.Where(e => LevelStorage.TryGetData(e, out _)).ToList();
            if (ours.Count == 0) return "none";
            return string.Join(", ", ours.Select(e =>
            {
                LevelStorage.TryGetData(e, out var d);
                return $"floor={e.floor}/uid={d.Uid}";
            }));
        }

        private static LevelEventType? FindType(string id)
        {
            var m = EventRegistry.All.FirstOrDefault(x => x.Id == id);
            if (m == null) return null;
            return (LevelEventType)PEV.Util.StableId.FromEventId(m.Id);
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            return d != null && d.TryGetValue(key, out var v) ? v as string : null;
        }
    }

    /// <summary>Shared editor-driving helpers for the harnesses.</summary>
    public static class EditorTestHelpers
    {
        public static Action<string, string, string> Row;

        public static void SelectFloor(scnEditor editor, int floor)
        {
            var f = editor.floors.FirstOrDefault(x => x.seqID == floor);
            if (f == null) return;
            editor.selectedFloors.Clear();
            editor.selectedFloors.Add(f);

            // Selecting by writing the list skips the editor's own step of redrawing the
            // tile's event indicators, which is driven from the selection-change path. A
            // test that asserts badges must reproduce that step, or every floor except the
            // one the editor last drew looks badgeless.
            try { editor.ShowEventIndicators(f); } catch { }
        }

        public static int PickFreeFloor(scnEditor editor)
        {
            int max = editor.floors.Count - 1;
            for (int i = 2; i <= max; i++)
            {
                if (!editor.events.Any(e => e.floor == i && LevelStorage.TryGetData(e, out _)))
                    return i;
            }
            return -1;
        }
    }
}
