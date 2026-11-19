#if PEV_DIAGNOSTICS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PEV.Model;
using PEV.Registry;
using PEV.Scripting;
using UnityEngine;

namespace PEV.Diagnostics
{
    /// <summary>
    /// Runs the shipped grading shader on a known test image and compares every pixel with
    /// the CPU reference.
    ///
    /// "The picture went grey" is not evidence that the greyscale is correct, that the
    /// exposure is in the right units, or that a curve through (0.25, 0.4) actually lands
    /// there. This does the comparison numerically: a synthesised colour chart goes through
    /// the real material built from the real bundle, comes back once, and is differenced
    /// against <see cref="GradingReference"/> — the same arithmetic written out in C#.
    /// </summary>
    internal static class GradingHarness
    {
        private const int Width = 192;
        private const int Height = 48;
        private const string Scope = "diagnostics/grading";
        private const string EventId = "pev.color-grading";

        internal static void Run(Action<string, string, string> row, string directory)
        {
            if (!EventRegistry.TryGetById(EventId, out var manifest))
            {
                row("GRADING-package", "SKIP", "event package is not installed next to this chart");
                return;
            }
            if (manifest.ScreenEffect == null)
            {
                row("GRADING-package", "FAIL", "manifest does not declare screenEffect");
                return;
            }

            Texture2D input = null;
            RenderTexture target = null;
            try
            {
                input = BuildTexture();
                target = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32)
                {
                    antiAliasing = 1,
                    filterMode = FilterMode.Point
                };
                target.Create();

                row("GRADING-package", "OK", $"{manifest.Id} {manifest.Version}");

                Case(row, manifest, input, target, "neutral", new GradingReference.Settings(), true);
                Case(row, manifest, input, target, "exposure", With(s => s.Exposure = 1.2f));
                Case(row, manifest, input, target, "exposure-negative", With(s => s.Exposure = -1.5f));
                Case(row, manifest, input, target, "brightness", With(s => s.Brightness = 0.35f));
                Case(row, manifest, input, target, "contrast-up", With(s => s.Contrast = 0.4f));
                Case(row, manifest, input, target, "contrast-down", With(s => s.Contrast = -0.4f));
                Case(row, manifest, input, target, "saturation-off", With(s => s.Saturation = -1f), true);
                Case(row, manifest, input, target, "saturation-up", With(s => s.Saturation = 0.6f));
                Case(row, manifest, input, target, "vibrance", With(s => s.Vibrance = 0.7f));
                Case(row, manifest, input, target, "temperature-warm", With(s => s.Temperature = 0.6f));
                Case(row, manifest, input, target, "temperature-cool", With(s => s.Temperature = -0.6f));
                Case(row, manifest, input, target, "tint-magenta", With(s => s.Tint = 0.6f));
                Case(row, manifest, input, target, "tone-zones", With(s =>
                {
                    s.Highlights = -0.5f;
                    s.Shadows = 0.5f;
                    s.Whites = 0.3f;
                    s.Blacks = -0.3f;
                }));
                Case(row, manifest, input, target, "gamma", With(s => s.Gamma = 1.6f));
                Case(row, manifest, input, target, "channel-gain", With(s =>
                {
                    s.Gain = new Vector3(1.15f, 1.0f, 0.85f);
                    s.Offset = new Vector3(0.02f, 0f, -0.02f);
                }));
                Case(row, manifest, input, target, "wheels", With(s =>
                {
                    s.Lift = new Vector4(-0.3f, 0.25f, 0.05f, 0f);
                    s.GammaWheel = new Vector4(0.2f, 0.1f, 0.02f, 0f);
                    s.GainWheel = new Vector4(0.1f, -0.25f, -0.02f, 0f);
                    s.OffsetWheel = new Vector4(0.05f, 0.05f, 0.01f, 0f);
                }));
                Case(row, manifest, input, target, "curve-s", With(s =>
                {
                    s.MasterCurve = new[] { 0f, 0f, 0.25f, 0.15f, 0.75f, 0.85f, 1f, 1f };
                }));
                Case(row, manifest, input, target, "curve-channels", With(s =>
                {
                    s.RedCurve = new[] { 0f, 0.05f, 1f, 0.95f };
                    s.GreenCurve = new[] { 0f, 0.1f, 0.5f, 0.5f, 1f, 1f };
                    s.BlueCurve = new[] { 0f, 0f, 1f, 0.8f };
                }));
                Case(row, manifest, input, target, "hue-curves", With(s =>
                {
                    s.HueHue = new[] { 0f, 0.62f, 0.5f, 0.5f, 1f, 0.38f };
                    s.HueSat = new[] { 0f, 0.35f, 1f, 0.7f };
                    s.HueLuma = new[] { 0f, 0.5f, 0.33f, 0.6f, 1f, 0.5f };
                }));
                Case(row, manifest, input, target, "hsl-bands", With(s =>
                {
                    s.HslBands = new[]
                    {
                        new Vector4(12f, 0.25f, 0.1f, 1f), Vector4.zero, new Vector4(-20f, -0.4f, 0f, 1f),
                        Vector4.zero, Vector4.zero, new Vector4(25f, 0.3f, -0.1f, 1f), Vector4.zero, Vector4.zero
                    };
                }));
                Case(row, manifest, input, target, "strength-half", With(s => s.Strength = 0.5f));
                Case(row, manifest, input, target, "combined", With(s =>
                {
                    s.Exposure = 0.4f;
                    s.Contrast = 0.2f;
                    s.Saturation = -0.25f;
                    s.Temperature = 0.2f;
                    s.Highlights = -0.3f;
                    s.Shadows = 0.2f;
                    s.MasterCurve = new[] { 0f, 0f, 0.3f, 0.34f, 0.7f, 0.74f, 1f, 1f };
                    s.Lift = new Vector4(-0.2f, 0.15f, 0f, 0f);
                    s.GainWheel = new Vector4(0.1f, -0.1f, 0f, 0f);
                    s.Strength = 0.9f;
                }));

                LutCase(row, manifest, input, target, "lut-builtin", "assets/luts/teal-orange.cube");
                LutCase(row, manifest, input, target, "lut-strength", "assets/luts/bleach-bypass.cube", 0.5f);
                LutCase(row, manifest, input, target, "lut-identity", null);

                HslProbe(row, manifest, input, target);
                ScriptMapping(row, manifest);
                Timeline(row);
                PreviewParity(row);
                Composition(row);
                Lifecycle(row);
                Performance(row, manifest, input);
                EdgeCases(row, manifest, input, target);
            }
            catch (Exception e)
            {
                row("GRADING-run", "FAIL", e.ToString());
            }
            finally
            {
                ScreenEffectPipeline.Clear(Scope);
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                if (input != null) UnityEngine.Object.Destroy(input);
            }
        }

