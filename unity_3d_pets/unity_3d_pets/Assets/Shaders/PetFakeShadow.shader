Shader "PetV1/FakeShadow"
{
    // 宠物脚下的椭圆假阴影。PRD 4.5 要求：「宠物脚下必须有跟随移动的
    // 椭圆半透明假阴影，否则 3D 宠物会显得漂浮在背景上」。
    //
    // 为什么不用现成 shader：
    // URP/Unlit 没有 _ShadowColor 与径向衰减，Sprites/Default 走的是
    // Sprite 渲染路径、在 URP 下需要额外配置。这里自己算最省资源 ——
    // 径向渐变在 fragment 里用距离算，不占纹理（首包只有 4MB）。

    Properties
    {
        _ShadowColor ("Shadow Color", Color) = (0.25, 0.20, 0.16, 0.35)
        _Softness ("Softness", Range(0, 1)) = 0.75
        [HideInInspector] _ZWrite ("ZWrite", Float) = 0
        [HideInInspector] _ZTest ("ZTest", Float) = 4  // LEqual
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "FakeShadowUnlit"
            Blend SrcAlpha OneMinusSrcAlpha
ZWrite [_ZWrite]
 ZTest [_ZTest]
      // 必须 Cull Off，不能 Cull Back。
            //
      // 阴影 quad 被 Euler(90, petY, 0) 放平在地面上，
      // 而 mesh 的法线是 Vector3.back（本地 -Z）。
      // 转90° 之后法线朝**下**，相机在斜上方俯视，
      // 看到的就是背面 —— Cull Back 会整片剔掉，阴影完全不可见。
        //
            // 这里跟背景 quad 是同一类问题（背景那次也踩了）。
      // 用 Cull Off 更稳：地面阴影是薄片，双面渲染没有副作用，
      // 也不依赖绕序方向，阴影贴图转 90°/180° 都不会突然消失。
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                half4  _ShadowColor;
                float  _Softness;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // 椭圆径向衰减：以 uv 中心 (0.5,0.5) 到边缘的距离为准。
                // 用 max 而不是 length，得到的是方形超椭圆 ——
                // 对四边形 quad 来说，max 恰好形成内切椭圆，边缘不会有方角。
                float2 d = abs(IN.uv - 0.5) * 2.0;      // 0..1
                float r = max(d.x, d.y);

                // softness=0 硬边，softness=1 完全弥散
                float inner = 1.0 - saturate(_Softness);
                float alpha = 1.0 - smoothstep(inner, 1.0, r);

                half4 col = _ShadowColor;
                col.a *= alpha;
                // 完全透明处直接 discard，省一次混合，也避免和背景产生排序抖动
                clip(col.a - 0.001);
                return col;
            }
            ENDHLSL
        }
    }

    FallBack Off
}