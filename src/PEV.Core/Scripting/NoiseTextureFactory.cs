using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Procedural noise-field textures, modelled on the original BlockRender pipeline.
    ///
    /// The reference effect is not "a noise pattern": it composes three layers from one
    /// low-resolution mask — a translucent fill, a bright edge traced around the mask, and
    /// sparse spark grains — with a displacement field that warps them so the boundary
    /// reads as jagged rather than geometric. That is reproduced here per texel:
    ///
    ///   1. rasterise the union of every region into a cell grid (boolean union, max)
    ///   2. trace the edge as "inside cell with an outside neighbour"
    ///   3. fill = fillColour * fillOpacity, edge = edgeColour * edgeOpacity
    ///   4. sparks = sparse bright grains, sampled through the displacement field
    ///
    /// Working at cell resolution is what gives the authentic blocky silhouette: the
    /// original mask is low-resolution too, and its edge is quantised to those same blocks.
    ///
    /// Frames are ref-counted and shared. Generation is O(cells) per frame, and the result
    /// is only rebuilt when the structure key changes — which is why the key must not
    /// contain anything that drifts every frame (see ResolutionBucket).
    /// </summary>
    public static class NoiseTextureFactory
    {
        /// <summary>
        /// Pixel widths are quantised to this step before they enter the structure key.
        /// The camera zooms on the beat, so a continuous width would invalidate the key on
        /// every frame and regenerate eight textures per update — the reported stutter.
        /// </summary>
        public const int ResolutionStep = 96;


        public const float PixelsPerUnit = 100f;

        public sealed class Frames
        {
            public Texture2D[] Textures;
            public Sprite[] Sprites;
            /// <summary>
            /// World size of one sprite at scale 1. Sprites are built at 100 pixels per
            /// unit, so this is (texW/100, texH/100). Callers must divide their desired
            /// world size by it: multiplying a scale by the size directly multiplies the
            /// quad by the texture resolution as well.
            /// </summary>
            public Vector2 NaturalSize;
            public string Key;
            internal int Refs;

            // --- bake bookkeeping -----------------------------------------------------------
            // A set is computed on a worker and then uploaded a few frames at a time, so it
            // exists in three states: placed (pixels only), partly uploaded, and complete.
            internal Color32[][] Pixels;
            internal int TexW, TexH;
            internal int Uploaded;
            internal volatile bool PixelsReady;
            internal volatile bool Cancelled;
            internal readonly System.Threading.CancellationTokenSource Cancellation = new System.Threading.CancellationTokenSource();
            internal bool Ready;
            internal volatile string Error;
            internal float WorkerMs;
            internal float UploadTotalMs;
            internal System.Threading.Thread Worker;
            internal float WorstUploadSliceMs;
        }

        public struct Piece
        {
            public Vector2 Offset;
            public Vector2 Half;
            public float Rotation;
            public float Feather;
            public bool Circle;

            /// <summary>
            /// Coastline band for this piece, in world units. Per piece, not per cluster:
            /// a small rectangle sharing a depth with a large region would otherwise be
            /// eroded by that region's band and end up as debris. Its own size decides.
            /// </summary>
            public float Band;

            // Per-piece colours: a union merges geometry, not appearance. Two regions of
            // different colours at the same depth must each keep their own fill and edge,
            // with only the silhouette shared.
            public Color FillColor;
            public Color EdgeColor;
            public float FillOpacity;
            public float EdgeOpacity;
        }

        public struct Request
        {
            // Field initialisers make the defaulted values real, but the language then requires
            // an explicit constructor for the struct.
            public Request() { }

            internal PEV.Model.EventManifest Provider;
#if PEV_DIAGNOSTICS
            internal LegacyFieldTextureSet Samples;
#endif

            public float WidthWorld;
            public float HeightWorld;
            public Piece[] Pieces;

            public Color FillColor;
            public Color EdgeColor;
            public Color GlowColor;
            public Color SparkColor;
            public float FillOpacity;

            /// <summary>
            /// How far the fill is lifted toward the outline's bright tone.
            ///
            /// The sprite is alpha-blended over the live scene, so the composite inside the
            /// region is `fill * alpha + behind * (1 - alpha)`. Raising `fill` therefore makes
            /// everything behind the region read brighter without reading a single pixel of
            /// it: no grab, no readback, no extra pass. That is the whole of "raise the
            /// background's exposure through the field"; sampling the scene instead would
            /// only have been needed to warp its content, which the baking design cannot pay
            /// for anyway.
            /// </summary>
            public float Exposure;

            /// <summary>Frames per second the flipbook is played at. Set by the renderer.</summary>
            public float EvolveSpeed = 12f;

            /// <summary>
            /// How long one flipbook cycle lasts, in shader seconds.
            ///
            /// The reference advances its displacement field continuously, so it never loops.
            /// A flipbook must, and a linear drift cannot return to its start without a jump —
            /// the displacement only repeats every ~21.8 shader-seconds, which would need a
            /// quarter-thousand frames. So the drift is driven around a circle instead (see
            /// NoiseComposer): any loop length closes exactly, and the motion still reads as a
            /// continuous stream. This is the one place the port is not a literal copy, and it
            /// exists only because a baked cycle cannot chase an unbounded offset.
            /// </summary>
            public float LoopPeriod = 2f;

            /// <summary>
            /// How much of the spark-processed fill survives, the reference's `fillStrength`.
            /// Separate from FillOpacity: one is the alpha the region occupies, the other how
            /// strongly the grain tints the fill before alpha is applied.
            /// </summary>
            public float FillStrength;

            /// <summary>
            /// What the region is drawn over, standing in for the reference's scene grab.
            ///
            /// main.glsl builds the fill as `fillBase + fillStrength * (processed - fillBase)`
            /// where `processed` is the captured scene. Without a scene the fill is just
            /// fillBase, which is far too bright — that is what made the field read as a pale
            /// pink wash. The level's own background colour is the honest substitute: it is
            /// exactly what the grab would have returned wherever no track covers the region.
            /// </summary>
            public Color SceneColor;

            public float EdgeOpacity;
            public float GlowOpacity;
            public float SparkOpacity;

            public float Brightness;
            public float Saturation;
            public float SampleRate;
            public float LineOpacity;
            public float Displace;

            /// <summary>
            /// Displacement strength of the coverage mask, in mask cells. Coverage itself is
            /// plain rectangle geometry; the noise only wobbles WHERE it is sampled, which is
            /// what gives the boundary its ragged, streaming bite. The reference's
            /// `_NoiseDisplaceStrength` is 1.0, i.e. about one cell of drift.
            /// </summary>
            public float EdgeDistance;

            /// <summary>
            /// Noise features across the shorter side. Low values give slow, broad wobble;
            /// high values give a finely shredded coast.
            /// </summary>
            public float EdgeDetail;

            /// <summary>Border line thickness, in mask cells. The reference uses exactly 1.</summary>
            public int EdgeThickness;

            /// <summary>
            /// Ridges of glow outside the border. Each pass dilates one more cell and
            /// contributes a lower weight, which is the reference's 5-pass bloom.
            /// </summary>
            public float GlowPasses;

            /// <summary>
            /// How far detached fragments survive outside the geometry, as a fraction of
            /// the shorter side. Also controls how much of the quad is padded to show them.
            /// </summary>
            public float EdgeMargin;

            /// <summary>
            /// How much debris survives outside the coast. 0 leaves a clean boundary; higher
            /// values scatter torn fragments around the region.
            /// </summary>
            public float EdgeFragment;

            public int FrameCount;
            public int Seed;
            public float TargetPixelWidth;

            /// <summary>
            /// On-screen pixel width of the quad, before the budget shrinks the texture.
            ///
            /// The reference sizes its compose and effects targets from the SCREEN —
            /// `maskPixelScale = 8` means one mask texel is eight screen pixels — while the
            /// texture here is usually a fraction of that width. Sizing the grids from the
            /// texture instead made every stair step 4–5x too big: the same displacement then
            /// throws the coast by whole blocks, which reads as rectangular bites appearing
            /// at random rather than a fine edge flowing. Kept out of the fingerprint because
            /// it only decides grid density and is already bucketed by the caller.
            /// </summary>
            public int ScreenPixelWidth;

            /// <summary>
            /// Horizontal slice displacement, 0..1. Rows are shifted sideways by a
            /// per-frame amount, which is the reference's GlitchMap: whole scanlines of the
            /// field tear sideways for a frame and snap back.
            /// </summary>
            public float Glitch;

            /// <summary>
            /// World-space padding on every side of the quad, beyond the geometry's own
            /// bounds. Without it the coastline can only bite inward and detached flakes
            /// are clipped away at the quad edge; with it they drift outside the region and
            /// stay visible, which is what the reference shows.
            /// </summary>
            public float Margin;

            /// <summary>
            /// Pre-state ("ready") fill: the region exactly where it will be, flat and
            /// translucent, with grains but no border and no glow. The reference flashes this
            /// for a moment before the border arrives, which is what makes its entrance read
            /// as a warning rather than a pop-in.
            /// </summary>
            public bool Ready;
        }

        private static readonly Dictionary<string, Frames> Cache =
            new Dictionary<string, Frames>(StringComparer.Ordinal);

        public static int CachedFrameSets => Cache.Count;

        /// <summary>
        /// Monotonic count of texture sets built. The harness samples it across a steady
        /// window: a value that climbs while nothing changed means a key includes
        /// per-frame data, which shows up to the player as stutter.
        /// </summary>
        public static int Generations;

        /// <summary>
        /// Worst and most recent bake wall time, in milliseconds.
        ///
        /// "It must not stutter" needs a number, not an impression. A bake is the only moment
        /// this effect touches the main thread, so this is the quantity that decides whether
        /// the budget holds; the harness asserts a ceiling against it.
        /// </summary>
        public static float LastBakeMs;

        /// <summary>
        /// Wall time of the last bake's texture upload, in milliseconds.
        ///
        /// The pixel work is fanned out over workers, but Texture2D.SetPixels32/Apply must
        /// run on the main thread — so if a bake is slow, this is where to look first, and
        /// the number says whether the remaining cost is upload or compute.
        /// </summary>
        public static float LastUploadMs;
        public static float WorstBakeMs;
        public static int Bakes;

        /// <summary>
        /// What the last bake actually cost, per stage, plus the size it settled on.
        ///
        /// The cumulative stage counters are useless for tuning: thirteen bakes summed together
        /// cannot say whether one budget-driven resolution is too big or the upload is the tail.
        /// This is one bake's own numbers, taken as deltas around Generate, and it includes the
        /// resolution the budget policy actually chose — the figure the whole question turns on.
        /// </summary>
        public static string LastBakeDetail = "";

        /// <summary>
        /// How many worker threads a bake may use.
        ///
        /// A bake used to take the whole machine. Measured on a 16-core box it ran 466 ms of
        /// wall time while the game thread still had to render, and saturating every core is
        /// how a background job becomes a visible stutter without ever touching the main
        /// thread. Half the cores, at least one, leaves the game its share; the set is bigger
        /// in wall time and completely invisible, which is the right trade for a texture that
        /// is only ever seen after it finishes.
        /// </summary>
        private static readonly System.Threading.SemaphoreSlim BakeGate = new System.Threading.SemaphoreSlim(1);

        public static Frames Get(Request req, out string error)
        {
            var frames = Begin(req, out error);
            if (frames == null) return null;
            // Blocking convenience for callers with no frame loop of their own (diagnostics,
            // tests). The render path uses Begin + Pump instead, so no frame ever waits.
            while (!frames.Ready && string.IsNullOrEmpty(frames.Error))
            {
                Pump(1000f);
                if (!frames.PixelsReady) System.Threading.Thread.Sleep(1);
            }
            error = frames.Error;
            if (error != null) { Release(frames); return null; }
            return frames;
        }

        /// <summary>
        /// Places a bake and returns immediately.
        ///
        /// Measuring settled the design question: one set is 3 MB of pixels, and doing it
        /// inline cost 92 ms (later ~300 ms with the screen-density grids) of a single frame —
        /// 63 ms of render plus 30 ms of texture upload, all on the main thread. That is a
        /// visible hitch, and it is not avoidable by shaving instructions. So the pixel work
        /// moves to a worker thread and the upload is spread over consecutive frames by
        /// <see cref="Pump"/>, with the previous set staying on screen meanwhile.
        ///
        /// The returned object is cached by key immediately, so a second request for the same
        /// structure joins the bake in flight rather than starting another.
        /// </summary>
        public static Frames Begin(Request req, out string error)
        {
            error = null;
            string key = Fingerprint(req);
            if (Cache.TryGetValue(key, out var hit) && HitIsAlive(hit))
            {
                hit.Refs++;
                return hit;
            }

            if (req.Provider == null || !req.Provider.VectorPrograms.ContainsKey("bake") ||
                !req.Provider.VectorPrograms.ContainsKey("bakePlan"))
            { error = "package does not declare its CPU bake program and plan; update the event package"; return null; }
            var frames = new Frames { Key = key, Refs = 1 };

            Generations++;
            Bakes++;
            Cache[key] = frames;

            var worker = new System.Threading.Thread(() => RunWorker(req, frames))
            {
                IsBackground = true,
                Name = "PEV.NoiseBake"
            };
            frames.Worker = worker;
            worker.Start();
            return frames;
        }

        /// <summary>
        /// Uploads as much of any finished bake as fits in <paramref name="budgetMs"/>.
        ///
        /// Textures can only be created on the main thread, so this is the half of the bake
        /// that cannot be moved off it. It is bounded per call, and one call per rendered
        /// frame is enough: 96 frames at 242x132 leave the queue in about a quarter second,
        /// with the effect showing the set it already had.
        ///
        /// A set with nothing uploaded yet always gets its first two frames regardless of the
        /// budget, so a field appears on the frame it is placed instead of a beat later.
        /// </summary>
        public static void Pump(float budgetMs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (var kv in Cache)
            {
                var f = kv.Value;
                if (f == null || !f.PixelsReady || f.Ready || f.Error != null) continue;

                bool first = f.Uploaded == 0;
                while (f.Uploaded < f.Textures.Length)
                {
                    try { UploadOne(f, f.Uploaded); }
                    catch (Exception e)
                    {
                        f.Error = "noise upload failed: " + e.Message;
                        DestroyFrames(f);
                        break;
                    }
                    f.Uploaded++;
                    if (first && f.Uploaded >= 2) break;
                    if (watch.Elapsed.TotalMilliseconds >= budgetMs) break;
                }

                if (f.Error != null) continue;
                float slice = (float)watch.Elapsed.TotalMilliseconds;
                if (slice > f.WorstUploadSliceMs) f.WorstUploadSliceMs = slice;

                if (f.Uploaded >= f.Textures.Length)
                {
                    f.Ready = true;
                    LastUploadMs = slice;
                    LastBakeDetail = $"{f.TexW}x{f.TexH}x{f.Textures.Length} " +
                                     $"payload={(long)f.TexW * f.TexH * f.Textures.Length / 1024}KB " +
                                     $"worker={f.WorkerMs:F1}ms uploadTotal={f.UploadTotalMs:F1}ms " +
                                     $"worstSlice={f.WorstUploadSliceMs:F1}ms";
                    // What the frame actually paid. The worker's duration is reported above so
                    // the two cannot be confused: only this number can drop a frame.
                    LastBakeMs = f.WorstUploadSliceMs;
                    if (LastBakeMs > WorstBakeMs) WorstBakeMs = LastBakeMs;
                }
                if (watch.Elapsed.TotalMilliseconds >= budgetMs) return;
            }
        }

        private static void UploadOne(Frames f, int k)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var pixels = f.Pixels[k];
            if (pixels == null) return;

            var tex = new Texture2D(f.TexW, f.TexH, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                name = "PEV.Noise." + k
            };
            f.Textures[k] = tex;
            tex.SetPixels32(pixels);
            tex.Apply(false);

            f.Sprites[k] = Sprite.Create(
                tex, new Rect(0f, 0f, f.TexW, f.TexH), new Vector2(0.5f, 0.5f), PixelsPerUnit);
            f.Sprites[k].name = "PEV.NoiseSprite." + k;
            f.UploadTotalMs += (float)watch.Elapsed.TotalMilliseconds;
            f.Pixels[k] = null;
        }

        public static void Release(Frames frames)
        {
            if (frames == null) return;
            frames.Refs--;
            if (frames.Refs > 0) return;
            if (frames.Key != null && Cache.TryGetValue(frames.Key, out var cached) && ReferenceEquals(cached, frames))
                Cache.Remove(frames.Key);
            DestroyFrames(frames);
        }

        public static void Clear()
        {
            foreach (var kv in Cache) DestroyFrames(kv.Value);
            Cache.Clear();
        }

        private static void DestroyFrames(Frames frames)
        {
            Sprite[] sprites;
            Texture2D[] textures;
            lock (frames)
            {
                frames.Cancelled = true;
                frames.PixelsReady = false;
                frames.Pixels = null;
                sprites = frames.Sprites; frames.Sprites = null;
                textures = frames.Textures; frames.Textures = null;
            }
            frames.Cancellation.Cancel();
            if (sprites != null)
            {
                foreach (var s in sprites)
                    if (s != null) UnityEngine.Object.Destroy(s);
            }
            if (textures != null)
            {
                foreach (var t in textures)
                    if (t != null) UnityEngine.Object.Destroy(t);
            }
        }

        private static bool HitIsAlive(Frames f)
        {
            // A set that is still being built or uploaded is alive even though most of its
            // slots are null: it has been placed in the cache on purpose so a second request
            // for the same structure joins it instead of starting a competitor. Only a set
            // that claims to be finished is checked slot by slot.
            if (!f.Ready) return string.IsNullOrEmpty(f.Error);
            if (f.Textures == null || f.Sprites == null) return false;
            foreach (var t in f.Textures) if (t == null) return false;
            foreach (var s in f.Sprites) if (s == null) return false;
            return true;
        }

        /// <summary>Coarse bucket so camera zoom does not invalidate the texture every frame.</summary>
        public static int ResolutionBucket(float px)
        {
            int steps = Mathf.Clamp(Mathf.RoundToInt(px / ResolutionStep), 1, 12);
            return steps * ResolutionStep;
        }

        /// <summary>
        /// The generation key, exposed so the renderer compares exactly what the factory
        /// caches. A separate key in the renderer drifts from this one and causes either
        /// missed rebuilds or a rebuild on every frame.
        /// </summary>
        public static string Key(Request req)
        {
            return Fingerprint(req);
        }

        private static string Fingerprint(Request r)
        {
            var sb = new StringBuilder(240);
            sb.Append(PackageIdentity.Of(r.Provider)).Append('|');
            sb.Append(F(r.WidthWorld)).Append('|').Append(F(r.HeightWorld)).Append('|')
              .Append(C(r.FillColor)).Append('|').Append(C(r.EdgeColor)).Append('|')
              .Append(C(r.SparkColor)).Append('|')
              .Append(F(r.FillOpacity)).Append('|').Append(F(r.Exposure)).Append('|')
              .Append(F(r.EdgeOpacity)).Append('|')
              .Append(F(r.SparkOpacity)).Append('|')
              .Append(C(r.GlowColor)).Append('|').Append(F(r.GlowOpacity)).Append('|')
              .Append(F(r.GlowPasses)).Append('|').Append(F(r.FillStrength)).Append('|')
              .Append(C(r.SceneColor)).Append('|').Append(r.Ready).Append('|')
              .Append(F(r.Brightness)).Append('|').Append(F(r.Saturation)).Append('|')
              .Append(F(r.SampleRate)).Append('|').Append(F(r.LineOpacity)).Append('|')
              .Append(F(r.Displace)).Append('|')
              .Append(F(r.Glitch)).Append('|')
              .Append(F(r.EdgeDistance)).Append('|').Append(F(r.EdgeDetail)).Append('|')
              .Append(r.EdgeThickness).Append('|')
              .Append(F(r.EdgeMargin)).Append('|').Append(F(r.EdgeFragment)).Append('|')
              .Append(r.FrameCount).Append('|').Append(r.Seed).Append('|')
              .Append(F(r.Margin)).Append('|')
              .Append(ResolutionBucket(r.TargetPixelWidth)).Append('|')
              .Append(r.ScreenPixelWidth).Append('|');

            if (r.Pieces != null)
            {
                foreach (var p in r.Pieces)
                {
                    sb.Append(F(p.Offset.x)).Append(',').Append(F(p.Offset.y)).Append(',')
                      .Append(F(p.Half.x)).Append(',').Append(F(p.Half.y)).Append(',')
                      .Append(F(p.Rotation)).Append(',').Append(F(p.Feather)).Append(',')
                      .Append(p.Circle ? 'c' : 'r').Append(';');
                }
            }
            return sb.ToString();
        }

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
        private static string C(Color c) => F(c.r) + "," + F(c.g) + "," + F(c.b);

        /// <summary>
        /// Worker half of a bake: renders every frame's pixels, then hands them back.
        ///
        /// Nothing here touches UnityEngine objects. Texture creation is the one thing that
        /// cannot happen off the main thread, and it is left to Pump; everything else — the
        /// compose, edge and glow passes, the spark composite — is pure arithmetic over
        /// arrays, which is exactly what makes this split possible.
        /// </summary>
        private static void RunWorker(Request req, Frames f)
        {
            bool entered = false;
            try
            {
                BakeGate.Wait(f.Cancellation.Token); entered = true;
                f.Cancellation.Token.ThrowIfCancellationRequested();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var plan = CpuPackageBake.Prepare(req, f.Cancellation.Token);
                var pixels = new Color32[plan.Frames][];
                lock (f)
                {
                    if (f.Cancelled) return;
                    f.TexW = plan.Width; f.TexH = plan.Height;
                    f.Textures = new Texture2D[plan.Frames]; f.Sprites = new Sprite[plan.Frames]; f.Pixels = pixels;
                    f.NaturalSize = new Vector2(plan.Width / PixelsPerUnit, plan.Height / PixelsPerUnit);
                }
                CpuPackageBake.Render(req, pixels, plan.Width, plan.Height, f.Cancellation.Token);
                lock (f)
                {
                    f.WorkerMs = (float)watch.Elapsed.TotalMilliseconds;
                    if (!f.Cancelled) f.PixelsReady = true;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (!f.Cancelled) f.Error = "noise bake failed: " + e.Message;
            }
            finally { if (entered) BakeGate.Release(); }
        }
    }
}
