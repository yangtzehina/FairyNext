using FairyNext.Numerics;

namespace FairyNext.Core.Text;

// ============================================================================
// TextCore：无状态排版核心（架构平面四 B「无状态排版核心与 LayoutArena」的 M1 落地）。
//
// <see cref="TextCore.Layout"/> 是**静态纯函数**：文本 + 样式 + 约束 + 度量半区 → 定位字形流。
// 不持有任何可变状态、不读全局、任何时刻可重跑——同输入必同输出**逐位**相等
// （文本·不变量 2；行为侧由「双跑逐字节」用例钉住）。
//
// ── 三条结构承诺（比测试更硬的半边）────────────────────────────────────────
//  1. **DPI 不是参数**（文本·不变量 1 的结构保证）：本函数的入参没有任何栅格密度量，
//     度量全程在 em 空间求值、只在收尾一步乘 <see cref="TextStyle.SizePx"/>。
//     栅格字号（DPI × SizePx）只存在于光栅半区（TextSystem 的发射侧），类型上进不来。
//  2. **只依赖 <see cref="IGlyphMetricsSource"/>**（M1-17 留缝①）：advance/kerning/行高全部
//     来自度量半区；页账/locator 在签名里不存在——页淘汰想影响排版，无路可走。
//  3. **两把尺同一实现路径**（plan M1-18）：折行尺（约束宽下的 ContentW）与自然尺
//     （<see cref="TextLayoutResult.MaxIntrinsicWidth"/>）都从同一份整形产物
//     （arena 的 Adv/GlyphId 数组）经同一个求和函数 <see cref="AdvanceRun"/> 得出，
//     且求和的浮点结合序全库唯一（先 kerning 后 advance，逐字形累加）——
//     「同宽时两把尺位等」不是巧合是构造。
//
// ── M1 面（plan：cmap 直映 + 纯文本单 style）──────────────────────────────
//  · 整形 = 一码点一 gid（无 GSUB/GPOS；真 shaper 契约随 M2-09 进驻）；
//  · kerning 从第一天按「advance + kerning」求和写（M1 恒 0，M2-09 落数据零改动）；
//  · 富文本 RichRun/簇映射/内联对象归 M2-08——本文件的结果类型只有字形流与行表。
//
// ── 断行细则（golden 用例的规则原文；改规则先改这里）─────────────────────
//  · '\n' 硬断行（'\r' 忽略——\r\n 归一的 M1 形态）；
//  · 软断行机会 = ①前一字符是空格类（0x20/0x09/0x3000）②前一字符是 '-'
//    ③当前或前一字符是 CJK（IsCjk 的封闭区间表；CJK 任意断，无 kinsoku）；
//  · 溢出判定：待放字形会使行宽超过限宽、且行内已有内容才折行——**空格类永不触发折行**
//    （行尾空格允许溢出，CSS 同款），单个超限字形独占一行而不空转；
//  · 折行回卷到本行最近的软断行机会；没有机会则在当前字形前强拆（超长词强拆）；
//  · 行宽 = 行内全部字形（**含行尾空格**）的 advance+kerning 累计。
//
// ── ellipsis 细则 ─────────────────────────────────────────────────────────
//  · 只在 <see cref="TextOverflow.Ellipsis"/> 下生效；行数按 maxHeight 截（至少留 1 行），
//    末可见行若因截行或超宽而收尾，从行尾回退字形直到「保留段 + …」放得下；
//  · 一个字形都放不下时**仍给省略号**（可超宽；Truncated=true 有声）——
//    「不够一个字符」的形态是省略号，不是空行；
//  · 省略号字形 = cmap('…' U+2026)，缺失时退 '.'（单点；M2-08 富文本期再谈三点形态），
//    再缺失退 .notdef——链条每级都确定，无环境依赖。
// ============================================================================

/// <summary>水平对齐。</summary>
public enum TextHAlign : byte
{
    /// <summary>左。</summary>
    Left = 0,
    /// <summary>中。</summary>
    Center = 1,
    /// <summary>右。</summary>
    Right = 2,
}

