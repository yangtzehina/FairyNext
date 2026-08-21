using FairyNext.Core;
using FairyNext.Core.Text;
using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-17 字形源 + GlyphStore 用例（Program 的 partial 分片）。
/// 覆盖：TTF 解析 golden（合成字体钉字节 + Monaco.ttf 独立神谕钉值）/ cmap 直映（format 4 BMP、
/// format 12 BMP 外与优先级）/ CFF 结构性拒载有声 / 三本账 append-only（重复入店不重分配、
/// locator 代内终身）/ 页型是页的属性 / 页淘汰双门（正例 + 两负例 + 提交二次验门）/
/// generation 仅 P2（帧中申请不生效、P2 生效、核按钮下引用叶被全局失效）/
/// 双半区独立（页淘汰后度量位等不变、度量账与页账类型隔离）。
///
/// 合成字体由 <see cref="Ttf"/> 固定字节构造（解析结果由构造推得，测试自洽无外部依赖）；
/// Monaco.ttf golden 的期望值**独立采自 Python struct 手解**（非本移植件自产——真神谕），
/// 系统字体缺失时 SKIP 有声（Apple 字体许可不允许入库，故读系统路径）。
/// </summary>
public static partial class Program
{
    // ── 合成 TTF 构造器（测试固定资产：字节由代码定死，等价入库的二进制 fixture）──

    private static class Ttf
    {
        public static byte[] Build(uint sfntVersion, params (string tag, byte[] body)[] tables)
        {
            int n = tables.Length;
            int cur = 12 + n * 16;
            var offs = new int[n];
            for (int i = 0; i < n; i++)
            {
                cur = (cur + 3) & ~3;
                offs[i] = cur;
                cur += tables[i].body.Length;
            }
            var b = new byte[cur];
            WU32(b, 0, sfntVersion);
            WU16(b, 4, (ushort)n);
            for (int i = 0; i < n; i++)
            {
                int rec = 12 + i * 16;
                for (int k = 0; k < 4; k++) b[rec + k] = (byte)tables[i].tag[k];
                WU32(b, rec + 8, (uint)offs[i]);
                WU32(b, rec + 12, (uint)tables[i].body.Length);
                Array.Copy(tables[i].body, 0, b, offs[i], tables[i].body.Length);
            }
            return b;
        }

        private static void WU16(byte[] b, int o, ushort v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }

        private static void WU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static void A16(List<byte> b, ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        private static void A16(List<byte> b, short v) => A16(b, (ushort)v);

        public static byte[] Head(ushort upem, short indexToLoc)
        {
            var b = new byte[54];                      // head 表定长 54B；解析器只读 18(unitsPerEm)/50(indexToLocFormat)
            WU16(b, 18, upem);
            WU16(b, 50, (ushort)indexToLoc);
            return b;
        }

        public static byte[] Hhea(short ascent, short descent, short lineGap, ushort numHMetrics)
        {
            var b = new byte[36];
            WU16(b, 4, (ushort)ascent);
            WU16(b, 6, (ushort)descent);
            WU16(b, 8, (ushort)lineGap);
            WU16(b, 34, numHMetrics);
            return b;
        }

        public static byte[] Maxp(ushort numGlyphs)
        {
            var b = new byte[6];
            WU16(b, 4, numGlyphs);
            return b;
        }

        /// <summary>hmtx：longHorMetric[numHMetrics] + 尾部 lsb[trailingLsb]（等宽尾段共享 advance）。</summary>
        public static byte[] Hmtx((ushort adv, short lsb)[] metrics, int trailingLsb)
        {
            var b = new byte[metrics.Length * 4 + trailingLsb * 2];
            for (int i = 0; i < metrics.Length; i++)
            {
                WU16(b, i * 4, metrics[i].adv);
                WU16(b, i * 4 + 2, (ushort)metrics[i].lsb);
            }
            return b;
        }

        public static byte[] Cmap(params (ushort platform, ushort encoding, byte[] sub)[] subs)
        {
            var b = new List<byte>();
            A16(b, (ushort)0);
            A16(b, (ushort)subs.Length);
            int off = 4 + subs.Length * 8;
            foreach (var s in subs)
            {
                A16(b, s.platform);
                A16(b, s.encoding);
                b.Add((byte)(off >> 24)); b.Add((byte)(off >> 16)); b.Add((byte)(off >> 8)); b.Add((byte)off);
                off += s.sub.Length;
            }
            foreach (var s in subs) b.AddRange(s.sub);
            return b.ToArray();
        }

        /// <summary>format 4 子表（idRangeOffset 恒 0 的直 delta 段；终段 0xFFFF/delta=1 自动补）。</summary>
        public static byte[] CmapFormat4((ushort start, ushort end, short delta)[] segs)
        {
            int segCount = segs.Length + 1;
            int len = 14 + segCount * 2 * 4 + 2;
            var b = new byte[len];
            WU16(b, 0, 4);
            WU16(b, 2, (ushort)len);
            WU16(b, 6, (ushort)(segCount * 2));
            int endB = 14, startB = endB + segCount * 2 + 2, deltaB = startB + segCount * 2, rangeB = deltaB + segCount * 2;
            for (int i = 0; i < segCount; i++)
            {
                (ushort start, ushort end, short delta) s = i < segs.Length ? segs[i] : ((ushort)0xFFFF, (ushort)0xFFFF, (short)1);
                WU16(b, endB + i * 2, s.end);
                WU16(b, startB + i * 2, s.start);
                WU16(b, deltaB + i * 2, (ushort)s.delta);
                WU16(b, rangeB + i * 2, 0);
            }
            return b;
        }

        /// <summary>format 12 子表（组按 startChar 升序传入）。</summary>
        public static byte[] CmapFormat12((uint start, uint end, uint gid)[] groups)
        {
            var b = new byte[16 + groups.Length * 12];
            WU16(b, 0, 12);
            WU32(b, 4, (uint)b.Length);
            WU32(b, 12, (uint)groups.Length);
            for (int i = 0; i < groups.Length; i++)
            {
                WU32(b, 16 + i * 12, groups[i].start);
                WU32(b, 20 + i * 12, groups[i].end);
                WU32(b, 24 + i * 12, groups[i].gid);
            }
            return b;
        }

        /// <summary>简单字形（坐标全走 int16 delta 编码，flags 不用 repeat——字节形态最直白）。</summary>
        public static byte[] SimpleGlyph(short xmin, short ymin, short xmax, short ymax,
            ushort[] endPts, (short x, short y, bool on)[] pts)
        {
            var b = new List<byte>();
            A16(b, (short)endPts.Length);
            A16(b, xmin); A16(b, ymin); A16(b, xmax); A16(b, ymax);
            foreach (ushort e in endPts) A16(b, e);
            A16(b, (ushort)0);                        // instructionLength
            foreach (var p in pts) b.Add(p.on ? (byte)1 : (byte)0);
            short prev = 0;
            foreach (var p in pts) { A16(b, (short)(p.x - prev)); prev = p.x; }
            prev = 0;
            foreach (var p in pts) { A16(b, (short)(p.y - prev)); prev = p.y; }
            while (b.Count % 4 != 0) b.Add(0);        // 4 对齐：short loca 存 offset/2，且下一字形起点对齐
            return b.ToArray();
        }

        /// <summary>复合字形：单组件、字空间 XY 偏移（ARG_1_AND_2_ARE_WORDS|ARGS_ARE_XY_VALUES）。</summary>
        public static byte[] CompositeGlyph(short xmin, short ymin, short xmax, short ymax,
            ushort componentGid, short dx, short dy)
        {
            var b = new List<byte>();
            A16(b, (short)-1);
            A16(b, xmin); A16(b, ymin); A16(b, xmax); A16(b, ymax);
            A16(b, (ushort)0x0003);
            A16(b, componentGid);
            A16(b, dx); A16(b, dy);
            while (b.Count % 4 != 0) b.Add(0);
            return b.ToArray();
        }

        /// <summary>拼 glyf 段 + 生成 short loca（字形体已 4 对齐，offset/2 必为整）。</summary>
        public static (byte[] glyf, byte[] loca) GlyfLoca(byte[][] glyphs)
        {
            var glyf = new List<byte>();
            var loca = new byte[(glyphs.Length + 1) * 2];
            for (int i = 0; i < glyphs.Length; i++)
            {
                WU16(loca, i * 2, (ushort)(glyf.Count / 2));
                glyf.AddRange(glyphs[i]);
            }
            WU16(loca, glyphs.Length * 2, (ushort)(glyf.Count / 2));
            return (glyf.ToArray(), loca);
        }
    }