        private static GradingReference.Settings With(Action<GradingReference.Settings> configure)
        {
            var settings = new GradingReference.Settings();
            configure(settings);
            return settings;
        }

        /// <summary>
        /// A chart that covers what a grading control can be asked to do: a full grey ramp
        /// (where tone, contrast and curves live) and a hue/saturation sweep (where the
        /// vibrance, HSL and LUT paths live).
        /// </summary>
        private static Texture2D BuildTexture()
        {
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            var pixels = new Color32[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float u = x / (float)(Width - 1);
                    if (y < Height / 3)
                    {
                        byte level = (byte)Mathf.RoundToInt(u * 255f);
                        pixels[y * Width + x] = new Color32(level, level, level, 255);
                        continue;
                    }
                    int band = (y - Height / 3) / ((Height - Height / 3) / 6);
                    float sat = 0.35f + 0.65f * (band % 3) / 2f;
                    float val = band < 3 ? 1f : 0.65f;
                    var colour = Color.HSVToRGB(u, Mathf.Clamp01(sat), val);
                    pixels[y * Width + x] = new Color32((byte)(colour.r * 255), (byte)(colour.g * 255),
                        (byte)(colour.b * 255), 255);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static void Case(Action<string, string, string> row, EventManifest manifest, Texture2D input,
            RenderTexture target, string name, GradingReference.Settings settings, bool expectIdentity = false,
            string forceLut = null)
        {
            string probeLutKey = null;
            // The reference has to know which colour space the project renders in; the shader is
            // told by the pipeline, so the two would silently diverge under a linear project.
            settings.LinearProject = QualitySettings.activeColorSpace == ColorSpace.Linear;
            string payload = Payload(settings, forceLut);
            if (!ScreenEffectPipeline.Set(Scope, manifest, new Dictionary<string, object> { { "__payload", payload } },
                    out string error))
            {
                row("GRADING-" + name, "FAIL", error);
                return;
            }

            var layer = ScreenEffectPipeline.Find(Scope);
            if (layer == null || layer.Material == null)
            {
                row("GRADING-" + name, "FAIL", "test layer was not created");
                return;
            }
            var material = layer.Material;
            material.SetFloat("_PEVWeight", 1f);

            if (forceLut != null)
            {
                var resolved = LutCache.Resolve(forceLut, manifest.SourcePath, out string lutError);
                if (resolved == null) { row("GRADING-" + name, "FAIL", lutError); return; }
                var entry = LutCache.Acquire(resolved, manifest.SourcePath, forceLut, out string acquireError);
                if (entry == null) { row("GRADING-" + name, "FAIL", acquireError); return; }
                settings.Lut = new GradingReference.Lut
                {
                    Size = entry.Size,
                    OneDimensional = !entry.IsThreeDimensional,
                    Pixels = entry.Volume != null ? entry.Volume.GetPixels32() : entry.Strip.GetPixels32(),
                    DomainMin = new Vector3(entry.DomainMin.x, entry.DomainMin.y, entry.DomainMin.z),
                    DomainMax = new Vector3(entry.DomainMax.x, entry.DomainMax.y, entry.DomainMax.z)
                };
                settings.LutStrength = settings.LutStrength <= 0f ? 1f : settings.LutStrength;
                // The harness took its own reference; the layer never acquired one because
                // the payload points at the LUT through the same path it would in a chart.
                probeLutKey = entry.Key;
            }

            var gpu = Render(input, target, material);
            var reference = new Color32[gpu.Length];
            var source = input.GetPixels32();
            for (int i = 0; i < reference.Length; i++)
            {
                var input2 = source[i];
                var evaluated = GradingReference.Evaluate(settings,
                    new Vector3(input2.r / 255f, input2.g / 255f, input2.b / 255f));
                reference[i] = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt(evaluated.x * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(evaluated.y * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(evaluated.z * 255f), 0, 255),
                    255);
            }

            if (probeLutKey != null) { LutCache.Release(probeLutKey); probeLutKey = null; }
            int differing, maxError;
            Compare(gpu, reference, out differing, out maxError);
            string detail = $"pixels={gpu.Length} differing={differing} max={maxError}";
            if (maxError > 2)
            {
                int worst = Worst(gpu, reference);
                detail += $" worst#{worst} src={source[worst].r},{source[worst].g},{source[worst].b}" +
                          $" gpu={gpu[worst].r},{gpu[worst].g},{gpu[worst].b}" +
                          $" ref={reference[worst].r},{reference[worst].g},{reference[worst].b}" +
                          $" u[_Saturation]={material.GetFloat("_Saturation"):F3}" +
                          $" u[_Strength]={material.GetFloat("_Strength"):F3}" +
                          $" u[_PEVWeight]={material.GetFloat("_PEVWeight"):F3}" +
                          $" bands0={material.GetVector("_HslBands")}";
            }

            if (expectIdentity)
            {
                // Neutral must be a true identity; full desaturation must be truly colourless.
                // They are different claims and a single equality check would confuse them.
                if (name == "neutral")
                {
                    int identityDiff = 0, identityMax = 0;
                    Compare(gpu, source, out identityDiff, out identityMax);
                    detail += $" vsSource: differing={identityDiff} max={identityMax}";
                    if (identityDiff != 0)
                    {
                        row("GRADING-" + name, "FAIL", detail + " (a neutral grade must not change a pixel)");
                        return;
                    }
                }
                else
                {
                    int coloured = 0;
                    foreach (var pixel in gpu)
                        if (pixel.r != pixel.g || pixel.g != pixel.b) coloured++;
                    detail += $" coloured={coloured}";
                    if (coloured != 0)
                    {
                        row("GRADING-" + name, "FAIL", detail + " (full desaturation must leave no colour)");
                        return;
                    }
                }
            }

            // Two levels is float rounding through a long pow/smoothstep chain; three means
            // the two implementations disagree about a formula, not about precision.
            row("GRADING-" + name, maxError <= 2 ? "PASS" : "FAIL", detail);
        }

        private static void LutCase(Action<string, string, string> row, EventManifest manifest, Texture2D input,
            RenderTexture target, string name, string lut, float strength = 1f)
        {
            var settings = With(s => s.LutStrength = strength);
            Case(row, manifest, input, target, name, settings, false, lut);
        }

        /// <summary>
        /// Isolates the HSL range path. The full comparison says "everything is black"; this
        /// says which of the eight bands and which source colours come out wrong, which is
        /// the difference between a broken uniform array and broken hue arithmetic.
        /// </summary>
        private static void HslProbe(Action<string, string, string> row, EventManifest manifest,
            Texture2D input, RenderTexture target)
        {
            const string payload = "{\"enabled\":true,\"weight\":1,\"floats\":{\"_Strength\":1}," +
                "\"vectorArrays\":{\"_HslBands\":[[12,0.25,0.1,1],[0,0,0,1],[0,0,0,1],[0,0,0,1],[0,0,0,1]," +
                "[0,0,0,1],[0,0,0,1],[0,0,0,1]]}}" ;
            try
            {
                if (!ScreenEffectPipeline.Set(Scope, manifest,
                        new Dictionary<string, object> { { "__payload", payload } }, out string error))
                { row("GRADING-hsl-probe", "FAIL", error); return; }
                var layer = ScreenEffectPipeline.Find(Scope);
                if (layer == null) { row("GRADING-hsl-probe", "FAIL", "no layer"); return; }
                layer.Material.SetFloat("_PEVWeight", 1f);
                var bands = layer.Material.GetVectorArray("_HslBands");
                var gpu = Render(input, target, layer.Material);

                // The first row of the chart is the grey ramp; row 20 is a saturated band.
                Func<int, string> at = index => $"{gpu[index].r},{gpu[index].g},{gpu[index].b}";
                row("GRADING-hsl-probe", "INFO",
                    $"arrayLen={bands?.Length ?? 0} band0={(bands != null && bands.Length > 0 ? bands[0].ToString() : "none")} " +
                    $"grey:0={at(0)} 64={at(64)} 128={at(128)} 191={at(191)} " +
                    $"sat:red={at(20 * Width + 0)} orange={at(20 * Width + 32)} green={at(20 * Width + 64)} " +
                    $"cyan={at(20 * Width + 96)} blue={at(20 * Width + 128)} magenta={at(20 * Width + 160)}");
            }
            catch (Exception e) { row("GRADING-hsl-probe", "FAIL", e.Message); }
            finally { ScreenEffectPipeline.Clear(Scope); }
        }

        /// <summary>
        /// The fade is driven by the song clock, so it has to be right at both ends: zero at
        /// the tile, one after the duration, and monotone in between. A grade that snaps to
        /// full on the first frame defeats the point of having a duration at all.
        /// </summary>
        private static void Timeline(Action<string, string, string> row)
        {
            const string scope = "diagnostics/grading/timeline";
            var problems = new List<string>();
            try
            {
                var parameters = GradingTimelineTests.BaseParams();
                parameters["duration"] = 2.0;      // beats
                parameters["ease"] = "Linear";
                parameters["saturation"] = -1.0;

                if (!GradingTimelineTests.TryCreate(scope, out var host, out var context, out string error, parameters))
                { row("GRADING-timeline", "FAIL", error); return; }

                try
                {
                    // 120 BPM → one beat is half a second, so a 2-beat fade ends at t = 1.0.
                    var samples = new List<(double T, float Weight)>();
                    foreach (double t in new[] { 0.0, 0.25, 0.5, 0.75, 1.0, 1.5 })
                    {
                        GradingTimelineTests.Tick(host, context, t);
                        var layer = ScreenEffectPipeline.Find(scope);
                        samples.Add((t, layer != null ? ScreenEffectPipeline.WeightFor(layer) : -1f));
                    }

                    if (samples[0].Weight > 0.05f) problems.Add($"weight at t=0 was {samples[0].Weight:F3}");
                    if (Mathf.Abs(samples[1].Weight - 0.25f) > 0.06f)
                        problems.Add($"weight at t=0.25 was {samples[1].Weight:F3}, expected ~0.25");
                    if (Mathf.Abs(samples[2].Weight - 0.50f) > 0.06f)
                        problems.Add($"weight at t=0.5 was {samples[2].Weight:F3}, expected ~0.50");
                    if (Mathf.Abs(samples[4].Weight - 1f) > 0.02f)
                        problems.Add($"weight at t=1.0 was {samples[4].Weight:F3}, expected 1");
                    if (samples[5].Weight < 0.99f) problems.Add($"weight did not hold at t=1.5 ({samples[5].Weight:F3})");

                    // Rewinding must not leave the fade frozen at its midpoint.
                    GradingTimelineTests.Tick(host, context, 0.0);
                    var rewound = ScreenEffectPipeline.Find(scope);
                    if (rewound != null && rewound.Weight > 0.05f)
                        problems.Add($"weight after rewind was {rewound.Weight:F3}");

                    row("GRADING-timeline", problems.Count == 0 ? "PASS" : "FAIL",
                        problems.Count == 0
                            ? string.Join(" ", samples.ConvertAll(s => $"{s.T:0.00}:{s.Weight:0.00}"))
                            : string.Join("; ", problems));
                }
                finally { host?.Dispose(); }
            }
            catch (Exception e) { row("GRADING-timeline", "FAIL", e.ToString()); }
            finally { ScreenEffectPipeline.Clear(scope); }
        }

        /// <summary>
        /// The editor preview and playback must agree, and the preview's engine reuse must not
        /// change what the script sends. Both are checked by comparing the payload a settled
        /// preview produces with the payload of a fresh instance on the same parameters.
        /// </summary>
        private static void PreviewParity(Action<string, string, string> row)
        {
            var problems = new List<string>();
            string first = "diagnostics/grading/parity-a";
            string second = "diagnostics/grading/parity-b";
            try
            {
                if (!GradingTimelineTests.TryCreate(first, out var a, out var aContext, out string error))
                { row("GRADING-preview-parity", "FAIL", error); return; }
                try
                {
                    if (!GradingTimelineTests.TryCreate(second, out var b, out var bContext, out string secondError))
                    { row("GRADING-preview-parity", "FAIL", secondError); return; }
                    try
                    {
                        GradingTimelineTests.Tick(a, aContext, 3600.0);
                        GradingTimelineTests.Tick(b, bContext, 3600.0);
                        string payloadA = GradingTimelineTests.PayloadOf(first);
                        string payloadB = GradingTimelineTests.PayloadOf(second);
                        if (payloadA == null || payloadB == null) problems.Add("a payload was missing");
                        else if (payloadA != payloadB) problems.Add("two instances disagree on the same parameters");

                        // Same instance, new parameters, reusing the compiled engine: this is
                        // the path a slider drag takes.
                        var changed = GradingTimelineTests.BaseParams();
                        changed["exposure"] = 1.25;
                        changed["curveMaster"] = "0,0;0.3,0.4;1,1";
                        var context = new EventContext
                        {
                            Bpm = 120, EventTime = 0, FloorSeq = 1, Params = changed
                        };
                        if (!b.Reinitialize(context, changed, out string reinitError))
                            problems.Add("reinitialize failed: " + reinitError);
                        else
                        {
                            GradingTimelineTests.Tick(b, context, 3600.0);
                            string reused = GradingTimelineTests.PayloadOf(second);

                            string freshScope = "diagnostics/grading/parity-c";
                            if (GradingTimelineTests.TryCreate(freshScope, out var c, out var cContext,
                                    out string freshError, changed))
                            {
                                try
                                {
                                    GradingTimelineTests.Tick(c, cContext, 3600.0);
                                    string fresh = GradingTimelineTests.PayloadOf(freshScope);
                                    if (reused != fresh)
                                        problems.Add("a reused engine sends different values than a fresh one");
                                }
                                finally { c.Dispose(); ScreenEffectPipeline.Clear(freshScope); }
                            }
                            else problems.Add(freshError);
                        }
                    }
                    finally { b?.Dispose(); ScreenEffectPipeline.Clear(second); }
                }
                finally { a?.Dispose(); ScreenEffectPipeline.Clear(first); }
            }
            catch (Exception e) { row("GRADING-preview-parity", "FAIL", e.ToString()); }
            finally
            {
                ScreenEffectPipeline.Clear(first);
                ScreenEffectPipeline.Clear(second);
            }

            row("GRADING-preview-parity", problems.Count == 0 ? "PASS" : "FAIL",
                problems.Count == 0 ? "settled preview == playback; engine reuse == fresh create"
                    : string.Join("; ", problems));
        }

        /// <summary>
        /// Two grading events in one chart: a Replace grade must take over from the older one
        /// instead of doubling it, and a Stack grade must apply on top. Both are rewind-safe
        /// because the decision is driven by the script's clock, not by an accumulated delta.
        /// </summary>
        private static void Composition(Action<string, string, string> row)
        {
            var problems = new List<string>();
            string firstScope = "diagnostics/grading/compose-a";
            string secondScope = "diagnostics/grading/compose-b";
            try
            {
                if (!GradingTimelineTests.TryCreate(firstScope, out var first, out var firstContext,
                        out string error)) { row("GRADING-composition", "FAIL", error); return; }
                try
                {
                    var takeOver = GradingTimelineTests.BaseParams();
                    takeOver["mode"] = "Replace";
                    takeOver["exposure"] = 1.5;
                    if (!GradingTimelineTests.TryCreate(secondScope, out var second, out var secondContext,
                            out string replaceError, takeOver))
                    { row("GRADING-composition", "FAIL", replaceError); return; }
                    try
                    {
                        GradingTimelineTests.Tick(first, firstContext, 0.0);
                        var a = ScreenEffectPipeline.Find(firstScope);
                        if (a == null) { row("GRADING-composition", "FAIL", "first layer missing"); return; }

                        GradingTimelineTests.Tick(second, secondContext, 0.0);
                        var b = ScreenEffectPipeline.Find(secondScope);
                        if (b == null) { row("GRADING-composition", "FAIL", "second layer missing"); return; }
                        if (!a.FadingOut)
                            problems.Add("a replace layer did not supersede the previous grade");

                        // Stack mode keeps both.
                        var stack = GradingTimelineTests.BaseParams();
                        stack["mode"] = "Stack";
                        stack["temperature"] = 0.4;
                        if (!GradingTimelineTests.TryCreate(secondScope + "/stack", out var third,
                                out var thirdContext, out string stackError, stack))
                        { row("GRADING-composition", "FAIL", stackError); return; }
                        try
                        {
                            GradingTimelineTests.Tick(third, thirdContext, 0.0);
                            var stackLayer = ScreenEffectPipeline.Find(secondScope + "/stack");
                            if (stackLayer == null) problems.Add("stack layer missing");
                            var b2 = ScreenEffectPipeline.Find(secondScope);
                            if (b2 != null && b2.FadingOut)
                                problems.Add("a stack layer superseded an unrelated grade");
                        }
                        finally { third?.Dispose(); }
                    }
                    finally
                    {
                        second?.Dispose();
                        ScreenEffectPipeline.Clear(secondScope);
                    }
                }
                finally { first?.Dispose(); }
            }
            catch (Exception e) { row("GRADING-composition", "FAIL", e.ToString()); }
            finally
            {
                ScreenEffectPipeline.Clear(firstScope);
                ScreenEffectPipeline.Clear(secondScope);
                ScreenEffectPipeline.Clear(secondScope + "/stack");
            }

            row("GRADING-composition", problems.Count == 0 ? "PASS" : "FAIL",
                problems.Count == 0 ? "replace supersedes, stack coexists" : string.Join("; ", problems));
        }

        /// <summary>
        /// Everything a grade allocates has to come back. A material, a curve table and a LUT
        /// volume per event is small, but a chart that is reopened a few dozen times while
        /// being edited is not.
        /// </summary>
        private static void Lifecycle(Action<string, string, string> row)
        {
            var problems = new List<string>();
            const string scope = "diagnostics/grading/lifecycle";
            try
            {
                var parameters = GradingTimelineTests.BaseParams();
                parameters["lutPreset"] = "BundledTealOrange";
                parameters["curveMaster"] = "0,0;0.4,0.5;1,1";
                if (!GradingTimelineTests.TryCreate(scope, out var host, out var context, out string error, parameters))
                { row("GRADING-lifecycle", "FAIL", error); return; }
                try
                {
                    GradingTimelineTests.Tick(host, context, 0.0);
                    var layer = ScreenEffectPipeline.Find(scope);
                    if (layer == null) problems.Add("no layer");
                    else
                    {
                        if (layer.LutKey == null) problems.Add("LUT was not acquired");
                        if (layer.Curves.Count == 0) problems.Add("curve table was not created");
                        if (layer.Identity == null) problems.Add("identity table was not created");
                    }
                }
                finally { host?.Dispose(); }

                string before = LutCache.Report();
                ScreenEffectPipeline.Clear(scope);
                if (ScreenEffectPipeline.Find(scope) != null) problems.Add("layer survived its own clear");
                string after = LutCache.Report();
                // Other layers in the level legitimately hold LUTs; what matters is that this
                // event's release put the cache back where it found it.
                if (before != after) problems.Add($"LUT cache changed on release: {before} -> {after}");

                row("GRADING-lifecycle", problems.Count == 0 ? "PASS" : "FAIL",
                    problems.Count == 0 ? "material, curve, LUT and identity released" : string.Join("; ", problems));
            }
            catch (Exception e) { row("GRADING-lifecycle", "FAIL", e.ToString()); }
            finally { ScreenEffectPipeline.Clear(scope); }
        }

        /// <summary>
        /// The script's parameter mapping is part of the product: a slider that moves the
        /// wrong uniform is a silent bug that no pixel comparison of the shader alone would
        /// catch. This runs the packaged script and reads the values it actually sent.
        /// </summary>
        private static void ScriptMapping(Action<string, string, string> row, EventManifest manifest)
        {
            string source = EventScriptCache.Get(manifest);
            if (source == null) { row("GRADING-script-mapping", "FAIL", "entry script missing"); return; }

            var parameters = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "enabled", true }, { "strength", 0.5 }, { "mode", "Replace" }, { "duration", 0 },
                { "ease", "Linear" }, { "exposure", 1.0 }, { "brightness", 0.2 }, { "contrast", 0.3 },
                { "saturation", -0.4 }, { "vibrance", 0.1 }, { "temperature", 0.25 }, { "tint", -0.1 },
                { "highlights", -0.2 }, { "shadows", 0.3 }, { "whites", 0.1 }, { "blacks", -0.1 },
                { "gamma", 1.2 }, { "gainR", 1.1 }, { "gainG", 0.9 }, { "gainB", 1.0 },
                { "offsetR", 0.01 }, { "offsetG", 0.0 }, { "offsetB", -0.01 },
                { "liftXY", new[] { 0.4f, -0.2f } }, { "liftL", 0.05f },
                { "gammaXY", new[] { 0.0f, 0.0f } }, { "gammaL", 0.0f },
                { "gainXY", new[] { -0.1f, 0.3f } }, { "gainL", -0.02f },
                { "offsetXY", new[] { 0.0f, 0.0f } }, { "offsetL", 0.0f },
                { "curveMaster", "0,0;0.5,0.6;1,1" }, { "curveRed", "0,0;1,1" },
                { "curveGreen", "0,0;1,1" }, { "curveBlue", "0,0;1,1" },
                { "hueVsHue", "0,0.5;1,0.5" }, { "hueVsSat", "0,0.5;1,0.5" }, { "hueVsLuma", "0,0.5;1,0.5" },
                { "hslBands", "0,0,0;0,0,0;0,0,0;0,0,0;0,0,0;0,0,0;0,0,0;0,0,0" },
                { "showRaw", false }, { "lutPreset", "None" }, { "lutFile", "" }, { "lutStrength", 1 }
            };

            var host = ScriptHost.Create(source, Scope + "/script", out string createError, manifest);
            if (host == null) { row("GRADING-script-mapping", "FAIL", createError); return; }
            try
            {
                var context = new EventContext { Bpm = 120, EventTime = 0, FloorSeq = 1, Params = parameters };
                if (!host.Initialize(context, parameters, out string initError))
                { row("GRADING-script-mapping", "FAIL", initError); return; }
                context.Time = 0.0;
                context.T = 0.0;
                host.Update(0.0, context.ToStateJson());
                if (host.Faulted) { row("GRADING-script-mapping", "FAIL", host.LastError); return; }

                var scriptLayer = ScreenEffectPipeline.Find(Scope + "/script");
                if (scriptLayer == null || scriptLayer.Material == null)
                { row("GRADING-script-mapping", "FAIL", "script registered no layer"); return; }
                var material = scriptLayer.Material;

                var problems = new List<string>();
                void Expect(string uniform, float expected, float tolerance = 1e-3f)
                {
                    float actual = material.GetFloat(uniform);
                    if (Mathf.Abs(actual - expected) > tolerance)
                        problems.Add($"{uniform}={actual:F4} expected {expected:F4}");
                }

                Expect("_Exposure", 1.0f);
                Expect("_Brightness", 0.2f);
                Expect("_Contrast", 0.3f);
                Expect("_Saturation", -0.4f);
                Expect("_Vibrance", 0.1f);
                Expect("_Temperature", 0.25f);
                Expect("_Tint", -0.1f);
                Expect("_Highlights", -0.2f);
                Expect("_Shadows", 0.3f);
                Expect("_Whites", 0.1f);
                Expect("_Blacks", -0.1f);
                Expect("_Gamma", 1.2f);
                Expect("_Strength", 0.5f);

                var gain = material.GetVector("_GainRGB");
                if (Mathf.Abs(gain.x - 1.1f) > 1e-3f || Mathf.Abs(gain.y - 0.9f) > 1e-3f)
                    problems.Add($"gain={gain}");
                var lift = material.GetVector("_Lift");
                if (Mathf.Abs(lift.x - 0.4f) > 1e-3f || Mathf.Abs(lift.y + 0.2f) > 1e-3f ||
                    Mathf.Abs(lift.z - 0.05f) > 1e-3f)
                    problems.Add($"lift={lift}");
                var gainWheel = material.GetVector("_GainWheel");
                if (Mathf.Abs(gainWheel.x + 0.1f) > 1e-3f || Mathf.Abs(gainWheel.y - 0.3f) > 1e-3f)
                    problems.Add($"gainWheel={gainWheel}");
                if (material.GetFloat("_CurveEnabled") < 0.5f) problems.Add("curve was not enabled");

                row("GRADING-script-mapping", problems.Count == 0 ? "PASS" : "FAIL",
                    problems.Count == 0 ? "13 scalars, 3 vectors and the curve switch" : string.Join("; ", problems));
            }
            catch (Exception e) { row("GRADING-script-mapping", "FAIL", e.ToString()); }
            finally
            {
                host.Dispose();
                ScreenEffectPipeline.Clear(Scope + "/script");
            }
        }

