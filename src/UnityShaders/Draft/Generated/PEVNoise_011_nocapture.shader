// Draft: Unity build and pixel equivalence have not been verified.
Shader "Hidden/PEV/PEVNoise_011_nocapture" {
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "DisableBatching"="True" }
        Cull Off ZWrite Off ZTest LEqual

        Pass {
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex pev_vertex
            #pragma fragment pev_fragment
            #include "UnityCG.cginc"
            #include "PEVNoise_011_nocapture.hlsl"

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
    // Provisional screen-space mapping; must be verified against game camera,
    // render target Y orientation and the reference's chart transform.
    o.sceneUV = o.position.xy / o.position.w * 0.5 + 0.5;
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

            ENDHLSL
        }
    }
}
