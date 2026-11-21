using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Video;

namespace PEV.Scripting
{
    /// <summary>
    /// A video surface: one decoded clip drawn as a textured quad at a world frame.
    ///
    /// This is a host primitive, not an effect. Decoding, texture ownership, preparation,
    /// seeking and teardown live here; when the clip should be at, how it is transformed and
    /// whether it should be visible are decided by the event script that calls in every
    /// frame with absolute values. That split is what makes pause, rewind and scrubbing
    /// correct without the script tracking a delta time: it states the target time, and the
    /// surface reconciles toward it.
    ///
    /// Measured facts this is built around (ADOFAI r150, Unity 6000.3.25f1, Windows D3D11):
    ///   - Prepare() completes and frames arrive for MP4/MOV/M4V (H.264, HEVC, AV1, MPEG-4),
    ///     AVI (H.264, MJPEG) and WMV. MKV, FLV, TS, OGV and VP9-in-WebM do not open at all.
    ///   - Setting time after playback has started seeks correctly and seekCompleted fires.
    ///   - Several players decode concurrently.
    ///   - A VP8 WebM alpha plane does not survive into the RenderTexture; the picture is
    ///     opaque. Surfaces therefore composite with a flat colour alpha, not media alpha.
    /// </summary>
    public static class VideoSurface
    {
        /// <summary>
        /// Decoration-backed surfaces are sized from the carrier's scale exactly like a field:
        /// 1x1 tiles at 100%. scrVisualDecoration falls back to a unit box because the marker
        /// image does not exist, so this constant is the scale-to-tiles mapping.
        /// </summary>
        public static readonly Vector2 BaseSizeTiles = DecorationBridge.BaseSizeTiles;

        private sealed class Entry
        {
            public string Key;
            public string Scope;

            public GameObject GameObject;
            public MeshRenderer Renderer;
            public Mesh Mesh;
            public Material Material;
            public VideoPlayer Player;
            public RenderTexture Target;
            public AudioSource Audio;

            /// <summary>The file this surface was opened from, after path resolution.</summary>
            public string SourcePath;
            public string RequestedFile;
            public string SourceError;

            /// <summary>
            /// True while the file is being converted for this backend.
            ///
            /// Distinct from a failure on purpose: the panel and the report have to be able to
            /// say "still preparing this format" rather than "this video is broken", and the
            /// retry poll keys off it.
            /// </summary>
            public bool Converting;
            public bool Prepared;
            public bool PrepareFailed;
            public string PlayerError;

            /// <summary>Media time the script asked for this frame, in seconds.</summary>
            public double DesiredTime;
            /// <summary>Playback rate the script asked for.</summary>
            public float DesiredSpeed = 1f;
            public bool DesiredPlaying;
            public bool DesiredLoop;
            public float DesiredVolume = 1f;
            public bool DesiredMuted;

            /// <summary>The file the author named, before any conversion substitution.</summary>
            public string OriginalPath;

            /// <summary>Whether to fall back to a converted copy when the backend refuses a file.</summary>
            public bool AllowConversion = true;

            /// <summary>
            /// Whether the package wants the picture on screen.
            ///
            /// Distinct from the decoration's own visibility: a package that reaches the end of
            /// a clip with "hold last frame" off takes the picture down while the decoration row
            /// stays where the author put it.
            /// </summary>
            public bool DesiredVisible = true;

            /// <summary>Native media duration once known; 0 until prepared.</summary>
            public double Duration;
            public int Width, Height;

            /// <summary>
            /// The author's clip window, in media seconds.
            ///
            /// Kept here rather than in the package because the effective end depends on the
            /// media duration, which only the decoder knows and only after preparing. A
            /// package cannot clamp to an end it cannot see.
            /// </summary>
            public double TrimStart;
            public double TrimEnd;
            public string Fit = "stretch";
            public Vector2 UvScale = Vector2.one;

            /// <summary>
            /// Set while a seek is in flight. Native seeking is asynchronous: assigning time
            /// does not take effect on that frame, so a second assignment before the backend
            /// acknowledges it is what makes the picture stutter and stay behind.
            /// </summary>
            public bool SeekPending;
            public int SeekFrames;
            public int SeekAttempts;
            public double LastObservedTime = -1;
            public int StallFrames;
            public int LastRestartFrame = int.MinValue / 2;

            /// <summary>Geometry and tint, read from the bound decoration every frame.</summary>
            public Placement.Frame World;
            public Color Tint = Color.white;
            public float Opacity = 1f;
            public bool Visible = true;
            public bool Renderable = true;
            public int SortingLayerID;
            public int SortingOrder;
            public int UnityLayer;
            public bool HasFrame;

            public ADOFAI.LevelEvent Bound;

            public int UpdateCalls;
            public double LastErrorLogTime;
        }

        private static readonly Dictionary<string, Entry> Live =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>
        /// Objects whose destroy had to be postponed because their decoder was still opening.
        /// </summary>
        private static readonly List<PendingDestroy> Deferred = new List<PendingDestroy>();

        private struct PendingDestroy
        {
            public GameObject Object;
            public float Since;
        }

        public static int LiveCount => Live.Count;
        public static IEnumerable<string> Keys => Live.Keys;

#if PEV_DIAGNOSTICS
        internal static Renderer FindVisibleRenderer(string scope = null, string key = null)
        {
            if (scope != null)
                return Live.TryGetValue(Scoped(scope, key), out var entry) && entry.Renderer != null &&
                    entry.Renderer.enabled ? entry.Renderer : null;
            foreach (var candidate in Live.Values)
                if (candidate.Bound != null && candidate.Renderer != null && candidate.Renderer.enabled)
                    return candidate.Renderer;
            return null;
        }
#endif

