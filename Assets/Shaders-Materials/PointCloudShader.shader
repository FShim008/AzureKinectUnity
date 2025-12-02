// Shader for rendering point clouds.
Shader "Custom/PointCloudShader"
{
    Properties
    {
        _PointSize("Point Size (Screen Pixels)", Range(1, 10)) = 5
        _ColorMode("Color Mode (1: Vertex Color)", Float) = 1
        _UniformColor("Uniform Color", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 100
        
        ZWrite On
        ZTest LEqual
        Cull Off // Ensure points are rendered regardless of view angle
        
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #pragma target 4.0 
            
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
            };

            struct v2g
            {
                float4 pos : POSITION; // Object space position passed to GS
                float4 color : COLOR;
            };
            
            struct g2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
            };

            uniform float _PointSize;
            uniform float _ColorMode;
            uniform float4 _UniformColor;

            // Vertex Shader
            v2g vert (appdata v)
            {
                v2g o;
                o.pos = v.vertex; 
                o.color = v.color;
                return o;
            }
            
            // Geometry Shader: Explicitly takes a single 'point' and outputs a 'TriangleStream'
            [maxvertexcount(4)]
            void geom(point v2g p[1], inout TriangleStream<g2f> triStream)
            {
                float4 v = UnityObjectToClipPos(float4(p[0].pos.xyz, 1.0));
                float pointSize = _PointSize;
                
                float2 viewportSize = float2(_ScreenParams.x, _ScreenParams.y);
                float2 halfSize = pointSize / viewportSize;

                // Adjust for perspective
                halfSize *= v.w; 

                // 1. Top Left
                g2f tl;
                tl.pos = v + float4(-halfSize.x, halfSize.y, 0, 0);
                tl.color = p[0].color;
                triStream.Append(tl);

                // 2. Top Right
                g2f tr;
                tr.pos = v + float4(halfSize.x, halfSize.y, 0, 0);
                tr.color = p[0].color;
                triStream.Append(tr);

                // 3. Bottom Left
                g2f bl;
                bl.pos = v + float4(-halfSize.x, -halfSize.y, 0, 0);
                bl.color = p[0].color;
                triStream.Append(bl);

                // 4. Bottom Right
                g2f br;
                br.pos = v + float4(halfSize.x, -halfSize.y, 0, 0);
                br.color = p[0].color;
                triStream.Append(br);

                triStream.RestartStrip();
            }

            // Fragment Shader
            float4 frag (g2f i) : SV_Target
            {
                if (_ColorMode > 0.5)
                {
                    return i.color;
                }
                return _UniformColor;
            }
            ENDCG
        }
    }
}