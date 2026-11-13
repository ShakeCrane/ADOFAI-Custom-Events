using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ADOFAI;
using HarmonyLib;
using PEV.Registry;
using PEV.Storage;
using UnityEngine;
using Debug = UnityEngine.Debug;
using EventInstance = PEV.Scripting.EventInstance;

namespace PEV.Diagnostics
{
    /// <summary>
    /// P2 harness: drives the editor the way a chart author would and checks that the
    /// stored data follows. Inert unless "p2-test.txt" exists next to the DLL.
    /// </summary>
    public static class P2Harness
    {
        public static string LevelPath;
        public static bool Enabled;

        private static string _resultPath;
        private static bool _ran;

        public static void Configure(string modDir)
        {
            Enabled = File.Exists(Path.Combine(modDir, "p2-test.txt"));
            if (!Enabled) return;

            _resultPath = Path.Combine(modDir, "p2-results.txt");
            LevelPath = ReadOr(Path.Combine(modDir, "p2-level.txt"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "A Dance of Fire and Ice", "Levels", "P2Test.adofai"));

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
            Debug.Log("[PEV-P2] " + line);
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
            var screenShake = FindType("pev.screen-shake");
            if (cameraWarp == null)
            {
                Row("SETUP", "FAIL", "camera-warp not registered");
                yield break;
            }

            Row("BASELINE", DescribeEvents(editor), "events at load");

            // --- 1. Place a new instance the way the palette button does ---
            int placeFloor = PickFreeFloor(editor, 3);
            if (placeFloor < 0) { Row("PLACE", "FAIL", "no free floor"); yield break; }

            SelectFloor(editor, placeFloor);
            editor.AddEventAtSelected(cameraWarp.Value);
            yield return null;

            var placed = FindOnFloor(editor, placeFloor, cameraWarp.Value);
            Row("PLACE", placed != null ? "PASS" : "FAIL",
                $"floor {placeFloor}: {DescribeOne(placed)}");

            // --- 2. Edit a parameter through the same store the inspector writes to ---
            if (placed != null)
            {
                placed["strength"] = 1.25f;
                placed["duration"] = 6f;
                Row("EDIT", "ok", $"set strength=1.25 duration=6 -> {DescribeOne(placed)}");
            }

            // --- 3. Copy it to another floor ---
            int copyFloor = PickFreeFloor(editor, placeFloor + 2);
            bool copied = false;
            if (placed != null && copyFloor >= 0)
            {
                copied = CopyTo(editor, placed, copyFloor);
                yield return null;
                var copy = FindOnFloor(editor, copyFloor, cameraWarp.Value);
                Row("COPY", copy != null ? "PASS" : "FAIL", $"floor {copyFloor}: {DescribeOne(copy)}");

                if (copy != null && placed != null)
                {
                    LevelStorage.TryGetData(placed, out var a);
                    LevelStorage.TryGetData(copy, out var b);
                    bool distinctUid = a != null && b != null && a.Uid != b.Uid;
                    Row("COPY-UID", distinctUid ? "PASS" : "FAIL",
                        $"{a?.Uid} vs {b?.Uid}");
                }
            }

            // --- 4. Delete one instance ---
            if (placed != null)
            {
                bool had = LevelStorage.TryGetData(placed, out _);
                editor.RemoveEvent(placed);
                yield return null;
                bool stillAttached = LevelStorage.TryGetData(placed, out _);
                bool stillListed = editor.events.Contains(placed);
                Row("DELETE", (!stillAttached && !stillListed) ? "PASS" : "FAIL",
                    $"wasAttached={had}, stillAttached={stillAttached}, stillListed={stillListed}");
            }

            Row("AFTER-EDITS", DescribeEvents(editor), "events after place/edit/copy/delete");

            // --- 5. Encode and write: the comment set must match what is in the editor ---
            string exportPath = Path.Combine(Path.GetDirectoryName(LevelPath) ?? ".", "P2-roundtrip.adofai");
            var summaries = LevelStorage.Bake(editor.levelData, null);
            var dict = editor.levelData.EncodeToDictionary();
            var actions = dict["actions"] as List<Dictionary<string, object>>;

            int pevComments = actions.Count(a =>
                Str(a, "eventType") == "EditorComment" &&
                EventInstanceData.LooksLikeOurs(Str(a, "comment")));
            int unknown = actions.Count(a =>
                Str(a, "eventType") is string s && !Enum.IsDefined(typeof(LevelEventType), s) &&
                !EventRegistry.All.Any(m => ((int)PEV.Util.StableId.FromEventId(m.Id)).ToString() == s));

            Row("ENCODE", unknown == 0 ? "PASS" : "FAIL",
                $"actions={actions.Count}, pevComments={pevComments}, unknownTypes={unknown}");

            File.WriteAllText(exportPath, GDMiniJSON.Json.Serialize(dict));
            Row("EXPORT", "ok", exportPath);

            foreach (var a in actions)
            {
                if (Str(a, "eventType") == "EditorComment")
                    Row("PAYLOAD", (Str(a, "comment") ?? "").Substring(0, Math.Min(120, (Str(a, "comment") ?? "").Length)),
                        $"floor {(a.TryGetValue("floor", out var fv) ? fv : null)}");
            }

            // --- 6. Reopen: decode the written file back into a fresh LevelData ---
            // Decoding in memory rather than reloading the scene keeps this coroutine alive
            // and still exercises the real LevelData.Decode path.
            try
            {
                var roundTrip = GDMiniJSON.Json.Deserialize(File.ReadAllText(exportPath))
                    as Dictionary<string, object>;
                var fresh = new LevelData();
                fresh.Decode(roundTrip, out var status);
                Row("REOPEN-status", status.ToString(), "vanilla decode result");

                var restored = fresh.levelEvents
                    .Where(e => e.eventType != LevelEventType.EditorComment)
                    .ToList();
                Row("REOPEN-events", restored.Count.ToString(), "virtual events after reopen");

                foreach (var ev in restored)
                {
                    LevelStorage.TryGetData(ev, out var d);
                    Row("REOPEN-instance",
                        $"{d?.Id} uid={d?.Uid}",
                        $"floor={ev.floor} strength={ev.GetFloat("strength")} duration={ev.GetFloat("duration")}");
                }

                var leftOverComments = fresh.levelEvents
                    .Count(e => e.eventType == LevelEventType.EditorComment &&
                                EventInstanceData.LooksLikeOurs(e.GetString("comment")));
                Row("REOPEN-leftover", leftOverComments == 0 ? "PASS" : "FAIL",
                    $"{leftOverComments} payload comments left unconsumed");
            }
            catch (Exception e)
            {
                Row("REOPEN", "EXCEPTION", e.ToString());
            }

            Row("DONE", "ok", "p2 harness finished");
        }

