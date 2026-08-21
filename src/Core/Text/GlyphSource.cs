using System.Collections.Generic;
using FairyNext.Numerics;

namespace FairyNext.Core.Text;

// ============================================================================
// M1-17 字形源双半区契约（架构平面四 B「字形源双半区契约」的 M1 落地形态）：
//
//   度量半区 IGlyphMetricsSource —— P5 布局要的 advance / kerning / lineHeight。
//     纯 CPU 字体表，**不碰页**：实现类（GlyphMetricsTable）在类型上拿不到
//     GlyphStore 的引用，「度量永不因页淘汰而变」是结构保证而不是纪律条款。
//     这是文本·不变量 1（跨 DPI bit-identical 度量）的地基：本半区只有 em 逻辑单位，
//     没有任何设备像素量可掺。
//
//   光栅半区 IGlyphRasterSource —— P7 发射要的页坐标 UV。碰页账（GlyphStore），
//     只影响像素、永不影响度量；页淘汰/换代都只发生在这一侧。
//
// 架构终态的 IGlyphSource.Shape（整段归一化文本 + run 窗口的真 shaper 契约）不在 M1：
// M1 直映 = 一码点一 gid（MapCodepoint），Shape 面随 M2-09 曲线源/真 shaper 进驻。
// ============================================================================

/// <summary>页型（**页的属性不是店的属性**：一家店同时开位图页与 SDF 页）。</summary>
public enum GlyphPageKind : byte
{
    /// <summary>位图页：emoji 与小字号（SDF 小字无 hinting 是物理限制）。</summary>
    Bitmap = 0,
    /// <summary>SDF 页：中字号主力。</summary>
    Sdf = 1,
}

/// <summary>
/// 驻留状态。<see cref="Pending"/> 在 M1-17 尚无产者——本包的 EnsureResident 同步落账
/// （账本操作无异步半程）；异步烘焙队列（曲线提取 job/P8 预算上传）随 M2-09 进驻后，
/// 「已申请未烘完」才会落在 Pending（排版照发 quad、alpha=0 淡入的那一态）。
/// </summary>
public enum ResidencyState : byte
{
    /// <summary>从未入店（或所在页已被淘汰）：要先 <see cref="IGlyphRasterSource.EnsureResident"/>。</summary>
    Missing = 0,
    /// <summary>已申请、像素未就绪（M1-17 无产者，见类型文档）。</summary>
    Pending = 1,
    /// <summary>在店且页活着：locator 可用。</summary>
    Resident = 2,
}

/// <summary>
/// 光栅半区的字形键。像素身份 = 字形身份 × 页型 × 栅格字号——同一 gid 的位图 12px 与
/// SDF 32px 是两条独立的位置账；度量半区没有这个维度（度量与栅格密度解耦）。
/// </summary>
public readonly struct GlyphRasterKey : IEquatable<GlyphRasterKey>
{
    /// <summary>face id（<see cref="GlyphMetricsTable.RegisterFace"/> 发出）。</summary>
    public readonly ushort FaceId;
    /// <summary>字形 id（cmap 直映结果；0 = .notdef）。</summary>
    public readonly ushort GlyphId;
    /// <summary>页型。</summary>
    public readonly GlyphPageKind Kind;
    /// <summary>栅格字号（px）。SDF 页按烘焙字号记（per-size 路由在源侧，键只记事实）。</summary>
    public readonly ushort PxSize;

    /// <summary>建键。</summary>
    public GlyphRasterKey(ushort faceId, ushort glyphId, GlyphPageKind kind, ushort pxSize)
    {
        FaceId = faceId; GlyphId = glyphId; Kind = kind; PxSize = pxSize;
    }

    /// <inheritdoc/>
    public bool Equals(GlyphRasterKey other) =>
        FaceId == other.FaceId && GlyphId == other.GlyphId && Kind == other.Kind && PxSize == other.PxSize;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GlyphRasterKey k && Equals(k);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        (FaceId << 16) ^ GlyphId ^ ((int)Kind << 30) ^ (PxSize << 8);

    /// <inheritdoc/>
    public override string ToString() => $"face{FaceId}:g{GlyphId}:{Kind}@{PxSize}px";
}

