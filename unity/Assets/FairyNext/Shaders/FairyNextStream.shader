// FairyNext 顶点流着色器（M1-15）。ABI 常量与位域宏一律取自 abi.g.hlsl（AbiGen 生成物，
// 与 shaders/abi.g.hlsl 逐字节同源）——本文件不得手抄任何移位常量。
//
// 输入顶点流（UnityVertexStreamBackend.StreamVertex，60B/顶点）：
//   POSITION   槽本地坐标（y 向下；y 翻转只在流根 GameObject 的 localScale=(1,-1,1) 一处）
//   TEXCOORD0  uv（按角归位，发射器已展开）
//   TEXCOORD1  uint4 = (color, route, flags, aux)——整数属性，float32 会把大整数圆掉
//   TEXCOORD2  extra（按 kind 复用；径向填充 = 中心 xy + 起角 + 有符号扫角，turns 制）
//   TEXCOORD3  quad 内归一化角（径向填充与圆角 clip 的求值域）
//
// M1 消费面：直通色 × 纹理、grayed、fontAlpha、径向填充（FN_RADIAL_FILL_*）、
// clip 条目（矩形 + 软边 + 圆角，条目 0 = None 零采样）。SDF/曲线字形位只透传不求值（M1-17/18）。
Shader "FairyNext/Stream"
{
    Properties
    {
        _FnTex0 ("Tex Slot 0", 2D) = "white" {}
        _FnTex1 ("Tex Slot 1", 2D) = "white" {}
        _FnTex2 ("Tex Slot 2", 2D) = "white" {}
        _FnTex3 ("Tex Slot 3", 2D) = "white" {}
        _SrcBlend ("Src Blend", Float) = 5    // SrcAlpha
        _DstBlend ("Dst Blend", Float) = 10   // OneMinusSrcAlpha
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off          // y 翻转在流根：绕序反了，两面都画
        ZWrite Off
        ZTest LEqual
        Blend [_SrcBlend] [_DstBlend]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5    // 整数顶点属性
            #include "UnityCG.cginc"
            #include "abi.g.hlsl"

            sampler2D _FnTex0;
            sampler2D _FnTex1;
            sampler2D _FnTex2;
            sampler2D _FnTex3;
            // 槽矩阵：2 float4/槽 = (m00,m01,m10,m11) + (tx,ty,0,0)；_FnSlotInv 为逆（clip 换帧用）。
            float4 _FnSlots[FN_TRANSFORM_SLOT_BUDGET * 2];
            float4 _FnSlotInv[FN_TRANSFORM_SLOT_BUDGET * 2];
            // 裁剪条目：3 float4/条 = rect(xMin,yMin,xMax,yMax) + (soft.xy, slot, 0) + radii。
            float4 _FnClips[FN_CLIP_ENTRY_BUDGET * 3];

            struct appdata
            {
                float3 pos    : POSITION;
                float2 uv     : TEXCOORD0;
                uint4  packed : TEXCOORD1;   // color | route | flags | aux
                float4 extra  : TEXCOORD2;
                float2 corner : TEXCOORD3;
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                float2 uv     : TEXCOORD0;
                nointerpolation uint4 packed : TEXCOORD1;
                float4 extra  : TEXCOORD2;
                float2 corner : TEXCOORD3;
                float2 streamPos : TEXCOORD4;   // 流根空间（槽已应用；clip 换帧的公共坐标）
            };

            float2 FnApplySlot(float4 a, float4 b, float2 p)
            {
                return float2(dot(a.xy, p), dot(a.zw, p)) + b.xy;
            }

            v2f vert(appdata v)
            {
                v2f o;
                uint slot = FN_ROUTE_SLOT(v.packed.y);
                float2 sp = FnApplySlot(_FnSlots[slot * 2], _FnSlots[slot * 2 + 1], v.pos.xy);
                o.streamPos = sp;
                o.pos = UnityObjectToClipPos(float4(sp, 0.0, 1.0));
                o.uv = v.uv;
                o.packed = v.packed;
                o.extra = v.extra;
                o.corner = v.corner;
                return o;
            }

            float4 FnUnpackColor(uint c)   // Color32.Pack：bit0-7=r … bit24-31=a（RGBA8 unorm 同序）
            {
                return float4(c & 0xFFu, (c >> 8) & 0xFFu, (c >> 16) & 0xFFu, (c >> 24) & 0xFFu) / 255.0;
            }

            // 圆角矩形有符号距离（<0 在内）。象限取角半径：radii = (左上, 右上, 右下, 左下)——
            // 与 fork 的四角序一致；像素级钉死归 M2-14 全矩阵。
            float FnClipDistance(float4 rect, float4 radii, float2 p)
            {
                float2 c = (rect.xy + rect.zw) * 0.5;
                float2 he = (rect.zw - rect.xy) * 0.5;
                float r = p.x < c.x ? (p.y < c.y ? radii.x : radii.w)
                                    : (p.y < c.y ? radii.y : radii.z);
                float2 q = abs(p - c) - (he - r);
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                uint route = i.packed.y;
                uint flags = i.packed.z;

                // 径向填充：extra = (center.xy, startTurns, signedSweepTurns)。y 向下 ⇒ atan2 正向
                // 即视觉顺时针，与 RadialFill.StartTurns 的 turns 约定同向。逆时针在 CPU 侧已折成
                // 「起始边挪到另一端 + 扫角取负」，这里只有一条「沿 sign(sweep) 走 |sweep| 以内」判据。
                if (FN_FLAGS_RADIAL_FILL(flags) != 0u)
                {
                    float2 d = i.corner - i.extra.xy;
                    float turns = frac(atan2(d.y, d.x) / 6.28318530718);
                    float sweep = i.extra.w;
                    float travelled = sweep >= 0.0 ? frac(turns - i.extra.z) : frac(i.extra.z - turns);
                    clip(abs(sweep) - travelled);
                }

                uint ts = FN_ROUTE_TEX_SLOT(route);
                float4 tex = ts == 0u ? tex2D(_FnTex0, i.uv)
                           : ts == 1u ? tex2D(_FnTex1, i.uv)
                           : ts == 2u ? tex2D(_FnTex2, i.uv)
                                      : tex2D(_FnTex3, i.uv);
                if (FN_FLAGS_FONT_ALPHA(flags) != 0u) tex = float4(1.0, 1.0, 1.0, tex.a);

                float4 col = tex * FnUnpackColor(i.packed.x);
                if (FN_FLAGS_GRAYED(flags) != 0u)
                    col.rgb = dot(col.rgb, float3(0.299, 0.587, 0.114));

                // clip：条目 0 = None 哨兵零采样。条目 rect 在**它自己绑的槽**的本地帧里——
                // 像素先回到流根空间（streamPos），再经该槽的逆矩阵进条目的帧。
                uint clipIdx = FN_ROUTE_CLIP_INDEX(route);
                if (clipIdx != 0u)
                {
                    float4 e0 = _FnClips[clipIdx * 3];
                    float4 e1 = _FnClips[clipIdx * 3 + 1];
                    float4 e2 = _FnClips[clipIdx * 3 + 2];
                    uint cslot = (uint)e1.z;
                    float2 cp = FnApplySlot(_FnSlotInv[cslot * 2], _FnSlotInv[cslot * 2 + 1], i.streamPos);
                    float dist = FnClipDistance(e0, e2, cp);
                    float soft = max(e1.x, e1.y);           // M1 近似：各向软边取大者；像素钉死归 M2-14
                    float cov = soft > 0.0 ? saturate(-dist / soft) : (dist <= 0.0 ? 1.0 : 0.0);
                    col.a *= cov;
                }

                return col;
            }
            ENDCG
        }
    }
}
