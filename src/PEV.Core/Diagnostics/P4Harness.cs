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
    /// P4 harness: prove that a continuously animated event is actually driven per frame.
    ///
    /// A one-shot effect only needs "did it fire". A continuous one needs evidence that the
    /// host applies new values every frame and that the values follow the script's formula,
    /// so the checks sample the live object over time instead of looking for a single call.
    /// Inert unless "p4-test.txt" exists next to the DLL.
    /// </summary>
    public static class P4Harness
    {
        public static string LevelPath;
        public static bool Enabled;

        private static string _resultPath;
        private static bool _ran;

        public static void Configure(string modDir)
        {
            Enabled = File.Exists(Path.Combine(modDir, "p4-test.txt"));
            if (!Enabled) return;

            _resultPath = Path.Combine(modDir, "p4-results.txt");
            LevelPath = ReadOr(Path.Combine(modDir, "p4-level.txt"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "A Dance of Fire and Ice", "Levels", "P4Test.adofai"));

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
            Debug.Log("[PEV-P4] " + line);
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
            yield return new WaitForSeconds(2f);

            // Log what each instance actually hands to create(ctx, params). Reading the
            // cached snapshot tells us nothing about what the script sees.
            ScriptHost.LogInit = true;

            var pulse = FindType("pev.pulse");
            if (pulse == null)
            {
                Row("SETUP", "FAIL", "pev.pulse is not registered");
                yield break;
            }

            // Place two pulses on different floors: they must animate independently.
            var floors = PickFloors(editor, 2);
            if (floors.Count < 2) { Row("SETUP", "FAIL", "not enough free floors"); yield break; }

            foreach (int f in floors)
            {
                EditorTestHelpers.SelectFloor(editor, f);
                editor.AddEventAtSelected(pulse.Value);
                yield return null;
            }
            Row("PLACED", floors.Count.ToString(), "floors " + string.Join(", ", floors));

            // Give each instance distinct parameters so their animations differ.
            var ours = editor.events.Where(e => LevelStorage.TryGetData(e, out _)).ToList();
            foreach (var ev in ours)
            {
                LevelStorage.TryGetData(ev, out var d);
                if (d.Id != "pev.pulse") continue;

                int idx = floors.IndexOf(ev.floor);
                ev["duration"] = 3f;
                ev["cycles"] = idx == 0 ? 2f : 5f;
                ev["size"] = 0.3f;
                ev["drift"] = idx == 0 ? 0.2f : 0.6f;

                // The script reads Data.Effective, not the LevelEvent's live values. Show
                // what will actually reach create(ctx, params).
                string effective = string.Join(", ",
                    d.Effective.Select(kv => $"{kv.Key}={kv.Value}({kv.Value?.GetType().Name})"));
                Row("PARAMS", $"floor{ev.floor}", effective.Length > 0 ? effective : "<empty>");
            }

            Row("LIVE-before-play", ContinuousEffects.LiveCount.ToString(), "effects alive before playback");

            editor.Play();

            // Instances start when the song clock reaches their tile, which is after the
            // countdown. Poll for the objects instead of guessing a fixed delay.
            bool aOk = false, bOk = false;
            for (int i = 0; i < 40 && !(aOk && bOk); i++)
            {
                yield return new WaitForSeconds(0.1f);
                aOk = ContinuousEffects.TryGetStats(KeyForFloor(floors[0]), out _, out _, out _, out _);
                bOk = ContinuousEffects.TryGetStats(KeyForFloor(floors[1]), out _, out _, out _, out _);
            }

            ContinuousEffects.TryGetStats(KeyForFloor(floors[0]), out int calls0, out float sa, out _, out var pa);
            ContinuousEffects.TryGetStats(KeyForFloor(floors[1]), out int calls1, out float sb, out _, out var pb);

            Row("LIVE-during-play", ContinuousEffects.LiveCount.ToString(), "effects alive while playing");
            Row("INSTANCES-live", (aOk && bOk) ? "PASS" : "FAIL",
                $"both objects present: floor{floors[0]}={aOk}, floor{floors[1]}={bOk}");

            if (aOk && bOk)
            {
                bool differ = Math.Abs(pa.x - pb.x) > 1e-3 || Math.Abs(sa - sb) > 1e-3;
                Row("INSTANCES-independent", differ ? "PASS" : "FAIL",
                    $"scale {sa:F3} vs {sb:F3}, x {pa.x:F2} vs {pb.x:F2}");
            }
            else
            {
                Row("INSTANCES-independent", "SKIP", "objects did not appear");
            }

            // Sample over time. A continuous effect should produce a new value every frame,
            // not once at trigger time.
            var samples = new List<string>();
            for (int i = 0; i < 20; i++)
            {
                yield return new WaitForSeconds(0.1f);

                if (ContinuousEffects.TryGetStats(KeyForFloor(floors[0]), out int c, out float scale, out float alpha, out var pos))
                {
                    if (i % 5 == 0)
                        samples.Add($"t{i}: calls={c} scale={scale:F3} alpha={alpha:F3} x={pos.x:F2}");
                }
            }

            foreach (var s in samples) Row("SAMPLE", s, "");

            // Per-frame driving: the call count should be roughly the frame count, not zero
            // and not one. This is the check that distinguishes "animated" from "triggered".
            if (aOk && ContinuousEffects.TryGetStats(KeyForFloor(floors[0]), out int totalCalls, out _, out _, out _))
            {
                Row("PER-FRAME", totalCalls > 30 ? "PASS" : "FAIL",
                    $"fx.set calls on instance 0 = {totalCalls} (expect tens, not 1)");
            }

            // The instance's own update() must have run as well.
            var armed = GetAllInstances();
            var pulseInst = armed.FirstOrDefault(i => i.Manifest != null && i.Manifest.Id == "pev.pulse");
            if (pulseInst?.Host != null)
            {
                Row("UPDATE-CALLS", pulseInst.Host.UpdateCalls.ToString(),
                    $"script update() invocations, faulted={pulseInst.Host.Faulted}");
            }

            // After the event's lifetime the script must have destroyed its object.
            yield return new WaitForSeconds(4f);
            int aliveAfter = ContinuousEffects.LiveCount;
            Row("CLEANUP", aliveAfter == 0 ? "PASS" : "CHECK",
                $"effects alive after lifetime={aliveAfter} (script should destroy them)");

            Row("DONE", "ok", "p4 harness finished");
        }

        private static string KeyForFloor(int floor) => "pulse:" + floor;

        private static List<int> PickFloors(scnEditor editor, int count)
        {
            var result = new List<int>();
            int max = editor.floors.Count - 1;
            for (int i = 2; i <= max && result.Count < count; i += 3)
            {
                if (!editor.events.Any(e => e.floor == i && LevelStorage.TryGetData(e, out _)))
                    result.Add(i);
            }
            return result;
        }

        private static LevelEventType? FindType(string id)
        {
            var m = EventRegistry.All.FirstOrDefault(x => x.Id == id);
            if (m == null) return null;
            return (LevelEventType)PEV.Util.StableId.FromEventId(m.Id);
        }

        private static List<EventInstance> GetAllInstances()
        {
            var host = RuntimeHost.Ensure();
            var field = typeof(RuntimeHost).GetField("_instances",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (field?.GetValue(host) as List<EventInstance>) ?? new List<EventInstance>();
        }
    }
}
