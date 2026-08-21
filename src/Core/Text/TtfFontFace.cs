using System.Collections.Generic;
using FairyNext.Numerics;

namespace FairyNext.Core.Text;

// ============================================================================
// 移植自 fork：~/ECS/FairyGUI-unity Assets/Scripts/Core/Instanced/CurveFontStore.cs
// 行 278-597 @ 08a2d56（纯 TTF 解析段：表目录 / head / hhea / hmtx / cmap / loca / glyf
// 轮廓收集——fork 中零 Unity 依赖的 54%）。BakeGlyph 的 8-band 烘焙半段（378-401 的 band
// 循环）**不随行**：那是曲线字形源的 GPU banding 财产，归 M2-09；本件只到「二次轮廓点
// 收集 + bbox + advance」为止。
//
// 相对 fork 的改动（每条写病因，不是风格改写）：
//  1. static 单例 → 实例类。fork 的 sFontData/sGlyphs 是进程级单字体：多 face（font-map
//     配置供真 TTF 字节）与并行编包下静态态互踩。实例 Load 后不可变，任意线程可读。
//  2. Dictionary<char, GlyphInfo> 懒烘缓存不随行：缓存是「字形度量账」的财产，归
//     GlyphMetricsTable（append-only 三本账分账记是 M1-17 的结构承诺）；解析器保持无状态纯读。
//  3. cmap 增 format 12（fork 只有 format 4）：BMP 外码点直映需要它（emoji 位图路径的 key
//     在 BMP 外）；选择规则 = 有 12 用 12、无 12 用 4，M1 不做 GSUB/GPOS。
//  4. 全读口窗口校验（ByteBuffer 移植件同款硬化）：字体是外部输入，fork 只有越过整个
//     byte[] 才抛 IndexOutOfRange。这里每读口先验窗口，越窗抛 FontFaceException，
//     TryLoad 收敛成「拒载 + 诊断」——平面五 fuzz 纪律（任意字节输入永不越界读）的前置。
//  5. CFF 拒载保留并加严（fork 296-302 的 IOException 语义原样）：无 glyf = 结构性拒载
//     不降级——CFF/OTF 轮廓归 M2-09 编译器离线路径，emoji 归位图路径（plan M1-17 条目）。
//  6. maxp.numGlyphs 入验（fork 不读 maxp）：cmap 直映结果与 loca 下标都以它为上界，
//     corrupt cmap 给出的越界 gid 折成 .notdef（0）而不是拿去越窗读 loca。
//  7. hhea.lineGap 补读（fork 只读 ascent/descent）：度量半区的 lineHeight 要它。
// ============================================================================

/// <summary>
/// TTF 结构性拒载（平面四 B 文本：失败只有拒载一级，不降级、不静默半解析）。
/// 消息必须可执行（说清缺什么表/哪里越窗），fork 审计教训：
/// 「三个字段之后的 KeyNotFoundException」不算诊断。
/// </summary>
public sealed class FontFaceException : Exception
{
    /// <summary>建一条结构性拒载诊断。</summary>
    public FontFaceException(string message) : base(message) { }
}

/// <summary>
/// 一张 TrueType 字体的 CPU 字体表（**仅 glyf** 轮廓；度量半区的唯一数据源）。
/// <see cref="Load"/> 之后不可变——任何线程可读，无锁。
/// 所有度量以**字体单位**（font units）返回；em 归一（除以 <see cref="UnitsPerEm"/>）
/// 归 <see cref="GlyphMetricsTable"/>，本类不掺任何设备像素量（文本·不变量 1 的地基）。
/// </summary>
public sealed class TtfFontFace
{
    private readonly byte[] _data;
    private int _head, _loca, _glyf, _hmtx;
    private int _glyfLen, _locaLen;
    private int _cmap4 = -1;       // format 4 子表绝对偏移（-1 = 无）
    private int _cmap12 = -1;      // format 12 子表绝对偏移（-1 = 无）
    private int _segCount;         // format 4 段数
    private uint _cmap12Groups;    // format 12 组数
    private int _indexToLoc;       // 0 = short loca，1 = long loca
    private int _numHMetrics;

