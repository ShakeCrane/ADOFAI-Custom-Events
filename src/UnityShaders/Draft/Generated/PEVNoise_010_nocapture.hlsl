uniform float _PEVTouchOpacity;
uniform float4 _PEVBackground;
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

float4 disabledColor(float mask)
{
    float3 _44;
    if (true)
    {
        _44 = float3(1.0f, 0.3294117748737335205078125f, 0.3294117748737335205078125f);
    }
    else
    {
        _44 = uDisabledColors.Sample(sampleruDisabledColors, fieldUV).xyz;
    }
    float3 rgb = _44;
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
    float3 _102 = u_xlat16_1_1;
    float2 _106 = _102.xx * _DisplaceDirection.xy;
    u_xlat16_1_1.x = _106.x;
    u_xlat16_1_1.y = _106.y;
    float3 _118 = u_xlat4_1.xxx * u_xlat16_1_1.xyx;
    float4 u_xlat2;
    u_xlat2.x = _118.x;
    u_xlat2.y = _118.y;
    u_xlat2.w = _118.z;
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
    float2 _184 = (u_xlat16_1_1.xy * u_xlat16_3_1.xx) + u_xlat2.xy;
    u_xlat4_1.x = _184.x;
    u_xlat4_1.y = _184.y;
    float3 _189 = u_xlat4_1;
    float2 _197 = (_189.xy * uDisabledSparkIntensity.xx) + sparkUV;
    u_xlat4_1.x = _197.x;
    u_xlat4_1.y = _197.y;
    u_xlat16_4_1 = uSparkTex.Sample(sampleruSparkTex, u_xlat4_1.xy).x;
    u_xlat4_1 = u_xlat16_4_1.xxx * uDisabledSparkTint;
    u_xlat4_1 = u_xlat16_13_1.xxx * u_xlat4_1;
    u_xlat16_1_1 = uDisabledFillColor.xyz * uDisabledFillOpacity.xxx;
    u_xlat16_1_1 = (u_xlat4_1 * uDisabledSparkOpacity.xxx) + u_xlat16_1_1;
    float3 _236 = u_xlat16_0_1.xxx * u_xlat16_1_1;
    float4 result;
    result.x = _236.x;
    result.y = _236.y;
    result.z = _236.z;
    result.w = 1.0f;
    return result;
}

float composeSample(float2 uv)
{
    float2 param = uv;
    float _2473 = uMasks.Sample(sampleruMasks, basePixelUV(param)).x;
    return _2473;
}

float2 effectSample(float2 uv)
{
    float2 _2481 = uMasks.Sample(sampleruMasks, uv).yz;
    return _2481;
}

float4 auxSample(float2 uv)
{
    float2 param = uv;
    float4 _2491 = uAuxMasks.Sample(sampleruAuxMasks, basePixelUV(param));
    return _2491;
}

float hoverSample(float2 uv)
{
    float2 size = _EffectRT_TexelSize.zw * 0.5f;
    float _2510 = uAuxMasks.Sample(sampleruAuxMasks, (floor(uv * size) + 0.5f.xx) / size).w;
    return _2510;
}

float4 snapshotSample(inout float2 uv)
{
    float2 size = max(floor(_PEVScreenParams.xy / 6.0f.xx), 1.0f.xx);
    uv = (floor(clamp(uv, 0.0f.xx, 1.0f.xx) * size) + 0.5f.xx) / size;
    float4 _2537 = _PEVBackground;
    return _2537;
}