    /// <summary>
    /// 主测试字体：upem=1000、short loca、4 字形——gid0 空（.notdef 无轮廓）、gid1 方块
    /// (0,0)-(500,500)、gid2 带 off-curve 的三角、gid3 复合（gid1 偏移 +100,0）。
    /// numHMetrics=2（gid≥2 共享 gid1 的 600——钉等宽尾段钳制路径）。cmap format 4：A/B/C→1/2/3。
    /// </summary>
    private static byte[] SynthFontA(ushort gid1Advance = 600)
    {
        var gid1 = Ttf.SimpleGlyph(0, 0, 500, 500, new ushort[] { 3 }, new (short, short, bool)[]
        {
            (0, 0, true), (500, 0, true), (500, 500, true), (0, 500, true),
        });
        var gid2 = Ttf.SimpleGlyph(0, 0, 500, 500, new ushort[] { 2 }, new (short, short, bool)[]
        {
            (0, 0, true), (250, 500, false), (500, 0, true),
        });
        var gid3 = Ttf.CompositeGlyph(100, 0, 600, 500, 1, 100, 0);
        (byte[] glyf, byte[] loca) = Ttf.GlyfLoca(new[] { Array.Empty<byte>(), gid1, gid2, gid3 });
        return Ttf.Build(0x00010000,
            ("cmap", Ttf.Cmap(((ushort)3, (ushort)1, Ttf.CmapFormat4(new[] { ((ushort)0x41, (ushort)0x43, (short)(1 - 0x41)) })))),
            ("glyf", glyf),
            ("head", Ttf.Head(1000, 0)),
            ("hhea", Ttf.Hhea(800, -200, 90, 2)),
            ("hmtx", Ttf.Hmtx(new[] { ((ushort)500, (short)0), (gid1Advance, (short)0) }, 2)),
            ("loca", loca),
            ("maxp", Ttf.Maxp(4)));
    }

    /// <summary>store 测试台：树 + 失效协议 + 已接钩子的店，店钟起步帧 1。</summary>
    private static (NodeTable t, Invalidation inv, GlyphStore store) StoreRig()
    {
        var t = new NodeTable();
        var inv = new Invalidation(t);
        var store = new GlyphStore();
        store.AttachInvalidation(inv);
        store.BeginFrame(1);
        return (t, inv, store);
    }

