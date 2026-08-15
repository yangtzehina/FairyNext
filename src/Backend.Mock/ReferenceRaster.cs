using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Backend.Mock;

// ============================================================================
// 参考光栅（架构「平面六 · 验证平面」机制「mock 参考光栅三规则」，不变量 13）
//
// 目的：把实例流栅格成 RGBA8 位图，作为像素门（M1-26）与会话金样（M2-13）的 CPU 侧真值。
// 前提：**跨 CPU / 跨 JIT / 跨 CI runner 逐字节一致**。浮点光栅做不到这件事——
// 同一段 C# 在 x64 与 arm64 上、在分层编译的两个阶段之间，都可能给出不同的最低位。
// 金样一旦逐位不可复现，它就退化成「本机跑绿」，验证价值归零。
//
// ── 三规则（本文件的全部纪律，逐条可机检）────────────────────────────────────
//  1. **整数色彩运算**：采样、调色、混合全程 int。除以 255 走 Div255 的精确四舍五入
//     （(v+128 + ((v+128)>>8)) >> 8，v ∈ [0,65535] 时等于 round(v/255)），不用浮点比例。
//  2. **整数边函数覆盖判定**：顶点量化到 1/16 像素定点，覆盖 = 三条边函数（long 运算）
//     同号，共享边按 top-left 填充规则归一——同一条对角线只会被两个三角形之一覆盖，
//     既不裂缝也不双写。
//  3. **f32 仅用于线性插值**：允许的浮点只有仿射变换点（乘加）与重心权重插值 UV。
//     **零超越函数**——本文件不出现 sin/cos/sqrt/pow/exp/log，也不出现 System.Math/MathF
//     的任何调用（有一条单测直接扫本文件源码执法这一条）。
//
// ── What this does NOT claim（明文边界）──────────────────────────────────────
//  - 它**不是 shader 的第二实现**。SDF 描边、曲线字形、径向填充（技能 CD）、圆角 mask、
//    clip 软边、旋转裁剪域——一律不求值：它们要么需要超越函数，要么需要与 shader 逐行对齐，
//    两者都会把「确定性」换成「两份可能不一致的实现」。这些归 L4 像素门与 oracle 对拍。
//  - 裁剪只做**屏幕轴对齐 AABB**（clip rect 经其绑定槽变换后取包围盒）。
//  - 纹理采样只有 nearest（texel = floor(uv × 尺寸)），没有双线性与 mip。
//  - 它不做 y 翻转：row 0 是图像顶行，与 <c>PngImage.FromRgba</c> 和 UI 的 y 向下一致。
// ============================================================================

/// <summary>
/// 参考光栅的纹理源（测试自备）。返回值为 RGBA8 打包（bit0-7=r … bit24-31=a），
/// 与 <see cref="Color32.Pack"/> 同序。
/// </summary>
public interface IMockTexture
{
    /// <summary>宽（texel）。</summary>
    int Width { get; }
    /// <summary>高（texel）。</summary>
    int Height { get; }
    /// <summary>取一个 texel（调用方保证下标已在范围内）。</summary>
    uint Texel(int x, int y);
}

/// <summary>
/// 两色棋盘纹理（测试夹具）：<paramref name="cell"/> 像素一格。
/// 参考光栅需要一个「能看出 UV 有没有插反」的纹理，纯色不行。
/// </summary>
public sealed class CheckerTexture : IMockTexture
{
    private readonly uint _a;
    private readonly uint _b;
    private readonly int _cell;

    /// <summary>建棋盘。</summary>
    public CheckerTexture(int width, int height, uint colorA, uint colorB, int cell = 1)
    {
        Width = width < 1 ? 1 : width;
        Height = height < 1 ? 1 : height;
        _a = colorA;
        _b = colorB;
        _cell = cell < 1 ? 1 : cell;
    }

    /// <inheritdoc/>
    public int Width { get; }
    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public uint Texel(int x, int y) => (((x / _cell) + (y / _cell)) & 1) == 0 ? _a : _b;
}

/// <summary>
/// 确定性参考光栅（三规则见本文件头）。一张 RGBA8 目标位图 + 一条 quad 流进，位图出。
/// 无状态可复用：<see cref="Clear"/> 后重画同一份输入必得同一批字节。
/// </summary>
public sealed class ReferenceRaster
{
    /// <summary>定点小数位数（1/16 像素）。够表达半像素接缝，又离 long 溢出很远。</summary>
    public const int SubpixelBits = 4;

