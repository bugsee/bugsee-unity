Shader "Bugsee/LitColored"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Texture", 2D) = "white" {}
        // Grass / soft foliage: tip bend in wind. Leave strength 0 for rigid props.
        _WindStrength ("Wind Strength", Float) = 0
        _WindSpeed ("Wind Speed", Float) = 1.35
        _WindAmp ("Wind Amplitude", Float) = 0.12
        _WindDir ("Wind Direction", Vector) = (1, 0, 0.32, 0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 pos : SV_POSITION;
                float3 worldNormal : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                SHADOW_COORDS(3)
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _WindStrength;
            float _WindSpeed;
            float _WindAmp;
            float4 _WindDir;

            v2f vert (appdata v)
            {
                v2f o;
                float4 local = v.vertex;
                // Horizontal tip sway (object Y = height). Chunk rotation looks like bobbing.
                if (_WindStrength > 0.0001)
                {
                    float3 worldPos = mul(unity_ObjectToWorld, local).xyz;
                    float bend = saturate(local.y * 1.65) * _WindStrength;
                    float phase = worldPos.x * 0.42 + worldPos.z * 0.31;
                    float t = _Time.y * _WindSpeed;
                    float wave = sin(t + phase) + sin(t * 1.7 + phase * 1.3) * 0.35;
                    float3 dir = normalize(float3(_WindDir.x, 0.0, _WindDir.z));
                    // Keep displacement in XZ so roots stay planted.
                    float3 worldDisp = dir * (wave * bend * _WindAmp);
                    float3 localDisp = mul((float3x3)unity_WorldToObject, worldDisp);
                    local.xyz += localDisp;
                }

                o.pos = UnityObjectToClipPos(local);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, local).xyz;
                TRANSFER_SHADOW(o);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 albedo = tex2D(_MainTex, i.uv) * _Color;
                float3 n = normalize(i.worldNormal);
                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                // Half-Lambert so round forms stay readable even with a single light.
                float ndotl = saturate(dot(n, l) * 0.5 + 0.5);
                UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos);
                fixed3 ambient = UNITY_LIGHTMODEL_AMBIENT.rgb * albedo.rgb;
                fixed3 diffuse = _LightColor0.rgb * albedo.rgb * ndotl * atten;
                return fixed4(ambient + diffuse, albedo.a);
            }
            ENDCG
        }
    }
    // Critical: do NOT ship a custom ShadowCaster here — SkinnedMeshRenderer needs
    // Unity's built-in skinned caster (from this fallback). A non-skinned caster
    // produces broken blob shadows for the Meshy anteater.
    FallBack "Diffuse"
}
