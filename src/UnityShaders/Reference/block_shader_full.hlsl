// Draft translation; not yet validated in Unity or D3D11.
// Phira-Pro c8fb11c6a41faaf1233a5affabf87b0223e2edd8,
// prpr/src/core/block_shader_full.frag, GPL-3.0-or-later.
// Generated with tools/translate_phira_shader.py; do not edit by hand.
uniform int uColored;
Texture2D<float4> uDisabledColors : register(t1);
SamplerState _uDisabledColors_sampler : register(s1);
uniform float4 uUnityTime;
uniform float uDisabledSpeed;
uniform float4 _DisplaceDirection;
Texture2D<float4> uDisplaceTex : register(t2);
SamplerState _uDisplaceTex_sampler : register(s2);
uniform float uDisabledSparkIntensity;
Texture2D<float4> uSparkTex : register(t3);
SamplerState _uSparkTex_sampler : register(s3);
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
Texture2D<float4> uNoiseTex : register(t7);
SamplerState _uNoiseTex_sampler : register(s7);
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
Texture2D<float4> uMasks : register(t4);
SamplerState _uMasks_sampler : register(s4);
Texture2D<float4> uAuxMasks : register(t6);
SamplerState _uAuxMasks_sampler : register(s6);
Texture2D<float4> uScene : register(t5);
SamplerState _uScene_sampler : register(s5);
uniform int uLayer;

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

float3 blockPalette(float3 nativeColor, float3 rgb)
{
    bool _2501 = uColored == 0;
    bool _2512;
    if (!_2501)
    {
        float3 _2507 = abs(rgb - float3(1.0f, 0.3294117748737335205078125f, 0.3294117748737335205078125f));
        _2512 = all(bool3(_2507.x < 0.001000000047497451305389404296875f.xxx.x, _2507.y < 0.001000000047497451305389404296875f.xxx.y, _2507.z < 0.001000000047497451305389404296875f.xxx.z));
    }
    else
    {
        _2512 = _2501;
    }
    if (_2512)
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
    float3 _54;
    if (uColored == 0)
    {
        _54 = float3(1.0f, 0.3294117748737335205078125f, 0.3294117748737335205078125f);
    }
    else
    {
        _54 = uDisabledColors.Sample(_uDisabledColors_sampler, fieldUV).xyz;
    }
    float3 rgb = _54;
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
    float3 _112 = u_xlat16_1_1;
    float2 _116 = _112.xx * _DisplaceDirection.xy;
    u_xlat16_1_1.x = _116.x;
    u_xlat16_1_1.y = _116.y;
    float3 _128 = u_xlat4_1.xxx * u_xlat16_1_1.xyx;
    float4 u_xlat2;
    u_xlat2.x = _128.x;
    u_xlat2.y = _128.y;
    u_xlat2.w = _128.z;
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
    float2 _194 = (u_xlat16_1_1.xy * u_xlat16_3_1.xx) + u_xlat2.xy;
    u_xlat4_1.x = _194.x;
    u_xlat4_1.y = _194.y;
    float3 _199 = u_xlat4_1;
    float2 _207 = (_199.xy * uDisabledSparkIntensity.xx) + sparkUV;
    u_xlat4_1.x = _207.x;
    u_xlat4_1.y = _207.y;
    u_xlat16_4_1 = uSparkTex.Sample(_uSparkTex_sampler, u_xlat4_1.xy).x;
    float3 param = uDisabledSparkTint;
    float3 param_1 = rgb;
    u_xlat4_1 = u_xlat16_4_1.xxx * blockPalette(param, param_1);
    u_xlat4_1 = u_xlat16_13_1.xxx * u_xlat4_1;
    float3 param_2 = uDisabledFillColor.xyz;
    float3 param_3 = rgb;
    u_xlat16_1_1 = blockPalette(param_2, param_3) * uDisabledFillOpacity.xxx;
    u_xlat16_1_1 = (u_xlat4_1 * uDisabledSparkOpacity.xxx) + u_xlat16_1_1;
    float3 _254 = u_xlat16_0_1.xxx * u_xlat16_1_1;
    float4 result;
    result.x = _254.x;
    result.y = _254.y;
    result.z = _254.z;
    result.w = 1.0f;
    return result;
}

float composeSample(float2 uv)
{
    float2 param = uv;
    float _2603 = uMasks.Sample(_uMasks_sampler, basePixelUV(param)).x;
    return _2603;
}

float2 effectSample(float2 uv)
{
    float2 _2611 = uMasks.Sample(_uMasks_sampler, uv).yz;
    return _2611;
}

float4 auxSample(float2 uv)
{
    float2 param = uv;
    float4 _2621 = uAuxMasks.Sample(_uAuxMasks_sampler, basePixelUV(param));
    return _2621;
}

