using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ADOFAI;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PEV.Registry;
using PEV.Scripting;
using PEV.Storage;
using UnityEngine;

namespace PEV.Diagnostics
{
    /// <summary>
    /// End-to-end acceptance for the video event, inside the real editor.
    ///
    /// Everything asserted here is a fact a unit test cannot supply: that the decoration row the
    /// palette creates is a genuine AddDecoration, that its panel exposes the video parameters
    /// and hides the masking ones, that a placed row renders while the editor is idle, that the
    /// surface follows a drag and a depth change, that playback seeks to the clock, that a
    /// missing file produces a diagnosis instead of a crash, and that the chart still saves and
    /// reopens with the same data.
    ///
    /// Inert unless "video-test.txt" exists next to the DLL. The fixture chart is read from
    /// PEV_VIDEO_TEST_CHART; media comes from PEV_VIDEO_TEST_MEDIA.
    /// </summary>
    public static class VideoHarness
    {
        private static string _resultPath, _mediaRoot, _chartPath;
        private static bool _ran;

        public static bool Enabled { get; private set; }

        public static void Configure(string modDir)
        {
            Enabled = File.Exists(Path.Combine(modDir, "video-test.txt"));
            if (!Enabled) return;

            _resultPath = Path.Combine(modDir, "video-results.txt");
            _mediaRoot = Environment.GetEnvironmentVariable("PEV_VIDEO_TEST_MEDIA");
            _chartPath = Environment.GetEnvironmentVariable("PEV_VIDEO_TEST_CHART");
            try { File.WriteAllText(_resultPath, ""); } catch { }
            Row("HARNESS", "enabled", "chart=" + _chartPath + " media=" + _mediaRoot);
        }

        public static void Row(string key, string value, string note = "")
        {
            string line = key + "\t" + value + "\t" + note;
            try { Debug.Log("[PEV-VIDEO] " + line); } catch { }
            try { File.AppendAllText(_resultPath, line + Environment.NewLine); } catch { }
        }

        private static string One(Exception e) =>
            (e.GetType().Name + ": " + e.Message).Replace('\n', ' ').Replace('\r', ' ');

        [HarmonyPatch(typeof(scnLevelSelect), "Start")]
        public static class AutoOpen
        {
            public static void Postfix()
            {
                if (!Enabled || _ran) return;
                _ran = true;
                try
                {
                    if (string.IsNullOrEmpty(_chartPath) || !File.Exists(_chartPath))
                    {
                        Row("SETUP", "FAIL", "chart missing: " + _chartPath);
                        File.AppendAllText(_resultPath, "DONE\tfail\n");
                        return;
                    }
                    scnEditor.levelToOpenOnLoad = _chartPath;
                    ADOBase.loader.LoadScene("scnEditor");
                    Row("SETUP", "opening", _chartPath);
                }
                catch (Exception e)
                {
                    Row("SETUP", "EXCEPTION", One(e));
                    try { File.AppendAllText(_resultPath, "DONE\tfail\n"); } catch { }
                }
            }
        }

        [HarmonyPatch(typeof(scnEditor), "Start")]
        public static class Probe
        {
            public static void Postfix(scnEditor __instance)
            {
                if (!Enabled) return;
                if (!string.Equals(ADOBase.levelPath, _chartPath, StringComparison.OrdinalIgnoreCase)) return;
                if (Run._started) return;
                Run._started = true;
                ((MonoBehaviour)__instance).StartCoroutine(new Run().Execute(__instance));
            }
        }

        private sealed class Run
        {
            public static bool _started;
            private scnEditor _editor;

            public IEnumerator Execute(scnEditor editor)
            {
                _editor = editor;
                yield return null;
                yield return null;

                // Wait for the level to finish decoding before touching its rows.
                float deadline = Time.realtimeSinceStartup + 20f;
                while ((editor.isLoading || scnGame.instance == null) && Time.realtimeSinceStartup < deadline)
                    yield return null;
                yield return null;

                foreach (var step in CheckRegistration()) yield return step;
                foreach (var step in CheckPlacement()) yield return step;
                foreach (var step in CheckEditorRender()) yield return step;
                foreach (var step in CheckTimingMath()) yield return step;
                foreach (var step in CheckTimeline()) yield return step;
                foreach (var step in CheckFormatMatrix()) yield return step;
                foreach (var step in CheckConversion()) yield return step;
                foreach (var step in CheckMultiInstance()) yield return step;
                foreach (var step in CheckPerformance()) yield return step;
                foreach (var step in CheckSaveReload()) yield return step;
                foreach (var step in CheckPlayback()) yield return step;
                foreach (var step in CheckReopen()) yield return step;
                foreach (var step in CheckAudio()) yield return step;
                foreach (var step in CheckDiagnostics()) yield return step;

                File.AppendAllText(_resultPath, "DONE\tok\n");
            }

            // ------------------------------------------------------------ reopen

            /// <summary>
            /// Saves the chart, closes it, and opens it again.
            ///
            /// Reopening is where a storage bug becomes visible: the panel in memory can hold
            /// values that never reached the file, and the restore path can drop ones that did.
            /// The session here is the game's own — the same decode, the same editor start.
            /// </summary>
            private IEnumerable CheckReopen()
            {
                if (_placed == null) { Row("REOPEN", "SKIP", "no placed row"); yield break; }

                string saved = "";
                try { saved = _editor.levelData.Encode(); }
                catch (Exception e) { Row("REOPEN", "EXCEPTION", One(e)); yield break; }

                // Write the encoded chart next to the fixture and reopen it through the same
                // entry point the editor uses, so this is not a second, synthetic path.
                string reopened = _chartPath + ".reopen.adofai";
                try { File.WriteAllText(reopened, saved); }
                catch (Exception e) { Row("REOPEN", "EXCEPTION", "cannot write: " + One(e)); yield break; }

                try { _editor.OpenLevel(reopened); }
                catch (Exception e) { Row("REOPEN", "EXCEPTION", One(e)); TryDelete(reopened); yield break; }

                // Wait for the decode to finish and for the row to be restored.
                float deadline = Time.realtimeSinceStartup + 25f;
                LevelEvent restored = null;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    if (_editor.isLoading) continue;
                    var level = _editor.levelData;
                    var rows = level != null ? level.decorations : null;
                    if (rows == null) continue;
                    foreach (var row in rows)
                    {
                        if (row == null) continue;
                        if (!LevelStorage.TryGetData(row, out var data)) continue;
                        if (data.Id == "pev.video") { restored = row; break; }
                    }
                    if (restored != null) break;
                }

                Row("REOPEN-ROW", restored != null ? "PASS" : "FAIL",
                    restored == null ? "video row did not come back" : "uid=" + UidOf(restored));

                if (restored != null)
                {
                    LevelStorage.TryGetData(restored, out var data);
                    bool sameUid = UidOf(restored) == UidOf(_placed);
                    Row("REOPEN-IDENTITY", sameUid ? "PASS" : "FAIL",
                        "before=" + UidOf(_placed) + " after=" + UidOf(restored));

                    // The surface must come back too: a row that exists but draws nothing is a
                    // regression the row check alone would miss.
                    float draw = Time.realtimeSinceStartup + 25f;
                    string report = "";
                    while (Time.realtimeSinceStartup < draw)
                    {
                        yield return null;
                        report = VideoSurface.Report();
                        if (report.Contains("prepared=True") && report.Contains("frame=True")) break;
                    }
                    Row("REOPEN-RENDERS", report.Contains("frame=True") ? "PASS" : "FAIL", Truncate(report, 220));

                    _placed = restored;
                }

