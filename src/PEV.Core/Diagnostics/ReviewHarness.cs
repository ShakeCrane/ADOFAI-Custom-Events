using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using ADOFAI;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PEV.Loading;
using PEV.Model;
using PEV.Registry;
using PEV.Scripting;
using PEV.Storage;
using UnityEngine;

namespace PEV.Diagnostics
{
    // Regression checks for data loss, sandbox isolation and asynchronous texture ownership.
    // Like the stage harnesses, this is inert unless its opt-in file exists.
    public static class ReviewHarness
    {
        private static string _results;
        private static bool _ran;

        public static void Configure(string modDir)
        {
            if (!File.Exists(Path.Combine(modDir, "review-test.txt"))) return;
            _results = Path.Combine(modDir, "review-results.txt");
            File.WriteAllText(_results, "");
        }

        private static void Check(string name, Func<bool> test)
        {
            try { Row(name, test() ? "PASS" : "FAIL"); }
            catch (Exception e) { Row(name, "FAIL " + e); }
        }

        private static void Row(string name, string result) =>
            File.AppendAllText(_results, name + "\t" + result + Environment.NewLine);

        [HarmonyPatch(typeof(scnLevelSelect), "Start")]
        public static class AutoOpen
        {
            public static void Postfix()
            {
                if (_results == null || _ran) return;
                scnEditor.levelToOpenOnLoad = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "A Dance of Fire and Ice", "Levels", "P3Test.adofai");
                ADOBase.loader.LoadScene("scnEditor");
            }
        }

        [HarmonyPatch(typeof(scnEditor), "Start")]
        public static class Probe
        {
            public static void Postfix(scnEditor __instance)
            {
                if (_results == null || _ran) return;
                _ran = true;
                __instance.StartCoroutine(Run(__instance));
            }
        }

        private static IEnumerator Run(scnEditor editor)
        {
            yield return new WaitForSeconds(2f);
            RuntimeHost.Ensure().Teardown();
            CheckPayloads();
            CheckStorage(editor);
            CheckScripts();
            CheckKeys();
            yield return CheckTextures();
            yield return CheckFields();
            Row("DONE", "ok");
        }