        /// <summary>
        /// Whether the song clock is advancing.
        ///
        /// A script is not called while the song is paused, so its last "playing" request
        /// would leave the decoder running and the picture drifting away from the frame the
        /// author paused on. The runtime flips this from the clock it already measures, and
        /// the surface holds frame instead.
        /// </summary>
        /// <summary>
        /// Whether the runtime owns surfaces right now.
        ///
        /// In the editor, the runtime is idle and its instances have not started, while the
        /// idle preview draws the same rows. Refreshing both would put two quads on one
        /// decoration at two different media positions. The runtime therefore claims
        /// ownership only while the level is actually playing; every other time the editor
        /// preview is the only owner.
        /// </summary>
        public static bool RuntimeActive = true;

        /// <summary>
        /// Whether the song clock is advancing.
        ///
        /// A script is not called while the song is paused, so its last "playing" request
        /// would leave the decoder running and the picture drifting away from the frame the
        /// author paused on. The runtime flips this from the clock it already measures, and
        /// the surface holds frame instead.
        /// </summary>
        public static bool ClockRunning = true;

        /// <summary>
        /// Forces <see cref="ClockRunning"/> for tests; 0 leaves the real clock in charge.
        ///
        /// An acceptance harness inspects decoding and seeking while the editor is idle, and
        /// in that state the song clock is legitimately stopped — so the surface correctly
        /// pauses and the measurement would only ever report "nothing happened".
        /// </summary>
        public static int ClockOverride;

        internal static string Scoped(string scope, string key) =>
            GameBridge.ScopedKey(scope, key);

        /// <summary>
        /// Test hook: record what packages ask for instead of opening media.
        ///
        /// The timing arithmetic lives in the package's JavaScript, and the only way to test
        /// the arithmetic that actually ships is to run that script and look at the values it
        /// sends. With this active the surface records each call and opens nothing, so a
        /// harness can drive the real main.js with a synthetic clock and assert the media
        /// positions it requests.
        /// </summary>
        public static readonly List<Dictionary<string, object>> Capture =
            new List<Dictionary<string, object>>();

        public static bool CaptureActive;

        /// <summary>
        /// The duration reported to a package while <see cref="CaptureActive"/> is set.
        ///
        /// The real duration comes from the decoder, which is exactly what capture mode skips.
        /// A package that clamps to the media length cannot be tested without one, so the
        /// harness supplies it here rather than reaching into the script's closure.
        /// </summary>
        public static double CaptureDuration;

        /// <summary>Creates or updates the surface for <paramref name="key"/>.
        ///
        /// Arguments are absolute, never deltas: time is the media position that should be
        /// on screen now. A surface reconciles toward it, seeking when it is far enough away
        /// to be visible and otherwise letting the decoder run.
        /// </summary>
        public static bool Set(string key, Dictionary<string, object> args, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(key)) { error = "fx.video needs a key"; return false; }

            if (CaptureActive && args != null)
            {
                Capture.Add(new Dictionary<string, object>(args, StringComparer.Ordinal));
                return true;
            }

            try
            {
                string scope = ScopeOf(args);
                string id = Scoped(scope, key);
                if (!Live.TryGetValue(id, out var entry))
                {
                    entry = new Entry { Key = string.IsNullOrEmpty(scope) ? key : scope + "/" + key, Scope = scope };
                    Live[id] = entry;
                }

                entry.UpdateCalls++;
                // Before Open: whether a refused container may be substituted decides what
                // Open hands to the decoder, so it has to be known when Open runs.
                bool compat = Bool(args, "compat", true);
                bool hasFile = args != null && args.ContainsKey("file");
                string file = hasFile ? Str(args, "file", "").Trim() : entry.RequestedFile;
                if (!string.Equals(file, entry.RequestedFile, StringComparison.Ordinal) ||
                    entry.AllowConversion != compat)
                {
                    Release(entry);
                    entry.RequestedFile = file;
                    entry.AllowConversion = compat;
                    if (!string.IsNullOrEmpty(file)) Open(entry, file, args);
                }

                entry.DesiredTime = Num(args, "time", entry.DesiredTime);
                entry.DesiredSpeed = (float)Num(args, "speed", 1.0);
                entry.DesiredPlaying = Bool(args, "playing", true);
                entry.DesiredLoop = Bool(args, "loop", false);
                entry.DesiredVolume = (float)Num(args, "volume", 1.0);
                entry.DesiredMuted = Bool(args, "muted", false);
                entry.DesiredVisible = Bool(args, "visible", true);
                entry.TrimStart = Math.Max(0, Num(args, "trimStart", 0));
                entry.TrimEnd = Math.Max(0, Num(args, "trimEnd", 0));
                string fit = Str(args, "fit", null);
                if (!string.IsNullOrEmpty(fit)) entry.Fit = fit;

                ReadBinding(entry, args);
                if (entry.Bound != null) ReadDecoration(entry);
                else
                {
                    entry.World.Center = new Vector3((float)Num(args, "x", 0), (float)Num(args, "y", 0));
                    entry.World.Size = new Vector2((float)Num(args, "width", 0), (float)Num(args, "height", 0));
                    entry.World.Rotation = (float)Num(args, "rotation", 0);
                    entry.SortingOrder = (int)Num(args, "sortingOrder", 0);
                    entry.Opacity = Mathf.Clamp01((float)Num(args, "alpha", 1));
                    if (!ColorUtility.TryParseHtmlString("#" + Str(args, "color", "ffffff"), out entry.Tint))
                        entry.Tint = Color.white;
                }
                ApplyAudio(entry);

                if (!entry.Prepared) error = entry.SourceError ?? entry.PlayerError;
                return error == null || entry.Converting;
            }
            catch (Exception e)
            {
                error = "fx.video failed: " + e.Message;
                return false;
            }
        }

