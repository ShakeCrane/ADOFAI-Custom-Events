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
            Check("PIXEL-package-resource-limits", CheckPackagePixelResourceLimits);
            Check("PIXEL-kernel-mono-performance", CheckPixelKernelPerformance);
            CheckKeys();
            yield return CheckTextures();
            yield return CheckFields();
            string vectorProbe = Environment.GetEnvironmentVariable("PEV_TEST_VECTOR_SCRIPT");
            if (!string.IsNullOrEmpty(vectorProbe))
            {
                CheckGenericVectorProbe(vectorProbe);
                yield return CheckVectorWorker();
                yield return CheckPackagedVectorWorker();
                yield return CheckPackagePixelResources();
            }
            if (Environment.GetEnvironmentVariable("PEV_TEST_REAL_FIELD_BUNDLE") == "1")
                yield return CheckRealFieldBundle();
            yield return ProbeGlobalSettingsGui();
            CheckEditorPlacement(editor);
            Row("DONE", "ok");
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
            foreach (var item in manifest.FieldShaders)
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
            Row("GPU-real-bundle-assets", assets && manifest.FieldShaders.Count == 11 ? "PASS" : "FAIL");
            if (!assets) yield break;
            Check("GPU-package-graph-parity", () => CheckGpuGraphParity(manifest));
            Check("GPU-package-graph-limits", () => CheckGpuGraphLimits(manifest));
            Check("GPU-package-region-modes", () => CheckRegionModes(manifest));
            Check("GPU-package-compositor-limits", () => CheckCompositorLimits(manifest));
            Check("GPU-package-contacts-parity", () => CheckContactControllerParity(manifest));
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
            NoiseFieldGpuMasks masks = null;
            LegacyGpuLayerReference legacy = null;
            NoiseFieldGpuLayer current = null;
            var prior = RenderTexture.active;
            try
            {
                byte[] graphData = EventLoader.ReadResource(manifest.SourcePath, manifest.ComputeGraphs["coverage"], 131072);
                var graph = GpuGraphDefinition.Parse(graphData);
                var compositor = CompositorDefinition.Parse(graphData);
                var shader = ShaderBundleCache.LoadCompute(manifest, manifest.FieldShaders["compute"], out string error);
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
                var areas = new[] {
                    new NoiseFieldGpuMasks.Area { Center = new Vector2(-.2f,0), HalfSize = new Vector2(.5f,.6f), YScale = 1, Opacity = 1, Flags = 1 },
                    new NoiseFieldGpuMasks.Area { Center = new Vector2(.35f,0), HalfSize = new Vector2(.25f,.35f), YScale = 1, Opacity = .8f, Flags = 4 }
                };
                var touches = new[] { new NoiseFieldGpuMasks.Touch { Center = new Vector2(.5f,.5f), Scale = 1f } };
                masks.Render(areas, areas.Length, 128, 128, 1f, 1f, displacement, touches, 1, touch);
                activeColors = new Texture2D(masks.Width, masks.Height, TextureFormat.RGBA32, false, true);
                auxiliaryColors = new Texture2D(masks.Width, masks.Height, TextureFormat.RGBA32, false, true);
                activeColors.SetPixels32(Enumerable.Repeat(new Color32(20, 220, 80, 255), masks.Width * masks.Height).ToArray());
                auxiliaryColors.SetPixels32(Enumerable.Repeat(new Color32(30, 70, 220, 255), masks.Width * masks.Height).ToArray());
                activeColors.Apply(false, false); auxiliaryColors.Apply(false, false);
                legacy = new LegacyGpuLayerReference(manifest, 0, 0, 30);
                current = new NoiseFieldGpuLayer(manifest, compositor, 0, 0, 31);
                var positions = new Vector4[FieldContactController.Capacity];
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
                    current.Draw(camera, masks, displacement, spark, noise, 1f, 1f, captureScene, hover,
                        showDisabled, showActive, Color.black, positions, hover ? 1 : 0, first, second);
                    yield return null;
                    var before = capture(30); var after = capture(31);
                    if (!before.SequenceEqual(backgroundOnly)) visibleScenarios++;
                    if (!before.SequenceEqual(after))
                    { Row("GPU-compositor-parity", "FAIL", $"scenario {i}, differing pixels {before.Zip(after, (a,b) => !a.Equals(b)).Count(x => x)}"); yield break; }
                    scenarios++;
                }
                Row("GPU-compositor-parity", scenarios == 24 && visibleScenarios == 24 ? "PASS" : "FAIL",
                    scenarios + " material and layer combinations; " + visibleScenarios + " visibly rendered");
            }
            finally
            {
                current?.Dispose(); legacy?.Dispose(); masks?.Dispose(); RenderTexture.active = prior;
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                if (read != null) UnityEngine.Object.Destroy(read);
                if (activeColors != null) UnityEngine.Object.Destroy(activeColors);
                if (auxiliaryColors != null) UnityEngine.Object.Destroy(auxiliaryColors);
                if (cameraObject != null) UnityEngine.Object.Destroy(cameraObject);
            }
        }

        private static bool CheckRegionModes(EventManifest manifest)
        {
            var bytes = EventLoader.ReadResource(manifest.SourcePath, manifest.ComputeGraphs["coverage"], 131072);
            var root = JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            var spec = (JObject)root["regionModes"];
            var values = (JObject)spec["values"];
            values["Custom"] = new JObject { ["shaderFlags"] = 8, ["mainLayer"] = false,
                ["auxLayer"] = true, ["palette"] = true, ["contacts"] = false };
            spec["default"] = "Custom"; spec["tagKey"] = "myMode";
            var modes = RegionModes.Parse(System.Text.Encoding.UTF8.GetBytes(root.ToString()));
            var custom = modes.Resolve("unknown");
            if (modes.TagKey != "myMode" || custom.ShaderFlags != 8 || custom.MainLayer || !custom.AuxiliaryLayer ||
                !custom.Palette || custom.Contacts || modes.Resolve("ready").ShaderFlags != 4) return false;
            values["Custom"]["shaderFlags"] = 2;
            try { RegionModes.Parse(System.Text.Encoding.UTF8.GetBytes(root.ToString())); return false; }
            catch (ArgumentException) { }
            return true;
        }

        private static bool CheckContactControllerParity(EventManifest manifest)
        {
            string path = manifest.VectorPrograms["contacts"];
            string source = System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, path, 262144));
            var actual = new FieldContactController(source);
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
                var points = new List<FieldContactController.Contact>();
                for (int i = 0; i < frame % 13; i++)
                    points.Add(new FieldContactController.Contact { Id = i, Position = new Vector2((float)random.NextDouble(), (float)random.NextDouble()) });
                var hits = points.Where(p => NoiseFieldTouchGeometry.Contains(legacyAreas,
                    new Vector2(p.Position.x * 2f - 1f, (p.Position.y * 2f - 1f) / (16f / 9f)))).ToArray();
                expected.Update(hits.Select(p => new NoiseFieldTouchHistory.Contact { Id = p.Id, Position = p.Position }).ToArray(), time);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                actual.Update(areas, points, 16f / 9f, time);
                watch.Stop(); sumMs += watch.Elapsed.TotalMilliseconds; worstMs = Math.Max(worstMs, watch.Elapsed.TotalMilliseconds);
                if (actual.Visible != expected.Visible || actual.Contacts.Count != Math.Min(hits.Length, FieldContactController.Capacity))
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

        private static bool CheckGpuGraphLimits(EventManifest manifest)
        {
            var data = EventLoader.ReadResource(manifest.SourcePath, manifest.ComputeGraphs["coverage"], 131072);
            string source = System.Text.Encoding.UTF8.GetString(data);
            var invalid = new List<string> { "{}", source + "{}", source.Replace("\"divisor\": 8", "\"divisor\": 0"),
                source.Replace("\"format\": \"r32uint\"", "\"format\": \"unknown\""),
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
            var shader = ShaderBundleCache.LoadCompute(manifest, manifest.FieldShaders["compute"], out var error);
            if (shader == null) throw new InvalidOperationException(error);
            var noise = ReferenceNoiseTextures.Load(manifest, "displacement", out error);
            if (noise == null) throw new InvalidOperationException(error);
            var touchTexture = ReferenceNoiseTextures.Load(manifest, "touch", out error);
            if (touchTexture == null) throw new InvalidOperationException(error);
            manifest.LegacyFieldProfile.TryGetValue("glowWeights", out var weights);
            var areas = new[] {
                new NoiseFieldGpuMasks.Area { Center = new Vector2(-.15f,.05f), HalfSize = new Vector2(.55f,.3f), AngleRadians = .35f, YScale = 1f, Opacity = .75f, Flags = 1 },
                new NoiseFieldGpuMasks.Area { Center = new Vector2(.32f,-.12f), HalfSize = new Vector2(.25f,.4f), AngleRadians = -.17f, YScale = 1f, Opacity = 1f, Flags = 3 },
                new NoiseFieldGpuMasks.Area { Center = Vector2.zero, HalfSize = new Vector2(.2f,.2f), YScale = 1f, Opacity = .5f, Flags = 4 },
                new NoiseFieldGpuMasks.Area { Center = new Vector2(0,.3f), HalfSize = new Vector2(.3f,.1f), YScale = 1f, Opacity = 1f, Flags = 0 }
            };
            var touches = new[] { new NoiseFieldGpuMasks.Touch { Center = new Vector2(.5f,.5f), Scale = 1f } };
            using (var legacy = new LegacyGpuMaskReference(shader, weights))
            using (var graph = new NoiseFieldGpuMasks(shader, definition))
            {
                for (int i = 0; i < 8; i++)
                {
                    int width = i % 2 == 0 ? 256 : 255, height = i % 2 == 0 ? 144 : 143;
                    int count = i == 0 ? 0 : areas.Length, touchCount = i % 2;
                    float time = i * .77f;
                    legacy.Render(areas, count, width, height, 16f / 9f, time, noise, touches, touchCount, touchTexture);
                    var oldMain = ReadGpuPixels(legacy.Effect); var oldAux = ReadGpuPixels(legacy.Auxiliary);
                    graph.Render(areas, count, width, height, 16f / 9f, time, noise, touches, touchCount, touchTexture);
                    if (!oldMain.SequenceEqual(ReadGpuPixels(graph.Effect)) || !oldAux.SequenceEqual(ReadGpuPixels(graph.Auxiliary)))
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
                coordinator.Render(entries, camera, 1f, false, Color.black);
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
                const string json = "{\"id\":\"another.author\",\"schemaVersion\":3,\"apiVersion\":2,\"hostPrimitive\":\"fx.field\",\"globalSettings\":[{\"name\":\"authorChoice\",\"type\":\"Bool\",\"default\":true,\"binding\":\"fx.field.captureScene\"}]}";
                var manifest = EventManifest.Parse(json, "", out _);
                var store = new PEV.Core.EventGlobalSettings();
                if (manifest == null || !store.GetBoundBool(manifest, "fx.field.captureScene", false)) return false;
                store.Set(manifest, manifest.GlobalSettings[0], false);
                return !store.GetBoundBool(manifest, "fx.field.captureScene", true) &&
                    store.GetBoundBool(manifest, "not.declared", true);
            });
            Check("FIELD-event-uniform-declarations", () =>
            {
                const string valid = "{\"id\":\"example.shader\",\"schemaVersion\":3,\"hostPrimitive\":\"fx.field\",\"fieldWaves\":{\"_Pulse\":{\"base\":0.63,\"amplitude\":0.37,\"frequency\":43,\"scale\":2}},\"fieldUniforms\":{\"_Intensity\":0.5,\"_Tint\":[1,0,0.25,1]}}";
                var manifest = EventManifest.Parse(valid, "", out _);
                return manifest != null && manifest.FieldUniforms.Count == 2 && manifest.FieldWaves.Count == 1 &&
                    Mathf.Abs(manifest.FieldWaves[0].At(0) - 1.63f) < 0.00001f &&
                    manifest.FieldUniforms[0].Values[0] == 0.5f && manifest.FieldUniforms[1].Values[2] == 0.25f &&
                    EventManifest.Parse(valid.Replace("_Intensity", "uView"), "", out _) == null &&
                    EventManifest.Parse(valid.Replace("[1,0,0.25,1]", "[1,2]"), "", out _) == null;
            });
            Check("FIELD-event-uniform-material", () =>
            {
                var manifest = EventManifest.Parse("{\"id\":\"example\",\"schemaVersion\":3,\"hostPrimitive\":\"fx.field\",\"fieldUniforms\":{\"_Color\":[0.25,0.5,0.75,1]}}", "", out _);
                var shader = Shader.Find("Hidden/BlendModes/VisualDeco/Grab");
                if (manifest == null || shader == null) return false;
                var material = new Material(shader);
                try
                {
                    NoiseFieldGpuLayer.ApplyUniforms(material, manifest.FieldUniforms);
                    var value = material.GetVector("_Color");
                    return value == new Vector4(0.25f, 0.5f, 0.75f, 1f);
                }
                finally { UnityEngine.Object.Destroy(material); }
            });
            Check("FIELD-event-asset-roles", () =>
            {
                if (!EventRegistry.TryGetById("pev.noise-field", out var manifest) || manifest.SchemaVersion < 3) return true;
                if (!manifest.FieldTextures.ContainsKey("displacement") || !manifest.FieldShaders.ContainsKey("compute") ||
                    manifest.FieldShaders.Count != 11 || manifest.FieldTextures.Count != 4) return false;
                var json = JObject.Parse(System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, "event.json")));
                json["fieldTextures"]["touch"] = "../outside";
                return EventManifest.Parse(json.ToString(), "", out _) == null;
            });
            Check("FIELD-custom-shader-roles", () =>
            {
                if (!EventRegistry.TryGetById("pev.noise-field", out var manifest)) return false;
                var json = JObject.Parse(System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, "event.json")));
                json["fieldShaders"] = new JObject { ["compute"] = "assets/compute", ["customLayer"] = "assets/layer" };
                var parsed = EventManifest.Parse(json.ToString(), "", out _);
                if (parsed == null || parsed.FieldShaders.Count != 2 || !parsed.FieldShaders.ContainsKey("customLayer")) return false;
                json["fieldShaders"]["customLayer"] = "../escape";
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
                var wave = new FieldWaveSpec { Base = 0.63f, Amplitude = 0.37f, Frequency = 43f, Scale = 2f };
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
                    if (!NoiseFieldProjection.TryProject(cam, frame, cam.aspect, out var result)) return false;
                    float expected = 1f / (5f * cam.aspect);
                    if (result.Center.sqrMagnitude > 0.000001f || Mathf.Abs(result.HalfSize.x - 2 * expected) > 0.00001f ||
                        Mathf.Abs(result.HalfSize.y - expected) > 0.00001f || Mathf.Abs(result.AngleRadians) > 0.00001f) return false;
                    frame.Rotation = 45;
                    if (!NoiseFieldProjection.TryProject(cam, frame, cam.aspect, out result) ||
                        Mathf.Abs(result.AngleRadians - Mathf.PI / 4) > 0.0001f) return false;
                    frame.Center = go.transform.position;
                    if (!NoiseFieldProjection.TryProject(cam, frame, cam.aspect, out result)) return false;
                    cam.orthographic = false;
                    return !NoiseFieldProjection.TryProject(cam, frame, cam.aspect, out _);
                }
                finally { UnityEngine.Object.Destroy(go); }
            });
            Check("FIELD-gpu-plan-groups", () =>
            {
                var go = new GameObject("PEV.ReviewGpuPlan");
                try
                {
                    if (!EventRegistry.TryGetById("pev.noise-field", out var provider) || !provider.ComputeGraphs.TryGetValue("coverage", out var graphPath)) return false;
                    var modes = RegionModes.Parse(EventLoader.ReadResource(provider.SourcePath, graphPath, 131072));
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
                    var plan = new NoiseFieldGpuPlan();
                    plan.Build(new[] { a, b, c }, camera, camera.aspect, modes, values);
                    if (plan.Groups.Count != 2) return false;
                    var key = new NoiseFieldGpuPlan.Key { SortingOrder = 1, UnityLayer = 7 };
                    if (!plan.Groups.TryGetValue(key, out var group) || group.Areas.Count != 2 ||
                        group.Areas[0].Flags != 6 || group.Areas[1].Flags != 1 || !group.HasActive || !group.HasDisabled ||
                        group.Colors[1].Red != 0f || group.Colors[1].Blue != 1f || !group.Colored) return false;
                    plan.Build(new[] { a }, camera, camera.aspect, modes, values);
                    return plan.Groups.Count == 1 && plan.Groups[key].Areas.Count == 1;
                }
                finally { UnityEngine.Object.Destroy(go); }
            });
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
                    manifest.FieldTextures["displacement"] = "assets/BlockNoise1.png";
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
                    first.LegacyFieldTextures["block"] = first.LegacyFieldTextures["point"] = "tex.png";
                    second.LegacyFieldTextures["block"] = second.LegacyFieldTextures["point"] = "tex.png";
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
                a.LegacyFieldTextures["block"] = b.LegacyFieldTextures["block"] = "block.png";
                a.LegacyFieldTextures["point"] = b.LegacyFieldTextures["point"] = "point.png";
                a.LegacyFieldProfile["sample"] = new[] { 1f };
                b.LegacyFieldProfile["sample"] = new[] { 2f };
                return LegacyFieldTextureSet.Identity(a) != LegacyFieldTextureSet.Identity(b);
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
            var args = new Dictionary<string, object>
            {
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
            var first = LegacyFieldTextureSet.ResolveProvider(null);
            var other = new EventManifest { Id = "example.other", SourcePath = first.SourcePath, Version = "other" };
            foreach (var item in first.LegacyFieldTextures) other.LegacyFieldTextures[item.Key] = item.Value;
            foreach (var item in first.LegacyFieldProfile) other.LegacyFieldProfile[item.Key] = item.Value;
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
            else using (var worker = new NoiseFieldColorWorker(paletteSource))
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
                    worker.Latest.Active[0] == 0 && worker.Latest.Active[1] == 255 && worker.Latest.Active[2] == 0);
            }
            Check("FIELD-color-texture-upload", () =>
            {
                var rgba = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 };
                var texture = NoiseFieldGpuPalette.MakeTexture(2, 2, "PEV.ReviewPalette", rgba);
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