        private static void CheckPayloads()
        {
            Check("PARSE-invalid-fields", () => EventInstanceData.Parse("@PEV1 {\"id\":{},\"p\":{}}", out _) == null);
            Check("PARSE-invalid-params", () => EventInstanceData.Parse("@PEV1 {\"id\":\"test\",\"p\":[]}", out _) == null);
            Check("RAW-unknown-shapes", () =>
            {
                var d = EventInstanceData.Parse("@PEV1 {\"id\":\"test\",\"p\":{\"n\":9223372036854775807,\"shape\":{\"x\":1,\"y\":2}}}", out _);
                d.Materialize(null);
                var copy = EventInstanceData.Parse(d.ToComment(), out _);
                return JToken.DeepEquals(d.Raw["shape"], copy.Raw["shape"]) && d.GetString("n") == "9223372036854775807";
            });
            Check("MANIFEST-invalid-fields", () => EventManifest.Parse("{\"id\":\"test\",\"schemaVersion\":{}}", "", out _) == null);
            Check("MANIFEST-api-version", () => EventManifest.Parse("{\"id\":\"test\",\"apiVersion\":2}", "", out _) == null);
            Check("MANIFEST-duplicate-param", () => EventManifest.Parse("{\"id\":\"test\",\"params\":[{\"name\":\"a\",\"type\":\"Float\"},{\"name\":\"a\",\"type\":\"Int\"}]}", "", out _) == null);
            Check("RESOURCE-package-boundary", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "pev-review-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    File.WriteAllText(Path.Combine(root, "main.js"), "test");
                    string zip = Path.Combine(root, "test.event");
                    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                    using (var writer = new StreamWriter(archive.CreateEntry("main.js").Open())) writer.Write("test");
                    return EventLoader.ReadResource(root, "../outside") == null &&
                           EventLoader.ReadResource(root, Path.Combine(root, "main.js")) == null &&
                           EventLoader.ReadResource(root, "main.js").Length == 4 &&
                           EventLoader.ReadResource(zip, "main.js").Length == 4;
                }
                finally { Directory.Delete(root, true); }
            });
        }

        private static void CheckStorage(scnEditor editor)
        {
            Check("DELETE-undo-binding", () =>
            {
                var type = (LevelEventType)PEV.Util.StableId.FromEventId("pev.camera-warp");
                var ev = new LevelEvent(1, type);
                var data = new EventInstanceData { Id = "pev.camera-warp" };
                LevelStorage.Attach(ev, data);
                var snapshot = ev.Copy();
                LevelStorage.Detach(ev);
                return !LevelStorage.TryGetData(ev, out _) && LevelStorage.TryGetData(snapshot, out var restored) && ReferenceEquals(data, restored);
            });
            Check("DECORATION-copy-identity", () =>
            {
                var row = new LevelEvent(1, LevelEventType.AddDecoration);
                var data = new EventInstanceData { Id = "pev.noise-field" };
                data.Set("fillOpacity", 0.4f);
                LevelStorage.Attach(row, data);
                var copy = row.Copy();
                editor.levelData.decorations.Add(row);
                editor.levelData.decorations.Add(copy);
                try
                {
                    PEV.Editor.DecorationPlacement.PasteDecorationsPatch.Postfix();
                    LevelStorage.TryGetData(row, out var a);
                    LevelStorage.TryGetData(copy, out var b);
                    return !ReferenceEquals(a, b) && a.Uid != b.Uid;
                }
                finally { editor.levelData.decorations.Remove(row); editor.levelData.decorations.Remove(copy); }
            });
            Check("MIGRATE-legacy-decoration", () =>
            {
                var level = new LevelData();
                var comment = new LevelEvent(1, LevelEventType.EditorComment);
                comment["comment"] = "@PEV1 {\"id\":\"pev.noise-field\",\"p\":{\"size\":[8,6],\"opacity\":0,\"tag\":\"\"}}";
                level.levelEvents.Add(comment);
                var summary = LevelStorage.Restore(level);
                return summary.Migrated == 1 && level.levelEvents.Count == 0 && level.decorations.Count == 1 &&
                       level.decorations[0].GetFloat("opacity") == 0 &&
                       level.decorations[0].Get<Vector2>("scale") == new Vector2(200, 200) &&
                       !string.IsNullOrEmpty(level.decorations[0].GetString("tag"));
            });
            Check("MIGRATE-failure-keeps-comment", () =>
            {
                DecorationParams.TryGetNativeInfo(out var native);
                var removed = GCS.levelEventsInfo.Where(kv => ReferenceEquals(kv.Value, native)).ToList();
                foreach (var kv in removed) GCS.levelEventsInfo.Remove(kv.Key);
                try
                {
                    var level = new LevelData();
                    var comment = new LevelEvent(1, LevelEventType.EditorComment);
                    comment["comment"] = "@PEV1 {\"id\":\"pev.noise-field\"}";
                    level.levelEvents.Add(comment);
                    LevelStorage.Restore(level);
                    return level.levelEvents.Contains(comment) && level.decorations.Count == 0;
                }
                finally { foreach (var kv in removed) GCS.levelEventsInfo[kv.Key] = kv.Value; }
            });
            Check("PLACEHOLDER-failure-keeps-comment", () =>
            {
                var infos = (Dictionary<LevelEventType, LevelEventInfo>)typeof(EventRegistry)
                    .GetField("InfoByType", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                var type = (LevelEventType)PEV.Util.StableId.FromEventId(PlaceholderEvent.Id);
                var info = infos[type];
                infos.Remove(type);
                try
                {
                    var level = new LevelData();
                    var ev = new LevelEvent(1, LevelEventType.EditorComment);
                    ev["comment"] = "@PEV1 malformed";
                    level.levelEvents.Add(ev);
                    LevelStorage.Restore(level);
                    return level.levelEvents.Contains(ev);
                }
                finally { infos[type] = info; }
            });
            Check("ENCODE-failure-is-atomic", () =>
            {
                var level = new LevelData();
                var type = (LevelEventType)PEV.Util.StableId.FromEventId("pev.camera-warp");
                var ev = new LevelEvent(1, type);
                LevelStorage.Attach(ev, new EventInstanceData { Id = "pev.camera-warp", Raw = null });
                level.levelEvents.Add(ev);
                var original = new Dictionary<string, object> { { "eventType", ((int)type).ToString() } };
                var actions = new List<Dictionary<string, object>> { original };
                try { LevelStorage.Bake(level, actions); return false; }
                catch { return actions.Count == 1 && ReferenceEquals(actions[0], original); }
            });
            Check("ENCODE-preserves-editor-flags", () =>
            {
                var level = new LevelData();
                var ev = new LevelEvent(1, (LevelEventType)PEV.Util.StableId.FromEventId("pev.camera-warp")) { visible = false, locked = true };
                LevelStorage.Attach(ev, new EventInstanceData { Id = "pev.camera-warp" });
                level.levelEvents.Add(ev);
                var actions = new List<Dictionary<string, object>>();
                LevelStorage.Bake(level, actions);
                return actions.Count == 1 && (bool)actions[0]["locked"] && !(bool)actions[0]["visible"];
            });
        }

        private static ScriptHost Host(string source, string scope)
        {
            var host = ScriptHost.Create(source, scope, out var error);
            if (host == null) throw new Exception(error);
            if (!host.Initialize(new EventContext(), new Dictionary<string, object>(), out error)) throw new Exception(error);
            return host;
        }

        private static void CheckScripts()
        {
            Check("SCRIPT-statement-budget", () =>
            {
                using (var host = Host("function create(){return {update:function(){while(true){}}};}", "review/loop"))
                {
                    host.Update(0, "{}");
                    return host.Faulted && host.LastError.Contains("statement budget");
                }
            });
            Check("SCRIPT-context-refresh", () =>
            {
                using (var host = Host("function create(ctx){return {update:function(t){if(ctx.t!==t || ctx.bpm!==180)throw Error('stale context');}};}", "review/context"))
                {
                    host.Update(3, "{\"t\":3,\"bpm\":180}");
                    host.Update(4, "{\"t\":4,\"bpm\":180}");
                    return !host.Faulted && host.UpdateCalls == 2;
                }
            });
            Check("SCRIPT-dispose-this", () =>
            {
                var host = Host("var disposed=false;function create(){return {marker:7,dispose:function(){disposed=this.marker===7;}};}", "review/dispose");
                var engine = (Jint.Engine)typeof(ScriptHost).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(host);
                host.Dispose();
                return Convert.ToBoolean(engine.GetValue("disposed").ToObject());
            });
            Check("SCRIPT-scoped-effects", () =>
            {
                const string source = "function create(){fx.create('same',{color:'ff0000',scale:2});return {}; }";
                using (var a = Host(source, "review/a"))
                using (var b = Host(source, "review/b"))
                {
                    bool both = ContinuousEffects.Exists(GameBridge.ScopedKey("review/a", "same")) &&
                                ContinuousEffects.Exists(GameBridge.ScopedKey("review/b", "same"));
                    a.Dispose();
                    return both && !ContinuousEffects.Exists(GameBridge.ScopedKey("review/a", "same")) &&
                           ContinuousEffects.Exists(GameBridge.ScopedKey("review/b", "same"));
                }
            });
            Check("SCRIPT-init-failure-cleans", () =>
            {
                var host = ScriptHost.Create("function create(){fx.create('leak',{});throw Error('init');}", "review/fail", out _);
                bool failed = !host.Initialize(new EventContext(), new Dictionary<string, object>(), out _);
                return failed && !ContinuousEffects.Exists(GameBridge.ScopedKey("review/fail", "leak"));
            });
            Check("FX-partial-update", () =>
            {
                ContinuousEffects.Create("review/partial", new Dictionary<string, object> { { "scale", 2f }, { "alpha", 0.3f }, { "x", 0.2f } }, out _);
                ContinuousEffects.TryGetStats("review/partial", out _, out _, out _, out var before);
                ContinuousEffects.Set("review/partial", new Dictionary<string, object> { { "alpha", 0.5f } }, out _);
                ContinuousEffects.TryGetStats("review/partial", out _, out float scale, out float alpha, out var after);
                ContinuousEffects.Destroy("review/partial", out _);
                return scale == 2 && alpha == 0.5f && before == after;
            });
        }

        private static NoiseTextureFactory.Request Request() => new NoiseTextureFactory.Request
        {
            WidthWorld = 2, HeightWorld = 2, FrameCount = 2, TargetPixelWidth = 192,
            FillOpacity = 0.6f, EdgeOpacity = 0.8f, GlowOpacity = 0.8f,
            EdgeColor = Color.red, SampleRate = 1f, ScreenPixelWidth = 192
        };

        private static void CheckKeys()
        {
            var req = Request();
            string key = NoiseTextureFactory.Key(req);
            Check("CACHE-glow-opacity", () => { var r = req; r.GlowOpacity = 0.4f; return NoiseTextureFactory.Key(r) != key; });
            Check("CACHE-scene-colour", () => { var r = req; r.SceneColor = Color.white; return NoiseTextureFactory.Key(r) != key; });
            Check("CACHE-screen-density", () => { var r = req; r.ScreenPixelWidth = 384; return NoiseTextureFactory.Key(r) != key; });
        }

        private static IEnumerator CheckTextures()
        {
            var req = Request();
            var frames = NoiseTextureFactory.Begin(req, out _);
            NoiseTextureFactory.Release(frames); // cancel while the worker may still be starting
            var replacement = NoiseTextureFactory.Begin(req, out _);
            NoiseTextureFactory.Release(frames); // stale release must not evict the replacement
            Check("CACHE-stale-release", () => NoiseTextureFactory.CachedFrameSets == 1);
            float deadline = Time.realtimeSinceStartup + 20f;
            while (!replacement.Ready && replacement.Error == null && Time.realtimeSinceStartup < deadline)
            {
                NoiseTextureFactory.Pump(2f);
                yield return null;
            }
            Check("BAKE-worker-completes", () => replacement.Ready && replacement.Textures.All(t => t != null));
            NoiseTextureFactory.Release(replacement);
            yield return null;
            Check("BAKE-cleanup", () => NoiseTextureFactory.CachedFrameSets == 0);
        }

        private static IEnumerator CheckFields()
        {
            var args = new Dictionary<string, object>
            {
                { "__instance", "review/field" }, { "relativeTo", "Camera" },
                { "size", new[] { 2f, 2f } }, { "frameCount", 2 }, { "frame", 0 }, { "depth", 123 }
            };
            NoiseFieldRenderer.Set("field", args, out _);
            args["depth"] = 124;
            NoiseFieldRenderer.Set("field", args, out _);
            Check("FIELD-depth-change", () => NoiseFieldRenderer.LayerCount == 1);

            float deadline = Time.realtimeSinceStartup + 20f;
            while (NoiseFieldRenderer.AllEntries()[0].GameObject == null && Time.realtimeSinceStartup < deadline)
            {
                NoiseTextureFactory.Pump(2f);
                NoiseFieldRenderer.Set("field", args, out _);
                yield return null;
            }
            var go = NoiseFieldRenderer.AllEntries()[0].GameObject;
            if (go != null)
            {
                var sr = go.GetComponent<SpriteRenderer>();
                var old = sr.sprite;
                args["exposure"] = 0.75f;
                NoiseFieldRenderer.Set("field", args, out _);
                yield return null; // Unity's deferred destruction would invalidate the old sprite here.
                Check("FIELD-retains-old-texture", () => old != null && sr.sprite == old && old.texture != null);
                deadline = Time.realtimeSinceStartup + 20f;
                while (sr.sprite == old && Time.realtimeSinceStartup < deadline)
                {
                    NoiseTextureFactory.Pump(2f);
                    NoiseFieldRenderer.Set("field", args, out _);
                    yield return null;
                }
                Check("FIELD-swaps-ready-texture", () => sr.sprite != null && sr.sprite != old);
            }
            else Row("FIELD-first-texture", "FAIL timeout");
            NoiseFieldRenderer.DestroyAll();

            args["size"] = new[] { 4f, 4f };
            foreach (var pair in new[] { new[] { "a", "0" }, new[] { "b", "6" }, new[] { "c", "3" } })
            {
                args["__instance"] = "review/cluster-" + pair[0];
                args["position"] = new[] { float.Parse(pair[1]), 0f };
                NoiseFieldRenderer.Set("field", args, out _);
            }
            Check("FIELD-transitive-union", () => NoiseFieldRenderer.LayerCount == 1);
            args["visible"] = false;
            NoiseFieldRenderer.Set("field", args, out _); // hide the bridge between the two disjoint fields
            Check("FIELD-hidden-member", () => NoiseFieldRenderer.LayerCount == 2);
            NoiseFieldRenderer.DestroyAll();
            yield return null;
            Check("FIELD-cleanup", () => NoiseTextureFactory.CachedFrameSets == 0 && NoiseFieldRenderer.LiveCount == 0);
        }
    }
}
