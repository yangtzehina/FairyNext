using FairyNext.Numerics;

namespace FairyNext.Core.Rendering;

// ============================================================================
// 叶发射器（架构「平面三」机制 1 的入口半边；M1-13）。
//
// 职责一句话：**给定一个叶的内容描述与它的框大小，产出若干个 quad 的几何与 UV**。
// 不产 route（slot/clipIndex/texSlot 由 RenderStream 盖写，见 LeafDesc 的文档）、
// 不产最终 color（发射的是未乘 α 的基色，机制 7）、不知道自己在树的哪里。
//
// 于是本文件是**纯函数**：入参只有内容描述 + 宽高，出参只有 span 里的 quad。
// 一棵树都不需要就能把「九宫格切几片、UV 对不对、退化格掉不掉」全部测掉——
// 这正是把发射器与 Extract 分开的理由（fork 的 SliceFill 长在 DisplayObject 上，
// 想验一格 UV 得先有一个 Unity 场景）。
//
// ── 坐标与 UV 约定（全仓库一处声明）────────────────────────────────────────
//  · 叶本地空间：原点 = 叶框左上角，+x 向右、+y 向下（与全链路一致，核心永不翻 y）。
//  · UV：v 向下——v=0 是图集**顶行**，与 ReferenceRaster 的 `ty = v × texHeight`、
//    row 0 = 顶行同源。fork 的 SliceFill 要把 gridTexY 倒着排，是因为 Unity 的 v 向上；
//    我们没有那一次翻转，所以九宫格的 UV 表与几何表同序（x 顺、y 顺），少一类错位。
//  · quad 的四角 UV 按角归位（uvA = corner(0,0)+(1,0)，uvB = corner(0,1)+(1,1)），
//    不用 min/max：旋转图集与镜像才有地方表达。
// ============================================================================

/// <summary>
/// 图元在图集里的一块区域（+ 可选九宫格）。
///
/// <see cref="Uv"/> 是 <c>xMin,yMin,xMax,yMax</c> 的 UV 矩形；<see cref="SourceWidth"/>/
/// <see cref="SourceHeight"/> 是这块区域的**像素尺寸**——九宫格的边宽是以源像素给的
/// （编辑器里量的就是像素），换算成 UV 与本地坐标都需要它。
/// </summary>
public struct SpriteRegion : IEquatable<SpriteRegion>
{
    /// <summary>UV 矩形（xMin,yMin,xMax,yMax；v 向下）。</summary>
    public Vector4 Uv;
    /// <summary>源宽（像素）。</summary>
    public float SourceWidth;
    /// <summary>源高（像素）。</summary>
    public float SourceHeight;
    /// <summary>九宫格（源像素坐标 x,y,w,h）；<c>w &lt;= 0 || h &lt;= 0</c> = 无九宫格。</summary>
    public Vector4 Grid;

    /// <summary>纯色叶用的退化区域：1×1 白（后端给 texSlot 绑 1×1 白，采样点无所谓）。</summary>
    public static SpriteRegion Solid => new SpriteRegion
    {
        Uv = new Vector4(0f, 0f, 1f, 1f),
        SourceWidth = 1f,
        SourceHeight = 1f,
        Grid = Vector4.zero,
    };

    /// <summary>整块图元（无九宫格）。</summary>
    public static SpriteRegion Full(Vector4 uv, float sourceWidth, float sourceHeight) => new SpriteRegion
    {
        Uv = uv,
        SourceWidth = sourceWidth,
        SourceHeight = sourceHeight,
        Grid = Vector4.zero,
    };

    /// <summary>带九宫格的图元（<paramref name="grid"/> = 源像素 x,y,w,h）。</summary>
    public static SpriteRegion Sliced(Vector4 uv, float sourceWidth, float sourceHeight, Vector4 grid) =>
        new SpriteRegion
        {
            Uv = uv,
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            Grid = grid,
        };

    /// <summary>
    /// 是否真的要走九宫格。四个「不是九宫格」的形态在此合并成一个判据：
    /// 没格子、格子退化、源尺寸非法（除零的来源）、格子越出源框。
    /// 一个越界的格子在 fork 里会产出 UV 落在邻图元上的边片——那是最难查的一类花屏。
    /// </summary>
    public readonly bool HasGrid =>
        Grid.z > 0f && Grid.w > 0f
        && SourceWidth > 0f && SourceHeight > 0f
        && Grid.x >= 0f && Grid.y >= 0f
        && Grid.x + Grid.z <= SourceWidth && Grid.y + Grid.w <= SourceHeight;