    /// <summary>定点单位（1 像素 = 16）。</summary>
    public const int SubpixelOne = 1 << SubpixelBits;

    /// <summary>坐标量化前的截断范围（像素）。超出者按边界截断，不让定点溢出。</summary>
    public const float CoordinateLimit = 1 << 20;

    private readonly byte[] _pixels;

    /// <summary>建目标位图（初值全 0，即透明黑）。</summary>
    public ReferenceRaster(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException($"尺寸非法 {width}x{height}");
        Width = width;
        Height = height;
        _pixels = new byte[width * height * 4];
    }

    /// <summary>宽（像素）。</summary>
    public int Width { get; }

    /// <summary>高（像素）。</summary>
    public int Height { get; }

    /// <summary>RGBA8 缓冲（长度 = W×H×4，row-major，**row 0 = 顶行**）。</summary>
    public byte[] Pixels => _pixels;

    /// <summary>被覆盖过的像素次数（诊断：overdraw 的粗口径）。</summary>
    public long CoveredSamples { get; private set; }

    /// <summary>因坐标非有限（NaN/∞）而整只跳过的 quad 数——非零即上游算错了几何。</summary>
    public int SkippedNonFinite { get; private set; }

    /// <summary>清屏（RGBA8 打包色）。</summary>
    public void Clear(uint rgba)
    {
        byte r = (byte)rgba, g = (byte)(rgba >> 8), b = (byte)(rgba >> 16), a = (byte)(rgba >> 24);
        for (int i = 0; i < _pixels.Length; i += 4)
        {
            _pixels[i] = r;
            _pixels[i + 1] = g;
            _pixels[i + 2] = b;
            _pixels[i + 3] = a;
        }
        CoveredSamples = 0;
        SkippedNonFinite = 0;
    }

    /// <summary>把一整份流快照栅格成新位图（像素门与金样的便捷入口）。</summary>
    /// <param name="width">目标宽。</param>
    /// <param name="height">目标高。</param>
    /// <param name="snapshot">流快照。</param>
    /// <param name="clear">清屏色（RGBA8 打包）。</param>
    /// <param name="texture">纹理源（null = 纯色，采样恒为白）。</param>
    public static byte[] Render(int width, int height, StreamSnapshot snapshot,
        uint clear = 0, IMockTexture? texture = null)
    {
        var raster = new ReferenceRaster(width, height);
        raster.Clear(clear);
        if (snapshot != null)
            raster.DrawQuads(snapshot.Quads, snapshot.Slots, snapshot.Clips, texture);
        return raster.Pixels;
    }

    /// <summary>按流序画一批 quad（流序 = 绘制序，后画的盖先画的）。</summary>
    /// <param name="quads">实例流。</param>
    /// <param name="slots">槽表（空 = 全 identity）。</param>
    /// <param name="clips">裁剪条目表（空 = 不裁剪）。</param>
    /// <param name="texture">纹理源（null = 采样恒为白）。</param>
    public void DrawQuads(ReadOnlySpan<QuadInstance> quads, ReadOnlySpan<SlotEntry> slots,
        ReadOnlySpan<ClipEntry> clips, IMockTexture? texture = null)
    {
        for (int i = 0; i < quads.Length; i++) DrawQuad(quads[i], slots, clips, texture);
    }