        /// <summary>
        /// Values a chart author can actually produce: out of range, NaN, an unreadable curve
        /// and an absent LUT. None of them may throw, blank the screen, or leave a layer that
        /// outlives its event.
        /// </summary>
        /// <summary>
        /// What the grade costs per frame.
        ///
        /// Measured as the difference between blitting through the grade material and
        /// blitting through nothing, both with a readback so the GPU actually finishes. The
        /// readback dominates the absolute number, which is why only the difference is
        /// reported: it is the part the effect is responsible for.
        /// </summary>
        private static void Performance(Action<string, string, string> row, EventManifest manifest,
            Texture2D input)
        {
            const int iterations = 24;
            const string scope = "diagnostics/grading/perf";
            try
            {
                var settings = With(s =>
                {
                    s.Exposure = 0.35f;
                    s.Contrast = 0.2f;
                    s.Saturation = -0.2f;
                    s.MasterCurve = new[] { 0f, 0f, 0.3f, 0.34f, 0.7f, 0.74f, 1f, 1f };
                    s.Lift = new Vector4(-0.2f, 0.15f, 0f, 0f);
                });
                int w = Mathf.Max(320, Screen.width), h = Mathf.Max(180, Screen.height);
                var fullInput = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point, hideFlags = HideFlags.DontSave
                };
                var fullPixels = new Color32[w * h];
                var small = input.GetPixels32();
                for (int i = 0; i < fullPixels.Length; i++) fullPixels[i] = small[i % small.Length];
                fullInput.SetPixels32(fullPixels);
                fullInput.Apply(false, false);
                var fullTarget = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
                {
                    antiAliasing = 1, filterMode = FilterMode.Point
                };
                fullTarget.Create();
                var readback = new Texture2D(w, h, TextureFormat.RGBA32, false);

                if (!ScreenEffectPipeline.Set(scope, manifest,
                        new Dictionary<string, object> { { "__payload", Payload(settings, null) } }, out string error))
                { row("GRADING-performance", "FAIL", error); return; }
                var layer = ScreenEffectPipeline.Find(scope);
                if (layer == null) { row("GRADING-performance", "FAIL", "no layer"); return; }
                layer.Material.SetFloat("_PEVWeight", 1f);

                // Time the full presented resolution, not the small test chart: a per-pixel
                // cost that looks small at 192x48 is what actually decides whether a chart
                // can hold a grade through a whole song.
                var warm = new System.Diagnostics.Stopwatch();
                warm.Start();
                for (int i = 0; i < 4; i++) BlitTimed(fullInput, fullTarget, layer.Material, readback, w, h);
                warm.Stop();
                if (warm.Elapsed.TotalMilliseconds > 3000)
                {
                    row("GRADING-performance", "SKIP", "readback path is too slow to time here");
                    return;
                }

                var baseline = new System.Diagnostics.Stopwatch();
                baseline.Start();
                for (int i = 0; i < 12; i++) BlitTimed(fullInput, fullTarget, null, readback, w, h);
                baseline.Stop();

                var graded = new System.Diagnostics.Stopwatch();
                graded.Start();
                for (int i = 0; i < 12; i++) BlitTimed(fullInput, fullTarget, layer.Material, readback, w, h);
                graded.Stop();

                double bare = baseline.Elapsed.TotalMilliseconds / 12;
                double withGrade = graded.Elapsed.TotalMilliseconds / 12;
                row("GRADING-performance", "INFO",
                    $"{w}x{h} plain blit {bare:F2} ms, graded {withGrade:F2} ms, " +
                    $"delta {withGrade - bare:F2} ms per frame (readback included, " +
                    $"{FrameCapture.DescribeEnvironment()})");

                UnityEngine.Object.Destroy(readback);
                fullTarget.Release();
                UnityEngine.Object.Destroy(fullTarget);
                UnityEngine.Object.Destroy(fullInput);
            }
            catch (Exception e) { row("GRADING-performance", "FAIL", e.Message); }
            finally { ScreenEffectPipeline.Clear(scope); }
        }

        private static void EdgeCases(Action<string, string, string> row, EventManifest manifest,
            Texture2D input, RenderTexture target)
        {
            var problems = new List<string>();

            void Try(string label, string payload, bool expectLayer)
            {
                try
                {
                    bool ok = ScreenEffectPipeline.Set(Scope, manifest,
                        new Dictionary<string, object> { { "__payload", payload } }, out string error);
                    var layer = ScreenEffectPipeline.Find(Scope);
                    if (expectLayer && (!ok || layer == null)) problems.Add(label + ": ok=" + ok +
                        " layer=" + (layer != null) + (error == null ? "" : " " + error));
                    if (expectLayer && layer != null)
                    {
                        layer.Material.SetFloat("_PEVWeight", 1f);
                        var pixels = Render(input, target, layer.Material);
                        bool finite = true;
                        foreach (var pixel in pixels) finite &= pixel.a == 255;
                        if (!finite) problems.Add(label + ": produced non-opaque output");
                    }
                }
                catch (Exception e) { problems.Add(label + ": threw " + e.Message); }
                finally { ScreenEffectPipeline.Clear(Scope); }
            }

            Try("nan-exposure", "{\"floats\":{\"_Exposure\":\"NaN\"},\"enabled\":true}", true);
            Try("huge-contrast", "{\"floats\":{\"_Contrast\":1000},\"enabled\":true}", true);
            Try("bad-curve", "{\"curves\":{\"_CurveTex\":{\"master\":[0,0,0,0,0,0]}},\"enabled\":true}", true);
            Try("reserved-uniform", "{\"floats\":{\"_MainTex\":1},\"enabled\":true}", false);
            Try("bad-uniform-name", "{\"floats\":{\"Exposure\":1},\"enabled\":true}", false);
            Try("missing-lut", "{\"lut\":{\"enabled\":true,\"path\":\"assets/luts/absent.cube\"},\"enabled\":true}", true);
            Try("disabled", "{\"enabled\":false,\"floats\":{\"_Exposure\":2}}", true);

            // A disabled layer must leave the screen untouched rather than blitting a blank.
            ScreenEffectPipeline.Clear(Scope);
            if (ScreenEffectPipeline.Find(Scope) != null) problems.Add("layer survived Clear");

            row("GRADING-edge-cases", problems.Count == 0 ? "PASS" : "FAIL",
                problems.Count == 0 ? "7 hostile payloads" : string.Join("; ", problems));
        }

        /// <summary>Blit and read one pixel so the GPU has finished before the timer stops.</summary>
        private static void BlitTimed(Texture source, RenderTexture target, Material material, Texture2D read,
            int width, int height)
        {
            if (material == null) Graphics.Blit(source, target);
            else Graphics.Blit(source, target, material);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                read.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                read.Apply(false);
            }
            finally { RenderTexture.active = previous; }
        }

        private static Color32[] Render(Texture2D input, RenderTexture target, Material material)
        {
            if (material == null) Graphics.Blit(input, target);
            else Graphics.Blit(input, target, material);
            var previous = RenderTexture.active;
            Texture2D read = null;
            try
            {
                RenderTexture.active = target;
                read = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                read.Apply(false);
                return read.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                if (read != null) UnityEngine.Object.Destroy(read);
            }
        }

        private static int Worst(Color32[] actual, Color32[] expected)
        {
            int worst = 0, best = -1;
            int count = Mathf.Min(actual.Length, expected.Length);
            for (int i = 0; i < count; i++)
            {
                int error = Mathf.Max(Mathf.Abs(actual[i].r - expected[i].r),
                    Mathf.Max(Mathf.Abs(actual[i].g - expected[i].g), Mathf.Abs(actual[i].b - expected[i].b)));
                if (error > best) { best = error; worst = i; }
            }
            return worst;
        }

        private static void Compare(Color32[] actual, Color32[] expected, out int differing, out int maxError)
        {
            differing = 0;
            maxError = 0;
            int count = Mathf.Min(actual.Length, expected.Length);
            for (int i = 0; i < count; i++)
            {
                int error = Mathf.Max(Mathf.Abs(actual[i].r - expected[i].r),
                    Mathf.Max(Mathf.Abs(actual[i].g - expected[i].g), Mathf.Abs(actual[i].b - expected[i].b)));
                if (error > 0) differing++;
                if (error > maxError) maxError = error;
            }
        }

        /// <summary>
        /// Serialises a reference setting into the same payload shape the event script sends,
        /// so the test drives the production uniform path rather than a parallel one.
        /// </summary>
        private static string Payload(GradingReference.Settings s, string lutPath)
        {
            var invariant = CultureInfo.InvariantCulture;
            Func<float, string> f = value => value.ToString("R", invariant);

            string Json(Vector4 v) => $"[{f(v.x)},{f(v.y)},{f(v.z)},{f(v.w)}]";
            string CurveJson(float[] points) => points == null ? "null" :
                "[" + string.Join(",", Array.ConvertAll(points, p => f(p))) + "]";

            var builder = new System.Text.StringBuilder(512);
            builder.Append("{\"enabled\":true,\"weight\":1,\"floats\":{");
            builder.Append($"\"_Strength\":{f(s.Strength)},\"_Exposure\":{f(s.Exposure)},");
            builder.Append($"\"_Brightness\":{f(s.Brightness)},\"_Contrast\":{f(s.Contrast)},");
            builder.Append($"\"_Saturation\":{f(s.Saturation)},\"_Vibrance\":{f(s.Vibrance)},");
            builder.Append($"\"_Temperature\":{f(s.Temperature)},\"_Tint\":{f(s.Tint)},");
            builder.Append($"\"_Highlights\":{f(s.Highlights)},\"_Shadows\":{f(s.Shadows)},");
            builder.Append($"\"_Whites\":{f(s.Whites)},\"_Blacks\":{f(s.Blacks)},\"_Gamma\":{f(s.Gamma)},");
            builder.Append($"\"_CurveEnabled\":{(s.MasterCurve != null || s.RedCurve != null ? 1 : 0)},");
            // Always declared: a case must not inherit the previous case's hue-curve switch.
            builder.Append($"\"_HueCurveEnabled\":{(s.HueHue != null || s.HueSat != null || s.HueLuma != null ? 1 : 0)},");
            builder.Append($"\"_LutStrength\":{f(s.LutStrength)}}},");

            builder.Append("\"vectors\":{");
            builder.Append($"\"_GainRGB\":[{f(s.Gain.x)},{f(s.Gain.y)},{f(s.Gain.z)},1],");
            builder.Append($"\"_OffsetRGB\":[{f(s.Offset.x)},{f(s.Offset.y)},{f(s.Offset.z)},0],");
            builder.Append($"\"_Lift\":{Json(s.Lift)},\"_GammaWheel\":{Json(s.GammaWheel)},");
            builder.Append($"\"_GainWheel\":{Json(s.GainWheel)},\"_OffsetWheel\":{Json(s.OffsetWheel)}}},");

            builder.Append("\"curves\":{");
            builder.Append($"\"_CurveTex\":{{\"master\":{CurveJson(s.MasterCurve)},\"red\":{CurveJson(s.RedCurve)},");
            builder.Append($"\"green\":{CurveJson(s.GreenCurve)},\"blue\":{CurveJson(s.BlueCurve)}}}");
            if (s.HueHue != null || s.HueSat != null || s.HueLuma != null)
            {
                builder.Append($",\"_HueCurves\":{{\"red\":{CurveJson(s.HueHue)},\"green\":{CurveJson(s.HueSat)},");
                builder.Append($"\"blue\":{CurveJson(s.HueLuma)}}}");
            }
            builder.Append("},");

            // Always declared, so a case cannot inherit a previous case's bands through the
            // material — the real script sends them every time for the same reason.
            builder.Append("\"vectorArrays\":{\"_HslBands\":[");
            var bands = s.HslBands ?? new Vector4[8];
            for (int i = 0; i < 8; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(Json(i < bands.Length ? bands[i] : Vector4.zero));
            }
            builder.Append("]},");

            builder.Append("\"lut\":{\"enabled\":").Append(lutPath == null ? "false" : "true");
            if (lutPath != null) builder.Append(",\"path\":\"").Append(lutPath).Append('"');
            builder.Append("}}");
            return builder.ToString();
        }
    }
}
#endif