    /// <summary>左边宽（源像素）。</summary>
    public readonly float BorderLeft => Grid.x;
    /// <summary>右边宽（源像素）。</summary>
    public readonly float BorderRight => SourceWidth - (Grid.x + Grid.z);
    /// <summary>上边高（源像素）。</summary>
    public readonly float BorderTop => Grid.y;
    /// <summary>下边高（源像素）。</summary>
    public readonly float BorderBottom => SourceHeight - (Grid.y + Grid.w);

    /// <inheritdoc/>
    public readonly bool Equals(SpriteRegion other) =>
        Uv == other.Uv && Grid == other.Grid
        && BitEquals.Eq(SourceWidth, other.SourceWidth) && BitEquals.Eq(SourceHeight, other.SourceHeight);

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is SpriteRegion r && Equals(r);
    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(Uv, SourceWidth, SourceHeight, Grid);
    /// <inheritdoc/>
    public readonly override string ToString() =>
        HasGrid ? $"region uv={Uv} src={SourceWidth}×{SourceHeight} grid={Grid}" : $"region uv={Uv}";
}

/// <summary>
/// 一个叶要发射什么（内容描述；资产平面 M1-20/M1-22 的产物，运行期只读）。
/// 它**不含**位置与大小——那是树的 resolved 几何，由 Extract 在遍历时取。
/// </summary>
public struct LeafSpec
{
    /// <summary>纹理身份（<see cref="TexId.None"/> = 纯色）。</summary>
    public TexId Texture;
    /// <summary>混合类别（进段键）。</summary>
    public BlendClass Blend;
    /// <summary>图集区域 + 九宫格。</summary>
    public SpriteRegion Region;
    /// <summary>**未乘 α 的**基色（RGBA8 打包；颜色纯函数的定义域，机制 7）。</summary>
    public uint BaseColor;
    /// <summary>发射器 flags（fontAlpha/SDF/borderW…；grayed 位归 Color 通道）。</summary>
    public uint EmitFlags;
    /// <summary>预留实例数下限（文本叶用；0 = 严丝合缝）。</summary>
    public int SlackHint;
    /// <summary>填充参数（<see cref="FillMethod.None"/> = 整图）。</summary>
    public RadialFillParams Fill;

    /// <summary>纯色叶（无纹理，一个 quad）。</summary>
    public static LeafSpec Solid(uint baseColor, BlendClass blend = BlendClass.Normal) => new LeafSpec
    {
        Texture = TexId.None,
        Blend = blend,
        Region = SpriteRegion.Solid,
        BaseColor = baseColor,
        EmitFlags = 0u,
        SlackHint = 0,
        Fill = RadialFillParams.None,
    };

    /// <summary>图片叶（基色默认不透明白 = 原图色）。</summary>
    public static LeafSpec Image(TexId texture, in SpriteRegion region,
        uint baseColor = 0xFFFFFFFFu, BlendClass blend = BlendClass.Normal) => new LeafSpec
        {
            Texture = texture,
            Blend = blend,
            Region = region,
            BaseColor = baseColor,
            EmitFlags = 0u,
            SlackHint = 0,
            Fill = RadialFillParams.None,
        };
}

/// <summary>
/// 叶发射器（纯函数集合）。三条路径：纯色/单图 quad、九宫格 ≤9 quad、填充。
/// 全部写进调用方给的 span——发射器不分配，一次 Extract 的全部叶共用一块 scratch。
/// </summary>
public static class LeafEmitter
{
    /// <summary>九宫格的最大切片数。</summary>
    public const int Scale9MaxQuads = 9;

    /// <summary>
    /// 本 spec 最多会发射多少个 quad（调用方按此备 scratch；**不依赖宽高**，
    /// 于是缓冲尺寸在遍历前就能定，遍历中不会因为某个叶大一点而重新分配）。
    /// </summary>
    public static int MaxQuads(in LeafSpec spec) =>
        spec.Fill.Method == FillMethod.None && spec.Region.HasGrid ? Scale9MaxQuads : 1;

