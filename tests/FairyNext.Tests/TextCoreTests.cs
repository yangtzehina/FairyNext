using System.Runtime.InteropServices;
using FairyNext.Core;
using FairyNext.Core.Layout;
using FairyNext.Core.Rendering;
using FairyNext.Core.Text;
using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-18 TextCore 无状态排版用例（Program 的 partial 分片）。
/// 覆盖：断行 golden（拉丁词边界 / CJK 任意断 / 混排 / 超长词强拆 / 硬换行与空行）/
/// 对齐 3×3 / ellipsis（尾部 / 不够一个字符 / 行数截断）/ 两把尺（自然尺=折行尺同路径）/
/// kerning 从第一天进求和 / 文本·不变量 1（跨 DPI bit-identical）2（双跑逐字节）
/// 3（arena 复用不残留）/ P5 度量层（autoSize 双轴 + 定宽自动高回流）/
/// 三级惰性端到端（Text→Content、产物没变不脏）/ slack 档内重写与跨档整编 /
/// Pending α=0 占位全链路 / 页引用纪律 / 核按钮端到端。
///
/// 合成字体 SynthFontT 的度量刻意全取 2 的幂分数（advance 0.25/0.375/0.5/0.875/1.0 em、
/// 行高恰 1.0 em）——golden 期望值在 float 上**精确**，比对全部用位等不用容差。
/// </summary>
public static partial class Program
{
    // ── 排版测试字体：upem 1000、行高恰 1em、advance 全为 2 的幂分数 ─────────
    //   gid0 .notdef 空（adv 0.5）；gid1-8 = 'A'-'H' 方块 (0,0,500,750) adv 0.5；
    //   gid9 = U+4E00/U+4E01 宽块 (0,0,1000,750) adv 1.0；gid10 = '…' (0,0,875,125) adv 0.875；
    //   gid11 = '-' (0,250,375,500) adv 0.375；gid12 = ' ' 空字形 adv 0.25。

    private static byte[] SynthFontT()
    {
        var latin = Ttf.SimpleGlyph(0, 0, 500, 750, new ushort[] { 3 }, new (short, short, bool)[]
        {
            (0, 0, true), (500, 0, true), (500, 750, true), (0, 750, true),
        });
        var cjk = Ttf.SimpleGlyph(0, 0, 1000, 750, new ushort[] { 3 }, new (short, short, bool)[]
        {
            (0, 0, true), (1000, 0, true), (1000, 750, true), (0, 750, true),
        });
        var ell = Ttf.SimpleGlyph(0, 0, 875, 125, new ushort[] { 3 }, new (short, short, bool)[]
        {
            (0, 0, true), (875, 0, true), (875, 125, true), (0, 125, true),
        });
        var hyp = Ttf.SimpleGlyph(0, 250, 375, 500, new ushort[] { 3 }, new (short, short, bool)[]
        {
            (0, 250, true), (375, 250, true), (375, 500, true), (0, 500, true),
        });
        var bodies = new byte[13][];
        bodies[0] = Array.Empty<byte>();
        for (int i = 1; i <= 8; i++) bodies[i] = latin;
        bodies[9] = cjk;
        bodies[10] = ell;
        bodies[11] = hyp;
        bodies[12] = Array.Empty<byte>();
        (byte[] glyf, byte[] loca) = Ttf.GlyfLoca(bodies);

        var hmtx = new (ushort adv, short lsb)[13];
        hmtx[0] = (500, 0);
        for (int i = 1; i <= 8; i++) hmtx[i] = (500, 0);
        hmtx[9] = (1000, 0);
        hmtx[10] = (875, 0);
        hmtx[11] = (375, 0);
        hmtx[12] = (250, 0);

        return Ttf.Build(0x00010000,
            ("cmap", Ttf.Cmap(((ushort)3, (ushort)1, Ttf.CmapFormat4(new[]
            {
                ((ushort)0x20, (ushort)0x20, (short)(12 - 0x20)),
                ((ushort)0x2D, (ushort)0x2D, (short)(11 - 0x2D)),
                ((ushort)0x41, (ushort)0x48, (short)(1 - 0x41)),
                ((ushort)0x2026, (ushort)0x2026, (short)(10 - 0x2026)),
                ((ushort)0x4E00, (ushort)0x4E00, (short)(9 - 0x4E00)),
                ((ushort)0x4E01, (ushort)0x4E01, (short)(9 - 0x4E01)),
            })))),
            ("glyf", glyf),
            ("head", Ttf.Head(1000, 0)),
            ("hhea", Ttf.Hhea(750, -250, 0, 13)),
            ("hmtx", Ttf.Hmtx(hmtx, 0)),
            ("loca", loca),
            ("maxp", Ttf.Maxp(13)));
    }

    private static (GlyphMetricsTable met, ushort face) TextFontRig()
    {
        var met = new GlyphMetricsTable();
        ushort face = met.RegisterFace(TtfFontFace.Load(SynthFontT(), "SynthT"));
        return (met, face);
    }

    private static TextStyle TStyle(ushort face, float size = 10f, bool wrap = true,
        TextHAlign h = TextHAlign.Left, TextVAlign v = TextVAlign.Top,
        TextOverflow of = TextOverflow.Visible, uint color = 0xFF4080C0u) => new TextStyle
        {
            FaceId = face, SizePx = size, Color = color, HAlign = h, VAlign = v, Wrap = wrap, Overflow = of,
        };

