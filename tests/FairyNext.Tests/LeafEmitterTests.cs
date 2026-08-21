using System.Collections.Generic;
using FairyNext.Contracts;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-13 叶发射器 + 填充 ABI + QuadReassembler 用例（Program 的 partial 分片）。
///
/// 对齐 docs/architecture.md「平面三 · 渲染平面」：机制 1（实例流是唯一主路径）、
/// 机制 9 的末句（**径向填充不落孤岛**：参数进 aux/extra 由 shader 求值）、
/// 机制 4（无一级画错——回收器准入不过只跳过并计数，绝不近似）。
/// 发射器是纯函数，本片一棵树都不建：九宫格切几片、UV 对不对、退化格掉不掉，全在这里定死。
/// </summary>
public static partial class Program
{
    private static void LeafEmitterSuite()
    {
        // 九宫格：切片数与 UV
        PlainImageIsOneQuadCoveringTheBox();
        Scale9CutsNineSlices();
        Scale9UvFollowsSourcePixels();
        Scale9GeometryTilesTheBoxWithoutGaps();
        Scale9WithoutGridIsOneQuad();
        Scale9ZeroBorderDropsThatColumn();
        Scale9NarrowBoxSquashesBordersWithoutNaN();
        Scale9RejectsOutOfBoundsGrid();

        // 审计补（M1-14b）：挤压支路 golden + 发射器门缺口
        Scale9SquashGoldenNarrowWidth();
        Scale9SquashGoldenShortHeight();
        Scale9SquashGoldenBothAxesAsymmetric();
        NegativeGridOriginIsRejected();
        NonFiniteSizeEmitsNothingOnEveryPath();
        TilingSpecRefusesToEmit();

        // 填充：线性走几何、径向走 ABI
        LinearFillCropsRectAndUv();
        LinearFillFromRightTakesTheFarEnd();
        FillAmountZeroEmitsNothing();
        RadialFullAmountIsPlainQuad();

        // 径向填充的位布局（对生成常量回读）
        RadialCenterAndStartTurnsCoverAllOrigins();
        RadialAuxPacksThroughGeneratedShifts();
        RadialAuxRoundTrips();
        RadialExtraIsThePolarForm();
        RadialCounterClockwiseStartsAtTheOtherEnd();
        FillWinsOverScale9();

        // QuadReassembler（移植件）
        ReassemblerAcceptsAPlainQuad();
        ReassemblerRebuildsSharedVertexGrid();
        ReassemblerRejectsNonRectangularPair();
        ReassemblerRejectsCornerGradient();
        ReassemblerRejectsDegenerateAndOddTail();
        ReassemblerScratchIsCallerOwned();
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    /// <summary>整张图集 128×128 上的一块 64×64 图元，UV 落在 [0.25,0.5]²。</summary>
    private static SpriteRegion LeRegion() =>
        SpriteRegion.Full(new Vector4(0.25f, 0.25f, 0.75f, 0.75f), 64f, 64f);

    /// <summary>同上但带 (16,8,32,48) 的九宫格（左 16 / 右 16 / 上 8 / 下 8）。</summary>
    private static SpriteRegion LeSliced() =>
        SpriteRegion.Sliced(new Vector4(0.25f, 0.25f, 0.75f, 0.75f), 64f, 64f,
            new Vector4(16f, 8f, 32f, 48f));

    private static bool LeNear(float a, float b, float eps = 1e-5f) => MathF.Abs(a - b) <= eps;

    private static bool LeRectIs(in QuadInstance q, float x, float y, float w, float h) =>
        LeNear(q.Rect.x, x) && LeNear(q.Rect.y, y) && LeNear(q.Rect.z, w) && LeNear(q.Rect.w, h);

    private static bool LeUvIs(in QuadInstance q, float u0, float v0, float u1, float v1) =>
        LeNear(q.UvA.x, u0) && LeNear(q.UvA.y, v0) && LeNear(q.UvA.z, u1) && LeNear(q.UvA.w, v0) &&
        LeNear(q.UvB.x, u0) && LeNear(q.UvB.y, v1) && LeNear(q.UvB.z, u1) && LeNear(q.UvB.w, v1);

    private static int LeEmit(in LeafSpec spec, float w, float h, out QuadInstance[] quads)
    {
        var buf = new QuadInstance[16];
        var colors = new uint[16];
        int n = LeafEmitter.Emit(in spec, w, h, buf, colors);
        quads = buf;
        return n;
    }

    // ── 九宫格 ──────────────────────────────────────────────────────────────

    private static void PlainImageIsOneQuadCoveringTheBox()
    {
        LeafSpec spec = LeafSpec.Image(new TexId(1), LeRegion());
        int n = LeEmit(in spec, 100f, 40f, out QuadInstance[] q);
        Check("发射: 无九宫格 = 1 quad 覆盖整框整区",
            n == 1 && LeRectIs(in q[0], 0f, 0f, 100f, 40f) && LeUvIs(in q[0], 0.25f, 0.25f, 0.75f, 0.75f)
            && q[0].Route == 0u);          // 发射器不写 route（三分量由流盖写）
    }

    private static void Scale9CutsNineSlices()
    {
        LeafSpec spec = LeafSpec.Image(new TexId(1), LeSliced());
        int n = LeEmit(in spec, 100f, 60f, out QuadInstance[] q);

        // 左 16 / 右 16、上 8 / 下 8 保持原尺寸，中间拉伸：列 16|68|16，行 8|44|8。
        bool corners =
            LeRectIs(in q[0], 0f, 0f, 16f, 8f) &&      // 左上
            LeRectIs(in q[1], 16f, 0f, 68f, 8f) &&     // 上中
            LeRectIs(in q[2], 84f, 0f, 16f, 8f) &&     // 右上
            LeRectIs(in q[4], 16f, 8f, 68f, 44f) &&    // 正中
            LeRectIs(in q[8], 84f, 52f, 16f, 8f);      // 右下
        Check("发射: 九宫格切 9 片且边框不拉伸", n == 9 && corners);
    }

    private static void Scale9UvFollowsSourcePixels()
    {
        LeafSpec spec = LeafSpec.Image(new TexId(1), LeSliced());
        LeEmit(in spec, 100f, 60f, out QuadInstance[] q);

        // uv 宽 0.5 覆盖 64 源像素 ⇒ 每像素 1/128。左边框 16px → u ∈ [0.25, 0.375]；
        // 上边框 8px → v ∈ [0.25, 0.3125]。**v 向下**：不需要 fork 那次倒排。
        const float px = 0.5f / 64f;
        bool topLeft = LeUvIs(in q[0], 0.25f, 0.25f, 0.25f + 16f * px, 0.25f + 8f * px);
        bool center = LeUvIs(in q[4], 0.25f + 16f * px, 0.25f + 8f * px,
            0.75f - 16f * px, 0.75f - 8f * px);
        bool bottomRight = LeUvIs(in q[8], 0.75f - 16f * px, 0.75f - 8f * px, 0.75f, 0.75f);
        Check("发射: 九宫格 UV 按源像素换算（v 向下，无倒排）", topLeft && center && bottomRight);
    }

    private static void Scale9GeometryTilesTheBoxWithoutGaps()
    {
        LeafSpec spec = LeafSpec.Image(new TexId(1), LeSliced());
        int n = LeEmit(in spec, 100f, 60f, out QuadInstance[] q);

        float area = 0f;
        float maxX = 0f, maxY = 0f;
        for (int i = 0; i < n; i++)
        {
            area += q[i].Rect.z * q[i].Rect.w;
            maxX = MathF.Max(maxX, q[i].Rect.x + q[i].Rect.z);
            maxY = MathF.Max(maxY, q[i].Rect.y + q[i].Rect.w);
        }
        // 九格恰好铺满整框（无缝无叠）：面积和 == 框面积，且右下角收口于框。
        Check("发射: 九格铺满整框（面积和 == 框面积，收口于右下角）",
            LeNear(area, 100f * 60f, 1e-3f) && LeNear(maxX, 100f) && LeNear(maxY, 60f));
    }

    private static void Scale9WithoutGridIsOneQuad()
    {
        var region = SpriteRegion.Sliced(new Vector4(0f, 0f, 1f, 1f), 64f, 64f, Vector4.zero);
        LeafSpec spec = LeafSpec.Image(new TexId(1), region);
        int n = LeEmit(in spec, 30f, 30f, out QuadInstance[] q);
        Check("发射: 退化九宫格（w/h 为 0）= 1 quad",
            !region.HasGrid && n == 1 && LeRectIs(in q[0], 0f, 0f, 30f, 30f));
    }

    private static void Scale9ZeroBorderDropsThatColumn()
    {
        // 左边框为 0（grid.x == 0）：第 0 列宽度为 0 ⇒ 该列整列不发射 ⇒ 3 行 × 2 列 = 6 片。
        var region = SpriteRegion.Sliced(new Vector4(0f, 0f, 1f, 1f), 64f, 64f,
            new Vector4(0f, 8f, 48f, 48f));
        LeafSpec spec = LeafSpec.Image(new TexId(1), region);
        int n = LeEmit(in spec, 100f, 60f, out QuadInstance[] q);
        bool firstIsMiddleColumn = LeRectIs(in q[0], 0f, 0f, 84f, 8f);   // 中列从 x=0 起
        Check("发射: 边界为 0 的退化格不发射（左边框 0 ⇒ 6 片）", n == 6 && firstIsMiddleColumn);

        // 四条边框全为 0：只剩中格 ⇒ 1 片，且等价于整图。
        var noBorders = SpriteRegion.Sliced(new Vector4(0f, 0f, 1f, 1f), 64f, 64f,
            new Vector4(0f, 0f, 64f, 64f));
        LeafSpec spec2 = LeafSpec.Image(new TexId(1), noBorders);
        int n2 = LeEmit(in spec2, 100f, 60f, out QuadInstance[] q2);
        Check("发射: 四边框全 0 的九宫格退化为 1 片整图",
            n2 == 1 && LeRectIs(in q2[0], 0f, 0f, 100f, 60f) && LeUvIs(in q2[0], 0f, 0f, 1f, 1f));
    }

    private static void Scale9NarrowBoxSquashesBordersWithoutNaN()
    {
        // 右边框为 0 的九宫格 + 比左边框还窄的框：fork 的 `tmp = left/right` 在此除零 → ∞ → NaN 顶点。
        // 我们走等价的 `w · left/(left+right)`，right==0 时结果就是 w（左边框吃满，中列归零）。
        var region = SpriteRegion.Sliced(new Vector4(0f, 0f, 1f, 1f), 64f, 64f,
            new Vector4(20f, 20f, 44f, 44f));       // 左 20 / 右 0 / 上 20 / 下 0
        LeafSpec spec = LeafSpec.Image(new TexId(1), region);
        int n = LeEmit(in spec, 10f, 10f, out QuadInstance[] q);

        bool finite = true;
        for (int i = 0; i < n; i++)
        {
            if (float.IsNaN(q[i].Rect.x) || float.IsNaN(q[i].Rect.y)
                || float.IsNaN(q[i].Rect.z) || float.IsNaN(q[i].Rect.w)) finite = false;
        }
        // 左边框按比例占满 10（右边框 0），中列与右列归零 ⇒ 只剩左上一格。
        Check("发射: 边框放不下时按比例压缩，右边框为 0 也不产 NaN（fork 除零反例）",
            finite && n == 1 && LeRectIs(in q[0], 0f, 0f, 10f, 10f));
    }

    /// <summary>逐字节比一个 quad（golden 用：期望值全部精确可表示，不设 eps）。</summary>
    private static bool LeExactly(in QuadInstance q, float x, float y, float w, float h,
        float u0, float v0, float u1, float v1) =>
        q.Rect.x == x && q.Rect.y == y && q.Rect.z == w && q.Rect.w == h
        && q.UvA.x == u0 && q.UvA.y == v0 && q.UvA.z == u1 && q.UvA.w == v0
        && q.UvB.x == u0 && q.UvB.y == v1 && q.UvB.z == u1 && q.UvB.w == v1;

    /// <summary>整条发射结果对 golden 表逐字节比（数量与每片的 rect/UV 一次钉死）。</summary>
    private static bool LeGolden(in LeafSpec spec, float w, float h, float[][] expect)
    {
        int n = LeEmit(in spec, w, h, out QuadInstance[] q);
        if (n != expect.Length) return false;
        for (int i = 0; i < n; i++)
        {
            float[] e = expect[i];
            if (!LeExactly(in q[i], e[0], e[1], e[2], e[3], e[4], e[5], e[6], e[7])) return false;
        }
        return true;
    }

    // ── 挤压支路 golden（M1-14b 审计补）────────────────────────────────────
    //
    // 变异史（2026-08 审计）：SplitAxis 的「框放不下两条边框」支路曾有两个真 bug 变异
    // 静默通过——旧的唯一挤压用例右边框为 0，`cut = length·head/(head+tail)` 在 tail=0 时
    // 对「head 换 tail」「Min(head, length)」一类错法输出**巧合相同**，且那条用例不查 UV；
    // 「中格必须零宽」这类断言也杀不死后者（错的 cut 同样让中格零宽）。
    // 三条 golden 把 rect 与 UV 全数组逐字节钉死（期望值全为精确可表示的浮点），巧合失效。
    // 夹具：LeSliced = UV [0.25,0.75]²、源 64²、边框 L16/R16/T8/B8，每源像素 UV = 1/128。

    private static void Scale9SquashGoldenNarrowWidth()
    {
        // 框宽 24 < 左右边框和 32 ⇒ 两边框按比例压至 12、中列零宽；行向放得下照旧（8|44|8）。
        LeafSpec spec = LeafSpec.Image(new TexId(1), LeSliced());
        Check("发射: 挤压 golden ①（宽 24 < 边框和 32：边框压至 12/12，中列不发射）",
            LeGolden(in spec, 24f, 60f, new[]
            {
                new[] { 0f, 0f, 12f, 8f, 0.25f, 0.25f, 0.375f, 0.3125f },
                new[] { 12f, 0f, 12f, 8f, 0.625f, 0.25f, 0.75f, 0.3125f },
                new[] { 0f, 8f, 12f, 44f, 0.25f, 0.3125f, 0.375f, 0.6875f },
                new[] { 12f, 8f, 12f, 44f, 0.625f, 0.3125f, 0.75f, 0.6875f },
                new[] { 0f, 52f, 12f, 8f, 0.25f, 0.6875f, 0.375f, 0.75f },
                new[] { 12f, 52f, 12f, 8f, 0.625f, 0.6875f, 0.75f, 0.75f },
            }));
    }

    private static void Scale9SquashGoldenShortHeight()
    {
        // 框高 12 < 上下边框和 16 ⇒ 上下边框压至 6/6、中行零高；列向照旧（16|68|16）。
        LeafSpec spec = LeafSpec.Image(new TexId(1), LeSliced());
        Check("发射: 挤压 golden ②（高 12 < 边框和 16：边框压至 6/6，中行不发射）",
            LeGolden(in spec, 100f, 12f, new[]
            {
                new[] { 0f, 0f, 16f, 6f, 0.25f, 0.25f, 0.375f, 0.3125f },
                new[] { 16f, 0f, 68f, 6f, 0.375f, 0.25f, 0.625f, 0.3125f },
                new[] { 84f, 0f, 16f, 6f, 0.625f, 0.25f, 0.75f, 0.3125f },
                new[] { 0f, 6f, 16f, 6f, 0.25f, 0.6875f, 0.375f, 0.75f },
                new[] { 16f, 6f, 68f, 6f, 0.375f, 0.6875f, 0.625f, 0.75f },
                new[] { 84f, 6f, 16f, 6f, 0.625f, 0.6875f, 0.75f, 0.75f },
            }));
    }

    private static void Scale9SquashGoldenBothAxesAsymmetric()
    {
        // 非对称边框（L24/R8、T8/B24）+ 双向挤压：x 向 cut = 16·24/32 = 12（左吃 3/4），
        // y 向 cut = 20·8/32 = 5（上吃 1/4）。对称夹具杀不死「head 换 tail」——这条杀得死。
        var region = SpriteRegion.Sliced(new Vector4(0.25f, 0.25f, 0.75f, 0.75f), 64f, 64f,
            new Vector4(24f, 8f, 32f, 32f));
        LeafSpec spec = LeafSpec.Image(new TexId(1), region);
        Check("发射: 挤压 golden ③（非对称边框双向挤压：比例分割 12|4 与 5|15）",
            LeGolden(in spec, 16f, 20f, new[]
            {
                new[] { 0f, 0f, 12f, 5f, 0.25f, 0.25f, 0.4375f, 0.3125f },
                new[] { 12f, 0f, 4f, 5f, 0.6875f, 0.25f, 0.75f, 0.3125f },
                new[] { 0f, 5f, 12f, 15f, 0.25f, 0.5625f, 0.4375f, 0.75f },
                new[] { 12f, 5f, 4f, 15f, 0.6875f, 0.5625f, 0.75f, 0.75f },
            }));
    }

    private static void NegativeGridOriginIsRejected()
    {
        // 负的 grid 原点（损坏/手写资产）：HasGrid 的下界检查判「无九宫格」。若删掉
        // Grid.x/y >= 0 两项，BorderLeft = -8 会进 UV 表：us[1] = uv.x - 8px 落到邻图元上。
        var region = SpriteRegion.Sliced(new Vector4(0.25f, 0.25f, 0.75f, 0.75f), 64f, 64f,
            new Vector4(-8f, -8f, 32f, 32f));
        LeafSpec spec = LeafSpec.Image(new TexId(1), region);
        int n = LeEmit(in spec, 100f, 60f, out QuadInstance[] q);
        Check("发射: 负 grid 原点被判「无九宫格」（不产落在邻图元上的 UV）",
            !region.HasGrid && n == 1 && LeRectIs(in q[0], 0f, 0f, 100f, 60f)
            && LeUvIs(in q[0], 0.25f, 0.25f, 0.75f, 0.75f));
    }

    private static void NonFiniteSizeEmitsNothingOnEveryPath()
    {
        // 审计实测：旧判据 `!(w > 0f)` 只吃零/负/NaN，`+∞ > 0` 为真照发。四条路径同门。
        LeafSpec plain = LeafSpec.Image(new TexId(1), LeRegion());
        int a = LeEmit(in plain, float.PositiveInfinity, 40f, out _);
        int b = LeEmit(in plain, 40f, float.PositiveInfinity, out _);
        LeafSpec sliced = LeafSpec.Image(new TexId(1), LeSliced());
        int c = LeEmit(in sliced, float.PositiveInfinity, float.PositiveInfinity, out _);
        LeafSpec filled = LeafSpec.Image(new TexId(1), LeRegion());
        filled.Fill = RadialFillParams.Of(FillMethod.Horizontal, (byte)OriginHorizontal.Left, 0.5f);
        int d = LeEmit(in filled, float.PositiveInfinity, 20f, out _);
        Check("发射: 非有限尺寸拒发 0 实例（整图/九宫/填充同门；`+∞ > 0` 曾放行）",
            a == 0 && b == 0 && c == 0 && d == 0);
    }

    private static void TilingSpecRefusesToEmit()
    {
        // 平铺（scaleByTile / tileGridIndice）M2 前无表达：按机制 4 拒发而不是画成拉伸。
        var byTile = SpriteRegion.Full(new Vector4(0f, 0f, 1f, 1f), 64f, 64f);
        byTile.ScaleByTile = true;
        LeafSpec s1 = LeafSpec.Image(new TexId(1), byTile);
        int a = LeEmit(in s1, 100f, 60f, out _);

        var tiledGrid = SpriteRegion.Sliced(new Vector4(0f, 0f, 1f, 1f), 64f, 64f,
            new Vector4(16f, 16f, 32f, 32f));
        tiledGrid.TileGridIndice = 1 << 4;           // 中格平铺
        LeafSpec s2 = LeafSpec.Image(new TexId(1), tiledGrid);
        int b = LeEmit(in s2, 100f, 60f, out _);

        // fork 优先序：fillMethod 一设，scale9/tile 整个不看 ⇒ 带填充的平铺 spec 照常发射。
        LeafSpec s3 = LeafSpec.Image(new TexId(1), tiledGrid);
        s3.Fill = RadialFillParams.Of(FillMethod.Radial360, (byte)Origin360.Top, 0.5f);
        int c = LeEmit(in s3, 100f, 60f, out _);

        Check("发射: 平铺声明拒发 0 实例（画成拉伸是「近似」；填充优先序下不拒）",
            s1.Region.HasTiling && s2.Region.HasTiling && a == 0 && b == 0 && c == 1);
    }

    private static void Scale9RejectsOutOfBoundsGrid()
    {
        // 格子越出源框：fork 会产出 UV 落到邻图元上的边片（图集里最难查的一类花屏）。
        var region = SpriteRegion.Sliced(new Vector4(0.25f, 0.25f, 0.75f, 0.75f), 64f, 64f,
            new Vector4(40f, 8f, 40f, 48f));        // x + w = 80 > 64
        LeafSpec spec = LeafSpec.Image(new TexId(1), region);
        int n = LeEmit(in spec, 100f, 60f, out QuadInstance[] q);
        Check("发射: 越出源框的九宫格被判为「无九宫格」（不产越界 UV）",
            !region.HasGrid && n == 1 && LeUvIs(in q[0], 0.25f, 0.25f, 0.75f, 0.75f));
    }

    // ── 填充 ────────────────────────────────────────────────────────────────

    private static void LinearFillCropsRectAndUv()
    {
        LeafSpec spec = LeafSpec.Image(new TexId(1), LeRegion());
        spec.Fill = RadialFillParams.Of(FillMethod.Horizontal, (byte)OriginHorizontal.Left, 0.25f);
        int n = LeEmit(in spec, 80f, 20f, out QuadInstance[] q);
        Check("填充: 水平填充 = 几何裁短的普通实例（rect 与 UV 同步收缩，零 flag）",
            n == 1 && LeRectIs(in q[0], 0f, 0f, 20f, 20f)
            && LeUvIs(in q[0], 0.25f, 0.25f, 0.375f, 0.75f)
            && q[0].Flags == 0u && q[0].Aux == 0u);
    }

    private static void LinearFillFromRightTakesTheFarEnd()
    {
        LeafSpec spec = LeafSpec.Image(new TexId(1), LeRegion());
        spec.Fill = RadialFillParams.Of(FillMethod.Vertical, (byte)OriginVertical.Bottom, 0.5f);
        int n = LeEmit(in spec, 40f, 80f, out QuadInstance[] q);
        Check("填充: 自下而上的垂直填充取远端（rect 靠下、UV 取下半）",
            n == 1 && LeRectIs(in q[0], 0f, 40f, 40f, 40f)
            && LeUvIs(in q[0], 0.25f, 0.5f, 0.75f, 0.75f));
    }

    private static void FillAmountZeroEmitsNothing()
    {
        LeafSpec spec = LeafSpec.Image(new TexId(1), LeRegion());
        spec.Fill = RadialFillParams.Of(FillMethod.Radial360, (byte)Origin360.Top, 0f);
        int n = LeEmit(in spec, 40f, 40f, out _);

        LeafSpec linear = LeafSpec.Image(new TexId(1), LeRegion());
        linear.Fill = RadialFillParams.Of(FillMethod.Horizontal, 0, float.NaN);
        int n2 = LeEmit(in linear, 40f, 40f, out _);

        Check("填充: 空进度不发射（0 个实例，不是一个透明 quad；NaN 同路）", n == 0 && n2 == 0);
    }

    private static void RadialFullAmountIsPlainQuad()
    {
        LeafSpec spec = LeafSpec.Image(new TexId(1), LeRegion());
        spec.Fill = RadialFillParams.Of(FillMethod.Radial360, (byte)Origin360.Top, 1f);
        int n = LeEmit(in spec, 40f, 40f, out QuadInstance[] q);
        Check("填充: 满格径向 = 整图普通实例（不付 shader 分支的钱）",
            n == 1 && q[0].Flags == 0u && q[0].Aux == 0u && q[0].Extra == Vector4.zero
            && LeRectIs(in q[0], 0f, 0f, 40f, 40f));
    }

    private static void RadialCenterAndStartTurnsCoverAllOrigins()
    {
        // 12 组 (method, origin) 的 golden 表（M1-14b 审计补：此前只钉了 4 组）。
        // 编号 = fork FieldTypes.cs = .fui 字节值——错一组，旧资源那一档就整体画错。
        // 期望值按 RadialFill.StartTurns 文档里的规则独立推导：center = pivot 归一化坐标；
        // start = 从 pivot 出发沿顺时针遇到的第一条覆盖边（turns，0 = +x，y 向下故正向 = 顺时针）；
        // 360° 的起始边 = origin 指的方向。PackExtra 对每组再对拍一次求值形态。
        bool ok = true;
        void Row(FillMethod m, byte o, float cx, float cy, float start)
        {
            Vector2 c = RadialFill.Center(m, o);
            if (c.x != cx || c.y != cy || RadialFill.StartTurns(m, o) != start) ok = false;
            var p = RadialFillParams.Of(m, o, 0.5f);
            Vector4 e = RadialFill.PackExtra(in p);
            if (e.x != cx || e.y != cy || e.z != start || !(e.w > 0f)) ok = false;
        }
        Row(FillMethod.Radial90, (byte)Origin90.TopLeft, 0f, 0f, 0f);
        Row(FillMethod.Radial90, (byte)Origin90.TopRight, 1f, 0f, 0.25f);
        Row(FillMethod.Radial90, (byte)Origin90.BottomLeft, 0f, 1f, 0.75f);
        Row(FillMethod.Radial90, (byte)Origin90.BottomRight, 1f, 1f, 0.5f);
        Row(FillMethod.Radial180, (byte)Origin180.Top, 0.5f, 0f, 0f);
        Row(FillMethod.Radial180, (byte)Origin180.Bottom, 0.5f, 1f, 0.5f);
        Row(FillMethod.Radial180, (byte)Origin180.Left, 0f, 0.5f, 0.75f);
        Row(FillMethod.Radial180, (byte)Origin180.Right, 1f, 0.5f, 0.25f);
        Row(FillMethod.Radial360, (byte)Origin360.Top, 0.5f, 0.5f, 0.75f);
        Row(FillMethod.Radial360, (byte)Origin360.Bottom, 0.5f, 0.5f, 0.25f);
        Row(FillMethod.Radial360, (byte)Origin360.Left, 0.5f, 0.5f, 0.5f);
        Row(FillMethod.Radial360, (byte)Origin360.Right, 0.5f, 0.5f, 0f);
        Check("填充: center/startTurns 的 12 组 (method,origin) golden 表盖全（编号 = fork = .fui 字节）", ok);
    }

    private static void RadialAuxPacksThroughGeneratedShifts()
    {
        LeafSpec spec = LeafSpec.Image(new TexId(1), LeRegion());
        var p = RadialFillParams.Of(FillMethod.Radial180, (byte)Origin180.Left, 0.25f, clockwise: false);
        spec.Fill = p;
        int n = LeEmit(in spec, 40f, 40f, out QuadInstance[] q);

        uint aux = q[0].Aux;
        bool flagSet = ((q[0].Flags >> AbiLayout.FlagsRadialFillShift) & 1u) != 0;
        bool fields =
            AbiLayout.RadialFillMethod(aux) == (uint)FillMethod.Radial180 &&
            AbiLayout.RadialFillOrigin(aux) == (uint)Origin180.Left &&
            AbiLayout.RadialFillClockwise(aux) == 0u &&
            AbiLayout.RadialFillAmount(aux) == RadialFill.QuantizeAmount(0.25f) &&
            AbiLayout.RadialFillReserved(aux) == 0u;         // 保留位写零（ABI append-only 的运行期半边）
        // 位域表必须覆盖整个 u32：没被认领的位将来会被两处各自认领。
        bool covers = Abi.RadialFillAuxBits[0].Shift == 0
            && Abi.RadialFillAuxBits[Abi.RadialFillAuxBits.Length - 1].Shift
               + Abi.RadialFillAuxBits[Abi.RadialFillAuxBits.Length - 1].Width == 32;
        Check("填充: 径向参数按生成常量落进 aux（flags.radialFill 置位、保留位零、位域覆盖全 32 位）",
            n == 1 && flagSet && fields && covers);
    }

    private static void RadialAuxRoundTrips()
    {
        bool ok = true;
        var methods = new[] { FillMethod.Radial90, FillMethod.Radial180, FillMethod.Radial360 };
        foreach (FillMethod m in methods)
        {
            for (byte origin = 0; origin < 4; origin++)
            {
                foreach (bool cw in new[] { true, false })
                {
                    foreach (float amount in new[] { 0.001f, 0.25f, 0.5f, 0.75f, 0.999f })
                    {
                        var p = RadialFillParams.Of(m, origin, amount, cw);
                        RadialFillParams back = RadialFill.UnpackAux(RadialFill.PackAux(in p));
                        if (back.Method != m || back.Origin != origin || back.Clockwise != cw) ok = false;
                        if (MathF.Abs(back.Amount - amount) > 1f / RadialFill.AmountScale) ok = false;
                    }
                }
            }
        }
        Check("填充: aux 往返无损（amount 走 u16 定点，误差 ≤ 1 级）", ok);
    }

    private static void RadialExtraIsThePolarForm()
    {
        // Radial360 + Origin.Top：圆心在中心、起始边指向 12 点（0.75 turns），顺时针扫 0.3 圈。
        var p = RadialFillParams.Of(FillMethod.Radial360, (byte)Origin360.Top, 0.3f);
        Vector4 e = RadialFill.PackExtra(in p);
        bool cd = LeNear(e.x, 0.5f) && LeNear(e.y, 0.5f) && LeNear(e.z, 0.75f) && LeNear(e.w, 0.3f, 1e-4f);

        // Radial90 + TopLeft：圆心在左上角，顺时针的第一条覆盖边是「右」(0)，扫 0.25×amount。
        var q = RadialFillParams.Of(FillMethod.Radial90, (byte)Origin90.TopLeft, 0.5f);
        Vector4 e2 = RadialFill.PackExtra(in q);
        bool corner = LeNear(e2.x, 0f) && LeNear(e2.y, 0f) && LeNear(e2.z, 0f) && LeNear(e2.w, 0.125f, 1e-4f);

        // Radial180 + Bottom：圆心在下边中点，起始边「左」(0.5)，覆盖半圈。
        var r = RadialFillParams.Of(FillMethod.Radial180, (byte)Origin180.Bottom, 1f);
        Vector4 e3 = RadialFill.PackExtra(in r);
        bool half = LeNear(e3.x, 0.5f) && LeNear(e3.y, 1f) && LeNear(e3.z, 0.5f) && LeNear(e3.w, 0.5f, 1e-4f);

        Check("填充: extra = 求值形态（中心 + 起角 + 有符号扫角，turns 制）", cd && corner && half);
    }

    private static void RadialCounterClockwiseStartsAtTheOtherEnd()
    {
        var cw = RadialFillParams.Of(FillMethod.Radial90, (byte)Origin90.TopRight, 0.4f);
        var ccw = RadialFillParams.Of(FillMethod.Radial90, (byte)Origin90.TopRight, 0.4f, clockwise: false);
        Vector4 a = RadialFill.PackExtra(in cw);
        Vector4 b = RadialFill.PackExtra(in ccw);
        // 被填区间恒在 [s0, s0+coverage] 内：逆时针只是从区间另一端起填，扫角取负。
        Check("填充: 逆时针从区间另一端起（起角 += coverage，扫角取负）",
            LeNear(a.z, 0.25f) && LeNear(b.z, 0.5f) && LeNear(b.w, -a.w) && a.w > 0f);
    }

    private static void FillWinsOverScale9()
    {
        LeafSpec spec = LeafSpec.Image(new TexId(1), LeSliced());
        spec.Fill = RadialFillParams.Of(FillMethod.Radial360, (byte)Origin360.Top, 0.5f);
        int n = LeEmit(in spec, 100f, 60f, out QuadInstance[] q);
        Check("填充: 填充优先于九宫格（同 fork Image.OnPopulateMesh：不切片）",
            n == 1 && ((q[0].Flags >> AbiLayout.FlagsRadialFillShift) & 1u) != 0);
    }

    // ── QuadReassembler（移植件）────────────────────────────────────────────

    private sealed class MeshBuilder
    {
        internal readonly List<Vector3> V = new List<Vector3>();
        internal readonly List<Vector2> Uv = new List<Vector2>();
        internal readonly List<Color32> C = new List<Color32>();
        internal readonly List<int> T = new List<int>();

        internal int Vert(float x, float y, float u, float v, byte gray = 255)
        {
            V.Add(new Vector3(x, y, 0f));
            Uv.Add(new Vector2(u, v));
            C.Add(new Color32(gray, gray, gray, 255));
            return V.Count - 1;
        }

        internal void Pair(int a, int b, int c, int d)
        {
            T.Add(a); T.Add(b); T.Add(c);
            T.Add(c); T.Add(d); T.Add(a);
        }
    }

    private static int Reassemble(MeshBuilder m, List<QuadInstance> output, out int skipped)
    {
        Span<int> scratch = stackalloc int[QuadReassembler.ScratchLength];
        return QuadReassembler.Append(output, m.V, m.Uv, m.C, m.T,
            Matrix4x4.Identity, Vector2.zero, 0u, scratch, out skipped);
    }

    private static void ReassemblerAcceptsAPlainQuad()
    {
        var m = new MeshBuilder();
        int a = m.Vert(10f, 20f, 0f, 0f);
        int b = m.Vert(40f, 20f, 1f, 0f);
        int c = m.Vert(40f, 50f, 1f, 1f);
        int d = m.Vert(10f, 50f, 0f, 1f);
        m.Pair(a, b, c, d);

        var output = new List<QuadInstance>();
        int n = Reassemble(m, output, out int skipped);
        Check("回收: 合法 quad 通过（四角归位、rect = min 角 + size、route 留零）",
            n == 1 && skipped == 0 && output.Count == 1
            && LeRectIs(output[0], 10f, 20f, 30f, 30f)
            && LeUvIs(output[0], 0f, 0f, 1f, 1f)
            && output[0].Route == 0u && output[0].Color == 0xFFFFFFFFu);
    }

    private static void ReassemblerRebuildsSharedVertexGrid()
    {
        // 共享顶点的 4×4 网格（九宫格网格的拓扑）：9 对三角形共用 16 个顶点。
        var m = new MeshBuilder();
        var ids = new int[4, 4];
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
                ids[r, c] = m.Vert(c * 10f, r * 10f, c / 3f, r / 3f);
        }
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
                m.Pair(ids[r, c], ids[r, c + 1], ids[r + 1, c + 1], ids[r + 1, c]);
        }

