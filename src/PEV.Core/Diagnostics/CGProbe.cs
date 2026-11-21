#if PEV_DIAGNOSTICS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ADOFAI;
using HarmonyLib;
using PEV.Scripting;
using UnityEngine;

namespace PEV.Diagnostics
{
    /// <summary>
    /// Opt-in probe for the colour-grading render path.
    ///
    /// The question it answers is the one the effect cannot be built without: which camera's
    /// OnRenderImage sees the finished gameplay picture, in the editor and during playback,
    /// and what that picture actually contains. Guessing here would mean shipping a grade
    /// that silently misses the background or the video layer.
    ///
    /// Enabled by a `cg-probe.txt` file in the mod directory; writes to `cg-probe/`.
    /// </summary>
    public static class CGProbe
    {
        private static string _dir;
        private static bool _ran;
        private static readonly List<RenderProbe> Probes = new List<RenderProbe>();

        public static void Configure(string modDir)
        {
            try
            {
                if (!File.Exists(Path.Combine(modDir, "cg-probe.txt"))) return;
                _dir = Path.Combine(modDir, "cg-probe");
                Directory.CreateDirectory(_dir);
                File.WriteAllText(Path.Combine(_dir, "probe.txt"), "");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.Log("[PEV] cg probe configure failed: " + e.Message);
            }
        }

        private static void Row(string name, string result, string note = null)
        {
            if (_dir == null) return;
            File.AppendAllText(Path.Combine(_dir, "probe.txt"),
                name + "\t" + result + (note == null ? "" : "\t" + note) + Environment.NewLine);
        }

        [HarmonyPatch(typeof(scnLevelSelect), "Start")]
        public static class AutoOpen
        {
            public static void Postfix()
            {
                if (_dir == null || _ran) return;
                scnEditor.levelToOpenOnLoad = Environment.GetEnvironmentVariable("PEV_CG_CHART") ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "A Dance of Fire and Ice", "Levels", "CGProbe.adofai");
                ADOBase.loader.LoadScene("scnEditor");
            }
        }

        [HarmonyPatch(typeof(scnEditor), "Start")]
        public static class Entry
        {
            public static void Postfix(scnEditor __instance)
            {
                if (_dir == null || _ran) return;
                _ran = true;
                __instance.StartCoroutine(Run(__instance));
            }
        }

        private static IEnumerator Run(scnEditor editor)
        {
            yield return new WaitForSeconds(2.5f);
            Row("ENVIRONMENT", "INFO", FrameCapture.DescribeEnvironment());

            yield return Phase(editor, "editor-idle");

            bool played = false;
            try { editor.Play(); played = true; }
            catch (Exception e) { Row("PLAY", "FAIL", e.Message); }
            if (played)
            {
                yield return new WaitForSeconds(3f);
                yield return Phase(editor, "play");
            }

            GradingHarness.Run(Row, _dir);
            Row("DONE", "ok");
            File.AppendAllText(Path.Combine(_dir, "probe.txt"), "");
        }

        private static IEnumerator Phase(scnEditor editor, string label)
        {
            DumpCameras(label);
            Capture(label);
            yield return CaptureBackbuffer(label + "-ui");
            yield return CaptureTabs(editor, label);
            Attach(label);
            yield return new WaitForSeconds(1.2f);
            Report(label);
            Detach();
            yield return null;
        }

