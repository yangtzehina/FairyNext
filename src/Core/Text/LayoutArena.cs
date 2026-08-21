namespace FairyNext.Core.Text;

// ============================================================================
// 排版结果与 LayoutArena（架构平面四 B「无状态排版核心与 LayoutArena」的 M1 落地）。
//
// 结果的内存契约（文本·不变量 3 的结构半边）：
//  · 全部结果切片指向调用方 LayoutArena 的单块数组——排版自身**零分配**；
//  · <see cref="TextLayoutResult"/> 是 ref struct：存字段/闭包捕获/跨帧持有=编译错，
//    「跨帧缓存排版行引用」整类陷阱在类型上消失；
//  · 下次 Layout 复用同一 arena 即整块作废——没有逐行 Borrow/Return 池，也就没有归还遗漏。
//
// 终态的 ClusterEntry/DecoRect/InlinePlace/LinkRect 四条切片随 M2-08 富文本 IR 进驻；
// M1 面（纯文本单 style）只有字形流与行表两条。
// ============================================================================

/// <summary>
/// 已定位字形（主输出流）。<see cref="X"/>/<see cref="Y"/> 是**像素**坐标（+y 向下、Y=基线）——
/// 排版内部全程在 em 空间求值、只在收尾一步乘 <see cref="TextStyle.SizePx"/>（跨 DPI bit-identical
/// 的结构保证：DPI 根本不是 <c>TextCore.Layout</c> 的参数）。
/// </summary>
public struct PlacedGlyph
{
    /// <summary>字形 id（0 = .notdef，照常占位照常发射——缺字必须看得见，不是静默消失）。</summary>
    public ushort GlyphId;
    /// <summary>所在行下标。</summary>
    public ushort Line;
    /// <summary>笔位 x（px；已含对齐偏移）。字形轮廓的落笔点，不是墨迹左缘。</summary>
    public float X;
    /// <summary>基线 y（px）。墨迹矩形由消费方用字形 bbox 自算（度量半区永远在手）。</summary>
    public float Y;
}

/// <summary>一行的度量（px；行框顶 + 基线 + 内容宽——行尾空格计入宽，见 TextCore 断行细则）。</summary>
public struct TextLine
{
    /// <summary>行框顶 y（px；已含垂直对齐偏移）。</summary>
    public float Y;
    /// <summary>行内容宽（px；advance 累计，含行尾空格）。</summary>
    public float Width;
    /// <summary>基线 y（px）= 行顶 + ascent。</summary>
    public float Baseline;
    /// <summary>本行首字形在字形流里的下标。</summary>
    public int GlyphStart;
    /// <summary>本行字形数（空行为 0）。</summary>
    public int GlyphCount;
}

/// <summary>
/// 排版结果（ref struct：**出不了帧**，跨帧持有编译不过——文本·不变量 3）。
/// 切片全部指向产出它的 <see cref="LayoutArena"/>；同 arena 的下一次 Layout 整块作废本结果。
/// </summary>
public ref struct TextLayoutResult
{
    /// <summary>定位字形流（含空格等无轮廓字形——它们占 advance、不产 quad）。</summary>
    public Span<PlacedGlyph> Glyphs;
    /// <summary>行表。</summary>
    public Span<TextLine> Lines;
    /// <summary>内容宽（px）= 最宽行宽。</summary>
    public float ContentW;
    /// <summary>内容高（px）= 行数 × 行高。</summary>
    public float ContentH;
    /// <summary>不限宽自然尺的最宽行宽（px）——measure 免重排快路径的第二把尺。</summary>
    public float MaxIntrinsicWidth;
    /// <summary>是否发生了 ellipsis 截断。</summary>
    public bool Truncated;
}

/// <summary>
/// 排版工作内存（每工作线程一个；M1 单线程 = 每 TextSystem 一个）。
/// 单块分配、整块作废：<see cref="Prepare"/> 只保证容量，不清旧内容——旧字节由本次写入覆盖，
/// 结果切片只盖住 <c>[0, n)</c>，残留字节在切片之外（「复用不残留」由行为用例逐字节钉死）。
/// </summary>
public sealed class LayoutArena
{
    internal PlacedGlyph[] Glyphs = new PlacedGlyph[64];
    internal TextLine[] Lines = new TextLine[8];
    internal float[] Adv = new float[64];          // 每字形 advance（em）——回卷重排笔位的依据
    internal byte[] BreakFlags = new byte[64];     // 每字形「可在其前断行」位
    internal float[] LineOff = new float[8];       // 每行水平对齐偏移（em，收尾换算用）

    /// <summary>保证容量（不清内容；容量只增不减，稳态零分配）。</summary>
    public void Prepare(int glyphCapacity, int lineCapacity)
    {
        if (Glyphs.Length < glyphCapacity)
        {
            int cap = Glyphs.Length;
            while (cap < glyphCapacity) cap *= 2;
            Array.Resize(ref Glyphs, cap);
            Array.Resize(ref Adv, cap);
            Array.Resize(ref BreakFlags, cap);
        }
        if (Lines.Length < lineCapacity)
        {
            int cap = Lines.Length;
            while (cap < lineCapacity) cap *= 2;
            Array.Resize(ref Lines, cap);
            Array.Resize(ref LineOff, cap);
        }
    }
}
