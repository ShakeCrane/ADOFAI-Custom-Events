// Draft translation; not yet validated in Unity or D3D11.
// Phira-Pro c8fb11c6a41faaf1233a5affabf87b0223e2edd8,
// prpr/src/core/block_shader_full.frag, GPL-3.0-or-later.
// Generated with tools/translate_phira_shader.py; do not edit by hand.
Texture2D<float4> uDisabledColors : register(t0);
SamplerState _uDisabledColors_sampler : register(s0);
uniform float4 uUnityTime;
uniform float uDisabledSpeed;
uniform float4 _DisplaceDirection;
Texture2D<float4> uDisplaceTex : register(t0);
SamplerState _uDisplaceTex_sampler : register(s0);
uniform float uDisabledSparkIntensity;
Texture2D<float4> uSparkTex : register(t1);
SamplerState _uSparkTex_sampler : register(s1);
uniform float3 uDisabledSparkTint;
uniform float4 uDisabledFillColor;
uniform float uDisabledFillOpacity;
uniform float uDisabledSparkOpacity;
Texture2D<float4> uActiveColors : register(t0);
SamplerState _uActiveColors_sampler : register(s0);
uniform float4 _EffectRT_TexelSize;
uniform float _EdgeOpacity;
uniform float _GlowIntensity;
uniform float _DisplaceSpeed;
uniform float _BackgroundPixelScale;
uniform float4 _ScreenParams;
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
Texture2D<float4> uNoiseTex : register(t0);
SamplerState _uNoiseTex_sampler : register(s0);
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
Texture2D<float4> uMasks : register(t2);
SamplerState _uMasks_sampler : register(s2);
Texture2D<float4> uAuxMasks : register(t0);
SamplerState _uAuxMasks_sampler : register(s0);
Texture2D<float4> uScene : register(t0);
SamplerState _uScene_sampler : register(s0);

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
        _44 = uDisabledColors.Sample(_uDisabledColors_sampler, fieldUV).xyz;
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
    float u_xlat16_4_1 = uDisplaceTex.Sample(_uDisplaceTex_sampler, u_xlat2.zw).x;
    float u_xlat16_8_1 = uDisplaceTex.Sample(_uDisplaceTex_sampler, u_xlat2.xy).x;
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
    u_xlat16_4_1 = uSparkTex.Sample(_uSparkTex_sampler, u_xlat4_1.xy).x;
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
    float _2469 = uMasks.Sample(_uMasks_sampler, basePixelUV(param)).x;
    return _2469;
}

float2 effectSample(float2 uv)
{
    float2 _2477 = uMasks.Sample(_uMasks_sampler, uv).yz;
    return _2477;
}

float4 auxSample(float2 uv)
{
    float2 param = uv;
    float4 _2487 = uAuxMasks.Sample(_uAuxMasks_sampler, basePixelUV(param));
    return _2487;
}

float hoverSample(float2 uv)
{
    return 0.0f;
}

