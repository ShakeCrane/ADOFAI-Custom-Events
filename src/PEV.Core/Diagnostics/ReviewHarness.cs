using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using ADOFAI;
using HarmonyLib;
using Jint;
using Newtonsoft.Json.Linq;
using PEV.Editor;
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
        private const string StateEventId = "pev.noise-field-state";
        private static string _results;
        private static bool _ran;
        private sealed class DisposableProbe : IDisposable
        {
            internal int Count;
            public void Dispose() { Count++; }
        }
        private struct PlainSnapshotProbe
        {
            public int Number;
            public Vector2 Position;
            public int HiddenProperty => 42;
        }
        private struct UnsafeSnapshotProbe { public GameObject SceneObject; }
        private struct GenericFrameProbe { public int Width, Height; public float Value; }
        private struct MutableRecordProbe { public int[] Values; }
        private struct GroupProgramProbe { public float Time, Aspect; public int Capacity, ViewWidth, ViewHeight; public string DefaultColor; public ViewportPoint[] Points; }
        private const string GenericVectorSource = @"function computeVector(i){
function run(x){return Number(__compute(JSON.stringify(x)));}
var p=run({op:'pixelFill',width:i.width,height:i.height,value:i.request.Items[0].Value+i.request.Options/100});
return [run({op:'pixelPack',channels:[p,p,p,p]})];}";

        private static bool CheckPlainSnapshot()
        {
            string json = PublicFieldsSnapshot.Serialize(new PlainSnapshotProbe { Number = 3, Position = new Vector2(2, 4) });
            var data = JObject.Parse(json);
            if (data.Value<int>("Number") != 3 || data["Position"]?.Value<float>("x") != 2 || data["HiddenProperty"] != null) return false;
            try { PublicFieldsSnapshot.Serialize(new UnsafeSnapshotProbe { SceneObject = null }); return false; }
            catch (Exception) { }
            try { PublicFieldsSnapshot.Serialize<GameObject>(null); return false; }
            catch (Exception) { }
            return true;
        }

        private static bool CheckResourceStaging()
        {
            var first = new DisposableProbe(); var obsolete = new DisposableProbe(); var replacement = new DisposableProbe();
            var staging = new ResourceGroupStaging<int, DisposableProbe>();
            var keep = new Dictionary<int, bool> { [1] = true };
            staging.SetPending(1, first); staging.SetPending(2, obsolete);
            staging.Commit(keep);
            if (obsolete.Count != 1 || first.Count != 0 || staging.Active.Count != 1 || !staging.Active.ContainsKey(1)) return false;
            staging.SetPending(1, replacement); staging.Commit(keep);
            if (first.Count != 1 || replacement.Count != 0 || staging.Active[1] != replacement) return false;
            staging.Dispose();
            return replacement.Count == 1 && staging.Active.Count == 0 && staging.Pending.Count == 0 && !staging.Committed;
        }

        private static bool CheckGpuRecordLayouts()
        {
            var field = typeof(GpuRecordBuffer).GetField("Types", BindingFlags.NonPublic | BindingFlags.Static);
            var types = (Type[])field.GetValue(null);
            if (types.Length != 64) return false;
            for (int i = 0; i < types.Length; i++)
                if (System.Runtime.InteropServices.Marshal.SizeOf(types[i]) != (i + 1) * 4) return false;
            var buffer = new GpuRecordBuffer(16);
            buffer.Prepare(2); buffer.SetFloat(0, 0, -1.25f); buffer.SetUInt(1, 3, uint.MaxValue);
            var data = (byte[])typeof(GpuRecordBuffer).GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(buffer);
            if (BitConverter.ToSingle(data, 0) != -1.25f || BitConverter.ToUInt32(data, 28) != uint.MaxValue) return false;
            try { buffer.SetFloat(0, 0, float.NaN); return false; } catch (ArgumentException) { }
            try { buffer.SetUInt(2, 0, 0); return false; } catch (ArgumentOutOfRangeException) { }
            try { buffer.Prepare(65537); return false; } catch (ArgumentOutOfRangeException) { }
            try { new GpuRecordBuffer(3); return false; } catch (ArgumentOutOfRangeException) { }
            return true;
        }

        private static bool CheckRectangleClusters()
        {
            var items = new[] { new Rect(0, 0, 1, 1), new Rect(4, 0, 1, 1), new Rect(2, 0, 1, 1),
                new Rect(.5f, 0, 4, 1), new Rect(20, 0, 1, 1) };
            var groups = RectangleClusters.Group(items, item => item);
            return groups.Count == 2 && groups[0].Count == 4 && groups[1].Count == 1 && groups[1][0] == items[4];
        }

        private static bool CheckStickyQuantization()
        {
            if (StickyQuantization.Update(22, 0, 10, 1, 5, 12) != 20 ||
                StickyQuantization.Update(29, 20, 10, 1, 5, 12) != 20 ||
                StickyQuantization.Update(32, 20, 10, 1, 5, 12) != 30 ||
                StickyQuantization.Update(-5, 0, 10, 1, 5, 12) != 10 ||
                StickyQuantization.Update(999, 0, 10, 1, 5, 12) != 50) return false;
            try { StickyQuantization.Update(2, 0, float.NaN, 1, 5, 1); return false; }
            catch (ArgumentException) { }
            return true;
        }

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

        private static void Row(string name, string result, string note = null) =>
            File.AppendAllText(_results, name + "\t" + result + (note == null ? "" : "\t" + note) + Environment.NewLine);

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
            Check("PIXEL-kernel-operations", CheckPixelKernel);
            Check("PIXEL-kernel-limits", CheckPixelKernelLimits);
            Check("PIXEL-worker-limits-and-reuse", CheckPixelWorkerLimits);
            Check("RESOURCE-group-staging", CheckResourceStaging);
            Check("SANDBOX-plain-data-snapshot", CheckPlainSnapshot);
            Check("GPU-record-layouts-and-limits", CheckGpuRecordLayouts);
            Check("GEOMETRY-rectangle-cluster-bridges", CheckRectangleClusters);
            Check("RESOURCE-sticky-quantization", CheckStickyQuantization);
            Check("PIXEL-package-resource-limits", CheckPackagePixelResourceLimits);
            Check("PIXEL-kernel-mono-performance", CheckPixelKernelPerformance);
            CheckKeys();
            yield return CheckPackagePlanScaleSafe();
            yield return CheckGenericFrameProgramSafe();
            yield return CheckGenericStructuredWorkerSafe();
            yield return CheckGenericVectorTexturesSafe();
            yield return CheckGenericBakedFrameCacheSafe();
            yield return CheckTextures();
            yield return CheckFieldsWithoutGpuDriver();
            string vectorProbe = Environment.GetEnvironmentVariable("PEV_TEST_VECTOR_SCRIPT");
            if (!string.IsNullOrEmpty(vectorProbe))
            {
                CheckGenericVectorProbe(vectorProbe);
                yield return CheckVectorWorker();
                yield return CheckPackagedVectorWorker();
                yield return CheckPackagePixelResources();
                yield return CheckCpuCoverageProgram();
                yield return CheckCpuGlitchProgram();
                yield return CheckCpuBakePerformance();
                yield return CheckCpuBakeCancellation();
                yield return CheckCpuBakePlanCancellation();
                if (Environment.GetEnvironmentVariable("PEV_TEST_BIG_CPU") == "1") yield return CheckCpuBakeLarge();
            }
            if (Environment.GetEnvironmentVariable("PEV_TEST_REAL_FIELD_BUNDLE") == "1")
                yield return CheckRealFieldBundle();
            yield return ProbeGlobalSettingsGui();
            CheckEditorPlacement(editor);
            Row("DONE", "ok");
        }

        private static IEnumerator CheckGenericStructuredWorkerSafe()
        {
            var probe = CheckGenericStructuredWorker();
            while (true)
            {
                bool next;
                try { next = probe.MoveNext(); }
                catch (Exception e) { Row("VECTOR-structured-generic", "FAIL", e.ToString()); yield break; }
                if (!next) yield break;
                yield return probe.Current;
            }
        }

        private static IEnumerator CheckGenericStructuredWorker()
        {
            string source = GenericVectorSource;
            var mutable = new MutableRecordProbe { Values = Array.Empty<int>() };
            bool rejected = false;
            try { using (new StructuredVectorWorker<MutableRecordProbe, int>(source, 1)) { } }
            catch (ArgumentException) { rejected = true; }
            if (!rejected || mutable.Values.Length != 0) { Row("VECTOR-structured-generic", "FAIL", "mutable record accepted"); yield break; }
            using (var worker = new StructuredVectorWorker<GenericFrameProbe, int>(source, 1))
            {
                var items = new[] { new GenericFrameProbe { Width = 4, Height = 3, Value = .25f } };
                float deadline = Time.realtimeSinceStartup + 5f;
                while (worker.Latest == null && worker.LastError == null && Time.realtimeSinceStartup < deadline)
                { worker.Update(4, 3, 1, items, 0); yield return null; }
                if (worker.Latest == null || worker.Latest[0][0] != 64)
                { Row("VECTOR-structured-generic", "FAIL", worker.LastError?.ToString()); yield break; }
                items[0].Value = .5f; deadline = Time.realtimeSinceStartup + 5f;
                while (worker.LastError == null && Time.realtimeSinceStartup < deadline)
                {
                    worker.Update(4, 3, 1, items, 25);
                    if (worker.Latest != null && worker.Latest[0][0] == 191) break;
                    yield return null;
                }
                Row("VECTOR-structured-generic", worker.LastError == null && worker.Latest?[0]?[0] == 191 ? "PASS" : "FAIL");
            }
        }

        private static IEnumerator CheckGenericVectorTexturesSafe()
        {
            var probe = CheckGenericVectorTextures();
            while (true)
            {
                bool next;
                try { next = probe.MoveNext(); }
                catch (Exception e) { Row("VECTOR-textures-generic", "FAIL", e.ToString()); yield break; }
                if (!next) yield break;
                yield return probe.Current;
            }
        }
        private static IEnumerator CheckGenericVectorTextures()
        {
            var definition = new VectorTextureDefinition();
            definition.Outputs.Add(new VectorTextureDefinition.Output { Name = "custom", Filter = FilterMode.Bilinear,
                Wrap = TextureWrapMode.Repeat, Linear = true });
            Texture2D allocated = null;
            using (var textures = new VectorTextureSet<GenericFrameProbe, int>(GenericVectorSource, definition))
            {
                var items = new[] { new GenericFrameProbe { Width = 4, Height = 3, Value = .25f } };
                bool ready = false; float deadline = Time.realtimeSinceStartup + 5f;
                while (!ready && Time.realtimeSinceStartup < deadline)
                { ready = textures.Update(4, 3, 1, items, 0, null); yield return null; }
                var first = ready ? textures.Get("custom") as Texture2D : null;
                allocated = first;
                if (first == null || first.GetPixels32()[0].r != 64 || first.filterMode != FilterMode.Bilinear || first.wrapMode != TextureWrapMode.Repeat)
                { Row("VECTOR-textures-generic", "FAIL", "initial output"); yield break; }
                items[0].Value = .5f; deadline = Time.realtimeSinceStartup + 5f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    textures.Update(4, 3, 1, items, 25, null);
                    if (first.GetPixels32()[0].r == 191) break;
                    yield return null;
                }
                Row("VECTOR-textures-generic", ReferenceEquals(first, textures.Get("custom")) && first.GetPixels32()[0].r == 191 ? "PASS" : "FAIL",
                    "independent output, declared filtering and same-size texture reuse");
            }
            yield return null;
            Row("VECTOR-textures-cleanup", allocated == null ? "PASS" : "FAIL", "released generated texture after dispose");
        }

        private static IEnumerator CheckGenericFrameProgramSafe()
        {
            var probe = CheckGenericFrameProgram();
            while (true)
            {
                bool next;
                try { next = probe.MoveNext(); }
                catch (Exception e) { Row("FRAME-package-program-generic", "FAIL", e.ToString()); yield break; }
                if (!next) yield break;
                yield return probe.Current;
            }
        }

        private static IEnumerator CheckGenericFrameProgram()
        {
            string root = Path.Combine(Path.GetTempPath(), "pev-frame-program-" + Guid.NewGuid().ToString("N"));
            string planPath = Path.Combine(root, "plan.js"), drawPath = Path.Combine(root, "draw.js");
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(planPath, "function updateController(i){return {width:i.request.Width,height:i.request.Height,frames:1};}");
                File.WriteAllText(drawPath, @"function computeVector(i){
function run(x){return Number(__compute(JSON.stringify(x)));}
var p=run({op:'pixelFill',width:i.width,height:i.height,value:i.request.Value});
return [run({op:'pixelPack',channels:[p,p,p,p]})];}");
                var manifest = new EventManifest { SourcePath = root };
                manifest.VectorPrograms.Add("customPlan", "plan.js"); manifest.VectorPrograms.Add("customDraw", "draw.js");
                manifest.RuntimeBindings.Add("planSlot", "customPlan"); manifest.RuntimeBindings.Add("drawSlot", "customDraw");
                var data = new GenericFrameProbe { Width = 4, Height = 3, Value = .25f };
                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    var plan = PackageFrameProgram.Prepare(manifest, data, "planSlot", System.Threading.CancellationToken.None);
                    var frames = new Color32[plan.Frames][];
                    PackageFrameProgram.Render(manifest, data, "drawSlot", frames, plan.Width, plan.Height, System.Threading.CancellationToken.None);
                    return plan.Width == 4 && plan.Height == 3 && plan.Frames == 1 && frames[0].Length == 12 && frames[0][0].r == 64;
                });
                float deadline = Time.realtimeSinceStartup + 5f;
                while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Row("FRAME-package-program-generic", task.IsCompleted && !task.IsFaulted && task.Result ? "PASS" : "FAIL", task.Exception?.ToString());
            }
            finally
            {
                if (File.Exists(planPath)) File.Delete(planPath);
                if (File.Exists(drawPath)) File.Delete(drawPath);
                Directory.Delete(root);
            }
        }

        private static IEnumerator CheckGenericBakedFrameCacheSafe()
        {
            var probe = CheckGenericBakedFrameCache();
            while (true)
            {
                bool next;
                try { next = probe.MoveNext(); }
                catch (Exception e) { Row("FRAME-cache-generic-lifecycle", "FAIL", e.ToString()); yield break; }
                if (!next) yield break;
                yield return probe.Current;
            }
        }

        private static IEnumerator CheckGenericBakedFrameCache()
        {
            var cache = new BakedFrameCache(100f);
            try
            {
                int jobs = 0;
                Func<System.Threading.CancellationToken, BakedFrameCache.Plan> plan = token =>
                { System.Threading.Interlocked.Increment(ref jobs); return new BakedFrameCache.Plan { Width = 4, Height = 3, Count = 3 }; };
                Action<Color32[][], int, int, System.Threading.CancellationToken> render = (frames, width, height, token) =>
                {
                    for (int k = 0; k < frames.Length; k++)
                        frames[k] = Enumerable.Repeat(new Color32((byte)(k + 1), 2, 3, 255), width * height).ToArray();
                };
                var first = cache.Begin("generic", plan, render);
                var second = cache.Begin("generic", plan, render);
                if (!ReferenceEquals(first, second) || first.Refs != 2) throw new InvalidOperationException("Frame set was not shared");
                var other = new BakedFrameCache(100f);
                try { other.Release(first); throw new InvalidOperationException("Foreign cache accepted the frame set"); }
                catch (ArgumentException) { }
                cache.Release(first);
                float deadline = Time.realtimeSinceStartup + 5f;
                while (!second.Ready && second.Error == null && Time.realtimeSinceStartup < deadline)
                { cache.Pump(2f); yield return null; }
                bool ok = second.Ready && jobs == 1 && second.Sprites.Length == 3 && second.Textures[2] != null &&
                    second.Textures[2].GetPixels32()[0].r == 3;
                cache.Release(second);
                cache.Release(second);
                ok &= cache.Cache.Count == 0 && second.Cancelled;
                var invalid = cache.Begin("too-large", token => new BakedFrameCache.Plan { Width = 4097, Height = 1, Count = 1 }, render);
                deadline = Time.realtimeSinceStartup + 5f;
                while (invalid.Worker.IsAlive && Time.realtimeSinceStartup < deadline) yield return null;
                ok &= invalid.Error != null && invalid.Textures == null;
                cache.Release(invalid);
                var incomplete = cache.Begin("incomplete", token => new BakedFrameCache.Plan { Width = 4, Height = 3, Count = 1 },
                    (frames, width, height, token) => { });
                deadline = Time.realtimeSinceStartup + 5f;
                while (incomplete.Worker.IsAlive && Time.realtimeSinceStartup < deadline) yield return null;
                ok &= incomplete.Error != null && !incomplete.PixelsReady;
                cache.Release(incomplete);
                Row("FRAME-cache-generic-lifecycle", ok && cache.Cache.Count == 0 ? "PASS" : "FAIL",
                    "sharing, ownership, partial and duplicate release, upload, cleanup and invalid output rejection");
            }
            finally { cache.Clear(); }
        }

        private static IEnumerator CheckCpuBakePlanCancellation()
        {
            if (!EventRegistry.TryGetById("pev.noise-field", out var manifest)) yield break;
            var request = Request(); request.Provider = manifest; request.FrameCount = 96;
            var released = new List<BakedFrameCache.Frames>();
            for (int i = 0; i < 20; i++)
            {
                request.Seed = i;
                var frames = NoiseTextureFactory.Begin(request, out string error);
                if (frames == null) { Row("PIXEL-package-plan-cancellation", "FAIL", error); yield break; }
                released.Add(frames); NoiseTextureFactory.Release(frames);
            }
            float deadline = Time.realtimeSinceStartup + 5f;
            while (released.Any(f => f.Worker.IsAlive) && Time.realtimeSinceStartup < deadline) yield return null;
            bool clean = NoiseTextureFactory.CachedFrameSets == 0 && released.All(f => !f.Worker.IsAlive &&
                f.Cancelled && !f.PixelsReady && f.Pixels == null && f.Textures == null && f.Sprites == null);
            Row("PIXEL-package-plan-cancellation", clean ? "PASS" : "FAIL", "20 immediate release races");
        }

        private static IEnumerator CheckCpuBakeCancellation()
        {
            if (!EventRegistry.TryGetById("pev.noise-field", out var manifest)) yield break;
            var request = Request(); request.Provider = manifest; request.FrameCount = 96; request.TargetPixelWidth = 768;
            request.Pieces = new[] { new NoiseTextureFactory.Piece { Half = new Vector2(.8f,.6f), Rotation = 27f } };
            var frames = NoiseTextureFactory.Begin(request, out string error);
            if (frames == null) { Row("PIXEL-package-bake-cancellation", "FAIL", error); yield break; }
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < .1f && frames.Worker.IsAlive) yield return null;
            NoiseTextureFactory.Release(frames);
            float canceled = Time.realtimeSinceStartup, deadline = canceled + 3f;
            while (frames.Worker.IsAlive && Time.realtimeSinceStartup < deadline) yield return null;
            bool ok = !frames.Worker.IsAlive && frames.Cancelled && frames.Pixels == null && NoiseTextureFactory.CachedFrameSets == 0;
            Row("PIXEL-package-bake-cancellation", ok ? "PASS" : "FAIL", $"worker stopped {(Time.realtimeSinceStartup-canceled)*1000:F0} ms after release");
        }

        private static IEnumerator CheckCpuBakeLarge()
        {
            if (!EventRegistry.TryGetById("pev.noise-field", out var manifest)) yield break;
            var request = Request(); request.Provider = manifest; request.ScreenPixelWidth = 1024; request.FrameCount = 2;
            request.EvolveSpeed = 24; request.EdgeColor = new Color(.8f,.2f,.4f); request.SceneColor = new Color(.1f,.3f,.7f);
            request.FillStrength = .7f; request.Exposure = .15f; request.SparkOpacity = .8f;
            request.Pieces = new[] { new NoiseTextureFactory.Piece { Half = new Vector2(.8f,.6f), Rotation = 27f } };
            var frames = new Color32[2][];
            using (var cancellation = new System.Threading.CancellationTokenSource())
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var task = System.Threading.Tasks.Task.Run(() => PackageFrameProgram.Render(request.Provider, request, "frameProgram", frames, 1024, 1024, cancellation.Token));
                float deadline = Time.realtimeSinceStartup + 30f;
                while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                if (!task.IsCompleted) cancellation.Cancel();
                watch.Stop();
                bool ok = task.IsCompleted && !task.IsFaulted && frames.All(frame => frame != null && frame.Length == 1024 * 1024);
                Row("PIXEL-package-bake-large", ok ? "PASS" : "FAIL", $"2 frames 1024x1024: {watch.Elapsed.TotalMilliseconds:F0} ms; {task.Exception}");
            }
        }

        private static IEnumerator CheckFieldsWithoutGpuDriver()
        {
            NoiseFieldGpuDriver.SuppressForFallbackTest = true;
            var test = CheckFields();
            try
            {
                while (test.MoveNext()) yield return test.Current;
            }
            finally
            {
                (test as IDisposable)?.Dispose();
                NoiseFieldGpuDriver.SuppressForFallbackTest = false;
            }
        }

        private static IEnumerator CheckCpuBakePerformance()
        {
            if (!EventRegistry.TryGetById("pev.noise-field", out var manifest)) yield break;
            var request = Request(); request.Provider = manifest; request.ScreenPixelWidth = 640; request.FrameCount = 96;
            request.EvolveSpeed = 24; request.EdgeColor = new Color(.8f,.2f,.4f); request.SceneColor = new Color(.1f,.3f,.7f);
            request.FillStrength = .7f; request.Exposure = .15f; request.SparkOpacity = .8f;
            request.Pieces = new[] { new NoiseTextureFactory.Piece { Half = new Vector2(.8f,.6f), Rotation = 27f } };
            var frames = new Color32[96][];
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var task = System.Threading.Tasks.Task.Run(() => PackageFrameProgram.Render(request.Provider, request, "frameProgram", frames, 192, 132, System.Threading.CancellationToken.None));
            float deadline = Time.realtimeSinceStartup + 20f, previous = Time.realtimeSinceStartup, worstFrame = 0;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                float now = Time.realtimeSinceStartup; worstFrame = Math.Max(worstFrame, now - previous); previous = now;
            }
            watch.Stop();
            bool ok = task.IsCompleted && !task.IsFaulted && frames.All(frame => frame != null && frame.Length == 192 * 132);
            Row("PIXEL-package-bake-performance", ok ? "PASS" : "FAIL",
                $"96 frames 192x132: {watch.Elapsed.TotalMilliseconds:F0} ms wall, worst observed game frame {worstFrame * 1000:F1} ms; {task.Exception}");
        }

        private static IEnumerator CheckCpuGlitchProgram()
        {
            if (!EventRegistry.TryGetById("pev.noise-field", out var manifest) || !manifest.VectorPrograms.TryGetValue("bake", out var path))
            { Row("PIXEL-package-glitch-parity", "FAIL", "package CPU program is missing"); yield break; }
            var samples = LegacyFieldTextureSet.Load(manifest, out string error);
            if (samples?.Profile == null) { Row("PIXEL-package-glitch-parity", "FAIL", error); yield break; }
            const int texW = 80, texH = 61, frameCount = 4;
            var req = Request(); req.Provider = manifest; req.Samples = samples; req.WidthWorld = 2.3f; req.HeightWorld = 1.8f;
            req.Margin = .25f; req.ScreenPixelWidth = 320; req.EvolveSpeed = 24f; req.Seed = 123456; req.Glitch = .9f;
            req.EdgeColor = new Color(.8f, .2f, .4f); req.SceneColor = new Color(.1f, .3f, .7f);
            req.FillStrength = .7f; req.Exposure = .15f; req.SparkOpacity = .8f;
            req.Pieces = new[] { new NoiseTextureFactory.Piece { Offset = new Vector2(.1f,.1f), Half = new Vector2(.8f,.6f), Rotation = 27f } };
            var expected = new Color32[frameCount][];
            var baked = new Color32[frameCount][];
            var reference = System.Threading.Tasks.Task.Run(() =>
                LegacyCpuBakeReference.RenderPixels(req, expected, texW, texH, () => false));
            var load = System.Threading.Tasks.Task.Run(() => PixelResourceLoader.Load(manifest, System.Threading.CancellationToken.None));
            var bakeTime = System.Diagnostics.Stopwatch.StartNew();
            var bake = System.Threading.Tasks.Task.Run(() => PackageFrameProgram.Render(req.Provider, req, "frameProgram", baked, texW, texH, System.Threading.CancellationToken.None));
            float deadline = Time.realtimeSinceStartup + 15f;
            while ((!reference.IsCompleted || !load.IsCompleted || !bake.IsCompleted) && Time.realtimeSinceStartup < deadline) yield return null;
            bakeTime.Stop();
            if (!reference.IsCompleted || reference.IsFaulted || !load.IsCompleted || load.IsFaulted || !bake.IsCompleted || bake.IsFaulted)
            { Row("PIXEL-package-glitch-parity", "FAIL", reference.Exception?.ToString() ?? load.Exception?.ToString() ?? bake.Exception?.ToString()); yield break; }
            bool bakeMatches = Enumerable.Range(0, frameCount).All(i => baked[i] != null && baked[i].SequenceEqual(expected[i]));
            Row("PIXEL-package-bake-scheduler", bakeMatches ? "PASS" : "FAIL", $"{frameCount} frames {texW}x{texH}, {bakeTime.Elapsed.TotalMilliseconds:F0} ms");
            string source = System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, path, 262144));
            var profile = LegacyFieldTextureSet.ProfileData(manifest);
            int cw = Mathf.Clamp(Mathf.RoundToInt(req.ScreenPixelWidth * samples.Profile.ComposeScale), 1, texW);
            int ch = Mathf.Clamp(Mathf.RoundToInt(cw * (float)texH / texW), 1, texH);
            int ew = Mathf.Clamp(Mathf.Max(2, cw * 2), 1, texW), eh = Mathf.Clamp(Mathf.Max(2, ch * 2), 1, texH);
            int displaced = 0;
            using (var worker = new VectorWorker<string>(source, x => x, (a,b) => a == b, (w,h,a,x) => x, load.Result))
            {
                for (int k = 0; k < frameCount; k++)
                {
                    float time = k / req.EvolveSpeed;
                    string payload = Newtonsoft.Json.JsonConvert.SerializeObject(new {
                        stage = "frame", width = texW, height = texH, screenPixelWidth = req.ScreenPixelWidth,
                        time, loopPeriod = frameCount / req.EvolveSpeed,
                        frameCount, frameIndex = k, seed = req.Seed,
                        totalWidth = req.WidthWorld + req.Margin * 2f, totalHeight = req.HeightWorld + req.Margin * 2f,
                        profile = profile.ToDictionary(p => p.Key, p => p.Value.Length == 1 ? (object)p.Value[0] : p.Value),
                        options = new { edgeColor = new[] { req.EdgeColor.r, req.EdgeColor.g, req.EdgeColor.b },
                            sceneColor = new[] { req.SceneColor.r, req.SceneColor.g, req.SceneColor.b },
                            fillOpacity = req.FillOpacity, fillStrength = req.FillStrength, exposure = req.Exposure,
                            edgeOpacity = req.EdgeOpacity, glowOpacity = req.GlowOpacity, sparkOpacity = req.SparkOpacity, glitch = req.Glitch },
                        pieces = req.Pieces.Select(p => new { x = p.Offset.x, y = p.Offset.y, hx = p.Half.x, hy = p.Half.y,
                            rotation = p.Rotation, circle = p.Circle }).ToArray()
                    });
                    bool ready = worker.Update(texW, texH, 1f, payload);
                    deadline = Time.realtimeSinceStartup + 10f;
                    while (!ready && worker.LastError == null && Time.realtimeSinceStartup < deadline)
                    { yield return null; ready = worker.Update(texW, texH, 1f, payload); }
                    if (!ready || worker.Latest == null)
                    { Row("PIXEL-package-glitch-parity", "FAIL", "frame " + k + ": " + worker.LastError); yield break; }
                    var raw = new Color32[texW * texH];
                    req.LoopPeriod = frameCount / req.EvolveSpeed;
                    NoiseComposer.Frame.Build(req.Pieces, req, texW, texH, time, 0, frameCount).RenderRows(raw, 0, texH);
                    byte[] actual = worker.Latest[0]; int differences = 0;
                    for (int i = 0; i < expected[k].Length; i++)
                    {
                        var pixel = expected[k][i];
                        if (actual[4*i] != pixel.r || actual[4*i+1] != pixel.g || actual[4*i+2] != pixel.b || actual[4*i+3] != pixel.a) differences++;
                        if (!pixel.Equals(raw[i])) displaced++;
                    }
                    if (differences != 0)
                    { Row("PIXEL-package-glitch-parity", "FAIL", $"frame {k}, {differences} pixels differ"); yield break; }
                }
            }
            Row("PIXEL-package-glitch-parity", displaced > 0 ? "PASS" : "FAIL", $"{frameCount} frames, {displaced} pixels actually shifted");
        }

        private static IEnumerator CheckCpuCoverageProgram()
        {
            if (!EventRegistry.TryGetById("pev.noise-field", out var manifest) || !manifest.VectorPrograms.TryGetValue("bake", out var path))
            { Row("PIXEL-package-coverage-parity", "FAIL", "package CPU program is missing"); yield break; }
            byte[] script = EventLoader.ReadResource(manifest.SourcePath, path, 262144);
            var load = System.Threading.Tasks.Task.Run(() => PixelResourceLoader.Load(manifest, System.Threading.CancellationToken.None));
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!load.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            if (!load.IsCompleted || load.IsFaulted || script == null)
            { Row("PIXEL-package-coverage-parity", "FAIL", load.Exception?.ToString()); yield break; }
            var samples = LegacyFieldTextureSet.Load(manifest, out string error);
            if (samples?.Profile == null) { Row("PIXEL-package-coverage-parity", "FAIL", error); yield break; }
            var profile = LegacyFieldTextureSet.ProfileData(manifest);
            var random = new System.Random(3127);
            using (var worker = new VectorWorker<string>(System.Text.Encoding.UTF8.GetString(script), x => x, (a,b) => a == b,
                (w,h,a,x) => x, load.Result))
            {
                for (int trial = 0; trial < 80; trial++)
                {
                    int count = trial % 17;
                    var pieces = new NoiseTextureFactory.Piece[count];
                    for (int i = 0; i < count; i++)
                        pieces[i] = new NoiseTextureFactory.Piece {
                            Offset = new Vector2((float)random.NextDouble() * 1.4f - .7f, (float)random.NextDouble() - .5f),
                            Half = new Vector2(.1f + (float)random.NextDouble() * .5f, .1f + (float)random.NextDouble() * .4f),
                            Rotation = (float)random.NextDouble() * 160f - 80f, Circle = i % 3 == 2
                        };
                    var req = Request(); req.Provider = manifest; req.Samples = samples;
                    req.WidthWorld = 2.3f; req.HeightWorld = 1.8f; req.Margin = .25f;
                    req.ScreenPixelWidth = trial % 2 == 0 ? 320 : 511;
                    req.EvolveSpeed = 24f; req.LoopPeriod = 12f / req.EvolveSpeed;
                    req.EdgeColor = new Color((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                    req.SceneColor = new Color((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                    req.FillStrength = .35f + .55f * (float)random.NextDouble();
                    req.Exposure = .2f; req.SparkOpacity = .8f;
                    int texW = trial % 2 == 0 ? 80 : 127, texH = trial % 2 == 0 ? 60 : 71;
                    float time = trial * .071f;
                    var old = NoiseComposer.Frame.Build(pieces, req, texW, texH, time, 0, 12);
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var type = typeof(NoiseComposer.Frame);
                    int width = (int)type.GetField("_cw", flags).GetValue(old), height = (int)type.GetField("_ch", flags).GetValue(old);
                    var expected = (float[])type.GetField("_compose", flags).GetValue(old);
                    string payload = Newtonsoft.Json.JsonConvert.SerializeObject(new {
                        stage = "coverage", width, height, time, loopPeriod = req.LoopPeriod, frameCount = 12,
                        totalWidth = Mathf.Max(.001f, req.WidthWorld + req.Margin * 2f),
                        totalHeight = Mathf.Max(.001f, req.HeightWorld + req.Margin * 2f),
                        profile = new { composeDisplaceDirection = profile["composeDisplaceDirection"],
                            composeDisplaceMapScale = profile["composeDisplaceMapScale"],
                            composeDisplaceSpeed = profile["composeDisplaceSpeed"][0],
                            composeDisplaceStrength = profile["composeDisplaceStrength"][0] },
                        pieces = pieces.Select(p => new { x = p.Offset.x, y = p.Offset.y, hx = p.Half.x, hy = p.Half.y,
                            rotation = p.Rotation, circle = p.Circle }).ToArray()
                    });
                    bool ready = worker.Update(width, height, 1f, payload);
                    deadline = Time.realtimeSinceStartup + 10f;
                    while (!ready && worker.LastError == null && Time.realtimeSinceStartup < deadline)
                    { yield return null; ready = worker.Update(width, height, 1f, payload); }
                    if (!ready || worker.Latest == null)
                    { Row("PIXEL-package-coverage-parity", "FAIL", "trial " + trial + ": " + worker.LastError); yield break; }
                    byte[] actual = worker.Latest[0]; int differences = 0;
                    for (int i = 0; i < expected.Length; i++) if (actual[i * 4] != (expected[i] > .5f ? 255 : 0)) differences++;
                    if (differences != 0)
                    { Row("PIXEL-package-coverage-parity", "FAIL", $"trial {trial}, {count} pieces, {width}x{height}, {differences} pixels differ"); yield break; }
                    int effectWidth = (int)type.GetField("_ew", flags).GetValue(old), effectHeight = (int)type.GetField("_eh", flags).GetValue(old);
                    var oldEdges = (float[])type.GetField("_edges", flags).GetValue(old);
                    var oldGlow = (float[])type.GetField("_glow", flags).GetValue(old);
                    var effectInput = JObject.Parse(payload);
                    effectInput["stage"] = "effects"; effectInput["coverageWidth"] = width; effectInput["coverageHeight"] = height;
                    effectInput["width"] = effectWidth; effectInput["height"] = effectHeight;
                    effectInput["profile"]["glowWeights"] = JArray.FromObject(profile["glowWeights"]);
                    string effectPayload = effectInput.ToString(Newtonsoft.Json.Formatting.None);
                    ready = worker.Update(effectWidth, effectHeight, 1f, effectPayload);
                    deadline = Time.realtimeSinceStartup + 10f;
                    while (!ready && worker.LastError == null && Time.realtimeSinceStartup < deadline)
                    { yield return null; ready = worker.Update(effectWidth, effectHeight, 1f, effectPayload); }
                    if (!ready || worker.Latest == null)
                    { Row("PIXEL-package-effects-parity", "FAIL", "trial " + trial + ": " + worker.LastError); yield break; }
                    actual = worker.Latest[0]; int edgeDifferences = 0, glowDifferences = 0;
                    for (int i = 0; i < oldEdges.Length; i++)
                    {
                        int expectedEdge = (int)Math.Floor(Math.Max(0f, Math.Min(1f, oldEdges[i])) * 255f + .5f);
                        int expectedGlow = (int)Math.Floor(Math.Max(0f, Math.Min(1f, oldGlow[i])) * 255f + .5f);
                        if (actual[i * 4] != expectedEdge) edgeDifferences++;
                        if (actual[i * 4 + 1] != expectedGlow) glowDifferences++;
                    }
                    if (edgeDifferences != 0 || glowDifferences != 0)
                    { Row("PIXEL-package-effects-parity", "FAIL", $"trial {trial}, edge {edgeDifferences}, glow {glowDifferences} differing bytes"); yield break; }
                    if (trial < 16)
                    {
                        var expectedFrame = new Color32[texW * texH];
                        old.RenderRows(expectedFrame, 0, texH);
                        var frameInput = (JObject)effectInput.DeepClone();
                        frameInput["stage"] = "frame"; frameInput["width"] = texW; frameInput["height"] = texH;
                        frameInput.Remove("coverageWidth"); frameInput.Remove("coverageHeight");
                        frameInput["screenPixelWidth"] = req.ScreenPixelWidth;
                        frameInput["profile"] = JObject.FromObject(profile.ToDictionary(p => p.Key,
                            p => p.Value.Length == 1 ? (object)p.Value[0] : p.Value));
                        frameInput["options"] = JObject.FromObject(new {
                            edgeColor = new[] { req.EdgeColor.r, req.EdgeColor.g, req.EdgeColor.b },
                            sceneColor = new[] { req.SceneColor.r, req.SceneColor.g, req.SceneColor.b },
                            fillOpacity = req.FillOpacity, fillStrength = req.FillStrength, exposure = req.Exposure,
                            edgeOpacity = req.EdgeOpacity, glowOpacity = req.GlowOpacity, sparkOpacity = req.SparkOpacity
                        });
                        string framePayload = frameInput.ToString(Newtonsoft.Json.Formatting.None);
                        ready = worker.Update(texW, texH, 1f, framePayload);
                        deadline = Time.realtimeSinceStartup + 10f;
                        while (!ready && worker.LastError == null && Time.realtimeSinceStartup < deadline)
                        { yield return null; ready = worker.Update(texW, texH, 1f, framePayload); }
                        if (!ready || worker.Latest == null)
                        { Row("PIXEL-package-frame-parity", "FAIL", "trial " + trial + ": " + worker.LastError); yield break; }
                        actual = worker.Latest[0]; int changed = 0, maxDifference = 0, first = -1;
                        for (int i = 0; i < expectedFrame.Length; i++)
                        {
                            var color = expectedFrame[i]; var expectedBytes = new[] { color.r, color.g, color.b, color.a };
                            for (int channel = 0; channel < 4; channel++)
                            {
                                int delta = Math.Abs(actual[i * 4 + channel] - expectedBytes[channel]);
                                if (delta > 0) { changed++; if (first < 0) first = i * 4 + channel; maxDifference = Math.Max(maxDifference, delta); }
                            }
                        }
                        if (changed != 0)
                        { Row("PIXEL-package-frame-parity", "FAIL", $"trial {trial}, {changed} bytes differ, max {maxDifference}, first {first}"); yield break; }
                    }
                }
            }
            Row("PIXEL-package-coverage-parity", "PASS", "80 old/new CPU coverage cases including circles, rotation and up to 16 regions");
            Row("PIXEL-package-effects-parity", "PASS", "80 old/new CPU edge and five-pass glow cases");
            Row("PIXEL-package-frame-parity", "PASS", "16 full old/new CPU frames with varied colors and scene backgrounds");
        }

        private static bool CheckPackagePixelResourceLimits()
        {
            string root = Path.Combine(Path.GetTempPath(), "pev-pixel-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(root, "image.png");
            Directory.CreateDirectory(root);
            try
            {
                var manifest = new EventManifest { SourcePath = root };
                manifest.PixelResources.Add("image", "../escape.png");
                try { PixelResourceLoader.Load(manifest, System.Threading.CancellationToken.None); return false; }
                catch (ArgumentException) { }
                var image = NoiseTextures.EncodePng(new[] { new Color32(1, 2, 3, 255) }, 1, 1);
                image[16] = 0; image[17] = 0; image[18] = 16; image[19] = 1; // width 4097
                File.WriteAllBytes(file, image); manifest.PixelResources["image"] = "image.png";
                try { PixelResourceLoader.Load(manifest, System.Threading.CancellationToken.None); return false; }
                catch (ArgumentException) { }
                using (var cancellation = new System.Threading.CancellationTokenSource())
                {
                    cancellation.Cancel();
                    try { PixelResourceLoader.Load(manifest, cancellation.Token); return false; }
                    catch (OperationCanceledException) { }
                }
                return true;
            }
            finally { if (File.Exists(file)) File.Delete(file); Directory.Delete(root); }
        }

        private static IEnumerator CheckPackagePixelResources()
        {
            if (!EventRegistry.TryGetById("pev.noise-field", out var manifest))
            { Row("PIXEL-package-resources", "FAIL", "missing package"); yield break; }
            var task = System.Threading.Tasks.Task.Run(() => PixelResourceLoader.Load(manifest, System.Threading.CancellationToken.None));
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            if (!task.IsCompleted || task.IsFaulted)
            { Row("PIXEL-package-resources", "FAIL", task.Exception?.ToString()); yield break; }
            var loaded = task.Result;
            var expected = LegacyFieldTextureSet.Load(manifest, out string error);
            if (expected == null || !loaded.TryGetValue("block", out var block) || !loaded.TryGetValue("point", out var point))
            { Row("PIXEL-package-resources", "FAIL", error); yield break; }
            var b = block.Plane("point", "clamp"); var p = point.Plane("point", "clamp");
            bool same = block.Width == expected.BlockWidth && block.Height == expected.BlockHeight &&
                point.Width == expected.PointWidth && point.Height == expected.PointHeight &&
                Math.Abs(b.Sample(.5f / block.Width, .5f / block.Height, 0) - expected.Block[0].r / 255f) < .000001f &&
                Math.Abs(p.Sample(.5f / point.Width, .5f / point.Height, 0) - expected.Point[0].r / 255f) < .000001f;
            Row("PIXEL-package-resources", same ? "PASS" : "FAIL", "package PNGs decoded off the main thread");
        }

        private static bool CheckPixelKernel()
        {
            var plane = new PixelPlane(2, 2, 1, new[] { 1f, 2f, 3f, 4f }, "point", "clamp");
            var program = PixelKernel.Compile(JArray.Parse("[[\"u\"],[\"v\"],[\"sample\",0,0]]"), 1, 0);
            var output = new float[8];
            program.Execute(new[] { plane }, Array.Empty<float>(), output, 4, 2, System.Threading.CancellationToken.None);
            if (!output.SequenceEqual(new[] { 1f, 1f, 2f, 2f, 3f, 3f, 4f, 4f })) return false;
            var linear = new PixelPlane(2, 1, 1, new[] { 1f, 3f }, "linear", "repeat");
            var mirrored = new PixelPlane(2, 1, 1, new[] { 1f, 3f }, "point", "mirror");
            if (Math.Abs(linear.Sample(0, .5f, 0) - 2f) > .00001f ||
                Math.Abs(linear.Sample(.5f, .5f, 0) - 2f) > .00001f || mirrored.Sample(-.25f, .5f, 0) != 1f) return false;
            var expression = PixelKernel.Compile(JArray.Parse("[[\"u\"],[\"c\",0.5],[\"ge\"],[\"uniform\",0],[\"set\",0],[\"get\",0],[\"c\",-1],[\"select\"],[\"clamp\"]]"), 0, 1);
            expression.Execute(Array.Empty<PixelPlane>(), new[] { .75f }, output, 4, 2, System.Threading.CancellationToken.None);
            return output.SequenceEqual(new[] { 0f, 0f, .75f, .75f, 0f, 0f, .75f, .75f });
        }

        private static bool CheckPixelKernelLimits()
        {
            foreach (string text in new[] { "[[\"call\",\"System\"]]", "[[\"add\"]]", "[[\"get\",0]]",
                "[[\"c\",0],[\"c\",0],[\"sample\",1,0]]", "[[\"uniform\",1]]" })
            {
                try { PixelKernel.Compile(JArray.Parse(text), 1, 1); return false; }
                catch (Exception) { }
            }
            var kernel = PixelKernel.Compile(JArray.Parse("[[\"u\"]]"), 0, 0);
            using (var cancellation = new System.Threading.CancellationTokenSource())
            {
                cancellation.Cancel();
                try { kernel.Execute(Array.Empty<PixelPlane>(), Array.Empty<float>(), new float[1], 1, 1, cancellation.Token); return false; }
                catch (OperationCanceledException) { }
            }
            try { kernel.Execute(Array.Empty<PixelPlane>(), Array.Empty<float>(), new float[2], 1, 1, System.Threading.CancellationToken.None); return false; }
            catch (ArgumentException) { }
            return true;
        }

        private static bool CheckPixelKernelPerformance()
        {
            var program = PixelKernel.Compile(JArray.Parse("[[\"u\"],[\"v\"],[\"sample\",0,0],[\"set\",0],[\"u\"],[\"v\"],[\"sample\",1,0],[\"get\",0],[\"add\"],[\"uniform\",0],[\"mul\"],[\"c\",0.1],[\"add\"],[\"clamp\"]]"), 2, 1);
            var source = new PixelPlane(128, 128, 1, Enumerable.Repeat(.25f, 128 * 128).ToArray(), "point", "repeat");
            var output = new float[400 * 224];
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) program.Execute(new[] { source, source }, new[] { 1f }, output, 400, 224, System.Threading.CancellationToken.None);
            watch.Stop();
            Row("PIXEL-kernel-cost", "INFO", $"400x224, two samples, mean {watch.Elapsed.TotalMilliseconds / 5:F2} ms/pass on game Mono");
            return Math.Abs(output[0] - .6f) < .00001f && Math.Abs(output[output.Length - 1] - .6f) < .00001f;
        }

        private static bool CheckPixelWorkerLimits()
        {
            var vector = new VectorCompute(1, 1, 1f);
            vector.Begin(System.Threading.CancellationToken.None);
            try { vector.Apply("{\"op\":\"pixelResource\",\"name\":\"outside\"}"); return false; }
            catch (ArgumentException) { }
            int old = int.Parse(vector.Apply("{\"op\":\"pixelFill\",\"width\":1,\"height\":1,\"value\":0.5}"));
            vector.Begin(System.Threading.CancellationToken.None);
            try
            {
                vector.Apply("{\"op\":\"pixelRun\",\"width\":1,\"height\":1,\"sources\":[{\"handle\":" + old +
                    ",\"filter\":\"point\",\"wrap\":\"clamp\"}],\"uniforms\":[],\"program\":[[\"u\"]]}");
                return false;
            }
            catch (ArgumentException) { }
            try { vector.Apply("{\"op\":\"pixelFill\",\"width\":4096,\"height\":4096,\"value\":0}"); return false; }
            catch (ArgumentOutOfRangeException) { }
            int source = int.Parse(vector.Apply("{\"op\":\"pixelFill\",\"width\":1,\"height\":1,\"value\":0.5}"));
            string invalid = "{\"op\":\"pixelRun\",\"width\":1,\"height\":1,\"sources\":[{\"handle\":" + source +
                ",\"filter\":\"point\",\"wrap\":\"clamp\"}],\"uniforms\":[],\"program\":[[\"call\",\"System\"]]}";
            try { vector.Apply(invalid); return false; }
            catch (ArgumentException) { }
            return true;
        }

        private static IEnumerator CheckPackagedVectorWorker()
        {
            if (!EventRegistry.TryGetById("pev.noise-field", out var manifest) ||
                !manifest.VectorPrograms.TryGetValue("palette", out var path))
            { Row("VECTOR-package-worker", "FAIL", "package vector program is missing"); yield break; }
            byte[] bytes=EventLoader.ReadResource(manifest.SourcePath,path);
            if(bytes==null){Row("VECTOR-package-worker","FAIL","package vector script not found");yield break;}
            string source=System.Text.Encoding.UTF8.GetString(bytes);
            var zones=new[] {
                new NoiseFieldColorOwnership.Zone {CenterX=-.2f,CenterY=.05f,HalfX=.45f,HalfY=.27f,Active=true,YScale=1,Opacity=1,Angle=.23f,Red=0,Green=1,Blue=0},
                new NoiseFieldColorOwnership.Zone {CenterX=.3f,CenterY=-.1f,HalfX=.35f,HalfY=.18f,Active=true,Inverted=true,YScale=1,Opacity=1,Blue=1},
                new NoiseFieldColorOwnership.Zone {CenterX=.05f,CenterY=.1f,HalfX=.28f,HalfY=.22f,Active=false,YScale=1,Opacity=.35f,Red=1,Green=1,Blue=1}
            };
            var input=zones.Select(z=>new {centerX=z.CenterX,centerY=z.CenterY,halfX=z.HalfX,halfY=z.HalfY,
                inverted=z.Inverted,active=z.Active,yScale=z.YScale,opacity=z.Opacity,angle=z.Angle,
                red=z.Red,green=z.Green,blue=z.Blue}).ToArray();
            const int width=64,height=36;const float aspect=16f/9f;
            string payload=Newtonsoft.Json.JsonConvert.SerializeObject(new {width,height,aspect,defaultRgb=0xff5454,zones=input});
            var expected=NoiseFieldColorOwnership.Build(width,height,aspect,zones,0xff5454);
            using(var worker=new VectorWorker<string>(source,x=>x,(a,b)=>a==b,(w,h,a,x)=>x))
            {
                worker.Update(width,height,aspect,payload);
                float deadline=Time.realtimeSinceStartup+10f;
                while(worker.Latest==null && worker.LastError==null && Time.realtimeSinceStartup<deadline)
                { worker.Update(width,height,aspect,payload); yield return null; }
                var result=worker.Latest;
                Row("VECTOR-package-worker", result!=null && result.Length==2 && result[0].SequenceEqual(expected.Active) &&
                    result[1].SequenceEqual(expected.Disabled) ? "PASS" : "FAIL", worker.LastError?.ToString());
            }
        }

        private static IEnumerator CheckVectorWorker()
        {
            const string source = @"function computeVector(input) {
if (typeof System !== 'undefined' || typeof Uint8Array !== 'undefined') throw Error('sandbox escape');
function run(x) { return Number(__compute(JSON.stringify(x))); }
var a=run({op:'raster',blend:'set',shapes:[{x:0,y:0,hx:2,hy:2,scale:1,angle:0,value:1,owner:0}]});
var b=run({op:'raster',blend:'add',shapes:[]});
var v=run({op:'evaluate',inputs:[a],program:[['c',0]]});
var s=run({op:'differenceSeeds',a:a,b:b,value:v});
return [run({op:'map',seeds:s,colors:[input.color],fallback:0})];
}";
            using (var worker = new VectorWorker<string>(source, x => x, (a,b) => a == b, (w,h,a,x) => x))
            {
                const string payload = "{\"color\":[0.25,0.5,0.75]}";
                worker.Update(16,8,2f,payload);
                float deadline = Time.realtimeSinceStartup + 10f;
                while (worker.Latest == null && worker.LastError == null && Time.realtimeSinceStartup < deadline)
                { worker.Update(16,8,2f,payload); yield return null; }
                var data=worker.Latest;
                Row("VECTOR-worker-sandbox", data != null && data.Length == 1 && data[0].Length == 16*8*4 &&
                    data[0][0] == 64 && data[0][1] == 128 && data[0][2] == 191 ? "PASS" : "FAIL",
                    worker.LastError?.ToString());
            }
            const string pixelSource = @"function computeVector(input) {
function run(x) { return Number(__compute(JSON.stringify(x))); }
var red=run({op:'pixelFill',width:16,height:8,value:input.red});
var green=run({op:'pixelRun',width:16,height:8,sources:[{handle:red,filter:'point',wrap:'clamp'}],uniforms:[],
  program:[['u'],['v'],['sample',0,0],['c',2],['mul'],['clamp']]});
var blue=run({op:'pixelFill',width:16,height:8,value:0.75});
var alpha=run({op:'pixelFill',width:16,height:8,value:1});
return [run({op:'pixelPack',channels:[red,green,blue,alpha]})];
}";
            using (var worker = new VectorWorker<string>(pixelSource, x => x, (a,b) => a == b, (w,h,a,x) => x))
            {
                const string payload = "{\"red\":0.25}";
                worker.Update(16,8,2f,payload);
                float deadline = Time.realtimeSinceStartup + 10f;
                while (worker.Latest == null && worker.LastError == null && Time.realtimeSinceStartup < deadline)
                { worker.Update(16,8,2f,payload); yield return null; }
                var pixels = worker.Latest;
                Row("PIXEL-worker-package-commands", pixels != null && pixels.Length == 1 && pixels[0].Length == 16*8*4 &&
                    pixels[0][0] == 64 && pixels[0][1] == 128 && pixels[0][2] == 191 && pixels[0][3] == 255 ? "PASS" : "FAIL",
                    worker.LastError?.ToString());
            }
            const string resourceSource = @"function computeVector() {
function run(x) { return Number(__compute(JSON.stringify(x))); }
var image=run({op:'pixelResource',name:'fixture'});
function channel(n) { return run({op:'pixelRun',width:2,height:1,sources:[{handle:image,filter:'point',wrap:'clamp'}],
  uniforms:[],program:[['u'],['v'],['sample',0,n]]}); }
return [run({op:'pixelPack',channels:[channel(0),channel(1),channel(2),channel(3)]})];
}";
            var bytes = new byte[] { 10, 20, 30, 255, 40, 50, 60, 128 };
            var resources = new Dictionary<string, PixelResource> { ["fixture"] = new PixelResource(2, 1, 4, bytes) };
            bytes[0] = 99; // The worker must retain its own immutable snapshot.
            using (var worker = new VectorWorker<string>(resourceSource, x => x, (a,b) => a == b, (w,h,a,x) => x, resources))
            {
                worker.Update(2, 1, 2f, "{}");
                float deadline = Time.realtimeSinceStartup + 10f;
                while (worker.Latest == null && worker.LastError == null && Time.realtimeSinceStartup < deadline)
                { worker.Update(2, 1, 2f, "{}"); yield return null; }
                var pixels = worker.Latest;
                Row("PIXEL-worker-resource-snapshot", pixels != null && pixels.Length == 1 &&
                    pixels[0].SequenceEqual(new byte[] { 10, 20, 30, 255, 40, 50, 60, 128 }) ? "PASS" : "FAIL",
                    worker.LastError?.ToString());
            }
            using (var worker = new VectorWorker<string>("function computeVector(){return eval('1');}", x => x, (a,b) => a == b, (w,h,a,x) => x))
            {
                worker.Update(1,1,1f,"{}");
                float deadline=Time.realtimeSinceStartup+10f;
                while(worker.LastError==null && worker.Latest==null && Time.realtimeSinceStartup<deadline)
                { worker.Update(1,1,1f,"{}"); yield return null; }
                Row("VECTOR-worker-no-eval", worker.LastError!=null && worker.Latest==null ? "PASS" : "FAIL");
            }
            using (var worker = new VectorWorker<string>("function computeVector(){while(true){} }", x => x, (a,b) => a == b, (w,h,a,x) => x))
            {
                worker.Update(1,1,1f,"{}");
                float deadline=Time.realtimeSinceStartup+10f;
                while(worker.LastError==null && worker.Latest==null && Time.realtimeSinceStartup<deadline)
                { worker.Update(1,1,1f,"{}"); yield return null; }
                Row("VECTOR-worker-runaway-limited", worker.LastError!=null && worker.Latest==null ? "PASS" : "FAIL");
            }
            Check("VECTOR-expression-rejects-invalid", () =>
            {
                foreach (string json in new[] { "[[\"add\"]]", "[[\"in\",1]]", "[[\"get\",0]]", "[[\"unknown\"]]", "[[\"c\",1],[\"c\",2]]" })
                {
                    try { VectorExpression.Compile(JToken.Parse(json),1); return false; }
                    catch (ArgumentException) { }
                }
                return true;
            });
            Check("VECTOR-expression-cancellation", () =>
            {
                var program=VectorExpression.Compile(JToken.Parse("[[\"in\",0]]"),1);
                var input=new byte[8192]; var output=new byte[input.Length]; int calls=0;
                try { program(new[] { input },output,input.Length,()=>{ if(++calls==2) throw new OperationCanceledException(); }); return false; }
                catch(OperationCanceledException) { return calls==2; }
            });
            Check("VECTOR-cancellation-and-handles", () =>
            {
                var vector=new VectorCompute(1,1,1f);
                vector.Begin(System.Threading.CancellationToken.None);
                int handle=int.Parse(vector.Apply("{\"op\":\"raster\",\"blend\":\"add\",\"shapes\":[]}"));
                vector.Begin(System.Threading.CancellationToken.None);
                try { vector.Apply("{\"op\":\"evaluate\",\"inputs\":["+handle+"],\"program\":[[\"c\",0]]}"); return false; }
                catch (ArgumentException) { }
                var canceled=new System.Threading.CancellationTokenSource(); canceled.Cancel();
                try { vector.Begin(canceled.Token); return false; }
                catch (OperationCanceledException) { return true; }
                finally { canceled.Dispose(); }
            });
            Check("VECTOR-limits", () =>
            {
                try { new VectorCompute(4096,4096,1f); return false; }
                catch (ArgumentOutOfRangeException) { }
                var vector=new VectorCompute(1,1,1f);vector.Begin(System.Threading.CancellationToken.None);
                try { vector.Apply("{\"op\":\"unknown\"}"); return false; }
                catch (ArgumentException) { }
                try { vector.Apply("{\"op\":\"raster\",\"blend\":\"set\",\"shapes\":[],\"op\":\"raster\"}"); return false; }
                catch (Newtonsoft.Json.JsonReaderException) { }
                var large=new VectorCompute(1024,1024,1f);large.Begin(System.Threading.CancellationToken.None);
                string tooMuch="{\"op\":\"raster\",\"blend\":\"add\",\"shapes\":["+
                    string.Join(",",Enumerable.Repeat("{}",64))+"]}";
                try { large.Apply(tooMuch); return false; }
                catch (InvalidOperationException) { }
                return true;
            });
        }

        private static void CheckGenericVectorProbe(string scriptPath)
        {
            Check("VECTOR-prototype-reference", () =>
            {
                var zones = new[] {
                    new NoiseFieldColorOwnership.Zone { CenterX=-.2f,CenterY=.05f,HalfX=.45f,HalfY=.27f,Inverted=false,Active=true,YScale=1,Opacity=1,Angle=.23f,Red=0,Green=1,Blue=0 },
                    new NoiseFieldColorOwnership.Zone { CenterX=.3f,CenterY=-.1f,HalfX=.35f,HalfY=.18f,Inverted=true,Active=true,YScale=1,Opacity=1,Angle=0,Red=0,Green=0,Blue=1 },
                    new NoiseFieldColorOwnership.Zone { CenterX=.05f,CenterY=.1f,HalfX=.28f,HalfY=.22f,Inverted=false,Active=false,YScale=1,Opacity=.35f,Angle=0,Red=1,Green=1,Blue=1 },
                    new NoiseFieldColorOwnership.Zone { CenterX=-.3f,CenterY=-.12f,HalfX=.24f,HalfY=.20f,Inverted=true,Active=false,YScale=1,Opacity=.65f,Angle=0,Red=.2f,Green=.4f,Blue=.9f },
                    new NoiseFieldColorOwnership.Zone { CenterX=.05f,CenterY=-.05f,HalfX=.21f,HalfY=.13f,Inverted=false,Active=false,YScale=1,Opacity=1,Angle=0,Red=.9f,Green=.2f,Blue=.7f },
                    new NoiseFieldColorOwnership.Zone { CenterX=.15f,CenterY=.08f,HalfX=.17f,HalfY=.14f,Inverted=true,Active=false,YScale=1,Opacity=.4f,Angle=0,Red=.3f,Green=.9f,Blue=.2f }
                };
                var inputs = zones.Select(z => new { centerX=z.CenterX,centerY=z.CenterY,halfX=z.HalfX,halfY=z.HalfY,
                    inverted=z.Inverted,active=z.Active,yScale=z.YScale,opacity=z.Opacity,angle=z.Angle,
                    red=z.Red,green=z.Green,blue=z.Blue }).ToArray();
                var engine = new Jint.Engine(o => {
                    o.Interop.Enabled=false; o.Interop.AllowWrite=false; o.Host.StringCompilationAllowed=false;
                    o.TimeoutInterval(TimeSpan.FromSeconds(5)); o.MaxStatements(200000); o.LimitRecursion(64);
                    o.Strict(); o.Constraints.StackOverflowGuard=true;
                });
                engine.Execute(File.ReadAllText(scriptPath));
                foreach (var size in new[] { new[] {64,36}, new[] {62,34}, new[] {400,224} })
                {
                    int w=size[0], h=size[1]; float aspect=16f/9f;
                    var service=new VectorCompute(w,h,aspect);
                    engine.SetValue("__compute",new Func<string,string>(service.Apply));
                    engine.SetValue("__input", Newtonsoft.Json.JsonConvert.SerializeObject(new {width=w,height=h,aspect,defaultRgb=0xff5454,zones=inputs}));
                    service.Begin(System.Threading.CancellationToken.None);
                    var ids=(object[])engine.Evaluate("computeVector(JSON.parse(__input))").ToObject();
                    var actualA=service.Result(Convert.ToInt32(ids[0])); var actualB=service.Result(Convert.ToInt32(ids[1]));
                    var reference=NoiseFieldColorOwnership.Build(w,h,aspect,zones,0xff5454);
                    if (!actualA.SequenceEqual(reference.Active) || !actualB.SequenceEqual(reference.Disabled))
                    { Row("VECTOR-prototype-difference", "INFO", $"{w}x{h}"); return false; }
                    if (w == 400)
                    {
                        for(int i=0;i<2;i++){service.Begin(System.Threading.CancellationToken.None);engine.Evaluate("computeVector(JSON.parse(__input))");}
                        var watch=System.Diagnostics.Stopwatch.StartNew();
                        const int iterations=10;
                        for(int i=0;i<iterations;i++){service.Begin(System.Threading.CancellationToken.None);engine.Evaluate("computeVector(JSON.parse(__input))");}
                        watch.Stop();
                        Row("VECTOR-prototype-mono-time", "INFO", $"{watch.Elapsed.TotalMilliseconds/iterations:F2} ms/job, 400x224, six areas");
                        var nativeWorkspace=new NoiseFieldColorOwnership.Workspace();
                        for(int i=0;i<2;i++)NoiseFieldColorOwnership.Build(w,h,aspect,zones,nativeWorkspace,0xff5454);
                        watch.Restart();
                        for(int i=0;i<iterations;i++)NoiseFieldColorOwnership.Build(w,h,aspect,zones,nativeWorkspace,0xff5454);
                        watch.Stop();
                        Row("VECTOR-native-mono-time", "INFO", $"{watch.Elapsed.TotalMilliseconds/iterations:F2} ms/job, same input");
                    }
                }
                var random=new System.Random(684231);
                var randomized=new VectorCompute(41,27,1.6f);
                engine.SetValue("__compute",new Func<string,string>(randomized.Apply));
                for(int test=0;test<80;test++)
                {
                    var sample=new NoiseFieldColorOwnership.Zone[random.Next(0,9)];
                    for(int i=0;i<sample.Length;i++)
                    {
                        float opacity = new[] {0f,.1f,.35f,.5f,1f}[random.Next(5)];
                        sample[i]=new NoiseFieldColorOwnership.Zone {
                            CenterX=(float)(random.NextDouble()*2.6-1.3),CenterY=(float)(random.NextDouble()*1.8-.9),
                            HalfX=(float)(random.NextDouble()*.8),HalfY=(float)(random.NextDouble()*.6),
                            YScale=(float)(random.NextDouble()*1.8+.1),Angle=(float)(random.NextDouble()*6.2-3.1),
                            Opacity=opacity,Active=random.Next(2)==0,Inverted=random.Next(2)==0,
                            Red=(float)random.NextDouble(),Green=(float)random.NextDouble(),Blue=(float)random.NextDouble() };
                    }
                    var values=sample.Select(z=>new {centerX=z.CenterX,centerY=z.CenterY,halfX=z.HalfX,halfY=z.HalfY,
                        inverted=z.Inverted,active=z.Active,yScale=z.YScale,opacity=z.Opacity,angle=z.Angle,
                        red=z.Red,green=z.Green,blue=z.Blue}).ToArray();
                    engine.SetValue("__input",Newtonsoft.Json.JsonConvert.SerializeObject(new {width=41,height=27,aspect=1.6f,defaultRgb=0x12ab34,zones=values}));
                    randomized.Begin(System.Threading.CancellationToken.None);
                    var ids=(object[])engine.Evaluate("computeVector(JSON.parse(__input))").ToObject();
                    var expected=NoiseFieldColorOwnership.Build(41,27,1.6f,sample,0x12ab34);
                    if(!randomized.Result(Convert.ToInt32(ids[0])).SequenceEqual(expected.Active) ||
                       !randomized.Result(Convert.ToInt32(ids[1])).SequenceEqual(expected.Disabled))
                    { Row("VECTOR-prototype-difference", "INFO", "random case " + test); return false; }
                }
                Row("VECTOR-prototype-random", "PASS", "80 deterministic cases including zero opacity and empty regions");
                return true;
            });
        }

        private static IEnumerator CheckRealFieldBundle()
        {
            if (!EventRegistry.TryGetById("pev.noise-field", out var manifest))
            { Row("GPU-real-bundle-assets", "FAIL", "event not registered"); yield break; }
            bool assets = true;
            foreach (var item in manifest.ShaderAssets)
            {
                string error;
                if (item.Key == "compute")
                {
                    if (ShaderBundleCache.LoadCompute(manifest, item.Value, out error) == null)
                    { assets = false; Row("GPU-real-asset-" + item.Key, "FAIL", error); }
                }
                else if (ShaderBundleCache.Load(manifest, item.Value, out error) == null)
                { assets = false; Row("GPU-real-asset-" + item.Key, "FAIL", error); }
            }
            Row("GPU-real-bundle-assets", assets && manifest.ShaderAssets.Count == 11 ? "PASS" : "FAIL");
            if (!assets) yield break;
            Check("GPU-identical-bundle-sharing", () =>
            {
                string json = System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, "event.json"));
                var clone = EventManifest.Parse(json, manifest.SourcePath, out _);
                if (clone == null) return false;
                var first = ShaderBundleCache.LoadCompute(manifest, manifest.ShaderAssets["compute"], out _);
                var second = ShaderBundleCache.LoadCompute(clone, clone.ShaderAssets["compute"], out _);
                return first != null && first == second;
            });
            yield return CheckRuntimeBindingsSafe(manifest);
            yield return CheckPackageImages(manifest);
            Check("GPU-package-graph-parity", () => CheckGpuGraphParity(manifest));
            Check("GPU-package-graph-limits", () => CheckGpuGraphLimits(manifest));
            Check("GPU-package-record-layout", () => CheckGpuPackageRecordLayout(manifest));
            Check("VECTOR-texture-declaration", () => CheckVectorTextureDefinition(manifest));
            yield return CheckVectorTextureRemapSafe(manifest);
            Check("GPU-package-render-plan", () => CheckRenderPlanProgram(manifest));
            Check("GPU-package-group-commands", () => CheckGroupRenderCommands(manifest));
            yield return CheckGroupRunnerScaleSafe(manifest);
            Check("GPU-package-compositor-limits", () => CheckCompositorLimits(manifest));
            Check("PIXEL-package-bake-plan-parity", () => CheckBakePlan(manifest));
            Check("GPU-package-contacts-parity", () => CheckContactControllerParity(manifest));
            Check("GPU-package-contact-capacity", () => CheckContactCapacity(manifest));
            Check("GPU-contact-controller-sandbox", CheckControllerSandbox);
            yield return CheckCompositorParitySafe(manifest);

            var args = new Dictionary<string, object>
            {
                ["__instance"] = "review/real-gpu", ["__manifest"] = manifest,
                ["relativeTo"] = "Camera", ["size"] = new[] { 2f, 2f },
                ["frameCount"] = 2, ["color"] = "ff5454", ["depth"] = 0
            };
            NoiseFieldGpuCoordinator coordinator = null;
            try
            {
                NoiseFieldRenderer.Set("field", args, out _);
                coordinator = new NoiseFieldGpuCoordinator(manifest);
            }
            catch (Exception e) { Row("GPU-real-create", "FAIL", e.ToString()); }
            if (coordinator == null) { NoiseFieldRenderer.DestroyAll(); yield break; }
            bool captureReady = false, noCaptureReady = false;
            string renderError = null;
            var entries = NoiseFieldRenderer.AllEntries();
            float deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline && (!captureReady || !noCaptureReady))
            {
                try
                {
                    var camera = GameCamera.Current;
                    captureReady |= coordinator.Render(entries, camera, 1f, true, Color.black);
                    if (captureReady) noCaptureReady |= coordinator.Render(entries, camera, 1f, false, Color.black);
                }
                catch (Exception e) { renderError = e.ToString(); break; }
                yield return null;
            }
            Row("GPU-real-coordinator-capture", captureReady ? "PASS" : "FAIL", renderError);
            Row("GPU-real-coordinator-no-capture", noCaptureReady ? "PASS" : "FAIL", renderError);
            coordinator.Dispose();
            NoiseFieldRenderer.DestroyAll();
            yield return CheckRealFieldDepthSafe(manifest);
            yield return CheckRealFieldDriver(manifest);
            yield return CheckRealGpuFailureFallback(manifest);
        }

        private static IEnumerator CheckRuntimeBindingsSafe(EventManifest manifest)
        {
            var probe = CheckRuntimeBindings(manifest);
            while (true)
            {
                bool next;
                try { next = probe.MoveNext(); }
                catch (Exception e) { Row("GPU-runtime-binding-renames", "FAIL", e.ToString()); yield break; }
                if (!next) yield break;
                yield return probe.Current;
            }
        }

        private static IEnumerator CheckRuntimeBindings(EventManifest original)
        {
            string json = System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(original.SourcePath, "event.json"));
            var manifest = EventManifest.Parse(json, original.SourcePath, out string error);
            if (manifest == null) throw new InvalidOperationException(error);
            foreach (string binding in new[] { "renderGraph", "renderPlan", "inputController", "colorProgram", "framePlan", "frameProgram", "frameKey" })
            {
                var resources = binding == "renderGraph" ? manifest.ComputeGraphs : binding == "frameKey" ? manifest.KeySpecs : manifest.VectorPrograms;
                string old = manifest.RuntimeBindings[binding], renamed = "custom_" + binding;
                string path = resources[old]; resources.Remove(old); resources.Add(renamed, path);
                manifest.RuntimeBindings[binding] = renamed;
            }
            var request = Request(); request.Provider = manifest; request.EvolveSpeed = 24f;
            var task = System.Threading.Tasks.Task.Run(() =>
            {
                var plan = PackageFrameProgram.Prepare(request.Provider, request, "framePlan", System.Threading.CancellationToken.None);
                var frames = new Color32[2][];
                PackageFrameProgram.Render(request.Provider, request, "frameProgram", frames, 32, 32, System.Threading.CancellationToken.None);
                return plan.Frames == 2 && frames.All(f => f != null && f.Length == 1024) && NoiseTextureFactory.Key(request).Length > 0;
            });
            var go = new GameObject("PEV.ReviewRenamedBindings");
            RenderTexture target = null;
            NoiseFieldGpuCoordinator coordinator = null;
            try
            {
                var camera = go.AddComponent<Camera>(); camera.enabled = false; camera.orthographic = true;
                camera.orthographicSize = 1; camera.aspect = 1; go.transform.position = new Vector3(0, 0, -10);
                target = new RenderTexture(64, 64, 0); target.Create(); camera.targetTexture = target;
                coordinator = new NoiseFieldGpuCoordinator(manifest);
                var entry = new NoiseFieldRenderer.Entry { Provider = manifest, Visible = true, Renderable = true,
                    Opacity = 1, FillColor = Color.blue, SortingLayerID = 0, UnityLayer = 31,
                    World = new Placement.Frame { Size = new Vector2(1, 1) } };
                bool ready = false; float deadline = Time.realtimeSinceStartup + 15f;
                var settings = new Dictionary<string, bool> { ["captureScene"] = false };
                while ((!ready || !task.IsCompleted) && Time.realtimeSinceStartup < deadline)
                {
                    if (!ready) ready = coordinator.Render(new[] { entry }, camera, 0, settings, Color.black);
                    yield return null;
                }
                Row("GPU-runtime-binding-renames", ready && task.IsCompleted && !task.IsFaulted && task.Result ? "PASS" : "FAIL",
                    "renamed graph, input controller, color worker, CPU plan, CPU program and cache key; " + task.Exception);
            }
            finally
            {
                coordinator?.Dispose();
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                UnityEngine.Object.Destroy(go);
            }
        }

        private static bool CheckGpuPackageRecordLayout(EventManifest manifest)
        {
            var bytes = EventLoader.ReadResource(manifest.SourcePath, manifest.ComputeGraphs["coverage"], 131072);
            var root = JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            var geometry = ((JArray)root["resources"]).OfType<JObject>().Single(x => x.Value<string>("name") == "geometry");
            var fields = (JArray)geometry["fields"];
            geometry["fields"] = new JArray(fields.Reverse().Select(x => x.DeepClone()));
            var parsed = GpuGraphDefinition.Parse(System.Text.Encoding.UTF8.GetBytes(root.ToString()));
            var record = parsed.Resources["geometry"];
            if (record.Field("centerX", "float") != 7 || record.Field("flags", "uint") != 0) return false;
            ((JArray)geometry["fields"]).RemoveAt(0);
            try { GpuGraphDefinition.Parse(System.Text.Encoding.UTF8.GetBytes(root.ToString())); return false; }
            catch (ArgumentException) { }
            return true;
        }

        private static IEnumerator CheckPackageImages(EventManifest manifest)
        {
            // Exercise cancellation before background decode finishes, then reload all assets.
            foreach (var role in manifest.ImageAssets.Keys) PackageImageCache.TryGet(manifest, role, out _, out _);
            PackageImageCache.Clear();
            float deadline = Time.realtimeSinceStartup + 10f;
            bool ready = false; string error = null;
            while (!ready && error == null && Time.realtimeSinceStartup < deadline)
            {
                ready = true;
                foreach (var role in manifest.ImageAssets.Keys)
                {
                    bool loaded = PackageImageCache.TryGet(manifest, role, out _, out var failure);
                    if (failure != null) error = failure;
                    ready &= loaded;
                }
                if (!ready && error == null) yield return null;
            }
            if (!ready) { Row("GPU-package-image-cache", "FAIL", error ?? "image load timed out"); yield break; }
            foreach (var role in manifest.ImageAssets.Keys)
            {
                if (!PackageImageCache.TryGet(manifest, role, out var actual, out error))
                { Row("GPU-package-image-cache", "FAIL", error); yield break; }
                var reference = ReferenceNoiseTextures.Load(manifest, role, out error);
                if (reference == null || actual.width != reference.width || actual.height != reference.height ||
                    actual.wrapMode != reference.wrapMode || actual.filterMode != reference.filterMode ||
                    !actual.GetPixels32().SequenceEqual(reference.GetPixels32()))
                { Row("GPU-package-image-cache", "FAIL", "image mismatch: " + role + "; " + error); yield break; }
            }
            string json = System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, "event.json"));
            var clone = EventManifest.Parse(json, manifest.SourcePath, out error);
            if (clone == null) { Row("GPU-package-image-cache", "FAIL", error); yield break; }
            clone.ImageAssets["displacement"].FlipY = false;
            Texture2D independent = null;
            deadline = Time.realtimeSinceStartup + 10f;
            while (!PackageImageCache.TryGet(clone, "displacement", out independent, out error) && error == null && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (independent == null || !PackageImageCache.TryGet(manifest, "displacement", out var original, out error) || independent == original)
            { Row("GPU-package-image-cache", "FAIL", "package instances did not retain independent sampling"); yield break; }
            var firstPixels = original.GetPixels32(); var secondPixels = independent.GetPixels32();
            for (int y = 0; y < original.height; y++)
                for (int x = 0; x < original.width; x++)
                    if (!firstPixels[y * original.width + x].Equals(secondPixels[(original.height - 1 - y) * original.width + x]))
                    { Row("GPU-package-image-cache", "FAIL", "independent image orientation differs"); yield break; }
            Row("GPU-package-image-cache", "PASS", "asynchronous decode, cancellation and byte-identical textures");
        }

        private static bool CheckBakePlan(EventManifest manifest)
        {
            var bytes = EventLoader.ReadResource(manifest.SourcePath, manifest.VectorPrograms["bakePlan"], 262144);
            var controller = new JsonController(System.Text.Encoding.UTF8.GetString(bytes));
            var random = new System.Random(937);
            var request = Request(); request.Provider = manifest;
            for (int i = 0; i < 250; i++)
            {
                request.WidthWorld = .01f + (float)random.NextDouble() * 20;
                request.HeightWorld = .01f + (float)random.NextDouble() * 20;
                request.Margin = (float)random.NextDouble() * 2f;
                request.TargetPixelWidth = i % 3 == 0 ? (i % 13) * 96 + 48 : (float)random.NextDouble() * 1200;
                request.FrameCount = 1 + random.Next(140);
                request.SampleRate = .1f + (float)random.NextDouble() * 9f;
                var expected = LegacyBakePlanReference.Prepare(request);
                var input = Newtonsoft.Json.JsonConvert.SerializeObject(new { widthWorld = request.WidthWorld, heightWorld = request.HeightWorld,
                    margin = request.Margin, targetPixelWidth = request.TargetPixelWidth, frameCount = request.FrameCount, sampleRate = request.SampleRate });
                var actual = PackageFrameProgram.ValidatePlan(controller.Update(input));
                if (actual.Width != expected.Width || actual.Height != expected.Height || actual.Frames != expected.Frames)
                    throw new InvalidOperationException($"case {i}: {actual.Width}x{actual.Height}x{actual.Frames} vs {expected.Width}x{expected.Height}x{expected.Frames}");
            }
            var integrated = PackageFrameProgram.Prepare(request.Provider, request, "framePlan", System.Threading.CancellationToken.None);
            var last = LegacyBakePlanReference.Prepare(request);
            if (integrated.Width != last.Width || integrated.Height != last.Height || integrated.Frames != last.Frames) return false;
            foreach (var invalid in new[] { "{}", "{\"width\":4096,\"height\":4096,\"frames\":2}",
                "{\"width\":1,\"height\":1,\"frames\":0}", "{\"width\":1.5,\"height\":1,\"frames\":2}" })
            {
                try { PackageFrameProgram.ValidatePlan(JObject.Parse(invalid)); return false; }
                catch (ArgumentException) { }
            }
            return true;
        }

        private static IEnumerator CheckRealGpuFailureFallback(EventManifest manifest)
        {
            string json = System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, "event.json"));
            var broken = EventManifest.Parse(json, manifest.SourcePath, out string error);
            if (broken == null) { Row("GPU-real-failure-cpu-fallback", "FAIL", error); yield break; }
            broken.Id = "pev.review-fault";
            broken.ComputeGraphs["coverage"] = "missing-test-graph.json";
            var args = new Dictionary<string, object>
            {
                ["__instance"] = "review/gpu-failure", ["__manifest"] = broken, ["relativeTo"] = "Camera",
                ["size"] = new[] { 2f, 2f }, ["frameCount"] = 2, ["frame"] = 0, ["depth"] = 0
            };
            try
            {
                NoiseFieldRenderer.DestroyAll();
                NoiseFieldRenderer.Set("field", args, out _);
                float deadline = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    NoiseTextureFactory.Pump(2f);
                    NoiseFieldRenderer.Set("field", args, out _);
                    var entry = NoiseFieldRenderer.AllEntries().FirstOrDefault();
                    if (NoiseFieldGpuDriver.TestFaults.Contains(broken) && entry?.GameObject != null &&
                        entry.GameObject.GetComponent<SpriteRenderer>()?.sprite != null) break;
                    yield return null;
                }
                var result = NoiseFieldRenderer.AllEntries().FirstOrDefault();
                bool ok = NoiseFieldGpuDriver.TestFaults.Contains(broken) && result?.GameObject != null &&
                    result.GameObject.GetComponent<SpriteRenderer>()?.sprite != null && !NoiseFieldRenderer.IsGpuProviderActive(broken);
                Row("GPU-real-failure-cpu-fallback", ok ? "PASS" : "FAIL", "broken GPU graph must display the package CPU bake");
            }
            finally { NoiseFieldRenderer.DestroyAll(); NoiseFieldGpuDriver.TestFaults.Remove(broken); }
        }

        private static bool CheckCompositorLimits(EventManifest manifest)
        {
            var data = EventLoader.ReadResource(manifest.SourcePath, manifest.ComputeGraphs["coverage"], 131072);
            var root = JObject.Parse(System.Text.Encoding.UTF8.GetString(data));
            var compositor = CompositorDefinition.Parse(data);
            if (compositor.Layers.Count != 2 || compositor.Bindings.Count == 0) return false;
            root["compositor"]["layers"][0]["bindings"][0]["type"] = "nativeCode";
            try { CompositorDefinition.Parse(System.Text.Encoding.UTF8.GetBytes(root.ToString())); return false; }
            catch (ArgumentException) { }
            return true;
        }

        private static bool CheckVectorTextureDefinition(EventManifest manifest)
        {
            var bytes = EventLoader.ReadResource(manifest.SourcePath, manifest.ComputeGraphs["coverage"], 131072);
            byte[] changed = RemapVectorTextureGraph(bytes);
            var root = JObject.Parse(System.Text.Encoding.UTF8.GetString(changed));
            var spec = (JObject)root["vectorTextures"];
            var graph = GpuGraphDefinition.Parse(changed);
            var definition = VectorTextureDefinition.Parse(changed, graph);
            if (definition.Role("primary") != "renamedOutput" || definition.Outputs[0].Input != "renamedInput" ||
                definition.Outputs[0].Target != "renamedTarget") return false;
            spec["outputs"][1]["name"] = "renamedOutput";
            try { VectorTextureDefinition.Parse(System.Text.Encoding.UTF8.GetBytes(root.ToString()), graph); return false; }
            catch (ArgumentException) { }
            return true;
        }
        private static byte[] RemapVectorTextureGraph(byte[] bytes)
        {
            var root = JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            var spec = (JObject)root["vectorTextures"];
            spec["outputs"][0]["name"] = "renamedOutput";
            spec["roles"]["primary"] = "renamedOutput";
            spec["outputs"][0]["transform"]["input"] = "renamedInput";
            spec["outputs"][0]["transform"]["output"] = "renamedTarget";
            root["entries"]["colors"][0]["textures"]["_RawActiveColors"] = "$renamedInput";
            root["entries"]["colors"][0]["textures"]["_WarpedActiveColors"] = "$renamedTarget";
            return System.Text.Encoding.UTF8.GetBytes(root.ToString());
        }
        private static IEnumerator CheckVectorTextureRemapSafe(EventManifest manifest)
        {
            var probe = CheckVectorTextureRemap(manifest);
            while (true)
            {
                bool next;
                try { next = probe.MoveNext(); }
                catch (Exception e) { Row("VECTOR-texture-remap-parity", "FAIL", e.ToString()); yield break; }
                if (!next) yield break;
                yield return probe.Current;
            }
        }
        private static IEnumerator CheckVectorTextureRemap(EventManifest manifest)
        {
            var bytes = EventLoader.ReadResource(manifest.SourcePath, manifest.ComputeGraphs["coverage"], 131072);
            var alternate = RemapVectorTextureGraph(bytes);
            var graph = GpuGraphDefinition.Parse(bytes); var changed = GpuGraphDefinition.Parse(alternate);
            var shader = ShaderBundleCache.LoadCompute(manifest, manifest.ShaderAssets["compute"], out string error);
            var displacement = ReferenceNoiseTextures.Load(manifest, "displacement", out error);
            var touch = ReferenceNoiseTextures.Load(manifest, "touch", out error);
            if (shader == null || displacement == null || touch == null) throw new InvalidOperationException(error);
            var images = new Dictionary<string, Texture2D> { ["displacement"] = displacement, ["touch"] = touch };
            using (var firstMasks = new NoiseFieldGpuMasks(shader, graph))
            using (var secondMasks = new NoiseFieldGpuMasks(shader, changed))
            using (var first = new NoiseFieldGpuPalette(manifest, VectorTextureDefinition.Parse(bytes, graph)))
            using (var second = new NoiseFieldGpuPalette(manifest, VectorTextureDefinition.Parse(alternate, changed)))
            {
                var areas = new[] { new NoiseFieldGpuMasks.Area { Center = Vector2.zero, HalfSize = new Vector2(.5f, .4f),
                    YScale = 1, Opacity = 1, Flags = 1 } };
                var contacts = Array.Empty<NoiseFieldGpuMasks.Touch>();
                firstMasks.Render(areas, 1, 128, 128, 1, .37f, images, contacts, 0);
                secondMasks.Render(areas, 1, 128, 128, 1, .37f, images, contacts, 0);
                var zones = new[] { new NoiseFieldColorOwnership.Zone { CenterX = 0, CenterY = 0, HalfX = .5f, HalfY = .4f,
                    YScale = 1, Opacity = 1, Active = true, Red = .2f, Green = .8f, Blue = .3f } };
                bool readyFirst = false, readySecond = false; float deadline = Time.realtimeSinceStartup + 10f;
                while ((!readyFirst || !readySecond) && Time.realtimeSinceStartup < deadline)
                {
                    readyFirst = first.Update(firstMasks, 1, zones, 0xff5454); readySecond = second.Update(secondMasks, 1, zones, 0xff5454);
                    yield return null;
                }
                bool matches = readyFirst && readySecond && first.Active is RenderTexture a && second.Active is RenderTexture b &&
                    ReadGpuPixels(a).SequenceEqual(ReadGpuPixels(b)) && first.Disabled is Texture2D c && second.Disabled is Texture2D d &&
                    c.GetPixels32().SequenceEqual(d.GetPixels32());
                Row("VECTOR-texture-remap-parity", matches ? "PASS" : "FAIL", "renamed output and compute texture bindings, byte-for-byte GPU outputs");
            }
        }

        private static IEnumerator CheckCompositorParitySafe(EventManifest manifest)
        {
            var probe = CheckCompositorParity(manifest);
            while (true)
            {
                bool more;
                try { more = probe.MoveNext(); }
                catch (Exception e) { Row("GPU-compositor-parity", "FAIL", e.ToString()); yield break; }
                if (!more) yield break;
                yield return probe.Current;
            }
        }

        private static IEnumerator CheckCompositorParity(EventManifest manifest)
        {
            GameObject cameraObject = null;
            RenderTexture target = null;
            Texture2D read = null, activeColors = null, auxiliaryColors = null;
            NoiseFieldGpuMasks masks = null, genericMasks = null;
            LegacyGpuLayerReference legacy = null;
            NoiseFieldGpuLayer current = null;
            var prior = RenderTexture.active;
            try
            {
                byte[] graphData = EventLoader.ReadResource(manifest.SourcePath, manifest.ComputeGraphs["coverage"], 131072);
                var graph = GpuGraphDefinition.Parse(graphData);
                var remapped = JObject.Parse(System.Text.Encoding.UTF8.GetString(graphData));
                remapped["uniforms"]["_NoiseDimensions"]["input"] = "imageADimensions";
                remapped["uniforms"]["_TouchTextureDimensions"]["input"] = "imageDDimensions";
                foreach (var entry in ((JObject)remapped["entries"]).Properties())
                    foreach (var pass in (JArray)entry.Value)
                        if (pass["textures"] is JObject bindings)
                            foreach (var binding in bindings.Properties().ToArray())
                            {
                                if (binding.Value.Value<string>() == "$displacement") binding.Value = "$imageA";
                                if (binding.Value.Value<string>() == "$touch") binding.Value = "$imageD";
                            }
                remapped["outputs"] = new JObject { ["shape"] = "mainOut", ["detail"] = "auxOut" };
                var renamedInputs = new Dictionary<string, string> { ["image_displacement"] = "image_imageA", ["image_spark"] = "image_imageB",
                    ["image_noise"] = "image_imageC", ["output_main"] = "output_shape", ["output_auxiliary"] = "output_detail",
                    ["output_main_texelSize"] = "output_shape_texelSize" };
                foreach (var binding in (JArray)remapped["compositor"]["bindings"])
                    if (renamedInputs.TryGetValue(binding.Value<string>("input"), out var replacement)) binding["input"] = replacement;
                foreach (var layer in (JArray)remapped["compositor"]["layers"])
                    foreach (var variant in (JArray)layer["variants"])
                        if (variant["when"] is JObject when && when["setting_captureScene"] != null)
                        {
                            when["setting_customOption"] = when["setting_captureScene"];
                            when.Remove("setting_captureScene");
                        }
                byte[] alternateData = System.Text.Encoding.UTF8.GetBytes(remapped.ToString(Newtonsoft.Json.Formatting.None));
                var alternateGraph = GpuGraphDefinition.Parse(alternateData);
                var compositor = CompositorDefinition.Parse(alternateData);
                var shader = ShaderBundleCache.LoadCompute(manifest, manifest.ShaderAssets["compute"], out string error);
                if (shader == null) throw new InvalidOperationException(error);
                var displacement = ReferenceNoiseTextures.Load(manifest, "displacement", out error);
                var spark = ReferenceNoiseTextures.Load(manifest, "spark", out error);
                var noise = ReferenceNoiseTextures.Load(manifest, "noise", out error);
                var touch = ReferenceNoiseTextures.Load(manifest, "touch", out error);
                if (displacement == null || spark == null || noise == null || touch == null) throw new InvalidOperationException(error);
                cameraObject = new GameObject("PEV.CompositorParityCamera");
                var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false; camera.orthographic = true;
                camera.orthographicSize = 1f; camera.aspect = 1f; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color32(28, 52, 180, 255); cameraObject.transform.position = new Vector3(0, 0, -10);
                target = new RenderTexture(128, 128, 0, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
                if (!target.Create()) throw new InvalidOperationException("Cannot create compositor parity target");
                camera.targetTexture = target;
                read = new Texture2D(128, 128, TextureFormat.RGBA32, false, true);
                masks = new NoiseFieldGpuMasks(shader, graph);
                genericMasks = new NoiseFieldGpuMasks(shader, alternateGraph);
                var areas = new[] {
                    new NoiseFieldGpuMasks.Area { Center = new Vector2(-.2f,0), HalfSize = new Vector2(.5f,.6f), YScale = 1, Opacity = 1, Flags = 1 },
                    new NoiseFieldGpuMasks.Area { Center = new Vector2(.35f,0), HalfSize = new Vector2(.25f,.35f), YScale = 1, Opacity = .8f, Flags = 4 }
                };
                var touches = new[] { new NoiseFieldGpuMasks.Touch { Center = new Vector2(.5f,.5f), Scale = 1f } };
                var images = new Dictionary<string, Texture2D> { ["displacement"] = displacement, ["spark"] = spark, ["noise"] = noise, ["touch"] = touch };
                var renamedImages = new Dictionary<string, Texture2D> { ["imageA"] = displacement, ["imageB"] = spark, ["imageC"] = noise, ["imageD"] = touch };
                masks.Render(areas, areas.Length, 128, 128, 1f, 1f, images, touches, 1);
                genericMasks.Render(areas, areas.Length, 128, 128, 1f, 1f, renamedImages, touches, 1);
                activeColors = new Texture2D(masks.Width, masks.Height, TextureFormat.RGBA32, false, true);
                auxiliaryColors = new Texture2D(masks.Width, masks.Height, TextureFormat.RGBA32, false, true);
                activeColors.SetPixels32(Enumerable.Repeat(new Color32(20, 220, 80, 255), masks.Width * masks.Height).ToArray());
                auxiliaryColors.SetPixels32(Enumerable.Repeat(new Color32(30, 70, 220, 255), masks.Width * masks.Height).ToArray());
                activeColors.Apply(false, false); auxiliaryColors.Apply(false, false);
                legacy = new LegacyGpuLayerReference(manifest, CompositorDefinition.Parse(graphData), 0, 0, 30);
                current = new NoiseFieldGpuLayer(manifest, compositor, 0, 0, 31);
                var positions = new Vector4[10];
                positions[0] = NoiseFieldGpuLayer.TouchPosition(new Vector2(.5f, .5f), 128, 128);
                Func<int, Color32[]> capture = layer =>
                {
                    camera.cullingMask = unchecked((int)(1u << layer)); camera.Render();
                    RenderTexture.active = target; read.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); read.Apply(false, false);
                    RenderTexture.active = prior; return read.GetPixels32();
                };
                var backgroundOnly = capture(29);
                int scenarios = 0, visibleScenarios = 0;
                for (int i = 0; i < 24; i++)
                {
                    bool colored = (i & 1) != 0, captureScene = (i & 2) != 0, hover = (i & 4) != 0;
                    int layers = i / 8; bool showDisabled = layers != 1, showActive = layers != 2;
                    var first = colored ? activeColors : null; var second = colored ? auxiliaryColors : null;
                    legacy.Draw(camera, masks, displacement, spark, noise, 1f, 1f, captureScene, hover,
                        showDisabled, showActive, Color.black, positions, hover ? 1 : 0, first, second);
                    current.Draw(camera, genericMasks, renamedImages, 1f, 1f,
                        new Dictionary<string, bool> { ["customOption"] = captureScene }, hover,
                        showDisabled, showActive, Color.black, positions, hover ? 1 : 0, first, second);
                    yield return null;
                    var before = capture(30); var after = capture(31);
                    if (!before.SequenceEqual(backgroundOnly)) visibleScenarios++;
                    if (!before.SequenceEqual(after))
                    { Row("GPU-compositor-parity", "FAIL", $"scenario {i}, differing pixels {before.Zip(after, (a,b) => !a.Equals(b)).Count(x => x)}"); yield break; }
                    scenarios++;
                }
                Row("GPU-compositor-parity", scenarios == 24 && visibleScenarios == 24 ? "PASS" : "FAIL",
                    scenarios + " material and layer combinations; " + visibleScenarios + " visibly rendered; custom image/output/setting names");
            }
            finally
            {
                current?.Dispose(); legacy?.Dispose(); masks?.Dispose(); genericMasks?.Dispose(); RenderTexture.active = prior;
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                if (read != null) UnityEngine.Object.Destroy(read);
                if (activeColors != null) UnityEngine.Object.Destroy(activeColors);
                if (auxiliaryColors != null) UnityEngine.Object.Destroy(auxiliaryColors);
                if (cameraObject != null) UnityEngine.Object.Destroy(cameraObject);
            }
        }

        private static bool CheckRenderPlanProgram(EventManifest manifest)
        {
            if (!manifest.TryRuntimeResource("renderPlan", manifest.VectorPrograms, out var path)) return false;
            string source = System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, path, 262144));
            var items = new JArray();
            foreach (var state in new[] { "Active", "Ready", "Disabled", "unknown" })
                items.Add(new JObject { ["SortingLayer"] = 0, ["SortingOrder"] = 1, ["UnityLayer"] = 7, ["State"] = state,
                    ["Inverted"] = state == "Ready", ["HasDefault"] = true, ["Red"] = 1, ["Green"] = 0, ["Blue"] = 0,
                    ["DefaultRed"] = 1, ["DefaultGreen"] = 0, ["DefaultBlue"] = 0 });
            var result = new JsonController(source).Update(new JObject { ["Items"] = items }.ToString(Newtonsoft.Json.Formatting.None));
            var groups = (JArray)result["groups"];
            if (groups.Count != 1 || !groups[0].Value<bool>("primary") || !groups[0].Value<bool>("secondary") || groups[0].Value<bool>("colored")) return false;
            var rows = (JArray)groups[0]["data"];
            return rows.Count == 4 && rows[0][6].Value<int>() == 1 && rows[0][10].Value<bool>() &&
                rows[1][6].Value<int>() == 6 && !rows[1][10].Value<bool>() &&
                rows[2][6].Value<int>() == 0 && !rows[2][12].Value<bool>() && rows[3][6].Value<int>() == 1;
        }

        private static bool CheckGroupRenderCommands(EventManifest manifest)
        {
            if (!manifest.TryRuntimeResource("inputController", manifest.VectorPrograms, out var contactPath) ||
                !manifest.TryRuntimeResource("renderGroup", manifest.VectorPrograms, out var groupPath)) return false;
            string contacts = System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, contactPath, 262144));
            string groupSource = System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, groupPath, 262144));
            var expected = new FieldContactController(contacts, 10, 1024);
            var actual = new JsonController(contacts + "\n" + groupSource);
            var areas = new[] {
                new FieldContactController.Area { Center = Vector2.zero, HalfSize = new Vector2(.9f,.7f), Angle = .2f, Active = true, Visible = true },
                new FieldContactController.Area { Center = new Vector2(.15f,.05f), HalfSize = new Vector2(.12f,.15f), Angle = -.1f, Active = true, Visible = true, Inverted = true },
                new FieldContactController.Area { Center = new Vector2(.15f,.05f), HalfSize = new Vector2(.04f,.06f), Active = true, Visible = true, Inverted = true },
                new FieldContactController.Area { Center = Vector2.zero, HalfSize = new Vector2(2,2), Visible = true, Active = false }
            };
            var data = new JArray();
            foreach (var a in areas)
                data.Add(new JArray(a.Center.x, a.Center.y, a.HalfSize.x, a.HalfSize.y, a.Angle, 1f,
                    (a.Active ? 1 : 0) | (a.Inverted ? 2 : 0), .2f, .4f, .6f, a.Active, a.Inverted, a.Active, a.Inverted));
            var group = new JObject { ["data"] = data, ["primary"] = true, ["secondary"] = true, ["colored"] = true };
            var random = new System.Random(8219);
            var timing = new System.Diagnostics.Stopwatch(); double elapsed = 0;
            for (int frame = 0; frame < 200; frame++)
            {
                float time = frame < 130 ? frame * .013f : (frame - 130) * .02f;
                var points = Enumerable.Range(0, frame % 13).Select(i => new ViewportPoint {
                    Id = i, Position = new Vector2((float)random.NextDouble(), (float)random.NextDouble()) }).ToArray();
                expected.Update(areas, points, 16f / 9f, time);
                timing.Restart();
                var input = JObject.Parse(PublicFieldsSnapshot.Serialize(new GroupProgramProbe { Time = time, Aspect = 16f / 9f,
                    Capacity = 10, ViewWidth = 1600, ViewHeight = 900, Points = points, DefaultColor = "ff5454" }));
                if (frame == 0) input["Group"] = group.DeepClone();
                var result = actual.Update(input.ToString(Newtonsoft.Json.Formatting.None));
                timing.Stop(); elapsed += timing.Elapsed.TotalMilliseconds;
                var slots = result["buffers"]?["contacts"] as JArray;
                var positions = result["values"]?["contacts"] as JArray;
                var geometry = result["buffers"]?["geometry"] as JArray;
                if (slots == null || positions == null || geometry == null || geometry.Count != areas.Length ||
                    slots.Count != expected.Slots.Count || positions.Count != expected.Contacts.Count ||
                    result.Value<bool>("continuous") != expected.Visible || result["conditions"].Value<bool>("hover") != expected.Visible ||
                    !result["conditions"].Value<bool>("colored") || result["compute"].Value<int>("geometryCount") != areas.Length ||
                    result["values"].Value<int>("contactCount") != expected.Contacts.Count) return false;
                for (int i = 0; i < slots.Count; i++)
                    if (Mathf.Abs(slots[i][0].Value<float>() - expected.Slots[i].Position.x) > .00001f ||
                        Mathf.Abs(slots[i][1].Value<float>() - expected.Slots[i].Position.y) > .00001f ||
                        Mathf.Abs(slots[i][2].Value<float>() - expected.Slots[i].Scale) > .00001f) return false;
                for (int i = 0; i < positions.Count; i++)
                {
                    var expectedPosition = NoiseFieldGpuLayer.TouchPosition(expected.Contacts[i].Position, 1600, 900);
                    if (Mathf.Abs(positions[i][0].Value<float>() - expectedPosition.x) > .00001f ||
                        Mathf.Abs(positions[i][1].Value<float>() - expectedPosition.y) > .00001f) return false;
                }
                if (!(result["vector"]?["Items"] is JArray colors) || colors.Count != areas.Length ||
                    result["vector"].Value<int>("Options") != 0xff5454) return false;
            }
            Row("GPU-package-group-cost", "INFO", $"200 frames, four areas and up to twelve points: {elapsed / 200:F3} ms/frame");
            return true;
        }

        private static IEnumerator CheckGroupRunnerScaleSafe(EventManifest manifest)
        {
            var probe = CheckGroupRunnerScale(manifest);
            while (true)
            {
                bool next;
                try { next = probe.MoveNext(); }
                catch (Exception e) { Row("GPU-group-runner-scale", "FAIL", e.ToString()); yield break; }
                if (!next) yield break;
                yield return probe.Current;
            }
        }
        private static IEnumerator CheckGroupRunnerScale(EventManifest manifest)
        {
            if (!manifest.TryRuntimeResource("inputController", manifest.VectorPrograms, out var contactPath) ||
                !manifest.TryRuntimeResource("renderGroup", manifest.VectorPrograms, out var groupPath))
            { Row("GPU-group-runner-scale", "FAIL", "missing package programs"); yield break; }
            string source = System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, contactPath, 262144)) + "\n" +
                System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, groupPath, 262144));
            var data = new JArray();
            for (int i = 0; i < 1024; i++) data.Add(new JArray(0f, 0f, .5f, .5f, 0f, 1f, 1, .2f, .4f, .6f, true, false, true, false));
            var group = new JObject { ["data"] = data, ["primary"] = true, ["secondary"] = false, ["colored"] = true };
            var points = new[] { new ViewportPoint { Id = 1, Position = new Vector2(.5f, .5f) },
                new ViewportPoint { Id = 2, Position = new Vector2(.51f, .5f) } };
            var settings = new Dictionary<string, bool>();
            using (var runner = new GroupScriptRunner(source))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                bool ready = runner.Update(group, 0, 1, 10, 128, 128, "ff5454", points, settings, 1024, out var output);
                watch.Stop(); double worstMain = watch.Elapsed.TotalMilliseconds; bool initiallyAsync = !ready;
                float deadline = Time.realtimeSinceStartup + 10f;
                while (!ready && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    watch.Restart(); ready = runner.Update(group, 0, 1, 10, 128, 128, "ff5454", points, settings, 1024, out output); watch.Stop();
                    worstMain = Math.Max(worstMain, watch.Elapsed.TotalMilliseconds);
                }
                bool ok = initiallyAsync && ready && (output?["buffers"]?["geometry"] as JArray)?.Count == 1024 &&
                    (output?["vector"]?["Items"] as JArray)?.Count == 1024 && output["values"].Value<int>("contactCount") == 2;
                if (ok)
                {
                    var changed = (JObject)group.DeepClone(); changed["data"][0][0] = .1f;
                    ok &= runner.Update(changed, .2f, 1, 10, 128, 128, "ff5454", points, settings, 1024, out output) &&
                        output["buffers"]["geometry"][0][0].Value<float>() == 0;
                    deadline = Time.realtimeSinceStartup + 10f;
                    while (output["buffers"]["geometry"][0][0].Value<float>() == 0 && Time.realtimeSinceStartup < deadline)
                    {
                        yield return null;
                        watch.Restart(); runner.Update(changed, .2f, 1, 10, 128, 128, "ff5454", points, settings, 1024, out output); watch.Stop();
                        worstMain = Math.Max(worstMain, watch.Elapsed.TotalMilliseconds);
                    }
                    ok &= Mathf.Abs(output["buffers"]["geometry"][0][0].Value<float>() - .1f) < .00001f;
                }
                Row("GPU-group-runner-scale-cost", "INFO", $"1024 areas, two points; worst observed main-thread update {worstMain:F1} ms");
                Row("GPU-group-runner-scale", ok ? "PASS" : "FAIL", "background group commands retain the previous valid result");
            }
        }

        private static bool CheckContactControllerParity(EventManifest manifest)
        {
            string path = manifest.VectorPrograms["contacts"];
            string source = System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, path, 262144));
            var actual = new FieldContactController(source, 10, 1024);
            var expected = new NoiseFieldTouchHistory();
            var areas = new[] {
                new FieldContactController.Area { Center = Vector2.zero, HalfSize = new Vector2(.9f,.7f), Angle = .2f, Active = true, Visible = true },
                new FieldContactController.Area { Center = new Vector2(.15f,.05f), HalfSize = new Vector2(.12f,.15f), Angle = -.1f, Active = true, Visible = true, Inverted = true },
                new FieldContactController.Area { Center = new Vector2(.15f,.05f), HalfSize = new Vector2(.04f,.06f), Active = true, Visible = true, Inverted = true },
                new FieldContactController.Area { Center = Vector2.zero, HalfSize = new Vector2(2,2), Visible = true, Active = false }
            };
            var legacyAreas = areas.Select(a => new NoiseFieldTouchGeometry.Area { Center = a.Center, Size = a.HalfSize * 2,
                RotationDegrees = a.Angle * Mathf.Rad2Deg, Active = a.Active, Visible = a.Visible, Inverted = a.Inverted }).ToArray();
            var random = new System.Random(1731);
            double sumMs = 0, worstMs = 0;
            for (int frame = 0; frame < 200; frame++)
            {
                float time = frame < 130 ? frame * .013f : (frame - 130) * .02f;
                var points = new List<ViewportPoint>();
                for (int i = 0; i < frame % 13; i++)
                    points.Add(new ViewportPoint { Id = i, Position = new Vector2((float)random.NextDouble(), (float)random.NextDouble()) });
                var hits = points.Where(p => NoiseFieldTouchGeometry.Contains(legacyAreas,
                    new Vector2(p.Position.x * 2f - 1f, (p.Position.y * 2f - 1f) / (16f / 9f)))).ToArray();
                expected.Update(hits.Select(p => new NoiseFieldTouchHistory.Contact { Id = p.Id, Position = p.Position }).ToArray(), time);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                actual.Update(areas, points, 16f / 9f, time);
                watch.Stop(); sumMs += watch.Elapsed.TotalMilliseconds; worstMs = Math.Max(worstMs, watch.Elapsed.TotalMilliseconds);
                if (actual.Visible != expected.Visible || actual.Contacts.Count != Math.Min(hits.Length, 10))
                    throw new InvalidOperationException($"frame {frame} visible {actual.Visible}/{expected.Visible}, contacts {actual.Contacts.Count}/{hits.Length}");
                for (int i = 0; i < actual.Contacts.Count; i++) if (actual.Contacts[i].Id != hits[i].Id)
                    throw new InvalidOperationException($"frame {frame} contact {i}");
                int n = 0;
                for (int i = 0; i < NoiseFieldTouchHistory.Capacity; i++)
                {
                    var slot = expected.GetSlot(i);
                    if (slot.Scale <= 0) continue;
                    if (n >= actual.Slots.Count || Vector2.Distance(actual.Slots[n].Position, slot.Position) > .00001f ||
                        Mathf.Abs(actual.Slots[n].Scale - slot.Scale) > .00001f)
                        throw new InvalidOperationException($"frame {frame} slot {i}/{n} counts {actual.Slots.Count}, expected {slot.Position} scale {slot.Scale}");
                    n++;
                }
                if (n != actual.Slots.Count) throw new InvalidOperationException($"frame {frame} slot count {actual.Slots.Count}/{n}");
            }
            Row("GPU-contact-controller-cost", "INFO", $"200 frames, mean {sumMs / 200:F3}ms, worst {worstMs:F3}ms");
            return true;
        }

        private static bool CheckControllerSandbox()
        {
            var safe = new JsonController("function updateController(x){return {ok:typeof System==='undefined' && typeof Uint8Array==='undefined'};}");
            if (safe.Update("{}").Value<bool>("ok") != true) return false;
            foreach (var source in new[] { "function updateController(){return eval('1');}",
                "function updateController(){while(true){} return {};}", "function updateController(){return {x:'a'.repeat(20000)};}" })
            {
                try { new JsonController(source).Update("{}"); return false; }
                catch (Exception) { }
            }
            return true;
        }

        private static bool CheckContactCapacity(EventManifest manifest)
        {
            string path = manifest.VectorPrograms["contacts"];
            string source = System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, path, 262144));
            var controller = new FieldContactController(source, 3, 20);
            var areas = new[] { new FieldContactController.Area { Center = Vector2.zero, HalfSize = Vector2.one, Active = true, Visible = true } };
            var points = Enumerable.Range(0, 8).Select(i => new ViewportPoint { Id = i, Position = new Vector2(.5f, .5f) }).ToArray();
            controller.Update(areas, points, 1, 0); controller.Update(areas, points, 1, .2f);
            if (controller.Contacts.Count != 3 || controller.Slots.Count != 3) return false;
            var limited = new FieldContactController(source, 3, 5);
            try { limited.Update(areas, points, 1, 0); return false; }
            catch (ArgumentException) { }
            const string overflow = "function updateController(){return {visible:false,contacts:[],slots:[{x:0,y:0,scale:1},{x:0,y:0,scale:1},{x:0,y:0,scale:1},{x:0,y:0,scale:1}]};}";
            try { new FieldContactController(overflow, 3, 20).Update(areas, points, 1, 0); return false; }
            catch (ArgumentException) { }
            return true;
        }

        private static bool CheckGpuGraphLimits(EventManifest manifest)
        {
            var data = EventLoader.ReadResource(manifest.SourcePath, manifest.ComputeGraphs["coverage"], 131072);
            string source = System.Text.Encoding.UTF8.GetString(data);
            var invalid = new List<string> { "{}", source + "{}", source.Replace("\"divisor\": 8", "\"divisor\": 0"),
                source.Replace("\"format\": \"r32uint\"", "\"format\": \"unknown\""),
                source.Replace("\"capacity\": 10", "\"capacity\": 65"),
                source.Replace("\"_Areas\": \"geometry\"", "\"_Areas\": \"missing\"") };
            foreach (var text in invalid)
            {
                try { GpuGraphDefinition.Parse(System.Text.Encoding.UTF8.GetBytes(text)); return false; }
                catch (Exception) { }
            }
            var graph = GpuGraphDefinition.Parse(data);
            try { graph.Size(16384, 16384, out _, out _); return false; }
            catch (ArgumentOutOfRangeException) { }
            try { GpuGraphDefinition.Parse(new byte[131073]); return false; }
            catch (ArgumentException) { }
            return true;
        }

        private static bool CheckGpuGraphParity(EventManifest manifest)
        {
            var definition = GpuGraphDefinition.Parse(EventLoader.ReadResource(manifest.SourcePath, manifest.ComputeGraphs["coverage"], 131072));
            var shader = ShaderBundleCache.LoadCompute(manifest, manifest.ShaderAssets["compute"], out var error);
            if (shader == null) throw new InvalidOperationException(error);
            var noise = ReferenceNoiseTextures.Load(manifest, "displacement", out error);
            if (noise == null) throw new InvalidOperationException(error);
            var touchTexture = ReferenceNoiseTextures.Load(manifest, "touch", out error);
            if (touchTexture == null) throw new InvalidOperationException(error);
            LegacyFieldTextureSet.ProfileData(manifest).TryGetValue("glowWeights", out var weights);
            var areas = new[] {
                new NoiseFieldGpuMasks.Area { Center = new Vector2(-.15f,.05f), HalfSize = new Vector2(.55f,.3f), AngleRadians = .35f, YScale = 1f, Opacity = .75f, Flags = 1 },
                new NoiseFieldGpuMasks.Area { Center = new Vector2(.32f,-.12f), HalfSize = new Vector2(.25f,.4f), AngleRadians = -.17f, YScale = 1f, Opacity = 1f, Flags = 3 },
                new NoiseFieldGpuMasks.Area { Center = Vector2.zero, HalfSize = new Vector2(.2f,.2f), YScale = 1f, Opacity = .5f, Flags = 4 },
                new NoiseFieldGpuMasks.Area { Center = new Vector2(0,.3f), HalfSize = new Vector2(.3f,.1f), YScale = 1f, Opacity = 1f, Flags = 0 }
            };
            var touches = new[] { new NoiseFieldGpuMasks.Touch { Center = new Vector2(.5f,.5f), Scale = 1f } };
            var images = new Dictionary<string, Texture2D> { ["displacement"] = noise, ["touch"] = touchTexture };
            using (var legacy = new LegacyGpuMaskReference(shader, weights))
            using (var graph = new NoiseFieldGpuMasks(shader, definition))
            using (var commands = new PackageComputeFrame(shader, definition))
            {
                for (int i = 0; i < 8; i++)
                {
                    int width = i % 2 == 0 ? 256 : 255, height = i % 2 == 0 ? 144 : 143;
                    int count = i == 0 ? 0 : areas.Length, touchCount = i % 2;
                    float time = i * .77f;
                    legacy.Render(areas, count, width, height, 16f / 9f, time, noise, touches, touchCount, touchTexture);
                    var oldMain = ReadGpuPixels(legacy.Effect); var oldAux = ReadGpuPixels(legacy.Auxiliary);
                    graph.Render(areas, count, width, height, 16f / 9f, time, images, touches, touchCount);
                    if (!oldMain.SequenceEqual(ReadGpuPixels(graph.Effect)) || !oldAux.SequenceEqual(ReadGpuPixels(graph.Auxiliary)))
                        return false;
                    var geometry = new JArray();
                    for (int j = 0; j < count; j++)
                    {
                        var a = areas[j];
                        geometry.Add(new JArray(a.Center.x, a.Center.y, a.HalfSize.x, a.HalfSize.y,
                            a.AngleRadians, a.YScale, a.Opacity, a.Flags));
                    }
                    var contacts = new JArray();
                    for (int j = 0; j < touchCount; j++)
                        contacts.Add(new JArray(touches[j].Center.x, touches[j].Center.y, touches[j].Scale, touches[j].Padding));
                    var buffers = new JObject { ["geometry"] = geometry, ["contacts"] = contacts };
                    var inputs = new JObject { ["geometryCount"] = count, ["contactCount"] = touchCount,
                        ["aspect"] = 16f / 9f, ["screenAspect"] = (float)width / height, ["hasContacts"] = touchCount > 0 };
                    commands.Render(width, height, time, buffers, inputs, images);
                    var outputs = commands.Outputs.ToDictionary(pair => pair.Key, pair => pair.Value);
                    if (!oldMain.SequenceEqual(ReadGpuPixels(outputs["main"])) || !oldAux.SequenceEqual(ReadGpuPixels(outputs["auxiliary"])))
                        return false;
                }
            }
            return true;
        }

        private static Color32[] ReadGpuPixels(RenderTexture source)
        {
            var previous = RenderTexture.active;
            var target = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = source;
                target.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                target.Apply(false, false);
                return target.GetPixels32();
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(target); }
        }

        private static IEnumerator CheckRealFieldDriver(EventManifest manifest)
        {
            RuntimeHost.Ensure();
            var args = new Dictionary<string, object>
            {
                ["__instance"] = "review/real-driver", ["__manifest"] = manifest,
                ["relativeTo"] = "Camera", ["size"] = new[] { 2f, 2f },
                ["frameCount"] = 2, ["color"] = "ff5454", ["depth"] = 0
            };
            NoiseFieldRenderer.Set("field", args, out string error);
            float deadline = Time.realtimeSinceStartup + 15f;
            while (!NoiseFieldRenderer.IsGpuProviderActive(manifest) && Time.realtimeSinceStartup < deadline)
                yield return null;
            Row("GPU-real-driver-takeover", NoiseFieldRenderer.IsGpuProviderActive(manifest) &&
                NoiseFieldRenderer.LiveCount == 1 && NoiseFieldRenderer.LayerCount == 0 ? "PASS" : "FAIL", error);
            NoiseFieldRenderer.DestroyAll();
            yield return null;
        }

        private static IEnumerator CheckRealFieldDepthSafe(EventManifest manifest)
        {
            var probe = CheckRealFieldDepth(manifest);
            while (true)
            {
                bool next;
                try { next = probe.MoveNext(); }
                catch (Exception e) { Row("GPU-real-depth", "FAIL", e.ToString()); yield break; }
                if (!next) yield break;
                yield return probe.Current;
            }
        }

        private static IEnumerator CheckRealFieldDepth(EventManifest manifest)
        {
            var objects = new List<GameObject>();
            RenderTexture target = null;
            Texture2D read = null;
            NoiseFieldGpuCoordinator coordinator = null;
            var prior = RenderTexture.active;
            try
            {
                var cameraGo = new GameObject("PEV.ReviewRealGpuCamera"); objects.Add(cameraGo);
                var camera = cameraGo.AddComponent<Camera>(); camera.enabled = false; camera.orthographic = true;
                camera.orthographicSize = 1f; camera.aspect = 1f; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black; camera.cullingMask = unchecked((int)0x80000000u);
                cameraGo.transform.position = new Vector3(0, 0, -10);
                target = new RenderTexture(128, 128, 0, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
                if (!target.Create()) throw new InvalidOperationException("Cannot create GPU probe target");
                camera.targetTexture = target;
                read = new Texture2D(128, 128, TextureFormat.RGBA32, false, true);
                Func<string, Color, int, float, float, SpriteRenderer> sprite = (name, color, order, x, size) =>
                {
                    var go = new GameObject(name); objects.Add(go); go.layer = 31;
                    go.transform.position = new Vector3(x, 0, 0); go.transform.localScale = new Vector3(size, size, 1);
                    var renderer = go.AddComponent<SpriteRenderer>(); renderer.sprite = SolidSprite.White;
                    renderer.color = color; renderer.sortingOrder = order; return renderer;
                };
                var background = sprite("PEV.ReviewRealGpuBack", Color.blue, 0, 0, 2);
                sprite("PEV.ReviewRealGpuFront", Color.green, 10, .5f, 1);
                Func<Color32[]> capture = () =>
                {
                    try
                    {
                        camera.Render(); RenderTexture.active = target;
                        read.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); read.Apply(false, false);
                        var pixels = read.GetPixels32();
                        return new[] { pixels[64 * 128 + 48], pixels[64 * 128 + 96], pixels[64 * 128 + 4] };
                    }
                    finally { RenderTexture.active = prior; }
                };
                var before = capture();
                var entry = new NoiseFieldRenderer.Entry
                {
                    Provider = manifest, Visible = true, Renderable = true, Opacity = 1f, Tag = "review-phase",
                    FillColor = new Color32(255, 84, 84, 255), SortingLayerID = 0,
                    SortingOrder = 5, UnityLayer = 31,
                    World = new Placement.Frame { Center = Vector3.zero, Size = new Vector2(1.6f, 1.6f) }
                };
                var entries = new[] { entry };
                coordinator = new NoiseFieldGpuCoordinator(manifest);
                bool ready = false;
                float deadline = Time.realtimeSinceStartup + 15f;
                while (!ready && Time.realtimeSinceStartup < deadline)
                {
                    ready = coordinator.Render(entries, camera, 1f, true, Color.black);
                    yield return null;
                }
                if (!ready) { Row("GPU-real-depth", "FAIL", "GPU group did not become ready"); yield break; }
                var after = capture();
                bool changed = !before[0].Equals(after[0]);
                bool foregroundPreserved = before[1].Equals(after[1]);
                Row("GPU-real-depth", changed && foregroundPreserved ? "PASS" : "FAIL",
                    $"back {before[0]}->{after[0]}; front {before[1]}->{after[1]}");
                Func<Color32[]> captureFrame = () =>
                {
                    try
                    {
                        camera.Render(); RenderTexture.active = target;
                        read.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); read.Apply(false, false);
                        return read.GetPixels32();
                    }
                    finally { RenderTexture.active = prior; }
                };
                var frameA = captureFrame();
                coordinator.Render(entries, camera, 2.5f, true, Color.black);
                var frameB = captureFrame();
                int flowChanged = 0;
                for (int i = 0; i < frameA.Length; i++) if (!frameA[i].Equals(frameB[i])) flowChanged++;
                Row("GPU-real-flow", flowChanged > 10 ? "PASS" : "FAIL", $"{flowChanged} pixels changed between t=1 and t=2.5");
                coordinator.Render(entries, camera, 1f, true, Color.black);
                background.color = Color.red;
                var alternate = capture();
                Row("GPU-real-capture-samples", !after[0].Equals(alternate[0]) ? "PASS" : "FAIL",
                    $"blue {after[0]}; red {alternate[0]}");
                var store = new PEV.Core.EventGlobalSettings();
                var settings = new Dictionary<string, bool>();
                store.CopyBooleans(manifest, settings);
                bool defaultOn = settings.TryGetValue("captureScene", out var initial) && initial;
                var captureSetting = manifest.GlobalSettings.Single(s => s.Name == "captureScene");
                store.Set(manifest, captureSetting, false);
                store.CopyBooleans(manifest, settings);
                coordinator.Render(entries, camera, 1f, settings, Color.black);
                var noCaptureRed = capture();
                background.color = Color.blue;
                var noCaptureBlue = capture();
                // The no-capture shader still alpha-blends over the framebuffer. Switching
                // red to blue should change only the corresponding background contribution,
                // with identical attenuation and an unchanged green channel.
                var nr = noCaptureRed[0]; var nb = noCaptureBlue[0];
                bool sameBlend = Math.Abs((nr.r - nb.r) - (nb.b - nr.b)) <= 2 && Math.Abs(nr.g - nb.g) <= 2;
                Row("GPU-real-no-capture-background", sameBlend ? "PASS" : "FAIL",
                    $"red {noCaptureRed[0]}; blue {noCaptureBlue[0]}");
                Row("GPU-real-event-setting", defaultOn && settings.TryGetValue("captureScene", out var changedSetting) &&
                    !changedSetting && !noCaptureBlue[0].Equals(after[0]) ? "PASS" : "FAIL", "package-declared setting drives material selection");
                entry.FillColor=Color.green;
                Color32[] colored=null;
                float coloredDeadline=Time.realtimeSinceStartup+10f;
                while(Time.realtimeSinceStartup<coloredDeadline)
                {
                    coordinator.Render(entries,camera,1f,true,Color.black);
                    yield return null;
                    colored=capture();
                    if(colored[0].g>after[0].g+5 && colored[0].r<after[0].r-5)break;
                }
                Row("GPU-real-packaged-palette", colored!=null && colored[0].g>after[0].g+5 &&
                    colored[0].r<after[0].r-5 ? "PASS" : "FAIL", $"red {after[0]}; green {colored?[0]}");
                entry.FillColor=new Color32(255,84,84,255);
                for(int i=0;i<8;i++){coordinator.Render(entries,camera,1f,true,Color.black);yield return null;}
                GameBridge.Values.Set("review/phase", manifest.SourcePath, "phase", "review-phase", "Disabled", 0, 0);
                for (int i = 0; i < 8; i++) { coordinator.Render(entries, camera, 1f, true, Color.black); yield return null; }
                var disabled = capture();
                GameBridge.Values.Set("review/phase", manifest.SourcePath, "phase", "review-phase", "Ready", 0, 0);
                for (int i = 0; i < 8; i++) { coordinator.Render(entries, camera, 1f, true, Color.black); yield return null; }
                var warning = capture();
                Row("GPU-real-three-phases", !after[0].Equals(disabled[0]) && !disabled[0].Equals(warning[0]) &&
                    !after[0].Equals(warning[0]) ? "PASS" : "FAIL",
                    $"active {after[0]}; disabled {disabled[0]}; ready {warning[0]}");
                GameBridge.Values.Set("review/phase", manifest.SourcePath, "phase", "review-phase", "Active", 0, 0);
                var higher = new NoiseFieldRenderer.Entry
                {
                    Provider = manifest, Visible = true, Renderable = true, Opacity = 1f,
                    FillColor = new Color32(255, 84, 84, 255), SortingLayerID = 0,
                    SortingOrder = 7, UnityLayer = 31,
                    World = new Placement.Frame { Center = Vector3.zero, Size = new Vector2(1.6f, 1.6f) }
                };
                var twoLayers = new[] { entry, higher };
                for (int i = 0; i < 8; i++) { coordinator.Render(twoLayers, camera, 1f, true, Color.black); yield return null; }
                var both = capture();
                entry.Visible = false;
                for (int i = 0; i < 8; i++) { coordinator.Render(twoLayers, camera, 1f, true, Color.black); yield return null; }
                var higherOnly = capture();
                Row("GPU-real-two-depths", !both[0].Equals(higherOnly[0]) && both[1].Equals(before[1]) ? "PASS" : "FAIL",
                    $"both {both[0]}; upper only {higherOnly[0]}; foreground {both[1]}");
                entry.Visible = true;
                entry.GpuInverted = true;
                var outer = new NoiseFieldRenderer.Entry
                {
                    Provider = manifest, Visible = true, Renderable = true, Opacity = 1f,
                    FillColor = new Color32(255, 84, 84, 255), SortingLayerID = 0,
                    SortingOrder = 5, UnityLayer = 31,
                    World = new Placement.Frame { Center = Vector3.zero, Size = new Vector2(2.2f, 2.2f) }
                };
                var withHole = new[] { outer, entry };
                for (int i = 0; i < 8; i++) { coordinator.Render(withHole, camera, 1f, true, Color.black); yield return null; }
                var inverted = capture();
                Row("GPU-real-invert", inverted[0].Equals(before[0]) && !inverted[2].Equals(before[2]) ? "PASS" : "FAIL",
                    $"inside {before[0]}->{inverted[0]}; outside {before[2]}->{inverted[2]}");
            }
            finally
            {
                GameBridge.Values.Remove("review/phase");
                coordinator?.Dispose();
                RenderTexture.active = prior;
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                if (read != null) UnityEngine.Object.Destroy(read);
                foreach (var go in objects) if (go != null) UnityEngine.Object.Destroy(go);
            }
        }

        private sealed class SettingsGuiProbe : MonoBehaviour
        {
            internal bool Ran;
            internal string Error;
            private void OnGUI()
            {
                if (Ran) return;
                try { PEV.Core.Plugin.DrawEventSettings(); }
                catch (Exception e) { Error = e.ToString(); }
                Ran = true;
            }
        }

        private static IEnumerator ProbeGlobalSettingsGui()
        {
            var go = new GameObject("PEV.ReviewSettingsGui");
            var probe = go.AddComponent<SettingsGuiProbe>();
            float deadline = Time.realtimeSinceStartup + 3f;
            while (!probe.Ran && Time.realtimeSinceStartup < deadline) yield return null;
            Row("GLOBAL-umm-gui-draw", !probe.Ran ? "SKIP" : probe.Error == null ? "PASS" : "FAIL",
                !probe.Ran ? "Unity did not invoke OnGUI in the hidden test process" : probe.Error);
            UnityEngine.Object.Destroy(go);
        }

        private static void CheckEditorPlacement(scnEditor editor)
        {
            if (editor == null || editor.levelData == null || editor.levelData.decorations == null) return;
            if (EventRegistry.TryGetById("pev.noise-field", out var field) && field.SchemaVersion >= 2)
            {
                Check("FIELD-editor-place-default-color", () =>
                {
                    var old = new HashSet<LevelEvent>(editor.levelData.decorations);
                    LevelEvent added = null;
                    try
                    {
                        EditorTestHelpers.SelectFloor(editor, 1);
                        editor.AddEventAtSelected((LevelEventType)PEV.Util.StableId.FromEventId(field.Id));
                        added = editor.levelData.decorations.FirstOrDefault(row => !old.Contains(row));
                        return added != null && added.GetString("color") == "ff5454" &&
                            LevelStorage.TryGetData(added, out var data) && data.Id == field.Id;
                    }
                    finally
                    {
                        if (added != null) { LevelStorage.Detach(added); editor.levelData.decorations.Remove(added); }
                    }
                });
            }
            if (EventRegistry.TryGetById(StateEventId, out var state))
            {
                Check("FIELD-editor-place-state-event", () =>
                {
                    var old = new HashSet<LevelEvent>(editor.events);
                    LevelEvent added = null;
                    try
                    {
                        EditorTestHelpers.SelectFloor(editor, 1);
                        editor.AddEventAtSelected((LevelEventType)PEV.Util.StableId.FromEventId(state.Id));
                        added = editor.events.FirstOrDefault(row => !old.Contains(row));
                        if (added == null || !LevelStorage.TryGetData(added, out var data) || data.Id != state.Id) return false;
                        added["tag"] = "review-field";
                        var actionInfo = added.info.propertiesInfo["action"];
                        added["action"] = Enum.Parse(actionInfo.enumType, "WarnThenActive");
                        added["warningDuration"] = 0.75f;
                        Check("FIELD-editor-state-panel", () =>
                        {
                            editor.levelEventsPanel.ShowPanel(added.eventType, 0);
                            var panel = editor.levelEventsPanel.panelsList.FirstOrDefault(p => p.levelEventType == added.eventType);
                            if (panel == null) return false;
                            if (panel.properties.TryGetValue("action", out var choice) && choice?.control != null)
                            {
                                Row("FIELD-editor-state-control", "INFO", choice.control.GetType().FullName);
                                Check("FIELD-editor-state-control-change", () =>
                                {
                                    if (!(choice.control is ADOFAI.LevelEditor.Controls.PropertyControl_Toggle toggle)) return false;
                                    if (!panel.properties.TryGetValue("warningDuration", out var durationProperty) ||
                                        durationProperty?.control?.propertyInfo == null) return false;
                                    var info = durationProperty.control.propertyInfo;
                                    toggle.SelectVar("Disabled");
                                    bool disabled = added.Get<object>("action")?.ToString() == "Disabled" &&
                                        !info.CheckIfShown(added, info.groupName);
                                    toggle.SelectVar("WarnThenActive");
                                    return disabled && added.Get<object>("action")?.ToString() == "WarnThenActive" &&
                                        info.CheckIfShown(added, info.groupName);
                                });
                            }
                            return panel.properties.TryGetValue("tag", out var tag) && tag?.control != null &&
                                panel.properties.TryGetValue("action", out var action) && action?.control != null &&
                                panel.properties.TryGetValue("warningDuration", out var duration) && duration?.control != null;
                        });
                        var instance = new EventInstance { LevelEvent = added, Data = data, Manifest = state,
                            EntryTime = 10, EntryTimeResolved = true, EventOrder = 5 };
                        try
                        {
                            if (!instance.Begin()) return false;
                            instance.Tick(10, true);
                            bool ready = GameBridge.Values.At(state.SourcePath, "phase", "review-field") == "Ready";
                            instance.Tick(10.75, true);
                            return ready && GameBridge.Values.At(state.SourcePath, "phase", "review-field") == "Active";
                        }
                        finally { instance.End(); }
                    }
                    finally
                    {
                        if (added != null)
                        {
                            LevelStorage.Detach(added);
                            editor.events.Remove(added);
                            editor.levelData.levelEvents.Remove(added);
                        }
                    }
                });
            }
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
            Check("MANIFEST-api-version", () => EventManifest.Parse("{\"id\":\"test\",\"apiVersion\":3}", "", out _) == null);
            Check("MANIFEST-shader-bundle", () =>
            {
                var valid = EventManifest.Parse("{\"id\":\"test\",\"schemaVersion\":2,\"shaderBundle\":\"assets/field.bundle\"}", "", out _);
                return valid != null && valid.ShaderBundlePath == "assets/field.bundle" &&
                       EventManifest.Parse("{\"id\":\"test\",\"shaderBundle\":\"assets/field.bundle\"}", "", out _) == null &&
                       EventManifest.Parse("{\"id\":\"test\",\"schemaVersion\":2,\"shaderBundle\":\"../field.bundle\"}", "", out _) == null &&
                       EventManifest.Parse("{\"id\":\"test\",\"schemaVersion\":2,\"shaderBundle\":{}}", "", out _) == null;
            });
            Check("MANIFEST-duplicate-param", () => EventManifest.Parse("{\"id\":\"test\",\"params\":[{\"name\":\"a\",\"type\":\"Float\"},{\"name\":\"a\",\"type\":\"Int\"}]}", "", out _) == null);
            Check("FIELD-row-sorting-layer", () =>
            {
                var row = new LevelEvent(1, LevelEventType.AddDecoration);
                row["depth"] = 7;
                if (!DecorationBridge.TryReadEvent(row, out var bg) || bg.UnityLayer != 9 || bg.SortingOrder != -7) return false;
                row["depth"] = -3;
                return DecorationBridge.TryReadEvent(row, out var front) && front.UnityLayer == 7 && front.SortingOrder == 3;
            });
            Check("GLOBAL-settings-roundtrip", () =>
            {
                const string json = "{\"id\":\"example.a\",\"schemaVersion\":3,\"apiVersion\":2,\"globalSettings\":[{\"name\":\"enabled\",\"type\":\"Bool\",\"default\":true,\"label\":{\"zh-CN\":\"开关\"}}]}";
                var a = EventManifest.Parse(json, "", out _);
                var b = EventManifest.Parse(json.Replace("example.a", "example.b"), "", out _);
                if (a == null || b == null || a.GlobalSettings[0].Label("ChineseSimplified") != "开关") return false;
                string path = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(path, "{\"uninstalled\":{\"keep\":true}}");
                    var store = new PEV.Core.EventGlobalSettings();
                    store.Load(path);
                    if (!store.Get(a, a.GlobalSettings[0]) || !store.Get(b, b.GlobalSettings[0])) return false;
                    store.Set(a, a.GlobalSettings[0], false);
                    store.Save(path);
                    var loaded = new PEV.Core.EventGlobalSettings();
                    loaded.Load(path);
                    return !loaded.Get(a, a.GlobalSettings[0]) && loaded.Get(b, b.GlobalSettings[0]) &&
                        JObject.Parse(File.ReadAllText(path))["uninstalled"]["keep"].Value<bool>() &&
                        (bool)loaded.Snapshot(a)["enabled"] == false;
                }
                finally { File.Delete(path); }
            });
            Check("GLOBAL-settings-invalid-declarations", () =>
                EventManifest.Parse("{\"id\":\"x\",\"globalSettings\":[]}", "", out _) == null &&
                EventManifest.Parse("{\"id\":\"x\",\"schemaVersion\":3,\"apiVersion\":2,\"globalSettings\":[{\"name\":\"__proto__\",\"type\":\"Bool\",\"default\":true}]}", "", out _) == null);
            Check("GLOBAL-primitive-binding", () =>
            {
                const string json = "{\"id\":\"another.author\",\"schemaVersion\":3,\"apiVersion\":2,\"globalSettings\":[{\"name\":\"authorChoice\",\"type\":\"Bool\",\"default\":true,\"binding\":\"custom.effect.toggle\"}]}";
                var manifest = EventManifest.Parse(json, "", out _);
                var store = new PEV.Core.EventGlobalSettings();
                if (manifest == null || !store.GetBoundBool(manifest, "custom.effect.toggle", false)) return false;
                store.Set(manifest, manifest.GlobalSettings[0], false);
                return !store.GetBoundBool(manifest, "custom.effect.toggle", true) &&
                    store.GetBoundBool(manifest, "not.declared", true);
            });
            Check("FIELD-event-uniform-declarations", () =>
            {
                const string valid = "{\"compositor\":{\"layers\":[{\"name\":\"base\",\"visible\":\"show\",\"order\":0,\"variants\":[{\"shader\":\"x\"}]}],\"waves\":{\"_Pulse\":{\"base\":0.63,\"amplitude\":0.37,\"frequency\":43,\"scale\":2}},\"constants\":{\"_Intensity\":0.5,\"_Tint\":[1,0,0.25,1]}}}";
                var definition = CompositorDefinition.Parse(System.Text.Encoding.UTF8.GetBytes(valid));
                if (definition.Constants.Count != 2 || definition.Waves.Count != 1 ||
                    Mathf.Abs(definition.Waves[0].At(0) - 1.63f) > .00001f ||
                    definition.Constants[0].Values[0] != .5f || definition.Constants[1].Values[2] != .25f) return false;
                try { CompositorDefinition.Parse(System.Text.Encoding.UTF8.GetBytes(valid.Replace("[1,0,0.25,1]", "[1,2]"))); return false; }
                catch (ArgumentException) { }
                return true;
            });
            Check("FIELD-event-uniform-material", () =>
            {
                var shader = Shader.Find("Hidden/BlendModes/VisualDeco/Grab");
                if (shader == null) return false;
                var material = new Material(shader);
                try
                {
                    SpriteCompositor.ApplyConstants(material, new[] { new CompositorDefinition.Constant { Name = "_Color", Values = new[] { .25f,.5f,.75f,1f } } });
                    var value = material.GetVector("_Color");
                    return value == new Vector4(0.25f, 0.5f, 0.75f, 1f);
                }
                finally { UnityEngine.Object.Destroy(material); }
            });
            Check("FIELD-event-asset-roles", () =>
            {
                if (!EventRegistry.TryGetById("pev.noise-field", out var manifest) || manifest.SchemaVersion < 3) return true;
                if (!manifest.ImageAssets.ContainsKey("displacement") || !manifest.ShaderAssets.ContainsKey("compute") ||
                    manifest.ShaderAssets.Count != 11 || manifest.ImageAssets.Count != 4) return false;
                var json = JObject.Parse(System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, "event.json")));
                json["imageAssets"]["touch"]["path"] = "../outside";
                return EventManifest.Parse(json.ToString(), "", out _) == null;
            });
            Check("FIELD-custom-shader-roles", () =>
            {
                if (!EventRegistry.TryGetById("pev.noise-field", out var manifest)) return false;
                var json = JObject.Parse(System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, "event.json")));
                json["shaderAssets"] = new JObject { ["compute"] = "assets/compute", ["customLayer"] = "assets/layer" };
                var parsed = EventManifest.Parse(json.ToString(), "", out _);
                if (parsed == null || parsed.ShaderAssets.Count != 2 || !parsed.ShaderAssets.ContainsKey("customLayer")) return false;
                json["shaderAssets"]["customLayer"] = "../escape";
                return EventManifest.Parse(json.ToString(), "", out _) == null;
            });
            Check("GLOBAL-settings-corrupt-file-preserved", () =>
            {
                string path = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(path, "{broken");
                    var store = new PEV.Core.EventGlobalSettings();
                    try { store.Load(path); return false; } catch { }
                    try { store.Save(path); return false; } catch { }
                    return File.ReadAllText(path) == "{broken";
                }
                finally { File.Delete(path); }
            });
            Check("ENUM-dynamic-runtime", () =>
            {
                var name = new AssemblyName("PEV.ReviewEnum." + Guid.NewGuid().ToString("N"));
                var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(name, System.Reflection.Emit.AssemblyBuilderAccess.Run);
                var module = assembly.DefineDynamicModule(name.Name);
                var builder = module.DefineEnum("Review.Choice", TypeAttributes.Public, typeof(int));
                builder.DefineLiteral("First", 0); builder.DefineLiteral("Second", 1);
                var type = builder.CreateType();
                return Type.GetType(type.AssemblyQualifiedName) == type && Enum.Parse(type, "Second").ToString() == "Second";
            });
            Check("CHOICE-legacy-missing-enum", () =>
            {
                const string json = "{\"id\":\"example.legacy\",\"params\":[{\"name\":\"mode\",\"type\":\"Enum:Missing.Type, Missing.Assembly\",\"default\":\"On\",\"optionLabels\":{\"Off\":{\"en\":\"Off\"},\"On\":{\"en\":\"On\"}}}]}";
                var manifest = EventManifest.Parse(json, "", out _);
                if (manifest == null) return false;
                ChoiceEnumFactory.Prepare(manifest, manifest.Params[0]);
                var resolved = Type.GetType(manifest.Params[0].EffectiveType.Substring(5));
                return resolved != null && Enum.Parse(resolved, "On").ToString() == "On";
            });
            Check("FIELD-manifest-old-data", () =>
            {
                if (!EventRegistry.TryGetById("pev.noise-field", out var manifest)) return false;
                var data = EventInstanceData.Parse("@PEV1 {\"id\":\"pev.noise-field\",\"v\":\"1.0.0\",\"p\":{\"noiseFrameCount\":73,\"edgeDistance\":0.42,\"futureData\":{\"keep\":true}}}", out _);
                if (data == null) return false;
                var original = data.Raw["futureData"].DeepClone();
                data.Materialize(manifest.Params);
                var copy = EventInstanceData.Parse(data.ToComment(), out _);
                return copy != null && copy.Raw["noiseFrameCount"].Value<int>() == 73 &&
                    Math.Abs(copy.Raw["edgeDistance"].Value<double>() - 0.42) < 0.00001 &&
                    JToken.DeepEquals(original, copy.Raw["futureData"]) &&
                    (manifest.SchemaVersion < 2 || (data.TryGet("invert", out var invert) && invert is bool b && !b));
            });
            Check("FIELD-manifest-invert-panel", () =>
                EventRegistry.TryGetById("pev.noise-field", out var manifest) &&
                (manifest.SchemaVersion < 2 || DecorationParams.IsInjected("invert")));
            Check("FIELD-new-native-color", () =>
            {
                var draft = EventManifest.Parse("{\"id\":\"example.decoration\",\"schemaVersion\":3,\"decoration\":true,\"decorationDefaults\":{\"color\":\"ff5454\"}}", "", out _);
                var old = EventManifest.Parse("{\"id\":\"example.decoration\",\"schemaVersion\":1}", "", out _);
                var row = new LevelEvent(1, LevelEventType.AddDecoration);
                row["color"] = "ffffff";
                DecorationPlacement.ApplyDecorationDefaults(row, old);
                if (row.GetString("color") != "ffffff") return false;
                DecorationPlacement.ApplyDecorationDefaults(row, draft);
                return row.GetString("color") == "ff5454";
            });
            if (EventRegistry.TryGetById(StateEventId, out var stateManifest))
            {
                Check("FIELD-state-package-registration", () =>
                {
                    var type = (LevelEventType)PEV.Util.StableId.FromEventId(stateManifest.Id);
                    return stateManifest.Params.Exists(p => p.Name == "tag") &&
                        stateManifest.Params.Exists(p => p.Name == "action") &&
                        stateManifest.Params.Exists(p => p.Name == "warningDuration") &&
                        EventRegistry.TryGetInfo(type, out var info) && info.propertiesInfo.ContainsKey("action");
                });
                Check("FIELD-state-enum-localization", () =>
                {
                    var param = stateManifest.Params.Single(p => p.Name == "action");
                    var option = param.OptionLabels.Single(o => o.Key == "WarnThenActive");
                    string key = "enum." + param.EffectiveType.Substring("Enum:".Length).Trim() + ".WarnThenActive";
                    var zh = SA.GoogleDoc.Localization.Client.GetFullSectionLocalization(SystemLanguage.ChineseSimplified);
                    var en = SA.GoogleDoc.Localization.Client.GetFullSectionLocalization(SystemLanguage.English);
                    return zh != null && en != null && zh.TryGetValue(key, out var zhValue) &&
                        en.TryGetValue(key, out var enValue) && zhValue == option.Value["ChineseSimplified"] &&
                        enValue == option.Value["English"];
                });
                Check("CHOICE-storage-and-isolation", () =>
                {
                    var type = (LevelEventType)PEV.Util.StableId.FromEventId(stateManifest.Id);
                    if (!EventRegistry.TryGetInfo(type, out var info) || !info.propertiesInfo.TryGetValue("action", out var prop)) return false;
                    var value = ParamCoercion.Coerce(prop, "WarnThenActive");
                    if (value == null || value.GetType() != prop.enumType ||
                        StorageValue.Canonical(prop, value).Value<string>() != "WarnThenActive") return false;
                    const string json = "{\"id\":\"example.choice\",\"schemaVersion\":3,\"params\":[{\"name\":\"action\",\"type\":\"Choice\",\"default\":\"Active\",\"options\":[\"Disabled\",\"Ready\",\"Active\",\"WarnThenActive\"],\"optionLabels\":{\"WarnThenActive\":{\"en\":\"Different label\"}}}]}";
                    var other = EventManifest.Parse(json, "", out _);
                    if (other == null) return false;
                    ChoiceEnumFactory.Prepare(other, other.Params[0]);
                    EnumLabels.Register(other.Params[0]);
                    string otherType = other.Params[0].EffectiveType.Substring("Enum:".Length).Trim();
                    var en = SA.GoogleDoc.Localization.Client.GetFullSectionLocalization(SystemLanguage.English);
                    return Type.GetType(otherType) != prop.enumType &&
                        en.TryGetValue("enum." + otherType + ".WarnThenActive", out var label) && label == "Different label";
                });
                Check("FIELD-state-script-order-and-cleanup", () =>
                {
                    string script = EventScriptCache.Get(stateManifest);
                    if (script == null) return false;
                    var first = ScriptHost.Create(script, "review/state-a", out _, stateManifest);
                    var second = ScriptHost.Create(script, "review/state-b", out _, stateManifest);
                    try
                    {
                        var a = new EventContext { EventTime = 1, EventOrder = 0 };
                        var b = new EventContext { EventTime = 2, EventOrder = 1 };
                        if (first == null || second == null ||
                            !first.Initialize(a, new Dictionary<string, object> { ["tag"] = "red blue", ["action"] = "Disabled" }, out _) ||
                            !second.Initialize(b, new Dictionary<string, object> { ["tag"] = "blue", ["action"] = "WarnThenActive", ["warningDuration"] = 0.5 }, out _)) return false;
                        first.Update(1, a.ToStateJson()); second.Update(0, b.ToStateJson());
                        if (GameBridge.Values.At(stateManifest.SourcePath, "phase", "red") != "Disabled" ||
                            GameBridge.Values.At(stateManifest.SourcePath, "phase", "blue") != "Ready") return false;
                        second.Update(0.5, b.ToStateJson()); first.Update(1.5, a.ToStateJson());
                        if (GameBridge.Values.At(stateManifest.SourcePath, "phase", "blue") != "Active") return false;
                        second.Dispose();
                        return GameBridge.Values.At(stateManifest.SourcePath, "phase", "blue") == "Disabled";
                    }
                    finally { first?.Dispose(); second?.Dispose(); }
                });
                Check("FIELD-legacy-state-script-compatibility", () =>
                {
                    const string oldScript = "function create(){return {update:function(){fx.fieldState({tag:'legacy',phase:'Ready'});}};}";
                    var host = ScriptHost.Create(oldScript, "review/legacy-state", out _, stateManifest);
                    try
                    {
                        var context = new EventContext { EventTime = 1, EventOrder = 0 };
                        if (host == null || !host.Initialize(context, new Dictionary<string, object>(), out _)) return false;
                        host.Update(0, context.ToStateJson());
                        return GameBridge.Values.At(stateManifest.SourcePath, "phase", "legacy") == "Ready";
                    }
                    finally { host?.Dispose(); }
                });
            }
            Check("FIELD-touch-history", () =>
            {
                var h = new NoiseFieldTouchHistory();
                var a = new NoiseFieldTouchHistory.Contact { Id = 42, Position = new Vector2(0.2f, 0.3f) };
                var b = new NoiseFieldTouchHistory.Contact { Id = 7, Position = new Vector2(0.8f, 0.7f) };
                h.Update(new[] { a, b }, 1f);
                if (h.GetSlot(0).Scale != 0 || h.GetSlot(1).Scale != 0) return false;
                h.Update(new[] { b, a }, 1.05f);
                if (h.GetSlot(0).Id != 42 || h.GetSlot(1).Id != 7 ||
                    Mathf.Abs(h.GetSlot(0).Scale - 0.5f) > 0.001f) return false;
                h.Update(new[] { b }, 1.05f);
                h.Update(new[] { b }, 1.10f);
                if (h.GetSlot(0).Held || Mathf.Abs(h.GetSlot(0).Scale - 0.25f) > 0.001f) return false;
                h.Update(new[] { a, b }, 1.10f);
                if (!h.GetSlot(0).Held || h.GetSlot(0).Scale != 0) return false;
                h.Update(new[] { b }, 0.5f);
                return h.GetSlot(0).Held && h.GetSlot(0).Id == 7 && h.GetSlot(0).Scale == 0 && !h.GetSlot(1).Held;
            });
            Check("FIELD-state-tags", () =>
            {
                var overrides = new FieldPhaseOverrides();
                overrides.Set("a", "red blue", NoiseFieldPhase.Disabled, 1, 0);
                overrides.Set("b", "blue", NoiseFieldPhase.Active, 2, 1);
                overrides.Set("c", "red", NoiseFieldPhase.Ready, 2, 2);
                bool ok = overrides.At("green") == NoiseFieldPhase.Active &&
                    overrides.At("blue") == NoiseFieldPhase.Active &&
                    overrides.At("red blue") == NoiseFieldPhase.Ready;
                overrides.Remove("c"); overrides.Remove("b");
                return ok && overrides.At("blue") == NoiseFieldPhase.Disabled;
            });
            Check("TAGGED-values-package-isolation", () =>
            {
                var values = new TaggedValues();
                values.Set("a", "package-a", "state", "red blue", "off", 1, 0);
                values.Set("b", "package-a", "state", "blue", "on", 2, 1);
                values.Set("c", "package-b", "state", "blue", "other", 3, 1);
                values.Set("d", "package-a", "color", "blue", "green", 4, 1);
                if (values.At("package-a", "state", "red") != "off" || values.At("package-a", "state", "blue") != "on" ||
                    values.At("package-b", "state", "blue") != "other" || values.At("package-a", "color", "blue") != "green") return false;
                values.Remove("b");
                return values.At("package-a", "state", "blue") == "off" && values.At("package-b", "state", "blue") == "other";
            });
            Check("FIELD-touch-geometry", () =>
            {
                var normal = new NoiseFieldTouchGeometry.Area
                {
                    Center = Vector2.zero, Size = new Vector2(4f, 2f), RotationDegrees = 90f,
                    Active = true, Visible = true
                };
                var hole = new NoiseFieldTouchGeometry.Area
                {
                    Center = Vector2.zero, Size = Vector2.one, Inverted = true, Active = true, Visible = true
                };
                return NoiseFieldTouchGeometry.Contains(new[] { normal }, new Vector2(0, 1.5f)) &&
                       !NoiseFieldTouchGeometry.Contains(new[] { normal }, new Vector2(1.5f, 0)) &&
                       !NoiseFieldTouchGeometry.Contains(new[] { normal, hole }, Vector2.zero) &&
                       NoiseFieldTouchGeometry.Contains(new[] { normal, hole, hole }, Vector2.zero) &&
                       !NoiseFieldTouchGeometry.Contains(new[] { normal }, new Vector2(0, 1.95f), 0.1f) &&
                       !NoiseFieldTouchGeometry.Contains(new[] { new NoiseFieldTouchGeometry.Area { Active = true, Visible = true } }, Vector2.zero);
            });
            Check("FIELD-gpu-area-layout", () => System.Runtime.InteropServices.Marshal.SizeOf(typeof(NoiseFieldGpuMasks.Area)) == 32 &&
                System.Runtime.InteropServices.Marshal.SizeOf(typeof(NoiseFieldGpuMasks.Touch)) == 16);
            Check("FIELD-renderer-activation-gate", () =>
            {
                var gate = new VisibilityActivationGate();
                return !gate.Observe(true, 10) && !gate.Observe(true, 10) && gate.Observe(true, 11) &&
                    gate.Observe(false, 11) && !gate.Observe(true, 11) && gate.Observe(true, 12);
            });
            Check("FIELD-touch-shader-uniforms", () =>
            {
                var p = NoiseFieldGpuLayer.TouchPosition(new Vector2(0.5f, 0.25f), 1600, 900);
                var wave = new CompositorDefinition.Wave { Base = 0.63f, Amplitude = 0.37f, Frequency = 43f, Scale = 2f };
                return Mathf.Abs(p.x - 8f / 9f) < 0.00001f && Mathf.Abs(p.y - 0.25f) < 0.00001f &&
                       Mathf.Abs(wave.At(0f) - 1.63f) < 0.00001f &&
                       Mathf.Abs(wave.At(Mathf.PI / 86f) - 2f) < 0.00001f;
            });
            Check("FIELD-viewport-projection", () =>
            {
                var go = new GameObject("PEV.ReviewProjection");
                try
                {
                    var cam = go.AddComponent<Camera>();
                    cam.orthographic = true;
                    cam.orthographicSize = 5f;
                    cam.aspect = 16f / 9f;
                    go.transform.position = new Vector3(0, 0, -10);
                    var frame = new Placement.Frame { Center = Vector3.zero, Size = new Vector2(4, 2), Rotation = 0 };
                    if (!ViewportRectangleProjection.TryProject(cam, frame, new Vector2(1f, cam.aspect), out var result)) return false;
                    float expected = 1f / (5f * cam.aspect);
                    if (result.Center.sqrMagnitude > 0.000001f || Mathf.Abs(result.HalfSize.x - 2 * expected) > 0.00001f ||
                        Mathf.Abs(result.HalfSize.y - expected) > 0.00001f || Mathf.Abs(result.AngleRadians) > 0.00001f) return false;
                    if (!ViewportRectangleProjection.TryProject(cam, frame, new Vector2(2f, 2f * cam.aspect), out var scaled) ||
                        (scaled.Center - result.Center * .5f).sqrMagnitude > 0.000001f ||
                        (scaled.HalfSize - result.HalfSize * .5f).sqrMagnitude > 0.000001f ||
                        ViewportRectangleProjection.TryProject(cam, frame, new Vector2(0f, cam.aspect), out _)) return false;
                    frame.Rotation = 45;
                    if (!ViewportRectangleProjection.TryProject(cam, frame, new Vector2(1f, cam.aspect), out result) ||
                        Mathf.Abs(result.AngleRadians - Mathf.PI / 4) > 0.0001f) return false;
                    frame.Center = go.transform.position;
                    if (!ViewportRectangleProjection.TryProject(cam, frame, new Vector2(1f, cam.aspect), out result)) return false;
                    cam.orthographic = false;
                    return !ViewportRectangleProjection.TryProject(cam, frame, new Vector2(1f, cam.aspect), out _);
                }
                finally { UnityEngine.Object.Destroy(go); }
            });
            Check("FIELD-gpu-plan-groups", () =>
            {
                var go = new GameObject("PEV.ReviewGpuPlan");
                try
                {
                    if (!EventRegistry.TryGetById("pev.noise-field", out var provider) || !provider.ComputeGraphs.TryGetValue("coverage", out var graphPath)) return false;
                    using var plan = NoiseFieldGpuPlan.Create(provider, EventLoader.ReadResource(provider.SourcePath, graphPath, 131072));
                    var values = new TaggedValues();
                    values.Set("mode-b", provider.SourcePath, "phase", "test-b", "Ready", 0, 0);
                    values.Set("mode-c", provider.SourcePath, "phase", "test-c", "Disabled", 0, 0);
                    var camera = go.AddComponent<Camera>();
                    camera.orthographic = true; camera.orthographicSize = 5; camera.aspect = 16f / 9f;
                    go.transform.position = new Vector3(0, 0, -10);
                    var a = new NoiseFieldRenderer.Entry { Provider = provider, Tag = "test-a", GpuSequence = 2, Visible = true, Renderable = true,
                        Opacity = 1, World = new Placement.Frame { Size = new Vector2(2, 2) },
                        SortingOrder = 1, UnityLayer = 7,
                        FillColor = Color.blue, EdgeColor = Color.red };
                    var b = new NoiseFieldRenderer.Entry { Provider = provider, Tag = "test-b", GpuSequence = 1, Visible = true, Renderable = true,
                        Opacity = 0.5f, World = new Placement.Frame { Size = new Vector2(2, 2) },
                        SortingOrder = 1, UnityLayer = 7, GpuInverted = true };
                    var c = new NoiseFieldRenderer.Entry { Provider = provider, Tag = "test-c", GpuSequence = 3, Visible = true, Renderable = true,
                        Opacity = 1, World = new Placement.Frame { Size = new Vector2(2, 2) },
                        SortingOrder = 1, UnityLayer = 9,
                        Shape = NoiseFieldShape.Circle };
                    plan.Build(new[] { a, b, c }, camera, camera.aspect, values);
                    if (plan.Groups.Count != 2) return false;
                    var key = new NoiseFieldGpuPlan.Key { SortingOrder = 1, UnityLayer = 7 };
                    if (!plan.Groups.TryGetValue(key, out var group) || group.Count != 2 ||
                        group.Data["data"][0][6].Value<int>() != 6 || group.Data["data"][1][6].Value<int>() != 1 ||
                        !group.Data.Value<bool>("primary") || !group.Data.Value<bool>("secondary") ||
                        group.Data["data"][1][7].Value<float>() != 0f || group.Data["data"][1][9].Value<float>() != 1f || !group.Data.Value<bool>("colored")) return false;
                    plan.Build(new[] { a }, camera, camera.aspect, values);
                    return plan.Groups.Count == 1 && plan.Groups[key].Count == 1;
                }
                finally { UnityEngine.Object.Destroy(go); }
            });
            Check("FIELD-gpu-plan-randomized", CheckPackagePlanRandomized);
            Check("FIELD-gpu-plan-validation", CheckPackagePlanValidation);
            Check("FIELD-gpu-formats", () =>
                SystemInfo.supportsComputeShaders &&
                SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32B32A32_UInt,
                    UnityEngine.Experimental.Rendering.FormatUsage.LoadStore) &&
                SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.R32_UInt,
                    UnityEngine.Experimental.Rendering.FormatUsage.LoadStore) &&
                SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                    UnityEngine.Experimental.Rendering.FormatUsage.LoadStore));
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
                           EventLoader.ReadResource(zip, "main.js").Length == 4 &&
                           EventLoader.ReadResource(root, "main.js", 3) == null &&
                           EventLoader.ReadResource(zip, "main.js", 3) == null &&
                           EventLoader.ReadResource(root, "main.js", 4).Length == 4 &&
                           EventLoader.ReadResource(zip, "main.js", 4).Length == 4;
                }
                finally { Directory.Delete(root, true); }
            });
            Check("PACKAGE-included-events", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "pev-included-" + Guid.NewGuid().ToString("N"));
                string dirs = Path.Combine(root, "dirs"), zips = Path.Combine(root, "zips");
                string package = Path.Combine(dirs, "example.event");
                Directory.CreateDirectory(Path.Combine(package, "child"));
                Directory.CreateDirectory(zips);
                try
                {
                    File.WriteAllText(Path.Combine(package, "event.json"),
                        "{\"schemaVersion\":4,\"id\":\"example.parent\",\"includedEvents\":[\"child/state.json\"]}");
                    File.WriteAllText(Path.Combine(package, "child", "state.json"),
                        "{\"schemaVersion\":1,\"id\":\"example.state\",\"entry\":\"child/state.js\"}");
                    string script = Path.Combine(package, "child", "state.js");
                    File.WriteAllText(script, "function create(){}", new System.Text.UTF8Encoding(false));
                    var fromDir = EventLoader.Scan(new[] { dirs });
                    if (fromDir.Errors.Count != 0 || fromDir.Loaded.Count != 2 ||
                        fromDir.Loaded[1].Id != "example.state" ||
                        EventLoader.ReadResource(fromDir.Loaded[1].SourcePath, fromDir.Loaded[1].Entry) == null) return false;
                    string zip = Path.Combine(zips, "example.event");
                    ZipFile.CreateFromDirectory(package, zip);
                    var fromZip = EventLoader.Scan(new[] { zips });
                    if (fromZip.Errors.Count != 0 || fromZip.Loaded.Count != 2 ||
                        fromZip.Loaded[1].Id != "example.state" ||
                        EventLoader.ReadResource(fromZip.Loaded[1].SourcePath, fromZip.Loaded[1].Entry) == null) return false;
                    File.Delete(script);
                    var broken = EventLoader.Scan(new[] { dirs });
                    return broken.Loaded.Count == 0 && broken.Errors.Count == 1;
                }
                finally { Directory.Delete(root, true); }
            });
            Check("FIELD-reference-texture", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "pev-texture-" + Guid.NewGuid().ToString("N"));
                string assets = Path.Combine(root, "assets");
                Directory.CreateDirectory(assets);
                try
                {
                    // Independent PNG fixture: top row red bytes 1,4; bottom row 7,10.
                    byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAGElEQVR4nGNkZGL+z8zMzMDCxsbGAGIAAA+dAS/kUtAIAAAAAElFTkSuQmCC");
                    File.WriteAllBytes(Path.Combine(assets, "BlockNoise1.png"), png);
                    var manifest = new EventManifest { SourcePath = root };
                    manifest.ImageAssets["displacement"] = new ImageAssetSpec { Path = "assets/BlockNoise1.png", Wrap = "mirror", Filter = "point", FlipY = true, Linear = true };
                    var texture = ReferenceNoiseTextures.Load(manifest, "displacement", out var error);
                    if (texture == null || error != null || texture.wrapMode != TextureWrapMode.Mirror ||
                        texture.filterMode != FilterMode.Point) return false;
                    var pixels = texture.GetPixels32();
                    return pixels.Length == 4 && pixels[0].r == 7 && pixels[1].r == 10 &&
                           pixels[2].r == 1 && pixels[3].r == 4 &&
                           ReferenceNoiseTextures.Load(manifest, "displacement", out _) == texture &&
                           ReferenceNoiseTextures.Load(manifest, "../outside", out _) == null;
                }
                finally { ReferenceNoiseTextures.Clear(); Directory.Delete(root, true); }
            });
            Check("LEGACY-package-texture-isolation", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "pev-legacy-" + Guid.NewGuid().ToString("N"));
                string a = Path.Combine(root, "a"), b = Path.Combine(root, "b");
                Directory.CreateDirectory(a); Directory.CreateDirectory(b);
                try
                {
                    var red = new[] { new Color32(255, 0, 0, 255) };
                    var blue = new[] { new Color32(0, 0, 255, 255) };
                    File.WriteAllBytes(Path.Combine(a, "tex.png"), NoiseTextures.EncodePng(red, 1, 1));
                    File.WriteAllBytes(Path.Combine(b, "tex.png"), NoiseTextures.EncodePng(blue, 1, 1));
                    var first = new EventManifest { Id = "example.a", SourcePath = a };
                    var second = new EventManifest { Id = "example.b", SourcePath = b };
                    LegacyFieldTextureSet.ConfigureForTest(first, "tex.png", "tex.png");
                    LegacyFieldTextureSet.ConfigureForTest(second, "tex.png", "tex.png");
                    var x = LegacyFieldTextureSet.Load(first, out _);
                    var y = LegacyFieldTextureSet.Load(second, out _);
                    if (x == null || y == null || x.Block[0].r != 255 || y.Block[0].b != 255 ||
                        ReferenceEquals(x, y) || LegacyFieldTextureSet.Identity(first) == LegacyFieldTextureSet.Identity(second)) return false;
                    var malformed = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 127, 255, 255, 255, 73, 72, 68, 82 };
                    return NoiseTextures.DecodePng(malformed, out _, out _) == null;
                }
                finally { Directory.Delete(root, true); }
            });
            Check("CORE-no-embedded-event-png", () =>
                !typeof(NoiseTextures).Assembly.GetManifestResourceNames().Any(n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)));
            Check("LEGACY-profile-cache-isolation", () =>
            {
                var a = new EventManifest { SourcePath = "same", Version = "1" };
                var b = new EventManifest { SourcePath = "same", Version = "1" };
                LegacyFieldTextureSet.ConfigureForTest(a, "block.png", "point.png", new Dictionary<string, float[]> { ["sample"] = new[] { 1f } });
                LegacyFieldTextureSet.ConfigureForTest(b, "block.png", "point.png", new Dictionary<string, float[]> { ["sample"] = new[] { 2f } });
                return LegacyFieldTextureSet.Identity(a) != LegacyFieldTextureSet.Identity(b);
            });
            Check("CORE-package-identity-isolation", () =>
            {
                var a = new EventManifest { Id = "same", SourcePath = "same", Version = "1" };
                var b = new EventManifest { Id = "same", SourcePath = "same", Version = "1" };
                return PackageIdentity.Of(a) == PackageIdentity.Of(a) && PackageIdentity.Of(a) != PackageIdentity.Of(b) &&
                    PackageIdentity.Of(null) == "";
            });
        }

        private static bool CheckPackagePlanRandomized()
        {
            if (!EventRegistry.TryGetById("pev.noise-field", out var provider) ||
                !provider.TryRuntimeResource("renderGraph", provider.ComputeGraphs, out var graphPath)) return false;
            var plan = NoiseFieldGpuPlan.Create(provider, EventLoader.ReadResource(provider.SourcePath, graphPath, 131072));
            var go = new GameObject("PEV.ReviewPackagePlan");
            try
            {
                var camera = go.AddComponent<Camera>(); camera.orthographic = true; camera.orthographicSize = 5; camera.aspect = 16f / 9f;
                go.transform.position = new Vector3(0, 0, -10);
                var random = new System.Random(75211); var values = new TaggedValues();
                var states = new[] { "Active", "Ready", "Disabled", "unknown" };
                var entries = new NoiseFieldRenderer.Entry[12];
                var expectedStates = new string[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                    entries[i] = new NoiseFieldRenderer.Entry { Provider = provider, Tag = "plan-" + i, GpuSequence = i,
                        Visible = true, Renderable = true, Opacity = 1, SortingOrder = i % 3, UnityLayer = 7,
                        FillColor = new Color32(255, 84, 84, 255), World = new Placement.Frame { Size = new Vector2(2, 2) } };
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int frame = 0; frame < 100; frame++)
                {
                    for (int i = 0; i < entries.Length; i++)
                    {
                        var entry = entries[i]; string state = expectedStates[i] = states[random.Next(states.Length)];
                        entry.GpuInverted = random.Next(2) == 0;
                        entry.FillColor = i == frame % entries.Length ? Color.blue : (Color)new Color32(255, 84, 84, 255);
                        entry.World = new Placement.Frame { Center = new Vector3(i * .03f, frame * .0001f, 0), Size = new Vector2(2, 2) };
                        values.Set("plan-state-" + i, provider.SourcePath, "phase", entry.Tag, state, frame, i);
                    }
                    plan.Build(entries, camera, camera.aspect, values);
                    if (plan.Groups.Count != 3) return false;
                    for (int groupIndex = 0; groupIndex < 3; groupIndex++)
                    {
                        var key = new NoiseFieldGpuPlan.Key { SortingOrder = groupIndex, UnityLayer = 7 };
                        if (!plan.Groups.TryGetValue(key, out var group) || group.Count != 4) return false;
                        bool primary = false, secondary = false, colored = false; int row = 0;
                        for (int i = groupIndex; i < entries.Length; i += 3)
                        {
                            bool active = expectedStates[i] == "Active" || expectedStates[i] == "unknown";
                            bool ready = expectedStates[i] == "Ready";
                            uint flags = (uint)((active ? 1 : ready ? 4 : 0) | (entries[i].GpuInverted ? 2 : 0));
                            var data = group.Data["data"][row];
                            if (data[6].Value<uint>() != flags || data[10].Value<bool>() != active ||
                                data[12].Value<bool>() != active || data[13].Value<bool>() != entries[i].GpuInverted) return false;
                            primary |= active || ready; secondary |= !active; colored |= entries[i].FillColor == Color.blue; row++;
                        }
                        if (group.Data.Value<bool>("primary") != primary || group.Data.Value<bool>("secondary") != secondary ||
                            group.Data.Value<bool>("colored") != colored) return false;
                    }
                }
                watch.Stop();
                Row("GPU-package-plan-cost", "INFO", $"100 changing frames, 12 areas, 3 depths: {watch.Elapsed.TotalMilliseconds / 100:F3} ms/frame");
                return true;
            }
            finally { plan.Dispose(); UnityEngine.Object.Destroy(go); }
        }

        private static bool CheckPackagePlanValidation()
        {
            if (!EventRegistry.TryGetById("pev.noise-field", out var provider)) return false;
            var ctor = typeof(NoiseFieldGpuPlan).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(string) }, null);
            if (ctor == null) return false;
            var go = new GameObject("PEV.ReviewInvalidPlan");
            try
            {
                var camera = go.AddComponent<Camera>(); camera.orthographic = true; camera.orthographicSize = 5; camera.aspect = 1;
                go.transform.position = new Vector3(0, 0, -10);
                var entry = new NoiseFieldRenderer.Entry { Provider = provider, Visible = true, Renderable = true, Opacity = 1,
                    SortingOrder = 1, UnityLayer = 7, World = new Placement.Frame { Size = new Vector2(2, 2) } };
                const string prefix = "function updateController(){return {groups:[{sorting:[0,";
                const string middle = ",7],members:";
                foreach (string source in new[] {
                    prefix + "1" + middle + "[0,0],data:[[],[]]}]};}",
                    prefix + "2" + middle + "[0],data:[[]]}]};}",
                    "function updateController(){return {groups:[]};}" })
                {
                    using var plan = (NoiseFieldGpuPlan)ctor.Invoke(new object[] { "phase", source });
                    try { plan.Build(new[] { entry }, camera, 1, new TaggedValues()); return false; }
                    catch (ArgumentException) { }
                }
                return true;
            }
            finally { UnityEngine.Object.Destroy(go); }
        }

        private static IEnumerator CheckPackagePlanScaleSafe()
        {
            var probe = CheckPackagePlanScale();
            while (true)
            {
                bool next;
                try { next = probe.MoveNext(); }
                catch (Exception e) { Row("FIELD-gpu-plan-scale", "FAIL", e.ToString()); yield break; }
                if (!next) yield break;
                yield return probe.Current;
            }
        }
        private static IEnumerator CheckPackagePlanScale()
        {
            if (!EventRegistry.TryGetById("pev.noise-field", out var provider) ||
                !provider.TryRuntimeResource("renderGraph", provider.ComputeGraphs, out var path))
            { Row("FIELD-gpu-plan-scale", "FAIL", "missing package"); yield break; }
            var plan = NoiseFieldGpuPlan.Create(provider, EventLoader.ReadResource(provider.SourcePath, path, 131072));
            var go = new GameObject("PEV.ReviewLargePlan");
            try
            {
                var camera = go.AddComponent<Camera>(); camera.orthographic = true; camera.orthographicSize = 5; camera.aspect = 1;
                go.transform.position = new Vector3(0, 0, -10);
                var entries = Enumerable.Range(0, 1024).Select(i => new NoiseFieldRenderer.Entry {
                    Provider = provider, GpuSequence = i, Visible = true, Renderable = true, Opacity = 1, UnityLayer = 7,
                    FillColor = new Color32(255, 84, 84, 255), World = new Placement.Frame { Size = new Vector2(2, 2) } }).ToArray();
                var values = new TaggedValues(); var watch = System.Diagnostics.Stopwatch.StartNew();
                bool ready = plan.Build(entries, camera, 1, values);
                watch.Stop(); double worstMain = watch.Elapsed.TotalMilliseconds; bool initiallyAsync = !ready;
                float deadline = Time.realtimeSinceStartup + 8f;
                while (!ready && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    watch.Restart(); ready = plan.Build(entries, camera, 1, values); watch.Stop();
                    worstMain = Math.Max(worstMain, watch.Elapsed.TotalMilliseconds);
                }
                var key = new NoiseFieldGpuPlan.Key { UnityLayer = 7 };
                NoiseFieldGpuPlan.Group group = null;
                bool ok = initiallyAsync && ready && plan.Groups.TryGetValue(key, out group) && group.Count == entries.Length;
                if (ok)
                {
                    float old = group.Data["data"][0][0].Value<float>();
                    entries[0].World = new Placement.Frame { Center = new Vector3(1, 0, 0), Size = new Vector2(2, 2) };
                    ready = plan.Build(entries, camera, 1, values);
                    ok &= ready && plan.Groups[key].Data["data"][0][0].Value<float>() == old;
                    deadline = Time.realtimeSinceStartup + 8f;
                    while (plan.Groups[key].Data["data"][0][0].Value<float>() == old && Time.realtimeSinceStartup < deadline)
                    {
                        yield return null;
                        watch.Restart(); plan.Build(entries, camera, 1, values); watch.Stop();
                        worstMain = Math.Max(worstMain, watch.Elapsed.TotalMilliseconds);
                    }
                    ok &= plan.Groups[key].Data["data"][0][0].Value<float>() != old;
                }
                if (ok)
                {
                    entries[0].World = new Placement.Frame { Center = new Vector3(2, 0, 0), Size = new Vector2(2, 2) };
                    plan.Build(entries, camera, 1, values);
                    var pendingField = typeof(NoiseFieldGpuPlan).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic);
                    var pending = pendingField.GetValue(plan) as System.Threading.Tasks.Task;
                    ok &= pending != null && plan.Build(Array.Empty<NoiseFieldRenderer.Entry>(), camera, 1, values) && plan.Groups.Count == 0;
                    deadline = Time.realtimeSinceStartup + 8f;
                    while (pending != null && !pending.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                    ok &= pending != null && pending.IsCompleted;
                    ok &= plan.Build(new[] { entries[0] }, camera, 1, values) && plan.Groups.TryGetValue(key, out var single) && single.Count == 1;
                }
                if (ok)
                {
                    var transient = NoiseFieldGpuPlan.Create(provider, EventLoader.ReadResource(provider.SourcePath, path, 131072));
                    transient.Build(entries, camera, 1, values);
                    var pending = typeof(NoiseFieldGpuPlan).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(transient)
                        as System.Threading.Tasks.Task;
                    transient.Dispose();
                    deadline = Time.realtimeSinceStartup + 3f;
                    while (pending != null && !pending.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                    ok &= pending != null && pending.IsCompleted;
                }
                Row("GPU-package-plan-scale-cost", "INFO", $"1024 areas; worst observed main-thread build {worstMain:F1} ms");
                Row("FIELD-gpu-plan-scale", ok ? "PASS" : "FAIL", "background plan retains prior output, discards stale results and cancels on disposal");
            }
            finally { plan.Dispose(); UnityEngine.Object.Destroy(go); }
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
            Check("MIGRATE-generic-no-field-defaults", () =>
            {
                if (!DecorationParams.TryGetNativeInfo(out var nativeInfo)) return false;
                var level = new LevelData();
                var source = new LevelEvent(1, LevelEventType.EditorComment);
                var data = new EventInstanceData { Id = "example.non-field" };
                data.Set("size", new[] { 8, 6 });
                data.Materialize(null);
                var manifest = new EventManifest { Id = data.Id, DecorationBacked = true, SchemaVersion = 3 };
                var baseline = new LevelEvent(1, LevelEventType.AddDecoration, nativeInfo);
                var method = typeof(LevelStorage).GetMethod("MigrateToDecoration", BindingFlags.NonPublic | BindingFlags.Static);
                try
                {
                    bool migrated = (bool)method.Invoke(null, new object[] { level, source, data, manifest, new DecodeSummary() });
                    var row = level.decorations.Single();
                    return migrated && row.GetString("color") == baseline.GetString("color") &&
                        row.Get<Vector2>("scale") == baseline.Get<Vector2>("scale");
                }
                finally { foreach (var row in level.decorations) LevelStorage.Detach(row); }
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
            Check("SCRIPT-global-settings-refresh", () =>
            {
                const string source = "function create(ctx){if(!ctx.settings.enabled || !Object.isFrozen(ctx.settings))throw Error('initial');" +
                    "return {update:function(){if(ctx.settings.enabled || !Object.isFrozen(ctx.settings))throw Error('updated');}};}";
                var context = new EventContext { Settings = new Dictionary<string, object> { ["enabled"] = true } };
                using (var host = ScriptHost.Create(source, "review/settings", out _))
                {
                    if (host == null || !host.Initialize(context, new Dictionary<string, object>(), out _)) return false;
                    context.Settings["enabled"] = false;
                    host.Update(1, context.ToStateJson());
                    return !host.Faulted && host.UpdateCalls == 1 &&
                        !new EventContext().ToStateJson().Contains("\"settings\"");
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

        private static NoiseTextureFactory.Request Request()
        {
            EventRegistry.TryGetById("pev.noise-field", out var provider);
            return new NoiseTextureFactory.Request
            {
                Provider = provider, WidthWorld = 2, HeightWorld = 2, FrameCount = 2, TargetPixelWidth = 192,
                FillOpacity = 0.6f, EdgeOpacity = 0.8f, GlowOpacity = 0.8f,
                EdgeColor = Color.red, SampleRate = 1f, ScreenPixelWidth = 192
            };
        }

        private static void CheckKeys()
        {
            var req = Request();
            string key = NoiseTextureFactory.Key(req);
            Check("CACHE-glow-opacity", () => { var r = req; r.GlowOpacity = 0.4f; return NoiseTextureFactory.Key(r) != key; });
            Check("CACHE-scene-colour", () => { var r = req; r.SceneColor = Color.white; return NoiseTextureFactory.Key(r) != key; });
            Check("CACHE-screen-density", () => { var r = req; r.ScreenPixelWidth = 384; return NoiseTextureFactory.Key(r) != key; });
            Check("CACHE-evolve-speed", () => { var r = req; r.EvolveSpeed = 24; return NoiseTextureFactory.Key(r) != key; });
            Check("CACHE-rounded-stability", () => { var r = req; r.WidthWorld += .0001f; return NoiseTextureFactory.Key(r) == key; });
            Check("CACHE-resolution-bucket-stability", () => { var r = req; r.TargetPixelWidth += 2f; return NoiseTextureFactory.Key(r) == key; });
            Check("CACHE-package-sticky-bucket", () =>
                NoiseTextureFactory.TryStickyBucket(req.Provider, 197, 192, out int held, out _) && held == 192 &&
                NoiseTextureFactory.TryStickyBucket(req.Provider, 300, 192, out int moved, out _) && moved == 288);
            Check("CACHE-ready-state", () => { var r = req; r.Ready = !r.Ready; return NoiseTextureFactory.Key(r) != key; });
            Check("CACHE-piece-geometry", () =>
            {
                var r = req; r.Pieces = new[] { new NoiseTextureFactory.Piece { Half = Vector2.one } };
                string rectangle = NoiseTextureFactory.Key(r);
                r.Pieces[0].Circle = true;
                return NoiseTextureFactory.Key(r) != rectangle;
            });
            Check("CACHE-key-sandbox", () =>
            {
                try { DeclarativeCacheKey<NoiseTextureFactory.Request>.Parse(System.Text.Encoding.UTF8.GetBytes(
                    "{\"version\":1,\"values\":[{\"path\":\"Provider\"}]}")); return false; }
                catch (ArgumentException) { }
                var missing = new EventManifest { SourcePath = req.Provider.SourcePath };
                var bad = req; bad.Provider = missing;
                return !NoiseTextureFactory.TryKey(bad, out _, out var error) && !string.IsNullOrEmpty(error);
            });
            var timed = req;
            timed.Pieces = Enumerable.Range(0, 4).Select(i => new NoiseTextureFactory.Piece {
                Offset = new Vector2(i * .2f, 0), Half = new Vector2(.5f, .5f), Rotation = i * 9f }).ToArray();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 500; i++) NoiseTextureFactory.Key(timed);
            watch.Stop();
            double millisecondsPerKey = watch.Elapsed.TotalMilliseconds / 500;
            Row("CACHE-key-mono-performance", millisecondsPerKey < .5 ? "PASS" : "FAIL",
                $"four regions, 500 warm keys: {millisecondsPerKey:F3} ms/key");
            string manifestJson = System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(req.Provider.SourcePath, "event.json"));
            var coldProvider = EventManifest.Parse(manifestJson, req.Provider.SourcePath, out _);
            var cold = timed; cold.Provider = coldProvider;
            watch.Restart(); NoiseTextureFactory.Key(cold); watch.Stop();
            Row("CACHE-key-cold-load", watch.Elapsed.TotalMilliseconds < 100 ? "PASS" : "FAIL",
                $"package resource and specification: {watch.Elapsed.TotalMilliseconds:F1} ms");
            try
            {
                var piece = new NoiseTextureFactory.Piece { Half = new Vector2(1f, 0.7f),
                    FillColor = Color.red, EdgeColor = Color.red, FillOpacity = 0.65f, EdgeOpacity = 0.8f, Band = 0.2f };
                var pixels = NoiseComposer.Compose(new[] { piece }, req, 48, 32, 0.7f, 42);
                var bytes = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    bytes[i * 4] = pixels[i].r; bytes[i * 4 + 1] = pixels[i].g;
                    bytes[i * 4 + 2] = pixels[i].b; bytes[i * 4 + 3] = pixels[i].a;
                }
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    string hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                    Row("LEGACY-noise-baseline", hash == "A239194121FF97E023D150DE3A2E3F733675C89B7E04453B8B9BEAFA901EBDF7" ? "PASS" : "FAIL", hash);
                }
            }
            catch (Exception e) { Row("LEGACY-noise-baseline", "FAIL", e.Message); }
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
            EventRegistry.TryGetById("pev.noise-field", out var first);
            Check("FIELD-provider-required", () =>
            {
                var missing = new Dictionary<string, object> { ["__instance"] = "review/no-provider", ["relativeTo"] = "Camera",
                    ["size"] = new[] { 2f, 2f }, ["frameCount"] = 2 };
                bool accepted = NoiseFieldRenderer.Set("field", missing, out _);
                NoiseFieldRenderer.DestroyAll();
                return !accepted;
            });
            var args = new Dictionary<string, object>
            {
                { "__manifest", first },
                { "__instance", "review/field" }, { "relativeTo", "Camera" },
                { "size", new[] { 2f, 2f } }, { "frameCount", 2 }, { "frame", 0 }, { "depth", 123 }
            };
            NoiseFieldRenderer.Set("field", args, out _);
            args["depth"] = 124;
            NoiseFieldRenderer.Set("field", args, out _);
            Check("FIELD-depth-change", () => NoiseFieldRenderer.LayerCount == 1);
            NoiseFieldRenderer.SetGpuRendering(true);
            NoiseFieldRenderer.Set("field", args, out _);
            Check("FIELD-gpu-takeover-suspends-bake", () => NoiseFieldRenderer.LayerCount == 0 && NoiseFieldRenderer.LiveCount == 1);
            NoiseFieldRenderer.SetGpuRendering(false);
            Check("FIELD-gpu-failure-restores-legacy", () => NoiseFieldRenderer.LayerCount == 1 && NoiseFieldRenderer.LiveCount == 1);
            var other = new EventManifest { Id = "example.other", SourcePath = first.SourcePath, Version = "other" };
            foreach (var item in first.VectorPrograms) other.VectorPrograms[item.Key] = item.Value;
            foreach (var item in first.KeySpecs) other.KeySpecs[item.Key] = item.Value;
            foreach (var item in first.RuntimeBindings) other.RuntimeBindings[item.Key] = item.Value;
            foreach (var item in first.PixelResources) other.PixelResources[item.Key] = item.Value;
            var otherArgs = new Dictionary<string, object>(args) { ["__instance"] = "review/other", ["__manifest"] = other };
            NoiseFieldRenderer.Set("field", otherArgs, out _);
            NoiseFieldRenderer.SetGpuProviders(new HashSet<EventManifest> { first });
            Check("FIELD-gpu-provider-isolation", () => NoiseFieldRenderer.LayerCount == 1 && NoiseFieldRenderer.LiveCount == 2);
            NoiseFieldRenderer.SetGpuProviders(new HashSet<EventManifest>());
            Check("FIELD-gpu-provider-fallback", () => NoiseFieldRenderer.LayerCount == 2 && NoiseFieldRenderer.LiveCount == 2);
            NoiseFieldRenderer.Destroy("field", otherArgs, out _);

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
            else
            {
                var cache = typeof(NoiseTextureFactory).GetField("Cache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)
                    as System.Collections.IDictionary;
                var details = new List<string>();
                foreach (System.Collections.DictionaryEntry item in cache)
                {
                    var set = (BakedFrameCache.Frames)item.Value;
                    details.Add($"{set.TexW}x{set.TexH} frames={set.Pixels?.Length} built={set.Pixels?.Count(x => x != null)} " +
                        $"ready={set.PixelsReady}/{set.Ready} worker={set.Worker?.IsAlive} error={set.Error}");
                }
                Row("FIELD-first-texture", "FAIL", "timeout; " + string.Join("; ", details));
            }
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

            args["visible"] = true;
            args["position"] = new[] { 0f, 0f };
            args["__instance"] = "review/layer-a";
            NoiseFieldRenderer.Set("field", args, out _);
            args["__instance"] = "review/layer-b";
            NoiseFieldRenderer.Set("field", args, out _);
            var layerA = NoiseFieldRenderer.AllEntries().Single(e => e.Scope == "review/layer-a");
            layerA.UnityLayer = layerA.UnityLayer == 7 ? 9 : 7;
            NoiseFieldRenderer.Set("field", args, out _); // rebuild with one differing culling layer
            Check("FIELD-unity-layer-isolation", () => NoiseFieldRenderer.LayerCount == 2);
            NoiseFieldRenderer.DestroyAll();

            string paletteSource = null;
            if (EventRegistry.TryGetById("pev.noise-field", out var paletteManifest) &&
                paletteManifest.VectorPrograms.TryGetValue("palette", out var palettePath))
            {
                byte[] script = EventLoader.ReadResource(paletteManifest.SourcePath, palettePath);
                if (script != null) paletteSource = System.Text.Encoding.UTF8.GetString(script);
            }
            if (paletteSource == null) Row("FIELD-color-worker-runtime", "SKIP", "package has no vector program");
            else using (var worker = new StructuredVectorWorker<NoiseFieldColorOwnership.Zone, int>(paletteSource, 2))
            {
                var zones = new[] { new NoiseFieldColorOwnership.Zone {
                    CenterX = 0, CenterY = 0, HalfX = 0.5f, HalfY = 0.5f, YScale = 1f,
                    Opacity = 1f, Active = true, Red = 0f, Green = 1f, Blue = 0f } };
                worker.Update(16, 8, 2f, zones, 0xff5454);
                float workerDeadline = Time.realtimeSinceStartup + 5f;
                while (worker.Latest == null && worker.LastError == null && Time.realtimeSinceStartup < workerDeadline)
                {
                    worker.Update(16, 8, 2f, zones, 0xff5454);
                    yield return null;
                }
                Check("FIELD-color-worker-runtime", () => worker.LastError == null && worker.Latest != null &&
                    worker.Latest[0][0] == 0 && worker.Latest[0][1] == 255 && worker.Latest[0][2] == 0);
                var many = new[] { zones[0], zones[0], zones[0], zones[0] };
                var timing = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 5000; i++) worker.Update(16, 8, 2f, many, 0xff5454);
                timing.Stop();
                double cost = timing.Elapsed.TotalMilliseconds / 5000;
                Row("VECTOR-structured-snapshot-performance", cost < .1 ? "PASS" : "FAIL", $"four records, 5000 updates: {cost:F4} ms/update");
            }
            Check("FIELD-color-texture-upload", () =>
            {
                var rgba = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 };
                var texture = VectorTextureSet<GenericFrameProbe, int>.MakeTexture(2, 2, "PEV.ReviewPalette", rgba,
                    FilterMode.Point, TextureWrapMode.Clamp, true);
                try
                {
                    var pixels = texture.GetPixels32();
                    return pixels.Length == 4 && pixels[0].r == 255 && pixels[1].g == 255 &&
                           pixels[2].b == 255 && pixels[3].r == 255 && pixels[3].g == 255 && pixels[3].b == 255 &&
                           texture.filterMode == FilterMode.Point && texture.wrapMode == TextureWrapMode.Clamp;
                }
                finally { UnityEngine.Object.Destroy(texture); }
            });
            yield return ProbeNativeGrabOrderingSafe();

            var row = new LevelEvent(1, LevelEventType.AddDecoration);
            row["visible"] = false;
            var stored = new EventInstanceData { Id = "pev.noise-field" };
            stored.Set("fillOpacity", 0.23f);
            stored.Set("noiseFrameCount", 7);
            stored.Set("noiseBrightness", 1.7f);
            // Adopt normally places the saved values on the live AddDecoration row.
            row["fillOpacity"] = 0.23f;
            row["noiseFrameCount"] = 7;
            row["noiseBrightness"] = 1.7f;
            NoiseFieldRenderer.Describe("review/description", "noisefield", row, stored);
            Check("FIELD-description-params", () =>
            {
                var field = NoiseFieldRenderer.AllEntries().Single();
                return Mathf.Abs(field.FillOpacity - 0.23f) < 0.001f && field.FrameCount == 7 &&
                       Mathf.Abs(field.Brightness - 1.7f) < 0.001f && !field.Visible;
            });
            NoiseFieldRenderer.Set("noisefield", new Dictionary<string, object>
                { ["__instance"] = "review/description", ["invert"] = true }, out _);
            NoiseFieldRenderer.Destroy("noisefield", new Dictionary<string, object> { ["__instance"] = "review/description" }, out _);
            Check("FIELD-description-reset-params", () =>
                Mathf.Abs(NoiseFieldRenderer.AllEntries().Single().FillOpacity - 0.23f) < 0.001f &&
                !NoiseFieldRenderer.AllEntries().Single().GpuInverted);
            NoiseFieldRenderer.DestroyAll();
            NoiseFieldRenderer.UnbindAll();
        }

        private static IEnumerator ProbeNativeGrabOrderingSafe()
        {
            var probe = ProbeNativeGrabOrdering();
            while (true)
            {
                bool next;
                try { next = probe.MoveNext(); }
                catch (Exception e) { Row("FIELD-built-in-grab-sorting", "CHECK", e.Message); yield break; }
                if (!next) yield break;
                yield return probe.Current;
            }
        }

        private static IEnumerator ProbeNativeGrabOrdering()
        {
            var shader = Shader.Find("Hidden/BlendModes/VisualDeco/Grab");
            if (shader == null || !shader.isSupported) { Row("FIELD-built-in-grab-sorting", "SKIP unavailable shader"); yield break; }
            var objects = new List<GameObject>();
            RenderTexture target = null;
            Texture2D read = null;
            Material material = null;
            Material secondMaterial = null;
            var prior = RenderTexture.active;
            try
            {
                var cameraGo = new GameObject("PEV.ReviewGrabCamera"); objects.Add(cameraGo);
                var camera = cameraGo.AddComponent<Camera>(); camera.enabled = false; camera.orthographic = true;
                camera.orthographicSize = 1; camera.aspect = 1; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black; camera.cullingMask = unchecked((int)0x80000000u);
                cameraGo.transform.position = new Vector3(0, 0, -10);
                target = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
                if (!target.Create()) throw new InvalidOperationException("Cannot create grab probe target");
                camera.targetTexture = target;
                read = new Texture2D(64, 64, TextureFormat.RGBA32, false, true);
                Func<string, Color, int, float, float, SpriteRenderer> sprite = (name, color, order, x, size) =>
                {
                    var go = new GameObject(name); objects.Add(go); go.layer = 31;
                    go.transform.position = new Vector3(x, 0, 0); go.transform.localScale = new Vector3(size, size, 1);
                    var renderer = go.AddComponent<SpriteRenderer>(); renderer.sprite = SolidSprite.White;
                    renderer.color = color; renderer.sortingOrder = order; return renderer;
                };
                var background = sprite("PEV.ReviewGrabBack", Color.red, 0, 0, 2);
                sprite("PEV.ReviewGrabFront", Color.green, 10, .5f, 1);
                var fieldRoot = new GameObject("PEV.ReviewGrabGroup"); objects.Add(fieldRoot); fieldRoot.layer = 31;
                var field = sprite("PEV.ReviewGrabField", Color.white, 0, 0, 2);
                field.transform.SetParent(fieldRoot.transform, true);
                var sorting = fieldRoot.AddComponent<UnityEngine.Rendering.SortingGroup>();
                sorting.sortingOrder = 5;
                material = new Material(shader); material.SetColor("_Color", new Color(.2f, .5f, 1f, 1f));
                material.SetFloat("_Opacity", 1f); material.SetVector("_Tile", new Vector4(1, 1, 0, 0));
                // This game shader selects its sampling operation through a keyword;
                // without one it falls back to a solid-color path and proves no capture.
                material.EnableKeyword("BLENDMODES_MODE_MULTIPLY");
                field.sharedMaterial = material;
                var secondRoot = new GameObject("PEV.ReviewGrabSecondGroup"); objects.Add(secondRoot); secondRoot.layer = 31;
                var second = sprite("PEV.ReviewGrabSecond", Color.white, 0, 0, 2);
                second.transform.SetParent(secondRoot.transform, true);
                var secondSorting = secondRoot.AddComponent<UnityEngine.Rendering.SortingGroup>();
                secondSorting.sortingOrder = 7;
                secondMaterial = new Material(shader); secondMaterial.SetColor("_Color", new Color(.5f, 1f, 1f, 1f));
                secondMaterial.SetFloat("_Opacity", 1f); secondMaterial.SetVector("_Tile", new Vector4(1, 1, 0, 0));
                secondMaterial.EnableKeyword("BLENDMODES_MODE_MULTIPLY"); second.sharedMaterial = secondMaterial;
                secondRoot.SetActive(false);
                Func<Color32[]> capture = () =>
                {
                    try
                    {
                        camera.Render(); RenderTexture.active = target;
                        read.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); read.Apply(false, false);
                        var pixels = read.GetPixels32(); return new[] { pixels[32 * 64 + 16], pixels[32 * 64 + 48] };
                    }
                    finally { RenderTexture.active = prior; }
                };
                fieldRoot.SetActive(false); var before = capture();
                fieldRoot.SetActive(true); var after = capture();
                bool backgroundChanged = !before[0].Equals(after[0]);
                bool foregroundPreserved = before[1].Equals(after[1]);
                Row("FIELD-built-in-grab-sorting", backgroundChanged && foregroundPreserved ? "PASS" : "CHECK",
                    $"back {before[0]}->{after[0]}, front {before[1]}->{after[1]}; built-in shader only");
                background.color = Color.blue;
                var alternate = capture();
                Row("FIELD-built-in-grab-sampling", !after[0].Equals(alternate[0]) ? "PASS" : "CHECK",
                    $"back changed red to blue; rendered {after[0]}->{alternate[0]}; this does not test the draft shader");
                background.color = Color.red;
                second.color = Color.clear;
                secondRoot.SetActive(true);
                yield return null; // allow Unity to update renderer/culling registration
                second.color = Color.white;
                var twice = capture();
                bool secondSeesFirst = Math.Abs(twice[0].r - after[0].r * .5f) <= 2 && twice[1].Equals(after[1]);
                fieldRoot.SetActive(false);
                yield return null;
                var secondOnly = capture();
                Row("FIELD-built-in-grab-two-layers", secondSeesFirst ? "PASS" : "CHECK",
                    $"first {after[0]}, both {twice[0]}, second alone {secondOnly[0]}, foreground {twice[1]}; built-in shader only");
                if (!secondSeesFirst)
                {
                    Row("FIELD-built-in-grab-diagnostic", "INFO",
                        $"second active={second.gameObject.activeInHierarchy} enabled={second.enabled} bounds={second.bounds} " +
                        $"layer={second.gameObject.layer} sort={secondSorting.sortingOrder} tint={second.sharedMaterial.GetColor("_Color")} " +
                        $"keyword={second.sharedMaterial.IsKeywordEnabled("BLENDMODES_MODE_MULTIPLY")}");
                    second.sharedMaterial = background.sharedMaterial;
                    second.color = Color.blue;
                    yield return null;
                    var solidOnly = capture();
                    Row("FIELD-built-in-grab-solid-control", "INFO", $"second blue solid control={solidOnly[0]}");
                }
            }
            finally
            {
                RenderTexture.active = prior;
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                if (read != null) UnityEngine.Object.Destroy(read);
                if (material != null) UnityEngine.Object.Destroy(material);
                if (secondMaterial != null) UnityEngine.Object.Destroy(secondMaterial);
                foreach (var go in objects) if (go != null) UnityEngine.Object.Destroy(go);
            }
        }
    }
}
