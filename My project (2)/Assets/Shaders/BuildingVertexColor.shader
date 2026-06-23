// Flat-lit building surface that tints albedo by the mesh's per-vertex color.
// Lets every building in a chunk share one material (and one draw call) while
// keeping per-building colour as vertex data — see SFMapImporterWindow, which
// bakes each building's palette colour into its vertices before combining.
Shader "SFMap/BuildingVertexColor"
{
    Properties
    {
        _Color      ("Tint",       Color)       = (1,1,1,1)
        _Glossiness ("Smoothness", Range(0,1))  = 0.05
        _Metallic   ("Metallic",   Range(0,1))  = 0.0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        struct Input
        {
            float4 color : COLOR; // per-vertex colour from the combined mesh
        };

        half   _Glossiness;
        half   _Metallic;
        fixed4 _Color;

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = IN.color * _Color;
            o.Albedo     = c.rgb;
            o.Metallic   = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha      = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
