using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    /// contain anything that drifts every frame.
    /// </summary>
    public static class NoiseTextureFactory
    {
        public const float PixelsPerUnit = 100f;

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

        private static readonly BakedFrameCache FrameCache = new BakedFrameCache(PixelsPerUnit);
        // Retained for diagnostics which inspect outstanding frame ownership.
        private static readonly Dictionary<string, BakedFrameCache.Frames> Cache = FrameCache.Cache;
        public static int CachedFrameSets => Cache.Count;
        public static int Generations => FrameCache.Generations;
        public static int Bakes => FrameCache.Bakes;
        public static float LastBakeMs => FrameCache.LastBakeMs;
        public static float LastUploadMs => FrameCache.LastUploadMs;
        public static float WorstBakeMs => FrameCache.WorstBakeMs;
        public static string LastBakeDetail => FrameCache.LastBakeDetail;

        public static BakedFrameCache.Frames Get(Request req, out string error)
        {
            var frames = Begin(req, out error);
            if (frames == null) return null;
            while (!frames.Ready && string.IsNullOrEmpty(frames.Error))
            {
                Pump(1000f);
                if (!frames.PixelsReady) System.Threading.Thread.Sleep(1);
            }
            error = frames.Error;
            if (error != null) { Release(frames); return null; }
            return frames;
        }

        public static BakedFrameCache.Frames Begin(Request req, out string error)
        {
            error = null;
            if (req.Provider == null || !req.Provider.TryRuntimeResource("frameProgram", req.Provider.VectorPrograms, out _) ||
                !req.Provider.TryRuntimeResource("framePlan", req.Provider.VectorPrograms, out _))
            { error = "package does not declare its CPU bake program and plan; update the event package"; return null; }
            if (!TryKey(req, out var key, out error)) return null;
            return FrameCache.Begin(key, cancellation =>
            {
                var plan = CpuPackageBake.Prepare(req, cancellation);
                return new BakedFrameCache.Plan { Width = plan.Width, Height = plan.Height, Count = plan.Frames };
            }, (pixels, width, height, cancellation) => CpuPackageBake.Render(req, pixels, width, height, cancellation));
        }

        public static void Pump(float budgetMs) => FrameCache.Pump(budgetMs);
        public static void Release(BakedFrameCache.Frames frames) => FrameCache.Release(frames);
        public static void Clear() => FrameCache.Clear();

        /// <summary>
        /// The generation key, exposed so the renderer compares exactly what the factory
        /// caches. A separate key in the renderer drifts from this one and causes either
        /// missed rebuilds or a rebuild on every frame.
        /// </summary>
        public static string Key(Request req)
        {
            if (!TryKey(req, out var key, out var error)) throw new ArgumentException(error);
            return key;
        }
        private static readonly ConditionalWeakTable<PEV.Model.EventManifest, DeclarativeCacheKey<Request>> KeySpecs =
            new ConditionalWeakTable<PEV.Model.EventManifest, DeclarativeCacheKey<Request>>();
        private static DeclarativeCacheKey<Request> ReadKeySpec(PEV.Model.EventManifest provider)
        {
            if (provider == null) throw new ArgumentException("Missing package provider");
            return KeySpecs.GetValue(provider, package =>
            {
                if (!package.TryRuntimeResource("frameKey", package.KeySpecs, out var path))
                    throw new ArgumentException("Package frame cache key is not declared");
                return DeclarativeCacheKey<Request>.Parse(PEV.Loading.EventLoader.ReadResource(package.SourcePath, path, 32768));
            });
        }
        internal static bool TryStickyBucket(PEV.Model.EventManifest provider, float value, int previous, out int bucket, out string error)
        {
            bucket = 0; error = null;
            try
            {
                if (!ReadKeySpec(provider).TryBucket("TargetPixelWidth", out var spec))
                    throw new ArgumentException("Package frame resolution bucket is not declared");
                bucket = StickyQuantization.Update(value, previous, spec.Step, spec.Minimum, spec.Maximum, spec.Hysteresis);
                return true;
            }
            catch (Exception e) { error = "frame resolution: " + e.Message; return false; }
        }
        internal static bool TryKey(Request req, out string key, out string error)
        {
            key = null; error = null;
            try
            {
                var spec = ReadKeySpec(req.Provider);
                key = PackageIdentity.Of(req.Provider) + "|" + spec.Build(req);
                return true;
            }
            catch (Exception e) { error = "frame cache key: " + e.Message; return false; }
        }

    }
}
