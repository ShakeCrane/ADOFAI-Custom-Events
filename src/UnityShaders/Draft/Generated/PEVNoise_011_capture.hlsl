float2 PEVGrabUV(float2 uv) {
#if UNITY_UV_STARTS_AT_TOP
    return float2(uv.x, 1.0 - uv.y);
#else
    return uv;
#endif
}
// Draft translation; not yet validated in Unity or D3D11.
// Phira-Pro c8fb11c6a41faaf1233a5affabf87b0223e2edd8,
// prpr/src/core/block_shader_full.frag, GPL-3.0-or-later.
// Generated with tools/translate_phira_shader.py; do not edit by hand.
Texture2D<float4> uDisabledColors;
SamplerState sampleruDisabledColors;
uniform float4 uUnityTime;
uniform float uDisabledSpeed;
uniform float4 _DisplaceDirection;
Texture2D<float4> uDisplaceTex;
SamplerState sampleruDisplaceTex;
uniform float uDisabledSparkIntensity;
Texture2D<float4> uSparkTex;
SamplerState sampleruSparkTex;
uniform float3 uDisabledSparkTint;
uniform float4 uDisabledFillColor;
uniform float uDisabledFillOpacity;
uniform float uDisabledSparkOpacity;
Texture2D<float4> uActiveColors;
SamplerState sampleruActiveColors;
uniform float4 _EffectRT_TexelSize;
uniform float _EdgeOpacity;
uniform float _GlowIntensity;
uniform float _DisplaceSpeed;
uniform float _BackgroundPixelScale;
uniform float4 _PEVScreenParams;
uniform float _DisplaceStrength;
uniform float _SparkDisplaceIntensity;
uniform float3 _SparkTint;
uniform int _TouchPosCount;
uniform float2 _TouchPos[10];
uniform float _TouchPosSDFSmoothness;
uniform float _TouchPosRadius;
uniform float _TouchPosSDFFalloff;
uniform float _SparkMapOpacity;
uniform float _SparkHueShiftAmount;
uniform float _DisplaceBlendIntensity;
uniform float4 _FillColor;
uniform float _FillStrength;
uniform float4 _GlowColor;
uniform float4 _EdgeColor;
uniform float _TouchPosShine;
uniform float _FillOpacity;
uniform float _ShineBrightness;
uniform float4 _ShineColor;
uniform float _ShineSpeed;
uniform float4 _TouchDisplaceDirection;
uniform float _TouchDisplaceSpeed;
uniform float _TouchBackgroundPixelScale;
uniform float _TouchDisplaceStrength;
uniform float _NoiseDirChangeSpeed;
uniform float _NoiseEvoSpeed;
Texture2D<float4> uNoiseTex;
SamplerState sampleruNoiseTex;
uniform float _NoiseRadius;
uniform float _NoiseSmoothness;
uniform float _SDFCellSize;
uniform float _NoiseDisplaceStrength;
uniform float _SDFMoveSpeed;
uniform float _SDFFalloff;
uniform float _SDFSmoothness;
uniform float4 _NoiseTint;
uniform float4 _TouchGlowColor;
uniform float uReadyExtra;
Texture2D<float4> uMasks;
SamplerState sampleruMasks;
Texture2D<float4> uAuxMasks;
SamplerState sampleruAuxMasks;
Texture2D<float4> _GrabTexture;
SamplerState sampler_GrabTexture;

static float2 fieldUV;
static float2 disabledDisplaceUV;
static float2 sparkUV;
static float clipHalfWidth;
static float2 sceneUV;
static float2 displaceUV;
static float4 screenPos;
static float2 touchDisplaceUV;
static float2 noiseUV;
static float4 pev_output;
static float4 color;

struct SPIRV_Cross_Input
{
    float4 pev_position : SV_POSITION;
    float4 color : TEXCOORD0;
    float2 fieldUV : TEXCOORD1;
    float2 sceneUV : TEXCOORD2;
    float2 displaceUV : TEXCOORD3;
    float2 sparkUV : TEXCOORD4;
    float2 disabledDisplaceUV : TEXCOORD5;
    float2 touchDisplaceUV : TEXCOORD6;
    float2 noiseUV : TEXCOORD7;
    float4 screenPos : TEXCOORD8;
    float clipHalfWidth : TEXCOORD9;
};

struct SPIRV_Cross_Output
{
    float4 pev_output : SV_Target0;
};

static float4 u_xlat0;
static bool3 u_xlatb0;
static float u_xlat16_0;
static float2 u_xlat16;
static float u_xlat16_16;
static float u_xlat16_32;
static float u_xlat16_48;
static float2 u_xlat16_1;
static float u_xlat16_17;
static float4 u_xlat16_2;
static float u_xlat16_18;
static float u_xlat48;
static float u_xlat16_34;
static float u_xlat16_50;
static bool u_xlatb1;
static float4 u_xlat16_3;
static float2 u_xlat33;
static float4 u_xlat1;
static float2 u_xlat32;
static float4 u_xlat4;
static float u_xlat16_35;
static float u_xlat16_51;
static float4 u_xlat16_5;
static float u_xlat16_33;
static float3 u_xlat6;
static bool u_xlatb16;
static int u_xlati1;
static bool u_xlatb49;
static float2 u_xlat7;
static float u_xlat49;
static float u_xlat52;
static bool u_xlatb32;
static float3 u_xlat16_21;
static float4 u_xlat16_8;
static float4 u_xlat16_4;
static float4 u_xlat16_7;
static float u_xlat16_37;
static float3 u_xlat16_9;
static float3 u_xlat16_10;
static bool3 u_xlatb6;
static float3 u_xlat17;
static float2 u_xlat22;
static float3 u_xlat11;
static float4 u_xlat5;
static float2 u_xlat38;
static float3 u_xlat12;
static float u_xlat16_56;
static float3 u_xlat16_13;
static float3 u_xlat16_14;
static float3 u_xlat16_15;
static int u_xlati17;

float2 basePixelUV(float2 uv)
{
    float2 size = _EffectRT_TexelSize.zw;
    return (floor(uv * size) + 0.5f.xx) / size;
}

float3 blockPalette(float3 nativeColor, float3 rgb)
{
    bool _2504;
    if (!false)
    {
        float3 _2499 = abs(rgb - float3(1.0f, 0.3294117748737335205078125f, 0.3294117748737335205078125f));
        _2504 = all(bool3(_2499.x < 0.001000000047497451305389404296875f.xxx.x, _2499.y < 0.001000000047497451305389404296875f.xxx.y, _2499.z < 0.001000000047497451305389404296875f.xxx.z));
    }
    else
    {
        _2504 = false;
    }
    if (_2504)
    {
        return nativeColor;
    }
    float high = max(rgb.x, max(rgb.y, rgb.z));
    float low = min(rgb.x, min(rgb.y, rgb.z));
    float delta = high - low;
    if (delta < 9.9999997473787516355514526367188e-06f)
    {
        return (nativeColor.x * high).xxx;
    }
    float3 hue = (rgb - low.xxx) / delta.xxx;
    float saturation = ((1.0f - (nativeColor.y / max(nativeColor.x, 9.9999997473787516355514526367188e-06f))) * (delta / max(high, 9.9999997473787516355514526367188e-06f))) / 0.6705882549285888671875f;
    return lerp(1.0f.xxx, hue, clamp(saturation, 0.0f, 1.0f).xxx) * (nativeColor.x * high);
}