    /// <summary>画一个 quad。</summary>
    public void DrawQuad(in QuadInstance quad, ReadOnlySpan<SlotEntry> slots,
        ReadOnlySpan<ClipEntry> clips, IMockTexture? texture = null)
    {
        byte alpha = (byte)(quad.Color >> 24);
        if (alpha == 0) return;                              // Visible 通道清零后的 quad：不画
        if (quad.Rect.z == 0f || quad.Rect.w == 0f) return;  // 零尺寸

        Affine2D m = SlotMatrix(slots, quad.SlotIndex);

        // 四角按 ABI 的角序：uvA = corner(0,0)+(1,0)，uvB = corner(0,1)+(1,1)。
        float x0 = quad.Rect.x, y0 = quad.Rect.y;
        float x1 = x0 + quad.Rect.z, y1 = y0 + quad.Rect.w;
        Vertex c00 = MakeVertex(m, x0, y0, quad.UvA.x, quad.UvA.y);
        Vertex c10 = MakeVertex(m, x1, y0, quad.UvA.z, quad.UvA.w);
        Vertex c01 = MakeVertex(m, x0, y1, quad.UvB.x, quad.UvB.y);
        Vertex c11 = MakeVertex(m, x1, y1, quad.UvB.z, quad.UvB.w);
        if (!c00.Finite || !c10.Finite || !c01.Finite || !c11.Finite)
        {
            SkippedNonFinite++;
            return;
        }

        // 裁剪窗（屏幕轴对齐 AABB；软边与圆角不求值，见文件头边界声明）。
        int cx0 = 0, cy0 = 0, cx1 = Width - 1, cy1 = Height - 1;
        uint clipIndex = quad.ClipIndex;
        if (clipIndex != 0 && clipIndex < (uint)clips.Length)
            ClipWindow(clips[(int)clipIndex], slots, ref cx0, ref cy0, ref cx1, ref cy1);
        if (cx0 > cx1 || cy0 > cy1) return;

        // 两个三角形共用对角线 c00–c11：top-left 规则保证它只被其中一个覆盖。
        Triangle(in c00, in c10, in c11, in quad, texture, cx0, cy0, cx1, cy1);
        Triangle(in c00, in c11, in c01, in quad, texture, cx0, cy0, cx1, cy1);
    }

    // ── 几何 ────────────────────────────────────────────────────────────────

    /// <summary>顶点：定点屏幕坐标（规则 2）+ f32 UV（规则 3 允许的唯一浮点用途之一）。</summary>
    private readonly struct Vertex
    {
        internal readonly int X;
        internal readonly int Y;
        internal readonly float U;
        internal readonly float V;
        internal readonly bool Finite;

        internal Vertex(int x, int y, float u, float v, bool finite)
        {
            X = x; Y = y; U = u; V = v; Finite = finite;
        }
    }

    private static Affine2D SlotMatrix(ReadOnlySpan<SlotEntry> slots, uint slotIndex) =>
        slotIndex < (uint)slots.Length ? slots[(int)slotIndex].M : Affine2D.Identity;

    private static Vertex MakeVertex(in Affine2D m, float x, float y, float u, float v)
    {
        // 仿射变换 = 乘加，规则 3 允许的线性运算。
        float sx = m.m00 * x + m.m01 * y + m.tx;
        float sy = m.m10 * x + m.m11 * y + m.ty;
        bool finite = IsFinite(sx) && IsFinite(sy) && IsFinite(u) && IsFinite(v);
        return new Vertex(ToFixed(sx), ToFixed(sy), u, v, finite);
    }

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    /// <summary>
    /// f32 → 1/16 像素定点。四舍五入用「加 0.5 后向零截断」实现——
    /// 加法与强转都是 IEEE 定义好的确定运算，不引入任何库函数。
    /// </summary>
    private static int ToFixed(float v)
    {
        float clamped = v > CoordinateLimit ? CoordinateLimit : (v < -CoordinateLimit ? -CoordinateLimit : v);
        float scaled = clamped * SubpixelOne;
        return scaled >= 0f ? (int)(scaled + 0.5f) : -(int)(-scaled + 0.5f);
    }

    private static long Edge(in Vertex a, in Vertex b, int px, int py) =>
        (long)(b.X - a.X) * (py - a.Y) - (long)(b.Y - a.Y) * (px - a.X);

    /// <summary>
    /// top-left 填充规则（屏幕顺时针绕序、y 向下）：
    /// 顶边 = 水平且向右，左边 = 向上。落在这两类边上的样点算覆盖，其余边上的不算——
    /// 相邻三角形的公共边因此恰好被覆盖一次。
    /// </summary>
    private static bool IsTopLeft(in Vertex a, in Vertex b) =>
        (a.Y == b.Y && b.X > a.X) || (b.Y < a.Y);

    private static bool Covers(long e, in Vertex a, in Vertex b) => e > 0 || (e == 0 && IsTopLeft(in a, in b));