/// <summary>垂直对齐（maxHeight 有限时才有意义；无限高恒为顶对齐）。</summary>
public enum TextVAlign : byte
{
    /// <summary>顶。</summary>
    Top = 0,
    /// <summary>中。</summary>
    Middle = 1,
    /// <summary>底。</summary>
    Bottom = 2,
}

/// <summary>溢出策略。</summary>
public enum TextOverflow : byte
{
    /// <summary>照排照发（裁剪归父容器的 clip 域，不归排版）。</summary>
    Visible = 0,
    /// <summary>尾部省略号截断。</summary>
    Ellipsis = 1,
}

/// <summary>
/// 文本样式（M1 纯文本单 style；富文本 FormatEntry 表归 M2-08）。
/// **没有任何栅格密度字段**——这是文本·不变量 1 的类型半边（见文件头结构承诺 1）。
/// </summary>
public struct TextStyle : IEquatable<TextStyle>
{
    /// <summary>face id（<see cref="GlyphMetricsTable.RegisterFace"/> 发出）。</summary>
    public ushort FaceId;
    /// <summary>字号（逻辑 px / em；度量与栅格密度解耦——这是逻辑单位不是设备像素）。</summary>
    public float SizePx;
    /// <summary>基色（RGBA8，未乘 α——颜色纯函数的定义域，机制 7）。</summary>
    public uint Color;
    /// <summary>水平对齐。</summary>
    public TextHAlign HAlign;
    /// <summary>垂直对齐。</summary>
    public TextVAlign VAlign;
    /// <summary>是否自动折行。</summary>
    public bool Wrap;
    /// <summary>溢出策略。</summary>
    public TextOverflow Overflow;

    /// <inheritdoc/>
    public readonly bool Equals(TextStyle o) =>
        FaceId == o.FaceId && BitEquals.Eq(SizePx, o.SizePx) && Color == o.Color
        && HAlign == o.HAlign && VAlign == o.VAlign && Wrap == o.Wrap && Overflow == o.Overflow;

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is TextStyle s && Equals(s);

    /// <inheritdoc/>
    public readonly override int GetHashCode() =>
        HashCode.Combine(FaceId, SizePx, Color, (byte)HAlign, (byte)VAlign, Wrap, (byte)Overflow);
}

/// <summary>无状态排版核心（纯函数集合；见文件头）。</summary>
public static class TextCore
{
    // 工作条目 flags（arena.BreakFlags 的位）
    private const byte FlagSoftBreak = 1;   // 可在本字形前折行
    private const byte FlagSpace = 2;       // 空格类（不触发折行、不产 quad——advance 照占）
    private const byte FlagNewline = 4;     // 硬断行哨兵（不进结果字形流）

    /// <summary>
    /// 排版。<paramref name="maxWidth"/>/<paramref name="maxHeight"/> 是**逻辑 px** 约束
    /// （+∞ = 不限；≤0/NaN 按不限处理并断言）；结果切片指向 <paramref name="arena"/>，
    /// 同 arena 的下一次调用整块作废（<see cref="TextLayoutResult"/> 的内存契约）。
    /// </summary>
    public static void Layout(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, float maxHeight,
        IGlyphMetricsSource metrics, LayoutArena arena, out TextLayoutResult result)
    {
        result = default;
        if (metrics == null || arena == null || !(style.SizePx > 0f))
        {
            UiAssert.That(false, "TextCore.Layout 非法入参（metrics/arena null 或 SizePx ≤ 0）");
            return;
        }
        float size = style.SizePx;
        bool maxWValid = maxWidth > 0f && !float.IsNaN(maxWidth);
        bool maxHValid = maxHeight > 0f && !float.IsNaN(maxHeight);
        float limitEm = maxWValid && !float.IsPositiveInfinity(maxWidth)
            ? maxWidth / size : float.PositiveInfinity;

        arena.Prepare(text.Length + 2, text.Length + 2);
        FaceMetrics fm = metrics.MetricsOfFace(style.FaceId);
        float lineH = fm.LineHeightEm;
        float ascent = fm.AscentEm;

        // ── ① 整形（M1 直映）：工作条目 = 真字形 + 换行哨兵，写进 arena 的三条平行列 ──
        int m = 0;
        int prevCp = -1;
        for (int i = 0; i < text.Length;)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i += 2;
            }
            else i += 1;