        var output = new List<QuadInstance>();
        int n = Reassemble(m, output, out int skipped);
        Check("回收: 共享顶点网格（16 顶点 9 格）全部回收，零跳过",
            n == 9 && skipped == 0 && LeRectIs(output[0], 0f, 0f, 10f, 10f)
            && LeRectIs(output[8], 20f, 20f, 10f, 10f));
    }

    private static void ReassemblerRejectsNonRectangularPair()
    {
        // 三角扇对：4 个不同顶点（与真 quad 同签名），但四点不占满包围盒四角 ⇒ cornerMask != 15。
        var m = new MeshBuilder();
        int a = m.Vert(0f, 0f, 0f, 0f);
        int b = m.Vert(30f, 0f, 1f, 0f);
        int c = m.Vert(20f, 30f, 1f, 1f);          // 不在任何角上
        int d = m.Vert(0f, 30f, 0f, 1f);
        m.Pair(a, b, c, d);

        var output = new List<QuadInstance>();
        int n = Reassemble(m, output, out int skipped);
        Check("回收: 非矩形（四点不占满四角）被拒并计数", n == 0 && skipped == 1 && output.Count == 0);
    }

    private static void ReassemblerRejectsCornerGradient()
    {
        var m = new MeshBuilder();
        int a = m.Vert(0f, 0f, 0f, 0f, 255);
        int b = m.Vert(30f, 0f, 1f, 0f, 128);      // 角渐变：一个实例只带一个 color
        int c = m.Vert(30f, 30f, 1f, 1f, 255);
        int d = m.Vert(0f, 30f, 0f, 1f, 255);
        m.Pair(a, b, c, d);

        var output = new List<QuadInstance>();
        int n = Reassemble(m, output, out int skipped);
        Check("回收: 四角异色被拒并计数（宁可批次差，不可像素错）",
            n == 0 && skipped == 1 && output.Count == 0);
    }

    private static void ReassemblerRejectsDegenerateAndOddTail()
    {
        // 退化（零面积）+ 三角形数非 6 的倍数（三角扇奇数尾）。
        var m = new MeshBuilder();
        int a = m.Vert(5f, 5f, 0f, 0f);
        int b = m.Vert(5f, 5f, 1f, 0f);
        int c = m.Vert(5f, 5f, 1f, 1f);
        int d = m.Vert(5f, 5f, 0f, 1f);
        m.Pair(a, b, c, d);
        m.T.Add(a); m.T.Add(b); m.T.Add(c);        // 落单的三角形

        var output = new List<QuadInstance>();
        int n = Reassemble(m, output, out int skipped);
        Check("回收: 退化包围盒与奇数尾各计一次跳过", n == 0 && skipped == 2);
    }

    private static void ReassemblerScratchIsCallerOwned()
    {
        // fork 那份是进程级 `static int[4]`：并行编包时两个线程互相踩下标，症状是偶发的
        // 「某个 quad 的 UV 取自另一个网格」。改成调用方传入后，暂存的生命周期与调用栈一致。
        var m = new MeshBuilder();
        int a = m.Vert(0f, 0f, 0f, 0f);
        int b = m.Vert(10f, 0f, 1f, 0f);
        int c = m.Vert(10f, 10f, 1f, 1f);
        int d = m.Vert(0f, 10f, 0f, 1f);
        m.Pair(a, b, c, d);

        var output = new List<QuadInstance>();
        int refused = 0, skipped0 = -1;
        var tooShort = new int[QuadReassembler.ScratchLength - 1];
        bool fired = AssertFires(() =>
            refused = QuadReassembler.Append(output, m.V, m.Uv, m.C, m.T,
                Matrix4x4.Identity, Vector2.zero, 0u, tooShort, out skipped0));

        var ok = new int[QuadReassembler.ScratchLength];
        int accepted = QuadReassembler.Append(output, m.V, m.Uv, m.C, m.T,
            Matrix4x4.Identity, Vector2.zero, 0u, ok, out int skipped1);

        Check("回收: 暂存由调用方传入（无 static 共享态；过短的 span 拒绝而不越写）",
            refused == 0 && skipped0 == 0 && accepted == 1 && skipped1 == 0 && output.Count == 1
            && (!DebugGates || fired));
    }
}