float4 disabledColor(float mask)
{
    float3 _49;
    if (false)
    {
        _49 = float3(1.0f, 0.3294117748737335205078125f, 0.3294117748737335205078125f);
    }
    else
    {
        _49 = uDisabledColors.Sample(sampleruDisabledColors, fieldUV).xyz;
    }
    float3 rgb = _49;
    float u_xlat16_0_1 = mask;
    float3 u_xlat16_1_1;
    u_xlat16_1_1.x = u_xlat16_0_1 + (-9.9999997473787516355514526367188e-05f);
    bool u_xlatb4 = u_xlat16_1_1.x < 0.0f;
    if (u_xlatb4)
    {
        discard;
    }
    float3 u_xlat4_1;
    u_xlat4_1.x = uUnityTime.x * uDisabledSpeed;
    u_xlat16_1_1.x = dot(_DisplaceDirection.xy, _DisplaceDirection.xy);
    u_xlat16_1_1.x = rsqrt(u_xlat16_1_1.x);
    float3 _107 = u_xlat16_1_1;
    float2 _111 = _107.xx * _DisplaceDirection.xy;
    u_xlat16_1_1.x = _111.x;
    u_xlat16_1_1.y = _111.y;
    float3 _123 = u_xlat4_1.xxx * u_xlat16_1_1.xyx;
    float4 u_xlat2;
    u_xlat2.x = _123.x;
    u_xlat2.y = _123.y;
    u_xlat2.w = _123.z;
    u_xlat2.z = u_xlat4_1.x * (-u_xlat16_1_1.y);
    u_xlat2 += disabledDisplaceUV.xyxy;
    float u_xlat16_4_1 = uDisplaceTex.Sample(sampleruDisplaceTex, u_xlat2.zw).x;
    float u_xlat16_8_1 = uDisplaceTex.Sample(sampleruDisplaceTex, u_xlat2.xy).x;
    float u_xlat16_9_1 = u_xlat16_4_1 + (-0.5f);
    float u_xlat16_13_1 = u_xlat16_4_1 + u_xlat16_8_1;
    float u_xlat16_3_1 = u_xlat16_8_1 + (-0.5f);
    u_xlat16_13_1 *= 0.5f;
    u_xlat2.x = u_xlat16_9_1 * (-u_xlat16_1_1.y);
    u_xlat2.y = u_xlat16_9_1 * u_xlat16_1_1.x;
    float2 _189 = (u_xlat16_1_1.xy * u_xlat16_3_1.xx) + u_xlat2.xy;
    u_xlat4_1.x = _189.x;
    u_xlat4_1.y = _189.y;
    float3 _194 = u_xlat4_1;
    float2 _202 = (_194.xy * uDisabledSparkIntensity.xx) + sparkUV;
    u_xlat4_1.x = _202.x;
    u_xlat4_1.y = _202.y;
    u_xlat16_4_1 = uSparkTex.Sample(sampleruSparkTex, u_xlat4_1.xy).x;
    float3 param = uDisabledSparkTint;
    float3 param_1 = rgb;
    u_xlat4_1 = u_xlat16_4_1.xxx * blockPalette(param, param_1);
    u_xlat4_1 = u_xlat16_13_1.xxx * u_xlat4_1;
    float3 param_2 = uDisabledFillColor.xyz;
    float3 param_3 = rgb;
    u_xlat16_1_1 = blockPalette(param_2, param_3) * uDisabledFillOpacity.xxx;
    u_xlat16_1_1 = (u_xlat4_1 * uDisabledSparkOpacity.xxx) + u_xlat16_1_1;
    float3 _249 = u_xlat16_0_1.xxx * u_xlat16_1_1;
    float4 result;
    result.x = _249.x;
    result.y = _249.y;
    result.z = _249.z;
    result.w = 1.0f;
    return result;
}

float composeSample(float2 uv)
{
    float2 param = uv;
    return uMasks.Sample(sampleruMasks, basePixelUV(param)).x;
}

float2 effectSample(float2 uv)
{
    return uMasks.Sample(sampleruMasks, uv).yz;
}

float4 auxSample(float2 uv)
{
    float2 param = uv;
    return uAuxMasks.Sample(sampleruAuxMasks, basePixelUV(param));
}

float hoverSample(float2 uv)
{
    float2 size = _EffectRT_TexelSize.zw * 0.5f;
    return uAuxMasks.Sample(sampleruAuxMasks, (floor(uv * size) + 0.5f.xx) / size).w;
}

float4 snapshotSample(inout float2 uv)
{
    float2 size = max(floor(_PEVScreenParams.xy / 6.0f.xx), 1.0f.xx);
    uv = (floor(clamp(uv, 0.0f.xx, 1.0f.xx) * size) + 0.5f.xx) / size;
    return _GrabTexture.Sample(sampler_GrabTexture, PEVGrabUV(uv));
}