    /// <summary>来源名（诊断用；font-map 配置的 face 名或文件路径）。</summary>
    public string SourceName { get; }

    /// <summary>em 方格的字体单位数（head.unitsPerEm；零 = 结构性拒载）。</summary>
    public int UnitsPerEm { get; private set; }

    /// <summary>hhea.ascent（字体单位，向上为正）。</summary>
    public int Ascent { get; private set; }

    /// <summary>hhea.descent（字体单位，规范下典型为负值）。</summary>
    public int Descent { get; private set; }

    /// <summary>hhea.lineGap（字体单位；行高 = ascent − descent + lineGap）。</summary>
    public int LineGap { get; private set; }

    /// <summary>maxp.numGlyphs：gid 的开区间上界（cmap/loca 越界 gid 一律折成 .notdef）。</summary>
    public int GlyphCount { get; private set; }

    private TtfFontFace(byte[] data, string sourceName)
    {
        _data = data;
        SourceName = sourceName;
    }

    /// <summary>
    /// 解析一张 TTF（结构性问题抛 <see cref="FontFaceException"/>，永不返回半解析实例——
    /// fork 71-82「a half-parsed font must not read as loaded」的等价物，这里靠构造失败保证）。
    /// </summary>
    public static TtfFontFace Load(byte[] data, string sourceName)
    {
        if (data == null) throw new FontFaceException("TtfFontFace: 字体字节为 null（" + sourceName + "）");
        var face = new TtfFontFace(data, sourceName);
        face.ParseHeader();
        return face;
    }

    /// <summary>拒载收敛口：结构性问题返回 false + 诊断，不抛。</summary>
    public static bool TryLoad(byte[] data, string sourceName, out TtfFontFace? face, out string diagnostic)
    {
        try
        {
            face = Load(data, sourceName);
            diagnostic = string.Empty;
            return true;
        }
        catch (FontFaceException e)
        {
            face = null;
            diagnostic = e.Message;
            return false;
        }
    }

    // ── 读口（改动 4：每读口先验窗口）────────────────────────────────────────

    private ushort U16(int o)
    {
        if (o < 0 || o + 2 > _data.Length) throw Oob("u16", o);
        return (ushort)((_data[o] << 8) | _data[o + 1]);
    }

    private short S16(int o) => (short)U16(o);

    private uint U32(int o)
    {
        if (o < 0 || o + 4 > _data.Length) throw Oob("u32", o);
        return ((uint)_data[o] << 24) | ((uint)_data[o + 1] << 16) | ((uint)_data[o + 2] << 8) | _data[o + 3];
    }

    private byte U8(int o)
    {
        if (o < 0 || o >= _data.Length) throw Oob("u8", o);
        return _data[o];
    }

    private FontFaceException Oob(string what, int o) => new FontFaceException(
        "TtfFontFace: '" + SourceName + "' 越窗读（" + what + " @ " + o + "，字体共 " + _data.Length + " 字节）——字体截断或表偏移损坏");

    // ── 表解析（fork 287-326 + 改动 3/5/6/7）────────────────────────────────

    private void ParseHeader()
    {
        uint sfnt = U32(0);
        // 'OTTO' = CFF 轮廓的 OTF 容器：与「有表目录但无 glyf」是同一族，给同一条可执行诊断。
        bool otto = sfnt == 0x4F54544Fu;
        if (!otto && sfnt != 0x00010000u && sfnt != 0x74727565u /* Apple 'true' */)
            throw new FontFaceException(
                "TtfFontFace: '" + SourceName + "' 不是 sfnt 容器（version 0x" + sfnt.ToString("X8") + "）");

        int numTables = U16(4);
        if (12 + numTables * 16 > _data.Length)
            throw new FontFaceException("TtfFontFace: '" + SourceName + "' 表目录越窗（numTables=" + numTables + "）");

        var tables = new Dictionary<string, (int off, int len)>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            string tag = System.Text.Encoding.ASCII.GetString(_data, rec, 4);
            tables[tag] = ((int)U32(rec + 8), (int)U32(rec + 12));
        }