float4 snapshotSample(inout float2 uv)
{
    float2 size = max(floor(_ScreenParams.xy / 6.0f.xx), 1.0f.xx);
    uv = (floor(clamp(uv, 0.0f.xx, 1.0f.xx) * size) + 0.5f.xx) / size;
    float4 _2516 = uScene.Sample(_uScene_sampler, uv);
    return _2516;
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
        _248 = uActiveColors.Sample(_uActiveColors_sampler, fieldUV).xyz;
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
        float2 _461 = _456.xy * _ScreenParams.xy;
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
        u_xlat33 *= _ScreenParams.xy;
        u_xlat33 /= u_xlat32.xx;
        u_xlat33 = floor(u_xlat33);
        float2 _525 = (u_xlat33 * u_xlat32.xx) + u_xlat4.xx;
        u_xlat1.z = _525.x;
        u_xlat1.w = _525.y;
        u_xlat1 /= _ScreenParams.xyxy;
        u_xlat16_16 = uDisplaceTex.Sample(_uDisplaceTex_sampler, u_xlat1.xy).x;
        u_xlat16_1.x = uDisplaceTex.Sample(_uDisplaceTex_sampler, u_xlat1.zw).x;
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
        u_xlat33 = sceneUV * _ScreenParams.xy;
        u_xlat33 /= u_xlat32.xx;
        u_xlat33 = floor(u_xlat33);
        u_xlat16 = (u_xlat33 * u_xlat32.xx) + u_xlat4.xx;
        u_xlat16 /= _ScreenParams.xy;
        u_xlat16 = (u_xlat1.xy * _DisplaceStrength.xx) + u_xlat16;
        u_xlat33 = (u_xlat1.xy * _SparkDisplaceIntensity.xx) + sparkUV;
        u_xlat16_33 = uSparkTex.Sample(_uSparkTex_sampler, u_xlat33).x;
        float3 _631 = u_xlat16_33.xxx * _SparkTint;
        u_xlat4.x = _631.x;
        u_xlat4.y = _631.y;
        u_xlat4.z = _631.z;
        float2 param_7 = u_xlat16;
        float4 _642 = snapshotSample(param_7);
        u_xlat6 = _642.xyz;
        u_xlatb16 = false;
        if (u_xlatb16)
        {
            u_xlat16 = screenPos.xy / screenPos.ww;
            u_xlat16 = (u_xlat1.xy * _DisplaceStrength.xx) + u_xlat16;
            u_xlat16 *= _ScreenParams.xy;
            u_xlat16 /= _ScreenParams.yy;
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
        float3 _809 = u_xlat16_35.xxx * u_xlat4.xyz;
        u_xlat16_3.x = _809.x;
        u_xlat16_3.y = _809.y;
        u_xlat16_3.w = _809.z;
        float3 _821 = u_xlat16_3.xyw * _SparkMapOpacity.xxx;
        u_xlat1.x = _821.x;
        u_xlat1.y = _821.y;
        u_xlat1.z = _821.z;
        u_xlatb32 = u_xlat6.y >= u_xlat6.z;
        u_xlat16_5.x = float(u_xlatb32);
        float2 _843 = (-u_xlat6.zy) + u_xlat6.yz;
        u_xlat16_21.x = _843.x;
        u_xlat16_21.y = _843.y;
        u_xlat16_8.x = 1.0f;
        u_xlat16_8.y = -1.0f;
        float2 _860 = (u_xlat16_5.xx * u_xlat16_21.xy) + u_xlat6.zy;
        u_xlat16_4.x = _860.x;
        u_xlat16_4.y = _860.y;
        float2 _872 = (u_xlat16_5.xx * u_xlat16_8.xy) + float2(-1.0f, 0.666666686534881591796875f);
        u_xlat16_4.z = _872.x;
        u_xlat16_4.w = _872.y;
        u_xlatb32 = u_xlat6.x >= u_xlat16_4.x;
        u_xlat16_5.x = float(u_xlatb32);
        float3 _888 = -u_xlat16_4.xyw;
        u_xlat16_7.x = _888.x;
        u_xlat16_7.y = _888.y;
        u_xlat16_7.z = _888.z;
        u_xlat16_7.w = -u_xlat6.x;
        u_xlat16_8.x = u_xlat6.x + u_xlat16_7.x;
        float3 _909 = u_xlat16_4.yzx + u_xlat16_7.yzw;
        u_xlat16_8.y = _909.x;
        u_xlat16_8.z = _909.y;
        u_xlat16_8.w = _909.z;
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
        float _1008;
        if (true)
        {
            _1008 = 1.0f;
        }
        else
        {
            _1008 = ((paletteHigh - paletteLow) / max(paletteHigh, 9.9999997473787516355514526367188e-06f)) / 0.6705882549285888671875f;
        }
        float paletteSaturation = _1008;
        u_xlat16_37 = ((u_xlat1.y * _SparkHueShiftAmount) * paletteSaturation) + u_xlat49;
        u_xlat16_21.x = (u_xlat1.z * _SparkHueShiftAmount) + u_xlat16_21.x;
        float3 _1043 = u_xlat16_5.xxx + float3(1.0f, 0.666666686534881591796875f, 0.3333333432674407958984375f);
        u_xlat16_8.x = _1043.x;
        u_xlat16_8.y = _1043.y;
        u_xlat16_8.z = _1043.z;
        float4 _1050 = u_xlat16_8;
        float3 _1052 = frac(_1050.xyz);
        u_xlat16_8.x = _1052.x;
        u_xlat16_8.y = _1052.y;
        u_xlat16_8.z = _1052.z;
        float4 _1059 = u_xlat16_8;
        float3 _1065 = (_1059.xyz * 6.0f.xxx) + (-3.0f).xxx;
        u_xlat16_8.x = _1065.x;
        u_xlat16_8.y = _1065.y;
        u_xlat16_8.z = _1065.z;
        float4 _1072 = u_xlat16_8;
        float3 _1076 = abs(_1072.xyz) + (-1.0f).xxx;
        u_xlat16_8.x = _1076.x;
        u_xlat16_8.y = _1076.y;
        u_xlat16_8.z = _1076.z;
        float4 _1083 = u_xlat16_8;
        float3 _1087 = clamp(_1083.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat16_8.x = _1087.x;
        u_xlat16_8.y = _1087.y;
        u_xlat16_8.z = _1087.z;
        float4 _1094 = u_xlat16_8;
        float3 _1096 = _1094.xyz + (-1.0f).xxx;
        u_xlat16_8.x = _1096.x;
        u_xlat16_8.y = _1096.y;
        u_xlat16_8.z = _1096.z;
        float3 _1109 = (u_xlat16_37.xxx * u_xlat16_8.xyz) + 1.0f.xxx;
        u_xlat16_5.x = _1109.x;
        u_xlat16_5.z = _1109.y;
        u_xlat16_5.w = _1109.z;
        float3 _1120 = u_xlat16_5.xzw * u_xlat16_21.xxx;
        u_xlat16_8.x = _1120.x;
        u_xlat16_8.y = _1120.y;
        u_xlat16_8.z = _1120.z;
        u_xlat16_9 = u_xlat16_8.xyz + u_xlat16_8.xyz;
        u_xlat16_10 = u_xlat1.xyz * u_xlat16_9;
        float4 _1141 = u_xlat16_5;
        float3 _1144 = ((-u_xlat16_21.xxx) * _1141.xzw) + 1.0f.xxx;
        u_xlat16_5.x = _1144.x;
        u_xlat16_5.y = _1144.y;
        u_xlat16_5.z = _1144.z;
        float4 _1151 = u_xlat16_5;
        float4 _1153 = u_xlat16_5;
        float3 _1155 = _1151.xyz + _1153.xyz;
        u_xlat16_5.x = _1155.x;
        u_xlat16_5.y = _1155.y;
        u_xlat16_5.z = _1155.z;
        float4 _1162 = u_xlat16_3;
        float3 _1168 = ((-_1162.xyw) * _SparkMapOpacity.xxx) + 1.0f.xxx;
        u_xlat16_3.x = _1168.x;
        u_xlat16_3.y = _1168.y;
        u_xlat16_3.w = _1168.z;
        float4 _1178 = u_xlat16_3;
        float3 _1181 = ((-u_xlat16_5.xyz) * _1178.xyw) + 1.0f.xxx;
        u_xlat16_3.x = _1181.x;
        u_xlat16_3.y = _1181.y;
        u_xlat16_3.w = _1181.z;
        u_xlatb6 = bool4(u_xlat16_8.xyzx.x >= float4(0.5f, 0.5f, 0.5f, 0.0f).x, u_xlat16_8.xyzx.y >= float4(0.5f, 0.5f, 0.5f, 0.0f).y, u_xlat16_8.xyzx.z >= float4(0.5f, 0.5f, 0.5f, 0.0f).z, u_xlat16_8.xyzx.w >= float4(0.5f, 0.5f, 0.5f, 0.0f).w).xyz;
        u_xlat6.x = float(u_xlatb6.x);
        u_xlat6.y = float(u_xlatb6.y);
        u_xlat6.z = float(u_xlatb6.z);
        float4 _1209 = u_xlat1;
        float3 _1214 = ((-u_xlat16_9) * _1209.xyz) + u_xlat16_3.xyw;
        u_xlat1.x = _1214.x;
        u_xlat1.y = _1214.y;
        u_xlat1.z = _1214.z;
        float4 _1222 = u_xlat1;
        float3 _1226 = (u_xlat6 * _1222.xyz) + u_xlat16_10;
        u_xlat1.x = _1226.x;
        u_xlat1.y = _1226.y;
        u_xlat1.z = _1226.z;
        float4 _1233 = u_xlat1;
        float3 _1237 = clamp(_1233.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat1.x = _1237.x;
        u_xlat1.y = _1237.y;
        u_xlat1.z = _1237.z;
        float3 _1254 = ((-u_xlat16_35.xxx) * _DisplaceBlendIntensity.xxx) + _FillColor.xyz;
        u_xlat16_3.x = _1254.x;
        u_xlat16_3.y = _1254.y;
        u_xlat16_3.z = _1254.z;
        float3 _1266 = u_xlat1.xyz + (-u_xlat16_3.xyz);
        u_xlat16_5.x = _1266.x;
        u_xlat16_5.y = _1266.y;
        u_xlat16_5.z = _1266.z;
        float4 _1279 = u_xlat16_3;
        float3 _1281 = (_FillStrength.xxx * u_xlat16_5.xyz) + _1279.xyz;
        u_xlat16_3.x = _1281.x;
        u_xlat16_3.y = _1281.y;
        u_xlat16_3.z = _1281.z;
        float3 _1293 = u_xlat16_50.xxx * _GlowColor.xyz;
        u_xlat16_5.x = _1293.x;
        u_xlat16_5.y = _1293.y;
        u_xlat16_5.z = _1293.z;
        float4 _1306 = u_xlat16_5;
        float3 _1308 = (_EdgeColor.xyz * u_xlat16_2.xxx) + _1306.xyz;
        u_xlat16_5.x = _1308.x;
        u_xlat16_5.y = _1308.y;
        u_xlat16_5.z = _1308.z;
        float4 _1315 = u_xlat16_3;
        float3 _1322 = (_1315.xyz * u_xlat16_0.xxx) + u_xlat16_5.xyz;
        u_xlat16_3.x = _1322.x;
        u_xlat16_3.y = _1322.y;
        u_xlat16_3.z = _1322.z;
        u_xlat16_50 = (u_xlat16.x * _TouchPosShine) + 1.0f;
        float4 _1337 = u_xlat16_3;
        float3 _1339 = u_xlat16_50.xxx * _1337.xyz;
        u_xlat16_3.x = _1339.x;
        u_xlat16_3.y = _1339.y;
        u_xlat16_3.z = _1339.z;
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
    float3 _1376 = _ShineBrightness.xxx * _ShineColor.xyz;
    u_xlat1.x = _1376.x;
    u_xlat1.y = _1376.y;
    u_xlat1.z = _1376.z;
    u_xlat16.x = uUnityTime.y * _ShineSpeed;
    u_xlat16.x = sin(u_xlat16.x);
    u_xlat16.x = (u_xlat16.x * 0.5f) + 1.0f;
    float4 _1400 = u_xlat1;
    float3 _1402 = u_xlat16.xxx * _1400.xyz;
    u_xlat1.x = _1402.x;
    u_xlat1.y = _1402.y;
    u_xlat1.z = _1402.z;
    float4 _1411 = u_xlat1;
    float3 _1413 = u_xlat16_18.xxx * _1411.xyz;
    u_xlat1.x = _1413.x;
    u_xlat1.y = _1413.y;
    u_xlat1.z = _1413.z;
    float3 _1422;
    if (u_xlatb0.x)
    {
        _1422 = u_xlat1.xyz;
    }
    else
    {
        _1422 = 0.0f.xxx;
    }
    u_xlat16_2.x = _1422.x;
    u_xlat16_2.z = _1422.y;
    u_xlat16_2.w = _1422.z;
    u_xlat16_18 = u_xlatb0.x ? u_xlat16_18 : 0.0f;
    u_xlatb0.x = 9.9999997473787516355514526367188e-05f < u_xlat48;
    if (u_xlatb0.x)
    {
        u_xlat0.x = dot(_TouchDisplaceDirection.xy, _TouchDisplaceDirection.xy);
        u_xlat0.x = rsqrt(u_xlat0.x);
        float4 _1458 = u_xlat0;
        float2 _1462 = _1458.xx * _TouchDisplaceDirection.xy;
        u_xlat0.x = _1462.x;
        u_xlat0.y = _1462.y;
        u_xlat1.x = uUnityTime.x * _TouchDisplaceSpeed;
        float2 _1481 = (u_xlat0.xy * u_xlat1.xx) + touchDisplaceUV;
        u_xlat17.x = _1481.x;
        u_xlat17.y = _1481.y;
        u_xlat49 = max(_TouchBackgroundPixelScale, 1.0f);
        float3 _1489 = u_xlat17;
        float2 _1493 = _1489.xy * _ScreenParams.xy;
        u_xlat17.x = _1493.x;
        u_xlat17.y = _1493.y;
        float3 _1498 = u_xlat17;
        float2 _1502 = _1498.xy / u_xlat49.xx;
        u_xlat17.x = _1502.x;
        u_xlat17.y = _1502.y;
        float3 _1507 = u_xlat17;
        float2 _1509 = floor(_1507.xy);
        u_xlat17.x = _1509.x;
        u_xlat17.y = _1509.y;
        u_xlat6.x = u_xlat49 * 0.5f;
        float2 _1524 = (u_xlat17.xy * u_xlat49.xx) + u_xlat6.xx;
        u_xlat1.y = _1524.x;
        u_xlat1.z = _1524.y;
        float4 _1529 = u_xlat0;
        float2 _1531 = -_1529.yy;
        u_xlat0.z = _1531.x;
        u_xlat0.w = _1531.y;
        u_xlat22 = (u_xlat0.zx * u_xlat1.xx) + touchDisplaceUV;
        u_xlat22 *= _ScreenParams.xy;
        u_xlat22 /= u_xlat49.xx;
        u_xlat22 = floor(u_xlat22);
        float2 _1560 = (u_xlat22 * u_xlat49.xx) + u_xlat6.xx;
        u_xlat1.x = _1560.x;
        u_xlat1.w = _1560.y;
        u_xlat1 /= _ScreenParams.xxyy;
        u_xlat16_32 = uDisplaceTex.Sample(_uDisplaceTex_sampler, u_xlat1.yz).x;
        u_xlat16_1.x = uDisplaceTex.Sample(_uDisplaceTex_sampler, u_xlat1.xw).x;
        u_xlat16_51 = u_xlat16_32 + (-0.5f);
        u_xlat16_5.x = u_xlat16_1.x + (-0.5f);
        u_xlat32 = u_xlat0.wx * u_xlat16_5.xx;
        float4 _1591 = u_xlat0;
        float2 _1597 = (_1591.xy * u_xlat16_51.xx) + u_xlat32;
        u_xlat0.x = _1597.x;
        u_xlat0.y = _1597.y;
        float4 _1602 = u_xlat0;
        float2 _1609 = (_1602.xy * _TouchDisplaceStrength.xx) + sceneUV;
        u_xlat0.x = _1609.x;
        u_xlat0.y = _1609.y;
        float2 param_8 = u_xlat0.xy;
        u_xlat16_0 = hoverSample(param_8);
        u_xlat16 = screenPos.xy / screenPos.ww;
        u_xlat48 = uUnityTime.y * _NoiseDirChangeSpeed;
        u_xlat1.x = floor(u_xlat48);
        u_xlat48 = frac(u_xlat48);
        u_xlat17.x = u_xlat48 * u_xlat48;
        u_xlat48 = ((-u_xlat48) * 2.0f) + 3.0f;
        u_xlat48 *= u_xlat17.x;
        float2 _1650 = u_xlat1.xx * 0.103100001811981201171875f.xx;
        u_xlat4.x = _1650.x;
        u_xlat4.z = _1650.y;
        u_xlat4.y = 0.381470024585723876953125f;
        u_xlat4.w = 0.938210070133209228515625f;
        u_xlat4 = frac(u_xlat4);
        u_xlat17 = u_xlat4.yxx + 33.3300018310546875f.xxx;
        u_xlat17.x = dot(u_xlat4.xyx, u_xlat17);
        float3 _1671 = u_xlat17;
        float2 _1675 = _1671.xx + u_xlat4.xy;
        u_xlat17.x = _1675.x;
        u_xlat17.y = _1675.y;
        u_xlat33.x = u_xlat17.y + u_xlat17.x;
        u_xlat17.x *= u_xlat33.x;
        u_xlat6.x = frac(u_xlat17.x);
        u_xlat17 = u_xlat4.wzz + 33.3300018310546875f.xxx;
        u_xlat17.x = dot(u_xlat4.zwz, u_xlat17);
        float3 _1704 = u_xlat17;
        float2 _1708 = _1704.xx + u_xlat4.zw;
        u_xlat17.x = _1708.x;
        u_xlat17.y = _1708.y;
        u_xlat33.x = u_xlat17.y + u_xlat17.x;
        u_xlat17.x *= u_xlat33.x;
        u_xlat6.y = frac(u_xlat17.x);
        float2 _1734 = (u_xlat6.xy * 2.0f.xx) + (-1.0f).xx;
        u_xlat17.x = _1734.x;
        u_xlat17.y = _1734.y;
        u_xlat1.x += 1.0f;
        float2 _1745 = u_xlat1.xx * 0.103100001811981201171875f.xx;
        u_xlat4.x = _1745.x;
        u_xlat4.z = _1745.y;
        u_xlat4.y = 0.381470024585723876953125f;
        u_xlat4.w = 0.938210070133209228515625f;
        u_xlat4 = frac(u_xlat4);
        u_xlat6 = u_xlat4.yxx + 33.3300018310546875f.xxx;
        u_xlat1.x = dot(u_xlat4.xyx, u_xlat6);
        float4 _1762 = u_xlat1;
        float2 _1766 = _1762.xx + u_xlat4.xy;
        u_xlat1.x = _1766.x;
        u_xlat1.w = _1766.y;
        u_xlat49 = u_xlat1.w + u_xlat1.x;
        u_xlat1.x *= u_xlat49;
        u_xlat6.x = frac(u_xlat1.x);
        u_xlat11 = u_xlat4.wzz + 33.3300018310546875f.xxx;
        u_xlat1.x = dot(u_xlat4.zwz, u_xlat11);
        float4 _1794 = u_xlat1;
        float2 _1798 = _1794.xx + u_xlat4.zw;
        u_xlat1.x = _1798.x;
        u_xlat1.w = _1798.y;
        u_xlat49 = u_xlat1.w + u_xlat1.x;
        u_xlat1.x *= u_xlat49;
        u_xlat6.y = frac(u_xlat1.x);
        float2 _1820 = (u_xlat6.xy * 2.0f.xx) + (-1.0f).xx;
        u_xlat1.x = _1820.x;
        u_xlat1.w = _1820.y;
        float4 _1828 = u_xlat1;
        float2 _1830 = (-u_xlat17.xy) + _1828.xw;
        u_xlat1.x = _1830.x;
        u_xlat1.w = _1830.y;
        float4 _1837 = u_xlat1;
        float2 _1842 = (u_xlat48.xx * _1837.xw) + u_xlat17.xy;
        u_xlat1.x = _1842.x;
        u_xlat1.y = _1842.y;
        float4 _1847 = u_xlat1;
        float2 _1855 = (_1847.xy * _NoiseEvoSpeed.xx) + noiseUV;
        u_xlat1.x = _1855.x;
        u_xlat1.y = _1855.y;
        u_xlat16_1 = uNoiseTex.Sample(_uNoiseTex_sampler, u_xlat1.xy).xy;
        float2 _1868 = u_xlat16_1 + (-0.5f).xx;
        u_xlat1.x = _1868.x;
        u_xlat1.y = _1868.y;
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
        u_xlat33.x = _ScreenParams.y * _SDFCellSize;
        float2 _1924 = u_xlat1.xy + u_xlat1.xy;
        u_xlat16_5.x = _1924.x;
        u_xlat16_5.y = _1924.y;
        float2 _1934 = u_xlat16_5.xy * _NoiseDisplaceStrength.xx;
        u_xlat1.x = _1934.x;
        u_xlat1.y = _1934.y;
        float4 _1941 = u_xlat1;
        float2 _1943 = u_xlat33.xx * _1941.xy;
        u_xlat1.x = _1943.x;
        u_xlat1.y = _1943.y;
        float4 _1948 = u_xlat1;
        float2 _1952 = _1948.xy / _ScreenParams.xy;
        u_xlat1.x = _1952.x;
        u_xlat1.y = _1952.y;
        u_xlat16 += u_xlat1.xy;
        u_xlat16 *= _ScreenParams.xy;
        u_xlat16 /= u_xlat33.xx;
        float2 _1970 = floor(u_xlat16);
        u_xlat1.x = _1970.x;
        u_xlat1.y = _1970.y;
        u_xlat16 = frac(u_xlat16);
        u_xlat33.x = uUnityTime.y * _SDFMoveSpeed;
        u_xlat49 = floor(u_xlat33.x);
        u_xlat33.x = frac(u_xlat33.x);
        u_xlat6.x = u_xlat33.x * u_xlat33.x;
        u_xlat33.x = ((-u_xlat33.x) * 2.0f) + 3.0f;
        u_xlat33.x *= u_xlat6.x;
        float4 _2010 = u_xlat1;
        float2 _2012 = u_xlat49.xx + _2010.xy;
        u_xlat1.x = _2012.x;
        u_xlat1.y = _2012.y;
        float2 _2019 = u_xlat1.xy * 0.103100001811981201171875f.xx;
        u_xlat6.x = _2019.x;
        u_xlat6.y = _2019.y;
        float3 _2024 = u_xlat6;
        float2 _2026 = frac(_2024.xy);
        u_xlat6.x = _2026.x;
        u_xlat6.y = _2026.y;
        u_xlat11 = u_xlat6.yxx + 33.3300018310546875f.xxx;
        u_xlat49 = dot(u_xlat6.xyx, u_xlat11);
        float3 _2040 = u_xlat6;
        float2 _2042 = u_xlat49.xx + _2040.xy;
        u_xlat6.x = _2042.x;
        u_xlat6.y = _2042.y;
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
        float4 _2118 = u_xlat1;
        float2 _2122 = _2118.xy + 18.1700000762939453125f.xx;
        u_xlat1.x = _2122.x;
        u_xlat1.y = _2122.y;
        float4 _2127 = u_xlat1;
        float2 _2129 = _2127.xy * 0.103100001811981201171875f.xx;
        u_xlat1.x = _2129.x;
        u_xlat1.y = _2129.y;
        float4 _2134 = u_xlat1;
        float2 _2136 = frac(_2134.xy);
        u_xlat1.x = _2136.x;
        u_xlat1.y = _2136.y;
        u_xlat12 = u_xlat1.yxx + 33.3300018310546875f.xxx;
        u_xlat49 = dot(u_xlat1.xyx, u_xlat12);
        float4 _2151 = u_xlat1;
        float2 _2153 = u_xlat49.xx + _2151.xy;
        u_xlat1.x = _2153.x;
        u_xlat1.y = _2153.y;
        u_xlat17.x = u_xlat1.y + u_xlat1.x;
        u_xlat1.x *= u_xlat17.x;
        u_xlat11.y = frac(u_xlat1.x);
        float2 _2179 = (-u_xlat6.xy) + u_xlat11.xy;
        u_xlat1.x = _2179.x;
        u_xlat1.y = _2179.y;
        float4 _2186 = u_xlat1;
        float2 _2191 = (u_xlat33.xx * _2186.xy) + u_xlat6.xy;
        u_xlat1.x = _2191.x;
        u_xlat1.y = _2191.y;
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
        float3 _2255 = (u_xlat16.xxx * _NoiseTint.xyz) + u_xlat16.xxx;
        u_xlat16_8.x = _2255.x;
        u_xlat16_8.y = _2255.y;
        u_xlat16_8.z = _2255.z;
        float4 _2262 = u_xlat16_8;
        float3 _2266 = clamp(_2262.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat16_8.x = _2266.x;
        u_xlat16_8.y = _2266.y;
        u_xlat16_8.z = _2266.z;
        u_xlat16_9 = u_xlat48.xxx * u_xlat16_8.xyz;
        u_xlat16_51 = u_xlat16_0;
        u_xlat16_51 = clamp(u_xlat16_51, 0.0f, 1.0f);
        u_xlat16_56 = (u_xlat16_51 * (-2.0f)) + 3.0f;
        u_xlat16_51 *= u_xlat16_51;
        u_xlat16_51 *= u_xlat16_56;
        u_xlat16_10 = u_xlat16_51.xxx * _TouchGlowColor.xyz;
        u_xlat16_13 = u_xlat16_9 * u_xlat16_10;
        u_xlat16_14 = u_xlat16_13 + u_xlat16_13;
        float4 _2305 = u_xlat16_8;
        float3 _2311 = ((-_2305.xyz) * u_xlat48.xxx) + 1.0f.xxx;
        u_xlat16_8.x = _2311.x;
        u_xlat16_8.y = _2311.y;
        u_xlat16_8.z = _2311.z;
        float4 _2318 = u_xlat16_8;
        float4 _2320 = u_xlat16_8;
        float3 _2322 = _2318.xyz + _2320.xyz;
        u_xlat16_8.x = _2322.x;
        u_xlat16_8.y = _2322.y;
        u_xlat16_8.z = _2322.z;
        u_xlat16_15 = ((-u_xlat16_51.xxx) * _TouchGlowColor.xyz) + 1.0f.xxx;
        float4 _2337 = u_xlat16_8;
        float3 _2342 = ((-_2337.xyz) * u_xlat16_15) + 1.0f.xxx;
        u_xlat16_8.x = _2342.x;
        u_xlat16_8.y = _2342.y;
        u_xlat16_8.z = _2342.z;
        u_xlatb0 = bool4(u_xlat16_9.xyzx.x >= float4(0.5f, 0.5f, 0.5f, 0.0f).x, u_xlat16_9.xyzx.y >= float4(0.5f, 0.5f, 0.5f, 0.0f).y, u_xlat16_9.xyzx.z >= float4(0.5f, 0.5f, 0.5f, 0.0f).z, u_xlat16_9.xyzx.w >= float4(0.5f, 0.5f, 0.5f, 0.0f).w).xyz;
        u_xlat0.x = float(u_xlatb0.x);
        u_xlat0.y = float(u_xlatb0.y);
        u_xlat0.z = float(u_xlatb0.z);
        float3 _2371 = ((-u_xlat16_13) * 2.0f.xxx) + u_xlat16_8.xyz;
        u_xlat1.x = _2371.x;
        u_xlat1.y = _2371.y;
        u_xlat1.z = _2371.z;
        float4 _2378 = u_xlat0;
        float3 _2384 = (_2378.xyz * u_xlat1.xyz) + u_xlat16_14;
        u_xlat0.x = _2384.x;
        u_xlat0.y = _2384.y;
        u_xlat0.z = _2384.z;
        float4 _2391 = u_xlat0;
        float3 _2395 = clamp(_2391.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat0.x = _2395.x;
        u_xlat0.y = _2395.y;
        u_xlat0.z = _2395.z;
        float3 _2407 = (u_xlat16_10 * 0.5f.xxx) + u_xlat0.xyz;
        u_xlat16_8.x = _2407.x;
        u_xlat16_8.y = _2407.y;
        u_xlat16_8.z = _2407.z;
    }
    else
    {
        u_xlat16_8.x = 0.0f;
        u_xlat16_8.y = 0.0f;
        u_xlat16_8.z = 0.0f;
    }
    float4 _2418 = u_xlat16_2;
    float3 _2428 = (_2418.xzw * (u_xlat16_18 + uReadyExtra).xxx) + u_xlat16_3.xyz;
    u_xlat16_2.x = _2428.x;
    u_xlat16_2.y = _2428.y;
    u_xlat16_2.z = _2428.z;
    float3 _2439 = u_xlat16_8.xyz + u_xlat16_2.xyz;
    result.x = _2439.x;
    result.y = _2439.y;
    result.z = _2439.z;
    return result;
}

void frag_main()
{
    if (true)
    {
        float2 param = fieldUV;
        float disabledMask = uMasks.Sample(_uMasks_sampler, basePixelUV(param)).w;
        float param_1 = disabledMask;
        float4 _2533 = disabledColor(param_1);
        pev_output = _2533 * color;
    }
    else
    {
        float4 _2538 = fullActive();
        pev_output = _2538 * color;
    }
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
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