                TryDelete(reopened);
            }

            private static void TryDelete(string path)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }

            // ------------------------------------------------------------- audio

            /// <summary>
            /// Checks the audio track is wired to a controllable source.
            ///
            /// What is verified is that a file with an audio stream reports one, that the
            /// surface drives volume and mute onto an AudioSource, and that the parametrised
            /// values actually reach it. Whether the sound is *audible* is not something a
            /// headless run can assert, and is listed as unverified rather than claimed.
            /// </summary>
            private IEnumerable CheckAudio()
            {
                if (string.IsNullOrEmpty(_mediaRoot)) { Row("AUDIO", "SKIP", "no media root"); yield break; }
                string path = Path.Combine(_mediaRoot, "q-h264-audio.mp4");
                if (!File.Exists(path)) { Row("AUDIO", "SKIP", "no fixture"); yield break; }

                var args = new Dictionary<string, object>
                {
                    { "__instance", "audio" }, { "key", "video" }, { "file", path },
                    { "time", 0.0 }, { "playing", true }, { "volume", 0.25 }, { "muted", false }
                };
                VideoSurface.Set("video", args, out _);

                float deadline = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    if (EntryReport("audio/video").IndexOf("prepared=True", StringComparison.Ordinal) >= 0) break;
                    yield return null;
                }
                // Audio is applied on the refresh tick, not by the call itself; give the
                // runtime a frame to run it before reading the source back.
                for (int i = 0; i < 5; i++) yield return null;

                Row("AUDIO-TRACK", VideoSurface.AudioReport("audio", "video"), "track count and volume wiring");

                // Muting must reach the source, not just the parameter.
                args["muted"] = true;
                VideoSurface.Set("video", args, out _);
                for (int i = 0; i < 3; i++) yield return null;
                Row("AUDIO-MUTE", VideoSurface.AudioReport("audio", "video"), "muted=true");