        // 改动 5（fork 296-302 语义保留）：CFF/OTF（Source Han/Noto Sans SC 之列）带 CFF 轮廓、
        // 无 glyf 表——按可执行消息拒载，而不是三个字段之后的 KeyNotFoundException。
        // 结构性拒载不降级：CFF 归 M2-09 编译器离线，emoji 归位图路径。
        if (otto || !tables.ContainsKey("glyf") || tables.ContainsKey("CFF "))
            throw new FontFaceException(
                "TtfFontFace: '" + SourceName + "' 无 glyf 表（CFF/OTF 轮廓）——运行时字形源只受理 "
                + "TrueType 二次轮廓；CFF 轮廓归编译器离线路径（M2-09），不降级。");

        (int off, int len) Require(string tag)
        {
            if (!tables.TryGetValue(tag, out var t))
                throw new FontFaceException("TtfFontFace: '" + SourceName + "' 缺必需表 '" + tag + "'");
            if (t.off < 0 || t.len < 0 || t.off + t.len > _data.Length)
                throw new FontFaceException("TtfFontFace: '" + SourceName + "' 表 '" + tag + "' 窗口越界（off="
                    + t.off + " len=" + t.len + "）");
            return t;
        }

        var head = Require("head"); var hhea = Require("hhea"); var hmtx = Require("hmtx");
        var maxp = Require("maxp"); var cmap = Require("cmap");
        var loca = Require("loca"); var glyf = Require("glyf");
        _head = head.off; _hmtx = hmtx.off;
        _loca = loca.off; _locaLen = loca.len;
        _glyf = glyf.off; _glyfLen = glyf.len;

        UnitsPerEm = U16(_head + 18);
        if (UnitsPerEm <= 0)
            throw new FontFaceException("TtfFontFace: '" + SourceName + "' head.unitsPerEm=0——em 归一除数不可为零");
        _indexToLoc = S16(_head + 50);
        if (_indexToLoc != 0 && _indexToLoc != 1)
            throw new FontFaceException("TtfFontFace: '" + SourceName + "' head.indexToLocFormat="
                + _indexToLoc + "（合法值只有 0/1）");

        Ascent = S16(hhea.off + 4);
        Descent = S16(hhea.off + 6);
        LineGap = S16(hhea.off + 8);            // 改动 7
        _numHMetrics = U16(hhea.off + 34);
        if (_numHMetrics <= 0)
            throw new FontFaceException("TtfFontFace: '" + SourceName + "' hhea.numberOfHMetrics=0");

        GlyphCount = U16(maxp.off + 4);          // 改动 6

