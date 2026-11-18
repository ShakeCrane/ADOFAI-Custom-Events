#!/usr/bin/env python3
"""Generate the noise field's Unity Built-in shaders from the pinned variants."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src/UnityShaders/Reference/Variants"
TARGET = ROOT / "src/UnityShaders/Runtime/Generated"

# The standalone probes use a clip-space quad with an unflipped projection.
PROBE_TRANSFORM = """
static const float4 _ProjectionParams = float4(1, 0, 0, 0);
float4 UnityObjectToClipPos(float4 v) { return v; }
float4 ComputeScreenPos(float4 v) { return float4(v.xy * 0.5 + v.w * 0.5, v.zw); }
"""

VERTEX = """
float3 uView;
struct PEVVertexInput { float4 vertex : POSITION; float4 color : COLOR; };
struct PEVVertexOutput {
    float4 position : SV_POSITION;
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
PEVVertexOutput pev_vertex(PEVVertexInput input) {
    PEVVertexOutput o;
    o.position = UnityObjectToClipPos(input.vertex);
    o.color = input.color;
    // Use logical viewport coordinates, including when Unity flips clip space for a render target.
    float4 viewport = ComputeScreenPos(o.position);
    o.sceneUV = viewport.xy / viewport.w;
    o.fieldUV = o.sceneUV;
    o.screenPos = float4(o.sceneUV, 0.0, 1.0);
    o.displaceUV = o.sceneUV * float2(0.8, 0.3);
    o.sparkUV = o.sceneUV * float2(3.0, 1.2);
    o.disabledDisplaceUV = o.sceneUV * float2(0.5, 0.2);
    o.touchDisplaceUV = o.sceneUV * float2(0.55, 0.3);
    o.noiseUV = o.sceneUV * float2(1.5, 1.46);
    o.clipHalfWidth = uView.y * 0.888888896 / uView.x;
    return o;
}
"""


def variant(code, capture, disabled):
    # Unity owns register assignment. Coupled samplers use "sampler" + texture
    # name (Unity Manual, SL-SamplerStates); SPIRV-Cross uses a different name.
    code = re.sub(r"\s*:\s*register\([ts]\d+\)", "", code)
    code = re.sub(r"_([A-Za-z]\w*)_sampler", r"sampler\1", code)
    code = code.replace("_ScreenParams", "_PEVScreenParams")
    code = code.replace("SPIRV_Cross_Output main(", "SPIRV_Cross_Output pev_fragment(")
    # D3D11 links stage registers by signature. The vertex output reserves register
    # zero for SV_POSITION; retaining it as the first pixel input prevents FXC from
    # shifting every TEXCOORD down one register when compiling the stages separately.
    marker = "struct SPIRV_Cross_Input\n{\n"
    if code.count(marker) != 1:
        raise ValueError("Unexpected translated fragment input structure")
    code = code.replace(marker, marker + "    float4 pev_position : SV_POSITION;\n", 1)
    if capture:
        code = re.sub(r"\buScene\b", "_GrabTexture", code)
        code = code.replace("sampleruScene", "sampler_GrabTexture")
        needle = "_GrabTexture.Sample(sampler_GrabTexture, uv)"
        if code.count(needle) != 1:
            raise ValueError("Unexpected GrabPass sampling structure")
        # Same platform-dependent Y orientation as UnityCG.ComputeGrabScreenPos.
        # Convert logical viewport coordinates back to clip-space orientation first.
        helper = """float2 PEVGrabUV(float2 uv) {
    uv.y = (uv.y - 0.5) * _ProjectionParams.x + 0.5;
#if UNITY_UV_STARTS_AT_TOP
    return float2(uv.x, 1.0 - uv.y);
#else
    return uv;
#endif
}
"""
        code = helper + code.replace(needle, "_GrabTexture.Sample(sampler_GrabTexture, PEVGrabUV(uv))")
    elif not disabled:
        needle = "uScene.Sample(sampleruScene, uv)"
        if code.count(needle) != 1:
            raise ValueError("Unexpected scene sampling structure")
        code = "uniform float4 _PEVBackground;\n" + code.replace(needle, "_PEVBackground")
        code = re.sub(r"^Texture2D<float4> uScene;\n", "", code, flags=re.M)
        code = re.sub(r"^SamplerState sampleruScene;\n", "", code, flags=re.M)
    if "_TouchPosCount;" in code:
        code = code.replace("float2 _TouchPos[10]", "float4 _TouchPos[10]")
        code = code.replace("u_xlat33.x = 1.0f;", "u_xlat33.x = 1.0f;\n            float touchOpacity = 1.0f;")
        code = code.replace("u_xlat7 = u_xlat16 + (-_TouchPos[u_xlati_loop_1]);", """float4 touch = _TouchPos[u_xlati_loop_1];
                float size = touch.z > 0.0f ? touch.z : 1.0f;
                u_xlat7 = (u_xlat16 - touch.xy) / size;""")
        code = code.replace("u_xlat49 = sqrt(u_xlat49);", """u_xlat49 = sqrt(u_xlat49);
                if (u_xlat49 <= u_xlat33.x) touchOpacity = touch.w == 0.0f ? 1.0f : saturate(touch.w - 1.0f);""")
        code = code.replace("u_xlat16.x = exp2(u_xlat16.x);", "u_xlat16.x = exp2(u_xlat16.x) * touchOpacity;", 1)
    return code


def main():
    TARGET.mkdir(parents=True, exist_ok=True)
    for colored in (0, 1):
        for disabled, hover, mode in ((1, 0, "disabled"), (0, 0, "capture"), (0, 1, "capture"),
                                      (0, 0, "nocapture"), (0, 1, "nocapture")):
            key = f"{disabled}{hover}{colored}"
            name = f"PEVNoise_{key}_{mode}"
            code = variant((SOURCE / (key + ".hlsl")).read_text(encoding="utf-8"), mode == "capture", bool(disabled))
            (TARGET / (name + ".hlsl")).write_text(code, encoding="utf-8", newline="\n")
            grab = "GrabPass { }" if mode == "capture" else ""
            blend = "Blend One One" if disabled else "Blend One OneMinusSrcAlpha"
            shader = f'''// Generated by tools/generate_noise_unity_shaders.py.
Shader "Hidden/PEV/{name}" {{
    SubShader {{
        Tags {{ "Queue"="Transparent" "RenderType"="Transparent" "DisableBatching"="True" }}
        Cull Off ZWrite Off ZTest LEqual
        {grab}
        Pass {{
            {blend}
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex pev_vertex
            #pragma fragment pev_fragment
            #include "UnityCG.cginc"
            #include "{name}.hlsl"
            {VERTEX}
            ENDHLSL
        }}
    }}
}}
'''
            shader = "\n".join(line.rstrip() for line in shader.splitlines()) + "\n"
            (TARGET / (name + ".shader")).write_text(shader, encoding="utf-8", newline="\n")
    print("Generated ten shader variants in", TARGET)


if __name__ == "__main__":
    main()