                VideoSurface.Destroy("video", new Dictionary<string, object> { { "__instance", "audio" } }, out _);
            }

            // ---------------------------------------------------------- playback

            /// <summary>
            /// Presses play and checks the video follows the song clock.
            ///
            /// Everything before this drives the surface directly, which proves the machinery
            /// but not the product: the claim is that a placed video event advances with the
            /// song, holds when paused, and lands on the right frame after a jump. Only real
            /// playback and a real transport control can show that.
            /// </summary>
            private IEnumerable CheckPlayback()
            {
                if (_placed == null || _editor == null) { Row("PLAY", "SKIP", "no placed row"); yield break; }

                string before = TimePosition();

                try { _editor.Play(); }
                catch (Exception e) { Row("PLAY", "EXCEPTION", One(e)); yield break; }

                // The countdown runs before the song starts; give it enough frames for the
                // clock to begin, then sample the surface twice.
                float deadline = Time.realtimeSinceStartup + 25f;
                double first = -1, second = -1;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    double now = ParseTime(EntryReport("pev.video#" + UidOf(_placed) + "/video"));
                    if (now > 0.2 && first < 0) first = now;
                    else if (first >= 0 && now > first + 0.15) { second = now; break; }
                }

                Row("PLAY-ADVANCES", second > first && first >= 0 ? "PASS" : "FAIL",
                    "media " + first.ToString("F2") + " -> " + second.ToString("F2") + " during playback");

                // Pausing the song must freeze the picture; a decoder left running is exactly
                // the desync the whole clock-driven design exists to prevent. The pause goes
                // through the controller, because pausedInPlayMode only touches camera settings.
                bool paused = false;
                try
                {
                    if (ADOBase.controller != null) { ADOBase.controller.TogglePauseGame(); paused = true; }
                }
                catch (Exception e) { Row("PLAY-PAUSE", "EXCEPTION", One(e)); }

                for (int i = 0; i < 5; i++) yield return null;
                double held1 = ParseTime(EntryReport("pev.video#" + UidOf(_placed) + "/video"));
                for (int i = 0; i < 45; i++) yield return null;
                double held2 = ParseTime(EntryReport("pev.video#" + UidOf(_placed) + "/video"));
                Row("PLAY-HOLDS", Math.Abs(held2 - held1) < 0.12 ? "PASS" : "FAIL",
                    "paused at " + held1.ToString("F2") + ", 45 frames later " + held2.ToString("F2") +
                    " (pauseIssued=" + paused + ")");

                // Resuming must continue from where the song is, not resume a stale frame.
                try { if (ADOBase.controller != null) ADOBase.controller.TogglePauseGame(); }
                catch { }
                for (int i = 0; i < 45; i++) yield return null;
                double resumed = ParseTime(EntryReport("pev.video#" + UidOf(_placed) + "/video"));
                Row("PLAY-RESUMES", resumed > held2 + 0.05 ? "PASS" : "FAIL",
                    "resumed to " + resumed.ToString("F2") + " from " + held2.ToString("F2"));

                try { _editor.SwitchToEditMode(); }
                catch (Exception e) { Row("PLAY-EXIT", "EXCEPTION", One(e)); }
                for (int i = 0; i < 5; i++) yield return null;

                Row("PLAY-IDLE-RETURN", "INFO", "before=" + before + " after-exit=" + TimePosition());
            }

            private static string UidOf(LevelEvent row)
            {
                return LevelStorage.TryGetData(row, out var data) ? data.Uid : "";
            }

            /// <summary>The decoded position from one surface's report line.</summary>
            private static double ParseTime(string report)
            {
                if (string.IsNullOrEmpty(report)) return -1;
                int at = report.IndexOf(" t=", StringComparison.Ordinal);
                if (at < 0) at = report.StartsWith("t=", StringComparison.Ordinal) ? -1 : -2;
                if (at == -2) return -1;
                int start = at < 0 ? 2 : at + 3;
                int end = report.IndexOf(' ', start);
                double value;
                return double.TryParse(report.Substring(start, Math.Max(0, end - start)),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out value) ? value : -1;
            }

            // ---------------------------------------------------- package timing math

            /// <summary>
            /// Runs the shipped script with a synthetic clock and checks the media positions
            /// it asks for.
            ///
            /// This is the only test of the arithmetic that actually ships: the package's
            /// main.js is loaded through the same sandbox and the same host bridge, so a
            /// mistake in beat conversion, looping or clipping shows up as a wrong number
            /// rather than as a video that "looks a bit off".
            /// </summary>
            private IEnumerable CheckTimingMath()
            {
                if (!EventRegistry.TryGetById("pev.video", out var manifest))
                {
                    Row("MATH", "FAIL", "pev.video not registered");
                    yield break;
                }

                string source = EventScriptCache.Get(manifest);
                if (source == null) { Row("MATH", "FAIL", "script unreadable"); yield break; }

                var host = ScriptHost.Create(source, "math-test", out string createError, manifest);
                if (host == null) { Row("MATH", "FAIL", "script did not compile: " + createError); yield break; }

                VideoSurface.Capture.Clear();
                VideoSurface.CaptureActive = true;
                try
                {
                    // 120 BPM -> 0.5 s per beat. The tile sits at song time 4.0, so a video
                    // with start=2 begins at 5.0 and 1 second of song is 1 second of media.
                    var context = new EventContext
                    {
                        Bpm = 120,
                        Time = 4.0,
                        EventTime = 0,
                        TileTime = 4.0,
                        T = 4.0,
                        FloorSeq = 8,
                        Params = new Dictionary<string, object>(StringComparer.Ordinal)
                    };

                    var parameters = new Dictionary<string, object>
                    {
                        { "file", "x.mp4" },
                        { "start", 2.0 },
                        { "speed", 1.0 },
                        { "trimStart", 0.0 },
                        { "trimEnd", 0.0 },
                        { "loop", false },
                        { "holdEnd", true },
                        { "mode", "Fit" },
                        { "volume", 100.0 },
                        { "muted", true }
                    };
                    context.Params = parameters;

                    if (!host.Initialize(context, parameters, out string initError))
                    {
                        Row("MATH", "FAIL", "create threw: " + initError);
                        yield break;
                    }

                    // Before the clip starts the surface must hold the first frame, not play.
                    Drive(host, context, 4.0);
                    var before = LastCapture();
                    Row("MATH-BEFORE-START", before != null && !Truth(before, "playing") &&
                        Near(before, "time", 0.0) ? "PASS" : "FAIL", DescribeLast(before));

                    // At exactly the start moment playback begins at the clip's in point.
                    Drive(host, context, 5.0);
                    var atStart = LastCapture();
                    Row("MATH-START", atStart != null && Truth(atStart, "playing") &&
                        Near(atStart, "time", 0.0) ? "PASS" : "FAIL", DescribeLast(atStart));

                    // Two seconds later the media position must have advanced by two seconds.
                    Drive(host, context, 7.0);
                    var later = LastCapture();
                    Row("MATH-PROGRESS", later != null && Near(later, "time", 2.0) ? "PASS" : "FAIL",
                        DescribeLast(later));

                    // Speed scales media time against song time, not the other way round.
                    parameters["speed"] = 2.0;
                    host.Dispose();
                    host = ScriptHost.Create(source, "math-test2", out _, manifest);
                    context = new EventContext
                    {
                        Bpm = 120, Time = 5.0, EventTime = 0, TileTime = 4.0, T = 5.0,
                        FloorSeq = 8, Params = parameters
                    };
                    if (host.Initialize(context, parameters, out _))
                    {
                        Drive(host, context, 5.0);
                        Drive(host, context, 6.0);
                        var fast = LastCapture();
                        Row("MATH-SPEED", fast != null && Near(fast, "time", 2.0) ? "PASS" : "FAIL",
                            DescribeLast(fast));
                    }
                    else Row("MATH-SPEED", "FAIL", "re-create failed");

                    // A backwards clock jump must land back at the earlier media position,
                    // which is what makes editor scrubbing and checkpoint jumps work.
                    Drive(host, context, 5.5);
                    var rewound = LastCapture();
                    Row("MATH-REWIND", rewound != null && Near(rewound, "time", 1.0) ? "PASS" : "FAIL",
                        DescribeLast(rewound));
                }
                catch (Exception e) { Row("MATH", "EXCEPTION", One(e)); }
                finally
                {
                    VideoSurface.CaptureActive = false;
                    try { host.Dispose(); } catch { }
                }

                foreach (var step in CheckTempoChange()) yield return step;
            }

            /// <summary>
            /// A tempo change before the tile must not move when the clip starts.
            ///
            /// The beat offset is measured from the tile, so its conversion needs the tempo at
            /// the tile. A video instance starts at the level start, where the tempo is the
            /// chart's opening one — using it would start the clip at the wrong moment on any
            /// chart that changes BPM in between. This drives exactly that shape.
            /// </summary>
            private IEnumerable CheckTempoChange()
            {
                if (!EventRegistry.TryGetById("pev.video", out var manifest)) yield break;
                string source = EventScriptCache.Get(manifest);
                if (source == null) yield break;

                var host = ScriptHost.Create(source, "math-bpm", out string error, manifest);
                if (host == null) { Row("MATH-BPM", "FAIL", "script did not compile: " + error); yield break; }

                VideoSurface.Capture.Clear();
                VideoSurface.CaptureActive = true;
                try
                {
                    var parameters = new Dictionary<string, object>
                    {
                        { "file", "x.mp4" }, { "start", 2.0 }, { "speed", 1.0 },
                        { "trimStart", 0.0 }, { "trimEnd", 0.0 }, { "loop", false },
                        { "holdEnd", true }, { "mode", "Fit" }, { "volume", 100.0 }, { "muted", true }
                    };
                    var context = new EventContext
                    {
                        Bpm = 60, Time = 0, EventTime = 0, TileTime = 4.0, T = 0, FloorSeq = 8,
                        Params = parameters
                    };
                    if (!host.Initialize(context, parameters, out string initError))
                    {
                        Row("MATH-BPM", "FAIL", "create threw: " + initError);
                        yield break;
                    }

                    // Opening tempo 60 (1 s/beat). The tile sits at 4.0 s; with the tile tempo
                    // at 120 the two-beat offset is 1 s, so the clip must begin at 5.0 s. Using
                    // the opening tempo would give 6.0 s instead.
                    Drive(host, context, 1.0);
                    context.Bpm = 120;
                    Drive(host, context, 4.5);
                    Drive(host, context, 4.9);
                    var beforeStart = LastCapture();
                    Row("MATH-BPM-BEFORE", beforeStart != null && !Truth(beforeStart, "playing") ? "PASS" : "FAIL",
                        "4.9s is before the real 5.0s start; " + DescribeLast(beforeStart));

                    Drive(host, context, 5.1);
                    var afterStart = LastCapture();
                    // 0.1 s into the clip, so media 0.1. Starting at 6.0 would still be holding.
                    Row("MATH-BPM-AFTER", afterStart != null && Truth(afterStart, "playing") &&
                        Near(afterStart, "time", 0.1) ? "PASS" : "FAIL",
                        "5.1s should be 0.1s into the clip; " + DescribeLast(afterStart));
                }
                catch (Exception e) { Row("MATH-BPM", "EXCEPTION", One(e)); }
                finally
                {
                    VideoSurface.CaptureActive = false;
                    try { host.Dispose(); } catch { }
                }

                // Hold-last-frame off must actually take the picture down at the end, not
                // merely stop advancing. The two choices are different images and the package
                // can only express the second by telling the host to hide.
                var holder = ScriptHost.Create(source, "math-end", out _, manifest);
                if (holder == null) { Row("MATH-HOLD", "FAIL", "script did not compile"); yield break; }
                VideoSurface.Capture.Clear();
                VideoSurface.CaptureActive = true;
                VideoSurface.CaptureDuration = 3.0;
                try
                {
                    var keep = new Dictionary<string, object>
                    {
                        { "file", "x.mp4" }, { "start", 0.0 }, { "speed", 1.0 },
                        { "trimStart", 0.0 }, { "trimEnd", 0.0 }, { "loop", false },
                        { "holdEnd", false }, { "mode", "Fit" }, { "volume", 100.0 }, { "muted", true }
                    };
                    var context = new EventContext
                    {
                        Bpm = 120, Time = 0, EventTime = 0, TileTime = 1.0, T = 0, FloorSeq = 8,
                        Params = keep
                    };
                    if (!holder.Initialize(context, keep, out string initError))
                    {
                        Row("MATH-HOLD", "FAIL", "create threw: " + initError);
                        yield break;
                    }

                    // The first update teaches the package the media length; the rest of the
                    // sequence is what the two hold modes differ on.
                    Drive(holder, context, 1.5);
                    Drive(holder, context, 1.6);
                    var inside = LastCapture();
                    Row("MATH-HOLD-INSIDE", inside != null && Truth(inside, "visible") ? "PASS" : "FAIL",
                        "1.6s is inside the clip; " + DescribeLast(inside));

                    // Past the end with holdEnd=false the package must report visible=false.
                    Drive(holder, context, 9.0);
                    var pastEnd = LastCapture();
                    Row("MATH-HOLD-OFF", pastEnd != null && !Truth(pastEnd, "visible") ? "PASS" : "FAIL",
                        "9.0s is past the clip; " + DescribeLast(pastEnd));
                }
                catch (Exception e) { Row("MATH-HOLD", "EXCEPTION", One(e)); }
                finally
                {
                    VideoSurface.CaptureActive = false;
                    VideoSurface.CaptureDuration = 0;
                    try { holder.Dispose(); } catch { }
                }
            }

            private static void Drive(ScriptHost host, EventContext context, double songTime)
            {
                context.Time = songTime;
                context.T = songTime - context.EventTime;
                context.Beat = context.T * context.Bpm / 60.0;
                context.IsPlaying = true;
                host.Update(context.T, context.ToStateJson());
            }

            private static Dictionary<string, object> LastCapture() =>
                VideoSurface.Capture.Count == 0 ? null : VideoSurface.Capture[VideoSurface.Capture.Count - 1];

            private static bool Truth(Dictionary<string, object> args, string key)
            {
                if (args == null || !args.TryGetValue(key, out var v) || v == null) return false;
                try { return Convert.ToBoolean(v); } catch { return false; }
            }

            private static bool Near(Dictionary<string, object> args, string key, double expected)
            {
                if (args == null || !args.TryGetValue(key, out var v) || v == null) return false;
                try { return Math.Abs(Convert.ToDouble(v) - expected) < 0.05; } catch { return false; }
            }

            private static string DescribeLast(Dictionary<string, object> args)
            {
                if (args == null) return "no call captured";
                var note = new StringBuilder();
                foreach (var key in new[] { "time", "playing", "speed", "visible" })
                {
                    if (args.TryGetValue(key, out var v)) note.Append(key).Append('=').Append(v).Append(' ');
                }
                return note.ToString().Trim();
            }

            // ------------------------------------------------------------ checks

            /// <summary>The package must be discoverable and published like any other event.</summary>
            private IEnumerable CheckRegistration()
            {
                bool known = EventRegistry.TryGetById("pev.video", out var manifest);
                Row("REGISTERED", known ? "PASS" : "FAIL",
                    known ? manifest.SourcePath + " v" + manifest.Version : "pev.video not loaded");

                if (!known)
                {
                    Row("REGISTERED-TYPE", "FAIL", "cannot resolve a type without a manifest");
                    yield break;
                }

                bool typed = EventRegistry.TryGetByType((LevelEventType)PEV.Util.StableId.FromEventId(manifest.Id),
                    out var byType);
                Row("REGISTERED-TYPE", typed && byType != null && byType.Id == manifest.Id ? "PASS" : "FAIL",
                    typed ? byType.Id : "no type mapping");
                Row("REGISTERED-PRIMITIVE", manifest.HostPrimitive == "fx.video" ? "PASS" : "FAIL",
                    manifest.HostPrimitive ?? "none");
                Row("REGISTERED-DECORATION", manifest.DecorationBacked ? "PASS" : "FAIL",
                    "DecorationBacked=" + manifest.DecorationBacked);
                yield break;
            }

            /// <summary>Placement must create a real decoration row, and the panel must fit it.</summary>
            private IEnumerable CheckPlacement()
            {
                var level = _editor.levelData;
                int before = level.decorations != null ? level.decorations.Count : 0;
                var type = (LevelEventType)PEV.Util.StableId.FromEventId("pev.video");

                LevelEvent added = null;
                try
                {
                    EditorTestHelpers.SelectFloor(_editor, 1);
                    _editor.AddEventAtSelected(type);
                }
                catch (Exception e)
                {
                    Row("PLACE", "EXCEPTION", One(e));
                    yield break;
                }

                foreach (var row in level.decorations)
                {
                    if (row == null) continue;
                    if (!LevelStorage.TryGetData(row, out var data)) continue;
                    if (data.Id == "pev.video") { added = row; break; }
                }

                Row("PLACE", added != null ? "PASS" : "FAIL",
                    "decorations " + before + " -> " + (level.decorations?.Count ?? 0));

                if (added == null)
                {
                    Row("PLACE-MARKER", "FAIL", "no row carries a payload");
                    yield break;
                }

                Row("PLACE-MARKER", DecorationCarrier.IsOurs(added.GetString("decorationImage")) ? "PASS" : "FAIL",
                    added.GetString("decorationImage"));
                Row("PLACE-PAYLOAD", DecorationCarrier.TryUnpack(added.GetString("maskingTarget"), out _) ? "PASS" : "FAIL",
                    Truncate(added.GetString("maskingTarget"), 80));

                // Parameters must be visible on our rows and absent from native ones. The panel
                // reads AddDecoration's metadata, so this is where showIf has to do its job.
                bool panelOk = false, hiddenOk = false;
                try
                {
                    if (Registry.DecorationParams.TryGetNativeInfo(out var info) && info != null)
                    {
                        panelOk = info.propertiesInfo.ContainsKey("file") &&
                                  info.propertiesInfo.ContainsKey("speed") &&
                                  info.propertiesInfo.ContainsKey("loop");

                        // Hidden rows are suppressed per-row by CheckIfShown, not by the
                        // metadata's control type, so the placed row is what is asked.
                        hiddenOk = IsHiddenFor(info, added, "maskingTarget") &&
                                   IsHiddenFor(info, added, "maskingType");

                        if (!panelOk)
                        {
                            var names = new List<string>(info.propertiesInfo.Keys);
                            names.Sort(StringComparer.Ordinal);
                            Row("PANEL-KEYS", "INFO", Truncate(string.Join(",", names.ToArray()), 500));
                        }
                    }
                }
                catch (Exception e) { Row("PANEL", "EXCEPTION", One(e)); }

                Row("PANEL-PARAMS", panelOk ? "PASS" : "FAIL", "file/speed/loop rows on AddDecoration");
                Row("PANEL-HIDDEN", hiddenOk ? "PASS" : "FAIL", "masking rows stay hidden");

                // The native decoration list must show the event's own name.
                Row("PANEL-LISTNAME", ManifestListName() ?? "?", "declared by the manifest");

                _placed = added;
                yield break;
            }

            /// <summary>
            /// Whether a property is actually hidden for a specific row.
            ///
            /// The panel decides visibility per selected event through CheckIfShown, so
            /// reading the metadata's control type answers a different question than the one
            /// that matters: masking rows are Hidden controls, but our rows must additionally
            /// be suppressed by hiddenDecorationProperties.
            /// </summary>
            private static bool IsHiddenFor(LevelEventInfo info, LevelEvent row, string property)
            {
                if (info == null || row == null) return false;
                try
                {
                    if (!info.propertiesInfo.TryGetValue(property, out var pi) || pi == null) return false;
                    return !pi.CheckIfShown(row, null);
                }
                catch { return false; }
            }

            private LevelEvent _placed;

            private string ManifestListName()
            {
                return EventRegistry.TryGetById("pev.video", out var m) ? m.DecorationListName : null;
            }

            /// <summary>An idle editor must draw the video, follow a move, and honour depth.</summary>
            private IEnumerable CheckEditorRender()
            {
                if (_placed == null) { Row("RENDER", "SKIP", "no placed row"); yield break; }

                // Give the editor preview a few frames to register the row, prepare the clip
                // and decode its first picture. Prepared and "has a frame" are different
                // events: the decoder opens the file first and only produces pixels after
                // playback has started, so waiting on the wrong one reads a race.
                float deadline = Time.realtimeSinceStartup + 20f;
                string report = "";
                while (Time.realtimeSinceStartup < deadline)
                {
                    report = VideoSurface.Report();
                    if (report.Contains("prepared=True") && report.Contains("frame=True")) break;
                    yield return null;
                }

                Row("RENDER-SURFACE", ReportContains(report, "prepared=True") ? "PASS" : "FAIL", Truncate(report, 320));
                Row("RENDER-FRAME", ReportContains(report, "frame=True") ? "PASS" : "FAIL", "decoded at least one frame");

                var dec = DecorationBridge.Find(_placed);
                Row("RENDER-CARRIER", dec != null ? "PASS" : "FAIL", dec == null ? "no scrDecoration" : dec.GetType().Name);

                // Moving the decoration must move the quad: the surface reads the live
                // component each frame, so this is the whole "behaves like a decoration" claim.
                string beforeMove = QuadState();
                try
                {
                    var p = _placed.Get<Vector2>("position");
                    _placed["position"] = new Vector2(p.x + 1.5f, p.y + 0.75f);
                    _editor.UpdateDecorationObject(_placed);
                }
                catch (Exception e) { Row("RENDER-MOVE", "EXCEPTION", One(e)); }

                for (int i = 0; i < 3; i++) yield return null;
                string afterMove = QuadState();
                Row("RENDER-MOVE", beforeMove != afterMove ? "PASS" : "FAIL",
                    Truncate(beforeMove, 90) + " -> " + Truncate(afterMove, 90));

                // Depth is the native sorting mapping; a video that ignores it draws in front
                // of the track no matter what the author set.
                string beforeDepth = QuadSort();
                try
                {
                    _placed["depth"] = 3;
                    _editor.UpdateDecorationObject(_placed);
                }
                catch (Exception e) { Row("RENDER-DEPTH", "EXCEPTION", One(e)); }
                for (int i = 0; i < 3; i++) yield return null;
                string afterDepth = QuadSort();
                Row("RENDER-DEPTH", beforeDepth != afterDepth ? "PASS" : "FAIL",
                    beforeDepth + " -> " + afterDepth);

                // Opacity and colour are native properties the quad must carry.
                SetOpacity(55f);
                for (int i = 0; i < 2; i++) yield return null;
                string opacity = QuadOpacity();
                Row("RENDER-OPACITY", ReportContains(opacity, "opacity=0.5") ? "PASS" : "FAIL", opacity);

                // The editor preview and playback must apply the same scaling. The package
                // declares the input as `mode` and the host calls it `fit`, so this is exactly
                // where the two can drift apart.
                string fit = QuadFit();
                Row("RENDER-FIT", fit == "Fit" ? "PASS" : "FAIL",
                    "fit=" + fit + " (manifest default is Fit; a stretch here means the preview dropped the parameter)");
            }

            private string QuadFit()
            {
                int at = PlacedSurface().IndexOf("fit=", StringComparison.Ordinal);
                if (at < 0) return "none";
                int start = at + 4;
                int end = PlacedSurface().IndexOf(' ', start);
                if (end < 0) end = PlacedSurface().Length;
                return PlacedSurface().Substring(start, Math.Max(0, end - start));
            }

            private void SetOpacity(float value)
            {
                try
                {
                    _placed["opacity"] = value;
                    _editor.UpdateDecorationObject(_placed);
                }
                catch (Exception e) { Row("RENDER-OPACITY", "EXCEPTION", One(e)); }
            }

            /// <summary>Playback must follow the song clock, including pause and seek.</summary>
            private IEnumerable CheckTimeline()
            {
                if (_placed == null) { Row("TIME", "SKIP", "no placed row"); yield break; }

                // Record the media position the surface holds while the editor is idle.
                string idle = TimePosition();
                Row("TIME-IDLE", idle, "paused surface position");

                // The runtime drives the surface from the song clock during playback. This
                // check runs the arm/teardown path the editor's Play button uses, without
                // starting audio, and confirms the instance is armed at all.
                int armed = 0;
                try
                {
                    Scripting.RuntimeHost.RebuildFromEditor();
                    armed = Scripting.RuntimeHost.Instance != null ? Scripting.RuntimeHost.Instance.ArmedCount : -1;
                }
                catch (Exception e) { Row("TIME-ARM", "EXCEPTION", One(e)); }

                Row("TIME-ARM", armed > 0 ? "PASS" : "FAIL", "armed instances=" + armed);

                // The claim that matters for an editor is that any media position can be shown
                // on demand: dragging the timeline must land the picture where the author put
                // it, not where playback happened to be. This drives the surface directly with
                // an out-of-order sequence of positions and checks each one arrives.
                var seek = CheckSeekAccuracy();
                while (true)
                {
                    bool more;
                    try { more = seek.MoveNext(); }
                    catch (Exception e) { Row("SEEK", "EXCEPTION", One(e)); break; }
                    if (!more) break;
                    yield return seek.Current;
                }
            }

            private IEnumerator CheckSeekAccuracy()
            {
                if (string.IsNullOrEmpty(_mediaRoot)) { Row("SEEK", "SKIP", "no media root"); yield break; }
                string path = Path.Combine(_mediaRoot, "a-h264.mp4");
                if (!File.Exists(path)) { Row("SEEK", "SKIP", "no fixture"); yield break; }

                var args = new Dictionary<string, object>
                {
                    { "__instance", "seek" }, { "key", "video" }, { "file", path },
                    { "time", 0.0 }, { "playing", false }, { "volume", 0.0 }, { "muted", true }
                };

                // A paused editor surface holds a frame; forcing the clock running would make
                // the check measure playback instead of seeking.
                VideoSurface.ClockOverride = -1;
                VideoSurface.Set("video", args, out _);
                try
                {
                    float warm = Time.realtimeSinceStartup + 25f;
                    while (Time.realtimeSinceStartup < warm)
                    {
                        if (EntryReport("seek/video").IndexOf("frame=True", StringComparison.Ordinal) >= 0) break;
                        yield return null;
                    }

                    // Forward, then backwards, then back to the start: exactly the pattern an
                    // author produces by dragging the timeline around.
                    var targets = new[] { 2.0, 0.4, 2.6, 0.05 };
                    var misses = new StringBuilder();
                    int hits = 0;
                    foreach (var target in targets)
                    {
                        args["time"] = target;
                        VideoSurface.Set("video", args, out _);

                        // A seek is asynchronous; sampling immediately would read the previous
                        // frame and call a working seek broken.
                        float settle = Time.realtimeSinceStartup + 8f;
                        double actual = -1;
                        while (Time.realtimeSinceStartup < settle)
                        {
                            yield return null;
                            string report = EntryReport("seek/video");
                            int at = report.IndexOf(" t=", StringComparison.Ordinal);
                            if (at < 0) continue;
                            int end = report.IndexOf(' ', at + 3);
                            double.TryParse(report.Substring(at + 3, Math.Max(0, end - at - 3)), out actual);
                            if (Math.Abs(actual - target) < 0.25) break;
                        }

                        if (Math.Abs(actual - target) < 0.25) hits++;
                        else
                        {
                            misses.Append(target.ToString("F2")).Append("->").Append(actual.ToString("F2")).Append(' ');
                            Row("SEEK-DETAIL", target.ToString("F2"), Truncate(EntryReport("seek/video"), 260));
                        }
                    }

                    Row("SEEK-SCRUB", hits == targets.Length ? "PASS" : "FAIL",
                        hits + "/" + targets.Length + " positions landed" +
                        (misses.Length > 0 ? " misses: " + misses.ToString().Trim() : ""));
                }
                finally
                {
                    VideoSurface.ClockOverride = 0;
                    VideoSurface.Destroy("video", new Dictionary<string, object> { { "__instance", "seek" } }, out _);
                }
            }

            /// <summary>Save and reopen must round-trip the payload and the declared dependency.</summary>
            private IEnumerable CheckSaveReload()
            {
                if (_placed == null) { Row("SAVE", "SKIP", "no placed row"); yield break; }

                string serialized = "";
                try
                {
                    serialized = _editor.levelData.Encode();
                    var doc = JObject.Parse(serialized);
                    var deps = doc[ChartDependencies.Key] as JArray;
                    bool declared = deps != null && deps.OfType<JObject>()
                        .Any(d => d.Value<string>("id") == "pev.video");
                    var rows = doc["decorations"] as JArray;
                    bool payload = rows != null && rows.OfType<JObject>()
                        .Any(r => (r.Value<string>("maskingTarget") ?? "").StartsWith(DecorationCarrier.PayloadPrefix));
                    Row("SAVE-DEPENDENCY", declared ? "PASS" : "FAIL", deps?.ToString(Formatting.None));
                    Row("SAVE-PAYLOAD", payload ? "PASS" : "FAIL", "decoration row carries pev1 payload");
                }
                catch (Exception e)
                {
                    Row("SAVE", "EXCEPTION", One(e));
                    yield break;
                }

                // Re-decode the encoded document: the values must survive the round trip.
                try
                {
                    File.WriteAllText(_chartPath + ".roundtrip", serialized);
                    var reloaded = JObject.Parse(serialized);
                    var rows = reloaded["decorations"] as JArray;
                    DecorationCarrier.TryUnpack(
                        rows.OfType<JObject>().Select(r => r.Value<string>("maskingTarget"))
                            .FirstOrDefault(v => (v ?? "").StartsWith(DecorationCarrier.PayloadPrefix)),
                        out var data);
                    Row("SAVE-ROUNDTRIP", data != null && data.Id == "pev.video" ? "PASS" : "FAIL",
                        data == null ? "payload did not decode" : data.Id + " uid=" + data.Uid);
                    try { File.Delete(_chartPath + ".roundtrip"); } catch { }
                }
                catch (Exception e) { Row("SAVE-ROUNDTRIP", "EXCEPTION", One(e)); }

                yield break;
            }

            /// <summary>Bad input must produce a diagnosis, not an exception or a hang.</summary>
            private IEnumerable CheckDiagnostics()
            {
                var args = new Dictionary<string, object>
                {
                    { "__instance", "test/bad" },
                    { "key", "video" }
                };

                // Missing file.
                args["file"] = "definitely-missing-video.mp4";
                bool okMissing = VideoSurface.Set("video", args, out string errorMissing);
                string report = VideoSurface.Report();
                Row("DIAG-MISSING", okMissing && !string.IsNullOrEmpty(errorMissing) ? "PASS" : "FAIL",
                    errorMissing ?? "no error reported");

                // A file that exists but is not media at all.
                string junk = Path.Combine(Path.GetDirectoryName(_chartPath) ?? ".", "not-a-video.mp4");
                try { File.WriteAllBytes(junk, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }); } catch { }
                args["file"] = junk;
                var args2 = new Dictionary<string, object>(args) { ["__instance"] = "test/junk" };
                bool okJunk = VideoSurface.Set("video", args2, out string errorJunk);
                Row("DIAG-JUNK", okJunk ? "PASS" : "FAIL", errorJunk ?? "accepted (set returns true; prepare will fail)");
                try { File.Delete(junk); } catch { }

                // Diagnostics must name what the backend refused, not just say "failed".
                Row("DIAG-MEDIA", VideoMedia.Report(), "converter availability");
                if (!string.IsNullOrEmpty(_mediaRoot))
                {
                    foreach (var probe in new[] { "i-h264.mkv", "src-flv.flv" })
                    {
                        string path = Path.Combine(_mediaRoot, probe);
                        if (File.Exists(path))
                            Row("DIAG-" + probe, VideoMedia.Diagnose(path), "container diagnosis");
                    }
                }

                VideoSurface.DestroyScope("test");
                yield break;
            }

            // ------------------------------------------------------- format matrix

            /// <summary>Runs each real media file through the surface and reports the outcome.</summary>
            private IEnumerable CheckFormatMatrix()
            {
                if (string.IsNullOrEmpty(_mediaRoot) || !Directory.Exists(_mediaRoot))
                {
                    Row("FORMAT", "SKIP", "no media root");
                    yield break;
                }

                // With the compatibility tier enabled everything in the matrix is expected to
                // play: the files the backend refuses are the ones that get converted. The
                // native pass below is what records which of them need that conversion.
                var expected = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    { "a-h264.mp4", true },
                    { "b-hevc.mp4", true },
                    { "c-h264.mov", true },
                    { "d-h264.m4v", true },
                    { "e-vp8.webm", true },
                    { "g-vp9.webm", true },
                    { "h-av1.mp4", true },
                    { "i-h264.mkv", true },
                    { "j-mpeg4.mp4", true },
                    { "k-mjpeg.avi", true },
                    { "l-wmv2.wmv", true },
                    { "m-theora.ogv", true },
                    { "n-prores.mov", true },
                    { "o-h264.ts", true },
                    { "p-hevc.mkv", true },
                    { "q-h264-audio.mp4", true },
                    { "v-vfr.mp4", true }
                };

                var files = new List<string>();
                foreach (var name in expected.Keys)
                {
                    string path = Path.Combine(_mediaRoot, name);
                    if (File.Exists(path)) files.Add(path);
                }
                files.Sort(StringComparer.OrdinalIgnoreCase);

                var results = new List<string>();
                int agree = 0, tested = 0;
                foreach (var file in files)
                {
                    // Delivered capability, not raw backend capability: the backend draws the
                    // line between what opens directly and what needs the compatibility tier,
                    // and only the package's own surface knows about the tier.
                    var task = ProbeFormat(file, true);
                    while (true)
                    {
                        bool more;
                        try { more = task.MoveNext(); }
                        catch (Exception e) { Row("FORMAT", "EXCEPTION", One(e)); break; }
                        if (!more) break;
                        yield return task.Current;
                    }

                    string name = Path.GetFileName(file);
                    bool ok = _formatPlayed;
                    tested++;
                    if (!expected.TryGetValue(name, out bool want)) continue;
                    if (ok == want) agree++;
                    else results.Add(name + (want ? " expected play, did not" : " expected refuse, played"));
                }

                Row("FORMAT-MATRIX", tested > 0 && agree == tested ? "PASS" : "FAIL",
                    agree + "/" + tested + " as predicted" + (results.Count > 0 ? " — " + string.Join("; ", results.ToArray()) : ""));

                // Native-only pass: the same files with the compatibility tier switched off.
                // This is what makes the format claim honest — "opens directly" and "opens
                // after conversion" are different promises and are reported separately.
                var native = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    { "a-h264.mp4", true }, { "b-hevc.mp4", true }, { "c-h264.mov", true },
                    { "d-h264.m4v", true }, { "e-vp8.webm", true }, { "g-vp9.webm", false },
                    { "h-av1.mp4", true }, { "i-h264.mkv", false }, { "j-mpeg4.mp4", true },
                    { "k-mjpeg.avi", true }, { "l-wmv2.wmv", true }, { "m-theora.ogv", false },
                    { "n-prores.mov", false }, { "o-h264.ts", false }, { "p-hevc.mkv", false },
                    { "q-h264-audio.mp4", true }, { "v-vfr.mp4", true }
                };
                var direct = new List<string>();
                int nativeAgree = 0, nativeTested = 0;
                foreach (var file in files)
                {
                    string name = Path.GetFileName(file);
                    if (!native.ContainsKey(name)) continue;
                    var task = ProbeFormat(file, false);
                    while (true)
                    {
                        bool more;
                        try { more = task.MoveNext(); }
                        catch (Exception e) { Row("NATIVE", "EXCEPTION", One(e)); break; }
                        if (!more) break;
                        yield return task.Current;
                    }
                    nativeTested++;
                    if (_formatPlayed == native[name]) nativeAgree++;
                    else direct.Add(name + (_formatPlayed ? " opens directly (unexpected)" : " refused (unexpected)"));
                }
                Row("FORMAT-NATIVE", nativeTested > 0 && nativeAgree == nativeTested ? "PASS" : "FAIL",
                    nativeAgree + "/" + nativeTested + " direct" +
                    (direct.Count > 0 ? " — " + string.Join("; ", direct.ToArray()) : ""));
            }

            /// <summary>
            /// An unconvertible container must become playable on its own.
            ///
            /// The compatibility claim is not "we list MKV in the docs": it is that a chart
            /// author drops an MKV next to the chart, opens it, and sees video. That requires
            /// the conversion to be started automatically, to be cached, and for the surface
            /// to pick the result up without the author reloading anything.
            /// </summary>
            private IEnumerable CheckConversion()
            {
                if (string.IsNullOrEmpty(_mediaRoot))
                {
                    Row("CONVERT", "SKIP", "no media root");
                    yield break;
                }

                string source = Path.Combine(_mediaRoot, "i-h264.mkv");
                if (!File.Exists(source)) { Row("CONVERT", "SKIP", "no mkv fixture"); yield break; }

                if (string.IsNullOrEmpty(VideoMedia.FfmpegPath()))
                {
                    Row("CONVERT", "SKIP", "no ffmpeg on this machine");
                    yield break;
                }

                var args = new Dictionary<string, object>
                {
                    { "__instance", "convert" },
                    { "key", "video" },
                    { "file", source },
                    { "time", 0.5 },
                    { "playing", true },
                    { "volume", 0.0 },
                    { "muted", true }
                };

                VideoSurface.Set("video", args, out string firstError);
                Row("CONVERT-STARTED", string.IsNullOrEmpty(firstError) || firstError.IndexOf("converting", StringComparison.Ordinal) >= 0
                    ? "PASS" : "FAIL", firstError ?? "opened immediately (cached)");

                // Conversion of a 3-second clip is seconds of work; a longer timeout is what a
                // real chart's first open would need, so the wait here mirrors it.
                float deadline = Time.realtimeSinceStartup + 90f;
                string report = "";
                bool failed = false;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    report = EntryReport("convert/video");
                    if (report.IndexOf("failed=True", StringComparison.Ordinal) >= 0) { failed = true; break; }
                    if (report.IndexOf("frame=True", StringComparison.Ordinal) >= 0) break;
                }

                bool plays = !failed && report.IndexOf("frame=True", StringComparison.Ordinal) >= 0;
                Row("CONVERT-PLAYS", plays ? "PASS" : "FAIL", Truncate(report, 200));

                string cached = VideoMedia.CachedConversion(source);
                Row("CONVERT-CACHE", cached != null ? "PASS" : "FAIL", cached ?? "no cached file");

                // The cache exists so the second open is instant, not so it doubles in size.
                VideoSurface.Destroy("video", new Dictionary<string, object> { { "__instance", "convert" } }, out _);
                var second = new Dictionary<string, object>(args) { ["__instance"] = "convert2" };
                VideoSurface.Set("video", second, out string secondError);
                bool reused = false;
                float quick = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < quick)
                {
                    yield return null;
                    string again = EntryReport("convert2/video");
                    if (again.IndexOf("frame=True", StringComparison.Ordinal) >= 0) { reused = true; break; }
                }
                Row("CONVERT-REUSE", reused ? "PASS" : "FAIL",
                    "second open " + (string.IsNullOrEmpty(secondError) ? "reused cache" : secondError));
                VideoSurface.Destroy("video", new Dictionary<string, object> { { "__instance", "convert2" } }, out _);
            }

            /// <summary>Several videos at once is a stated capability; measure it.</summary>
            private IEnumerable CheckMultiInstance()
            {
                if (string.IsNullOrEmpty(_mediaRoot)) { Row("MULTI", "SKIP", "no media root"); yield break; }

                var files = new[] { "a-h264.mp4", "b-hevc.mp4", "e-vp8.webm", "k-mjpeg.avi" };
                var live = new List<string>();
                foreach (var name in files)
                {
                    string path = Path.Combine(_mediaRoot, name);
                    if (!File.Exists(path)) continue;
                    string scope = "multi" + live.Count;
                    VideoSurface.Set("video", new Dictionary<string, object>
                    {
                        { "__instance", scope }, { "key", "video" }, { "file", path },
                        { "time", 0.5 }, { "playing", true }, { "volume", 0.0 }, { "muted", true }
                    }, out _);
                    live.Add(scope);
                }

                float deadline = Time.realtimeSinceStartup + 25f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    int ready = 0;
                    foreach (var scope in live)
                        if (EntryReport(scope + "/video").IndexOf("frame=True", StringComparison.Ordinal) >= 0) ready++;
                    if (ready == live.Count) break;
                    yield return null;
                }

                int playing = 0;
                var detail = new StringBuilder();
                foreach (var scope in live)
                {
                    string report = EntryReport(scope + "/video");
                    bool ok = report.IndexOf("frame=True", StringComparison.Ordinal) >= 0;
                    if (ok) playing++;
                    detail.Append(scope).Append(ok ? "=ok " : "=fail ");
                }

                Row("MULTI-INSTANCE", playing == live.Count && live.Count >= 3 ? "PASS" : "FAIL",
                    playing + "/" + live.Count + " decoded concurrently; " + detail.ToString().Trim());

                foreach (var scope in live)
                    VideoSurface.Destroy("video", new Dictionary<string, object> { { "__instance", scope } }, out _);
            }

            /// <summary>Frame rate while several clips decode, as a coarse performance floor.</summary>
            private IEnumerable CheckPerformance()
            {
                if (string.IsNullOrEmpty(_mediaRoot)) { Row("PERF", "SKIP", "no media root"); yield break; }

                string path = Path.Combine(_mediaRoot, "x-1080p60.mp4");
                if (!File.Exists(path)) { Row("PERF", "SKIP", "no 1080p fixture"); yield break; }

                var args = new Dictionary<string, object>
                {
                    { "__instance", "perf" }, { "key", "video" }, { "file", path },
                    { "time", 0.0 }, { "playing", true }, { "volume", 0.0 }, { "muted", true }
                };
                VideoSurface.Set("video", args, out _);

                // Wait for it to be live before measuring, so the decode warm-up is excluded.
                float warm = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < warm)
                {
                    if (EntryReport("perf/video").IndexOf("frame=True", StringComparison.Ordinal) >= 0) break;
                    yield return null;
                }

                // Drive the target time like a package would, from a wall clock. Leaving it at
                // zero and then measuring "did the media advance" would test nothing: a surface
                // is supposed to sit still until the package asks it to move. The clock is
                // forced running because the editor is idle and would otherwise pause it.
                VideoSurface.ClockOverride = 1;
                int frames = 0;
                float start = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - start < 2.0f)
                {
                    float elapsedNow = Time.realtimeSinceStartup - start;
                    args["time"] = elapsedNow;
                    VideoSurface.Set("video", args, out _);

                    frames++;
                    yield return null;
                }
                float elapsed = Time.realtimeSinceStartup - start;
                VideoSurface.ClockOverride = 0;
                float fps = elapsed > 0 ? frames / elapsed : 0;

                string report = EntryReport("perf/video");
                Row("PERF-1080P60", fps >= 30f ? "PASS" : "FAIL",
                    fps.ToString("F1") + " fps while decoding 1920x1080@60");

                // The decoded position must have tracked the requested one, not fallen behind.
                double media = 0;
                int at = report.IndexOf("t=", StringComparison.Ordinal);
                if (at >= 0)
                {
                    int end = report.IndexOf(' ', at);
                    double.TryParse(report.Substring(at + 2, Math.Max(0, end - at - 2)), out media);
                }
                bool locked = Math.Abs(media - elapsed) < Math.Max(0.4, elapsed * 0.35);
                Row("PERF-LOCKSTEP", locked ? "PASS" : "FAIL",
                    "media " + media.ToString("F2") + "s vs " + elapsed.ToString("F2") + "s requested");

                VideoSurface.Destroy("video", new Dictionary<string, object> { { "__instance", "perf" } }, out _);
            }

            private bool _formatPlayed;

            private IEnumerator ProbeFormat(string file, bool compat)
            {
                _formatPlayed = false;
                string scope = compat ? "format" : "native";
                var args = new Dictionary<string, object>
                {
                    { "__instance", scope },
                    { "key", "video" },
                    { "file", file },
                    { "time", 0.5 },
                    { "playing", true },
                    { "loop", false },
                    { "trimStart", 0.0 },
                    { "trimEnd", 0.0 },
                    { "speed", 1.0 },
                    { "volume", 0.0 },
                    { "muted", true },
                    { "fit", "Fit" },
                    { "compat", compat }
                };

                VideoSurface.Set("video", args, out _);

                // A file that needs the compatibility tier is converting, not failing, and a
                // first conversion of a real clip takes seconds. Waiting the same span a chart
                // author would is the honest test; a short timeout would report the format as
                // unsupported when it merely was not finished.
                float deadline = Time.realtimeSinceStartup + 90f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    string report = EntryReport(scope + "/video");
                    if (report.IndexOf("converting=True", StringComparison.Ordinal) >= 0) continue;
                    if (report.IndexOf("failed=True", StringComparison.Ordinal) >= 0) break;
                    if (report.IndexOf("prepared=True", StringComparison.Ordinal) >= 0 &&
                        report.IndexOf("frame=True", StringComparison.Ordinal) >= 0) break;
                }

                string final = EntryReport(scope + "/video");
                _formatPlayed = final.IndexOf("prepared=True", StringComparison.Ordinal) >= 0 &&
                                final.IndexOf("frame=True", StringComparison.Ordinal) >= 0;

                string name = Path.GetFileName(file);
                Row((compat ? "FORMAT-" : "NATIVE-") + name, _formatPlayed ? "PLAYS" : "REFUSED",
                    VideoMedia.Diagnose(file) + (final.Length > 0 ? " | " + Truncate(final, 120) : ""));

                VideoSurface.Destroy("video", new Dictionary<string, object> { { "__instance", scope } }, out _);
            }

            /// <summary>One surface's report line, matched by key prefix.</summary>
            private static string EntryReport(string key)
            {
                foreach (var part in VideoSurface.Report().Split(';'))
                    if (part.TrimStart().StartsWith(key, StringComparison.Ordinal)) return part.Trim();
                return "";
            }

            // ------------------------------------------------------------ helpers

            private static bool ReportContains(string report, string text) =>
                !string.IsNullOrEmpty(report) && report.IndexOf(text, StringComparison.Ordinal) >= 0;

            private static string Truncate(string text, int max) =>
                string.IsNullOrEmpty(text) ? "" : (text.Length <= max ? text : text.Substring(0, max) + "…");

            /// <summary>
            /// The report line for the surface that is actually drawing the placed row.
            ///
            /// In the idle editor the preview owns the row and the runtime's described surface
            /// is dormant, so both appear in the report. Reading "the first prepared line"
            /// therefore answered about the wrong one and reported a surface with no geometry.
            /// </summary>
            private string PlacedSurface()
            {
                string previewKey = "editorvideo/" + UidOf(_placed) + "/video";
                string runtimeKey = "pev.video#" + UidOf(_placed) + "/video";

                string runtime = "", preview = "";
                foreach (var part in VideoSurface.Report().Split(';'))
                {
                    var line = part.Trim();
                    if (line.StartsWith(previewKey, StringComparison.Ordinal)) preview = line;
                    else if (line.StartsWith(runtimeKey, StringComparison.Ordinal)) runtime = line;
                }

                // The owner is the one with geometry; fall back to the runtime line so a check
                // still reports something meaningful outside the editor.
                return preview.Length > 0 ? preview : runtime;
            }

            private string QuadState() => PlacedSurface();

            private string QuadSort()
            {
                foreach (var part in PlacedSurface().Split(';'))
                {
                    int at = part.IndexOf("sort=", StringComparison.Ordinal);
                    if (at >= 0) return part.Substring(at).Trim();
                }
                return PlacedSurface();
            }

            private string QuadOpacity()
            {
                foreach (var part in PlacedSurface().Split(';'))
                {
                    int at = part.IndexOf("opacity=", StringComparison.Ordinal);
                    if (at >= 0) return part.Substring(at).Trim();
                }
                return PlacedSurface();
            }

            private string TimePosition()
            {
                foreach (var part in VideoSurface.Report().Split(';'))
                    if (part.Contains("prepared=True"))
                    {
                        int at = part.IndexOf("t=", StringComparison.Ordinal);
                        return at < 0 ? part.Trim() : part.Substring(at).Trim();
                    }
                return "none";
            }
        }
    }
}