    private static TextLayoutResult TLay(GlyphMetricsTable met, in TextStyle st, string s,
        float maxW, float maxH, LayoutArena? arena = null)
    {
        TextCore.Layout(s, in st, maxW, maxH, met, arena ?? new LayoutArena(), out TextLayoutResult r);
        return r;
    }

    private static bool SpanBytesEqual<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged =>
        MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));

    public static void TextCoreSuite()
    {
        TextBreakLatinGolden();
        TextBreakCjkAndMixed();
        TextForceBreakLongWord();
        TextHardNewlinesAndEmptyLines();
        TextAlignMatrix();
        TextEllipsisTail();
        TextEllipsisTinyAndMaxLines();
        TextTwoRulers();
        TextKerningFromDayOne();
        TextStatelessDoubleRun();
        TextArenaReuseNoResidue();
        TextPow2ScaleExact();
        TextMeasureAutoSizeE2E();
        TextAutoHeightReflow();
        TextConstraintDrivenWidthRemeasure();
        TextPipelineLazinessLevels();
        TextSlackTierEscalation();
        TextTransformMoveReemits();
        TextPendingAlphaZero();
        TextPageRefDiscipline();
        TextNuclearRebuildE2E();
        TextCrossDpiBitIdentical();
    }

    // ── 断行 golden ─────────────────────────────────────────────────────────

    private static void TextBreakLatinGolden()
    {
        (GlyphMetricsTable met, ushort face) = TextFontRig();
        TextLayoutResult r = TLay(met, TStyle(face), "AB CDE", 20f, float.PositiveInfinity);

        // 期望（全部精确 float）：limit 2em；D 放不下 → 回卷到空格后 → "AB "(1.25em) / "CDE"(1.5em)。
        bool structure = r.Lines.Length == 2 && r.Glyphs.Length == 6
            && r.Lines[0].GlyphCount == 3 && r.Lines[1].GlyphCount == 3
            && r.Glyphs[2].GlyphId == 12                    // 行尾空格在流里（占 advance 不产 quad）
            && !r.Truncated;
        bool widths = r.Lines[0].Width == 12.5f && r.Lines[1].Width == 15f
            && r.ContentW == 15f && r.ContentH == 20f;      // 行尾空格计入行宽
        bool positions = r.Glyphs[0].X == 0f && r.Glyphs[1].X == 5f && r.Glyphs[2].X == 10f
            && r.Glyphs[3].X == 0f && r.Glyphs[4].X == 5f && r.Glyphs[5].X == 10f
            && r.Glyphs[0].Y == 7.5f && r.Glyphs[3].Y == 17.5f
            && r.Lines[0].Y == 0f && r.Lines[1].Y == 10f
            && r.Lines[0].Baseline == 7.5f && r.Lines[1].Baseline == 17.5f;
        Check("排版: 拉丁词边界断行 golden（回卷到空格 + 行尾空格计宽）", structure && widths);
        Check("排版: 拉丁 golden 逐字形笔位与基线（em 求值 × 尾步缩放，全精确）", positions);

        // 行尾空格超限仍不折（「空格类永不触发折行」，CSS 同款）：limit 1.1em，空格把首行顶到
        // 1.25em 也不在空格处换行——断点仍是词边界 C，首行 "AB " **合法超宽**。
        TextLayoutResult sp = TLay(met, TStyle(face), "AB CD", 11f, float.PositiveInfinity);
        Check("排版: 行尾空格超限不触发折行（空格免检——首行合法超宽，断点仍在词边界）",
            sp.Lines.Length == 2 && sp.Lines[0].GlyphCount == 3 && sp.Lines[0].Width == 12.5f
            && sp.Lines[1].GlyphCount == 2 && sp.Lines[1].Width == 10f);
    }

    private static void TextBreakCjkAndMixed()
    {
        (GlyphMetricsTable met, ushort face) = TextFontRig();

        TextLayoutResult cjk = TLay(met, TStyle(face), "一丁一", 25f, float.PositiveInfinity);
        Check("排版: CJK 任意断（第三字回卷成第二行，无词边界也断）",
            cjk.Lines.Length == 2 && cjk.Lines[0].GlyphCount == 2 && cjk.Lines[1].GlyphCount == 1
            && cjk.Lines[0].Width == 20f && cjk.Lines[1].Width == 10f);

        TextLayoutResult mix = TLay(met, TStyle(face), "AB一C", 22f, float.PositiveInfinity);
        Check("排版: 混排（拉丁贴 CJK 处即是断点——前字是 CJK 就可断）",
            mix.Lines.Length == 2 && mix.Lines[0].GlyphCount == 3 && mix.Lines[1].GlyphCount == 1
            && mix.Lines[0].Width == 20f && mix.Lines[1].Width == 5f);

        // 回卷到 CJK 边界：拉丁词在 CJK 后溢出，断点必须回卷到「一|ABC」——这个形状把
        // 「CJK 断点参与 lastBreak 回卷」与「无断点字形粒度硬拆」区分开（硬拆会给出「一AB|C」）。
        TextLayoutResult roll = TLay(met, TStyle(face), "一ABC", 20f, float.PositiveInfinity);
        Check("排版: 混排回卷（CJK 断点吃回卷——「一|ABC」而非硬拆「一AB|C」）",
            roll.Lines.Length == 2 && roll.Lines[0].GlyphCount == 1 && roll.Lines[1].GlyphCount == 3
            && roll.Lines[0].Width == 10f && roll.Lines[1].Width == 15f);
    }

    private static void TextForceBreakLongWord()
    {
        (GlyphMetricsTable met, ushort face) = TextFontRig();
        TextLayoutResult r = TLay(met, TStyle(face), "ABCDEF", 12f, float.PositiveInfinity);
        Check("排版: 超长词强拆（无断点则字形粒度硬断，单超宽字形独占一行不空转）",
            r.Lines.Length == 3
            && r.Lines[0].GlyphCount == 2 && r.Lines[1].GlyphCount == 2 && r.Lines[2].GlyphCount == 2
            && r.Lines[0].Width == 10f && r.ContentH == 30f);
    }

    private static void TextHardNewlinesAndEmptyLines()
    {
        (GlyphMetricsTable met, ushort face) = TextFontRig();
        TextLayoutResult r = TLay(met, TStyle(face), "A\n\nB", 100f, float.PositiveInfinity);
        Check("排版: 硬换行与空行（\\n\\n = 中间一个 GlyphCount 0 的真行，行高照占）",
            r.Lines.Length == 3 && r.Lines[0].GlyphCount == 1 && r.Lines[1].GlyphCount == 0
            && r.Lines[2].GlyphCount == 1 && r.Lines[1].Y == 10f && r.Lines[2].Y == 20f
            && r.ContentH == 30f && r.Glyphs.Length == 2);

        TextLayoutResult crlf = TLay(met, TStyle(face), "A\r\nB", 100f, float.PositiveInfinity);
        Check("排版: \\r\\n 归一（\\r 不占条目不占行）",
            crlf.Lines.Length == 2 && crlf.Glyphs.Length == 2 && crlf.Lines[1].GlyphCount == 1);
    }

    private static void TextAlignMatrix()
    {
        (GlyphMetricsTable met, ushort face) = TextFontRig();
        // "AB" = 1em = 10px；箱 40×30 ⇒ 水平偏移 {0,15,30}、垂直偏移 {0,10,20}（全部精确）。
        float[] hoff = { 0f, 15f, 30f };
        float[] voff = { 0f, 10f, 20f };
        bool ok = true;
        for (int hi = 0; hi < 3; hi++)
        {
            for (int vi = 0; vi < 3; vi++)
            {
                TextStyle st = TStyle(face, h: (TextHAlign)hi, v: (TextVAlign)vi);
                TextLayoutResult r = TLay(met, st, "AB", 40f, 30f);
                ok &= r.Lines.Length == 1
                    && r.Glyphs[0].X == hoff[hi] && r.Glyphs[1].X == hoff[hi] + 5f
                    && r.Lines[0].Y == voff[vi] && r.Lines[0].Baseline == voff[vi] + 7.5f
                    && r.Glyphs[0].Y == voff[vi] + 7.5f;
                if (!ok)
                {
                    Console.WriteLine($"     [align] h={hi} v={vi} X0={r.Glyphs[0].X} Y={r.Lines[0].Y}");
                    break;
                }
            }
            if (!ok) break;
        }
        Check("排版: 对齐 3×3 全矩阵（水平三向 × 垂直三向，偏移逐位精确）", ok);
    }

    private static void TextEllipsisTail()
    {
        (GlyphMetricsTable met, ushort face) = TextFontRig();
        TextLayoutResult r = TLay(met, TStyle(face, wrap: false, of: TextOverflow.Ellipsis),
            "ABCDE", 20f, float.PositiveInfinity);
        // 2.5em > 2em ⇒ 回退到 "AB…"（1.0 + 0.875 = 1.875em = 18.75px）。
        Check("排版: 尾部 ellipsis（回退字形直到保留段+省略号放得下）",
            r.Truncated && r.Lines.Length == 1 && r.Lines[0].GlyphCount == 3
            && r.Glyphs[2].GlyphId == 10 && r.Glyphs[2].X == 10f
            && r.Lines[0].Width == 18.75f && r.ContentW == 18.75f);
    }

    private static void TextEllipsisTinyAndMaxLines()
    {
        (GlyphMetricsTable met, ushort face) = TextFontRig();

        TextLayoutResult tiny = TLay(met, TStyle(face, wrap: false, of: TextOverflow.Ellipsis),
            "ABCDE", 8f, float.PositiveInfinity);
        Check("排版: ellipsis 不够一个字符（仍给省略号，可超宽 + Truncated 有声）",
            tiny.Truncated && tiny.Glyphs.Length == 1 && tiny.Glyphs[0].GlyphId == 10
            && tiny.Lines[0].Width == 8.75f);

        // 6 个宽字 ⇒ 3 行；maxH 25 只放 2 行 ⇒ 末可见行 = 一 + …（1.0+0.875=1.875em）。
        TextLayoutResult lines = TLay(met, TStyle(face, of: TextOverflow.Ellipsis),
            "一一一一一一", 20f, 25f);
        Check("排版: ellipsis 行数截断（maxHeight 斩行 + 末行收尾省略号）",
            lines.Truncated && lines.Lines.Length == 2
            && lines.Lines[1].GlyphCount == 2 && lines.Glyphs[3].GlyphId == 10
            && lines.Lines[1].Width == 18.75f && lines.ContentH == 20f);
    }

    private static void TextTwoRulers()
    {
        (GlyphMetricsTable met, ushort face) = TextFontRig();
        TextStyle st = TStyle(face);

        TextLayoutResult wrapped = TLay(met, st, "AB CDE", 20f, float.PositiveInfinity);
        TextLayoutResult natural = TLay(met, st, "AB CDE", float.PositiveInfinity, float.PositiveInfinity);
        Check("排版: 两把尺——自然尺 ≥ 折行尺，且 MaxIntrinsicWidth 与不限宽 ContentW 位等（同一实现路径）",
            BitEq(wrapped.MaxIntrinsicWidth, natural.ContentW)
            && BitEq(natural.MaxIntrinsicWidth, natural.ContentW)
            && natural.ContentW >= wrapped.ContentW
            && natural.Lines.Length == 1);

        TextLayoutResult wide = TLay(met, st, "AB CDE", 100f, float.PositiveInfinity);
        Check("排版: 两把尺——限宽 ≥ 自然宽时折行尺退化为自然尺（位等，单行）",
            wide.Lines.Length == 1 && BitEq(wide.ContentW, natural.ContentW)
            && BitEq(wide.ContentW, wide.MaxIntrinsicWidth));
    }

    /// <summary>kerning 注入源：拉丁 gid 对之间 −0.125em，其余 0（包一层真度量账）。</summary>
    private sealed class KernedMetrics : IGlyphMetricsSource
    {
        private readonly IGlyphMetricsSource _inner;
        internal KernedMetrics(IGlyphMetricsSource inner) { _inner = inner; }
        public FaceMetrics MetricsOfFace(ushort faceId) => _inner.MetricsOfFace(faceId);
        public ushort MapCodepoint(ushort faceId, int cp) => _inner.MapCodepoint(faceId, cp);
        public bool TryGetGlyphMetrics(ushort faceId, ushort gid, out GlyphMetrics m) =>
            _inner.TryGetGlyphMetrics(faceId, gid, out m);
        public float KerningEm(ushort faceId, ushort left, ushort right) =>
            left >= 1 && left <= 8 && right >= 1 && right <= 8 ? -0.125f : 0f;
    }

    private static void TextKerningFromDayOne()
    {
        (GlyphMetricsTable met, ushort face) = TextFontRig();
        var kerned = new KernedMetrics(met);
        TextStyle st = TStyle(face, wrap: false);

        // 注意每次 Layout 各配一个 arena：同 arena 的下一次调用整块作废前一个结果
        // （TextLayoutResult 的内存契约——本用例第一版正是踩了这一条）。
        TextCore.Layout("AB", in st, float.PositiveInfinity, float.PositiveInfinity,
            kerned, new LayoutArena(), out TextLayoutResult r);
        bool pen = r.Glyphs[1].X == 3.75f                   // (0.5 − 0.125) × 10
            && r.ContentW == 8.75f && r.MaxIntrinsicWidth == 8.75f;   // 两把尺都吃 kerning

        // kerning 改变断点：limit 0.9em。有 kerning "AB"(0.875) 同行；无 kerning B 就放不下。
        TextStyle wrapSt = TStyle(face);
        TextCore.Layout("ABC", in wrapSt, 9f, float.PositiveInfinity,
            kerned, new LayoutArena(), out TextLayoutResult kr);
        TextCore.Layout("ABC", in wrapSt, 9f, float.PositiveInfinity,
            met, new LayoutArena(), out TextLayoutResult nr);
        Check("排版: kerning 第一天就进求和（笔位/两把尺/断点三处生效；M2-09 落数据零改动）",
            pen && kr.Lines[0].GlyphCount == 2 && nr.Lines[0].GlyphCount == 1);
    }

    // ── 文本·不变量 2 / 3 / 1 ───────────────────────────────────────────────

    private static void TextStatelessDoubleRun()
    {
        (GlyphMetricsTable met, ushort face) = TextFontRig();
        TextStyle st = TStyle(face, h: TextHAlign.Center, v: TextVAlign.Middle, of: TextOverflow.Ellipsis);
        const string s = "AB CDE一丁 FG-H\nAB";

        TextLayoutResult a = TLay(met, st, s, 33f, 27f);
        TextLayoutResult b = TLay(met, st, s, 33f, 27f);
        Check("文本·不变量 2: 无状态双跑逐字节相等（字形流 + 行表 + 四标量）",
            SpanBytesEqual<PlacedGlyph>(a.Glyphs, b.Glyphs) && SpanBytesEqual<TextLine>(a.Lines, b.Lines)
            && BitEq(a.ContentW, b.ContentW) && BitEq(a.ContentH, b.ContentH)
            && BitEq(a.MaxIntrinsicWidth, b.MaxIntrinsicWidth) && a.Truncated == b.Truncated);
    }

    private static void TextArenaReuseNoResidue()
    {
        (GlyphMetricsTable met, ushort face) = TextFontRig();
        TextStyle st = TStyle(face);
        var used = new LayoutArena();
        TextCore.Layout("ABCDEFGH ABCDEFGH 一丁一丁 ABCDEFGH", in st, 35f, 200f,
            met, used, out _);                              // 先塞一版长文本弄脏 arena

        TextCore.Layout("AB C", in st, 20f, 50f, met, used, out TextLayoutResult reused);
        TextCore.Layout("AB C", in st, 20f, 50f, met, new LayoutArena(), out TextLayoutResult fresh);
        Check("文本·不变量 3: LayoutArena 复用不残留（脏 arena 重排 ≡ 新 arena，逐字节）",
            SpanBytesEqual<PlacedGlyph>(reused.Glyphs, fresh.Glyphs)
            && SpanBytesEqual<TextLine>(reused.Lines, fresh.Lines)
            && BitEq(reused.ContentW, fresh.ContentW) && BitEq(reused.ContentH, fresh.ContentH));
    }

    private static void TextPow2ScaleExact()
    {
        (GlyphMetricsTable met, ushort face) = TextFontRig();
        TextStyle s10 = TStyle(face, size: 10f);
        TextStyle s20 = TStyle(face, size: 20f);
        TextLayoutResult a = TLay(met, s10, "AB CDE一", 20f, float.PositiveInfinity);
        TextLayoutResult b = TLay(met, s20, "AB CDE一", 40f, float.PositiveInfinity);

        bool sameBreaks = a.Lines.Length == b.Lines.Length && a.Glyphs.Length == b.Glyphs.Length;
        for (int i = 0; sameBreaks && i < a.Glyphs.Length; i++)
            sameBreaks = a.Glyphs[i].GlyphId == b.Glyphs[i].GlyphId && a.Glyphs[i].Line == b.Glyphs[i].Line
                && BitEq(a.Glyphs[i].X * 2f, b.Glyphs[i].X) && BitEq(a.Glyphs[i].Y * 2f, b.Glyphs[i].Y);
        Check("排版: 2 的幂缩放精确（字号×2 限宽×2 ⇒ 断行点逐一同、笔位恰为 ×2——em 空间求值的直接推论）",
            sameBreaks && BitEq(a.ContentW * 2f, b.ContentW) && BitEq(a.ContentH * 2f, b.ContentH));
    }

    // ── 管线接线（PipeFixture + TextSystem）─────────────────────────────────

    private static (PipeFixture f, TextSystem text, GlyphMetricsTable met, GlyphStore store, ushort face)
        TextRig(float rasterScale = 1f, bool syncResidency = true)
    {
        var f = new PipeFixture();
        var met = new GlyphMetricsTable();
        ushort face = met.RegisterFace(TtfFontFace.Load(SynthFontT(), "SynthT"));
        var store = new GlyphStore();
        store.AttachInvalidation(f.Inval);
        var text = new TextSystem(f.Kernel, met, store, f.Content)
        {
            RasterScale = rasterScale,
            SyncResidency = syncResidency,
        };
        text.Attach(f.Layout);
        return (f, text, met, store, face);
    }

    private static NodeHandle TextNode(PipeFixture f, TextSystem text, string s, in TextStyle st,
        float x, float y, float w, float h, bool autoW = false, bool autoH = false)
    {
        NodeHandle n = f.Table.CreateNode(NodeType.Text);
        f.Table.SetPosition(n, x, y);
        f.Table.SetSize(n, w, h);
        f.Table.AddChild(f.Table.Root, n);
        text.AttachText(n, s, in st, autoW, autoH);
        return n;
    }

    private static int TextLeafIndex(PipeFixture f, NodeHandle n)
    {
        ReadOnlySpan<LeafRange> ls = f.Stream.Leaves;
        for (int i = 0; i < ls.Length; i++)
            if (ls[i].Node.Equals(n)) return i;
        return -1;
    }

    private static void TextMeasureAutoSizeE2E()
    {
        (PipeFixture f, TextSystem text, _, _, ushort face) = TextRig();
        f.Layout.DifferentialGate = true;
        NodeHandle n = TextNode(f, text, "AB 一", TStyle(face), 3f, 4f, 0f, 0f, autoW: true, autoH: true);
        f.Layout.RegisterMeasured(n, autoWidth: true, autoHeight: true);

        bool g1 = f.TickAndCheck().Pass;
        ResolvedGeom g = f.Table.GetResolved(n);
        long measured = f.Layout.Stats.MeasureRuns;
        // 自然尺：0.5+0.5+0.25+1.0 = 2.25em ⇒ (22.5, 10)（精确）。
        bool sized = g.W == 22.5f && g.H == 10f && g.X == 3f && g.Y == 4f;

        bool g2 = f.TickAndCheck().Pass;                    // 静止帧：度量层不重跑（增量收据不涨）
        Check("P5 度量层: autoSize 双轴——resolved 尺寸 = 自然尺，幂等/差分/增量三门绿",
            g1 && g2 && sized && f.Sound() && f.Layout.Stats.DifferentialFailures == 0
            && measured > 0 && f.Layout.Stats.MeasureRuns == measured);

        text.SetText(n, "AB");                              // 改文本 → Text 排水 Mark Layout → 重量
        bool g3 = f.TickAndCheck().Pass;
        ResolvedGeom g2r = f.Table.GetResolved(n);
        Check("P5 度量层: 文本变更走 Text→Layout 接力重量（10×10），门仍绿",
            g3 && g2r.W == 10f && g2r.H == 10f && f.Sound()
            && f.Layout.Stats.MeasureRuns > measured);
    }

    private static void TextAutoHeightReflow()
    {
        (PipeFixture f, TextSystem text, _, _, ushort face) = TextRig();
        f.Layout.DifferentialGate = true;
        NodeHandle n = TextNode(f, text, "AB CDE", TStyle(face), 0f, 0f, 20f, 5f, autoH: true);
        f.Layout.RegisterMeasured(n, autoWidth: false, autoHeight: true);

        bool g1 = f.TickAndCheck().Pass;
        ResolvedGeom a = f.Table.GetResolved(n);            // 20 宽折 2 行 ⇒ 高 20

        f.Table.SetSize(n, 10f, 5f);                        // 用户改宽（authored）；高由度量层回写
        bool g2 = f.TickAndCheck().Pass;
        ResolvedGeom b = f.Table.GetResolved(n);            // 10 宽折 3 行（"AB " / "CD" / "E"）⇒ 高 30
        Check("P5 度量层: 定宽自动高回流（改宽 → 同帧重量 → 高随行数变），三门绿",
            g1 && g2 && a.W == 20f && a.H == 20f && b.W == 10f && b.H == 30f
            && f.Sound() && f.Layout.Stats.DifferentialFailures == 0);
    }

    private static void TextConstraintDrivenWidthRemeasure()
    {
        // 宽度被**约束层**改动（非 authored）⇒ 度量节点自动重新入量（Touch 路径的 re-pending，
        // 与 AuthoredChanged 那半各守一门）：文本右边跟随兄弟左边，兄弟移动 → 约束写宽 →
        // 同帧 MeasurePass 重量 → autoHeight 随行数变。「文本尺寸→约束→回改宽度」的回路
        // 由既有 ≤3 轮受控窗兜底，收敛后三门照绿。
        (PipeFixture f, TextSystem text, _, _, ushort face) = TextRig();
        f.Layout.DifferentialGate = true;
        NodeHandle sib = f.Box(20f, 0f, 5f, 5f);
        NodeHandle n = TextNode(f, text, "AB CDE", TStyle(face), 0f, 0f, 20f, 5f, autoH: true);
        f.Layout.RegisterMeasured(n, autoWidth: false, autoHeight: true);
        var b = new ConstraintGraphBuilder(3);
        b.Pin(2, EdgeSel.Min, LayoutAxis.X);                         // 左边钉住 + 右边跟随 = 拉伸
        b.Follow(2, EdgeSel.Max, 1, EdgeSel.Min, LayoutAxis.X);      // 文本右边 ← 兄弟左边
        ConstraintGraphResult cg = b.Seal();
        f.Layout.Arm(cg.Graph!, new[] { f.Table.Root, sib, n });

        bool g1 = f.TickAndCheck().Pass;
        ResolvedGeom a = f.Table.GetResolved(n);                     // 宽 20 → 2 行 → 高 20

        f.Table.SetPosition(sib, 10f, 0f);                           // 只动兄弟：文本宽由约束层写
        bool g2 = f.TickAndCheck().Pass;
        ResolvedGeom c = f.Table.GetResolved(n);                     // 宽 10 → 3 行 → 高 30
        Check("P5 度量层: 约束层写宽（非 authored）⇒ 自动重入量，autoHeight 随行数变、门仍绿",
            g1 && g2 && a.W == 20f && a.H == 20f && c.W == 10f && c.H == 30f
            && f.Sound() && f.Layout.Stats.DifferentialFailures == 0);
    }

    private static void TextPipelineLazinessLevels()
    {
        (PipeFixture f, TextSystem text, _, _, ushort face) = TextRig();
        NodeHandle n = TextNode(f, text, "AB CDE", TStyle(face), 0f, 0f, 20f, 30f);

        bool g1 = f.TickAndCheck().Pass;
        int leaf = TextLeafIndex(f, n);
        LeafRange r0 = f.Stream.Leaves[leaf];
        // 5 个有轮廓字形 + 容量 6（5+1 省略号位）⇒ slack 2 的幂档 = 8。
        bool shape = leaf >= 0 && r0.Count == 5 && r0.Slack == 8 && f.Stream.QuadCount == 8;

        // 三级惰性入口：改字只脏 Text——Content/Layout 队列此刻必须是空的。
        text.SetText(n, "AB CDEF");
        bool onlyText = f.Inval.QueueLength(Ch.Text) == 1
            && f.Inval.QueueLength(Ch.Content) == 0 && f.Inval.QueueLength(Ch.Layout) == 0;

        int rebuilds = f.Pipe.Extract.Rebuilds;
        int rewrites = f.Pipe.Drain.ContentRewrites;
        bool g2 = f.TickAndCheck().Pass;
        LeafRange r1 = f.Stream.Leaves[TextLeafIndex(f, n)];
        bool inPlace = f.Pipe.Extract.Rebuilds == rebuilds && f.Pipe.Drain.ContentRewrites == rewrites + 1
            && r1.Count == 6 && r1.Slack == 8;              // 档内改字 = 原位重写，不整编
        Check("三级惰性: 改字只走 Text→Content（档内原位重写，增量门绿）", g1 && g2 && onlyText && inPlace && f.Sound());

        // 产物没变的样式改动：Text 排了、stamp 同 ⇒ 不 Mark Content ⇒ 整帧零上传。
        long skipped = text.Diag.ContentMarksSkipped;
        TextStyle st2 = TStyle(face, of: TextOverflow.Ellipsis);   // 文本装得下：截断策略不改产物
        text.SetStyle(n, in st2);
        bool g3 = f.TickAndCheck().Pass;
        Check("三级惰性: 排版产物没变则不脏 Content（stamp 拦截 + 零上传帧收据）",
            g3 && text.Diag.ContentMarksSkipped == skipped + 1 && f.Pipe.LastReport.IsIdle && f.Sound());
    }

    private static void TextSlackTierEscalation()
    {
        (PipeFixture f, TextSystem text, _, _, ushort face) = TextRig();
        NodeHandle n = TextNode(f, text, "AB CDE", TStyle(face), 0f, 0f, 100f, 30f);
        bool g1 = f.TickAndCheck().Pass;

        int rebuilds = f.Pipe.Extract.Rebuilds;
        text.SetText(n, "ABCDEFGH ABCDEFGH");              // 16 个轮廓字形：容量 17 ⇒ 32 档，跨档
        bool g2 = f.TickAndCheck().Pass;
        LeafRange r = f.Stream.Leaves[TextLeafIndex(f, n)];
        Check("slack 跨档: 预留区形状变 ⇒ 升级 Structure 整编（SlackShape 归因，档内才原位）",
            g1 && g2 && f.Pipe.Drain.CauseCount(EscalateCause.SlackShape) >= 1
            && f.Pipe.Extract.Rebuilds == rebuilds + 1
            && r.Count == 16 && r.Slack == 32 && f.Sound());
    }

    private static void TextTransformMoveReemits()
    {
        // 文本叶从第一天就**可复跑**（ITextQuadEmitter 的帧内纯函数契约）：Transform 移动走与
        // Image 同一条「按新 world 重新发射并烘 rect」路径（M1-14 blockquote 给「M1-18 之前的
        // 文本叶」留的 tier-2 例外条款自此退役）——平移在 float 加法上逐位精确，增量门照绿。
        (PipeFixture f, TextSystem text, _, _, ushort face) = TextRig();
        NodeHandle n = TextNode(f, text, "AB", TStyle(face), 0f, 0f, 40f, 20f);
        bool g1 = f.TickAndCheck().Pass;
        int leaf = TextLeafIndex(f, n);
        LeafRange r = f.Stream.Leaves[leaf];
        Vector4 a0 = f.Stream.Quads[r.Start].Rect;
        Vector4 a1 = f.Stream.Quads[r.Start + 1].Rect;
        int rebuilds = f.Pipe.Extract.Rebuilds;
        int rewrites = f.Pipe.Drain.ContentRewrites;

        f.Table.SetPosition(n, 7f, 11f);
        bool g2 = f.TickAndCheck().Pass;
        ReadOnlySpan<QuadInstance> q = f.Stream.Quads.Slice(r.Start, r.Count);
        Check("Transform 移动文本叶: 原位重发射 + 重烘 rect（不整编、槽 0、增量门绿）——文本可复跑，tier-2 无需出场",
            g1 && g2 && f.Pipe.Extract.Rebuilds == rebuilds
            && f.Pipe.Drain.ContentRewrites == rewrites + 1
            && q[0].Rect == new Vector4(a0.x + 7f, a0.y + 11f, a0.z, a0.w)
            && q[1].Rect == new Vector4(a1.x + 7f, a1.y + 11f, a1.z, a1.w)
            && q[0].SlotIndex == 0u
            && f.Sound());
    }

    private static void TextPendingAlphaZero()
    {
        (PipeFixture f, TextSystem text, _, GlyphStore store, ushort face) = TextRig(syncResidency: false);
        NodeHandle n = TextNode(f, text, "AB", TStyle(face, color: 0xFF204060u), 0f, 0f, 40f, 20f);

        Vector2 before = text.Measure(n, float.PositiveInfinity);
        bool g1 = f.TickAndCheck().Pass;
        int leaf = TextLeafIndex(f, n);
        LeafRange r = f.Stream.Leaves[leaf];
        ReadOnlySpan<QuadInstance> q1 = f.Stream.Quads.Slice(r.Start, r.Count);
        var rects = new Vector4[q1.Length];
        for (int i = 0; i < q1.Length; i++) rects[i] = q1[i].Rect;
        bool placeholder = r.Count == 2 && store.PlacementCount == 0 && text.PendingCount == 2
            && (q1[0].Color >> 24) == 0 && (q1[1].Color >> 24) == 0   // α=0 占位
            && q1[0].Rect == new Vector4(0f, 0f, 5f, 7.5f)            // 几何来自度量半区，照占
            && q1[1].Rect == new Vector4(5f, 0f, 5f, 7.5f)
            && q1[0].UvA == Vector4.zero;

        int delivered = text.DeliverPending();              // 「页到货」：入店 + 订阅叶补 Mark Content
        bool g2 = f.TickAndCheck().Pass;
        ReadOnlySpan<QuadInstance> q2 = f.Stream.Quads.Slice(r.Start, r.Count);
        bool arrived = delivered == 2 && store.PlacementCount == 2 && text.PendingCount == 0
            && (q2[0].Color >> 24) == 0xFF && (q2[1].Color >> 24) == 0xFF
            && q2[0].UvA != Vector4.zero
            && q2[0].Rect == rects[0] && q2[1].Rect == rects[1];      // 不闪不跳版：几何逐位没动
        Vector2 after = text.Measure(n, float.PositiveInfinity);
        Check("Pending α=0 全链路: 未就绪占位（占 slack 区间）→ 到货补脏 → 原位换真 UV/色，几何逐位不动",
            g1 && g2 && placeholder && arrived && f.Sound());
        Check("Pending 不碰度量: 到货前后 Measure 位等（度量半区与驻留解耦）",
            BitEq(before.x, after.x) && BitEq(before.y, after.y));
    }

    private static void TextPageRefDiscipline()
    {
        (PipeFixture f, TextSystem text, _, GlyphStore store, ushort face) = TextRig();
        NodeHandle n = TextNode(f, text, "AB", TStyle(face), 0f, 0f, 40f, 20f);
        bool g1 = f.TickAndCheck().Pass;
        bool referenced = store.PageCount == 1 && store.PageRefCount(0) == 1;

        text.SetText(n, "一");                          // 换字：同页调和 ⇒ 引用计数不涨不漏
        bool g2 = f.TickAndCheck().Pass;
        bool stable = store.PageRefCount(0) == 1;

        // 页满外溢：塞满页 0（首行 A/B/一 高 8 texel，余高 1016 一条整宽横幅恰好占尽），
        // 再换成页 0 没有的字形 ⇒ 落页 1。调和的**释放半边**只在这里有行为可见点
        //（退休路径不算——RetirePage 自己清引用列）：页 0 引用必须归零，
        // 不释放 = 页 0 的淘汰门 1 永远闭死，页级回收失效。
        store.EnsureResident(new GlyphRasterKey(999, 1, GlyphPageKind.Sdf, 1),
            GlyphStore.PageSize, 1016);
        text.SetText(n, "CD");
        bool g3 = f.TickAndCheck().Pass;
        bool spilled = store.PageCount == 2
            && store.PageRefCount(0) == 0 && store.PageRefCount(1) == 1;

        text.DetachText(n);                                 // 摘文本：对称释放 + 叶出流
        bool g4 = f.TickAndCheck().Pass;
        Check("页引用纪律: 发射登记 → 换字集合调和（幂等）→ 页满外溢释放旧页 → 摘除对称释放，叶出流门绿",
            g1 && g2 && g3 && g4 && referenced && stable && spilled
            && store.PageRefCount(1) == 0 && f.Stream.LeafCount == 0 && f.Sound());
    }

    private static void TextNuclearRebuildE2E()
    {
        (PipeFixture f, TextSystem text, _, GlyphStore store, ushort face) = TextRig();
        NodeHandle n = TextNode(f, text, "AB", TStyle(face), 0f, 0f, 40f, 20f);
        bool g1 = f.TickAndCheck().Pass;
        int leaf = TextLeafIndex(f, n);
        LeafRange r = f.Stream.Leaves[leaf];
        Vector4 rect0 = f.Stream.Quads[r.Start].Rect;

        store.RequestRebuild();                             // 核按钮：帧中只挂起
        bool intact = store.Generation == 1 && !store.IsPageRetired(0);

        bool g2 = f.TickAndCheck().Pass;                    // P2 全库退休 + 引用叶定向失效 → P7 原位重写
        ReadOnlySpan<QuadInstance> q = f.Stream.Quads.Slice(r.Start, r.Count);
        Check("核按钮端到端: P2 换代 → 引用叶被定向失效 → 同帧重驻留重写，画面无一帧空窗",
            g1 && g2 && intact && store.Generation == 2 && store.IsPageRetired(0)
            && store.Diag.FanOutMarks >= 1
            && (q[0].Color >> 24) == 0xFF && q[0].Rect == rect0      // 几何没动、α 没闪
            && store.PageRefCount(1) == 1                            // 新页重新登记
            && store.PageRefCount(0) == 0                            // 退休页引用列已清（RetirePage 语义；调和的释放半边由页满外溢用例执法）
            && f.Sound());
    }

    private static void TextCrossDpiBitIdentical()
    {
        // DPI（栅格密度）只进光栅半区：三种 RasterScale 下度量与排版产物必须逐位相等，
        // 只有字形页里的 texel 账（UV）允许不同——TextCore.Layout 根本没有 DPI 参数，
        // 这条用例是那半结构保证的行为侧对拍。
        var rigs = new (PipeFixture f, TextSystem text, NodeHandle n)[3];
        float[] scales = { 1f, 1.5f, 2f };
        for (int i = 0; i < 3; i++)
        {
            (PipeFixture f, TextSystem text, _, _, ushort face) = TextRig(rasterScale: scales[i]);
            NodeHandle n = TextNode(f, text, "AB 一\nCD", TStyle(face), 0f, 0f, 30f, 40f);
            bool pass = f.TickAndCheck().Pass;
            if (!pass || !f.Sound()) { Check("跨 DPI: 基线帧门绿", false); return; }
            rigs[i] = (f, text, n);
        }

        bool metricsSame = true, rectsSame = true;
        Vector2 m0 = rigs[0].text.Measure(rigs[0].n, float.PositiveInfinity);
        int leaf0 = TextLeafIndex(rigs[0].f, rigs[0].n);
        LeafRange r0 = rigs[0].f.Stream.Leaves[leaf0];
        for (int i = 1; i < 3; i++)
        {
            Vector2 mi = rigs[i].text.Measure(rigs[i].n, float.PositiveInfinity);
            metricsSame &= BitEq(m0.x, mi.x) && BitEq(m0.y, mi.y);
            LeafRange ri = rigs[i].f.Stream.Leaves[TextLeafIndex(rigs[i].f, rigs[i].n)];
            rectsSame &= ri.Count == r0.Count;
            for (int k = 0; rectsSame && k < r0.Count; k++)
            {
                QuadInstance qa = rigs[0].f.Stream.Quads[r0.Start + k];
                QuadInstance qb = rigs[i].f.Stream.Quads[ri.Start + k];
                rectsSame = qa.Rect == qb.Rect && qa.Color == qb.Color;
            }
        }
        // 栅格账确实变了：1x 与 2x 的 texel 宽应不同（否则上面等式是「没接 DPI」的假绿）。
        QuadInstance u1 = rigs[0].f.Stream.Quads[r0.Start];
        LeafRange r2 = rigs[2].f.Stream.Leaves[TextLeafIndex(rigs[2].f, rigs[2].n)];
        QuadInstance u2 = rigs[2].f.Stream.Quads[r2.Start];
        Check("文本·不变量 1: 跨 DPI（1x/1.5x/2x）度量与 quad 几何/色逐位相等，仅 UV 账随密度变",
            metricsSame && rectsSame && u1.UvA != u2.UvA);
    }
}