    /// <summary>
    /// 发射一个叶（统一入口）。返回写进 <paramref name="quads"/> 的实例数（可能是 0）。
    ///
    /// 三条路径的优先级与 fork <c>Image.OnPopulateMesh</c> 一致：**填充优先于九宫格**——
    /// 一张带九宫格的图设了 fillMethod，fork 走 FillMesh 而不切片，这里同样不切片
    /// （两者叠加没有定义良好的语义：切片后的九块各自按什么角度填？）。
    /// </summary>
    /// <param name="spec">内容描述。</param>
    /// <param name="width">叶框宽（本地空间）。</param>
    /// <param name="height">叶框高。</param>
    /// <param name="quads">输出实例（长度须 ≥ <see cref="MaxQuads"/>）。</param>
    /// <param name="baseColors">输出基色（与 <paramref name="quads"/> 同长；机制 7 的定义域）。</param>
    public static int Emit(in LeafSpec spec, float width, float height,
        Span<QuadInstance> quads, Span<uint> baseColors)
    {
        int n = EmitGeometry(in spec, width, height, quads);
        UiAssert.That(baseColors.Length >= n,
            "baseColor 输出 span 短于发射出的实例数（颜色纯函数的定义域必须一一对应）");
        if (baseColors.Length < n) return 0;
        for (int i = 0; i < n; i++) baseColors[i] = spec.BaseColor;
        return n;
    }

    private static int EmitGeometry(in LeafSpec spec, float width, float height, Span<QuadInstance> quads)
    {
        // 零宽/零高/非有限尺寸的叶发射 0 个实例。留一个 size 0 的 quad 也画不出像素，
        // 但它会占一个实例、进上传区间、进规范化哈希——「不可见」应该等于「不存在」。
        if (!(width > 0f) || !(height > 0f)) return 0;
        if (quads.Length == 0) return 0;

        FillMethod method = spec.Fill.Method;
        if (method != FillMethod.None) return EmitFilled(in spec, width, height, quads);
        if (spec.Region.HasGrid) return EmitScale9(in spec.Region, width, height, quads);
        return EmitPlain(in spec.Region, width, height, quads);
    }

    // ── 路径 1：单 quad ──────────────────────────────────────────────────────

    /// <summary>整块区域一个 quad（纯色与单图共用；最简路径）。</summary>
    public static int EmitPlain(in SpriteRegion region, float width, float height, Span<QuadInstance> quads)
    {
        if (!(width > 0f) || !(height > 0f) || quads.Length == 0) return 0;
        quads[0] = MakeQuad(0f, 0f, width, height,
            region.Uv.x, region.Uv.y, region.Uv.z, region.Uv.w);
        return 1;
    }

    // ── 路径 2：九宫格 ───────────────────────────────────────────────────────

    /// <summary>
    /// 九宫格切片（≤9 个 quad；退化格不发射，于是「无左边框」是 6 片、「无边框」是 1 片）。
    ///
    /// 分格算法照 fork <c>Image.SliceFill</c>（Assets/Scripts/Core/Image.cs 286-）的语义，
    /// 两处改动，每处都是病因驱动的：
    ///  ① fork 在「框比两条边框还窄」时用 <c>tmp = left / right; w·tmp/(1+tmp)</c> 按比例压边框，
    ///     右边框为 0 时那是除零 → ±∞ → NaN 顶点。这里直接写等价形式 <c>w·left/(left+right)</c>，
    ///     并把 <c>left+right == 0</c> 单列（两条边框都是 0，中格占满）。
    ///     九宫格「只有左边框」在 UI 里很常见（进度条底），fork 那条路会画出一片 NaN。
    ///  ② UV 的 y 表与几何表同序（见文件头）：我们的 v 向下，不需要 fork 的倒排。
    /// </summary>
    public static int EmitScale9(in SpriteRegion region, float width, float height, Span<QuadInstance> quads)
    {
        if (!region.HasGrid) return EmitPlain(in region, width, height, quads);
        if (!(width > 0f) || !(height > 0f) || quads.Length == 0) return 0;

        Span<float> xs = stackalloc float[4];
        Span<float> ys = stackalloc float[4];
        Span<float> us = stackalloc float[4];
        Span<float> vs = stackalloc float[4];

        SplitAxis(width, region.BorderLeft, region.BorderRight, xs);
        SplitAxis(height, region.BorderTop, region.BorderBottom, ys);

        float sx = (region.Uv.z - region.Uv.x) / region.SourceWidth;
        float sy = (region.Uv.w - region.Uv.y) / region.SourceHeight;
        us[0] = region.Uv.x;
        us[1] = region.Uv.x + region.BorderLeft * sx;
        us[2] = region.Uv.x + (region.SourceWidth - region.BorderRight) * sx;
        us[3] = region.Uv.z;
        vs[0] = region.Uv.y;
        vs[1] = region.Uv.y + region.BorderTop * sy;
        vs[2] = region.Uv.y + (region.SourceHeight - region.BorderBottom) * sy;
        vs[3] = region.Uv.w;

        int n = 0;
        for (int row = 0; row < 3; row++)
        {
            float y0 = ys[row], y1 = ys[row + 1];
            if (!(y1 > y0)) continue;                 // 退化行（边框为 0 / 框被压扁）
            for (int col = 0; col < 3; col++)
            {
                float x0 = xs[col], x1 = xs[col + 1];
                if (!(x1 > x0)) continue;             // 退化格
                if (n >= quads.Length)
                {
                    UiAssert.That(false, "九宫格输出 span 不足（MaxQuads 给的是 9）");
                    return n;
                }
                quads[n++] = MakeQuad(x0, y0, x1 - x0, y1 - y0,
                    us[col], vs[row], us[col + 1], vs[row + 1]);
            }
        }
        return n;
    }