        /// <summary>Photographs each grading panel tab so the widgets can be inspected.</summary>
        private static IEnumerator CaptureTabs(scnEditor editor, string label)
        {
            var panel = editor.GetComponent<PEV.Editor.ScreenEffectPanel>();
            if (panel == null) { Row(label + "/panel", "MISSING"); yield break; }
            Row(label + "/panel", "present");
            for (int tab = 0; tab < 6; tab++)
            {
                panel.ProbeTab(tab);
                yield return null;
                yield return null;
                yield return CaptureBackbuffer(label + "-tab" + tab);
            }
            var view = typeof(PEV.Editor.ScreenEffectPanel).GetField("_scopes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(panel);
            var dump = view?.GetType().GetMethod("Dump", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            if (dump != null) { try { dump.Invoke(view, new object[] { _dir }); } catch { } }
            panel.ProbeTab(0);
        }

        /// <summary>
        /// Reads the presented frame, IMGUI included.
        ///
        /// A camera render target cannot contain the editor's own widgets, so checking the
        /// grading panel needs the back buffer itself. This runs once per phase and only in
        /// the probe: a per-frame readback would cost more than the effect being measured.
        /// </summary>
        private static IEnumerator CaptureBackbuffer(string label)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                int w = Screen.width, h = Screen.height;
                if (w <= 0 || h <= 0) { Row(label, "FAIL", "degenerate viewport"); yield break; }
                var read = new Texture2D(w, h, TextureFormat.RGBA32, false);
                try
                {
                    read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    read.Apply(false);
                    var pixels = read.GetPixels32();
                    var png = NoiseTextures.EncodePng(pixels, w, h);
                    if (png != null && png.Length > 0)
                    {
                        File.WriteAllBytes(Path.Combine(_dir, label + ".png"), png);
                        Row(label, "OK", $"{w}x{h} bytes={png.Length}");
                    }
                    else Row(label, "FAIL", "png encode produced no bytes");
                }
                finally { UnityEngine.Object.Destroy(read); }
            }
            catch (Exception e) { Row(label, "FAIL", e.Message); }
        }

        private static void Capture(string label)
        {
            try
            {
                var camera = scrCamera.instance != null ? scrCamera.instance.camobj : null;
                var result = FrameCapture.Capture(camera, Path.Combine(_dir, label + "-screen.png"));
                Row(label + "/screen", result.Ok ? "OK" : "FAIL", result.Detail);
            }
            catch (Exception e) { Row(label + "/screen", "FAIL", e.Message); }
        }

        private static void DumpCameras(string label)
        {
            Camera[] cameras;
            try { cameras = Camera.allCameras; }
            catch (Exception e) { Row(label + "/cameras", "FAIL", e.Message); return; }
            Row(label + "/camera-count", cameras.Length.ToString());
            foreach (var cam in cameras)
            {
                if (cam == null) continue;
                string target = cam.targetTexture != null
                    ? $"{cam.targetTexture.name}:{cam.targetTexture.width}x{cam.targetTexture.height}"
                    : "screen";
                Row(label + "/camera", cam.name,
                    $"depth={cam.depth} enabled={cam.enabled} type={cam.cameraType} " +
                    $"clear={cam.clearFlags} target={target} mask=0x{cam.cullingMask:X} " +
                    $"ortho={cam.orthographic} size={cam.orthographicSize:F2} " +
                    $"px={cam.pixelWidth}x{cam.pixelHeight} active={cam.gameObject.activeInHierarchy} " +
                    $"rect={cam.rect}");
            }

            try
            {
                var scr = scrCamera.instance;
                if (scr != null)
                {
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    var useR = typeof(scrCamera).GetField("useRTCam", flags);
                    var rt = typeof(scrCamera).GetField("camRT", flags);
                    var quadMesh = typeof(scrCamera).GetField("camQuadMesh", flags);
                    object useV = useR?.GetValue(scr);
                    var rtV = rt?.GetValue(scr) as RenderTexture;
                    var meshV = quadMesh?.GetValue(scr) as MeshRenderer;
                    Row(label + "/scrCamera", "INFO",
                        $"useRTCam={useV} camRT={(rtV == null ? "null" : rtV.width + "x" + rtV.height)} " +
                        $"quadMesh={(meshV == null ? "null" : meshV.name)} quadActive={(scr.quad != null && scr.quad.activeSelf)} " +
                        $"overlayActive={(scr.Overlaycam != null && scr.Overlaycam.gameObject.activeInHierarchy)}");
                    if (meshV != null && meshV.material != null && meshV.material.mainTexture != null)
                        Row(label + "/quad-texture", "INFO", meshV.material.mainTexture.name +
                            $" {meshV.material.mainTexture.width}x{meshV.material.mainTexture.height} shader={meshV.material.shader.name}");
                }
            }
            catch (Exception e) { Row(label + "/scrCamera", "FAIL", e.Message); }
        }