/// <summary>
/// 页坐标 locator（光栅半区的产出）。**代内终身有效**（文本·不变量 4）：同一
/// <see cref="Generation"/> 内页 append-only 永不重排，字段一经发出不变；
/// 页被淘汰后查询返回 <see cref="ResidencyState.Missing"/>，但已发出的旧值本身不被改写。
/// </summary>
public readonly struct GlyphLocator : IEquatable<GlyphLocator>
{
    /// <summary>页型。</summary>
    public readonly GlyphPageKind Kind;
    /// <summary>页号（店内页账下标）。</summary>
    public readonly ushort Page;
    /// <summary>页内 texel 坐标（页边长 <see cref="GlyphStore.PageSize"/>）。</summary>
    public readonly ushort X;
    /// <summary>页内 texel 坐标。</summary>
    public readonly ushort Y;
    /// <summary>texel 宽。</summary>
    public readonly ushort W;
    /// <summary>texel 高。</summary>
    public readonly ushort H;
    /// <summary>发出时的店代（度量缓存键的组成部分）。</summary>
    public readonly uint Generation;

    internal GlyphLocator(GlyphPageKind kind, ushort page, ushort x, ushort y, ushort w, ushort h, uint generation)
    {
        Kind = kind; Page = page; X = x; Y = y; W = w; H = h; Generation = generation;
    }

    /// <inheritdoc/>
    public bool Equals(GlyphLocator o) =>
        Kind == o.Kind && Page == o.Page && X == o.X && Y == o.Y && W == o.W && H == o.H && Generation == o.Generation;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GlyphLocator l && Equals(l);

    /// <inheritdoc/>
    public override int GetHashCode() => (Page << 16) ^ (X << 8) ^ Y ^ ((int)Generation << 4);
}

/// <summary>face 级度量（em 逻辑单位；跨 DPI/后端 bit-identical——除法只除 unitsPerEm 一个整数）。</summary>
public readonly struct FaceMetrics
{
    /// <summary>基线以上（em；正值向上）。</summary>
    public readonly float AscentEm;
    /// <summary>基线以下（em；hhea 惯例为负值）。</summary>
    public readonly float DescentEm;
    /// <summary>行间隙（em）。</summary>
    public readonly float LineGapEm;

    internal FaceMetrics(float ascentEm, float descentEm, float lineGapEm)
    {
        AscentEm = ascentEm; DescentEm = descentEm; LineGapEm = lineGapEm;
    }

    /// <summary>行高 = ascent − descent + lineGap（em）。</summary>
    public float LineHeightEm => AscentEm - DescentEm + LineGapEm;
}

/// <summary>字形级度量（em 逻辑单位；与 per-size 路由无关——文本·不变量 10）。</summary>
public readonly struct GlyphMetrics
{
    /// <summary>水平推进量（em）。</summary>
    public readonly float AdvanceEm;
    /// <summary>轮廓边界盒（em；y 向上的 TTF 原生系）。无轮廓字形四值全零。</summary>
    public readonly float XMinEm, YMinEm, XMaxEm, YMaxEm;
    /// <summary>false = 无轮廓（空格类）：照常有 advance，没有可发射的像素。</summary>
    public readonly bool HasOutline;

    internal GlyphMetrics(float advanceEm, float xMinEm, float yMinEm, float xMaxEm, float yMaxEm, bool hasOutline)
    {
        AdvanceEm = advanceEm;
        XMinEm = xMinEm; YMinEm = yMinEm; XMaxEm = xMaxEm; YMaxEm = yMaxEm;
        HasOutline = hasOutline;
    }
}

/// <summary>
/// 度量半区（P5 布局的唯一字形依赖面）。实现必须满足：
/// 纯函数（同输入同输出位等）、零设备像素量、**与页账在类型上隔离**（不持有光栅半区引用）。
/// </summary>
public interface IGlyphMetricsSource
{
    /// <summary>face 级度量。</summary>
    FaceMetrics MetricsOfFace(ushort faceId);

    /// <summary>码点 → gid 直映（0 = .notdef）。M1 无 GSUB/GPOS：一码点一 gid。</summary>
    ushort MapCodepoint(ushort faceId, int codepoint);

    /// <summary>字形级度量（advance/bbox，em）。false = face 或 gid 越界。</summary>
    bool TryGetGlyphMetrics(ushort faceId, ushort glyphId, out GlyphMetrics metrics);

    /// <summary>
    /// 字偶间距（em）。**M1 恒 0**：kern/GPOS 归 M2-09（plan「M1 不做 GSUB/GPOS」）。
    /// 接口位置现在钉死——排版核心（M1-18）从第一天就按「advance + kerning」求和写，
    /// M2-09 落数据时排版代码零改动。
    /// </summary>
    float KerningEm(ushort faceId, ushort left, ushort right);
}

