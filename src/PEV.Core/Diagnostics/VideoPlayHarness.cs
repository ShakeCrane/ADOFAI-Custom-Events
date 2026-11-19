using System;
using System.Collections;
using System.IO;
using System.Text;
using ADOFAI;
using HarmonyLib;
using PEV.Scripting;
using UnityEngine;

namespace PEV.Diagnostics
{
    /// <summary>
    /// Plays a chart in real gameplay and checks the video appears and follows the clock.
    ///
    /// The editor harness covers the editor and its play mode, but a chart in gameplay takes a
    /// different route: scnGame loads the level, no editor exists, and the runtime's ownership
    /// rule takes its other branch. That branch is worth one test of its own rather than being
    /// trusted, because it is the path every player takes.
    ///
    /// Inert unless "video-play-test.txt" exists next to the DLL.
    /// </summary>
    public static class VideoPlayHarness
    {
        private static string _resultPath, _chartPath;
        private static bool _launched;

        public static bool Enabled { get; private set; }

        public static void Configure(string modDir)
        {
            Enabled = File.Exists(Path.Combine(modDir, "video-play-test.txt"));
            if (!Enabled) return;

            _resultPath = Path.Combine(modDir, "video-play-results.txt");
            _chartPath = Environment.GetEnvironmentVariable("PEV_VIDEO_TEST_CHART");
            try { File.WriteAllText(_resultPath, ""); } catch { }
            Row("PLAY-HARNESS", "enabled", _chartPath);
        }

        public static void Row(string key, string value, string note = "")
        {
            string line = key + "\t" + value + "\t" + note;
            try { Debug.Log("[PEV-VIDEO-PLAY] " + line); } catch { }
            try { File.AppendAllText(_resultPath, line + Environment.NewLine); } catch { }
        }

        [HarmonyPatch(typeof(scnLevelSelect), "Start")]
        public static class AutoStart
        {
            public static void Postfix()
            {
                if (!Enabled || _launched) return;
                _launched = true;
                try
                {
                    if (string.IsNullOrEmpty(_chartPath) || !File.Exists(_chartPath))
                    {
                        Row("PLAY-SETUP", "FAIL", "chart missing: " + _chartPath);
                        File.AppendAllText(_resultPath, "DONE\tfail\n");
                        return;
                    }

                    // Gameplay is entered the way the level list enters it: point GCS at the
                    // chart and load the scnGame scene. scnGame.Update then calls its own
                    // LoadAndPlayLevel, so this exercises the real path rather than a
                    // harness-only shortcut.
                    GCS.customLevelPaths = new[] { _chartPath };
                    GCS.customLevelIndex = 0;
                    GCS.internalLevelName = null;
                    ADOBase.loader.LoadScene("scnGame");
                    Row("PLAY-SETUP", "loading", _chartPath);

                    var host = new GameObject("PEV.VideoPlayProbe");
                    UnityEngine.Object.DontDestroyOnLoad(host);
                    var runner = host.AddComponent<Runner>();
                    runner.Bind(_resultPath);
                }
                catch (Exception e)
                {
                    Row("PLAY-SETUP", "EXCEPTION", (e.GetType().Name + ": " + e.Message).Replace('\n', ' '));
                    try { File.AppendAllText(_resultPath, "DONE\tfail\n"); } catch { }
                }
            }
        }

        public class Runner : MonoBehaviour
        {
            private string _resultPath;
            public void Bind(string path) { _resultPath = path; }
            private void Start() { StartCoroutine(Run()); }

            private IEnumerator Run()
            {
                // Wait for the scene to exist and the level to finish decoding.
                float deadline = Time.realtimeSinceStartup + 60f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    if (ADOBase.customLevel != null && scnGame.instance != null && !scnEditorIsLoading())
                        break;
                    yield return null;
                }
                yield return null;

                Row("PLAY-SCENE", ADOBase.customLevel != null ? "PASS" : "FAIL",
                    "scnGame instance " + (scnGame.instance != null ? "present" : "absent"));

                // The video must prepare and decode without any editor involvement.
                string report = "";
                float ready = Time.realtimeSinceStartup + 45f;
                while (Time.realtimeSinceStartup < ready)
                {
                    yield return null;
                    report = VideoSurface.Report();
                    if (report.IndexOf("prepared=True", StringComparison.Ordinal) >= 0 &&
                        report.IndexOf("frame=True", StringComparison.Ordinal) >= 0) break;
                }

                Row("PLAY-RENDERS", report.IndexOf("frame=True", StringComparison.Ordinal) >= 0 ? "PASS" : "FAIL",
                    Truncate(report, 300));

                // And it must advance with the song clock, which is the whole point of driving
                // playback from lookups rather than from a delta the script accumulates.
                double first = -1, second = -1;
                float watch = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < watch)
                {
                    yield return null;
                    double now = ParseTime(VideoSurface.Report());
                    if (now > 0.1 && first < 0) first = now;
                    else if (first >= 0 && now > first + 0.15) { second = now; break; }
                }

                Row("PLAY-ADVANCES", second > first && first >= 0 ? "PASS" : "FAIL",
                    "media " + first.ToString("F2") + " -> " + second.ToString("F2") + " in gameplay");

                File.AppendAllText(_resultPath, "DONE\tok\n");
            }

            private static bool scnEditorIsLoading()
            {
                try { return ADOBase.editor != null && ADOBase.editor.isLoading; }
                catch { return false; }
            }

            private static double ParseTime(string report)
            {
                if (string.IsNullOrEmpty(report)) return -1;
                foreach (var part in report.Split(';'))
                {
                    if (part.IndexOf("frame=True", StringComparison.Ordinal) < 0) continue;
                    int at = part.IndexOf(" t=", StringComparison.Ordinal);
                    if (at < 0) continue;
                    int start = at + 3;
                    int end = part.IndexOf(' ', start);
                    double value;
                    if (double.TryParse(part.Substring(start, Math.Max(0, end - start)),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out value))
                        return value;
                }
                return -1;
            }

            private static string Truncate(string text, int max) =>
                string.IsNullOrEmpty(text) ? "" : (text.Length <= max ? text : text.Substring(0, max) + "…");
        }
    }
}