    private void Triangle(in Vertex v0, in Vertex v1, in Vertex v2, in QuadInstance quad,
        IMockTexture? texture, int cx0, int cy0, int cx1, int cy1)
    {
        // 绕序归一：面积为负说明是逆时针（镜像/负缩放），交换两个顶点即可，
        // 这样边函数的「内侧为正」在两种绕序下都成立。
        long area = Edge(in v0, in v1, v2.X, v2.Y);
        if (area == 0) return;
        Vertex a = v0, b = v1, c = v2;
        if (area < 0)
        {
            b = v2;
            c = v1;
            area = -area;
        }

        int minX = MinOf3(a.X, b.X, c.X) >> SubpixelBits;
        int maxX = MaxOf3(a.X, b.X, c.X) >> SubpixelBits;
        int minY = MinOf3(a.Y, b.Y, c.Y) >> SubpixelBits;
        int maxY = MaxOf3(a.Y, b.Y, c.Y) >> SubpixelBits;
        if (minX < cx0) minX = cx0;
        if (minY < cy0) minY = cy0;
        if (maxX > cx1) maxX = cx1;
        if (maxY > cy1) maxY = cy1;
        if (minX > maxX || minY > maxY) return;

        float invArea = 1f / (float)area;
        int texW = texture == null ? 0 : texture.Width;
        int texH = texture == null ? 0 : texture.Height;
        bool fontAlpha = quad.HasFlag(AbiMock.FlagsFontAlphaShift);
        bool grayed = quad.HasFlag(AbiMock.FlagsGrayedShift);

        for (int py = minY; py <= maxY; py++)
        {
            int sy = (py << SubpixelBits) + (SubpixelOne >> 1);
            for (int px = minX; px <= maxX; px++)
            {
                int sx = (px << SubpixelBits) + (SubpixelOne >> 1);

                long e0 = Edge(in b, in c, sx, sy);      // 对 a 的权重
                if (!Covers(e0, in b, in c)) continue;
                long e1 = Edge(in c, in a, sx, sy);      // 对 b 的权重
                if (!Covers(e1, in c, in a)) continue;
                long e2 = Edge(in a, in b, sx, sy);      // 对 c 的权重
                if (!Covers(e2, in a, in b)) continue;

                int sr = (int)(quad.Color & 0xFFu);
                int sg = (int)((quad.Color >> 8) & 0xFFu);
                int sb = (int)((quad.Color >> 16) & 0xFFu);
                int sa = (int)((quad.Color >> 24) & 0xFFu);

                if (texture != null)
                {
                    // 规则 3：重心权重与 UV 插值是允许的线性浮点；插完立刻回整数域。
                    float l0 = (float)e0 * invArea;
                    float l1 = (float)e1 * invArea;
                    float l2 = (float)e2 * invArea;
                    float u = l0 * a.U + l1 * b.U + l2 * c.U;
                    float v = l0 * a.V + l1 * b.V + l2 * c.V;

                    int tx = ClampInt((int)(u * texW), 0, texW - 1);
                    int ty = ClampInt((int)(v * texH), 0, texH - 1);
                    uint texel = texture.Texel(tx, ty);

                    if (fontAlpha)
                    {
                        // 字体图集只有 α 通道：texel 的 α 当灰度覆盖用，色取实例色。
                        sa = Div255(sa * (int)((texel >> 24) & 0xFFu));
                    }
                    else
                    {
                        sr = Div255(sr * (int)(texel & 0xFFu));
                        sg = Div255(sg * (int)((texel >> 8) & 0xFFu));
                        sb = Div255(sb * (int)((texel >> 16) & 0xFFu));
                        sa = Div255(sa * (int)((texel >> 24) & 0xFFu));
                    }
                }

                if (grayed)
                {
                    // 整数亮度权重（77+151+28 = 256，故右移 8 位即归一）：规则 1，不用浮点系数。
                    int lum = (77 * sr + 151 * sg + 28 * sb) >> 8;
                    sr = lum; sg = lum; sb = lum;
                }

                if (sa == 0) continue;
                Blend(px, py, sr, sg, sb, sa);
                CoveredSamples++;
            }
        }
    }