    private static bool BitEq(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    public static void TextGlyphSuite()
    {
        TtfGoldenSynthetic();
        TtfCmapDirect();
        TtfCmapFormat12();
        TtfRefusals();
        TtfMonacoGolden();
        GlyphLedgersAppendOnly();
        GlyphPageKinds();
        GlyphLocatorLifetime();
        GlyphEvictionDoubleGate();
        GlyphGenerationOnlyP2();
        GlyphRebuildFanOut();
        GlyphHalfPlaneIndependence();
        GlyphMetricsFaceIsolation();
        GlyphStoreHookExclusive();
    }

    // ── TTF 解析 ────────────────────────────────────────────────────────────

    private static void TtfGoldenSynthetic()
    {
        var face = TtfFontFace.Load(SynthFontA(), "SynthA");
        Check("TTF 头字段（upem/asc/desc/gap/numGlyphs）",
            face.UnitsPerEm == 1000 && face.Ascent == 800 && face.Descent == -200
            && face.LineGap == 90 && face.GlyphCount == 4);

        // 方块：4 条二次（每边一条退化直线，中点为控制点）——12 点钉字节
        var pts = new List<Vector2>();
        bool ok = face.TryCollectOutline(1, pts);
        Check("方块字形轮廓 4 quads 钉点", ok && pts.Count == 12
            && pts[0] == new Vector2(0, 0) && pts[1] == new Vector2(250, 0) && pts[2] == new Vector2(500, 0)
            && pts[3] == new Vector2(500, 0) && pts[4] == new Vector2(500, 250) && pts[5] == new Vector2(500, 500)
            && pts[9] == new Vector2(0, 500) && pts[11] == new Vector2(0, 0));

        // off-curve 三角：一条真二次 + 一条闭合直线 = 2 quads
        pts.Clear();
        ok = face.TryCollectOutline(2, pts);
        Check("off-curve 三角 2 quads 钉点", ok && pts.Count == 6
            && pts[0] == new Vector2(0, 0) && pts[1] == new Vector2(250, 500) && pts[2] == new Vector2(500, 0)
            && pts[3] == new Vector2(500, 0) && pts[4] == new Vector2(250, 0) && pts[5] == new Vector2(0, 0));

        // 复合：gid1 平移 (100,0)；点数同源
        pts.Clear();
        ok = face.TryCollectOutline(3, pts);
        Check("复合字形 = 组件平移", ok && pts.Count == 12
            && pts[0] == new Vector2(100, 0) && pts[2] == new Vector2(600, 0) && pts[5] == new Vector2(600, 500));

        Check("advance（含等宽尾段钳制 gid≥numHMetrics）",
            face.AdvanceOf(0) == 500 && face.AdvanceOf(1) == 600 && face.AdvanceOf(2) == 600 && face.AdvanceOf(3) == 600);

        bool has = face.TryGetGlyphHeader(1, out int contours, out Vector4 bbox);
        bool none = face.TryGetGlyphHeader(0, out _, out _);
        Check("glyf 头 bbox 钉字节 + 空字形无轮廓",
            has && contours == 1 && bbox == new Vector4(0, 0, 500, 500) && !none);
    }

    private static void TtfCmapDirect()
    {
        var face = TtfFontFace.Load(SynthFontA(), "SynthA");
        Check("cmap format 4 直映（BMP 命中与脱靶）",
            face.MapCodepoint('A') == 1 && face.MapCodepoint('B') == 2 && face.MapCodepoint('C') == 3
            && face.MapCodepoint('D') == 0 && face.MapCodepoint(0x1F600) == 0
            && face.HasCodepoint('A') && !face.HasCodepoint('D'));
    }

    private static void TtfCmapFormat12()
    {
        // 同字体带 format 4（A→1）与 format 12（A→2、😀→1）：有 12 用 12——BMP 内也归它。
        var gid1 = Ttf.SimpleGlyph(0, 0, 500, 500, new ushort[] { 3 }, new (short, short, bool)[]
        {
            (0, 0, true), (500, 0, true), (500, 500, true), (0, 500, true),
        });
        (byte[] glyf, byte[] loca) = Ttf.GlyfLoca(new[] { Array.Empty<byte>(), gid1, Array.Empty<byte>() });
        var font = Ttf.Build(0x00010000,
            ("cmap", Ttf.Cmap(
                ((ushort)3, (ushort)1, Ttf.CmapFormat4(new[] { ((ushort)0x41, (ushort)0x41, (short)(1 - 0x41)) })),
                ((ushort)3, (ushort)10, Ttf.CmapFormat12(new[] { (0x41u, 0x41u, 2u), (0x1F600u, 0x1F601u, 1u) })))),
            ("glyf", glyf),
            ("head", Ttf.Head(1000, 0)),
            ("hhea", Ttf.Hhea(800, -200, 90, 1)),
            ("hmtx", Ttf.Hmtx(new[] { ((ushort)500, (short)0) }, 2)),
            ("loca", loca),
            ("maxp", Ttf.Maxp(3)));
        var face = TtfFontFace.Load(font, "SynthB");
        Check("cmap format 12 直映（BMP 外 + 优先于 format 4）",
            face.MapCodepoint(0x1F600) == 1 && face.MapCodepoint(0x1F601) == 2
            && face.MapCodepoint(0x1F602) == 0 && face.MapCodepoint('A') == 2);
    }

    private static void TtfRefusals()
    {
        // CFF/OTF：OTTO 容器（真实世界的 Source Han/Noto SC 形态）——结构性拒载 + 可执行诊断
        var otto = Ttf.Build(0x4F54544F,
            ("CFF ", new byte[] { 1, 0, 4, 4 }),
            ("head", Ttf.Head(1000, 0)),
            ("hhea", Ttf.Hhea(800, -200, 90, 1)),
            ("maxp", Ttf.Maxp(1)));
        bool ok = TtfFontFace.TryLoad(otto, "SourceHan.otf", out var face, out string diag);
        Check("CFF 拒载有声（OTTO 容器）", !ok && face == null && diag.Contains("CFF") && diag.Contains("glyf"));

        // sfnt 0x00010000 但缺 glyf：同族同诊断
        var noGlyf = Ttf.Build(0x00010000,
            ("head", Ttf.Head(1000, 0)),
            ("hhea", Ttf.Hhea(800, -200, 90, 1)),
            ("maxp", Ttf.Maxp(1)));
        ok = TtfFontFace.TryLoad(noGlyf, "noglyf.ttf", out _, out diag);
        Check("缺 glyf 拒载有声", !ok && diag.Contains("glyf"));

        // Load 口径：结构性问题 = 抛 FontFaceException（不返回半解析实例）
        bool threw = false;
        try { TtfFontFace.Load(otto, "SourceHan.otf"); }
        catch (FontFaceException) { threw = true; }
        Check("Load 口径抛 FontFaceException", threw);

        // 截断字体：越窗读收敛成拒载 + 诊断，不 panic 不越界（fuzz 纪律前置）
        byte[] whole = SynthFontA();
        var half = new byte[whole.Length / 2];
        Array.Copy(whole, half, half.Length);
        ok = TtfFontFace.TryLoad(half, "truncated.ttf", out _, out diag);
        Check("截断字体拒载有声", !ok && diag.Length > 0);
    }

    private static void TtfMonacoGolden()
    {
        // 期望值独立采自 Python struct 手解 /System/Library/Fonts/Monaco.ttf（2026-08，macOS 26）
        // ——非本移植件自产，是真神谕。Apple 字体许可不允许入库，读系统路径、缺失 SKIP 有声。
        const string path = "/System/Library/Fonts/Monaco.ttf";
        if (!File.Exists(path))
        {
            Check("Monaco.ttf golden [SKIP 有声：系统字体缺失，本机无从对拍]", true);
            return;
        }
        var face = TtfFontFace.Load(File.ReadAllBytes(path), path);
        Check("Monaco 头字段（upem 2048 / asc 2048 / desc -512 / gap 171 / 1678 字形，long loca）",
            face.UnitsPerEm == 2048 && face.Ascent == 2048 && face.Descent == -512
            && face.LineGap == 171 && face.GlyphCount == 1678);

        ushort gidA = face.MapCodepoint('A');
        ushort gidO = face.MapCodepoint('o');
        bool hasA = face.TryGetGlyphHeader(gidA, out int contA, out Vector4 bbA);
        bool hasO = face.TryGetGlyphHeader(gidO, out _, out Vector4 bbO);
        Check("Monaco 'A'：gid 36 / advance 1229 / 2 contour / bbox (33,0,1197,1552)",
            gidA == 36 && face.AdvanceOf(gidA) == 1229 && hasA && contA == 2
            && bbA == new Vector4(33, 0, 1197, 1552));
        Check("Monaco 'o'：gid 82 / advance 1229（等宽）/ bbox (87,-31,1142,1148)",
            gidO == 82 && face.AdvanceOf(gidO) == 1229 && hasO && bbO == new Vector4(87, -31, 1142, 1148));

        var pts = new List<Vector2>();
        bool okA = face.TryCollectOutline(gidA, pts);
        int quadsA = pts.Count / 3;
        Vector2 a0 = pts[0], a1 = pts[1], a2 = pts[2];
        pts.Clear();
        bool okO = face.TryCollectOutline(gidO, pts);
        Check("Monaco 轮廓 golden：'A' 13 quads 首条 (227,0)-(130,0)-(33,0)、'o' 16 quads",
            okA && quadsA == 13 && a0 == new Vector2(227, 0) && a1 == new Vector2(130, 0) && a2 == new Vector2(33, 0)
            && okO && pts.Count / 3 == 16);
    }

    // ── GlyphStore 三本账 ───────────────────────────────────────────────────

    private static void GlyphLedgersAppendOnly()
    {
        (_, _, GlyphStore store) = StoreRig();
        var k1 = new GlyphRasterKey(0, 1, GlyphPageKind.Sdf, 32);
        var k2 = new GlyphRasterKey(0, 2, GlyphPageKind.Sdf, 32);

        GlyphLocator l1 = store.EnsureResident(k1, 20, 24);
        GlyphLocator l2 = store.EnsureResident(k2, 20, 24);
        int placements = store.PlacementCount;
        long appended = store.Diag.PlacementsAppended;

        GlyphLocator again = store.EnsureResident(k1, 20, 24);
        Check("重复入店不重分配（append-only 账 + 去重收据）",
            placements == 2 && store.PlacementCount == 2 && store.Diag.PlacementsAppended == appended
            && store.Diag.DedupHits == 1 && again.Equals(l1) && !l1.Equals(l2));

        ResidencyState st = store.TryGetLocator(k1, out GlyphLocator q);
        ResidencyState miss = store.TryGetLocator(new GlyphRasterKey(0, 9, GlyphPageKind.Sdf, 32), out _);
        Check("locator 查询（Resident 位等 / 未入店 Missing）",
            st == ResidencyState.Resident && q.Equals(l1) && miss == ResidencyState.Missing);
    }

    private static void GlyphPageKinds()
    {
        (_, _, GlyphStore store) = StoreRig();
        GlyphLocator bmp = store.EnsureResident(new GlyphRasterKey(0, 1, GlyphPageKind.Bitmap, 12), 16, 16);
        GlyphLocator sdf = store.EnsureResident(new GlyphRasterKey(0, 1, GlyphPageKind.Sdf, 32), 40, 40);
        Check("页型是页的属性（位图/SDF 同店分页分账）",
            store.PageCount == 2 && bmp.Page != sdf.Page
            && store.PageKindOf(bmp.Page) == GlyphPageKind.Bitmap
            && store.PageKindOf(sdf.Page) == GlyphPageKind.Sdf
            && bmp.Kind == GlyphPageKind.Bitmap && sdf.Kind == GlyphPageKind.Sdf);
    }

    private static void GlyphLocatorLifetime()
    {
        (_, _, GlyphStore store) = StoreRig();
        var k0 = new GlyphRasterKey(0, 0, GlyphPageKind.Sdf, 32);
        GlyphLocator first = store.EnsureResident(k0, 512, 512);
        // 512² 一页放 4 个：第 5 个开新页（shelf 满行→满页）。页满开新页永不重排。
        for (ushort g = 1; g <= 4; g++)
            store.EnsureResident(new GlyphRasterKey(0, g, GlyphPageKind.Sdf, 32), 512, 512);
        ResidencyState st = store.TryGetLocator(k0, out GlyphLocator requeried);
        GlyphLocator fifth = store.EnsureResident(new GlyphRasterKey(0, 4, GlyphPageKind.Sdf, 32), 512, 512);
        Check("locator 代内终身有效（页满开新页，旧 locator 位等不动）",
            store.PageCount == 2 && st == ResidencyState.Resident && requeried.Equals(first)
            && first.Page == 0 && first.X == 0 && first.Y == 0 && fifth.Page == 1);
    }

    // ── 页淘汰双门 + generation 仅 P2 ───────────────────────────────────────

    private static void GlyphEvictionDoubleGate()
    {
        // 正例：引用归零 ∧ 页龄够老 → P2 整页退休
        (NodeTable t, Invalidation inv, GlyphStore store) = StoreRig();
        NodeHandle leaf = t.CreateNode(NodeType.Text);
        t.AddChild(t.Root, leaf);
        var k = new GlyphRasterKey(0, 1, GlyphPageKind.Sdf, 32);
        GlyphLocator loc = store.EnsureResident(k, 20, 20);
        store.AddPageRef(loc.Page, leaf);
        store.ReleasePageRef(loc.Page, leaf);
        store.BeginFrame(100);                                    // 页龄 99 ≥ 60
        int pending = store.RequestEviction();
        t.Phase = FramePhase.P2_GlobalInvalidate;
        inv.ApplyPendingGlobal();
        t.Phase = FramePhase.P3_Commands;
        Check("淘汰双门正例（归零 + 够老 → P2 整页退休换代）",
            pending == 1 && store.IsPageRetired(loc.Page) && store.Generation == 2
            && store.TryGetLocator(k, out _) == ResidencyState.Missing);

        // 负例门 1：引用未归零的老页不淘汰
        (t, inv, store) = StoreRig();
        leaf = t.CreateNode(NodeType.Text);
        t.AddChild(t.Root, leaf);
        loc = store.EnsureResident(k, 20, 20);
        store.AddPageRef(loc.Page, leaf);
        store.BeginFrame(100);
        Check("淘汰负例：引用未归零（老页照留）", store.RequestEviction() == 0 && !store.IsPageRetired(loc.Page));

        // 负例门 2：引用归零但页龄不够
        (t, inv, store) = StoreRig();
        loc = store.EnsureResident(k, 20, 20);
        store.BeginFrame(30);                                     // 页龄 29 < 60
        Check("淘汰负例：页龄不够（引用归零也不动）", store.RequestEviction() == 0 && !store.IsPageRetired(loc.Page));

        // 淘汰是整页不是单字形：同页多字形一起 Missing，异页字形不受累
        (t, inv, store) = StoreRig();
        var ka = new GlyphRasterKey(0, 1, GlyphPageKind.Sdf, 32);
        var kb = new GlyphRasterKey(0, 2, GlyphPageKind.Sdf, 32);
        var kc = new GlyphRasterKey(0, 3, GlyphPageKind.Bitmap, 12);
        GlyphLocator la = store.EnsureResident(ka, 20, 20);
        GlyphLocator lb = store.EnsureResident(kb, 20, 20);
        GlyphLocator lc = store.EnsureResident(kc, 20, 20);
        store.BeginFrame(200);
        store.TryGetLocator(kc, out _);                           // 触碰位图页：页龄归零，门 2 拦下
        store.RequestEviction();
        t.Phase = FramePhase.P2_GlobalInvalidate;
        inv.ApplyPendingGlobal();
        t.Phase = FramePhase.P3_Commands;
        Check("淘汰整页（同页字形同退、异页不受累）",
            la.Page == lb.Page && lc.Page != la.Page
            && store.TryGetLocator(ka, out _) == ResidencyState.Missing
            && store.TryGetLocator(kb, out _) == ResidencyState.Missing
            && store.TryGetLocator(kc, out _) == ResidencyState.Resident);

        // 提交二次验门：申请后、P2 前被触碰的页原样留店（申请与生效隔半帧的缝由重验补上）
        (t, inv, store) = StoreRig();
        loc = store.EnsureResident(k, 20, 20);
        store.BeginFrame(100);
        pending = store.RequestEviction();
        store.BeginFrame(101);
        store.TryGetLocator(k, out _);                            // 申请后触碰：页龄归零
        t.Phase = FramePhase.P2_GlobalInvalidate;
        inv.ApplyPendingGlobal();
        t.Phase = FramePhase.P3_Commands;
        Check("提交二次验门（申请后被触碰 → 留店 + 收据）",
            pending == 1 && !store.IsPageRetired(loc.Page) && store.Generation == 1
            && store.Diag.GateRejectsAtCommit == 1);
    }

    private static void GlyphGenerationOnlyP2()
    {
        (NodeTable t, Invalidation inv, GlyphStore store) = StoreRig();
        var k = new GlyphRasterKey(0, 1, GlyphPageKind.Sdf, 32);
        GlyphLocator loc = store.EnsureResident(k, 20, 20);
        store.BeginFrame(100);
        int pending = store.RequestEviction();

        // 帧中申请：任何可见状态都不动——页活着、代不动、locator 照常（不变量：无半新半旧）
        bool midFrameIntact = pending == 1 && !store.IsPageRetired(loc.Page) && store.Generation == 1
            && inv.IsGlobalPending(GlobalSource.GlyphStore);
        Check("换代仅 P2：帧中申请不生效（页/代/挂起位三查）", midFrameIntact);

        // 非 P2 强放行：Invalidation 的相位门断言 + 真拒绝，店状态不动
        // （断言是 Conditional("DEBUG")，Release 按 DebugGates 惯例放行；真拒绝两配置都验）
        bool gateFired = AssertFires(() => inv.ApplyPendingGlobal());
        Check("换代仅 P2：非 P2 放行被门拦（断言 + 真拒绝）",
            (!DebugGates || gateFired) && !store.IsPageRetired(loc.Page) && store.Generation == 1);

        // P2 窗口：一次性退休 + 换代
        t.Phase = FramePhase.P2_GlobalInvalidate;
        int applied = inv.ApplyPendingGlobal();
        t.Phase = FramePhase.P3_Commands;
        Check("换代仅 P2：窗口内一次性生效", applied == 1 && store.IsPageRetired(loc.Page) && store.Generation == 2);

        // 淘汰后重入店 = 新条目走 residency（旧条目留账，索引换新）
        int placementsBefore = store.PlacementCount;
        GlyphLocator relocated = store.EnsureResident(k, 20, 20);
        Check("淘汰字形重走 residency（账追加、新代 locator）",
            store.PlacementCount == placementsBefore + 1 && relocated.Generation == 2
            && store.TryGetLocator(k, out _) == ResidencyState.Resident);
    }

    private static void GlyphRebuildFanOut()
    {
        // 核按钮：引用中的页也退；换代帧引用该页的文本叶按全局失效重排（定向 fan-out Mark）
        (NodeTable t, Invalidation inv, GlyphStore store) = StoreRig();
        NodeHandle leaf = t.CreateNode(NodeType.Text);
        t.AddChild(t.Root, leaf);
        NodeHandle bystander = t.CreateNode(NodeType.Text);
        t.AddChild(t.Root, bystander);

        var k = new GlyphRasterKey(0, 1, GlyphPageKind.Sdf, 32);
        GlyphLocator loc = store.EnsureResident(k, 20, 20);
        store.AddPageRef(loc.Page, leaf);
        store.RequestRebuild();
        Check("核按钮帧中挂起（引用页未动、叶未失效）",
            !store.IsPageRetired(loc.Page) && !inv.IsDirty(leaf, Ch.Content));

        long marksBefore = inv.Diagnostics.MarksOf(InvalidateReason.GlobalInvalidate);
        t.Phase = FramePhase.P2_GlobalInvalidate;
        inv.ApplyPendingGlobal();
        t.Phase = FramePhase.P3_Commands;
        Check("核按钮 P2：引用叶被全局失效重排（Content 脏 + GlobalInvalidate 归因，旁观叶不受累）",
            store.IsPageRetired(loc.Page) && store.Generation == 2
            && inv.IsDirty(leaf, Ch.Content) && !inv.IsDirty(bystander, Ch.Content)
            && inv.Diagnostics.MarksOf(InvalidateReason.GlobalInvalidate) == marksBefore + 1
            && store.Diag.FanOutMarks == 1 && store.PageRefCount(loc.Page) == 0);
    }

    // ── 双半区 ──────────────────────────────────────────────────────────────

    private static void GlyphHalfPlaneIndependence()
    {
        // 度量半区不碰页：页淘汰（核按钮全退）前后，度量位等不变、度量账长度不动。
        (NodeTable t, Invalidation inv, GlyphStore store) = StoreRig();
        var metrics = new GlyphMetricsTable();
        ushort faceId = metrics.RegisterFace(TtfFontFace.Load(SynthFontA(), "SynthA"));

        bool okBefore = metrics.TryGetGlyphMetrics(faceId, 1, out GlyphMetrics before);
        FaceMetrics faceBefore = metrics.MetricsOfFace(faceId);
        int entries = metrics.EntryCount;

        store.EnsureResident(new GlyphRasterKey(faceId, 1, GlyphPageKind.Sdf, 32), 20, 20);
        store.RequestRebuild();
        t.Phase = FramePhase.P2_GlobalInvalidate;
        inv.ApplyPendingGlobal();
        t.Phase = FramePhase.P3_Commands;

        bool okAfter = metrics.TryGetGlyphMetrics(faceId, 1, out GlyphMetrics after);
        FaceMetrics faceAfter = metrics.MetricsOfFace(faceId);
        Check("双半区独立：页淘汰后度量位等不变（advance/bbox/行高逐位）",
            okBefore && okAfter && metrics.EntryCount == entries && store.Generation == 2
            && BitEq(before.AdvanceEm, after.AdvanceEm)
            && BitEq(before.XMinEm, after.XMinEm) && BitEq(before.YMaxEm, after.YMaxEm)
            && BitEq(faceBefore.LineHeightEm, faceAfter.LineHeightEm));

        Check("度量半区值面（em 归一 + kerning M1 恒 0 + 无轮廓字形）",
            BitEq(before.AdvanceEm, 600f / 1000f) && BitEq(before.XMaxEm, 0.5f) && before.HasOutline
            && BitEq(faceBefore.LineHeightEm, 800f / 1000f - (-200f) / 1000f + 90f / 1000f)
            && metrics.KerningEm(faceId, 1, 2) == 0f
            && metrics.TryGetGlyphMetrics(faceId, 0, out GlyphMetrics notdef)
            && !notdef.HasOutline && BitEq(notdef.AdvanceEm, 0.5f));
    }

    private static void GlyphMetricsFaceIsolation()
    {
        // 度量账键含 faceId：两张 face 同 gid 的度量互不污染（advance 不同的孪生字体）
        var metrics = new GlyphMetricsTable();
        ushort fa = metrics.RegisterFace(TtfFontFace.Load(SynthFontA(600), "SynthA"));
        ushort fb = metrics.RegisterFace(TtfFontFace.Load(SynthFontA(700), "SynthA700"));
        bool okA = metrics.TryGetGlyphMetrics(fa, 1, out GlyphMetrics ma);
        bool okB = metrics.TryGetGlyphMetrics(fb, 1, out GlyphMetrics mb);
        Check("度量账按 face 分键（同 gid 异 face 不串账）",
            okA && okB && BitEq(ma.AdvanceEm, 0.6f) && BitEq(mb.AdvanceEm, 0.7f)
            && metrics.EntryCount == 2 && metrics.MapCodepoint(fb, 'A') == 1);
    }

    private static void GlyphStoreHookExclusive()
    {
        (NodeTable t, Invalidation inv, GlyphStore store) = StoreRig();
        var second = new GlyphStore();
        bool threw = false;
        try { second.AttachInvalidation(inv); }
        catch (InvalidOperationException) { threw = true; }
        Check("GlyphStoreFanOut 钩子硬独占（第二家店 throw）", threw);

        // 拆装对称：卸下后可再接（测试拆装路径本身）
        store.DetachInvalidation();
        second.AttachInvalidation(inv);
        Check("钩子拆装对称（Detach 后新店可接）", inv.GlyphStoreFanOut != null);

        // 尺寸违约有声：超页边长的 texel 申请 = 门失败（Fail 无条件，release 也响）
        bool oversize = AssertFires(() => second.EnsureResident(new GlyphRasterKey(0, 1, GlyphPageKind.Sdf, 32), 2000, 20));
        Check("入店尺寸违约有声", oversize);
        _ = t;
    }
}