/// <summary>
/// 光栅半区（P7 发射的页坐标面）。只影响像素、永不影响度量；
/// 页淘汰/generation 换代都只发生在这一侧。
/// </summary>
public interface IGlyphRasterSource
{
    /// <summary>查 locator。<see cref="ResidencyState.Missing"/> 含「所在页已淘汰」。</summary>
    ResidencyState TryGetLocator(in GlyphRasterKey key, out GlyphLocator locator);

    /// <summary>
    /// 入店（同 key 幂等：已在店直接返回既有 locator，**不重分配**——append-only 三本账）。
    /// texel 尺寸由调用方给（栅格化器知道自己烘多大；账本不猜）。
    /// </summary>
    GlyphLocator EnsureResident(in GlyphRasterKey key, ushort texelW, ushort texelH);
}

/// <summary>
/// 字形度量账（三本账之一，append-only）+ face 注册表 = 度量半区的实现。
///
/// **结构隔离**：本类只持 <see cref="TtfFontFace"/>（不可变 CPU 字体表），没有任何
/// 通往 GlyphStore/页账的引用——页淘汰想影响度量，在类型上就无路可走
/// （「双半区独立」测试从行为侧再钉一遍）。
///
/// 账本形态：条目按首查序追加，(faceId, glyphId) → 条目下标的索引只增不删；
/// 重复查询命中既有条目（<see cref="EntryCount"/> 不动）。条目一经落账不可变——
/// 度量缓存键（Hash(约束) ⊗ Hash(文本) ⊗ generation）能把条目下标当稳定 id 用。
/// </summary>
public sealed class GlyphMetricsTable : IGlyphMetricsSource
{
    private readonly List<TtfFontFace> _faces = new List<TtfFontFace>();
    private readonly List<GlyphMetrics> _entries = new List<GlyphMetrics>();
    private readonly Dictionary<uint, int> _index = new Dictionary<uint, int>();   // (faceId<<16)|gid → 条目下标

    /// <summary>已注册 face 数。</summary>
    public int FaceCount => _faces.Count;

    /// <summary>度量账条目数（append-only：只增不减，重复查询不增）。</summary>
    public int EntryCount => _entries.Count;

    /// <summary>注册一张 face，发 face id（append-only，id 即下标、永不复用）。</summary>
    public ushort RegisterFace(TtfFontFace face)
    {
        if (face == null) { UiAssert.That(false, "RegisterFace 收到 null face"); return 0; }
        UiAssert.That(_faces.Count < ushort.MaxValue, "face 注册表溢出（u16 id 空间）");
        _faces.Add(face);
        return (ushort)(_faces.Count - 1);
    }

    /// <summary>按 id 取 face（越界 = 断言 + null）。</summary>
    public TtfFontFace? FaceOf(ushort faceId)
    {
        if (faceId < _faces.Count) return _faces[faceId];
        UiAssert.That(false, "FaceOf 越界 face id " + faceId);
        return null;
    }

    /// <inheritdoc/>
    public FaceMetrics MetricsOfFace(ushort faceId)
    {
        TtfFontFace? face = FaceOf(faceId);
        if (face == null) return default;
        float upem = face.UnitsPerEm;
        return new FaceMetrics(face.Ascent / upem, face.Descent / upem, face.LineGap / upem);
    }

    /// <inheritdoc/>
    public ushort MapCodepoint(ushort faceId, int codepoint) =>
        FaceOf(faceId)?.MapCodepoint(codepoint) ?? (ushort)0;

    /// <inheritdoc/>
    public bool TryGetGlyphMetrics(ushort faceId, ushort glyphId, out GlyphMetrics metrics)
    {
        metrics = default;
        TtfFontFace? face = FaceOf(faceId);
        if (face == null || glyphId >= face.GlyphCount) return false;

        uint key = ((uint)faceId << 16) | glyphId;
        if (_index.TryGetValue(key, out int at))
        {
            metrics = _entries[at];
            return true;
        }

        float upem = face.UnitsPerEm;
        bool hasOutline = face.TryGetGlyphHeader(glyphId, out _, out Vector4 bbox);
        metrics = new GlyphMetrics(
            face.AdvanceOf(glyphId) / upem,
            hasOutline ? bbox.x / upem : 0f,
            hasOutline ? bbox.y / upem : 0f,
            hasOutline ? bbox.z / upem : 0f,
            hasOutline ? bbox.w / upem : 0f,
            hasOutline);
        _index[key] = _entries.Count;
        _entries.Add(metrics);
        return true;
    }

    /// <inheritdoc/>
    public float KerningEm(ushort faceId, ushort left, ushort right) => 0f;   // M1：kern/GPOS 归 M2-09
}