    private void ClipWindow(in ClipEntry clip, ReadOnlySpan<SlotEntry> slots,
        ref int cx0, ref int cy0, ref int cx1, ref int cy1)
    {
        Affine2D m = SlotMatrix(slots, clip.Slot);
        float xMin = clip.Rect.x, yMin = clip.Rect.y, xMax = clip.Rect.z, yMax = clip.Rect.w;

        // 四角变换后取 AABB：旋转裁剪域在参考光栅里保守放大到包围盒（见文件头边界声明）。
        int px0 = ToFixed(m.m00 * xMin + m.m01 * yMin + m.tx);
        int px1 = ToFixed(m.m00 * xMax + m.m01 * yMin + m.tx);
        int px2 = ToFixed(m.m00 * xMin + m.m01 * yMax + m.tx);
        int px3 = ToFixed(m.m00 * xMax + m.m01 * yMax + m.tx);
        int py0 = ToFixed(m.m10 * xMin + m.m11 * yMin + m.ty);
        int py1 = ToFixed(m.m10 * xMax + m.m11 * yMin + m.ty);
        int py2 = ToFixed(m.m10 * xMin + m.m11 * yMax + m.ty);
        int py3 = ToFixed(m.m10 * xMax + m.m11 * yMax + m.ty);

        // 窗口按**与覆盖判定同一条半开区间语义**取整：样点是像素中心，
        // 中心 ∈ [min, max) 才算在窗内。两处取整口径不一致会让裁剪漏一列像素。
        int lo = FirstPixel(MinOf4(px0, px1, px2, px3));
        int hi = LastPixel(MaxOf4(px0, px1, px2, px3));
        if (lo > cx0) cx0 = lo;
        if (hi < cx1) cx1 = hi;

        lo = FirstPixel(MinOf4(py0, py1, py2, py3));
        hi = LastPixel(MaxOf4(py0, py1, py2, py3));
        if (lo > cy0) cy0 = lo;
        if (hi < cy1) cy1 = hi;
    }

    /// <summary>中心 ≥ 定点边界的最小像素下标（<c>(v + 7) >> 4</c>：算术右移即向下取整）。</summary>
    private static int FirstPixel(int fixedCoord) =>
        (fixedCoord + (SubpixelOne >> 1) - 1) >> SubpixelBits;

    /// <summary>中心 &lt; 定点边界的最大像素下标。</summary>
    private static int LastPixel(int fixedCoord) =>
        ((fixedCoord + (SubpixelOne >> 1) - 1) >> SubpixelBits) - 1;

    // ── 色彩（规则 1：全程整数）────────────────────────────────────────────

    private void Blend(int px, int py, int sr, int sg, int sb, int sa)
    {
        int i = (py * Width + px) * 4;
        if (sa == 255)
        {
            _pixels[i] = (byte)sr;
            _pixels[i + 1] = (byte)sg;
            _pixels[i + 2] = (byte)sb;
            _pixels[i + 3] = 255;
            return;
        }
        int inv = 255 - sa;
        _pixels[i] = (byte)Div255(sr * sa + _pixels[i] * inv);
        _pixels[i + 1] = (byte)Div255(sg * sa + _pixels[i + 1] * inv);
        _pixels[i + 2] = (byte)Div255(sb * sa + _pixels[i + 2] * inv);
        _pixels[i + 3] = (byte)(sa + Div255(_pixels[i + 3] * inv));
    }

    /// <summary>
    /// 精确的 round(v / 255)（v ∈ [0, 65535]）。整数除法与浮点比例都被规则 1 排除，
    /// 这条移位式是唯一入口。
    /// </summary>
    public static int Div255(int v)
    {
        int t = v + 128;
        return (t + (t >> 8)) >> 8;
    }

    private static int MinOf3(int a, int b, int c)
    {
        int m = a < b ? a : b;
        return m < c ? m : c;
    }

    private static int MaxOf3(int a, int b, int c)
    {
        int m = a > b ? a : b;
        return m > c ? m : c;
    }

    private static int MinOf4(int a, int b, int c, int d)
    {
        int m = MinOf3(a, b, c);
        return m < d ? m : d;
    }

    private static int MaxOf4(int a, int b, int c, int d)
    {
        int m = MaxOf3(a, b, c);
        return m > d ? m : d;
    }

    private static int ClampInt(int v, int lo, int hi)
    {
        if (hi < lo) return lo;
        return v < lo ? lo : (v > hi ? hi : v);
    }
}