        public static bool Destroy(string key, Dictionary<string, object> args, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(key)) return true;
            string id = Scoped(ScopeOf(args), key);
            if (Live.TryGetValue(id, out var entry))
            {
                Release(entry);
                Live.Remove(id);
            }
            return true;
        }

        public static void DestroyScope(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return;
            var doomed = new List<string>();
            foreach (var kv in Live)
                if (string.Equals(kv.Value.Scope, scope, StringComparison.Ordinal)) doomed.Add(kv.Key);
            foreach (var key in doomed)
            {
                Release(Live[key]);
                Live.Remove(key);
            }
        }

        public static void DestroyAll()
        {
            foreach (var entry in Live.Values) Release(entry);
            Live.Clear();
        }

        /// <summary>
        /// Drives every surface: geometry from its decoration, playback toward its target.
        ///
        /// Called once per frame by the runtime, outside the instance loop, so a described
        /// surface whose script has not started yet still follows its decoration and a paused
        /// surface still holds its frame.
        /// </summary>
        public static void Refresh()
        {
            FlushDeferred();
            if (Live.Count == 0) return;
            UpdateClock();
            foreach (var entry in Live.Values)
            {
                try
                {
                    if (entry.Bound != null) ReadDecoration(entry);

                    // Exactly one owner draws a given row at a time. During playback the
                    // runtime drives the real instances; in the idle editor the preview does.
                    // The conflict only exists for a surface bound to a decoration row, since
                    // that row is what both of them draw: a surface with no row (a test or a
                    // one-off overlay) has no counterpart to defer to.
                    bool owned = entry.Bound == null || IsPreviewScope(entry.Scope) != RuntimeActive;
                    if (!owned)
                    {
                        // Hiding alone is not enough: the losing surface would keep decoding in
                        // the background and compete for the media pipeline. Pausing stops that.
                        if (entry.Renderer != null) entry.Renderer.enabled = false;
                        try { if (entry.Player != null && SafeBool(() => entry.Player.isPlaying)) entry.Player.Pause(); }
                        catch { }
                        ApplyAudio(entry);
                        continue;
                    }
                    RetryConverted(entry);
                    // Audio is applied before the playback sequencer, not after it: that
                    // sequencer returns early while it waits for a frame or a seek, and an
                    // author who sets a volume expects it to take effect regardless of where
                    // the decoder happens to be.
                    ApplyAudio(entry);
                    Reconcile(entry);
                    ApplyGeometry(entry);
                }
                catch (Exception e) { LogOnce(entry, "refresh: " + e.Message); }
            }
        }

        /// <summary>
        /// Destroys objects whose decoder finished opening after they were dropped.
        ///
        /// Destroying a GameObject that still owns a preparing VideoPlayer tears the native
        /// decoder out from under itself and kills the process. Deferring by one frame is not
        /// enough —a prepare can take much longer than that —so the object is polled until
        /// its player is no longer preparing, then destroyed.
        /// </summary>
        private static void FlushDeferred()
        {
            if (Deferred.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = Deferred.Count - 1; i >= 0; i--)
            {
                var item = Deferred[i];
                if (item.Object == null) { Deferred.RemoveAt(i); continue; }

                var player = item.Object.GetComponent<VideoPlayer>();
                if (player != null)
                {
                    bool busy;
                    try { busy = !player.isPrepared && player.enabled; }
                    catch { busy = false; }

                    // The wait is bounded: a file that never prepares would otherwise keep its
                    // object alive for the rest of the session.
                    if (busy && now - item.Since < 5f) continue;
                }

                try { UnityEngine.Object.Destroy(item.Object); } catch { }
                Deferred.RemoveAt(i);
            }
        }

        /// <summary>
        /// True for surfaces the editor preview owns (scope prefix <c>editorvideo/</c>).
        ///
        /// The prefix is the whole ownership protocol: the runtime's instances are scoped by
        /// their own key, so a preview surface can never be mistaken for one of them.
        /// </summary>
        private static bool IsPreviewScope(string scope) =>
            scope != null && scope.StartsWith("editorvideo/", StringComparison.Ordinal);

        /// <summary>
        /// Decides whether the song clock is moving, from the clock itself.
        ///
        /// The runtime's own advance detection cannot be reused: it stops ticking once no
        /// instance is armed, and a surface must hold or follow regardless. The song position
        /// freezes when the game pauses, so a time delta is still the honest signal.
        /// </summary>
        private static double _lastClock = double.NegativeInfinity;

        private static void UpdateClock()
        {
#if PEV_DIAGNOSTICS
            if (ClockOverride != 0) { ClockRunning = ClockOverride > 0; return; }
#endif
            double now;
            try
            {
                var conductor = scrConductor.instance;
                if (conductor == null) { ClockRunning = false; return; }
                now = conductor.songposition_minusi;
            }
            catch { ClockRunning = false; return; }

            // Jitter in the audio-derived clock is a few milliseconds; anything smaller than
            // this is not motion and must not count as advancing.
            const double Epsilon = 1e-4;
            bool rewound = now < _lastClock - 0.05;
            ClockRunning = rewound || now > _lastClock + Epsilon;
            _lastClock = now;

        }

        /// <summary>
        /// Adds the media facts only the surface knows to a script call's reply.
        ///
        /// The effective end of a clip window is the author's trim-out or the media duration,
        /// whichever is shorter, and the duration exists only after the decoder prepared the
        /// file. A package cannot compute that itself, so the answer travels back on the call
        /// it already made.
        /// </summary>
        public static void DescribeForScript(string key, string scope, Dictionary<string, object> reply)
        {
            if (reply == null) return;

            // In capture mode no media is opened, so the duration a package would learn from
            // the decoder has to be supplied by the harness or a length-clamping test cannot
            // run at all.
            if (CaptureActive)
            {
                reply["ready"] = true;
                reply["duration"] = CaptureDuration;
                reply["width"] = 0;
                reply["height"] = 0;
                return;
            }

            string id = Scoped(scope, key);
            if (!Live.TryGetValue(id, out var entry))
            {
                reply["ready"] = false;
                return;
            }

            reply["ready"] = entry.Prepared;
            reply["duration"] = entry.Duration;
            reply["width"] = entry.Width;
            reply["height"] = entry.Height;
            reply["converting"] = entry.Converting;
        }

        /// <summary>Diagnostics: the audio wiring of one surface.</summary>
        public static string AudioReport(string scope, string key)
        {
            string id = Scoped(scope, key);
            if (!Live.TryGetValue(id, out var entry))
                return "no surface";
            if (entry.Player == null) return "no player";

            int tracks = SafeInt(() => entry.Player.audioTrackCount);
            var audio = entry.Audio;
            return "tracks=" + tracks +
                   " target=" + (audio == null ? "none" : "AudioSource") +
                   " volume=" + (audio == null ? "-" : audio.volume.ToString("F3")) +
                   " mute=" + (audio == null ? "-" : audio.mute.ToString()) +
                   " requestedVolume=" + entry.DesiredVolume.ToString("F3") +
                   " requestedMuted=" + entry.DesiredMuted;
        }

        private static int SafeInt(Func<ushort> read)
        {
            try { return read(); } catch { return -1; }
        }

        /// <summary>Diagnostics: what each surface is doing, for the in-game harness.</summary>
        public static string Report()
        {
            var sb = new StringBuilder();
            foreach (var kv in Live)
            {
                var e = kv.Value;
                sb.Append(e.Key)
                    .Append(" file=").Append(e.SourcePath == null ? "-" : Path.GetFileName(e.SourcePath))
                    .Append(" prepared=").Append(e.Prepared)
                    .Append(" failed=").Append(e.PrepareFailed)
                    .Append(" converting=").Append(e.Converting)
                    .Append(" t=").Append(e.Player != null && e.Prepared ? SafeDouble(() => e.Player.time).ToString("F3") : "?")
                    .Append(" want=").Append(e.DesiredTime.ToString("F3"))
                    .Append(" playing=").Append(e.DesiredPlaying)
                    .Append(" frame=").Append(e.HasFrame)
                    .Append(" size=").Append(e.Width).Append('x').Append(e.Height)
                    .Append(" sort=").Append(e.SortingLayerID).Append('/').Append(e.SortingOrder)
                    .Append(" layer=").Append(e.UnityLayer)
                    .Append(" opacity=").Append(e.Opacity.ToString("F3"))
                    .Append(" shown=").Append(e.DesiredVisible)
                    .Append(" playingNow=").Append(e.Player != null ? SafeBool(() => e.Player.isPlaying).ToString() : "?")
                    .Append(" seekPending=").Append(e.SeekPending)
                    .Append(" seeks=").Append(e.SeekAttempts)
                    .Append(" pos=").Append(e.World.Center.x.ToString("F2")).Append(',')
                    .Append(e.World.Center.y.ToString("F2"))
                    .Append(" size=").Append(e.World.Size.x.ToString("F2")).Append('x')
                    .Append(e.World.Size.y.ToString("F2"))
                    .Append(" fit=").Append(e.Fit)
                    .Append(" calls=").Append(e.UpdateCalls)
                    .Append(';');
            }
            return sb.ToString();
        }

        private static double SafeDouble(Func<double> read)
        {
            try { return read(); } catch { return -1; }
        }

        private static void LogOnce(Entry entry, string message)
        {
            double now = Time.realtimeSinceStartup;
            if (now - entry.LastErrorLogTime < 2.0) return;
            entry.LastErrorLogTime = now;
            PEV.Core.Plugin.Log("video " + entry.Key + ": " + message);
        }

        // ---------------------------------------------------------------- media

        /// <summary>
        /// Resolves the media path and starts it preparing.
        ///
        /// Chart-relative paths are the portable form: a package ships next to the .adofai and
        /// the video ships with it, so an absolute path from the author's machine would break
        /// the moment the level is shared. Absolute paths are still accepted so an author can
        /// preview something quickly, but they are not portable and the report says so.
        /// </summary>
        private static void Open(Entry entry, string file, Dictionary<string, object> args)
        {
            string resolved, problem;
            bool chartOnly = args != null && args.TryGetValue("__manifest", out var owner) && owner is PEV.Model.EventManifest;
            if (!VideoMedia.TryResolve(file, out resolved, out problem, chartOnly))
            {
                entry.SourceError = problem;
                entry.PrepareFailed = true;
                PEV.Core.Plugin.Log("video " + entry.Key + ": " + problem);
                return;
            }

            // A container this backend refuses is converted once into a cached MP4 (or a WebM
            // when the source carries alpha) and played from there. The file the author picked
            // never changes; only what gets handed to the decoder.
            entry.OriginalPath = resolved;
            string converted = entry.AllowConversion ? VideoMedia.CachedConversion(resolved) : null;
            if (converted != null) resolved = converted;
            else if (entry.AllowConversion && !VideoMedia.LooksNative(resolved))
            {
                if (VideoMedia.StartConversion(resolved, false, out string note) && note == "converting")
                {
                    PEV.Core.Plugin.Log("video " + entry.Key + ": '" + Path.GetFileName(resolved) +
                                        "' is in a container this backend refuses; " + note);
                    entry.SourcePath = resolved;
                    entry.SourceError = "converting " + Path.GetFileName(resolved); entry.Converting = true;
                    entry.PrepareFailed = true;
                    return;
                }
                PEV.Core.Plugin.Log("video " + entry.Key + ": " + VideoMedia.Diagnose(resolved) + " —" + note);
            }

            entry.SourcePath = resolved;
            OpenPrepared(entry);
        }

        /// <summary>Builds the player and starts preparing the file in <see cref="Entry.SourcePath"/>.</summary>
        private static void OpenPrepared(Entry entry)
        {
            try
            {
                EnsureObject(entry);
                var vp = entry.Player = entry.GameObject.AddComponent<VideoPlayer>();
                vp.playOnAwake = false;
                vp.waitForFirstFrame = false;
                vp.skipOnDrop = false;
                vp.isLooping = false;
                vp.source = VideoSource.Url;
                // A plain filesystem path is accepted by this backend; prefixing file://
                // makes Windows parse it as a URL and fail on the drive-colon.
                vp.url = entry.SourcePath;
                vp.renderMode = VideoRenderMode.APIOnly;

                entry.Audio = entry.GameObject.AddComponent<AudioSource>();
                entry.Audio.playOnAwake = false;
                entry.Audio.spatialBlend = 0f;
                vp.audioOutputMode = VideoAudioOutputMode.AudioSource;
                vp.controlledAudioTrackCount = 1;
                vp.SetTargetAudioSource(0, entry.Audio);

                // Which containers this build can decode is not knowable from the extension:
                // an .mp4 holding HEVC opens, an .mkv holding the same H.264 does not. Only
                // the backend can answer, so the answer is recorded and surfaced.
                vp.prepareCompleted += player => { if (entry.Player == player) OnPrepared(entry); };
                vp.errorReceived += (player, msg) => { if (entry.Player == player) OnError(entry, msg); };
                vp.seekCompleted += player => { if (entry.Player == player) entry.SeekPending = false; };
                vp.Prepare();
            }
            catch (Exception e)
            {
                entry.PrepareFailed = true;
                entry.PlayerError = e.Message;
                PEV.Core.Plugin.Log("video " + entry.Key + " open failed: " + e);
            }
        }

        private static void OnPrepared(Entry entry)
        {
            try
            {
                var vp = entry.Player;
                entry.Duration = vp.length;
                entry.Width = (int)Math.Max(1, vp.width);
                entry.Height = (int)Math.Max(1, vp.height);

                // The target texture is created only once the decoded size is known, so the
                // picture is never resampled through a guessed resolution.
                entry.Target = new RenderTexture(entry.Width, entry.Height, 0, RenderTextureFormat.ARGB32);
                if (!entry.Target.Create()) throw new InvalidOperationException("could not create video render target");
                vp.renderMode = VideoRenderMode.RenderTexture;
                vp.targetTexture = entry.Target;

                if (entry.Material != null) entry.Material.mainTexture = entry.Target;
                entry.Prepared = true;
                entry.PrepareFailed = false;
            }
            catch (Exception e)
            {
                entry.PlayerError = e.Message;
                entry.PrepareFailed = true;
                LogOnce(entry, "prepare handling failed: " + e.Message);
            }
        }

        private static void OnError(Entry entry, string message)
        {
            entry.PrepareFailed = true;
            entry.Prepared = false;
            entry.PlayerError = message;
            // The backend's message names the codec it could not find ("Unsupported video
            // codec 'VP9'"), which is the only diagnosis available for a file that will not
            // open; handing it to VideoMedia lets the report name the conversion it needs.
            PEV.Core.Plugin.Log("video " + entry.Key + " cannot play '" + entry.SourcePath + "': " + message
                                + " [" + VideoMedia.Diagnose(entry.SourcePath) + "]");

            // The extension whitelist can only predict; a .mov holding ProRes is listed as a
            // container this build opens and still fails. So a decode error is the second
            // trigger for the compatibility tier, and it is the one that actually decides.
            if (!entry.AllowConversion || entry.OriginalPath == null) return;
            if (entry.SourcePath != null && !string.Equals(entry.SourcePath, entry.OriginalPath, StringComparison.Ordinal))
                return; // already playing a converted copy that failed; nothing left to try

            if (VideoMedia.StartConversion(entry.OriginalPath, false, out string note))
            {
                entry.SourceError = "converting " + Path.GetFileName(entry.OriginalPath);
                entry.Converting = true;
            }
            else
            {
                entry.PlayerError = message + " (" + note + ")";
                PEV.Core.Plugin.Log("video " + entry.Key + ": cannot convert —" + note);
            }
        }

        /// <summary>
        /// Moves the player toward the requested state.
        ///
        /// The two rules that matter: never assign time every frame (the backend seeks
        /// asynchronously, so a stream of assignments leaves the picture permanently late),
        /// and only correct a drift big enough to be seen. A playing surface whose error is
        /// under the tolerance is left alone so the decoder keeps its own cadence.
        /// </summary>
        private static void Reconcile(Entry entry)
        {
            if (entry.Player == null || !entry.Prepared || entry.Target == null) return;

            var vp = entry.Player;
            double now = SafeDouble(() => vp.time);
            double target = entry.DesiredTime;

            // The clip window is resolved here, not in the package, because its end depends on
            // the media duration and that only exists after preparing. A package that guessed
            // the duration would clamp to the wrong point on every file it did not make.
            double lastTime = Math.Max(0, entry.Duration - 0.001);
            double windowStart = Math.Min(entry.TrimStart, lastTime);
            double windowEnd = entry.Duration > 0
                ? (entry.TrimEnd > entry.TrimStart ? Math.Min(entry.TrimEnd, entry.Duration) : entry.Duration)
                : 0;

            if (windowEnd > windowStart)
            {
                double span = windowEnd - windowStart;
                if (entry.DesiredLoop)
                {
                    target = windowStart + Mod(target - windowStart, span);
                }
                else
                {
                    target = Math.Max(windowStart, Math.Min(target, windowEnd - 0.001));
                }
            }
            else if (entry.Duration > 0)
            {
                target = Math.Max(0, Math.Min(target, Math.Max(0, entry.Duration - 0.001)));
            }

            float speed = entry.DesiredSpeed;
            if (speed <= 0.0001f) speed = 1f;
            try { if (vp.canSetPlaybackSpeed) vp.playbackSpeed = speed; } catch { }

            // A player that has never started holds an empty texture, and seeking it does not
            // fill one: this backend only produces frames once playback has begun. So the very
            // first frame is always obtained by playing, and any pause or seek waits for it.
            // Without this the sequencer below would park on a seek that can never complete and
            // the picture would stay black forever.
            if (!entry.HasFrame)
            {
                if (!SafeBool(() => vp.isPlaying))
                {
                    try { vp.Play(); } catch (Exception e) { LogOnce(entry, "prime failed: " + e.Message); }
                }
                entry.HasFrame = SafeLong(() => vp.frame) >= 0;
                return;
            }

            double drift = Math.Abs(now - target);
            double tolerance = 0.12;

            // A decoder that reports "playing" while its clock does not move is stalled, and
            // an assignment issued into that state never lands. Tracking the last position is
            // what distinguishes a slow seek from a wedged one.
            //
            // Only meaningful while motion is actually wanted: a surface that is paused on
            // purpose — the editor is idle, the song is stopped — holds its position, and
            // counting that as a stall would restart playback in a loop.
            bool wantMotion = entry.DesiredPlaying && ClockRunning;
            bool advancing = now > entry.LastObservedTime + 1e-3;
            entry.LastObservedTime = now;
            if (wantMotion && !advancing && SafeBool(() => vp.isPlaying)) entry.StallFrames++;
            else entry.StallFrames = 0;

            if (entry.StallFrames > 30)
            {
                entry.StallFrames = 0;
                entry.SeekPending = false;
                // One restart per surface per few seconds. A decoder that cannot advance at all
                // would otherwise be stopped and restarted on every stall window forever.
                if (Time.frameCount - entry.LastRestartFrame > 180)
                {
                    entry.LastRestartFrame = Time.frameCount;
                    PEV.Core.Plugin.Log("video " + entry.Key + ": decoder stalled; restarting playback");
                    try
                    {
                        vp.Stop();
                        vp.Play();
                    }
                    catch (Exception e) { LogOnce(entry, "restart failed: " + e.Message); }
                }
                return;
            }

            if (entry.SeekPending)
            {
                // A seek that never comes back (an unsupported file, a decoder stall) must not
                // pin the picture forever; give up and retry on a later frame.
                if (++entry.SeekFrames > 90)
                {
                    entry.SeekPending = false;
                    entry.SeekAttempts++;
                }
                else return;
            }

            if (drift > tolerance)
            {
                // A paused player ignores a time assignment, and a player that was just told to
                // play may not have started yet; assigning in the same frame is silently lost.
                // So the seek is issued only once playback is actually running, which costs one
                // frame and is the difference between a seek that works and one that does not.
                if (!SafeBool(() => vp.isPlaying))
                {
                    try { vp.Play(); } catch (Exception e) { LogOnce(entry, "play before seek failed: " + e.Message); }
                    return;
                }

                try
                {
                    vp.time = target;
                    entry.SeekPending = true;
                    entry.SeekFrames = 0;

                    // A paused surface asks the decoder to materialise the frame it just
                    // seeked to. Without this the assignment can sit unapplied until some
                    // later playback, which is what makes a scrubbed still image show the
                    // previous frame.
                    if (!entry.DesiredPlaying && SafeBool(() => vp.canStep))
                        try { vp.StepForward(); } catch { }
                }
                catch (Exception e) { LogOnce(entry, "seek failed: " + e.Message); }
                return;
            }

            if (entry.DesiredPlaying && ClockRunning)
            {
                if (!SafeBool(() => vp.isPlaying))
                {
                    try { vp.Play(); } catch (Exception e) { LogOnce(entry, "play failed: " + e.Message); }
                }
            }
            else if (SafeBool(() => vp.isPlaying))
            {
                try { vp.Pause(); } catch (Exception e) { LogOnce(entry, "pause failed: " + e.Message); }
            }

            if (entry.Audio != null)
            {
                ApplyAudio(entry);
            }
        }

        /// <summary>
        /// Copies the authored volume and mute onto the audio source.
        ///
        /// Kept separate from the playback sequencing because it must apply even when the
        /// surface is not the one drawing: an author who mutes a video expects it muted the
        /// moment they press play, not one frame after the runtime takes ownership.
        /// </summary>
        private static void ApplyAudio(Entry entry)
        {
            if (entry.Audio == null) return;
            entry.Audio.volume = entry.DesiredMuted ? 0f : Mathf.Clamp01(entry.DesiredVolume);
            entry.Audio.mute = entry.DesiredMuted;
            entry.Audio.loop = entry.DesiredLoop;
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); } catch { return false; }
        }

        private static long SafeLong(Func<long> read)
        {
            try { return read(); } catch { return -1; }
        }

        /// <summary>
        /// Picks up a conversion that finished after the surface gave up on the source.
        ///
        /// The first open of an unconvertible file starts the conversion and returns; the
        /// surface then has no player and would stay blank forever without this poll. It runs
        /// only for entries parked in that state, so a played surface pays nothing.
        /// </summary>
        private static void RetryConverted(Entry entry)
        {
            if (entry.SourcePath == null) return;
            if (!entry.Converting) return;
            if ((Time.frameCount & 15) != 0) return;

            string state = VideoMedia.ConversionState(entry.OriginalPath ?? entry.SourcePath, out bool failed);
            if (state == null) return;
            if (failed)
            {
                entry.Converting = false;
                entry.SourceError = "conversion failed: " + state;
                PEV.Core.Plugin.Log("video " + entry.Key + ": conversion failed —" + state);
                return;
            }

            string converted = VideoMedia.CachedConversion(entry.OriginalPath ?? entry.SourcePath);
            if (converted == null) return;

            Release(entry);
            entry.SourcePath = converted;
            entry.OriginalPath = null;
            OpenPrepared(entry);
        }

        /// <summary>Positive remainder, so a negative media position wraps like a positive one.</summary>
        private static double Mod(double value, double span)
        {
            if (span <= 0) return 0;
            double r = value % span;
            return r < 0 ? r + span : r;
        }

        // ------------------------------------------------------------ rendering

        private static void EnsureObject(Entry entry)
        {
            if (entry.GameObject != null) return;
            var go = EffectCanvas.CreateObject("PEV.Video." + entry.Key);
            entry.GameObject = go;

            // SpriteRenderer supplies its sprite texture as a per-renderer _MainTex.
            // A mesh lets the material sample the decoder's RenderTexture directly.
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) throw new InvalidOperationException("Sprites/Default shader unavailable");
            entry.Mesh = new Mesh
            {
                name = "PEV.VideoQuad",
                vertices = new[] { new Vector3(-.5f, -.5f), new Vector3(-.5f, .5f),
                                   new Vector3(.5f, .5f), new Vector3(.5f, -.5f) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
                colors = new[] { Color.white, Color.white, Color.white, Color.white }
            };
            SetUvs(entry, Vector2.one);
            go.AddComponent<MeshFilter>().sharedMesh = entry.Mesh;
            entry.Material = new Material(shader) { name = "PEV.VideoMat." + entry.Key };
            entry.Renderer = go.AddComponent<MeshRenderer>();
            entry.Renderer.sharedMaterial = entry.Material;
            entry.Renderer.enabled = false;
        }

        private static void SetUvs(Entry entry, Vector2 scale)
        {
            entry.UvScale = scale;
            Vector2 min = (Vector2.one - scale) * .5f;
            Vector2 max = min + scale;
            entry.Mesh.uv = new[] { min, new Vector2(min.x, max.y), max, new Vector2(max.x, min.y) };
        }

        private static void ReadBinding(Entry entry, Dictionary<string, object> args)
        {
            object ev;
            if (args != null && args.TryGetValue("__event", out ev) && ev is ADOFAI.LevelEvent levelEvent)
                entry.Bound = levelEvent;
        }

        /// <summary>
        /// Reads geometry, tint and native sorting off the bound decoration.
        ///
        /// This is what makes a video look like a decoration rather than an overlay: an
        /// editor drag, a MoveDecorations tween and a depth edit all reach this surface with
        /// no code of ours in the path, because they are read from the live component.
        /// </summary>
        private static void ReadDecoration(Entry entry)
        {
            if (!DecorationBridge.TryRead(entry.Bound, out var live))
            {
                if (!DecorationBridge.TryReadEvent(entry.Bound, out live)) return;
            }

            entry.World = live.Frame;
            entry.Tint = live.Color;
            entry.Opacity = live.Opacity;
            entry.Visible = live.Visible;
            entry.Renderable = live.Renderable;
            entry.SortingLayerID = live.SortingLayerID;
            entry.SortingOrder = live.SortingOrder;
            entry.UnityLayer = live.UnityLayer;
        }

        /// <summary>
        /// Places the quad, applies tint and copies the carrier's sorting.
        ///
        /// The decoration's box is the layout, and the clip's aspect decides how the picture
        /// occupies it. Native decorations stretch their image to the box, which for video
        /// means a 16:9 clip in a 4:3 box is visibly distorted, so the default 'Fit' keeps the
        /// media aspect and letterboxes inside the box; 'Fill' crops instead, and 'Stretch'
        /// reproduces the native behaviour for authors who want it.
        /// </summary>
        private static void ApplyGeometry(Entry entry)
        {
            if (entry.GameObject == null) return;

            Vector2 size = entry.World.Size;
            bool show = entry.HasFrame && entry.Visible && entry.Renderable && entry.DesiredVisible &&
                size.x > 0f && size.y > 0f;
            if (entry.Renderer != null) entry.Renderer.enabled = show;
            if (!show) return;

            var t = entry.GameObject.transform;
            t.position = entry.World.Center;
            t.rotation = Quaternion.Euler(0f, 0f, entry.World.Rotation);

            Vector2 uvScale = Vector2.one;
            if (entry.Width > 0 && entry.Height > 0)
            {
                float mediaAspect = (float)entry.Width / entry.Height;
                float boxAspect = size.x / size.y;

                if (string.Equals(entry.Fit, "fill", StringComparison.OrdinalIgnoreCase))
                {
                    uvScale = new Vector2(Mathf.Min(1f, boxAspect / mediaAspect),
                        Mathf.Min(1f, mediaAspect / boxAspect));
                }
                else if (string.Equals(entry.Fit, "fit", StringComparison.OrdinalIgnoreCase))
                {
                    size.x *= Mathf.Min(1f, mediaAspect / boxAspect);
                    size.y *= Mathf.Min(1f, boxAspect / mediaAspect);
                }
            }
            t.localScale = new Vector3(size.x, size.y, 1f);
            if (entry.UvScale != uvScale) SetUvs(entry, uvScale);

            var color = entry.Tint;
            color.a *= entry.Opacity;
            if (entry.Material != null) entry.Material.color = color;

            if (entry.Renderer != null)
            {
                entry.Renderer.sortingLayerID = entry.SortingLayerID;
                entry.Renderer.sortingOrder = entry.SortingOrder;
            }
            entry.GameObject.layer = entry.UnityLayer;
        }

        /// <summary>
        /// Tears a surface down without leaving the decoder writing into freed memory.
        ///
        /// The order matters and is not cosmetic: the player must let go of the render
        /// texture before the texture is released, and a player that is still preparing must
        /// not be destroyed underneath the native decoder. Destroying those in the obvious
        /// order crashes the process rather than throwing, which a chart author would see as
        /// "the game closed when I deleted the video".
        /// </summary>
        private static void Release(Entry entry)
        {
            var player = entry.Player;
            bool preparing = player != null && !entry.Prepared && !entry.PrepareFailed;
            entry.Player = null;
            if (player != null)
            {
                try { player.targetTexture = null; } catch { }
                try { player.renderMode = VideoRenderMode.APIOnly; } catch { }
                try { if (SafeBool(() => player.isPrepared)) player.Stop(); } catch { }
            }
            if (entry.Renderer != null) entry.Renderer.enabled = false;
            if (entry.Audio != null) entry.Audio.mute = true;

            try
            {
                if (entry.Target != null)
                {
                    // Releasing a texture that is still the active render target crashes the
                    // player ("Releasing render texture that is set to be RenderTexture.active"),
                    // and a harness or a frame capture can leave exactly that behind.
                    if (ReferenceEquals(RenderTexture.active, entry.Target)) RenderTexture.active = null;
                    entry.Target.Release();
                    UnityEngine.Object.Destroy(entry.Target);
                }
            }
            catch { }
            try { if (entry.Material != null) UnityEngine.Object.Destroy(entry.Material); } catch { }
            try { if (entry.Mesh != null) UnityEngine.Object.Destroy(entry.Mesh); } catch { }

            // A GameObject still holding a preparing VideoPlayer is destroyed on a later
            // frame rather than this one: the native decoder is mid-open and cutting it down
            // here crashes the process instead of throwing.
            if (entry.GameObject != null)
            {
                if (preparing)
                {
                    entry.GameObject.transform.SetParent(null);
                    UnityEngine.Object.DontDestroyOnLoad(entry.GameObject);
                    Deferred.Add(new PendingDestroy { Object = entry.GameObject, Since = Time.realtimeSinceStartup });
                }
                else
                    try { UnityEngine.Object.Destroy(entry.GameObject); } catch { }
            }

            entry.Target = null;
            entry.Material = null;
            entry.Mesh = null;
            entry.Renderer = null;
            entry.Audio = null;
            entry.GameObject = null;
            entry.HasFrame = false;
            entry.Prepared = false;
            entry.PrepareFailed = false;
            entry.Converting = false;
            entry.SourcePath = null;
            entry.OriginalPath = null;
            entry.PlayerError = null;
            entry.SourceError = null;
            entry.SeekPending = false;
            entry.SeekAttempts = 0;
            entry.StallFrames = 0;
            entry.LastObservedTime = -1;
            entry.LastRestartFrame = int.MinValue / 2;
            entry.Duration = 0;
            entry.Width = entry.Height = 0;
        }

        // -------------------------------------------------------------- parsing

        private static string ScopeOf(Dictionary<string, object> args)
        {
            if (args != null && args.TryGetValue("__instance", out var v) && v != null) return v.ToString();
            return null;
        }

        private static string Str(Dictionary<string, object> args, string key, string fallback)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null) return v.ToString();
            return fallback;
        }

        private static double Num(Dictionary<string, object> args, string key, double fallback)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null)
            {
                try
                {
                    double n = Convert.ToDouble(v);
                    if (!double.IsNaN(n) && !double.IsInfinity(n) && Math.Abs(n) <= float.MaxValue) return n;
                }
                catch { }
            }
            return fallback;
        }

        private static bool Bool(Dictionary<string, object> args, string key, bool fallback)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null)
            {
                if (v is bool b) return b;
                try { return Convert.ToBoolean(v); } catch { }
            }
            return fallback;
        }
    }
}