    /// <summary>
    /// 一根轴上的四个分割点（0 / 首边框 / 末边框起点 / 全长）。
    /// 框放得下两条边框 ⇒ 边框保持原尺寸、中格拉伸；放不下 ⇒ 两条边框按比例压缩、中格归零。
    /// </summary>
    private static void SplitAxis(float length, float head, float tail, Span<float> outPoints)
    {
        if (head < 0f) head = 0f;
        if (tail < 0f) tail = 0f;

        outPoints[0] = 0f;
        outPoints[3] = length;
        if (length >= head + tail)
        {
            outPoints[1] = head;
            outPoints[2] = length - tail;
            return;
        }
        float total = head + tail;
        float cut = total > 0f ? length * (head / total) : 0f;
        outPoints[1] = cut;
        outPoints[2] = cut;                          // 中格被压成零宽 → 该列/行不发射
    }

    // ── 路径 3：填充 ────────────────────────────────────────────────────────

    /// <summary>
    /// 填充路径。线性填充是**几何裁短**（rect 与 UV 一起收缩，仍是普通实例）；
    /// 径向填充满格发整图、空格不发、其余发整图 + shader 求值参数（<see cref="RadialFill.Write"/>）。
    /// </summary>
    private static int EmitFilled(in LeafSpec spec, float width, float height, Span<QuadInstance> quads)
    {
        float amount = RadialFill.Clamp01(spec.Fill.Amount);
        if (amount <= 0f) return 0;                  // 空进度 = 不发射，不是发一个透明 quad

        Vector4 uv = spec.Region.Uv;
        switch (spec.Fill.Method)
        {
            case FillMethod.Horizontal:
            {
                float w = width * amount;
                bool fromRight = (OriginHorizontal)spec.Fill.Origin == OriginHorizontal.Right;
                float x0 = fromRight ? width - w : 0f;
                float u0 = fromRight ? Lerp(uv.z, uv.x, amount) : uv.x;
                float u1 = fromRight ? uv.z : Lerp(uv.x, uv.z, amount);
                if (!(w > 0f)) return 0;
                quads[0] = MakeQuad(x0, 0f, w, height, u0, uv.y, u1, uv.w);
                return 1;
            }
            case FillMethod.Vertical:
            {
                float h = height * amount;
                bool fromBottom = (OriginVertical)spec.Fill.Origin == OriginVertical.Bottom;
                float y0 = fromBottom ? height - h : 0f;
                float v0 = fromBottom ? Lerp(uv.w, uv.y, amount) : uv.y;
                float v1 = fromBottom ? uv.w : Lerp(uv.y, uv.w, amount);
                if (!(h > 0f)) return 0;
                quads[0] = MakeQuad(0f, y0, width, h, uv.x, v0, uv.z, v1);
                return 1;
            }
            default:
            {
                // 径向：整图一个 quad；不满格才带 shader 求值参数（满格是整图，不该付 shader 的钱）。
                quads[0] = MakeQuad(0f, 0f, width, height, uv.x, uv.y, uv.z, uv.w);
                RadialFill.Write(ref quads[0], in spec.Fill);
                return 1;
            }
        }
    }

    // ── 公共小工具 ──────────────────────────────────────────────────────────

    /// <summary>按「角归位」装配一个 quad（rect = min 角 + size；四角 UV 各就各位）。</summary>
    private static QuadInstance MakeQuad(float x, float y, float w, float h,
        float u0, float v0, float u1, float v1) => new QuadInstance
        {
            Rect = new Vector4(x, y, w, h),
            UvA = new Vector4(u0, v0, u1, v0),
            UvB = new Vector4(u0, v1, u1, v1),
        };

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
