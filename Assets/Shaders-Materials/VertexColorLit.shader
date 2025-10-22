Shader "Custom/VertexColorLit"
{
    Properties
    {
        _FadeStart ("Fade Start Distance", Range(0,5)) = 1.5
        _FadeEnd ("Fade End Distance", Range(0,10)) = 4.5
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float4 color : COLOR;
            };

            float _FadeStart;
            float _FadeEnd;

            v2f vert (appdata v)
            {
                v2f o;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Simple Lambert lighting
                float3 lightDir = normalize(float3(0.3, 1.0, 0.5));
                float ndotl = saturate(dot(normalize(i.worldNormal), lightDir));
                float3 diffuse = i.color.rgb * ndotl;
                float3 ambient = i.color.rgb * 0.3;
                float3 finalColor = diffuse + ambient;

                // Depth fade
                float dist = distance(_WorldSpaceCameraPos, i.worldPos);
                float fade = saturate(1.0 - (dist - _FadeStart) / (_FadeEnd - _FadeStart));

                return fixed4(finalColor, fade);
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
