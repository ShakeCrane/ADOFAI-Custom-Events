Shader "PEV/DepthProbeGrab"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "DisableBatching"="True" }
        Cull Off ZWrite Off ZTest LEqual
        GrabPass { }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _GrabTexture;
            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 vertex : SV_POSITION; float4 grab : TEXCOORD0; };
            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.grab = ComputeGrabScreenPos(o.vertex);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 background = tex2Dproj(_GrabTexture, UNITY_PROJ_COORD(i.grab));
                return fixed4(lerp(background.rgb, fixed3(1, 0, 0), 0.5), 1);
            }
            ENDCG
        }
    }
}