float4 fullActive()
{
    float3 _261;
    if (false)
    {
        _261 = float3(1.0f, 0.3294117748737335205078125f, 0.3294117748737335205078125f);
    }
    else
    {
        _261 = uActiveColors.Sample(sampleruActiveColors, fieldUV).xyz;
    }
    float3 rgb = _261;
    u_xlat0.x = fieldUV.x + (-0.5f);
    u_xlat0.x = (-abs(u_xlat0.x)) + clipHalfWidth;
    u_xlatb0.x = u_xlat0.x < 0.0f;
    if (u_xlatb0.x)
    {
        discard;
    }
    float2 param = fieldUV;
    u_xlat16_0 = composeSample(param);
    u_xlat16 = fieldUV * _EffectRT_TexelSize.zw;
    u_xlat16 = floor(u_xlat16);
    u_xlat16 += 0.5f.xx;
    u_xlat16 *= _EffectRT_TexelSize.xy;
    float2 param_1 = u_xlat16;
    u_xlat16_16 = effectSample(param_1).x;
    float2 param_2 = fieldUV;
    u_xlat16_32 = effectSample(param_2).y;
    float2 param_3 = fieldUV;
    u_xlat16_48 = auxSample(param_3).x;
    float2 param_4 = fieldUV;
    u_xlat16_1.x = auxSample(param_4).y;
    float2 param_5 = fieldUV;
    u_xlat16_17 = auxSample(param_5).z;
    u_xlat16_2.x = (-u_xlat16_48) + u_xlat16_1.x;
    u_xlat16_18 = u_xlat16_17 * abs(u_xlat16_2.x);
    float2 param_6 = sceneUV;
    u_xlat48 = hoverSample(param_6);
    u_xlat16_34 = u_xlat16_16 + u_xlat16_0;
    u_xlat16_50 = u_xlat16_32 + u_xlat16_34;
    u_xlat16_2.x = (abs(u_xlat16_2.x) * u_xlat16_17) + u_xlat16_50;
    u_xlat16_2.x = u_xlat48 + u_xlat16_2.x;
    u_xlat16_2.x += (-9.9999997473787516355514526367188e-05f);
    u_xlatb1 = u_xlat16_2.x < 0.0f;
    if (u_xlatb1)
    {
        discard;
    }
    u_xlatb1 = 9.9999997473787516355514526367188e-05f < u_xlat16_50;
    float4 result;
    if (u_xlatb1)
    {
        u_xlat16_2.x = u_xlat16_16 * _EdgeOpacity;
        u_xlat16_34 = (u_xlat16_34 * (-u_xlat16_32)) + u_xlat16_32;
        u_xlat16_50 = u_xlat16_34 * _GlowIntensity;
        u_xlat16_3.x = dot(_DisplaceDirection.xy, _DisplaceDirection.xy);
        u_xlat16_3.x = rsqrt(u_xlat16_3.x);
        float4 _429 = u_xlat16_3;
        float2 _433 = _429.xx * _DisplaceDirection.xy;
        u_xlat16_3.x = _433.x;
        u_xlat16_3.y = _433.y;
        u_xlat16.x = uUnityTime.x * _DisplaceSpeed;
        u_xlat33.y = u_xlat16.x * u_xlat16_3.x;
        float2 _459 = (u_xlat16_3.xy * u_xlat16.xx) + displaceUV;
        u_xlat1.x = _459.x;
        u_xlat1.y = _459.y;
        u_xlat32.x = max(_BackgroundPixelScale, 1.0f);
        float4 _469 = u_xlat1;
        float2 _474 = _469.xy * _PEVScreenParams.xy;
        u_xlat1.x = _474.x;
        u_xlat1.y = _474.y;
        float4 _479 = u_xlat1;
        float2 _483 = _479.xy / u_xlat32.xx;
        u_xlat1.x = _483.x;
        u_xlat1.y = _483.y;
        float4 _488 = u_xlat1;
        float2 _490 = floor(_488.xy);
        u_xlat1.x = _490.x;
        u_xlat1.y = _490.y;
        u_xlat4.x = u_xlat32.x * 0.5f;
        float4 _500 = u_xlat1;
        float2 _507 = (_500.xy * u_xlat32.xx) + u_xlat4.xx;
        u_xlat1.x = _507.x;
        u_xlat1.y = _507.y;
        u_xlat33.x = u_xlat16.x * (-u_xlat16_3.y);
        u_xlat33 += displaceUV;
        u_xlat33 *= _PEVScreenParams.xy;
        u_xlat33 /= u_xlat32.xx;
        u_xlat33 = floor(u_xlat33);
        float2 _538 = (u_xlat33 * u_xlat32.xx) + u_xlat4.xx;
        u_xlat1.z = _538.x;
        u_xlat1.w = _538.y;
        u_xlat1 /= _PEVScreenParams.xyxy;
        u_xlat16_16 = uDisplaceTex.Sample(sampleruDisplaceTex, u_xlat1.xy).x;
        u_xlat16_1.x = uDisplaceTex.Sample(sampleruDisplaceTex, u_xlat1.zw).x;
        u_xlat16_35 = u_xlat16_16 + u_xlat16_1.x;
        u_xlat16_35 *= 0.5f;
        u_xlat16_51 = u_xlat16_16 + (-0.5f);
        u_xlat16_5.x = u_xlat16_1.x + (-0.5f);
        u_xlat1.x = (-u_xlat16_3.y) * u_xlat16_5.x;
        u_xlat1.y = u_xlat16_3.x * u_xlat16_5.x;
        float4 _591 = u_xlat1;
        float2 _593 = (u_xlat16_3.xy * u_xlat16_51.xx) + _591.xy;
        u_xlat1.x = _593.x;
        u_xlat1.y = _593.y;
        u_xlat33 = sceneUV * _PEVScreenParams.xy;
        u_xlat33 /= u_xlat32.xx;
        u_xlat33 = floor(u_xlat33);
        u_xlat16 = (u_xlat33 * u_xlat32.xx) + u_xlat4.xx;
        u_xlat16 /= _PEVScreenParams.xy;
        u_xlat16 = (u_xlat1.xy * _DisplaceStrength.xx) + u_xlat16;
        u_xlat33 = (u_xlat1.xy * _SparkDisplaceIntensity.xx) + sparkUV;
        u_xlat16_33 = uSparkTex.Sample(sampleruSparkTex, u_xlat33).x;
        float3 param_7 = _SparkTint;
        float3 param_8 = rgb;
        float3 _648 = u_xlat16_33.xxx * blockPalette(param_7, param_8);
        u_xlat4.x = _648.x;
        u_xlat4.y = _648.y;
        u_xlat4.z = _648.z;
        float2 param_9 = u_xlat16;
        float4 _659 = snapshotSample(param_9);
        u_xlat6 = _659.xyz;
        u_xlatb16 = 0 < _TouchPosCount;
        if (u_xlatb16)
        {
            u_xlat16 = screenPos.xy / screenPos.ww;
            u_xlat16 = (u_xlat1.xy * _DisplaceStrength.xx) + u_xlat16;
            u_xlat16 *= _PEVScreenParams.xy;
            u_xlat16 /= _PEVScreenParams.yy;
            u_xlati1 = _TouchPosCount;
            u_xlat33.x = 1.0f;
            for (int u_xlati_loop_1 = 0; u_xlati_loop_1 < 10; u_xlati_loop_1++)
            {
                u_xlatb49 = u_xlati_loop_1 >= u_xlati1;
                if (u_xlatb49)
                {
                    break;
                }
                u_xlat7 = u_xlat16 + (-_TouchPos[u_xlati_loop_1]);
                u_xlat49 = dot(u_xlat7, u_xlat7);
                u_xlat49 = sqrt(u_xlat49);
                u_xlat52 = (-u_xlat49) + u_xlat33.x;
                u_xlat52 = (-abs(u_xlat52)) + _TouchPosSDFSmoothness;
                u_xlat52 = max(u_xlat52, 0.0f);
                u_xlat52 /= _TouchPosSDFSmoothness;
                u_xlat49 = min(u_xlat49, u_xlat33.x);
                u_xlat52 *= u_xlat52;
                u_xlat52 *= _TouchPosSDFSmoothness;
                u_xlat33.x = ((-u_xlat52) * 0.25f) + u_xlat49;
            }
            u_xlat16.x = u_xlat33.x + (-_TouchPosRadius);
            u_xlat32.x = 1.0f / (-_TouchPosRadius);
            u_xlat16.x = u_xlat32.x * u_xlat16.x;
            u_xlat16.x = clamp(u_xlat16.x, 0.0f, 1.0f);
            u_xlat32.x = (u_xlat16.x * (-2.0f)) + 3.0f;
            u_xlat16.x *= u_xlat16.x;
            u_xlat16.x *= u_xlat32.x;
            u_xlat16.x = log2(u_xlat16.x);
            u_xlat16.x *= _TouchPosSDFFalloff;
            u_xlat16.x = exp2(u_xlat16.x);
        }
        else
        {
            u_xlat16.x = 0.0f;
        }
        float3 _830 = u_xlat16_35.xxx * u_xlat4.xyz;
        u_xlat16_3.x = _830.x;
        u_xlat16_3.y = _830.y;
        u_xlat16_3.w = _830.z;
        float3 _842 = u_xlat16_3.xyw * _SparkMapOpacity.xxx;
        u_xlat1.x = _842.x;
        u_xlat1.y = _842.y;
        u_xlat1.z = _842.z;
        u_xlatb32 = u_xlat6.y >= u_xlat6.z;
        u_xlat16_5.x = float(u_xlatb32);
        float2 _864 = (-u_xlat6.zy) + u_xlat6.yz;
        u_xlat16_21.x = _864.x;
        u_xlat16_21.y = _864.y;
        u_xlat16_8.x = 1.0f;
        u_xlat16_8.y = -1.0f;
        float2 _881 = (u_xlat16_5.xx * u_xlat16_21.xy) + u_xlat6.zy;
        u_xlat16_4.x = _881.x;
        u_xlat16_4.y = _881.y;
        float2 _893 = (u_xlat16_5.xx * u_xlat16_8.xy) + float2(-1.0f, 0.666666686534881591796875f);
        u_xlat16_4.z = _893.x;
        u_xlat16_4.w = _893.y;
        u_xlatb32 = u_xlat6.x >= u_xlat16_4.x;
        u_xlat16_5.x = float(u_xlatb32);
        float3 _909 = -u_xlat16_4.xyw;
        u_xlat16_7.x = _909.x;
        u_xlat16_7.y = _909.y;
        u_xlat16_7.z = _909.z;
        u_xlat16_7.w = -u_xlat6.x;
        u_xlat16_8.x = u_xlat6.x + u_xlat16_7.x;
        float3 _930 = u_xlat16_4.yzx + u_xlat16_7.yzw;
        u_xlat16_8.y = _930.x;
        u_xlat16_8.z = _930.y;
        u_xlat16_8.w = _930.z;
        u_xlat16_21 = (u_xlat16_5.xxx * u_xlat16_8.xyz) + u_xlat16_4.xyw;
        u_xlat16_5.x = (u_xlat16_5.x * u_xlat16_8.w) + u_xlat6.x;
        u_xlat16_8.x = min(u_xlat16_21.y, u_xlat16_5.x);
        u_xlat16_8.x = u_xlat16_21.x + (-u_xlat16_8.x);
        u_xlat16_5.x = (-u_xlat16_21.y) + u_xlat16_5.x;
        u_xlat32.x = (u_xlat16_8.x * 6.0f) + 1.0000000133514319600180897396058e-10f;
        u_xlat32.x = u_xlat16_5.x / u_xlat32.x;
        u_xlat32.x += u_xlat16_21.z;
        u_xlat49 = u_xlat16_21.x + 1.0000000133514319600180897396058e-10f;
        u_xlat49 = u_xlat16_8.x / u_xlat49;
        u_xlat16_5.x = (u_xlat1.x * _SparkHueShiftAmount) + abs(u_xlat32.x);
        float paletteHigh = max(rgb.x, max(rgb.y, rgb.z));
        float paletteLow = min(rgb.x, min(rgb.y, rgb.z));
        float _1029;
        if (false)
        {
            _1029 = 1.0f;
        }
        else
        {
            _1029 = ((paletteHigh - paletteLow) / max(paletteHigh, 9.9999997473787516355514526367188e-06f)) / 0.6705882549285888671875f;
        }
        float paletteSaturation = _1029;
        u_xlat16_37 = ((u_xlat1.y * _SparkHueShiftAmount) * paletteSaturation) + u_xlat49;
        u_xlat16_21.x = (u_xlat1.z * _SparkHueShiftAmount) + u_xlat16_21.x;
        float3 _1064 = u_xlat16_5.xxx + float3(1.0f, 0.666666686534881591796875f, 0.3333333432674407958984375f);
        u_xlat16_8.x = _1064.x;
        u_xlat16_8.y = _1064.y;
        u_xlat16_8.z = _1064.z;
        float4 _1071 = u_xlat16_8;
        float3 _1073 = frac(_1071.xyz);
        u_xlat16_8.x = _1073.x;
        u_xlat16_8.y = _1073.y;
        u_xlat16_8.z = _1073.z;
        float4 _1080 = u_xlat16_8;
        float3 _1086 = (_1080.xyz * 6.0f.xxx) + (-3.0f).xxx;
        u_xlat16_8.x = _1086.x;
        u_xlat16_8.y = _1086.y;
        u_xlat16_8.z = _1086.z;
        float4 _1093 = u_xlat16_8;
        float3 _1097 = abs(_1093.xyz) + (-1.0f).xxx;
        u_xlat16_8.x = _1097.x;
        u_xlat16_8.y = _1097.y;
        u_xlat16_8.z = _1097.z;
        float4 _1104 = u_xlat16_8;
        float3 _1108 = clamp(_1104.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat16_8.x = _1108.x;
        u_xlat16_8.y = _1108.y;
        u_xlat16_8.z = _1108.z;
        float4 _1115 = u_xlat16_8;
        float3 _1117 = _1115.xyz + (-1.0f).xxx;
        u_xlat16_8.x = _1117.x;
        u_xlat16_8.y = _1117.y;
        u_xlat16_8.z = _1117.z;
        float3 _1130 = (u_xlat16_37.xxx * u_xlat16_8.xyz) + 1.0f.xxx;
        u_xlat16_5.x = _1130.x;
        u_xlat16_5.z = _1130.y;
        u_xlat16_5.w = _1130.z;
        float3 _1141 = u_xlat16_5.xzw * u_xlat16_21.xxx;
        u_xlat16_8.x = _1141.x;
        u_xlat16_8.y = _1141.y;
        u_xlat16_8.z = _1141.z;
        u_xlat16_9 = u_xlat16_8.xyz + u_xlat16_8.xyz;
        u_xlat16_10 = u_xlat1.xyz * u_xlat16_9;
        float4 _1162 = u_xlat16_5;
        float3 _1165 = ((-u_xlat16_21.xxx) * _1162.xzw) + 1.0f.xxx;
        u_xlat16_5.x = _1165.x;
        u_xlat16_5.y = _1165.y;
        u_xlat16_5.z = _1165.z;
        float4 _1172 = u_xlat16_5;
        float4 _1174 = u_xlat16_5;
        float3 _1176 = _1172.xyz + _1174.xyz;
        u_xlat16_5.x = _1176.x;
        u_xlat16_5.y = _1176.y;
        u_xlat16_5.z = _1176.z;
        float4 _1183 = u_xlat16_3;
        float3 _1189 = ((-_1183.xyw) * _SparkMapOpacity.xxx) + 1.0f.xxx;
        u_xlat16_3.x = _1189.x;
        u_xlat16_3.y = _1189.y;
        u_xlat16_3.w = _1189.z;
        float4 _1199 = u_xlat16_3;
        float3 _1202 = ((-u_xlat16_5.xyz) * _1199.xyw) + 1.0f.xxx;
        u_xlat16_3.x = _1202.x;
        u_xlat16_3.y = _1202.y;
        u_xlat16_3.w = _1202.z;
        u_xlatb6 = bool4(u_xlat16_8.xyzx.x >= float4(0.5f, 0.5f, 0.5f, 0.0f).x, u_xlat16_8.xyzx.y >= float4(0.5f, 0.5f, 0.5f, 0.0f).y, u_xlat16_8.xyzx.z >= float4(0.5f, 0.5f, 0.5f, 0.0f).z, u_xlat16_8.xyzx.w >= float4(0.5f, 0.5f, 0.5f, 0.0f).w).xyz;
        u_xlat6.x = float(u_xlatb6.x);
        u_xlat6.y = float(u_xlatb6.y);
        u_xlat6.z = float(u_xlatb6.z);
        float4 _1230 = u_xlat1;
        float3 _1235 = ((-u_xlat16_9) * _1230.xyz) + u_xlat16_3.xyw;
        u_xlat1.x = _1235.x;
        u_xlat1.y = _1235.y;
        u_xlat1.z = _1235.z;
        float4 _1243 = u_xlat1;
        float3 _1247 = (u_xlat6 * _1243.xyz) + u_xlat16_10;
        u_xlat1.x = _1247.x;
        u_xlat1.y = _1247.y;
        u_xlat1.z = _1247.z;
        float4 _1254 = u_xlat1;
        float3 _1258 = clamp(_1254.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat1.x = _1258.x;
        u_xlat1.y = _1258.y;
        u_xlat1.z = _1258.z;
        float3 param_10 = _FillColor.xyz;
        float3 param_11 = rgb;
        float3 _1279 = ((-u_xlat16_35.xxx) * _DisplaceBlendIntensity.xxx) + blockPalette(param_10, param_11);
        u_xlat16_3.x = _1279.x;
        u_xlat16_3.y = _1279.y;
        u_xlat16_3.z = _1279.z;
        float3 _1291 = u_xlat1.xyz + (-u_xlat16_3.xyz);
        u_xlat16_5.x = _1291.x;
        u_xlat16_5.y = _1291.y;
        u_xlat16_5.z = _1291.z;
        float4 _1304 = u_xlat16_3;
        float3 _1306 = (_FillStrength.xxx * u_xlat16_5.xyz) + _1304.xyz;
        u_xlat16_3.x = _1306.x;
        u_xlat16_3.y = _1306.y;
        u_xlat16_3.z = _1306.z;
        float3 param_12 = _GlowColor.xyz;
        float3 param_13 = rgb;
        float3 _1322 = u_xlat16_50.xxx * blockPalette(param_12, param_13);
        u_xlat16_5.x = _1322.x;
        u_xlat16_5.y = _1322.y;
        u_xlat16_5.z = _1322.z;
        float3 param_14 = _EdgeColor.xyz;
        float3 param_15 = rgb;
        float4 _1339 = u_xlat16_5;
        float3 _1341 = (blockPalette(param_14, param_15) * u_xlat16_2.xxx) + _1339.xyz;
        u_xlat16_5.x = _1341.x;
        u_xlat16_5.y = _1341.y;
        u_xlat16_5.z = _1341.z;
        float4 _1348 = u_xlat16_3;
        float3 _1355 = (_1348.xyz * u_xlat16_0.xxx) + u_xlat16_5.xyz;
        u_xlat16_3.x = _1355.x;
        u_xlat16_3.y = _1355.y;
        u_xlat16_3.z = _1355.z;
        u_xlat16_50 = (u_xlat16.x * _TouchPosShine) + 1.0f;
        float4 _1370 = u_xlat16_3;
        float3 _1372 = u_xlat16_50.xxx * _1370.xyz;
        u_xlat16_3.x = _1372.x;
        u_xlat16_3.y = _1372.y;
        u_xlat16_3.z = _1372.z;
        u_xlat16_2.x = (u_xlat16_0 * _FillOpacity) + u_xlat16_2.x;
        result.w = (u_xlat16_34 * _GlowIntensity) + u_xlat16_2.x;
    }
    else
    {
        u_xlat16_3.x = 0.0f;
        u_xlat16_3.y = 0.0f;
        u_xlat16_3.z = 0.0f;
        result.w = 0.0f;
    }
    u_xlatb0.x = 9.9999997473787516355514526367188e-05f < u_xlat16_18;
    float3 _1409 = _ShineBrightness.xxx * _ShineColor.xyz;
    u_xlat1.x = _1409.x;
    u_xlat1.y = _1409.y;
    u_xlat1.z = _1409.z;
    u_xlat16.x = uUnityTime.y * _ShineSpeed;
    u_xlat16.x = sin(u_xlat16.x);
    u_xlat16.x = (u_xlat16.x * 0.5f) + 1.0f;
    float4 _1433 = u_xlat1;
    float3 _1435 = u_xlat16.xxx * _1433.xyz;
    u_xlat1.x = _1435.x;
    u_xlat1.y = _1435.y;
    u_xlat1.z = _1435.z;
    float4 _1444 = u_xlat1;
    float3 _1446 = u_xlat16_18.xxx * _1444.xyz;
    u_xlat1.x = _1446.x;
    u_xlat1.y = _1446.y;
    u_xlat1.z = _1446.z;
    float3 _1455;
    if (u_xlatb0.x)
    {
        _1455 = u_xlat1.xyz;
    }
    else
    {
        _1455 = 0.0f.xxx;
    }
    u_xlat16_2.x = _1455.x;
    u_xlat16_2.z = _1455.y;
    u_xlat16_2.w = _1455.z;
    u_xlat16_18 = u_xlatb0.x ? u_xlat16_18 : 0.0f;
    u_xlatb0.x = 9.9999997473787516355514526367188e-05f < u_xlat48;
    if (u_xlatb0.x)
    {
        u_xlat0.x = dot(_TouchDisplaceDirection.xy, _TouchDisplaceDirection.xy);
        u_xlat0.x = rsqrt(u_xlat0.x);
        float4 _1491 = u_xlat0;
        float2 _1495 = _1491.xx * _TouchDisplaceDirection.xy;
        u_xlat0.x = _1495.x;
        u_xlat0.y = _1495.y;
        u_xlat1.x = uUnityTime.x * _TouchDisplaceSpeed;
        float2 _1514 = (u_xlat0.xy * u_xlat1.xx) + touchDisplaceUV;
        u_xlat17.x = _1514.x;
        u_xlat17.y = _1514.y;
        u_xlat49 = max(_TouchBackgroundPixelScale, 1.0f);
        float3 _1522 = u_xlat17;
        float2 _1526 = _1522.xy * _PEVScreenParams.xy;
        u_xlat17.x = _1526.x;
        u_xlat17.y = _1526.y;
        float3 _1531 = u_xlat17;
        float2 _1535 = _1531.xy / u_xlat49.xx;
        u_xlat17.x = _1535.x;
        u_xlat17.y = _1535.y;
        float3 _1540 = u_xlat17;
        float2 _1542 = floor(_1540.xy);
        u_xlat17.x = _1542.x;
        u_xlat17.y = _1542.y;
        u_xlat6.x = u_xlat49 * 0.5f;
        float2 _1557 = (u_xlat17.xy * u_xlat49.xx) + u_xlat6.xx;
        u_xlat1.y = _1557.x;
        u_xlat1.z = _1557.y;
        float4 _1562 = u_xlat0;
        float2 _1564 = -_1562.yy;
        u_xlat0.z = _1564.x;
        u_xlat0.w = _1564.y;
        u_xlat22 = (u_xlat0.zx * u_xlat1.xx) + touchDisplaceUV;
        u_xlat22 *= _PEVScreenParams.xy;
        u_xlat22 /= u_xlat49.xx;
        u_xlat22 = floor(u_xlat22);
        float2 _1593 = (u_xlat22 * u_xlat49.xx) + u_xlat6.xx;
        u_xlat1.x = _1593.x;
        u_xlat1.w = _1593.y;
        u_xlat1 /= _PEVScreenParams.xxyy;
        u_xlat16_32 = uDisplaceTex.Sample(sampleruDisplaceTex, u_xlat1.yz).x;
        u_xlat16_1.x = uDisplaceTex.Sample(sampleruDisplaceTex, u_xlat1.xw).x;
        u_xlat16_51 = u_xlat16_32 + (-0.5f);
        u_xlat16_5.x = u_xlat16_1.x + (-0.5f);
        u_xlat32 = u_xlat0.wx * u_xlat16_5.xx;
        float4 _1624 = u_xlat0;
        float2 _1630 = (_1624.xy * u_xlat16_51.xx) + u_xlat32;
        u_xlat0.x = _1630.x;
        u_xlat0.y = _1630.y;
        float4 _1635 = u_xlat0;
        float2 _1642 = (_1635.xy * _TouchDisplaceStrength.xx) + sceneUV;
        u_xlat0.x = _1642.x;
        u_xlat0.y = _1642.y;
        float2 param_16 = u_xlat0.xy;
        u_xlat16_0 = hoverSample(param_16);
        u_xlat16 = screenPos.xy / screenPos.ww;
        u_xlat48 = uUnityTime.y * _NoiseDirChangeSpeed;
        u_xlat1.x = floor(u_xlat48);
        u_xlat48 = frac(u_xlat48);
        u_xlat17.x = u_xlat48 * u_xlat48;
        u_xlat48 = ((-u_xlat48) * 2.0f) + 3.0f;
        u_xlat48 *= u_xlat17.x;
        float2 _1683 = u_xlat1.xx * 0.103100001811981201171875f.xx;
        u_xlat4.x = _1683.x;
        u_xlat4.z = _1683.y;
        u_xlat4.y = 0.381470024585723876953125f;
        u_xlat4.w = 0.938210070133209228515625f;
        u_xlat4 = frac(u_xlat4);
        u_xlat17 = u_xlat4.yxx + 33.3300018310546875f.xxx;
        u_xlat17.x = dot(u_xlat4.xyx, u_xlat17);
        float3 _1704 = u_xlat17;
        float2 _1708 = _1704.xx + u_xlat4.xy;
        u_xlat17.x = _1708.x;
        u_xlat17.y = _1708.y;
        u_xlat33.x = u_xlat17.y + u_xlat17.x;
        u_xlat17.x *= u_xlat33.x;
        u_xlat6.x = frac(u_xlat17.x);
        u_xlat17 = u_xlat4.wzz + 33.3300018310546875f.xxx;
        u_xlat17.x = dot(u_xlat4.zwz, u_xlat17);
        float3 _1737 = u_xlat17;
        float2 _1741 = _1737.xx + u_xlat4.zw;
        u_xlat17.x = _1741.x;
        u_xlat17.y = _1741.y;
        u_xlat33.x = u_xlat17.y + u_xlat17.x;
        u_xlat17.x *= u_xlat33.x;
        u_xlat6.y = frac(u_xlat17.x);
        float2 _1767 = (u_xlat6.xy * 2.0f.xx) + (-1.0f).xx;
        u_xlat17.x = _1767.x;
        u_xlat17.y = _1767.y;
        u_xlat1.x += 1.0f;
        float2 _1778 = u_xlat1.xx * 0.103100001811981201171875f.xx;
        u_xlat4.x = _1778.x;
        u_xlat4.z = _1778.y;
        u_xlat4.y = 0.381470024585723876953125f;
        u_xlat4.w = 0.938210070133209228515625f;
        u_xlat4 = frac(u_xlat4);
        u_xlat6 = u_xlat4.yxx + 33.3300018310546875f.xxx;
        u_xlat1.x = dot(u_xlat4.xyx, u_xlat6);
        float4 _1795 = u_xlat1;
        float2 _1799 = _1795.xx + u_xlat4.xy;
        u_xlat1.x = _1799.x;
        u_xlat1.w = _1799.y;
        u_xlat49 = u_xlat1.w + u_xlat1.x;
        u_xlat1.x *= u_xlat49;
        u_xlat6.x = frac(u_xlat1.x);
        u_xlat11 = u_xlat4.wzz + 33.3300018310546875f.xxx;
        u_xlat1.x = dot(u_xlat4.zwz, u_xlat11);
        float4 _1827 = u_xlat1;
        float2 _1831 = _1827.xx + u_xlat4.zw;
        u_xlat1.x = _1831.x;
        u_xlat1.w = _1831.y;
        u_xlat49 = u_xlat1.w + u_xlat1.x;
        u_xlat1.x *= u_xlat49;
        u_xlat6.y = frac(u_xlat1.x);
        float2 _1853 = (u_xlat6.xy * 2.0f.xx) + (-1.0f).xx;
        u_xlat1.x = _1853.x;
        u_xlat1.w = _1853.y;
        float4 _1861 = u_xlat1;
        float2 _1863 = (-u_xlat17.xy) + _1861.xw;
        u_xlat1.x = _1863.x;
        u_xlat1.w = _1863.y;
        float4 _1870 = u_xlat1;
        float2 _1875 = (u_xlat48.xx * _1870.xw) + u_xlat17.xy;
        u_xlat1.x = _1875.x;
        u_xlat1.y = _1875.y;
        float4 _1880 = u_xlat1;
        float2 _1888 = (_1880.xy * _NoiseEvoSpeed.xx) + noiseUV;
        u_xlat1.x = _1888.x;
        u_xlat1.y = _1888.y;
        u_xlat16_1 = uNoiseTex.Sample(sampleruNoiseTex, u_xlat1.xy).xy;
        float2 _1901 = u_xlat16_1 + (-0.5f).xx;
        u_xlat1.x = _1901.x;
        u_xlat1.y = _1901.y;
        u_xlat48 = _NoiseRadius + (-_NoiseSmoothness);
        u_xlat33.x = _NoiseRadius + _NoiseSmoothness;
        u_xlat33.x = (-u_xlat48) + u_xlat33.x;
        u_xlat48 = (-u_xlat48) + u_xlat16_0;
        u_xlat33.x = 1.0f / u_xlat33.x;
        u_xlat48 *= u_xlat33.x;
        u_xlat48 = clamp(u_xlat48, 0.0f, 1.0f);
        u_xlat33.x = (u_xlat48 * (-2.0f)) + 3.0f;
        u_xlat48 *= u_xlat48;
        u_xlat48 *= u_xlat33.x;
        u_xlat33.x = _PEVScreenParams.y * _SDFCellSize;
        float2 _1957 = u_xlat1.xy + u_xlat1.xy;
        u_xlat16_5.x = _1957.x;
        u_xlat16_5.y = _1957.y;
        float2 _1967 = u_xlat16_5.xy * _NoiseDisplaceStrength.xx;
        u_xlat1.x = _1967.x;
        u_xlat1.y = _1967.y;
        float4 _1974 = u_xlat1;
        float2 _1976 = u_xlat33.xx * _1974.xy;
        u_xlat1.x = _1976.x;
        u_xlat1.y = _1976.y;
        float4 _1981 = u_xlat1;
        float2 _1985 = _1981.xy / _PEVScreenParams.xy;
        u_xlat1.x = _1985.x;
        u_xlat1.y = _1985.y;
        u_xlat16 += u_xlat1.xy;
        u_xlat16 *= _PEVScreenParams.xy;
        u_xlat16 /= u_xlat33.xx;
        float2 _2003 = floor(u_xlat16);
        u_xlat1.x = _2003.x;
        u_xlat1.y = _2003.y;
        u_xlat16 = frac(u_xlat16);
        u_xlat33.x = uUnityTime.y * _SDFMoveSpeed;
        u_xlat49 = floor(u_xlat33.x);
        u_xlat33.x = frac(u_xlat33.x);
        u_xlat6.x = u_xlat33.x * u_xlat33.x;
        u_xlat33.x = ((-u_xlat33.x) * 2.0f) + 3.0f;
        u_xlat33.x *= u_xlat6.x;
        float4 _2043 = u_xlat1;
        float2 _2045 = u_xlat49.xx + _2043.xy;
        u_xlat1.x = _2045.x;
        u_xlat1.y = _2045.y;
        float2 _2052 = u_xlat1.xy * 0.103100001811981201171875f.xx;
        u_xlat6.x = _2052.x;
        u_xlat6.y = _2052.y;
        float3 _2057 = u_xlat6;
        float2 _2059 = frac(_2057.xy);
        u_xlat6.x = _2059.x;
        u_xlat6.y = _2059.y;
        u_xlat11 = u_xlat6.yxx + 33.3300018310546875f.xxx;
        u_xlat49 = dot(u_xlat6.xyx, u_xlat11);
        float3 _2073 = u_xlat6;
        float2 _2075 = u_xlat49.xx + _2073.xy;
        u_xlat6.x = _2075.x;
        u_xlat6.y = _2075.y;
        u_xlat49 = u_xlat6.y + u_xlat6.x;
        u_xlat49 = u_xlat6.x * u_xlat49;
        u_xlat6.x = frac(u_xlat49);
        u_xlat4 = u_xlat1.yxyx + float4(17.1700000762939453125f, 17.1700000762939453125f, 1.0f, 1.0f);
        u_xlat4 *= 0.103100001811981201171875f.xxxx;
        u_xlat4 = frac(u_xlat4);
        u_xlat5 = u_xlat4 + 33.3300018310546875f.xxxx;
        u_xlat49 = dot(u_xlat4.yxy, u_xlat5.xyy);
        u_xlat38 = u_xlat49.xx + u_xlat4.yx;
        u_xlat49 = u_xlat38.y + u_xlat38.x;
        u_xlat49 = u_xlat38.x * u_xlat49;
        u_xlat6.y = frac(u_xlat49);
        u_xlat49 = dot(u_xlat4.wzw, u_xlat5.zww);
        u_xlat38 = u_xlat49.xx + u_xlat4.wz;
        u_xlat49 = u_xlat38.y + u_xlat38.x;
        u_xlat49 = u_xlat38.x * u_xlat49;
        u_xlat11.x = frac(u_xlat49);
        float4 _2151 = u_xlat1;
        float2 _2155 = _2151.xy + 18.1700000762939453125f.xx;
        u_xlat1.x = _2155.x;
        u_xlat1.y = _2155.y;
        float4 _2160 = u_xlat1;
        float2 _2162 = _2160.xy * 0.103100001811981201171875f.xx;
        u_xlat1.x = _2162.x;
        u_xlat1.y = _2162.y;
        float4 _2167 = u_xlat1;
        float2 _2169 = frac(_2167.xy);
        u_xlat1.x = _2169.x;
        u_xlat1.y = _2169.y;
        u_xlat12 = u_xlat1.yxx + 33.3300018310546875f.xxx;
        u_xlat49 = dot(u_xlat1.xyx, u_xlat12);
        float4 _2184 = u_xlat1;
        float2 _2186 = u_xlat49.xx + _2184.xy;
        u_xlat1.x = _2186.x;
        u_xlat1.y = _2186.y;
        u_xlat17.x = u_xlat1.y + u_xlat1.x;
        u_xlat1.x *= u_xlat17.x;
        u_xlat11.y = frac(u_xlat1.x);
        float2 _2212 = (-u_xlat6.xy) + u_xlat11.xy;
        u_xlat1.x = _2212.x;
        u_xlat1.y = _2212.y;
        float4 _2219 = u_xlat1;
        float2 _2224 = (u_xlat33.xx * _2219.xy) + u_xlat6.xy;
        u_xlat1.x = _2224.x;
        u_xlat1.y = _2224.y;
        u_xlat16 += (-u_xlat1.xy);
        u_xlat16.x = dot(u_xlat16, u_xlat16);
        u_xlat16.x = sqrt(u_xlat16.x);
        u_xlat16.x += (-_SDFFalloff);
        u_xlat32.x = 1.0f / _SDFSmoothness;
        u_xlat16.x = u_xlat32.x * u_xlat16.x;
        u_xlat16.x = clamp(u_xlat16.x, 0.0f, 1.0f);
        u_xlat32.x = (u_xlat16.x * (-2.0f)) + 3.0f;
        u_xlat16.x *= u_xlat16.x;
        u_xlat16.x *= u_xlat32.x;
        float3 param_17 = _NoiseTint.xyz;
        float3 param_18 = rgb;
        float3 _2292 = (u_xlat16.xxx * blockPalette(param_17, param_18)) + u_xlat16.xxx;
        u_xlat16_8.x = _2292.x;
        u_xlat16_8.y = _2292.y;
        u_xlat16_8.z = _2292.z;
        float4 _2299 = u_xlat16_8;
        float3 _2303 = clamp(_2299.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat16_8.x = _2303.x;
        u_xlat16_8.y = _2303.y;
        u_xlat16_8.z = _2303.z;
        u_xlat16_9 = u_xlat48.xxx * u_xlat16_8.xyz;
        u_xlat16_51 = u_xlat16_0;
        u_xlat16_51 = clamp(u_xlat16_51, 0.0f, 1.0f);
        u_xlat16_56 = (u_xlat16_51 * (-2.0f)) + 3.0f;
        u_xlat16_51 *= u_xlat16_51;
        u_xlat16_51 *= u_xlat16_56;
        float3 param_19 = _TouchGlowColor.xyz;
        float3 param_20 = rgb;
        u_xlat16_10 = u_xlat16_51.xxx * blockPalette(param_19, param_20);
        u_xlat16_13 = u_xlat16_9 * u_xlat16_10;
        u_xlat16_14 = u_xlat16_13 + u_xlat16_13;
        float4 _2346 = u_xlat16_8;
        float3 _2352 = ((-_2346.xyz) * u_xlat48.xxx) + 1.0f.xxx;
        u_xlat16_8.x = _2352.x;
        u_xlat16_8.y = _2352.y;
        u_xlat16_8.z = _2352.z;
        float4 _2359 = u_xlat16_8;
        float4 _2361 = u_xlat16_8;
        float3 _2363 = _2359.xyz + _2361.xyz;
        u_xlat16_8.x = _2363.x;
        u_xlat16_8.y = _2363.y;
        u_xlat16_8.z = _2363.z;
        float3 param_21 = _TouchGlowColor.xyz;
        float3 param_22 = rgb;
        u_xlat16_15 = ((-u_xlat16_51.xxx) * blockPalette(param_21, param_22)) + 1.0f.xxx;
        float4 _2382 = u_xlat16_8;
        float3 _2387 = ((-_2382.xyz) * u_xlat16_15) + 1.0f.xxx;
        u_xlat16_8.x = _2387.x;
        u_xlat16_8.y = _2387.y;
        u_xlat16_8.z = _2387.z;
        u_xlatb0 = bool4(u_xlat16_9.xyzx.x >= float4(0.5f, 0.5f, 0.5f, 0.0f).x, u_xlat16_9.xyzx.y >= float4(0.5f, 0.5f, 0.5f, 0.0f).y, u_xlat16_9.xyzx.z >= float4(0.5f, 0.5f, 0.5f, 0.0f).z, u_xlat16_9.xyzx.w >= float4(0.5f, 0.5f, 0.5f, 0.0f).w).xyz;
        u_xlat0.x = float(u_xlatb0.x);
        u_xlat0.y = float(u_xlatb0.y);
        u_xlat0.z = float(u_xlatb0.z);
        float3 _2416 = ((-u_xlat16_13) * 2.0f.xxx) + u_xlat16_8.xyz;
        u_xlat1.x = _2416.x;
        u_xlat1.y = _2416.y;
        u_xlat1.z = _2416.z;
        float4 _2423 = u_xlat0;
        float3 _2429 = (_2423.xyz * u_xlat1.xyz) + u_xlat16_14;
        u_xlat0.x = _2429.x;
        u_xlat0.y = _2429.y;
        u_xlat0.z = _2429.z;
        float4 _2436 = u_xlat0;
        float3 _2440 = clamp(_2436.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat0.x = _2440.x;
        u_xlat0.y = _2440.y;
        u_xlat0.z = _2440.z;
        float3 _2452 = (u_xlat16_10 * 0.5f.xxx) + u_xlat0.xyz;
        u_xlat16_8.x = _2452.x;
        u_xlat16_8.y = _2452.y;
        u_xlat16_8.z = _2452.z;
    }
    else
    {
        u_xlat16_8.x = 0.0f;
        u_xlat16_8.y = 0.0f;
        u_xlat16_8.z = 0.0f;
    }
    float4 _2463 = u_xlat16_2;
    float3 _2473 = (_2463.xzw * (u_xlat16_18 + uReadyExtra).xxx) + u_xlat16_3.xyz;
    u_xlat16_2.x = _2473.x;
    u_xlat16_2.y = _2473.y;
    u_xlat16_2.z = _2473.z;
    float3 _2484 = u_xlat16_8.xyz + u_xlat16_2.xyz;
    result.x = _2484.x;
    result.y = _2484.y;
    result.z = _2484.z;
    return result;
}

void frag_main()
{
    if (false)
    {
        float2 param = fieldUV;
        float disabledMask = uMasks.Sample(sampleruMasks, basePixelUV(param)).w;
        float param_1 = disabledMask;
        float4 _2666 = disabledColor(param_1);
        pev_output = _2666 * color;
    }
    else
    {
        float4 _2671 = fullActive();
        pev_output = _2671 * color;
    }
}

SPIRV_Cross_Output pev_fragment(SPIRV_Cross_Input stage_input)
{
    fieldUV = stage_input.fieldUV;
    disabledDisplaceUV = stage_input.disabledDisplaceUV;
    sparkUV = stage_input.sparkUV;
    clipHalfWidth = stage_input.clipHalfWidth;
    sceneUV = stage_input.sceneUV;
    displaceUV = stage_input.displaceUV;
    screenPos = stage_input.screenPos;
    touchDisplaceUV = stage_input.touchDisplaceUV;
    noiseUV = stage_input.noiseUV;
    color = stage_input.color;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.pev_output = pev_output;
    return stage_output;
}
