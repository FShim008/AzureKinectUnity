Shader "Custom/PointCloudPointsOnly"
{
    Properties
    {
        _PointSize("Point Size", Float) = 0.02
        _Brightness("Brightness", Float) = 1.0
        _MinBrightnessDiscard("Min Brightness Discard", Range(0, 0.1)) = 0.02
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
            "RenderType" = "Opaque"
        }

        Pass
        {
            Name "PointCloudPass"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest LEqual
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma require geometry
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
            };

            struct v2g
            {
                float4 positionWS : TEXCOORD0;
                float4 color      : COLOR;
            };

            struct g2f
            {
                float4 positionHCS : SV_POSITION;
                float4 color       : COLOR;
                float fogFactor    : TEXCOORD1;
            };

            float _PointSize;
            float _Brightness;
            float _MinBrightnessDiscard;

            v2g vert(Attributes v)
            {
                v2g o;
                o.positionWS = float4(TransformObjectToWorld(v.positionOS.xyz), 1.0);
                o.color = v.color;
                return o;
            }

            [maxvertexcount(4)]
            void geom(point v2g input[1], inout TriangleStream<g2f> outStream)
            {
                float3 posWS = input[0].positionWS.xyz;
                float4 color = input[0].color;

                float3 up = UNITY_MATRIX_V[1].xyz;
                float3 right = UNITY_MATRIX_V[0].xyz;
                float halfSize = _PointSize * 0.5;

                float3 p0 = posWS + ( -right - up) * halfSize;
                float3 p1 = posWS + (  right - up) * halfSize;
                float3 p2 = posWS + ( -right + up) * halfSize;
                float3 p3 = posWS + (  right + up) * halfSize;

                g2f o;
                o.color = color;

                o.positionHCS = TransformWorldToHClip(p0);
                o.fogFactor = ComputeFogFactor(o.positionHCS.z);
                outStream.Append(o);
                
                o.positionHCS = TransformWorldToHClip(p1);
                o.fogFactor = ComputeFogFactor(o.positionHCS.z);
                outStream.Append(o);
                
                o.positionHCS = TransformWorldToHClip(p2);
                o.fogFactor = ComputeFogFactor(o.positionHCS.z);
                outStream.Append(o);
                
                o.positionHCS = TransformWorldToHClip(p3);
                o.fogFactor = ComputeFogFactor(o.positionHCS.z);
                outStream.Append(o);

                outStream.RestartStrip();
            }

            half4 frag(g2f i) : SV_Target
            {
                half4 c = (half4)i.color;
                
                // DISCARD dark points (Sensor noise shadows)
                // The "black shade" is usually caused by Kinect cameras returning (0,0,0) in occluded areas.
                if (max(c.r, max(c.g, c.b)) < _MinBrightnessDiscard) discard;

                #if !defined(UNITY_COLORSPACE_GAMMA)
                    c.rgb = SRGBToLinear(c.rgb);
                #endif

                c.rgb *= (half)_Brightness;

                // Apply Fog
                c.rgb = MixFog(c.rgb, i.fogFactor);

                return half4(c.rgb, 1.0h);
            }
            ENDHLSL
        }
    }
}