float4 fullActive()
{
    float3 _248;
    if (true)
    {
        _248 = float3(1.0f, 0.3294117748737335205078125f, 0.3294117748737335205078125f);
    }
    else
    {
        _248 = uActiveColors.Sample(sampleruActiveColors, fieldUV).xyz;
    }
    float3 rgb = _248;
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
        float4 _416 = u_xlat16_3;
        float2 _420 = _416.xx * _DisplaceDirection.xy;
        u_xlat16_3.x = _420.x;
        u_xlat16_3.y = _420.y;
        u_xlat16.x = uUnityTime.x * _DisplaceSpeed;
        u_xlat33.y = u_xlat16.x * u_xlat16_3.x;
        float2 _446 = (u_xlat16_3.xy * u_xlat16.xx) + displaceUV;
        u_xlat1.x = _446.x;
        u_xlat1.y = _446.y;
        u_xlat32.x = max(_BackgroundPixelScale, 1.0f);
        float4 _456 = u_xlat1;
        float2 _461 = _456.xy * _PEVScreenParams.xy;
        u_xlat1.x = _461.x;
        u_xlat1.y = _461.y;
        float4 _466 = u_xlat1;
        float2 _470 = _466.xy / u_xlat32.xx;
        u_xlat1.x = _470.x;
        u_xlat1.y = _470.y;
        float4 _475 = u_xlat1;
        float2 _477 = floor(_475.xy);
        u_xlat1.x = _477.x;
        u_xlat1.y = _477.y;
        u_xlat4.x = u_xlat32.x * 0.5f;
        float4 _487 = u_xlat1;
        float2 _494 = (_487.xy * u_xlat32.xx) + u_xlat4.xx;
        u_xlat1.x = _494.x;
        u_xlat1.y = _494.y;
        u_xlat33.x = u_xlat16.x * (-u_xlat16_3.y);
        u_xlat33 += displaceUV;
        u_xlat33 *= _PEVScreenParams.xy;
        u_xlat33 /= u_xlat32.xx;
        u_xlat33 = floor(u_xlat33);
        float2 _525 = (u_xlat33 * u_xlat32.xx) + u_xlat4.xx;
        u_xlat1.z = _525.x;
        u_xlat1.w = _525.y;
        u_xlat1 /= _PEVScreenParams.xyxy;
        u_xlat16_16 = uDisplaceTex.Sample(sampleruDisplaceTex, u_xlat1.xy).x;
        u_xlat16_1.x = uDisplaceTex.Sample(sampleruDisplaceTex, u_xlat1.zw).x;
        u_xlat16_35 = u_xlat16_16 + u_xlat16_1.x;
        u_xlat16_35 *= 0.5f;
        u_xlat16_51 = u_xlat16_16 + (-0.5f);
        u_xlat16_5.x = u_xlat16_1.x + (-0.5f);
        u_xlat1.x = (-u_xlat16_3.y) * u_xlat16_5.x;
        u_xlat1.y = u_xlat16_3.x * u_xlat16_5.x;
        float4 _578 = u_xlat1;
        float2 _580 = (u_xlat16_3.xy * u_xlat16_51.xx) + _578.xy;
        u_xlat1.x = _580.x;
        u_xlat1.y = _580.y;
        u_xlat33 = sceneUV * _PEVScreenParams.xy;
        u_xlat33 /= u_xlat32.xx;
        u_xlat33 = floor(u_xlat33);
        u_xlat16 = (u_xlat33 * u_xlat32.xx) + u_xlat4.xx;
        u_xlat16 /= _PEVScreenParams.xy;
        u_xlat16 = (u_xlat1.xy * _DisplaceStrength.xx) + u_xlat16;
        u_xlat33 = (u_xlat1.xy * _SparkDisplaceIntensity.xx) + sparkUV;
        u_xlat16_33 = uSparkTex.Sample(sampleruSparkTex, u_xlat33).x;
        float3 _631 = u_xlat16_33.xxx * _SparkTint;
        u_xlat4.x = _631.x;
        u_xlat4.y = _631.y;
        u_xlat4.z = _631.z;
        float2 param_7 = u_xlat16;
        float4 _642 = snapshotSample(param_7);
        u_xlat6 = _642.xyz;
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
        float3 _813 = u_xlat16_35.xxx * u_xlat4.xyz;
        u_xlat16_3.x = _813.x;
        u_xlat16_3.y = _813.y;
        u_xlat16_3.w = _813.z;
        float3 _825 = u_xlat16_3.xyw * _SparkMapOpacity.xxx;
        u_xlat1.x = _825.x;
        u_xlat1.y = _825.y;
        u_xlat1.z = _825.z;
        u_xlatb32 = u_xlat6.y >= u_xlat6.z;
        u_xlat16_5.x = float(u_xlatb32);
        float2 _847 = (-u_xlat6.zy) + u_xlat6.yz;
        u_xlat16_21.x = _847.x;
        u_xlat16_21.y = _847.y;
        u_xlat16_8.x = 1.0f;
        u_xlat16_8.y = -1.0f;
        float2 _864 = (u_xlat16_5.xx * u_xlat16_21.xy) + u_xlat6.zy;
        u_xlat16_4.x = _864.x;
        u_xlat16_4.y = _864.y;
        float2 _876 = (u_xlat16_5.xx * u_xlat16_8.xy) + float2(-1.0f, 0.666666686534881591796875f);
        u_xlat16_4.z = _876.x;
        u_xlat16_4.w = _876.y;
        u_xlatb32 = u_xlat6.x >= u_xlat16_4.x;
        u_xlat16_5.x = float(u_xlatb32);
        float3 _892 = -u_xlat16_4.xyw;
        u_xlat16_7.x = _892.x;
        u_xlat16_7.y = _892.y;
        u_xlat16_7.z = _892.z;
        u_xlat16_7.w = -u_xlat6.x;
        u_xlat16_8.x = u_xlat6.x + u_xlat16_7.x;
        float3 _913 = u_xlat16_4.yzx + u_xlat16_7.yzw;
        u_xlat16_8.y = _913.x;
        u_xlat16_8.z = _913.y;
        u_xlat16_8.w = _913.z;
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
        float _1012;
        if (true)
        {
            _1012 = 1.0f;
        }
        else
        {
            _1012 = ((paletteHigh - paletteLow) / max(paletteHigh, 9.9999997473787516355514526367188e-06f)) / 0.6705882549285888671875f;
        }
        float paletteSaturation = _1012;
        u_xlat16_37 = ((u_xlat1.y * _SparkHueShiftAmount) * paletteSaturation) + u_xlat49;
        u_xlat16_21.x = (u_xlat1.z * _SparkHueShiftAmount) + u_xlat16_21.x;
        float3 _1047 = u_xlat16_5.xxx + float3(1.0f, 0.666666686534881591796875f, 0.3333333432674407958984375f);
        u_xlat16_8.x = _1047.x;
        u_xlat16_8.y = _1047.y;
        u_xlat16_8.z = _1047.z;
        float4 _1054 = u_xlat16_8;
        float3 _1056 = frac(_1054.xyz);
        u_xlat16_8.x = _1056.x;
        u_xlat16_8.y = _1056.y;
        u_xlat16_8.z = _1056.z;
        float4 _1063 = u_xlat16_8;
        float3 _1069 = (_1063.xyz * 6.0f.xxx) + (-3.0f).xxx;
        u_xlat16_8.x = _1069.x;
        u_xlat16_8.y = _1069.y;
        u_xlat16_8.z = _1069.z;
        float4 _1076 = u_xlat16_8;
        float3 _1080 = abs(_1076.xyz) + (-1.0f).xxx;
        u_xlat16_8.x = _1080.x;
        u_xlat16_8.y = _1080.y;
        u_xlat16_8.z = _1080.z;
        float4 _1087 = u_xlat16_8;
        float3 _1091 = clamp(_1087.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat16_8.x = _1091.x;
        u_xlat16_8.y = _1091.y;
        u_xlat16_8.z = _1091.z;
        float4 _1098 = u_xlat16_8;
        float3 _1100 = _1098.xyz + (-1.0f).xxx;
        u_xlat16_8.x = _1100.x;
        u_xlat16_8.y = _1100.y;
        u_xlat16_8.z = _1100.z;
        float3 _1113 = (u_xlat16_37.xxx * u_xlat16_8.xyz) + 1.0f.xxx;
        u_xlat16_5.x = _1113.x;
        u_xlat16_5.z = _1113.y;
        u_xlat16_5.w = _1113.z;
        float3 _1124 = u_xlat16_5.xzw * u_xlat16_21.xxx;
        u_xlat16_8.x = _1124.x;
        u_xlat16_8.y = _1124.y;
        u_xlat16_8.z = _1124.z;
        u_xlat16_9 = u_xlat16_8.xyz + u_xlat16_8.xyz;
        u_xlat16_10 = u_xlat1.xyz * u_xlat16_9;
        float4 _1145 = u_xlat16_5;
        float3 _1148 = ((-u_xlat16_21.xxx) * _1145.xzw) + 1.0f.xxx;
        u_xlat16_5.x = _1148.x;
        u_xlat16_5.y = _1148.y;
        u_xlat16_5.z = _1148.z;
        float4 _1155 = u_xlat16_5;
        float4 _1157 = u_xlat16_5;
        float3 _1159 = _1155.xyz + _1157.xyz;
        u_xlat16_5.x = _1159.x;
        u_xlat16_5.y = _1159.y;
        u_xlat16_5.z = _1159.z;
        float4 _1166 = u_xlat16_3;
        float3 _1172 = ((-_1166.xyw) * _SparkMapOpacity.xxx) + 1.0f.xxx;
        u_xlat16_3.x = _1172.x;
        u_xlat16_3.y = _1172.y;
        u_xlat16_3.w = _1172.z;
        float4 _1182 = u_xlat16_3;
        float3 _1185 = ((-u_xlat16_5.xyz) * _1182.xyw) + 1.0f.xxx;
        u_xlat16_3.x = _1185.x;
        u_xlat16_3.y = _1185.y;
        u_xlat16_3.w = _1185.z;
        u_xlatb6 = bool4(u_xlat16_8.xyzx.x >= float4(0.5f, 0.5f, 0.5f, 0.0f).x, u_xlat16_8.xyzx.y >= float4(0.5f, 0.5f, 0.5f, 0.0f).y, u_xlat16_8.xyzx.z >= float4(0.5f, 0.5f, 0.5f, 0.0f).z, u_xlat16_8.xyzx.w >= float4(0.5f, 0.5f, 0.5f, 0.0f).w).xyz;
        u_xlat6.x = float(u_xlatb6.x);
        u_xlat6.y = float(u_xlatb6.y);
        u_xlat6.z = float(u_xlatb6.z);
        float4 _1213 = u_xlat1;
        float3 _1218 = ((-u_xlat16_9) * _1213.xyz) + u_xlat16_3.xyw;
        u_xlat1.x = _1218.x;
        u_xlat1.y = _1218.y;
        u_xlat1.z = _1218.z;
        float4 _1226 = u_xlat1;
        float3 _1230 = (u_xlat6 * _1226.xyz) + u_xlat16_10;
        u_xlat1.x = _1230.x;
        u_xlat1.y = _1230.y;
        u_xlat1.z = _1230.z;
        float4 _1237 = u_xlat1;
        float3 _1241 = clamp(_1237.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat1.x = _1241.x;
        u_xlat1.y = _1241.y;
        u_xlat1.z = _1241.z;
        float3 _1258 = ((-u_xlat16_35.xxx) * _DisplaceBlendIntensity.xxx) + _FillColor.xyz;
        u_xlat16_3.x = _1258.x;
        u_xlat16_3.y = _1258.y;
        u_xlat16_3.z = _1258.z;
        float3 _1270 = u_xlat1.xyz + (-u_xlat16_3.xyz);
        u_xlat16_5.x = _1270.x;
        u_xlat16_5.y = _1270.y;
        u_xlat16_5.z = _1270.z;
        float4 _1283 = u_xlat16_3;
        float3 _1285 = (_FillStrength.xxx * u_xlat16_5.xyz) + _1283.xyz;
        u_xlat16_3.x = _1285.x;
        u_xlat16_3.y = _1285.y;
        u_xlat16_3.z = _1285.z;
        float3 _1297 = u_xlat16_50.xxx * _GlowColor.xyz;
        u_xlat16_5.x = _1297.x;
        u_xlat16_5.y = _1297.y;
        u_xlat16_5.z = _1297.z;
        float4 _1310 = u_xlat16_5;
        float3 _1312 = (_EdgeColor.xyz * u_xlat16_2.xxx) + _1310.xyz;
        u_xlat16_5.x = _1312.x;
        u_xlat16_5.y = _1312.y;
        u_xlat16_5.z = _1312.z;
        float4 _1319 = u_xlat16_3;
        float3 _1326 = (_1319.xyz * u_xlat16_0.xxx) + u_xlat16_5.xyz;
        u_xlat16_3.x = _1326.x;
        u_xlat16_3.y = _1326.y;
        u_xlat16_3.z = _1326.z;
        u_xlat16_50 = (u_xlat16.x * (_TouchPosShine * (_PEVTouchOpacity == 0.0 ? 1.0 : _PEVTouchOpacity - 1.0))) + 1.0f;
        float4 _1341 = u_xlat16_3;
        float3 _1343 = u_xlat16_50.xxx * _1341.xyz;
        u_xlat16_3.x = _1343.x;
        u_xlat16_3.y = _1343.y;
        u_xlat16_3.z = _1343.z;
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
    float3 _1380 = _ShineBrightness.xxx * _ShineColor.xyz;
    u_xlat1.x = _1380.x;
    u_xlat1.y = _1380.y;
    u_xlat1.z = _1380.z;
    u_xlat16.x = uUnityTime.y * _ShineSpeed;
    u_xlat16.x = sin(u_xlat16.x);
    u_xlat16.x = (u_xlat16.x * 0.5f) + 1.0f;
    float4 _1404 = u_xlat1;
    float3 _1406 = u_xlat16.xxx * _1404.xyz;
    u_xlat1.x = _1406.x;
    u_xlat1.y = _1406.y;
    u_xlat1.z = _1406.z;
    float4 _1415 = u_xlat1;
    float3 _1417 = u_xlat16_18.xxx * _1415.xyz;
    u_xlat1.x = _1417.x;
    u_xlat1.y = _1417.y;
    u_xlat1.z = _1417.z;
    float3 _1426;
    if (u_xlatb0.x)
    {
        _1426 = u_xlat1.xyz;
    }
    else
    {
        _1426 = 0.0f.xxx;
    }
    u_xlat16_2.x = _1426.x;
    u_xlat16_2.z = _1426.y;
    u_xlat16_2.w = _1426.z;
    u_xlat16_18 = u_xlatb0.x ? u_xlat16_18 : 0.0f;
    u_xlatb0.x = 9.9999997473787516355514526367188e-05f < u_xlat48;
    if (u_xlatb0.x)
    {
        u_xlat0.x = dot(_TouchDisplaceDirection.xy, _TouchDisplaceDirection.xy);
        u_xlat0.x = rsqrt(u_xlat0.x);
        float4 _1462 = u_xlat0;
        float2 _1466 = _1462.xx * _TouchDisplaceDirection.xy;
        u_xlat0.x = _1466.x;
        u_xlat0.y = _1466.y;
        u_xlat1.x = uUnityTime.x * _TouchDisplaceSpeed;
        float2 _1485 = (u_xlat0.xy * u_xlat1.xx) + touchDisplaceUV;
        u_xlat17.x = _1485.x;
        u_xlat17.y = _1485.y;
        u_xlat49 = max(_TouchBackgroundPixelScale, 1.0f);
        float3 _1493 = u_xlat17;
        float2 _1497 = _1493.xy * _PEVScreenParams.xy;
        u_xlat17.x = _1497.x;
        u_xlat17.y = _1497.y;
        float3 _1502 = u_xlat17;
        float2 _1506 = _1502.xy / u_xlat49.xx;
        u_xlat17.x = _1506.x;
        u_xlat17.y = _1506.y;
        float3 _1511 = u_xlat17;
        float2 _1513 = floor(_1511.xy);
        u_xlat17.x = _1513.x;
        u_xlat17.y = _1513.y;
        u_xlat6.x = u_xlat49 * 0.5f;
        float2 _1528 = (u_xlat17.xy * u_xlat49.xx) + u_xlat6.xx;
        u_xlat1.y = _1528.x;
        u_xlat1.z = _1528.y;
        float4 _1533 = u_xlat0;
        float2 _1535 = -_1533.yy;
        u_xlat0.z = _1535.x;
        u_xlat0.w = _1535.y;
        u_xlat22 = (u_xlat0.zx * u_xlat1.xx) + touchDisplaceUV;
        u_xlat22 *= _PEVScreenParams.xy;
        u_xlat22 /= u_xlat49.xx;
        u_xlat22 = floor(u_xlat22);
        float2 _1564 = (u_xlat22 * u_xlat49.xx) + u_xlat6.xx;
        u_xlat1.x = _1564.x;
        u_xlat1.w = _1564.y;
        u_xlat1 /= _PEVScreenParams.xxyy;
        u_xlat16_32 = uDisplaceTex.Sample(sampleruDisplaceTex, u_xlat1.yz).x;
        u_xlat16_1.x = uDisplaceTex.Sample(sampleruDisplaceTex, u_xlat1.xw).x;
        u_xlat16_51 = u_xlat16_32 + (-0.5f);
        u_xlat16_5.x = u_xlat16_1.x + (-0.5f);
        u_xlat32 = u_xlat0.wx * u_xlat16_5.xx;
        float4 _1595 = u_xlat0;
        float2 _1601 = (_1595.xy * u_xlat16_51.xx) + u_xlat32;
        u_xlat0.x = _1601.x;
        u_xlat0.y = _1601.y;
        float4 _1606 = u_xlat0;
        float2 _1613 = (_1606.xy * _TouchDisplaceStrength.xx) + sceneUV;
        u_xlat0.x = _1613.x;
        u_xlat0.y = _1613.y;
        float2 param_8 = u_xlat0.xy;
        u_xlat16_0 = hoverSample(param_8);
        u_xlat16 = screenPos.xy / screenPos.ww;
        u_xlat48 = uUnityTime.y * _NoiseDirChangeSpeed;
        u_xlat1.x = floor(u_xlat48);
        u_xlat48 = frac(u_xlat48);
        u_xlat17.x = u_xlat48 * u_xlat48;
        u_xlat48 = ((-u_xlat48) * 2.0f) + 3.0f;
        u_xlat48 *= u_xlat17.x;
        float2 _1654 = u_xlat1.xx * 0.103100001811981201171875f.xx;
        u_xlat4.x = _1654.x;
        u_xlat4.z = _1654.y;
        u_xlat4.y = 0.381470024585723876953125f;
        u_xlat4.w = 0.938210070133209228515625f;
        u_xlat4 = frac(u_xlat4);
        u_xlat17 = u_xlat4.yxx + 33.3300018310546875f.xxx;
        u_xlat17.x = dot(u_xlat4.xyx, u_xlat17);
        float3 _1675 = u_xlat17;
        float2 _1679 = _1675.xx + u_xlat4.xy;
        u_xlat17.x = _1679.x;
        u_xlat17.y = _1679.y;
        u_xlat33.x = u_xlat17.y + u_xlat17.x;
        u_xlat17.x *= u_xlat33.x;
        u_xlat6.x = frac(u_xlat17.x);
        u_xlat17 = u_xlat4.wzz + 33.3300018310546875f.xxx;
        u_xlat17.x = dot(u_xlat4.zwz, u_xlat17);
        float3 _1708 = u_xlat17;
        float2 _1712 = _1708.xx + u_xlat4.zw;
        u_xlat17.x = _1712.x;
        u_xlat17.y = _1712.y;
        u_xlat33.x = u_xlat17.y + u_xlat17.x;
        u_xlat17.x *= u_xlat33.x;
        u_xlat6.y = frac(u_xlat17.x);
        float2 _1738 = (u_xlat6.xy * 2.0f.xx) + (-1.0f).xx;
        u_xlat17.x = _1738.x;
        u_xlat17.y = _1738.y;
        u_xlat1.x += 1.0f;
        float2 _1749 = u_xlat1.xx * 0.103100001811981201171875f.xx;
        u_xlat4.x = _1749.x;
        u_xlat4.z = _1749.y;
        u_xlat4.y = 0.381470024585723876953125f;
        u_xlat4.w = 0.938210070133209228515625f;
        u_xlat4 = frac(u_xlat4);
        u_xlat6 = u_xlat4.yxx + 33.3300018310546875f.xxx;
        u_xlat1.x = dot(u_xlat4.xyx, u_xlat6);
        float4 _1766 = u_xlat1;
        float2 _1770 = _1766.xx + u_xlat4.xy;
        u_xlat1.x = _1770.x;
        u_xlat1.w = _1770.y;
        u_xlat49 = u_xlat1.w + u_xlat1.x;
        u_xlat1.x *= u_xlat49;
        u_xlat6.x = frac(u_xlat1.x);
        u_xlat11 = u_xlat4.wzz + 33.3300018310546875f.xxx;
        u_xlat1.x = dot(u_xlat4.zwz, u_xlat11);
        float4 _1798 = u_xlat1;
        float2 _1802 = _1798.xx + u_xlat4.zw;
        u_xlat1.x = _1802.x;
        u_xlat1.w = _1802.y;
        u_xlat49 = u_xlat1.w + u_xlat1.x;
        u_xlat1.x *= u_xlat49;
        u_xlat6.y = frac(u_xlat1.x);
        float2 _1824 = (u_xlat6.xy * 2.0f.xx) + (-1.0f).xx;
        u_xlat1.x = _1824.x;
        u_xlat1.w = _1824.y;
        float4 _1832 = u_xlat1;
        float2 _1834 = (-u_xlat17.xy) + _1832.xw;
        u_xlat1.x = _1834.x;
        u_xlat1.w = _1834.y;
        float4 _1841 = u_xlat1;
        float2 _1846 = (u_xlat48.xx * _1841.xw) + u_xlat17.xy;
        u_xlat1.x = _1846.x;
        u_xlat1.y = _1846.y;
        float4 _1851 = u_xlat1;
        float2 _1859 = (_1851.xy * _NoiseEvoSpeed.xx) + noiseUV;
        u_xlat1.x = _1859.x;
        u_xlat1.y = _1859.y;
        u_xlat16_1 = uNoiseTex.Sample(sampleruNoiseTex, u_xlat1.xy).xy;
        float2 _1872 = u_xlat16_1 + (-0.5f).xx;
        u_xlat1.x = _1872.x;
        u_xlat1.y = _1872.y;
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
        float2 _1928 = u_xlat1.xy + u_xlat1.xy;
        u_xlat16_5.x = _1928.x;
        u_xlat16_5.y = _1928.y;
        float2 _1938 = u_xlat16_5.xy * _NoiseDisplaceStrength.xx;
        u_xlat1.x = _1938.x;
        u_xlat1.y = _1938.y;
        float4 _1945 = u_xlat1;
        float2 _1947 = u_xlat33.xx * _1945.xy;
        u_xlat1.x = _1947.x;
        u_xlat1.y = _1947.y;
        float4 _1952 = u_xlat1;
        float2 _1956 = _1952.xy / _PEVScreenParams.xy;
        u_xlat1.x = _1956.x;
        u_xlat1.y = _1956.y;
        u_xlat16 += u_xlat1.xy;
        u_xlat16 *= _PEVScreenParams.xy;
        u_xlat16 /= u_xlat33.xx;
        float2 _1974 = floor(u_xlat16);
        u_xlat1.x = _1974.x;
        u_xlat1.y = _1974.y;
        u_xlat16 = frac(u_xlat16);
        u_xlat33.x = uUnityTime.y * _SDFMoveSpeed;
        u_xlat49 = floor(u_xlat33.x);
        u_xlat33.x = frac(u_xlat33.x);
        u_xlat6.x = u_xlat33.x * u_xlat33.x;
        u_xlat33.x = ((-u_xlat33.x) * 2.0f) + 3.0f;
        u_xlat33.x *= u_xlat6.x;
        float4 _2014 = u_xlat1;
        float2 _2016 = u_xlat49.xx + _2014.xy;
        u_xlat1.x = _2016.x;
        u_xlat1.y = _2016.y;
        float2 _2023 = u_xlat1.xy * 0.103100001811981201171875f.xx;
        u_xlat6.x = _2023.x;
        u_xlat6.y = _2023.y;
        float3 _2028 = u_xlat6;
        float2 _2030 = frac(_2028.xy);
        u_xlat6.x = _2030.x;
        u_xlat6.y = _2030.y;
        u_xlat11 = u_xlat6.yxx + 33.3300018310546875f.xxx;
        u_xlat49 = dot(u_xlat6.xyx, u_xlat11);
        float3 _2044 = u_xlat6;
        float2 _2046 = u_xlat49.xx + _2044.xy;
        u_xlat6.x = _2046.x;
        u_xlat6.y = _2046.y;
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
        float4 _2122 = u_xlat1;
        float2 _2126 = _2122.xy + 18.1700000762939453125f.xx;
        u_xlat1.x = _2126.x;
        u_xlat1.y = _2126.y;
        float4 _2131 = u_xlat1;
        float2 _2133 = _2131.xy * 0.103100001811981201171875f.xx;
        u_xlat1.x = _2133.x;
        u_xlat1.y = _2133.y;
        float4 _2138 = u_xlat1;
        float2 _2140 = frac(_2138.xy);
        u_xlat1.x = _2140.x;
        u_xlat1.y = _2140.y;
        u_xlat12 = u_xlat1.yxx + 33.3300018310546875f.xxx;
        u_xlat49 = dot(u_xlat1.xyx, u_xlat12);
        float4 _2155 = u_xlat1;
        float2 _2157 = u_xlat49.xx + _2155.xy;
        u_xlat1.x = _2157.x;
        u_xlat1.y = _2157.y;
        u_xlat17.x = u_xlat1.y + u_xlat1.x;
        u_xlat1.x *= u_xlat17.x;
        u_xlat11.y = frac(u_xlat1.x);
        float2 _2183 = (-u_xlat6.xy) + u_xlat11.xy;
        u_xlat1.x = _2183.x;
        u_xlat1.y = _2183.y;
        float4 _2190 = u_xlat1;
        float2 _2195 = (u_xlat33.xx * _2190.xy) + u_xlat6.xy;
        u_xlat1.x = _2195.x;
        u_xlat1.y = _2195.y;
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
        float3 _2259 = (u_xlat16.xxx * _NoiseTint.xyz) + u_xlat16.xxx;
        u_xlat16_8.x = _2259.x;
        u_xlat16_8.y = _2259.y;
        u_xlat16_8.z = _2259.z;
        float4 _2266 = u_xlat16_8;
        float3 _2270 = clamp(_2266.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat16_8.x = _2270.x;
        u_xlat16_8.y = _2270.y;
        u_xlat16_8.z = _2270.z;
        u_xlat16_9 = u_xlat48.xxx * u_xlat16_8.xyz;
        u_xlat16_51 = u_xlat16_0;
        u_xlat16_51 = clamp(u_xlat16_51, 0.0f, 1.0f);
        u_xlat16_56 = (u_xlat16_51 * (-2.0f)) + 3.0f;
        u_xlat16_51 *= u_xlat16_51;
        u_xlat16_51 *= u_xlat16_56;
        u_xlat16_10 = u_xlat16_51.xxx * _TouchGlowColor.xyz;
        u_xlat16_13 = u_xlat16_9 * u_xlat16_10;
        u_xlat16_14 = u_xlat16_13 + u_xlat16_13;
        float4 _2309 = u_xlat16_8;
        float3 _2315 = ((-_2309.xyz) * u_xlat48.xxx) + 1.0f.xxx;
        u_xlat16_8.x = _2315.x;
        u_xlat16_8.y = _2315.y;
        u_xlat16_8.z = _2315.z;
        float4 _2322 = u_xlat16_8;
        float4 _2324 = u_xlat16_8;
        float3 _2326 = _2322.xyz + _2324.xyz;
        u_xlat16_8.x = _2326.x;
        u_xlat16_8.y = _2326.y;
        u_xlat16_8.z = _2326.z;
        u_xlat16_15 = ((-u_xlat16_51.xxx) * _TouchGlowColor.xyz) + 1.0f.xxx;
        float4 _2341 = u_xlat16_8;
        float3 _2346 = ((-_2341.xyz) * u_xlat16_15) + 1.0f.xxx;
        u_xlat16_8.x = _2346.x;
        u_xlat16_8.y = _2346.y;
        u_xlat16_8.z = _2346.z;
        u_xlatb0 = bool4(u_xlat16_9.xyzx.x >= float4(0.5f, 0.5f, 0.5f, 0.0f).x, u_xlat16_9.xyzx.y >= float4(0.5f, 0.5f, 0.5f, 0.0f).y, u_xlat16_9.xyzx.z >= float4(0.5f, 0.5f, 0.5f, 0.0f).z, u_xlat16_9.xyzx.w >= float4(0.5f, 0.5f, 0.5f, 0.0f).w).xyz;
        u_xlat0.x = float(u_xlatb0.x);
        u_xlat0.y = float(u_xlatb0.y);
        u_xlat0.z = float(u_xlatb0.z);
        float3 _2375 = ((-u_xlat16_13) * 2.0f.xxx) + u_xlat16_8.xyz;
        u_xlat1.x = _2375.x;
        u_xlat1.y = _2375.y;
        u_xlat1.z = _2375.z;
        float4 _2382 = u_xlat0;
        float3 _2388 = (_2382.xyz * u_xlat1.xyz) + u_xlat16_14;
        u_xlat0.x = _2388.x;
        u_xlat0.y = _2388.y;
        u_xlat0.z = _2388.z;
        float4 _2395 = u_xlat0;
        float3 _2399 = clamp(_2395.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat0.x = _2399.x;
        u_xlat0.y = _2399.y;
        u_xlat0.z = _2399.z;
        float3 _2411 = (u_xlat16_10 * 0.5f.xxx) + u_xlat0.xyz;
        u_xlat16_8.x = _2411.x;
        u_xlat16_8.y = _2411.y;
        u_xlat16_8.z = _2411.z;
    }
    else
    {
        u_xlat16_8.x = 0.0f;
        u_xlat16_8.y = 0.0f;
        u_xlat16_8.z = 0.0f;
    }
    float4 _2422 = u_xlat16_2;
    float3 _2432 = (_2422.xzw * (u_xlat16_18 + uReadyExtra).xxx) + u_xlat16_3.xyz;
    u_xlat16_2.x = _2432.x;
    u_xlat16_2.y = _2432.y;
    u_xlat16_2.z = _2432.z;
    float3 _2443 = u_xlat16_8.xyz + u_xlat16_2.xyz;
    result.x = _2443.x;
    result.y = _2443.y;
    result.z = _2443.z;
    return result;
}

void frag_main()
{
    if (false)
    {
        float2 param = fieldUV;
        float disabledMask = uMasks.Sample(sampleruMasks, basePixelUV(param)).w;
        float param_1 = disabledMask;
        float4 _2555 = disabledColor(param_1);
        pev_output = _2555 * color;
    }
    else
    {
        float4 _2560 = fullActive();
        pev_output = _2560 * color;
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