        private static bool CopyTo(scnEditor editor, LevelEvent source, int floor)
        {
            try
            {
                var target = editor.floors.FirstOrDefault(f => f.seqID == floor);
                if (target == null) return false;

                // Drive the same path the editor uses for copy/paste.
                var method = AccessTools.Method(typeof(scnEditor), "PasteEvents");
                if (method == null) return false;

                var floorData = new scnEditor.FloorData('S', 0f,
                    new List<LevelEvent> { source },
                    new List<LevelEvent>());
                method.Invoke(editor, new object[] { target, floorData, false, false, false, true });
                return true;
            }
            catch (Exception e)
            {
                Row("COPY", "EXCEPTION", e.ToString());
                return false;
            }
        }

        private static void SelectFloor(scnEditor editor, int floor)
        {
            var f = editor.floors.FirstOrDefault(x => x.seqID == floor);
            if (f == null) return;
            editor.selectedFloors.Clear();
            editor.selectedFloors.Add(f);
        }

        private static int PickFreeFloor(scnEditor editor, int preferred)
        {
            int max = editor.floors.Count - 1;
            for (int i = preferred; i <= max; i++)
            {
                if (!editor.events.Any(e => e.floor == i && LevelStorage.TryGetData(e, out _)))
                    return i;
            }
            return -1;
        }

        private static LevelEventType? FindType(string id)
        {
            var m = EventRegistry.All.FirstOrDefault(x => x.Id == id);
            if (m == null) return null;
            return (LevelEventType)PEV.Util.StableId.FromEventId(m.Id);
        }

        private static LevelEvent FindOnFloor(scnEditor editor, int floor, LevelEventType type)
        {
            return editor.events.LastOrDefault(e => e.floor == floor && e.eventType == type);
        }

        private static string DescribeEvents(scnEditor editor)
        {
            var ours = editor.events.Where(e => LevelStorage.TryGetData(e, out _)).ToList();
            return $"{ours.Count} attached of {editor.events.Count} total";
        }

        private static string DescribeOne(LevelEvent ev)
        {
            if (ev == null) return "null";
            if (!LevelStorage.TryGetData(ev, out var data)) return "no data attached";

            // Read from the live event, not the cached data: the point of this probe is to
            // show what the editor currently holds.
            float strength = ev.GetFloat("strength");
            float duration = ev.GetFloat("duration");
            return $"{data.Id} uid={data.Uid} strength={strength} duration={duration}";
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            return d != null && d.TryGetValue(key, out var v) ? v as string : null;
        }
    }
}
