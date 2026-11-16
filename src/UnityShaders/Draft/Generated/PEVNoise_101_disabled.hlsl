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
Texture2D<float4> uScene;
SamplerState sampleruScene;

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
    bool _2499;
    if (!false)
    {
        float3 _2494 = abs(rgb - float3(1.0f, 0.3294117748737335205078125f, 0.3294117748737335205078125f));
        _2499 = all(bool3(_2494.x < 0.001000000047497451305389404296875f.xxx.x, _2494.y < 0.001000000047497451305389404296875f.xxx.y, _2494.z < 0.001000000047497451305389404296875f.xxx.z));
    }
    else
    {
        _2499 = false;
    }
    if (_2499)
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
    return 0.0f;
}

float4 snapshotSample(inout float2 uv)
{
    float2 size = max(floor(_PEVScreenParams.xy / 6.0f.xx), 1.0f.xx);
    uv = (floor(clamp(uv, 0.0f.xx, 1.0f.xx) * size) + 0.5f.xx) / size;
    return uScene.Sample(sampleruScene, uv);
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
        u_xlatb16 = false;
        if (u_xlatb16)
        {
            u_xlat16 = screenPos.xy / screenPos.ww;
            u_xlat16 = (u_xlat1.xy * _DisplaceStrength.xx) + u_xlat16;
            u_xlat16 *= _PEVScreenParams.xy;
            u_xlat16 /= _PEVScreenParams.yy;
            u_xlati1 = 0;
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
        float3 _825 = u_xlat16_35.xxx * u_xlat4.xyz;
        u_xlat16_3.x = _825.x;
        u_xlat16_3.y = _825.y;
        u_xlat16_3.w = _825.z;
        float3 _837 = u_xlat16_3.xyw * _SparkMapOpacity.xxx;
        u_xlat1.x = _837.x;
        u_xlat1.y = _837.y;
        u_xlat1.z = _837.z;
        u_xlatb32 = u_xlat6.y >= u_xlat6.z;
        u_xlat16_5.x = float(u_xlatb32);
        float2 _859 = (-u_xlat6.zy) + u_xlat6.yz;
        u_xlat16_21.x = _859.x;
        u_xlat16_21.y = _859.y;
        u_xlat16_8.x = 1.0f;
        u_xlat16_8.y = -1.0f;
        float2 _876 = (u_xlat16_5.xx * u_xlat16_21.xy) + u_xlat6.zy;
        u_xlat16_4.x = _876.x;
        u_xlat16_4.y = _876.y;
        float2 _888 = (u_xlat16_5.xx * u_xlat16_8.xy) + float2(-1.0f, 0.666666686534881591796875f);
        u_xlat16_4.z = _888.x;
        u_xlat16_4.w = _888.y;
        u_xlatb32 = u_xlat6.x >= u_xlat16_4.x;
        u_xlat16_5.x = float(u_xlatb32);
        float3 _904 = -u_xlat16_4.xyw;
        u_xlat16_7.x = _904.x;
        u_xlat16_7.y = _904.y;
        u_xlat16_7.z = _904.z;
        u_xlat16_7.w = -u_xlat6.x;
        u_xlat16_8.x = u_xlat6.x + u_xlat16_7.x;
        float3 _925 = u_xlat16_4.yzx + u_xlat16_7.yzw;
        u_xlat16_8.y = _925.x;
        u_xlat16_8.z = _925.y;
        u_xlat16_8.w = _925.z;
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
        float _1024;
        if (false)
        {
            _1024 = 1.0f;
        }
        else
        {
            _1024 = ((paletteHigh - paletteLow) / max(paletteHigh, 9.9999997473787516355514526367188e-06f)) / 0.6705882549285888671875f;
        }
        float paletteSaturation = _1024;
        u_xlat16_37 = ((u_xlat1.y * _SparkHueShiftAmount) * paletteSaturation) + u_xlat49;
        u_xlat16_21.x = (u_xlat1.z * _SparkHueShiftAmount) + u_xlat16_21.x;
        float3 _1059 = u_xlat16_5.xxx + float3(1.0f, 0.666666686534881591796875f, 0.3333333432674407958984375f);
        u_xlat16_8.x = _1059.x;
        u_xlat16_8.y = _1059.y;
        u_xlat16_8.z = _1059.z;
        float4 _1066 = u_xlat16_8;
        float3 _1068 = frac(_1066.xyz);
        u_xlat16_8.x = _1068.x;
        u_xlat16_8.y = _1068.y;
        u_xlat16_8.z = _1068.z;
        float4 _1075 = u_xlat16_8;
        float3 _1081 = (_1075.xyz * 6.0f.xxx) + (-3.0f).xxx;
        u_xlat16_8.x = _1081.x;
        u_xlat16_8.y = _1081.y;
        u_xlat16_8.z = _1081.z;
        float4 _1088 = u_xlat16_8;
        float3 _1092 = abs(_1088.xyz) + (-1.0f).xxx;
        u_xlat16_8.x = _1092.x;
        u_xlat16_8.y = _1092.y;
        u_xlat16_8.z = _1092.z;
        float4 _1099 = u_xlat16_8;
        float3 _1103 = clamp(_1099.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat16_8.x = _1103.x;
        u_xlat16_8.y = _1103.y;
        u_xlat16_8.z = _1103.z;
        float4 _1110 = u_xlat16_8;
        float3 _1112 = _1110.xyz + (-1.0f).xxx;
        u_xlat16_8.x = _1112.x;
        u_xlat16_8.y = _1112.y;
        u_xlat16_8.z = _1112.z;
        float3 _1125 = (u_xlat16_37.xxx * u_xlat16_8.xyz) + 1.0f.xxx;
        u_xlat16_5.x = _1125.x;
        u_xlat16_5.z = _1125.y;
        u_xlat16_5.w = _1125.z;
        float3 _1136 = u_xlat16_5.xzw * u_xlat16_21.xxx;
        u_xlat16_8.x = _1136.x;
        u_xlat16_8.y = _1136.y;
        u_xlat16_8.z = _1136.z;
        u_xlat16_9 = u_xlat16_8.xyz + u_xlat16_8.xyz;
        u_xlat16_10 = u_xlat1.xyz * u_xlat16_9;
        float4 _1157 = u_xlat16_5;
        float3 _1160 = ((-u_xlat16_21.xxx) * _1157.xzw) + 1.0f.xxx;
        u_xlat16_5.x = _1160.x;
        u_xlat16_5.y = _1160.y;
        u_xlat16_5.z = _1160.z;
        float4 _1167 = u_xlat16_5;
        float4 _1169 = u_xlat16_5;
        float3 _1171 = _1167.xyz + _1169.xyz;
        u_xlat16_5.x = _1171.x;
        u_xlat16_5.y = _1171.y;
        u_xlat16_5.z = _1171.z;
        float4 _1178 = u_xlat16_3;
        float3 _1184 = ((-_1178.xyw) * _SparkMapOpacity.xxx) + 1.0f.xxx;
        u_xlat16_3.x = _1184.x;
        u_xlat16_3.y = _1184.y;
        u_xlat16_3.w = _1184.z;
        float4 _1194 = u_xlat16_3;
        float3 _1197 = ((-u_xlat16_5.xyz) * _1194.xyw) + 1.0f.xxx;
        u_xlat16_3.x = _1197.x;
        u_xlat16_3.y = _1197.y;
        u_xlat16_3.w = _1197.z;
        u_xlatb6 = bool4(u_xlat16_8.xyzx.x >= float4(0.5f, 0.5f, 0.5f, 0.0f).x, u_xlat16_8.xyzx.y >= float4(0.5f, 0.5f, 0.5f, 0.0f).y, u_xlat16_8.xyzx.z >= float4(0.5f, 0.5f, 0.5f, 0.0f).z, u_xlat16_8.xyzx.w >= float4(0.5f, 0.5f, 0.5f, 0.0f).w).xyz;
        u_xlat6.x = float(u_xlatb6.x);
        u_xlat6.y = float(u_xlatb6.y);
        u_xlat6.z = float(u_xlatb6.z);
        float4 _1225 = u_xlat1;
        float3 _1230 = ((-u_xlat16_9) * _1225.xyz) + u_xlat16_3.xyw;
        u_xlat1.x = _1230.x;
        u_xlat1.y = _1230.y;
        u_xlat1.z = _1230.z;
        float4 _1238 = u_xlat1;
        float3 _1242 = (u_xlat6 * _1238.xyz) + u_xlat16_10;
        u_xlat1.x = _1242.x;
        u_xlat1.y = _1242.y;
        u_xlat1.z = _1242.z;
        float4 _1249 = u_xlat1;
        float3 _1253 = clamp(_1249.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat1.x = _1253.x;
        u_xlat1.y = _1253.y;
        u_xlat1.z = _1253.z;
        float3 param_10 = _FillColor.xyz;
        float3 param_11 = rgb;
        float3 _1274 = ((-u_xlat16_35.xxx) * _DisplaceBlendIntensity.xxx) + blockPalette(param_10, param_11);
        u_xlat16_3.x = _1274.x;
        u_xlat16_3.y = _1274.y;
        u_xlat16_3.z = _1274.z;
        float3 _1286 = u_xlat1.xyz + (-u_xlat16_3.xyz);
        u_xlat16_5.x = _1286.x;
        u_xlat16_5.y = _1286.y;
        u_xlat16_5.z = _1286.z;
        float4 _1299 = u_xlat16_3;
        float3 _1301 = (_FillStrength.xxx * u_xlat16_5.xyz) + _1299.xyz;
        u_xlat16_3.x = _1301.x;
        u_xlat16_3.y = _1301.y;
        u_xlat16_3.z = _1301.z;
        float3 param_12 = _GlowColor.xyz;
        float3 param_13 = rgb;
        float3 _1317 = u_xlat16_50.xxx * blockPalette(param_12, param_13);
        u_xlat16_5.x = _1317.x;
        u_xlat16_5.y = _1317.y;
        u_xlat16_5.z = _1317.z;
        float3 param_14 = _EdgeColor.xyz;
        float3 param_15 = rgb;
        float4 _1334 = u_xlat16_5;
        float3 _1336 = (blockPalette(param_14, param_15) * u_xlat16_2.xxx) + _1334.xyz;
        u_xlat16_5.x = _1336.x;
        u_xlat16_5.y = _1336.y;
        u_xlat16_5.z = _1336.z;
        float4 _1343 = u_xlat16_3;
        float3 _1350 = (_1343.xyz * u_xlat16_0.xxx) + u_xlat16_5.xyz;
        u_xlat16_3.x = _1350.x;
        u_xlat16_3.y = _1350.y;
        u_xlat16_3.z = _1350.z;
        u_xlat16_50 = (u_xlat16.x * _TouchPosShine) + 1.0f;
        float4 _1365 = u_xlat16_3;
        float3 _1367 = u_xlat16_50.xxx * _1365.xyz;
        u_xlat16_3.x = _1367.x;
        u_xlat16_3.y = _1367.y;
        u_xlat16_3.z = _1367.z;
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
    float3 _1404 = _ShineBrightness.xxx * _ShineColor.xyz;
    u_xlat1.x = _1404.x;
    u_xlat1.y = _1404.y;
    u_xlat1.z = _1404.z;
    u_xlat16.x = uUnityTime.y * _ShineSpeed;
    u_xlat16.x = sin(u_xlat16.x);
    u_xlat16.x = (u_xlat16.x * 0.5f) + 1.0f;
    float4 _1428 = u_xlat1;
    float3 _1430 = u_xlat16.xxx * _1428.xyz;
    u_xlat1.x = _1430.x;
    u_xlat1.y = _1430.y;
    u_xlat1.z = _1430.z;
    float4 _1439 = u_xlat1;
    float3 _1441 = u_xlat16_18.xxx * _1439.xyz;
    u_xlat1.x = _1441.x;
    u_xlat1.y = _1441.y;
    u_xlat1.z = _1441.z;
    float3 _1450;
    if (u_xlatb0.x)
    {
        _1450 = u_xlat1.xyz;
    }
    else
    {
        _1450 = 0.0f.xxx;
    }
    u_xlat16_2.x = _1450.x;
    u_xlat16_2.z = _1450.y;
    u_xlat16_2.w = _1450.z;
    u_xlat16_18 = u_xlatb0.x ? u_xlat16_18 : 0.0f;
    u_xlatb0.x = 9.9999997473787516355514526367188e-05f < u_xlat48;
    if (u_xlatb0.x)
    {
        u_xlat0.x = dot(_TouchDisplaceDirection.xy, _TouchDisplaceDirection.xy);
        u_xlat0.x = rsqrt(u_xlat0.x);
        float4 _1486 = u_xlat0;
        float2 _1490 = _1486.xx * _TouchDisplaceDirection.xy;
        u_xlat0.x = _1490.x;
        u_xlat0.y = _1490.y;
        u_xlat1.x = uUnityTime.x * _TouchDisplaceSpeed;
        float2 _1509 = (u_xlat0.xy * u_xlat1.xx) + touchDisplaceUV;
        u_xlat17.x = _1509.x;
        u_xlat17.y = _1509.y;
        u_xlat49 = max(_TouchBackgroundPixelScale, 1.0f);
        float3 _1517 = u_xlat17;
        float2 _1521 = _1517.xy * _PEVScreenParams.xy;
        u_xlat17.x = _1521.x;
        u_xlat17.y = _1521.y;
        float3 _1526 = u_xlat17;
        float2 _1530 = _1526.xy / u_xlat49.xx;
        u_xlat17.x = _1530.x;
        u_xlat17.y = _1530.y;
        float3 _1535 = u_xlat17;
        float2 _1537 = floor(_1535.xy);
        u_xlat17.x = _1537.x;
        u_xlat17.y = _1537.y;
        u_xlat6.x = u_xlat49 * 0.5f;
        float2 _1552 = (u_xlat17.xy * u_xlat49.xx) + u_xlat6.xx;
        u_xlat1.y = _1552.x;
        u_xlat1.z = _1552.y;
        float4 _1557 = u_xlat0;
        float2 _1559 = -_1557.yy;
        u_xlat0.z = _1559.x;
        u_xlat0.w = _1559.y;
        u_xlat22 = (u_xlat0.zx * u_xlat1.xx) + touchDisplaceUV;
        u_xlat22 *= _PEVScreenParams.xy;
        u_xlat22 /= u_xlat49.xx;
        u_xlat22 = floor(u_xlat22);
        float2 _1588 = (u_xlat22 * u_xlat49.xx) + u_xlat6.xx;
        u_xlat1.x = _1588.x;
        u_xlat1.w = _1588.y;
        u_xlat1 /= _PEVScreenParams.xxyy;
        u_xlat16_32 = uDisplaceTex.Sample(sampleruDisplaceTex, u_xlat1.yz).x;
        u_xlat16_1.x = uDisplaceTex.Sample(sampleruDisplaceTex, u_xlat1.xw).x;
        u_xlat16_51 = u_xlat16_32 + (-0.5f);
        u_xlat16_5.x = u_xlat16_1.x + (-0.5f);
        u_xlat32 = u_xlat0.wx * u_xlat16_5.xx;
        float4 _1619 = u_xlat0;
        float2 _1625 = (_1619.xy * u_xlat16_51.xx) + u_xlat32;
        u_xlat0.x = _1625.x;
        u_xlat0.y = _1625.y;
        float4 _1630 = u_xlat0;
        float2 _1637 = (_1630.xy * _TouchDisplaceStrength.xx) + sceneUV;
        u_xlat0.x = _1637.x;
        u_xlat0.y = _1637.y;
        float2 param_16 = u_xlat0.xy;
        u_xlat16_0 = hoverSample(param_16);
        u_xlat16 = screenPos.xy / screenPos.ww;
        u_xlat48 = uUnityTime.y * _NoiseDirChangeSpeed;
        u_xlat1.x = floor(u_xlat48);
        u_xlat48 = frac(u_xlat48);
        u_xlat17.x = u_xlat48 * u_xlat48;
        u_xlat48 = ((-u_xlat48) * 2.0f) + 3.0f;
        u_xlat48 *= u_xlat17.x;
        float2 _1678 = u_xlat1.xx * 0.103100001811981201171875f.xx;
        u_xlat4.x = _1678.x;
        u_xlat4.z = _1678.y;
        u_xlat4.y = 0.381470024585723876953125f;
        u_xlat4.w = 0.938210070133209228515625f;
        u_xlat4 = frac(u_xlat4);
        u_xlat17 = u_xlat4.yxx + 33.3300018310546875f.xxx;
        u_xlat17.x = dot(u_xlat4.xyx, u_xlat17);
        float3 _1699 = u_xlat17;
        float2 _1703 = _1699.xx + u_xlat4.xy;
        u_xlat17.x = _1703.x;
        u_xlat17.y = _1703.y;
        u_xlat33.x = u_xlat17.y + u_xlat17.x;
        u_xlat17.x *= u_xlat33.x;
        u_xlat6.x = frac(u_xlat17.x);
        u_xlat17 = u_xlat4.wzz + 33.3300018310546875f.xxx;
        u_xlat17.x = dot(u_xlat4.zwz, u_xlat17);
        float3 _1732 = u_xlat17;
        float2 _1736 = _1732.xx + u_xlat4.zw;
        u_xlat17.x = _1736.x;
        u_xlat17.y = _1736.y;
        u_xlat33.x = u_xlat17.y + u_xlat17.x;
        u_xlat17.x *= u_xlat33.x;
        u_xlat6.y = frac(u_xlat17.x);
        float2 _1762 = (u_xlat6.xy * 2.0f.xx) + (-1.0f).xx;
        u_xlat17.x = _1762.x;
        u_xlat17.y = _1762.y;
        u_xlat1.x += 1.0f;
        float2 _1773 = u_xlat1.xx * 0.103100001811981201171875f.xx;
        u_xlat4.x = _1773.x;
        u_xlat4.z = _1773.y;
        u_xlat4.y = 0.381470024585723876953125f;
        u_xlat4.w = 0.938210070133209228515625f;
        u_xlat4 = frac(u_xlat4);
        u_xlat6 = u_xlat4.yxx + 33.3300018310546875f.xxx;
        u_xlat1.x = dot(u_xlat4.xyx, u_xlat6);
        float4 _1790 = u_xlat1;
        float2 _1794 = _1790.xx + u_xlat4.xy;
        u_xlat1.x = _1794.x;
        u_xlat1.w = _1794.y;
        u_xlat49 = u_xlat1.w + u_xlat1.x;
        u_xlat1.x *= u_xlat49;
        u_xlat6.x = frac(u_xlat1.x);
        u_xlat11 = u_xlat4.wzz + 33.3300018310546875f.xxx;
        u_xlat1.x = dot(u_xlat4.zwz, u_xlat11);
        float4 _1822 = u_xlat1;
        float2 _1826 = _1822.xx + u_xlat4.zw;
        u_xlat1.x = _1826.x;
        u_xlat1.w = _1826.y;
        u_xlat49 = u_xlat1.w + u_xlat1.x;
        u_xlat1.x *= u_xlat49;
        u_xlat6.y = frac(u_xlat1.x);
        float2 _1848 = (u_xlat6.xy * 2.0f.xx) + (-1.0f).xx;
        u_xlat1.x = _1848.x;
        u_xlat1.w = _1848.y;
        float4 _1856 = u_xlat1;
        float2 _1858 = (-u_xlat17.xy) + _1856.xw;
        u_xlat1.x = _1858.x;
        u_xlat1.w = _1858.y;
        float4 _1865 = u_xlat1;
        float2 _1870 = (u_xlat48.xx * _1865.xw) + u_xlat17.xy;
        u_xlat1.x = _1870.x;
        u_xlat1.y = _1870.y;
        float4 _1875 = u_xlat1;
        float2 _1883 = (_1875.xy * _NoiseEvoSpeed.xx) + noiseUV;
        u_xlat1.x = _1883.x;
        u_xlat1.y = _1883.y;
        u_xlat16_1 = uNoiseTex.Sample(sampleruNoiseTex, u_xlat1.xy).xy;
        float2 _1896 = u_xlat16_1 + (-0.5f).xx;
        u_xlat1.x = _1896.x;
        u_xlat1.y = _1896.y;
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
        float2 _1952 = u_xlat1.xy + u_xlat1.xy;
        u_xlat16_5.x = _1952.x;
        u_xlat16_5.y = _1952.y;
        float2 _1962 = u_xlat16_5.xy * _NoiseDisplaceStrength.xx;
        u_xlat1.x = _1962.x;
        u_xlat1.y = _1962.y;
        float4 _1969 = u_xlat1;
        float2 _1971 = u_xlat33.xx * _1969.xy;
        u_xlat1.x = _1971.x;
        u_xlat1.y = _1971.y;
        float4 _1976 = u_xlat1;
        float2 _1980 = _1976.xy / _PEVScreenParams.xy;
        u_xlat1.x = _1980.x;
        u_xlat1.y = _1980.y;
        u_xlat16 += u_xlat1.xy;
        u_xlat16 *= _PEVScreenParams.xy;
        u_xlat16 /= u_xlat33.xx;
        float2 _1998 = floor(u_xlat16);
        u_xlat1.x = _1998.x;
        u_xlat1.y = _1998.y;
        u_xlat16 = frac(u_xlat16);
        u_xlat33.x = uUnityTime.y * _SDFMoveSpeed;
        u_xlat49 = floor(u_xlat33.x);
        u_xlat33.x = frac(u_xlat33.x);
        u_xlat6.x = u_xlat33.x * u_xlat33.x;
        u_xlat33.x = ((-u_xlat33.x) * 2.0f) + 3.0f;
        u_xlat33.x *= u_xlat6.x;
        float4 _2038 = u_xlat1;
        float2 _2040 = u_xlat49.xx + _2038.xy;
        u_xlat1.x = _2040.x;
        u_xlat1.y = _2040.y;
        float2 _2047 = u_xlat1.xy * 0.103100001811981201171875f.xx;
        u_xlat6.x = _2047.x;
        u_xlat6.y = _2047.y;
        float3 _2052 = u_xlat6;
        float2 _2054 = frac(_2052.xy);
        u_xlat6.x = _2054.x;
        u_xlat6.y = _2054.y;
        u_xlat11 = u_xlat6.yxx + 33.3300018310546875f.xxx;
        u_xlat49 = dot(u_xlat6.xyx, u_xlat11);
        float3 _2068 = u_xlat6;
        float2 _2070 = u_xlat49.xx + _2068.xy;
        u_xlat6.x = _2070.x;
        u_xlat6.y = _2070.y;
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
        float4 _2146 = u_xlat1;
        float2 _2150 = _2146.xy + 18.1700000762939453125f.xx;
        u_xlat1.x = _2150.x;
        u_xlat1.y = _2150.y;
        float4 _2155 = u_xlat1;
        float2 _2157 = _2155.xy * 0.103100001811981201171875f.xx;
        u_xlat1.x = _2157.x;
        u_xlat1.y = _2157.y;
        float4 _2162 = u_xlat1;
        float2 _2164 = frac(_2162.xy);
        u_xlat1.x = _2164.x;
        u_xlat1.y = _2164.y;
        u_xlat12 = u_xlat1.yxx + 33.3300018310546875f.xxx;
        u_xlat49 = dot(u_xlat1.xyx, u_xlat12);
        float4 _2179 = u_xlat1;
        float2 _2181 = u_xlat49.xx + _2179.xy;
        u_xlat1.x = _2181.x;
        u_xlat1.y = _2181.y;
        u_xlat17.x = u_xlat1.y + u_xlat1.x;
        u_xlat1.x *= u_xlat17.x;
        u_xlat11.y = frac(u_xlat1.x);
        float2 _2207 = (-u_xlat6.xy) + u_xlat11.xy;
        u_xlat1.x = _2207.x;
        u_xlat1.y = _2207.y;
        float4 _2214 = u_xlat1;
        float2 _2219 = (u_xlat33.xx * _2214.xy) + u_xlat6.xy;
        u_xlat1.x = _2219.x;
        u_xlat1.y = _2219.y;
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
        float3 _2287 = (u_xlat16.xxx * blockPalette(param_17, param_18)) + u_xlat16.xxx;
        u_xlat16_8.x = _2287.x;
        u_xlat16_8.y = _2287.y;
        u_xlat16_8.z = _2287.z;
        float4 _2294 = u_xlat16_8;
        float3 _2298 = clamp(_2294.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat16_8.x = _2298.x;
        u_xlat16_8.y = _2298.y;
        u_xlat16_8.z = _2298.z;
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
        float4 _2341 = u_xlat16_8;
        float3 _2347 = ((-_2341.xyz) * u_xlat48.xxx) + 1.0f.xxx;
        u_xlat16_8.x = _2347.x;
        u_xlat16_8.y = _2347.y;
        u_xlat16_8.z = _2347.z;
        float4 _2354 = u_xlat16_8;
        float4 _2356 = u_xlat16_8;
        float3 _2358 = _2354.xyz + _2356.xyz;
        u_xlat16_8.x = _2358.x;
        u_xlat16_8.y = _2358.y;
        u_xlat16_8.z = _2358.z;
        float3 param_21 = _TouchGlowColor.xyz;
        float3 param_22 = rgb;
        u_xlat16_15 = ((-u_xlat16_51.xxx) * blockPalette(param_21, param_22)) + 1.0f.xxx;
        float4 _2377 = u_xlat16_8;
        float3 _2382 = ((-_2377.xyz) * u_xlat16_15) + 1.0f.xxx;
        u_xlat16_8.x = _2382.x;
        u_xlat16_8.y = _2382.y;
        u_xlat16_8.z = _2382.z;
        u_xlatb0 = bool4(u_xlat16_9.xyzx.x >= float4(0.5f, 0.5f, 0.5f, 0.0f).x, u_xlat16_9.xyzx.y >= float4(0.5f, 0.5f, 0.5f, 0.0f).y, u_xlat16_9.xyzx.z >= float4(0.5f, 0.5f, 0.5f, 0.0f).z, u_xlat16_9.xyzx.w >= float4(0.5f, 0.5f, 0.5f, 0.0f).w).xyz;
        u_xlat0.x = float(u_xlatb0.x);
        u_xlat0.y = float(u_xlatb0.y);
        u_xlat0.z = float(u_xlatb0.z);
        float3 _2411 = ((-u_xlat16_13) * 2.0f.xxx) + u_xlat16_8.xyz;
        u_xlat1.x = _2411.x;
        u_xlat1.y = _2411.y;
        u_xlat1.z = _2411.z;
        float4 _2418 = u_xlat0;
        float3 _2424 = (_2418.xyz * u_xlat1.xyz) + u_xlat16_14;
        u_xlat0.x = _2424.x;
        u_xlat0.y = _2424.y;
        u_xlat0.z = _2424.z;
        float4 _2431 = u_xlat0;
        float3 _2435 = clamp(_2431.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat0.x = _2435.x;
        u_xlat0.y = _2435.y;
        u_xlat0.z = _2435.z;
        float3 _2447 = (u_xlat16_10 * 0.5f.xxx) + u_xlat0.xyz;
        u_xlat16_8.x = _2447.x;
        u_xlat16_8.y = _2447.y;
        u_xlat16_8.z = _2447.z;
    }
    else
    {
        u_xlat16_8.x = 0.0f;
        u_xlat16_8.y = 0.0f;
        u_xlat16_8.z = 0.0f;
    }
    float4 _2458 = u_xlat16_2;
    float3 _2468 = (_2458.xzw * (u_xlat16_18 + uReadyExtra).xxx) + u_xlat16_3.xyz;
    u_xlat16_2.x = _2468.x;
    u_xlat16_2.y = _2468.y;
    u_xlat16_2.z = _2468.z;
    float3 _2479 = u_xlat16_8.xyz + u_xlat16_2.xyz;
    result.x = _2479.x;
    result.y = _2479.y;
    result.z = _2479.z;
    return result;
}

void frag_main()
{
    if (true)
    {
        float2 param = fieldUV;
        float disabledMask = uMasks.Sample(sampleruMasks, basePixelUV(param)).w;
        float param_1 = disabledMask;
        float4 _2647 = disabledColor(param_1);
        pev_output = _2647 * color;
    }
    else
    {
        float4 _2652 = fullActive();
        pev_output = _2652 * color;
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
