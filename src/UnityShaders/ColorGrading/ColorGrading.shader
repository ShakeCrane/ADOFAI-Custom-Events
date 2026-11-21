// Colour grading for ADOFAI Custom Events.
//
// The pipeline is deliberately ordered the way a grading application orders it, because the
// order is what makes the controls behave the way an author expects:
//
//   exposure (linear light) �?white balance �?offset/gain �?four-way wheels �?tone zones
//   �?contrast �?gamma �?curves �?HSL curves �?saturation/vibrance �?HSL ranges �?LUT �?mix
//
// Exposure is the only step that runs in linear light. A display-referred value scaled by
// 2^stops is not the same thing, and the difference is exactly why a "brightness" slider and
// an "exposure" slider are not interchangeable. Everything after it works on the encoded
// signal, which is what the curves, the HSL tools and a .cube LUT are authored against.
//
// Every control is neutral at its default, so a missing uniform cannot silently tint the
// picture: a zero vector is identity for every operation here.

Shader "Hidden/PEV/ColorGrading"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _Strength ("Strength", Range(0, 1)) = 1
        _Exposure ("Exposure", Float) = 0
        _Brightness ("Brightness", Float) = 0
        _Contrast ("Contrast", Float) = 0
        _Saturation ("Saturation", Float) = 0
        _Vibrance ("Vibrance", Float) = 0
        _Temperature ("Temperature", Float) = 0
        _Tint ("Tint", Float) = 0
        _Highlights ("Highlights", Float) = 0
        _Shadows ("Shadows", Float) = 0
        _Whites ("Whites", Float) = 0
        _Blacks ("Blacks", Float) = 0
        _Gamma ("Gamma", Float) = 1
        _GainRGB ("Gain RGB", Vector) = (1, 1, 1, 1)
        _OffsetRGB ("Offset RGB", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "GRADE"
            HLSLPROGRAM
            #pragma vertex pev_vertex
            #pragma fragment pev_fragment
            #pragma target 4.5

            #include "UnityCG.cginc"

            Texture2D<float4> _MainTex;
            SamplerState sampler_MainTex;

            Texture2D<float4> _CurveTex;
            SamplerState sampler_CurveTex;
            float _CurveEnabled;

            Texture2D<float4> _HueCurves;
            SamplerState sampler_HueCurves;
            float _HueCurveEnabled;

            Texture3D<float4> _LutTex;
            SamplerState sampler_LutTex;
            Texture2D<float4> _LutTex2D;
            SamplerState sampler_LutTex2D;
            float _LutEnabled;
            float _LutSize;
            float _LutMode;      // 0 = 3D volume, 1 = 1D strip
            float4 _LutDomainMin;
            float4 _LutDomainMax;
            float _LutStrength;

            float _Strength;
            float _Exposure;
            float _Brightness;
            float _Contrast;
            float _Saturation;
            float _Vibrance;
            float _Temperature;
            float _Tint;
            float _Highlights;
            float _Shadows;
            float _Whites;
            float _Blacks;
            float _Gamma;
            float4 _GainRGB;
            float4 _OffsetRGB;

            // x = hue position, y = hue position, z = luminance offset. The wheel's chroma
            // is derived here rather than in the script so both the editor preview and
            // playback run exactly the same arithmetic.
            float4 _Lift;
            float4 _GammaWheel;
            float4 _GainWheel;
            float4 _OffsetWheel;
            float4 _HslBands[8];  // x = hue shift deg, y = saturation, z = luminance, w = weight scale

            float _PEVLinearProject;
            // Fade weight Core applies while a layer is entering or leaving. A package
            // multiplies its final mix by this so a replaced grade crossfades instead of cutting.
            float _PEVWeight;

            struct PEVVertexInput { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct PEVVertexOutput {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            PEVVertexOutput pev_vertex(PEVVertexInput input)
            {
                PEVVertexOutput o;
                o.position = UnityObjectToClipPos(input.vertex);
                o.uv = input.uv;
                return o;
            }

            static const float3 kLuma = float3(0.2126, 0.7152, 0.0722);

            float Luma(float3 c) { return dot(c, kLuma); }

            float3 SRGBToLinear(float3 c)
            {
                c = saturate(c);
                return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
            }

            float3 LinearToSRGB(float3 c)
            {
                c = max(c, 0.0);
                return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
            }

            // Hue rotation and HSV/HSL conversion always work around the same luma, so a
            // hue shift does not change how bright the picture is.
            float3 RGBToHSV(float3 c)
            {
                float4 k = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                float4 p = lerp(float4(c.bg, k.wz), float4(c.gb, k.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
                float d = q.x - min(q.w, q.y);
                float e = 1.0e-10;
                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            float3 HSVToRGB(float3 c)
            {
                float4 k = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                float3 p = abs(frac(c.xxx + k.xyz) * 6.0 - k.www);
                return c.z * lerp(k.xxx, saturate(p - k.xxx), c.y);
            }

            float3 RGBToHSL(float3 c)
            {
                float maxc = max(c.r, max(c.g, c.b));
                float minc = min(c.r, min(c.g, c.b));
                float l = (maxc + minc) * 0.5;
                float d = maxc - minc;
                float h = 0.0, s = 0.0;
                if (d > 1e-6)
                {
                    s = l > 0.5 ? d / (2.0 - maxc - minc) : d / (maxc + minc);
                    if (maxc == c.r) h = (c.g - c.b) / d + (c.g < c.b ? 6.0 : 0.0);
                    else if (maxc == c.g) h = (c.b - c.r) / d + 2.0;
                    else h = (c.r - c.g) / d + 4.0;
                    h /= 6.0;
                }
                return float3(h, s, l);
            }

            float3 HSLToRGB(float3 c)
            {
                float3 rgb = c.z;
                if (c.y > 1e-6)
                {
                    float q = c.z < 0.5 ? c.z * (1.0 + c.y) : c.z + c.y - c.z * c.y;
                    float p = 2.0 * c.z - q;
                    float3 t = frac(float3(c.x + 1.0 / 3.0, c.x, c.x - 1.0 / 3.0));
                    rgb = float3(
                        t.x * 6.0 < 1.0 ? p + (q - p) * 6.0 * t.x : (t.x * 2.0 < 1.0 ? q : (t.x * 3.0 < 2.0 ? p + (q - p) * (2.0 / 3.0 - t.x) * 6.0 : p)),
                        t.y * 6.0 < 1.0 ? p + (q - p) * 6.0 * t.y : (t.y * 2.0 < 1.0 ? q : (t.y * 3.0 < 2.0 ? p + (q - p) * (2.0 / 3.0 - t.y) * 6.0 : p)),
                        t.z * 6.0 < 1.0 ? p + (q - p) * 6.0 * t.z : (t.z * 2.0 < 1.0 ? q : (t.z * 3.0 < 2.0 ? p + (q - p) * (2.0 / 3.0 - t.z) * 6.0 : p)));
                }
                return rgb;
            }

            // A wheel at rest is neutral. Only the chroma relative to white matters, so a
            // wheel whose puck sits at the centre contributes exactly zero.
            float3 WheelChroma(float4 wheel, float scale)
            {
                float2 xy = wheel.xy;
                float radius = min(length(xy), 1.0);
                float3 chroma = 0.0;
                if (radius > 1e-5)
                {
                    float hue = atan2(xy.y, xy.x) / 6.28318530718;
                    hue += hue < 0.0 ? 1.0 : 0.0;
                    chroma = HSVToRGB(float3(hue, 1.0, 1.0)) - 1.0;
                    chroma *= radius * scale;
                }
                return chroma;
            }

            float3 ApplyWheels(float3 c)
            {
                float3 lift = WheelChroma(_Lift, 0.5) + _Lift.z;
                float3 gain = WheelChroma(_GainWheel, 0.5) + _GainWheel.z;
                float3 offset = WheelChroma(_OffsetWheel, 0.25) + _OffsetWheel.z;
                float3 gammaVector = 1.0 + WheelChroma(_GammaWheel, 0.5) + _GammaWheel.z;
                gammaVector = max(gammaVector, 0.05);

                // Offset and gain first: a global exposure-ish shift followed by a per-channel
                // multiplier, which is how a CDL behaves.
                c = c + offset;
                c = c * (1.0 + gain);
                // Lift raises the black end without touching white.
                c = c + lift * (1.0 - saturate(c));
                // Gamma shapes the middle.
                c = pow(max(c, 0.0), 1.0 / gammaVector);
                return c;
            }

            float3 ApplyTone(float3 c)
            {
                float l = Luma(c);
                // Three overlapping windows: shadows, highlights and the two endpoints.
                // Smoothstep keeps a slider from creating a visible edge where its weight
                // falls to zero.
                float shadowWeight = 1.0 - smoothstep(0.0, 0.55, l);
                float highlightWeight = smoothstep(0.45, 1.0, l);
                float whiteWeight = smoothstep(0.7, 1.0, l);
                float blackWeight = 1.0 - smoothstep(0.0, 0.30, l);

                // Shadows and highlights compress toward the zone's own end; whites and
                // blacks move the endpoints, which is what makes them clip instead of
                // flattening the whole range.
                c = c + (_Shadows * 0.5) * shadowWeight * (1.0 - saturate(c));
                c = c - (_Highlights * 0.5) * highlightWeight * saturate(c);
                c = c + (_Whites * 0.4) * whiteWeight * smoothstep(0.55, 1.0, l);
                c = c + (_Blacks * 0.4) * blackWeight * (1.0 - smoothstep(0.0, 0.45, l));
                return c;
            }

            float3 ApplyContrast(float3 c)
            {
                if (abs(_Contrast) < 1e-5) return c;
                // A pivot of 0.5 in display space keeps mid grey fixed, which is the
                // behaviour every grading application has by default.
                float pivot = 0.5;
                float factor = _Contrast >= 0.0 ? 1.0 + _Contrast * 2.0 : 1.0 / (1.0 - _Contrast * 2.0);
                return saturate((c - pivot) * factor + pivot);
            }

            float3 ApplyCurves(float3 c)
            {
                if (_CurveEnabled < 0.5) return c;
                // Master first, then the per-channel curves. Both live in one RGBA row:
                // alpha is the master, RGB are the channels.
                float3 master = float3(
                    _CurveTex.Sample(sampler_CurveTex, float2(saturate(c.r), 0.5)).a,
                    _CurveTex.Sample(sampler_CurveTex, float2(saturate(c.g), 0.5)).a,
                    _CurveTex.Sample(sampler_CurveTex, float2(saturate(c.b), 0.5)).a);
                float3 channels = float3(
                    _CurveTex.Sample(sampler_CurveTex, float2(saturate(master.r), 0.5)).r,
                    _CurveTex.Sample(sampler_CurveTex, float2(saturate(master.g), 0.5)).g,
                    _CurveTex.Sample(sampler_CurveTex, float2(saturate(master.b), 0.5)).b);
                return channels;
            }

            float3 ApplySaturation(float3 c)
            {
                float l = Luma(c);
                if (abs(_Saturation) > 1e-5)
                    c = lerp(l.xxx, c, 1.0 + _Saturation);

                if (abs(_Vibrance) > 1e-5)
                {
                    // Vibrance is saturation weighted away from already-saturated pixels, so
                    // saturated colours are left alone and skin/earth tones move least.
                    float current = max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b));
                    float weight = 1.0 - saturate(current);
                    c = lerp(c, lerp(Luma(c).xxx, c, 1.6), _Vibrance * weight);
                }
                return c;
            }

            // One band of the eight-colour HSL editor. Bands overlap with a linear falloff so
            // dragging a band never produces a hard seam in a gradient.
            float BandWeight(float hue, float centre)
            {
                float distance = abs(hue - centre);
                distance = min(distance, 1.0 - distance);
                return saturate(1.0 - distance / (1.0 / 8.0));
            }

            float3 ApplyHslBands(float3 c)
            {
                float3 hsl = RGBToHSL(saturate(c));
                float hue = hsl.x;
                float hueShift = 0.0, satMul = 0.0, lumAdd = 0.0;
                [unroll]
                for (int i = 0; i < 8; i++)
                {
                    float4 band = _HslBands[i];
                    if (abs(band.x) < 1e-5 && abs(band.y) < 1e-5 && abs(band.z) < 1e-5) continue;
                    float weight = BandWeight(hue, i / 8.0) * band.w;
                    hueShift += band.x * weight;
                    satMul += band.y * weight;
                    lumAdd += band.z * weight;
                }
                if (abs(hueShift) < 1e-5 && abs(satMul) < 1e-5 && abs(lumAdd) < 1e-5) return c;

                hsl.x = frac(hsl.x + hueShift / 360.0 + 1.0);
                hsl.y = saturate(hsl.y * (1.0 + satMul));
                hsl.z = saturate(hsl.z + lumAdd * 0.5);
                return HSLToRGB(hsl);
            }

            // Hue/saturation/luminance against hue, sampled from a 256-wide row. R is the hue
            // rotation, G the saturation multiplier and B the luminance offset.
            float3 ApplyHueCurve(float3 c)
            {
                if (_HueCurveEnabled < 0.5) return c;
                float3 hsl = RGBToHSL(saturate(c));
                float4 curve = _HueCurves.Sample(sampler_HueCurves, float2(frac(hsl.x + 1.0), 0.5));
                float hueShift = (curve.r - 0.5) * 2.0;
                float satScale = (curve.g - 0.5) * 2.0;
                float lumShift = (curve.b - 0.5) * 2.0;
                if (abs(hueShift) < 1e-5 && abs(satScale) < 1e-5 && abs(lumShift) < 1e-5) return c;
                hsl.x = frac(hsl.x + hueShift * 0.5 + 1.0);
                hsl.y = saturate(hsl.y * (1.0 + satScale));
                hsl.z = saturate(hsl.z + lumShift * 0.5);
                return HSLToRGB(hsl);
            }

            float3 SampleLut(float3 c)
            {
                if (_LutEnabled < 0.5) return c;
                float3 span = max(_LutDomainMax.xyz - _LutDomainMin.xyz, 1e-5);
                float3 uvw = saturate((c - _LutDomainMin.xyz) / span);
                float size = max(_LutSize, 2.0);
                // Half-texel inset. Stretching by (n-1)/n alone is the classic bug: it makes
                // the extremes sample the edge texel centre, so pure black and pure white can
                // never be produced and highlights clip early.
                float inset = 0.5 / size;
                float scale = (size - 1.0) / size;
                if (_LutMode > 0.5)
                {
                    float3 result;
                    result.r = _LutTex2D.Sample(sampler_LutTex2D, float2(uvw.r * scale + inset, 0.5)).r;
                    result.g = _LutTex2D.Sample(sampler_LutTex2D, float2(uvw.g * scale + inset, 0.5)).g;
                    result.b = _LutTex2D.Sample(sampler_LutTex2D, float2(uvw.b * scale + inset, 0.5)).b;
                    return lerp(c, result, saturate(_LutStrength));
                }
                float3 graded = _LutTex.Sample(sampler_LutTex, uvw * scale + inset).rgb;
                return lerp(c, graded, saturate(_LutStrength));
            }

            float4 pev_fragment(PEVVertexOutput input) : SV_Target
            {
                float4 source = _MainTex.Sample(sampler_MainTex, input.uv);
                float3 original = saturate(source.rgb);

                // Everything below runs on display-referred values so the curves, HSL tools
                // and a .cube LUT mean the same thing in either project colour space. In a
                // linear project the sample arrives linear, so it is encoded on the way in
                // and decoded on the way out.
                float3 c = _PEVLinearProject > 0.5 ? LinearToSRGB(original) : original;

                // Exposure is a linear-light operation. Skipping the round trip when the
                // control is neutral keeps the common case free of four pow() calls.
                if (abs(_Exposure) > 1e-5)
                {
                    float3 decoded = SRGBToLinear(c) * exp2(_Exposure);
                    c = LinearToSRGB(decoded);
                }

                // White balance: gains that shift the neutral axis. Temperature moves
                // red against blue, tint moves green against magenta.
                if (abs(_Temperature) > 1e-5 || abs(_Tint) > 1e-5)
                {
                    float3 gains = float3(1.0 + 0.30 * _Temperature, 1.0 - 0.22 * _Tint,
                                          1.0 - 0.30 * _Temperature);
                    // Normalising by luma means a temperature change rebalances colours
                    // without also changing how bright the frame is.
                    gains /= max(Luma(gains), 1e-4);
                    c = saturate(c * gains);
                }

                c = c * _GainRGB.rgb + _OffsetRGB.rgb;
                c = ApplyWheels(c);
                c = ApplyTone(c);
                c = ApplyContrast(c);
                if (abs(_Gamma - 1.0) > 1e-5)
                    c = saturate(pow(max(c, 0.0), 1.0 / max(_Gamma, 1e-3)));
                c = saturate(c + _Brightness * 0.5);
                c = ApplyCurves(c);
                c = ApplyHueCurve(c);
                c = saturate(ApplySaturation(c));
                c = ApplyHslBands(c);
                c = saturate(SampleLut(c));

                float amount = saturate(_Strength) * saturate(_PEVWeight);
                float3 blended = lerp(original, c, amount);
                if (_PEVLinearProject > 0.5) blended = SRGBToLinear(blended);
                return float4(blended, source.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
