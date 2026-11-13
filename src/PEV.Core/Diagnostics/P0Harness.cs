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

namespace PEV.Diagnostics
{
    /// <summary>
    /// Automated harness for P0. Completely inert unless "p0-test.txt" exists next to
    /// the DLL, so it ships harmlessly in normal play.
    /// </summary>
    public static class P0Harness
    {
        public static string LevelPath;
        public static string ExportPath;
        public static bool Enabled;

        private static string _resultPath;

        public static void Configure(string modDir)
        {
            Enabled = File.Exists(Path.Combine(modDir, "p0-test.txt"));
            if (!Enabled) return;

            _resultPath = Path.Combine(modDir, "p0-results.txt");
            LevelPath = ReadOr(Path.Combine(modDir, "p0-level.txt"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "A Dance of Fire and Ice", "Levels", "P0Test.adofai"));
            ExportPath = Path.Combine(modDir, "p0-export.adofai");

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
            UnityEngine.Debug.Log("[PEV-P0] " + line);
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
                if (!Enabled) return;
                ((MonoBehaviour)__instance).StartCoroutine(Run(__instance));
            }

            private static IEnumerator Run(scnEditor editor)
            {
                yield return new WaitForSeconds(3f);

                try
                {
                    Row("EDITOR", "ready", $"floors={editor.floors.Count}, events={editor.events.Count}");

                    var virtuals = editor.events.Where(e => LevelStorage.TryGetData(e, out _)).ToList();
                    Row("RESTORED", virtuals.Count.ToString(), "virtual events in editor");

                    foreach (var ev in virtuals)
                    {
                        LevelStorage.TryGetData(ev, out var data);
                        string summary = string.Join(", ",
                            data.Effective.Select(kv => $"{kv.Key}={kv.Value}"));
                        Row("INSTANCE", data.Id,
                            $"floor={ev.floor} uid={data.Uid} v={data.Version} :: {summary}");
                    }

                    // Independence: same event id placed twice must not share state.
                    var byId = virtuals.GroupBy(e => { LevelStorage.TryGetData(e, out var d); return d.Id; });
                    foreach (var g in byId)
                    {
                        var strengths = g.Select(e =>
                        {
                            LevelStorage.TryGetData(e, out var d);
                            return d.GetFloat("strength", float.NaN);
                        }).ToList();
                        if (strengths.Count >= 2)
                        {
                            bool distinct = strengths.Distinct().Count() > 1;
                            Row("INDEPENDENCE", distinct ? "PASS" : "FAIL",
                                $"{g.Key}: {string.Join(" vs ", strengths)}");
                        }
                    }

                    // Parameter types the game actually built controls for.
                    foreach (var m in EventRegistry.All)
                    {
                        int typeId = PEV.Util.StableId.FromEventId(m.Id);
                        if (EventRegistry.TryGetInfo((LevelEventType)typeId, out var info))
                        {
                            var built = string.Join(", ",
                                info.propertiesInfo.Select(kv => $"{kv.Key}:{kv.Value.type}"));
                            Row("SCHEMA", m.Id, built);
                        }
                    }

                    // Re-encode and write out, so the round trip can be inspected offline.
                    var dict = editor.levelData.EncodeToDictionary();
                    var actions = dict["actions"] as List<Dictionary<string, object>>;
                    int pev = actions.Count(a =>
                        a.TryGetValue("eventType", out var t) && (t as string) == "EditorComment" &&
                        a.TryGetValue("comment", out var c) && EventInstanceData.LooksLikeOurs(c as string));
                    int unknown = actions.Count(a =>
                        a.TryGetValue("eventType", out var t) &&
                        t is string s && !Enum.IsDefined(typeof(LevelEventType), s) &&
                        !EventRegistry.All.Any(m => ((int)PEV.Util.StableId.FromEventId(m.Id)).ToString() == s));

                    Row("ENCODE", "info", $"actions={actions.Count}, pevComments={pev}, unknownTypes={unknown}");
                    File.WriteAllText(ExportPath, GDMiniJSON.Json.Serialize(dict));
                    Row("EXPORT", "ok", ExportPath);
                }
                catch (Exception e)
                {
                    Row("EDITOR", "EXCEPTION", e.ToString());
                }

                Row("DONE", "ok", "p0 harness finished");
            }
        }
    }
}