        private static void Attach(string label)
        {
            Detach();
            var scr = scrCamera.instance;
            if (scr == null) { Row(label + "/attach", "FAIL", "no scrCamera"); return; }
            AddProbe(label, scr.camobj, "camobj");
            AddProbe(label, scr.Overlaycam, "Overlaycam");
            AddProbe(label, scr.BGcam, "BGcam");
            AddProbe(label, scr.Bgcamstatic, "Bgcamstatic");
            AddProbe(label, scr.PausePlanetsCam, "PausePlanetsCam");
            Row(label + "/attach", "ok", Probes.Count + " probes");
        }

        private static void AddProbe(string label, Camera cam, string name)
        {
            if (cam == null) return;
            try
            {
                var probe = cam.gameObject.AddComponent<RenderProbe>();
                probe.Label = label + "/" + name;
                probe.Dir = _dir;
                probe.TakeSnapshot = true;
                Probes.Add(probe);
            }
            catch (Exception e) { Row(label + "/attach/" + name, "FAIL", e.Message); }
        }

        private static void Report(string label)
        {
            foreach (var probe in Probes)
            {
                if (probe == null) continue;
                Row(probe.Label, probe.Calls > 0 ? "FIRED" : "silent",
                    $"calls={probe.Calls} src={probe.LastWidth}x{probe.LastHeight} " +
                    $"mean={probe.LastMean:F3} distinct={probe.LastDistinct} {probe.LastNote}");
            }
        }

        private static void Detach()
        {
            foreach (var probe in Probes)
                if (probe != null) UnityEngine.Object.DestroyImmediate(probe);
            Probes.Clear();
        }
    }

    /// <summary>Records what a camera's image-effect chain actually receives, then passes it on.</summary>
    public class RenderProbe : MonoBehaviour
    {
        public string Label;
        public string Dir;
        public bool TakeSnapshot;
        public int Calls;
        public int LastWidth, LastHeight, LastDistinct;
        public float LastMean;
        public string LastNote = "";

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            Calls++;
            if (source == null) { LastNote = "source null"; return; }
            LastWidth = source.width;
            LastHeight = source.height;
            if (TakeSnapshot && Calls >= 2)
            {
                TakeSnapshot = false;
                try { Snapshot(source); }
                catch (Exception e) { LastNote = "snapshot failed: " + e.Message; }
            }
            Graphics.Blit(source, destination);
        }

        private void Snapshot(RenderTexture source)
        {
            var previous = RenderTexture.active;
            Texture2D read = null;
            try
            {
                RenderTexture.active = source;
                read = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                read.Apply(false);
            }
            finally { RenderTexture.active = previous; }

            var pixels = read.GetPixels32();
            var seen = new HashSet<int>();
            double sum = 0;
            long n = 0;
            for (int y = 0; y < source.height; y += 4)
            {
                int row = y * source.width;
                for (int x = 0; x < source.width; x += 4)
                {
                    var c = pixels[row + x];
                    sum += (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f;
                    n++;
                    seen.Add((c.r << 16) | (c.g << 8) | c.b);
                }
            }
            LastMean = n > 0 ? (float)(sum / n) : 0f;
            LastDistinct = seen.Count;

            // Sample the four corners and the centre so a background colour is identifiable
            // without opening the image.
            Func<int, int, string> at = (x, y) =>
            {
                var c = pixels[y * source.width + x];
                return $"{c.r:x2}{c.g:x2}{c.b:x2}";
            };
            LastNote = "tl=" + at(2, source.height - 3) + " tr=" + at(source.width - 3, source.height - 3) +
                       " bl=" + at(2, 2) + " br=" + at(source.width - 3, 2) +
                       " mid=" + at(source.width / 2, source.height / 2);

            var png = NoiseTextures.EncodePng(pixels, source.width, source.height);
            if (png != null && png.Length > 0)
            {
                string file = Label.Replace('/', '-') + ".png";
                File.WriteAllBytes(Path.Combine(Dir, file), png);
                LastNote += " saved=" + file;
            }
            UnityEngine.Object.Destroy(read);
        }
    }
}
#endif