        // cmap 子表选择（fork 311-325 + 改动 3）：fork 的 platform 过滤原样
        // （(3, 1|10) 或 0），在其命中集内记下首个 format 4 与首个 format 12。
        int count = U16(cmap.off + 2);
        for (int i = 0; i < count; i++)
        {
            int rec = cmap.off + 4 + i * 8;
            int platform = U16(rec);
            int encoding = U16(rec + 2);
            int off = (int)U32(rec + 4);
            if (!((platform == 3 && (encoding == 1 || encoding == 10)) || platform == 0)) continue;
            int sub = cmap.off + off;
            int format = U16(sub);
            if (format == 4 && _cmap4 < 0) _cmap4 = sub;
            else if (format == 12 && _cmap12 < 0) _cmap12 = sub;
        }
        if (_cmap4 < 0 && _cmap12 < 0)
            throw new FontFaceException("TtfFontFace: '" + SourceName
                + "' cmap 无可用子表（M1 直映只受理 format 4/12）");
        if (_cmap4 >= 0) _segCount = U16(_cmap4 + 6) / 2;
        if (_cmap12 >= 0) _cmap12Groups = U32(_cmap12 + 12);
    }

    // ── cmap 直映（fork 328-348 + format 12）────────────────────────────────

    /// <summary>
    /// 码点 → gid 直映（0 = .notdef）。有 format 12 用 12（BMP 内外统一），无 12 用 4（仅 BMP）。
    /// M1 不做 GSUB/GPOS：一码点一 gid，无连写/替换。越出 <see cref="GlyphCount"/> 的
    /// 直映结果折成 0（改动 6：corrupt cmap 不外泄越界 gid）。
    /// </summary>
    public ushort MapCodepoint(int codepoint)
    {
        if (codepoint < 0 || codepoint > 0x10FFFF) return 0;
        int gid = _cmap12 >= 0 ? MapFormat12(codepoint)
                : codepoint <= 0xFFFF ? MapFormat4(codepoint)
                : 0;
        return (uint)gid < (uint)GlyphCount ? (ushort)gid : (ushort)0;
    }

    /// <summary>纯 cmap 探针（fork 91-94 HasChar 的等价物）：不烘、不分配。字体回退链在此探。</summary>
    public bool HasCodepoint(int codepoint) => MapCodepoint(codepoint) != 0;

    private int MapFormat4(int ch)
    {
        if (_cmap4 < 0) return 0;
        int endBase = _cmap4 + 14, startBase = endBase + _segCount * 2 + 2,
            deltaBase = startBase + _segCount * 2, rangeBase = deltaBase + _segCount * 2;
        for (int s = 0; s < _segCount; s++)
        {
            if (ch <= U16(endBase + s * 2))
            {
                int start = U16(startBase + s * 2);
                if (ch < start) return 0;
                int ro = U16(rangeBase + s * 2);
                if (ro == 0)
                    return (ch + S16(deltaBase + s * 2)) & 0xFFFF;
                int idx = rangeBase + s * 2 + ro + (ch - start) * 2;
                int gid = U16(idx);
                return gid == 0 ? 0 : (gid + S16(deltaBase + s * 2)) & 0xFFFF;
            }
        }
        return 0;
    }

    private int MapFormat12(int cp)
    {
        // SequentialMapGroup[nGroups]，每组 12B {startChar, endChar, startGlyph}，按 startChar 升序。
        int lo = 0, hi = (int)_cmap12Groups - 1;
        int groups = _cmap12 + 16;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int rec = groups + mid * 12;
            uint start = U32(rec), end = U32(rec + 4);
            if (cp < start) hi = mid - 1;
            else if (cp > end) lo = mid + 1;
            else return (int)(U32(rec + 8) + ((uint)cp - start));
        }
        return 0;
    }

    // ── hmtx（fork 353-355）────────────────────────────────────────────────

    /// <summary>
    /// 水平推进量（字体单位）。gid ≥ numberOfHMetrics 用最后一条（等宽字体尾段共享，fork 354 原样）；
    /// gid 越出 <see cref="GlyphCount"/> 按 .notdef（gid 0）计。
    /// </summary>
    public int AdvanceOf(ushort gid)
    {
        if (gid >= GlyphCount) gid = 0;
        int hm = gid < _numHMetrics ? gid : _numHMetrics - 1;
        return U16(_hmtx + hm * 4);
    }

    // ── loca / glyf（fork 357-372 + 421-435 的窗口化）───────────────────────

    private bool GlyphRange(int gid, out int off, out int len)
    {
        off = 0; len = 0;
        if ((uint)gid >= (uint)GlyphCount) return false;
        if (_indexToLoc == 0)
        {
            if ((gid + 1) * 2 + 2 > _locaLen) throw Oob("loca[short]", _loca + gid * 2);
            off = U16(_loca + gid * 2) * 2;
            len = U16(_loca + gid * 2 + 2) * 2 - off;
        }
        else
        {
            if ((gid + 1) * 4 + 4 > _locaLen) throw Oob("loca[long]", _loca + gid * 4);
            off = (int)U32(_loca + gid * 4);
            len = (int)U32(_loca + gid * 4 + 4) - off;
        }
        if (len <= 0) return false;               // 无轮廓（空格/纯组合载体）：合法
        if (off < 0 || off + len > _glyfLen) throw Oob("glyf[" + gid + "]", _glyf + off);
        return true;
    }

    /// <summary>
    /// 字形头（简单与复合字形的 glyf 头都带 bbox，fork 371 注释原样）。
    /// 返回 false = 无轮廓（如空格），此时 bbox 为零矩形。坐标为字体单位、y 向上（TTF 原生系；
    /// 翻转归消费方——树内 y 向下是渲染平面的事，度量表不掺）。
    /// </summary>
    public bool TryGetGlyphHeader(ushort gid, out int contours, out Vector4 bbox)
    {
        contours = 0;
        bbox = default;
        if (!GlyphRange(gid, out int go, out _)) return false;
        int p = _glyf + go;
        contours = S16(p);
        bbox = new Vector4(S16(p + 2), S16(p + 4), S16(p + 6), S16(p + 8));
        return true;
    }

    /// <summary>
    /// 收集字形的二次轮廓（fork 375 的调用形态：单位变换起步）。输出追加进
    /// <paramref name="quadPoints"/>：每条二次曲线 3 点（起点、控制点、终点），字体单位。
    /// 返回 false = 不支持的构造（point-matching 对位复合 / 复合嵌套超 4 层），调用方按
    /// 「幽灵回退」处理（fork 376：不发轮廓）。无轮廓字形返回 true 且不追加任何点。
    /// 暂存归调用方（QuadReassembler 改动①同款纪律：并行安全由调用栈给，不设进程级共享暂存）。
    /// </summary>
    public bool TryCollectOutline(ushort gid, List<Vector2> quadPoints)
    {
        if (quadPoints == null) { UiAssert.That(false, "TryCollectOutline 收到 null 输出表"); return false; }
        return CollectOutline(gid, quadPoints, 1, 0, 0, 1, 0, 0, 0);
    }

    private float F2Dot14(int o) => S16(o) / 16384f;

    /// <summary>
    /// fork 417-502 逐行：复合空间里收集二次轮廓，递归展开复合组件（累计仿射
    /// x' = a·x + c·y + e，y' = b·x + d·y + f）；offset 按复合空间（MS/Apple 常规），
    /// 罕见的 point-matching 对位返回 false；镜像组件的绕向翻转由非零环绕规则吸收。
    /// </summary>
    private bool CollectOutline(int gid, List<Vector2> outPts,
        float a, float b, float c, float d, float e, float f, int depth)
    {
        if (depth > 4)
            return false;
        if (!GlyphRange(gid, out int go, out int gl))
            return true;   // 空组件（如空格载体）：合法（fork 434）
        int p = _glyf + go;
        int contours = S16(p);

        if (contours >= 0)
        {
            bool identity = a == 1 && b == 0 && c == 0 && d == 1 && e == 0 && f == 0;
            int start = outPts.Count;
            ParseSimple(p, contours, outPts);
            if (!identity)
            {
                for (int i = start; i < outPts.Count; i++)
                {
                    Vector2 pt = outPts[i];
                    outPts[i] = new Vector2(a * pt.x + c * pt.y + e, b * pt.x + d * pt.y + f);
                }
            }
            return true;
        }

        int o = p + 10;
        while (true)
        {
            int flags = U16(o); o += 2;
            int cgid = U16(o); o += 2;
            float dx, dy;
            if ((flags & 0x0001) != 0)   // ARG_1_AND_2_ARE_WORDS
            {
                dx = S16(o); o += 2;
                dy = S16(o); o += 2;
            }
            else
            {
                dx = (sbyte)U8(o); o++;
                dy = (sbyte)U8(o); o++;
            }
            if ((flags & 0x0002) == 0)   // not ARGS_ARE_XY_VALUES: point matching
                return false;

            float ca = 1, cb = 0, cc = 0, cd = 1;
            if ((flags & 0x0008) != 0)          // WE_HAVE_A_SCALE
            {
                ca = cd = F2Dot14(o); o += 2;
            }
            else if ((flags & 0x0040) != 0)     // X_AND_Y_SCALE
            {
                ca = F2Dot14(o); o += 2;
                cd = F2Dot14(o); o += 2;
            }
            else if ((flags & 0x0080) != 0)     // TWO_BY_TWO
            {
                ca = F2Dot14(o); cb = F2Dot14(o + 2);
                cc = F2Dot14(o + 4); cd = F2Dot14(o + 6);
                o += 8;
            }

            // full = parent ∘ (component matrix, compound-space offset)
            float na = a * ca + c * cb, nb = b * ca + d * cb;
            float nc = a * cc + c * cd, nd = b * cc + d * cd;
            float ne = a * dx + c * dy + e, nf = b * dx + d * dy + f;
            if (!CollectOutline(cgid, outPts, na, nb, nc, nd, ne, nf, depth + 1))
                return false;

            if ((flags & 0x0020) == 0)   // MORE_COMPONENTS
                break;
        }
        return true;
    }

    /// <summary>
    /// fork 504-595 逐行：简单字形 → 二次曲线三元组。contour 环从首个 on-curve 点起步
    /// （全 off-curve 环插中点），相邻 on-curve 点间补中点为控制点（直线段 = 退化二次），
    /// 相邻 off-curve 点间按 TTF implied on-curve 规则取中点。
    /// </summary>
    private void ParseSimple(int p, int contours, List<Vector2> outPts)
    {
        int o = p + 10;
        var ends = new int[contours];
        for (int i = 0; i < contours; i++) { ends[i] = U16(o); o += 2; }
        int nPts = ends[contours - 1] + 1;
        o += 2 + U16(o);

        var flags = new byte[nPts];
        for (int i = 0; i < nPts;)
        {
            byte fl = U8(o++);
            flags[i++] = fl;
            if ((fl & 8) != 0)
            {
                int rep = U8(o++);
                for (int r = 0; r < rep && i < nPts; r++) flags[i++] = fl;   // i 上界钳制：corrupt repeat 不越 flags 数组
            }
        }
        var xs = new short[nPts];
        var ys = new short[nPts];
        short v = 0;
        for (int i = 0; i < nPts; i++)
        {
            byte fl = flags[i];
            if ((fl & 2) != 0) { byte dx = U8(o++); v += (fl & 16) != 0 ? dx : (short)-dx; }
            else if ((fl & 16) == 0) { v += S16(o); o += 2; }
            xs[i] = v;
        }
        v = 0;
        for (int i = 0; i < nPts; i++)
        {
            byte fl = flags[i];
            if ((fl & 4) != 0) { byte dy = U8(o++); v += (fl & 32) != 0 ? dy : (short)-dy; }
            else if ((fl & 32) == 0) { v += S16(o); o += 2; }
            ys[i] = v;
        }

        int startPt = 0;
        for (int c = 0; c < contours; c++)
        {
            int endPt = ends[c];
            int n = endPt - startPt + 1;
            if (n < 2) { startPt = endPt + 1; continue; }

            var ring = new List<(Vector2 pt, bool on)>(n + 1);
            for (int i = 0; i < n; i++)
            {
                int idx = startPt + i;
                ring.Add((new Vector2(xs[idx], ys[idx]), (flags[idx] & 1) != 0));
            }
            int first = ring.FindIndex(r => r.on);
            if (first < 0)
            {
                ring.Insert(1, ((ring[0].pt + ring[1].pt) * 0.5f, true));
                first = 1;
            }
            var seq = new List<(Vector2 pt, bool on)>(ring.Count + 1);
            for (int i = 0; i <= ring.Count; i++)
                seq.Add(ring[(first + i) % ring.Count]);

            Vector2 cur = seq[0].pt;
            int k = 1;
            while (k < seq.Count)
            {
                if (seq[k].on)
                {
                    outPts.Add(cur); outPts.Add((cur + seq[k].pt) * 0.5f); outPts.Add(seq[k].pt);
                    cur = seq[k].pt;
                    k++;
                }
                else
                {
                    Vector2 ctrl = seq[k].pt;
                    Vector2 next;
                    bool implied = k + 1 < seq.Count && !seq[k + 1].on;
                    if (implied)
                        next = (ctrl + seq[k + 1].pt) * 0.5f;
                    else if (k + 1 < seq.Count)
                        next = seq[k + 1].pt;
                    else
                        next = seq[0].pt;
                    outPts.Add(cur); outPts.Add(ctrl); outPts.Add(next);
                    cur = next;
                    k += implied ? 1 : 2;
                }
            }
            startPt = endPt + 1;
        }
    }
}