            if (cp == '\r') continue;
            if (cp == '\n')
            {
                arena.Glyphs[m].GlyphId = 0;
                arena.Adv[m] = 0f;
                arena.BreakFlags[m] = FlagNewline;
                m++;
                prevCp = -1;                          // 硬断行两侧无 kerning
                continue;
            }

            ushort gid = metrics.MapCodepoint(style.FaceId, cp);
            metrics.TryGetGlyphMetrics(style.FaceId, gid, out GlyphMetrics gm);
            byte flags = 0;
            if (IsSpace(cp)) flags |= FlagSpace;
            if (prevCp >= 0 && (IsSpace(prevCp) || prevCp == '-' || IsCjk(cp) || IsCjk(prevCp)))
                flags |= FlagSoftBreak;
            arena.Glyphs[m].GlyphId = gid;
            arena.Adv[m] = gm.AdvanceEm;
            arena.BreakFlags[m] = flags;
            m++;
            prevCp = cp;
        }

        // 自然尺（两把尺之二）必须在压缩改写 arena 之前取——它读的正是这份整形产物。
        float naturalEm = NaturalWidthEm(arena, metrics, style.FaceId, m);

        // ── ② 断行（em 空间贪心；规则见文件头）──────────────────────────────
        int lineCount = 0;
        int lineStart = 0, lastBreak = -1;
        float pen = 0f;
        ushort prevGid = 0;
        bool hasPrev = false;
        for (int i = 0; i < m; i++)
        {
            byte fl = arena.BreakFlags[i];
            if ((fl & FlagNewline) != 0)
            {
                CommitLine(arena, metrics, style.FaceId, ref lineCount, lineStart, i);
                lineStart = i + 1;
                lastBreak = -1;
                pen = 0f;
                hasPrev = false;
                continue;
            }
            if ((fl & FlagSoftBreak) != 0 && i > lineStart) lastBreak = i;

            float kern = hasPrev ? metrics.KerningEm(style.FaceId, prevGid, arena.Glyphs[i].GlyphId) : 0f;
            if (style.Wrap && (fl & FlagSpace) == 0 && i > lineStart
                && pen + kern + arena.Adv[i] > limitEm)
            {
                int br = lastBreak > lineStart ? lastBreak : i;
                CommitLine(arena, metrics, style.FaceId, ref lineCount, lineStart, br);
                lineStart = br;
                lastBreak = -1;
                pen = AdvanceRun(arena, metrics, style.FaceId, br, i);
                kern = i > br ? metrics.KerningEm(style.FaceId, arena.Glyphs[i - 1].GlyphId, arena.Glyphs[i].GlyphId) : 0f;
            }
            pen += kern;                              // 结合序与 AdvanceRun 逐位同：先 kern 后 advance
            pen += arena.Adv[i];
            prevGid = arena.Glyphs[i].GlyphId;
            hasPrev = true;
        }
        CommitLine(arena, metrics, style.FaceId, ref lineCount, lineStart, m);   // 末行（含空文本的唯一空行）

        // ── ③ ellipsis（行数截断 + 末行收尾）────────────────────────────────
        bool truncated = false;
        int visibleLines = lineCount;
        int ellipsisLine = -1;
        ushort ellGid = 0;
        float ellAdvEm = 0f;
        if (style.Overflow == TextOverflow.Ellipsis && lineCount > 0)
        {
            if (maxHValid && !float.IsPositiveInfinity(maxHeight) && lineH > 0f)
            {
                int fit = (int)(maxHeight / size / lineH);
                if (fit < 1) fit = 1;                 // 至少留一行——0 行的文本框没有可解释的形态
                if (fit < visibleLines) { visibleLines = fit; truncated = true; }
            }
            int last = visibleLines - 1;
            bool cutLines = visibleLines < lineCount;
            bool overWidth = !float.IsPositiveInfinity(limitEm) && arena.Lines[last].Width > limitEm;
            if (cutLines || overWidth)
            {
                truncated = true;
                ResolveEllipsisGlyph(metrics, style.FaceId, out ellGid, out ellAdvEm);
                int start = arena.Lines[last].GlyphStart;
                int keep = arena.Lines[last].GlyphCount;
                float kept = ellAdvEm;                // keep==0 的形态：只给省略号（可超宽，有声）
                while (keep > 0)
                {
                    float w = AdvanceRun(arena, metrics, style.FaceId, start, start + keep);
                    w += metrics.KerningEm(style.FaceId, arena.Glyphs[start + keep - 1].GlyphId, ellGid);
                    w += ellAdvEm;
                    if (float.IsPositiveInfinity(limitEm) || w <= limitEm) { kept = w; break; }
                    keep--;
                }
                ellipsisLine = last;
                arena.Lines[last].GlyphCount = keep;
                arena.Lines[last].Width = kept;
            }
        }

        // ── ④ 对齐偏移（em）────────────────────────────────────────────────
        float contentWEm = 0f;
        for (int l = 0; l < visibleLines; l++)
            if (arena.Lines[l].Width > contentWEm) contentWEm = arena.Lines[l].Width;
        float contentHEm = visibleLines * lineH;
        float alignRefEm = float.IsPositiveInfinity(limitEm) ? contentWEm : limitEm;
        float boxHEm = maxHValid && !float.IsPositiveInfinity(maxHeight) ? maxHeight / size : contentHEm;
        float vOffEm = style.VAlign switch
        {
            TextVAlign.Middle => (boxHEm - contentHEm) * 0.5f,
            TextVAlign.Bottom => boxHEm - contentHEm,
            _ => 0f,
        };
        for (int l = 0; l < visibleLines; l++)
        {
            arena.LineOff[l] = style.HAlign switch
            {
                TextHAlign.Center => (alignRefEm - arena.Lines[l].Width) * 0.5f,
                TextHAlign.Right => alignRefEm - arena.Lines[l].Width,
                _ => 0f,
            };
        }

        // ── ⑤ 收尾：原地压缩 + 定位 + **唯一一次**乘 SizePx ─────────────────
        // 压缩安全性：写下标 j ≤ 读下标 i 恒成立（j 只数保留条目，i 含哨兵与被截字形），
        // 唯一的追加（省略号）只发生在末可见行——其后再无读取。
        int j = 0;
        for (int l = 0; l < visibleLines; l++)
        {
            TextLine ln = arena.Lines[l];             // 先拷字段：下面要覆写同一槽
            int newStart = j;
            float penEm = 0f;
            ushort pg = 0;
            bool hp = false;
            for (int i = ln.GlyphStart; i < ln.GlyphStart + ln.GlyphCount; i++)
            {
                ushort gid = arena.Glyphs[i].GlyphId;
                if (hp) penEm += metrics.KerningEm(style.FaceId, pg, gid);
                arena.Glyphs[j] = new PlacedGlyph
                {
                    GlyphId = gid,
                    Line = (ushort)l,
                    X = (arena.LineOff[l] + penEm) * size,
                    Y = (l * lineH + vOffEm + ascent) * size,
                };
                penEm += arena.Adv[i];
                pg = gid;
                hp = true;
                j++;
            }
            if (l == ellipsisLine)
            {
                if (hp) penEm += metrics.KerningEm(style.FaceId, pg, ellGid);
                arena.Glyphs[j] = new PlacedGlyph
                {
                    GlyphId = ellGid,
                    Line = (ushort)l,
                    X = (arena.LineOff[l] + penEm) * size,
                    Y = (l * lineH + vOffEm + ascent) * size,
                };
                j++;
            }
            arena.Lines[l] = new TextLine
            {
                Y = (l * lineH + vOffEm) * size,
                Width = ln.Width * size,
                Baseline = (l * lineH + vOffEm + ascent) * size,
                GlyphStart = newStart,
                GlyphCount = j - newStart,
            };
        }

        result.Glyphs = new Span<PlacedGlyph>(arena.Glyphs, 0, j);
        result.Lines = new Span<TextLine>(arena.Lines, 0, visibleLines);
        result.ContentW = contentWEm * size;
        result.ContentH = contentHEm * size;
        result.MaxIntrinsicWidth = naturalEm * size;
        result.Truncated = truncated;
    }

    // ── 求和的唯一定义点（两把尺 + 断行 + 定位共用；结合序 = 先 kern 后 advance）──

    /// <summary>工作区间 [start, end) 的 advance+kerning 累计（em；区间内不得含换行哨兵）。</summary>
    private static float AdvanceRun(LayoutArena arena, IGlyphMetricsSource metrics, ushort faceId, int start, int end)
    {
        float pen = 0f;
        for (int i = start; i < end; i++)
        {
            if (i > start) pen += metrics.KerningEm(faceId, arena.Glyphs[i - 1].GlyphId, arena.Glyphs[i].GlyphId);
            pen += arena.Adv[i];
        }
        return pen;
    }

    /// <summary>自然尺：不折行（只认硬断行）时的最宽段宽（em）。与折行尺同数据同求和。</summary>
    private static float NaturalWidthEm(LayoutArena arena, IGlyphMetricsSource metrics, ushort faceId, int m)
    {
        float best = 0f;
        int start = 0;
        for (int i = 0; i <= m; i++)
        {
            if (i < m && (arena.BreakFlags[i] & FlagNewline) == 0) continue;
            float w = AdvanceRun(arena, metrics, faceId, start, i);
            if (w > best) best = w;
            start = i + 1;
        }
        return best;
    }

    private static void CommitLine(LayoutArena arena, IGlyphMetricsSource metrics, ushort faceId,
        ref int lineCount, int start, int end)
    {
        arena.Lines[lineCount] = new TextLine
        {
            GlyphStart = start,                        // 此刻是工作下标；收尾步改写为压缩后的下标
            GlyphCount = end - start,
            Width = AdvanceRun(arena, metrics, faceId, start, end),   // 此刻是 em；收尾步乘 SizePx
        };
        lineCount++;
    }

    private static void ResolveEllipsisGlyph(IGlyphMetricsSource metrics, ushort faceId,
        out ushort gid, out float advEm)
    {
        gid = metrics.MapCodepoint(faceId, 0x2026);    // '…'
        if (gid == 0) gid = metrics.MapCodepoint(faceId, '.');
        metrics.TryGetGlyphMetrics(faceId, gid, out GlyphMetrics gm);   // gid 0 = .notdef 也有 advance
        advEm = gm.AdvanceEm;
    }

    // ── 字符分类（封闭表；改分类 = 改断行契约，golden 会红）─────────────────

    /// <summary>空格类：0x20 / 0x09 / 0x3000（NBSP 0xA0 不在内——它就是不可断的普通字形）。</summary>
    private static bool IsSpace(int cp) => cp == 0x20 || cp == 0x09 || cp == 0x3000;

    /// <summary>CJK（任意断）区间表：统汉 + 扩展 + 假名 + 谚文音节 + 兼容 + 全角形。</summary>
    private static bool IsCjk(int cp) =>
        (cp >= 0x2E80 && cp <= 0x303F)     // 部首/康熙/CJK 符号标点
        || (cp >= 0x3040 && cp <= 0x30FF)  // 平/片假名
        || (cp >= 0x31C0 && cp <= 0x31EF)  // 笔画
        || (cp >= 0x3400 && cp <= 0x4DBF)  // 扩 A
        || (cp >= 0x4E00 && cp <= 0x9FFF)  // 统汉
        || (cp >= 0xAC00 && cp <= 0xD7AF)  // 谚文音节
        || (cp >= 0xF900 && cp <= 0xFAFF)  // 兼容表意
        || (cp >= 0xFF00 && cp <= 0xFFEF)  // 全角/半角形
        || (cp >= 0x20000 && cp <= 0x3FFFF);   // 扩 B+
}