float hoverSample(float2 uv)
{
    float2 size = _EffectRT_TexelSize.zw * 0.5f;
    float _2640 = uAuxMasks.Sample(_uAuxMasks_sampler, (floor(uv * size) + 0.5f.xx) / size).w;
    return _2640;
}

float4 snapshotSample(inout float2 uv)
{
    float2 size = max(floor(_ScreenParams.xy / 6.0f.xx), 1.0f.xx);
    uv = (floor(clamp(uv, 0.0f.xx, 1.0f.xx) * size) + 0.5f.xx) / size;
    float4 _2667 = uScene.Sample(_uScene_sampler, uv);
    return _2667;
}

float4 fullActive()
{
    float3 _268;
    if (uColored == 0)
    {
        _268 = float3(1.0f, 0.3294117748737335205078125f, 0.3294117748737335205078125f);
    }
    else
    {
        _268 = uActiveColors.Sample(_uActiveColors_sampler, fieldUV).xyz;
    }
    float3 rgb = _268;
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
        float4 _436 = u_xlat16_3;
        float2 _440 = _436.xx * _DisplaceDirection.xy;
        u_xlat16_3.x = _440.x;
        u_xlat16_3.y = _440.y;
        u_xlat16.x = uUnityTime.x * _DisplaceSpeed;
        u_xlat33.y = u_xlat16.x * u_xlat16_3.x;
        float2 _466 = (u_xlat16_3.xy * u_xlat16.xx) + displaceUV;
        u_xlat1.x = _466.x;
        u_xlat1.y = _466.y;
        u_xlat32.x = max(_BackgroundPixelScale, 1.0f);
        float4 _476 = u_xlat1;
        float2 _481 = _476.xy * _ScreenParams.xy;
        u_xlat1.x = _481.x;
        u_xlat1.y = _481.y;
        float4 _486 = u_xlat1;
        float2 _490 = _486.xy / u_xlat32.xx;
        u_xlat1.x = _490.x;
        u_xlat1.y = _490.y;
        float4 _495 = u_xlat1;
        float2 _497 = floor(_495.xy);
        u_xlat1.x = _497.x;
        u_xlat1.y = _497.y;
        u_xlat4.x = u_xlat32.x * 0.5f;
        float4 _507 = u_xlat1;
        float2 _514 = (_507.xy * u_xlat32.xx) + u_xlat4.xx;
        u_xlat1.x = _514.x;
        u_xlat1.y = _514.y;
        u_xlat33.x = u_xlat16.x * (-u_xlat16_3.y);
        u_xlat33 += displaceUV;
        u_xlat33 *= _ScreenParams.xy;
        u_xlat33 /= u_xlat32.xx;
        u_xlat33 = floor(u_xlat33);
        float2 _545 = (u_xlat33 * u_xlat32.xx) + u_xlat4.xx;
        u_xlat1.z = _545.x;
        u_xlat1.w = _545.y;
        u_xlat1 /= _ScreenParams.xyxy;
        u_xlat16_16 = uDisplaceTex.Sample(_uDisplaceTex_sampler, u_xlat1.xy).x;
        u_xlat16_1.x = uDisplaceTex.Sample(_uDisplaceTex_sampler, u_xlat1.zw).x;
        u_xlat16_35 = u_xlat16_16 + u_xlat16_1.x;
        u_xlat16_35 *= 0.5f;
        u_xlat16_51 = u_xlat16_16 + (-0.5f);
        u_xlat16_5.x = u_xlat16_1.x + (-0.5f);
        u_xlat1.x = (-u_xlat16_3.y) * u_xlat16_5.x;
        u_xlat1.y = u_xlat16_3.x * u_xlat16_5.x;
        float4 _598 = u_xlat1;
        float2 _600 = (u_xlat16_3.xy * u_xlat16_51.xx) + _598.xy;
        u_xlat1.x = _600.x;
        u_xlat1.y = _600.y;
        u_xlat33 = sceneUV * _ScreenParams.xy;
        u_xlat33 /= u_xlat32.xx;
        u_xlat33 = floor(u_xlat33);
        u_xlat16 = (u_xlat33 * u_xlat32.xx) + u_xlat4.xx;
        u_xlat16 /= _ScreenParams.xy;
        u_xlat16 = (u_xlat1.xy * _DisplaceStrength.xx) + u_xlat16;
        u_xlat33 = (u_xlat1.xy * _SparkDisplaceIntensity.xx) + sparkUV;
        u_xlat16_33 = uSparkTex.Sample(_uSparkTex_sampler, u_xlat33).x;
        float3 param_7 = _SparkTint;
        float3 param_8 = rgb;
        float3 _655 = u_xlat16_33.xxx * blockPalette(param_7, param_8);
        u_xlat4.x = _655.x;
        u_xlat4.y = _655.y;
        u_xlat4.z = _655.z;
        float2 param_9 = u_xlat16;
        float4 _666 = snapshotSample(param_9);
        u_xlat6 = _666.xyz;
        u_xlatb16 = 0 < _TouchPosCount;
        if (u_xlatb16)
        {
            u_xlat16 = screenPos.xy / screenPos.ww;
            u_xlat16 = (u_xlat1.xy * _DisplaceStrength.xx) + u_xlat16;
            u_xlat16 *= _ScreenParams.xy;
            u_xlat16 /= _ScreenParams.yy;
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
        float3 _834 = u_xlat16_35.xxx * u_xlat4.xyz;
        u_xlat16_3.x = _834.x;
        u_xlat16_3.y = _834.y;
        u_xlat16_3.w = _834.z;
        float3 _846 = u_xlat16_3.xyw * _SparkMapOpacity.xxx;
        u_xlat1.x = _846.x;
        u_xlat1.y = _846.y;
        u_xlat1.z = _846.z;
        u_xlatb32 = u_xlat6.y >= u_xlat6.z;
        u_xlat16_5.x = float(u_xlatb32);
        float2 _868 = (-u_xlat6.zy) + u_xlat6.yz;
        u_xlat16_21.x = _868.x;
        u_xlat16_21.y = _868.y;
        u_xlat16_8.x = 1.0f;
        u_xlat16_8.y = -1.0f;
        float2 _885 = (u_xlat16_5.xx * u_xlat16_21.xy) + u_xlat6.zy;
        u_xlat16_4.x = _885.x;
        u_xlat16_4.y = _885.y;
        float2 _897 = (u_xlat16_5.xx * u_xlat16_8.xy) + float2(-1.0f, 0.666666686534881591796875f);
        u_xlat16_4.z = _897.x;
        u_xlat16_4.w = _897.y;
        u_xlatb32 = u_xlat6.x >= u_xlat16_4.x;
        u_xlat16_5.x = float(u_xlatb32);
        float3 _913 = -u_xlat16_4.xyw;
        u_xlat16_7.x = _913.x;
        u_xlat16_7.y = _913.y;
        u_xlat16_7.z = _913.z;
        u_xlat16_7.w = -u_xlat6.x;
        u_xlat16_8.x = u_xlat6.x + u_xlat16_7.x;
        float3 _934 = u_xlat16_4.yzx + u_xlat16_7.yzw;
        u_xlat16_8.y = _934.x;
        u_xlat16_8.z = _934.y;
        u_xlat16_8.w = _934.z;
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
        float _1035;
        if (uColored == 0)
        {
            _1035 = 1.0f;
        }
        else
        {
            _1035 = ((paletteHigh - paletteLow) / max(paletteHigh, 9.9999997473787516355514526367188e-06f)) / 0.6705882549285888671875f;
        }
        float paletteSaturation = _1035;
        u_xlat16_37 = ((u_xlat1.y * _SparkHueShiftAmount) * paletteSaturation) + u_xlat49;
        u_xlat16_21.x = (u_xlat1.z * _SparkHueShiftAmount) + u_xlat16_21.x;
        float3 _1070 = u_xlat16_5.xxx + float3(1.0f, 0.666666686534881591796875f, 0.3333333432674407958984375f);
        u_xlat16_8.x = _1070.x;
        u_xlat16_8.y = _1070.y;
        u_xlat16_8.z = _1070.z;
        float4 _1077 = u_xlat16_8;
        float3 _1079 = frac(_1077.xyz);
        u_xlat16_8.x = _1079.x;
        u_xlat16_8.y = _1079.y;
        u_xlat16_8.z = _1079.z;
        float4 _1086 = u_xlat16_8;
        float3 _1092 = (_1086.xyz * 6.0f.xxx) + (-3.0f).xxx;
        u_xlat16_8.x = _1092.x;
        u_xlat16_8.y = _1092.y;
        u_xlat16_8.z = _1092.z;
        float4 _1099 = u_xlat16_8;
        float3 _1103 = abs(_1099.xyz) + (-1.0f).xxx;
        u_xlat16_8.x = _1103.x;
        u_xlat16_8.y = _1103.y;
        u_xlat16_8.z = _1103.z;
        float4 _1110 = u_xlat16_8;
        float3 _1114 = clamp(_1110.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat16_8.x = _1114.x;
        u_xlat16_8.y = _1114.y;
        u_xlat16_8.z = _1114.z;
        float4 _1121 = u_xlat16_8;
        float3 _1123 = _1121.xyz + (-1.0f).xxx;
        u_xlat16_8.x = _1123.x;
        u_xlat16_8.y = _1123.y;
        u_xlat16_8.z = _1123.z;
        float3 _1136 = (u_xlat16_37.xxx * u_xlat16_8.xyz) + 1.0f.xxx;
        u_xlat16_5.x = _1136.x;
        u_xlat16_5.z = _1136.y;
        u_xlat16_5.w = _1136.z;
        float3 _1147 = u_xlat16_5.xzw * u_xlat16_21.xxx;
        u_xlat16_8.x = _1147.x;
        u_xlat16_8.y = _1147.y;
        u_xlat16_8.z = _1147.z;
        u_xlat16_9 = u_xlat16_8.xyz + u_xlat16_8.xyz;
        u_xlat16_10 = u_xlat1.xyz * u_xlat16_9;
        float4 _1168 = u_xlat16_5;
        float3 _1171 = ((-u_xlat16_21.xxx) * _1168.xzw) + 1.0f.xxx;
        u_xlat16_5.x = _1171.x;
        u_xlat16_5.y = _1171.y;
        u_xlat16_5.z = _1171.z;
        float4 _1178 = u_xlat16_5;
        float4 _1180 = u_xlat16_5;
        float3 _1182 = _1178.xyz + _1180.xyz;
        u_xlat16_5.x = _1182.x;
        u_xlat16_5.y = _1182.y;
        u_xlat16_5.z = _1182.z;
        float4 _1189 = u_xlat16_3;
        float3 _1195 = ((-_1189.xyw) * _SparkMapOpacity.xxx) + 1.0f.xxx;
        u_xlat16_3.x = _1195.x;
        u_xlat16_3.y = _1195.y;
        u_xlat16_3.w = _1195.z;
        float4 _1205 = u_xlat16_3;
        float3 _1208 = ((-u_xlat16_5.xyz) * _1205.xyw) + 1.0f.xxx;
        u_xlat16_3.x = _1208.x;
        u_xlat16_3.y = _1208.y;
        u_xlat16_3.w = _1208.z;
        u_xlatb6 = bool4(u_xlat16_8.xyzx.x >= float4(0.5f, 0.5f, 0.5f, 0.0f).x, u_xlat16_8.xyzx.y >= float4(0.5f, 0.5f, 0.5f, 0.0f).y, u_xlat16_8.xyzx.z >= float4(0.5f, 0.5f, 0.5f, 0.0f).z, u_xlat16_8.xyzx.w >= float4(0.5f, 0.5f, 0.5f, 0.0f).w).xyz;
        u_xlat6.x = float(u_xlatb6.x);
        u_xlat6.y = float(u_xlatb6.y);
        u_xlat6.z = float(u_xlatb6.z);
        float4 _1236 = u_xlat1;
        float3 _1241 = ((-u_xlat16_9) * _1236.xyz) + u_xlat16_3.xyw;
        u_xlat1.x = _1241.x;
        u_xlat1.y = _1241.y;
        u_xlat1.z = _1241.z;
        float4 _1249 = u_xlat1;
        float3 _1253 = (u_xlat6 * _1249.xyz) + u_xlat16_10;
        u_xlat1.x = _1253.x;
        u_xlat1.y = _1253.y;
        u_xlat1.z = _1253.z;
        float4 _1260 = u_xlat1;
        float3 _1264 = clamp(_1260.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat1.x = _1264.x;
        u_xlat1.y = _1264.y;
        u_xlat1.z = _1264.z;
        float3 param_10 = _FillColor.xyz;
        float3 param_11 = rgb;
        float3 _1285 = ((-u_xlat16_35.xxx) * _DisplaceBlendIntensity.xxx) + blockPalette(param_10, param_11);
        u_xlat16_3.x = _1285.x;
        u_xlat16_3.y = _1285.y;
        u_xlat16_3.z = _1285.z;
        float3 _1297 = u_xlat1.xyz + (-u_xlat16_3.xyz);
        u_xlat16_5.x = _1297.x;
        u_xlat16_5.y = _1297.y;
        u_xlat16_5.z = _1297.z;
        float4 _1310 = u_xlat16_3;
        float3 _1312 = (_FillStrength.xxx * u_xlat16_5.xyz) + _1310.xyz;
        u_xlat16_3.x = _1312.x;
        u_xlat16_3.y = _1312.y;
        u_xlat16_3.z = _1312.z;
        float3 param_12 = _GlowColor.xyz;
        float3 param_13 = rgb;
        float3 _1328 = u_xlat16_50.xxx * blockPalette(param_12, param_13);
        u_xlat16_5.x = _1328.x;
        u_xlat16_5.y = _1328.y;
        u_xlat16_5.z = _1328.z;
        float3 param_14 = _EdgeColor.xyz;
        float3 param_15 = rgb;
        float4 _1345 = u_xlat16_5;
        float3 _1347 = (blockPalette(param_14, param_15) * u_xlat16_2.xxx) + _1345.xyz;
        u_xlat16_5.x = _1347.x;
        u_xlat16_5.y = _1347.y;
        u_xlat16_5.z = _1347.z;
        float4 _1354 = u_xlat16_3;
        float3 _1361 = (_1354.xyz * u_xlat16_0.xxx) + u_xlat16_5.xyz;
        u_xlat16_3.x = _1361.x;
        u_xlat16_3.y = _1361.y;
        u_xlat16_3.z = _1361.z;
        u_xlat16_50 = (u_xlat16.x * _TouchPosShine) + 1.0f;
        float4 _1376 = u_xlat16_3;
        float3 _1378 = u_xlat16_50.xxx * _1376.xyz;
        u_xlat16_3.x = _1378.x;
        u_xlat16_3.y = _1378.y;
        u_xlat16_3.z = _1378.z;
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
    float3 _1415 = _ShineBrightness.xxx * _ShineColor.xyz;
    u_xlat1.x = _1415.x;
    u_xlat1.y = _1415.y;
    u_xlat1.z = _1415.z;
    u_xlat16.x = uUnityTime.y * _ShineSpeed;
    u_xlat16.x = sin(u_xlat16.x);
    u_xlat16.x = (u_xlat16.x * 0.5f) + 1.0f;
    float4 _1439 = u_xlat1;
    float3 _1441 = u_xlat16.xxx * _1439.xyz;
    u_xlat1.x = _1441.x;
    u_xlat1.y = _1441.y;
    u_xlat1.z = _1441.z;
    float4 _1450 = u_xlat1;
    float3 _1452 = u_xlat16_18.xxx * _1450.xyz;
    u_xlat1.x = _1452.x;
    u_xlat1.y = _1452.y;
    u_xlat1.z = _1452.z;
    float3 _1461;
    if (u_xlatb0.x)
    {
        _1461 = u_xlat1.xyz;
    }
    else
    {
        _1461 = 0.0f.xxx;
    }
    u_xlat16_2.x = _1461.x;
    u_xlat16_2.z = _1461.y;
    u_xlat16_2.w = _1461.z;
    u_xlat16_18 = u_xlatb0.x ? u_xlat16_18 : 0.0f;
    u_xlatb0.x = 9.9999997473787516355514526367188e-05f < u_xlat48;
    if (u_xlatb0.x)
    {
        u_xlat0.x = dot(_TouchDisplaceDirection.xy, _TouchDisplaceDirection.xy);
        u_xlat0.x = rsqrt(u_xlat0.x);
        float4 _1497 = u_xlat0;
        float2 _1501 = _1497.xx * _TouchDisplaceDirection.xy;
        u_xlat0.x = _1501.x;
        u_xlat0.y = _1501.y;
        u_xlat1.x = uUnityTime.x * _TouchDisplaceSpeed;
        float2 _1520 = (u_xlat0.xy * u_xlat1.xx) + touchDisplaceUV;
        u_xlat17.x = _1520.x;
        u_xlat17.y = _1520.y;
        u_xlat49 = max(_TouchBackgroundPixelScale, 1.0f);
        float3 _1528 = u_xlat17;
        float2 _1532 = _1528.xy * _ScreenParams.xy;
        u_xlat17.x = _1532.x;
        u_xlat17.y = _1532.y;
        float3 _1537 = u_xlat17;
        float2 _1541 = _1537.xy / u_xlat49.xx;
        u_xlat17.x = _1541.x;
        u_xlat17.y = _1541.y;
        float3 _1546 = u_xlat17;
        float2 _1548 = floor(_1546.xy);
        u_xlat17.x = _1548.x;
        u_xlat17.y = _1548.y;
        u_xlat6.x = u_xlat49 * 0.5f;
        float2 _1563 = (u_xlat17.xy * u_xlat49.xx) + u_xlat6.xx;
        u_xlat1.y = _1563.x;
        u_xlat1.z = _1563.y;
        float4 _1568 = u_xlat0;
        float2 _1570 = -_1568.yy;
        u_xlat0.z = _1570.x;
        u_xlat0.w = _1570.y;
        u_xlat22 = (u_xlat0.zx * u_xlat1.xx) + touchDisplaceUV;
        u_xlat22 *= _ScreenParams.xy;
        u_xlat22 /= u_xlat49.xx;
        u_xlat22 = floor(u_xlat22);
        float2 _1599 = (u_xlat22 * u_xlat49.xx) + u_xlat6.xx;
        u_xlat1.x = _1599.x;
        u_xlat1.w = _1599.y;
        u_xlat1 /= _ScreenParams.xxyy;
        u_xlat16_32 = uDisplaceTex.Sample(_uDisplaceTex_sampler, u_xlat1.yz).x;
        u_xlat16_1.x = uDisplaceTex.Sample(_uDisplaceTex_sampler, u_xlat1.xw).x;
        u_xlat16_51 = u_xlat16_32 + (-0.5f);
        u_xlat16_5.x = u_xlat16_1.x + (-0.5f);
        u_xlat32 = u_xlat0.wx * u_xlat16_5.xx;
        float4 _1630 = u_xlat0;
        float2 _1636 = (_1630.xy * u_xlat16_51.xx) + u_xlat32;
        u_xlat0.x = _1636.x;
        u_xlat0.y = _1636.y;
        float4 _1641 = u_xlat0;
        float2 _1648 = (_1641.xy * _TouchDisplaceStrength.xx) + sceneUV;
        u_xlat0.x = _1648.x;
        u_xlat0.y = _1648.y;
        float2 param_16 = u_xlat0.xy;
        u_xlat16_0 = hoverSample(param_16);
        u_xlat16 = screenPos.xy / screenPos.ww;
        u_xlat48 = uUnityTime.y * _NoiseDirChangeSpeed;
        u_xlat1.x = floor(u_xlat48);
        u_xlat48 = frac(u_xlat48);
        u_xlat17.x = u_xlat48 * u_xlat48;
        u_xlat48 = ((-u_xlat48) * 2.0f) + 3.0f;
        u_xlat48 *= u_xlat17.x;
        float2 _1689 = u_xlat1.xx * 0.103100001811981201171875f.xx;
        u_xlat4.x = _1689.x;
        u_xlat4.z = _1689.y;
        u_xlat4.y = 0.381470024585723876953125f;
        u_xlat4.w = 0.938210070133209228515625f;
        u_xlat4 = frac(u_xlat4);
        u_xlat17 = u_xlat4.yxx + 33.3300018310546875f.xxx;
        u_xlat17.x = dot(u_xlat4.xyx, u_xlat17);
        float3 _1710 = u_xlat17;
        float2 _1714 = _1710.xx + u_xlat4.xy;
        u_xlat17.x = _1714.x;
        u_xlat17.y = _1714.y;
        u_xlat33.x = u_xlat17.y + u_xlat17.x;
        u_xlat17.x *= u_xlat33.x;
        u_xlat6.x = frac(u_xlat17.x);
        u_xlat17 = u_xlat4.wzz + 33.3300018310546875f.xxx;
        u_xlat17.x = dot(u_xlat4.zwz, u_xlat17);
        float3 _1743 = u_xlat17;
        float2 _1747 = _1743.xx + u_xlat4.zw;
        u_xlat17.x = _1747.x;
        u_xlat17.y = _1747.y;
        u_xlat33.x = u_xlat17.y + u_xlat17.x;
        u_xlat17.x *= u_xlat33.x;
        u_xlat6.y = frac(u_xlat17.x);
        float2 _1773 = (u_xlat6.xy * 2.0f.xx) + (-1.0f).xx;
        u_xlat17.x = _1773.x;
        u_xlat17.y = _1773.y;
        u_xlat1.x += 1.0f;
        float2 _1784 = u_xlat1.xx * 0.103100001811981201171875f.xx;
        u_xlat4.x = _1784.x;
        u_xlat4.z = _1784.y;
        u_xlat4.y = 0.381470024585723876953125f;
        u_xlat4.w = 0.938210070133209228515625f;
        u_xlat4 = frac(u_xlat4);
        u_xlat6 = u_xlat4.yxx + 33.3300018310546875f.xxx;
        u_xlat1.x = dot(u_xlat4.xyx, u_xlat6);
        float4 _1801 = u_xlat1;
        float2 _1805 = _1801.xx + u_xlat4.xy;
        u_xlat1.x = _1805.x;
        u_xlat1.w = _1805.y;
        u_xlat49 = u_xlat1.w + u_xlat1.x;
        u_xlat1.x *= u_xlat49;
        u_xlat6.x = frac(u_xlat1.x);
        u_xlat11 = u_xlat4.wzz + 33.3300018310546875f.xxx;
        u_xlat1.x = dot(u_xlat4.zwz, u_xlat11);
        float4 _1833 = u_xlat1;
        float2 _1837 = _1833.xx + u_xlat4.zw;
        u_xlat1.x = _1837.x;
        u_xlat1.w = _1837.y;
        u_xlat49 = u_xlat1.w + u_xlat1.x;
        u_xlat1.x *= u_xlat49;
        u_xlat6.y = frac(u_xlat1.x);
        float2 _1859 = (u_xlat6.xy * 2.0f.xx) + (-1.0f).xx;
        u_xlat1.x = _1859.x;
        u_xlat1.w = _1859.y;
        float4 _1867 = u_xlat1;
        float2 _1869 = (-u_xlat17.xy) + _1867.xw;
        u_xlat1.x = _1869.x;
        u_xlat1.w = _1869.y;
        float4 _1876 = u_xlat1;
        float2 _1881 = (u_xlat48.xx * _1876.xw) + u_xlat17.xy;
        u_xlat1.x = _1881.x;
        u_xlat1.y = _1881.y;
        float4 _1886 = u_xlat1;
        float2 _1894 = (_1886.xy * _NoiseEvoSpeed.xx) + noiseUV;
        u_xlat1.x = _1894.x;
        u_xlat1.y = _1894.y;
        u_xlat16_1 = uNoiseTex.Sample(_uNoiseTex_sampler, u_xlat1.xy).xy;
        float2 _1907 = u_xlat16_1 + (-0.5f).xx;
        u_xlat1.x = _1907.x;
        u_xlat1.y = _1907.y;
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
        float2 _1963 = u_xlat1.xy + u_xlat1.xy;
        u_xlat16_5.x = _1963.x;
        u_xlat16_5.y = _1963.y;
        float2 _1973 = u_xlat16_5.xy * _NoiseDisplaceStrength.xx;
        u_xlat1.x = _1973.x;
        u_xlat1.y = _1973.y;
        float4 _1980 = u_xlat1;
        float2 _1982 = u_xlat33.xx * _1980.xy;
        u_xlat1.x = _1982.x;
        u_xlat1.y = _1982.y;
        float4 _1987 = u_xlat1;
        float2 _1991 = _1987.xy / _ScreenParams.xy;
        u_xlat1.x = _1991.x;
        u_xlat1.y = _1991.y;
        u_xlat16 += u_xlat1.xy;
        u_xlat16 *= _ScreenParams.xy;
        u_xlat16 /= u_xlat33.xx;
        float2 _2009 = floor(u_xlat16);
        u_xlat1.x = _2009.x;
        u_xlat1.y = _2009.y;
        u_xlat16 = frac(u_xlat16);
        u_xlat33.x = uUnityTime.y * _SDFMoveSpeed;
        u_xlat49 = floor(u_xlat33.x);
        u_xlat33.x = frac(u_xlat33.x);
        u_xlat6.x = u_xlat33.x * u_xlat33.x;
        u_xlat33.x = ((-u_xlat33.x) * 2.0f) + 3.0f;
        u_xlat33.x *= u_xlat6.x;
        float4 _2049 = u_xlat1;
        float2 _2051 = u_xlat49.xx + _2049.xy;
        u_xlat1.x = _2051.x;
        u_xlat1.y = _2051.y;
        float2 _2058 = u_xlat1.xy * 0.103100001811981201171875f.xx;
        u_xlat6.x = _2058.x;
        u_xlat6.y = _2058.y;
        float3 _2063 = u_xlat6;
        float2 _2065 = frac(_2063.xy);
        u_xlat6.x = _2065.x;
        u_xlat6.y = _2065.y;
        u_xlat11 = u_xlat6.yxx + 33.3300018310546875f.xxx;
        u_xlat49 = dot(u_xlat6.xyx, u_xlat11);
        float3 _2079 = u_xlat6;
        float2 _2081 = u_xlat49.xx + _2079.xy;
        u_xlat6.x = _2081.x;
        u_xlat6.y = _2081.y;
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
        float4 _2157 = u_xlat1;
        float2 _2161 = _2157.xy + 18.1700000762939453125f.xx;
        u_xlat1.x = _2161.x;
        u_xlat1.y = _2161.y;
        float4 _2166 = u_xlat1;
        float2 _2168 = _2166.xy * 0.103100001811981201171875f.xx;
        u_xlat1.x = _2168.x;
        u_xlat1.y = _2168.y;
        float4 _2173 = u_xlat1;
        float2 _2175 = frac(_2173.xy);
        u_xlat1.x = _2175.x;
        u_xlat1.y = _2175.y;
        u_xlat12 = u_xlat1.yxx + 33.3300018310546875f.xxx;
        u_xlat49 = dot(u_xlat1.xyx, u_xlat12);
        float4 _2190 = u_xlat1;
        float2 _2192 = u_xlat49.xx + _2190.xy;
        u_xlat1.x = _2192.x;
        u_xlat1.y = _2192.y;
        u_xlat17.x = u_xlat1.y + u_xlat1.x;
        u_xlat1.x *= u_xlat17.x;
        u_xlat11.y = frac(u_xlat1.x);
        float2 _2218 = (-u_xlat6.xy) + u_xlat11.xy;
        u_xlat1.x = _2218.x;
        u_xlat1.y = _2218.y;
        float4 _2225 = u_xlat1;
        float2 _2230 = (u_xlat33.xx * _2225.xy) + u_xlat6.xy;
        u_xlat1.x = _2230.x;
        u_xlat1.y = _2230.y;
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
        float3 _2298 = (u_xlat16.xxx * blockPalette(param_17, param_18)) + u_xlat16.xxx;
        u_xlat16_8.x = _2298.x;
        u_xlat16_8.y = _2298.y;
        u_xlat16_8.z = _2298.z;
        float4 _2305 = u_xlat16_8;
        float3 _2309 = clamp(_2305.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat16_8.x = _2309.x;
        u_xlat16_8.y = _2309.y;
        u_xlat16_8.z = _2309.z;
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
        float4 _2352 = u_xlat16_8;
        float3 _2358 = ((-_2352.xyz) * u_xlat48.xxx) + 1.0f.xxx;
        u_xlat16_8.x = _2358.x;
        u_xlat16_8.y = _2358.y;
        u_xlat16_8.z = _2358.z;
        float4 _2365 = u_xlat16_8;
        float4 _2367 = u_xlat16_8;
        float3 _2369 = _2365.xyz + _2367.xyz;
        u_xlat16_8.x = _2369.x;
        u_xlat16_8.y = _2369.y;
        u_xlat16_8.z = _2369.z;
        float3 param_21 = _TouchGlowColor.xyz;
        float3 param_22 = rgb;
        u_xlat16_15 = ((-u_xlat16_51.xxx) * blockPalette(param_21, param_22)) + 1.0f.xxx;
        float4 _2388 = u_xlat16_8;
        float3 _2393 = ((-_2388.xyz) * u_xlat16_15) + 1.0f.xxx;
        u_xlat16_8.x = _2393.x;
        u_xlat16_8.y = _2393.y;
        u_xlat16_8.z = _2393.z;
        u_xlatb0 = bool4(u_xlat16_9.xyzx.x >= float4(0.5f, 0.5f, 0.5f, 0.0f).x, u_xlat16_9.xyzx.y >= float4(0.5f, 0.5f, 0.5f, 0.0f).y, u_xlat16_9.xyzx.z >= float4(0.5f, 0.5f, 0.5f, 0.0f).z, u_xlat16_9.xyzx.w >= float4(0.5f, 0.5f, 0.5f, 0.0f).w).xyz;
        u_xlat0.x = float(u_xlatb0.x);
        u_xlat0.y = float(u_xlatb0.y);
        u_xlat0.z = float(u_xlatb0.z);
        float3 _2422 = ((-u_xlat16_13) * 2.0f.xxx) + u_xlat16_8.xyz;
        u_xlat1.x = _2422.x;
        u_xlat1.y = _2422.y;
        u_xlat1.z = _2422.z;
        float4 _2429 = u_xlat0;
        float3 _2435 = (_2429.xyz * u_xlat1.xyz) + u_xlat16_14;
        u_xlat0.x = _2435.x;
        u_xlat0.y = _2435.y;
        u_xlat0.z = _2435.z;
        float4 _2442 = u_xlat0;
        float3 _2446 = clamp(_2442.xyz, 0.0f.xxx, 1.0f.xxx);
        u_xlat0.x = _2446.x;
        u_xlat0.y = _2446.y;
        u_xlat0.z = _2446.z;
        float3 _2458 = (u_xlat16_10 * 0.5f.xxx) + u_xlat0.xyz;
        u_xlat16_8.x = _2458.x;
        u_xlat16_8.y = _2458.y;
        u_xlat16_8.z = _2458.z;
    }
    else
    {
        u_xlat16_8.x = 0.0f;
        u_xlat16_8.y = 0.0f;
        u_xlat16_8.z = 0.0f;
    }
    float4 _2469 = u_xlat16_2;
    float3 _2479 = (_2469.xzw * (u_xlat16_18 + uReadyExtra).xxx) + u_xlat16_3.xyz;
    u_xlat16_2.x = _2479.x;
    u_xlat16_2.y = _2479.y;
    u_xlat16_2.z = _2479.z;
    float3 _2490 = u_xlat16_8.xyz + u_xlat16_2.xyz;
    result.x = _2490.x;
    result.y = _2490.y;
    result.z = _2490.z;
    return result;
}

void frag_main()
{
    if (uLayer == 0)
    {
        float2 param = fieldUV;
        float disabledMask = uMasks.Sample(_uMasks_sampler, basePixelUV(param)).w;
        float param_1 = disabledMask;
        float4 _2687 = disabledColor(param_1);
        pev_output = _2687 * color;
    }
    else
    {
        float4 _2692 = fullActive();
        pev_output = _2692 * color;
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
