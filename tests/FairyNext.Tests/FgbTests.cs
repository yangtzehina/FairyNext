using FairyNext.Compiler.Fgb;
using FairyNext.Contracts;
using FairyNext.Core;
using FairyNext.Core.Fgb;
using FairyNext.Numerics;
using System.Buffers.Binary;

namespace FairyNext.Tests;

/// <summary>
/// M1-19 FGB blob 写读：容器门矩阵（magic/版本/flags/目录越界/对齐/selfHash）、Cast 直读、
/// 写读往返与确定性、NODE 列序对账（生成物 ⇄ Abi 表 ⇄ NodeTable 实际列，三方逐列）、
/// FNV 与 fork 对拍、fuzz 雏形（≥1000 变体，seed 固定；M1-22 转常设门）。
/// </summary>
public static partial class Program
{
    private static void FgbSuite()
    {
        FgbRejectMatrix();
        FgbSectionDirGates();
        FgbSelfHashGate();
        FgbAlignmentAndCast();
        FgbRoundtripAndDeterminism();
        FgbUnknownAndDuplicateFourcc();
        FgbHeaderShapeAndFourcc();
        FgbNodeColumnLedger();
        FgbNodeSectionRoundtrip();
        FgbFnvForkParity();
        FgbFuzz();
    }

    // ── 素材 ────────────────────────────────────────────────────────────────

    private const uint FourccTest = 0x54534554; // "TEST"——刻意不在 Abi.FgbSectionIds（未知段素材）

    /// <summary>基准 blob 的包 id 哈希（v2 头字段；装载门 5 的对照物在 M1-22 的用例里比）。</summary>
    private const ulong FgbBasePkgId = 0x0BADC0DE0BADC0DEUL;

    /// <summary>已知内容小树：root(1) → p(2) → c1(3), c2(4), c3(5)；c2 带全套 authored 区分值。</summary>
    private static NodeTable FgbBuildTree(out NodeHandle[] byIndex)
    {
        var t = new NodeTable();
        var p = t.CreateNode(NodeType.Component, 11, 9);
        t.AddChild(t.Root, p);
        var c1 = t.CreateNode(NodeType.Image, 21, 9);
        var c2 = t.CreateNode(NodeType.Text, 22, 9);
        var c3 = t.CreateNode(NodeType.Shape, 23, 9);
        t.AddChild(p, c1);
        t.AddChild(p, c2);
        t.AddChild(p, c3);
        t.SetPosition(c2, 3f, 4f);
        t.SetSize(c2, 5f, 6f);
        t.SetScale(c2, 7f, 8f);
        t.SetRotation(c2, 0.5f);
        t.SetSkew(c2, 0.25f);
        t.SetPivot(c2, 0.125f, 0.0625f);
        t.SetAlpha(c2, 0.5f);
        t.SetGrayed(c2, true);
        t.SetContentRef(c2, 42);
        t.SetStateRef(c2, 77);
        t.AllocResolvedSlot(c2);   // 池槽 0 是哨兵 ⇒ 首分配 = 1（非零，可与未分配区分）
        byIndex = new[] { t.Root, p, c1, c2, c3 };
        return t;
    }

    /// <summary>基准 blob：NODE + 未知奇数长度段（13B）+ DEPS 形 u64 记录段。</summary>
    private static byte[] FgbBaseBlob()
    {
        var t = FgbBuildTree(out _);
        var w = new FgbWriter();
        w.AddSection(AbiLayout.FgbSectionNode, FgbNodeSection.Write(t, 1, 5));
        w.AddSection(FourccTest, FgbOddPayload());
        w.AddSection(AbiLayout.FgbSectionDeps, FgbU64Payload());
        return w.Finish(FgbBasePkgId, 0x1122334455667788UL, 0x99AABBCCDDEEFF00UL, 2, 3);
    }

    private static byte[] FgbOddPayload()
    {
        var odd = new byte[13];
        for (int i = 0; i < odd.Length; i++) odd[i] = (byte)(i * 7 + 1);
        return odd;
    }

    private static readonly ulong[] FgbU64Records = { 0xDEADBEEFCAFEF00DUL, 1UL, ulong.MaxValue, 0x0102030405060708UL };

    private static byte[] FgbU64Payload()
    {
        var bytes = new byte[FgbU64Records.Length * 8];
        for (int i = 0; i < FgbU64Records.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), FgbU64Records[i]);
        return bytes;
    }

    private static byte[] FgbPatch(byte[] src, int offset, params byte[] bytes)
    {
        var copy = (byte[])src.Clone();
        bytes.AsSpan().CopyTo(copy.AsSpan(offset));
        return copy;
    }

    private static byte[] FgbPatchU32(byte[] src, int offset, uint v)
    {
        var copy = (byte[])src.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offset), v);
        return copy;
    }

    private static byte[] FgbPatchU64(byte[] src, int offset, ulong v)
    {
        var copy = (byte[])src.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(offset), v);
        return copy;
    }

    private static FgbGate FgbGateOf(byte[] blob, bool verifySelfHash = true)
    {
        bool ok = FgbBlobView.TryOpen(blob, out _, out var report, verifySelfHash);
        return ok ? FgbGate.None : report.RejectedBy;
    }

    // ── 门矩阵 ──────────────────────────────────────────────────────────────

    private static void FgbRejectMatrix()
    {
        byte[] baseBlob = FgbBaseBlob();
        Check("FGB 门: 基准 blob 开门（selfHash 全验）", FgbGateOf(baseBlob) == FgbGate.None);

        Check("FGB 门: magic 不符拒载",
            FgbGateOf(FgbPatchU32(baseBlob, AbiLayout.FgbHeaderMagicOffset, Abi.FgbMagic + 1)) == FgbGate.Magic);

        // 版本矩阵：高一版、低一版都拒——精确匹配，不降级（装载门 1）。
        Check("FGB 门: formatVersion+1 拒载",
            FgbGateOf(FgbPatchU32(baseBlob, AbiLayout.FgbHeaderFormatVersionOffset, Abi.FgbFormatVersion + 1)) == FgbGate.FormatVersion);
        Check("FGB 门: formatVersion-1 拒载",
            FgbGateOf(FgbPatchU32(baseBlob, AbiLayout.FgbHeaderFormatVersionOffset, Abi.FgbFormatVersion - 1)) == FgbGate.FormatVersion);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(baseBlob.AsSpan(AbiLayout.FgbHeaderFlagsOffset));
        Check("FGB 门: LE 断言位缺失拒载",
            FgbGateOf(FgbPatchU32(baseBlob, AbiLayout.FgbHeaderFlagsOffset, flags & ~1u)) == FgbGate.HeaderFlags);
        Check("FGB 门: 压缩位置位拒载",
            FgbGateOf(FgbPatchU32(baseBlob, AbiLayout.FgbHeaderFlagsOffset, flags | 2u)) == FgbGate.HeaderFlags);
        Check("FGB 门: 保留位非零拒载",
            FgbGateOf(FgbPatchU32(baseBlob, AbiLayout.FgbHeaderFlagsOffset, flags | (1u << 17))) == FgbGate.HeaderFlags);

        Check("FGB 门: 空输入/短头拒载",
            FgbGateOf(Array.Empty<byte>()) == FgbGate.Truncated
            && FgbGateOf(baseBlob.AsSpan(0, 10).ToArray()) == FgbGate.Truncated
            && FgbGateOf(baseBlob.AsSpan(0, Abi.FgbHeaderSize).ToArray()) == FgbGate.Truncated); // 头够、目录不够
    }

    private static void FgbSectionDirGates()
    {
        byte[] baseBlob = FgbBaseBlob();
        int e0 = Abi.FgbHeaderSize;   // 目录条目 0（NODE）
        ulong off0 = BinaryPrimitives.ReadUInt64LittleEndian(baseBlob.AsSpan(e0 + AbiLayout.FgbDirOffsetOffset));

        Check("FGB 门: 段 length 越 blob 界拒载",
            FgbGateOf(FgbPatchU64(baseBlob, e0 + AbiLayout.FgbDirLengthOffset, (ulong)baseBlob.Length)) == FgbGate.SectionBounds);

        // u64 回绕构造：offset+length 溢出后落回小值——「先界后加」的比较必须拦住它。
        var wrap = FgbPatchU64(baseBlob, e0 + AbiLayout.FgbDirOffsetOffset, ulong.MaxValue - 8);
        wrap = FgbPatchU64(wrap, e0 + AbiLayout.FgbDirLengthOffset, 32);
        Check("FGB 门: offset+length u64 回绕拒载", FgbGateOf(wrap) == FgbGate.SectionBounds);

        Check("FGB 门: 段与头/目录重叠拒载（offset=0 对齐但指进头部）",
            FgbGateOf(FgbPatchU64(baseBlob, e0 + AbiLayout.FgbDirOffsetOffset, 0)) == FgbGate.SectionBounds);

        Check("FGB 门: 段 offset 非 16B 对齐拒载",
            FgbGateOf(FgbPatchU64(baseBlob, e0 + AbiLayout.FgbDirOffsetOffset, off0 + 4)) == FgbGate.SectionMisaligned);

        Check("FGB 门: 段数越水位拒载（先门后分配，不 OOM）",
            FgbGateOf(FgbPatchU32(baseBlob, AbiLayout.FgbHeaderSectionCountOffset, 0x7FFFFFFFu)) == FgbGate.SectionCount);
        Check("FGB 门: 段数 ≤ 水位但目录越 blob 界拒载",
            FgbGateOf(FgbPatchU32(baseBlob, AbiLayout.FgbHeaderSectionCountOffset, 1000)) == FgbGate.Truncated);
    }

    private static void FgbSelfHashGate()
    {
        byte[] baseBlob = FgbBaseBlob();
        // 翻 payload 末字节：结构门全过、内容变了——只有 selfHash 能拦。
        var tampered = FgbPatch(baseBlob, baseBlob.Length - 1,
            (byte)(baseBlob[baseBlob.Length - 1] ^ 0x40));
        Check("FGB 门: payload 翻 1 字节 → selfHash 拒载", FgbGateOf(tampered) == FgbGate.SelfHash);
        Check("FGB 门: selfHash 可信任跳过（发布包豁免口）",
            FgbGateOf(tampered, verifySelfHash: false) == FgbGate.None);
        // 存储值与三段续链重算一致（零段续链 == 真散列一个补零副本）。
        var zeroed = FgbPatchU64(baseBlob, AbiLayout.FgbHeaderSelfHashOffset, 0);
        Check("FGB: selfHash = 字段按零参与的整 blob FNV-1a",
            BinaryPrimitives.ReadUInt64LittleEndian(baseBlob.AsSpan(AbiLayout.FgbHeaderSelfHashOffset))
                == FnvHash.Hash64(zeroed)
            && FgbBlobView.ComputeSelfHash(baseBlob) == FnvHash.Hash64(zeroed));
    }

    // ── 对齐 / Cast / 往返 ──────────────────────────────────────────────────

    private static void FgbAlignmentAndCast()
    {
        byte[] blob = FgbBaseBlob();
        Check("FGB: 开门", FgbBlobView.TryOpen(blob, out var v, out var report) && report.SectionCount == 3);

        // 奇数长度段（13B）原样往返：长度不被 pad 污染，pad 只属于容器缝隙。
        Check("FGB: 奇数长度段 payload 原样（13B 不带 pad）",
            v.TryGetSection(FourccTest, out var odd) && odd.SequenceEqual(FgbOddPayload()));

        // 奇数段之后的段仍 16B 对齐可 Cast 直读：u64 记录逐元素等。
        bool cast = v.TryRecords<ulong>(AbiLayout.FgbSectionDeps, out var recs, out var gate)
            && gate == FgbGate.None && recs.Length == FgbU64Records.Length;
        if (cast)
            for (int i = 0; i < recs.Length; i++)
                cast &= recs[i] == FgbU64Records[i];
        Check("FGB: 奇数段后的段 16B 对齐 + Cast 直读逐元素等", cast);

        // 目录里所有 offset 真的对齐（直接读目录字节，不经视图）。
        bool aligned = true;
        for (int i = 0; i < 3; i++)
        {
            ulong off = BinaryPrimitives.ReadUInt64LittleEndian(
                blob.AsSpan(Abi.FgbHeaderSize + i * Abi.FgbSectionDirEntrySize + AbiLayout.FgbDirOffsetOffset));
            aligned &= off % (ulong)Abi.FgbSectionAlignment == 0;
        }
        Check("FGB: 全段 offset 皆 FgbSectionAlignment 对齐", aligned);

        Check("FGB: 记录宽不整除 → RecordStride 有声拒绝（Cast 不静默截断）",
            !v.TryRecords<ulong>(FourccTest, out _, out var g1) && g1 == FgbGate.RecordStride);
        Check("FGB: 缺段 → false + None（缺段不是错误，语义归取用方）",
            !v.TryRecords<ulong>(AbiLayout.FgbSectionQuad, out _, out var g2) && g2 == FgbGate.None);
    }

    private static void FgbRoundtripAndDeterminism()
    {
        byte[] blob = FgbBaseBlob();
        FgbBlobView.TryOpen(blob, out var v, out _);

        Check("FGB 往返: 头身份字段逐个等",
            v.FormatVersion == Abi.FgbFormatVersion
            && v.SourceHash == 0x1122334455667788UL
            && v.CombinedRefHash == 0x99AABBCCDDEEFF00UL
            && v.ScaleLevel == 2 && v.BranchId == 3 && v.SectionCount == 3);

        var t = FgbBuildTree(out _);
        Check("FGB 往返: 全段 payload 逐字节等",
            v.TryGetSection(AbiLayout.FgbSectionNode, out var node)
            && node.SequenceEqual(FgbNodeSection.Write(t, 1, 5))
            && v.TryGetSection(FourccTest, out var odd) && odd.SequenceEqual(FgbOddPayload())
            && v.TryGetSection(AbiLayout.FgbSectionDeps, out var deps) && deps.SequenceEqual(FgbU64Payload()));

        // 确定性：同输入两次全程重建（树也重建）→ 逐字节等。canonical 去重（M1-20）以此为地基。
        Check("FGB 确定性: 同输入两次写出逐字节等", FgbBaseBlob().AsSpan().SequenceEqual(FgbBaseBlob()));
    }

    private static void FgbUnknownAndDuplicateFourcc()
    {
        var w = new FgbWriter();
        w.AddSection(FourccTest, new byte[] { 1, 2, 3 });
        w.AddSection(AbiLayout.FgbSectionLang, new byte[] { 10 });
        w.AddSection(AbiLayout.FgbSectionLang, new byte[] { 20, 21 });   // LANG 每语言一段：同 fourcc 多段合法
        byte[] blob = w.Finish(0, 0, 0, 0, 0);

        bool ok = FgbBlobView.TryOpen(blob, out var v, out _);
        Check("FGB: 未知 fourcc 段不碍开门且整段可达（前向兼容 = 段粒度跳过）",
            ok && v.TryGetSection(FourccTest, out var p) && p.Length == 3);
        Check("FGB: 同 fourcc 多段——TryGetSection 取目录序首个，逐段消费走 SectionAt",
            ok && v.TryGetSection(AbiLayout.FgbSectionLang, out var first) && first.Length == 1 && first[0] == 10
            && v.FourccAt(2) == AbiLayout.FgbSectionLang && v.SectionAt(2).Length == 2);
        Check("FGB: 零段 blob 合法（空包也得能开）",
            FgbBlobView.TryOpen(new FgbWriter().Finish(0, 0, 0, 0, 0), out _, out var r0) && r0.SectionCount == 0);
    }

    // ── 记录布局自洽 + fourcc 词表 ──────────────────────────────────────────

    private static void FgbHeaderShapeAndFourcc()
    {
        Check("FGB ABI: 头 64B/目录条目 24B/16B 对齐自洽",
            Abi.FgbHeaderSize == 64 && Abi.FgbHeaderSize % Abi.FgbSectionAlignment == 0
            && Abi.FgbSectionDirEntrySize == 24
            && FieldsContiguous(Abi.FgbHeaderFields, Abi.FgbHeaderSize)
            && FieldsContiguous(Abi.FgbSectionDirFields, Abi.FgbSectionDirEntrySize)
            && BitsCoverWord(Abi.FgbFlagBits));

        // fourcc 值 == 名字四字符大写的 LE 字节——手算十六进制的笔误在此现形。
        bool fourccs = true;
        var seen = new HashSet<uint>();
        foreach (var f in Abi.FgbSectionIds)
        {
            string chars = f.Name.ToUpperInvariant();
            uint expect = (uint)(chars[0] | (chars[1] << 8) | (chars[2] << 16) | (chars[3] << 24));
            fourccs &= chars.Length == 4 && f.Value == expect && seen.Add(f.Value);
        }
        Check("FGB ABI: 段 fourcc = 名字字节（LE）且全表无重复", fourccs && seen.Count == Abi.FgbSectionIds.Length);
    }

    // ── NODE 列序对账（生成物 ⇄ Abi 表 ⇄ NodeTable 实际列，三方逐列）────────

    private static void FgbNodeColumnLedger()
    {
        // 表自洽：列宽求和收口 80B/节点、元素宽与机器类型一致、生成物列数一致。
        int sum = 0;
        bool kinds = true;
        foreach (var c in Abi.NodeColumns)
        {
            sum += c.ElementSize;
            kinds &= c.ElementSize == c.Kind switch
            {
                AbiFieldKind.UInt32 => 4,
                AbiFieldKind.Float32 => 4,
                AbiFieldKind.UInt16 => 2,
                _ => -1,
            };
        }
        Check("FGB NODE: 列宽求和 = NodeBytesPerNode = 80 且机器类型对宽",
            sum == Abi.NodeBytesPerNode && Abi.NodeBytesPerNode == 80 && kinds
            && Abi.NodeColumns.Length == AbiLayout.NodeColumnCount);

        var t = FgbBuildTree(out var byIndex);
        // 前置：本审计以「slab 顺序分配 ⇒ root=1 起连续五槽」为坐标系，先钉住它。
        bool seq = true;
        for (int k = 0; k < byIndex.Length; k++)
            seq &= t.TryResolve(byIndex[k], out uint idx) && idx == (uint)(1 + k);
        Check("FGB NODE 对账前置: 槽序 root=1 起连续", seq);

        uint IdxOf(NodeHandle h) => t.TryResolve(h, out uint i) ? i : 0u;
        var root = byIndex[0]; var p = byIndex[1];
        var c1 = byIndex[2]; var c2 = byIndex[3]; var c3 = byIndex[4];

        // 每列的期望值（下标序 root,p,c1,c2,c3）。拓扑列是环形链的**原始真值**：
        // 独子 next/prev 指自身、末子 next 回卷到首子——NODE 段冻结的就是这套编码，
        // 环形表示变了 = 格式变了 = 本测试就该红（然后 bump FgbFormatVersion）。
        object[] ExpectFor(int col) => col switch
        {
            AbiLayout.NodeColParent => new object[] { 0u, IdxOf(root), IdxOf(p), IdxOf(p), IdxOf(p) },
            AbiLayout.NodeColFirstChild => new object[] { IdxOf(p), IdxOf(c1), 0u, 0u, 0u },
            AbiLayout.NodeColNextSib => new object[] { 0u, IdxOf(p), IdxOf(c2), IdxOf(c3), IdxOf(c1) },
            AbiLayout.NodeColPrevSib => new object[] { 0u, IdxOf(p), IdxOf(c3), IdxOf(c1), IdxOf(c2) },
            AbiLayout.NodeColOwnerInst => new object[] { 0u, 9u, 9u, 9u, 9u },
            AbiLayout.NodeColLocalId => new object[] { (ushort)0, (ushort)11, (ushort)21, (ushort)22, (ushort)23 },
            AbiLayout.NodeColTypeId => new object[] { NodeType.Component, NodeType.Component, NodeType.Image, NodeType.Text, NodeType.Shape },
            AbiLayout.NodeColPosX => new object[] { 0f, 0f, 0f, t.GetPosition(c2).x, 0f },
            AbiLayout.NodeColPosY => new object[] { 0f, 0f, 0f, t.GetPosition(c2).y, 0f },
            AbiLayout.NodeColWidth => new object[] { 0f, 0f, 0f, t.GetSize(c2).x, 0f },
            AbiLayout.NodeColHeight => new object[] { 0f, 0f, 0f, t.GetSize(c2).y, 0f },
            AbiLayout.NodeColScaleX => new object[] { 1f, 1f, 1f, t.GetScale(c2).x, 1f },
            AbiLayout.NodeColScaleY => new object[] { 1f, 1f, 1f, t.GetScale(c2).y, 1f },
            AbiLayout.NodeColRotation => new object[] { 0f, 0f, 0f, 0.5f, 0f },
            AbiLayout.NodeColSkew => new object[] { 0f, 0f, 0f, 0.25f, 0f },
            AbiLayout.NodeColPivotX => new object[] { 0f, 0f, 0f, 0.125f, 0f },
            AbiLayout.NodeColPivotY => new object[] { 0f, 0f, 0f, 0.0625f, 0f },
            AbiLayout.NodeColLocalVisual => new object[] { Visual.DefaultLocal, Visual.DefaultLocal, Visual.DefaultLocal, t.LocalVisual(c2), Visual.DefaultLocal },
            AbiLayout.NodeColContentRef => new object[] { 0u, 0u, 0u, 42u, 0u },
            AbiLayout.NodeColStateRef => new object[] { 0u, 0u, 0u, 77u, 0u },
            AbiLayout.NodeColResolvedRef => new object[] { 0u, 0u, 0u, t.ResolvedRef(c2), 0u },
            _ => throw new InvalidOperationException($"对账缺列 {col}：Abi.NodeColumns 长了，审计没跟上"),
        };

        // c2 的区分值确实非零/非默认（否则对账退化成「零 == 零」）。
        Check("FGB NODE 对账前置: c2 区分值非默认（localVisual 变、resolvedRef=1）",
            t.LocalVisual(c2) != Visual.DefaultLocal && t.ResolvedRef(c2) == 1u
            && t.GetPosition(c2).x == 3f && t.GetScale(c2).y == 8f);

        for (int col = 0; col < Abi.NodeColumns.Length; col++)
        {
            int elem = Abi.NodeColumns[col].ElementSize;
            var actual = new byte[elem * 5];
            t.ExportColumn(col, 1, 5, actual);

            var expected = new byte[elem * 5];
            object[] vals = ExpectFor(col);
            for (int k = 0; k < 5; k++)
            {
                var cell = expected.AsSpan(k * elem);
                switch (vals[k])
                {
                    case uint u: BinaryPrimitives.WriteUInt32LittleEndian(cell, u); break;
                    case ushort us: BinaryPrimitives.WriteUInt16LittleEndian(cell, us); break;
                    case float f: BinaryPrimitives.WriteUInt32LittleEndian(cell, BitConverter.SingleToUInt32Bits(f)); break;
                    default: throw new InvalidOperationException("对账期望值类型未覆盖");
                }
            }
            Check($"FGB NODE 列对账[{col}] {Abi.NodeColumns[col].Name}", actual.AsSpan().SequenceEqual(expected));
        }
    }

    private static void FgbNodeSectionRoundtrip()
    {
        var t = FgbBuildTree(out _);
        byte[] payload = FgbNodeSection.Write(t, 1, 5);

        bool ok = FgbNodeSection.TryView(payload, out var view) && view.NodeCount == 5;
        if (ok)
        {
            for (int col = 0; col < Abi.NodeColumns.Length; col++)
            {
                var direct = new byte[Abi.NodeColumns[col].ElementSize * 5];
                t.ExportColumn(col, 1, 5, direct);
                ok &= view.Column(col).SequenceEqual(direct);
            }
        }
        Check("FGB NODE 段写读: 逐列 == 直接导出（写读同一份列序）", ok);

        var extended = new byte[payload.Length + 1];
        payload.CopyTo(extended, 0);
        Check("FGB NODE 段: 形状精确匹配（±1 字节、count 造假、count 越水位全拒）",
            !FgbNodeSection.TryView(payload.AsSpan(0, payload.Length - 1), out _)
            && !FgbNodeSection.TryView(extended, out _)
            && !FgbNodeSection.TryView(FgbPatchU32(payload, 0, 6), out _)
            && !FgbNodeSection.TryView(FgbPatchU32(payload, 0, 0x7FFFFFFFu), out _)
            && !FgbNodeSection.TryView(ReadOnlySpan<byte>.Empty, out _));

        // 空区间合法：零节点模板的 NODE 段就是 16B 头。
        byte[] empty = FgbNodeSection.Write(t, 1, 0);
        Check("FGB NODE 段: 零节点 payload = 16B 头且可开",
            empty.Length == FgbNodeSection.HeaderBytes && FgbNodeSection.TryView(empty, out var ev) && ev.NodeCount == 0);

        // 列起点 16B 对齐是段内布局承诺（M1-22 的 memcpy 目标）——写读同函数护不住它，单独钉。
        bool colAligned = FgbNodeSection.ColumnOffset(0, 5) == FgbNodeSection.HeaderBytes;
        for (int c = 0; c < Abi.NodeColumns.Length; c++)
            colAligned &= FgbNodeSection.ColumnOffset(c, 5) % Abi.FgbSectionAlignment == 0;
        Check("FGB NODE 段: 首列 @16 且全列起点 16B 对齐", colAligned);
    }

    // ── FNV 对拍 + fuzz ─────────────────────────────────────────────────────

    /// <summary>fork FqsBlob.Hash 的逐行复刻（Fqs.cs 328-337 @ 08a2d56）——对拍神谕，别处不用。</summary>
    private static ulong FgbForkHashReplica(byte[]? bytes, int offset, int count)
    {
        ulong h = 0xcbf29ce484222325UL;
        if (bytes == null)
            return h;
        int end = offset + count;
        for (int i = offset; i < end; i++)
            h = (h ^ bytes[i]) * 0x100000001b3UL;
        return h;
    }

    private static void FgbFnvForkParity()
    {
        var rng = new Random(0x5EED);
        var buf = new byte[1024];
        rng.NextBytes(buf);

        bool parity = FnvHash.Hash64((byte[]?)null) == FgbForkHashReplica(null, 0, 0)
            && FnvHash.Hash64(Array.Empty<byte>()) == 0xcbf29ce484222325UL    // fork 已知值：空输入 = offset basis
            && FnvHash.Hash64(new byte[] { (byte)'a' }) == 0xaf63dc4c8601ec8cUL  // FNV-1a 64 标准向量
            && FnvHash.Hash64(buf) == FgbForkHashReplica(buf, 0, buf.Length)
            && FnvHash.Hash64(buf, 100, 700) == FgbForkHashReplica(buf, 100, 700);
        Check("FGB FNV: 与 fork Hash 同值（null/空/标准向量/随机/区间）", parity);

        // 续链与零段：任意切分 == 一次散列；ContinueZeros == 散列真实零字节。
        ulong whole = FnvHash.Hash64(buf);
        ulong split = FnvHash.Hash64Continue(FnvHash.OffsetBasis64, buf.AsSpan(0, 313));
        split = FnvHash.Hash64Continue(split, buf.AsSpan(313));
        var withZeros = new byte[64];
        rng.NextBytes(withZeros);
        Array.Clear(withZeros, 20, 8);
        ulong chain = FnvHash.Hash64Continue(FnvHash.OffsetBasis64, withZeros.AsSpan(0, 20));
        chain = FnvHash.Hash64ContinueZeros(chain, 8);
        chain = FnvHash.Hash64Continue(chain, withZeros.AsSpan(28));
        Check("FGB FNV: 续链切分不变式 + 零段续链 == 真零字节", split == whole && chain == FnvHash.Hash64(withZeros));
    }

    private static void FgbFuzz()
    {
        byte[] baseBlob = FgbBaseBlob();
        var rng = new Random(0xF6B1);   // seed 固定：红了可复现
        const int Rounds = 1200;

        int accepted = 0, rejected = 0, violations = 0;
        string? firstViolation = null;

        for (int i = 0; i < Rounds; i++)
        {
            byte[] mut;
            switch (i % 4)
            {
                case 0:   // 随机截断
                    mut = baseBlob.AsSpan(0, rng.Next(0, baseBlob.Length + 1)).ToArray();
                    break;
                case 1:   // 随机翻位
                    mut = (byte[])baseBlob.Clone();
                    mut[rng.Next(mut.Length)] ^= (byte)(1 << rng.Next(8));
                    break;
                case 2:   // 伪造段目录（头+目录区内撒 4 随机字节）
                    mut = (byte[])baseBlob.Clone();
                    int dirZone = Abi.FgbHeaderSize + 3 * Abi.FgbSectionDirEntrySize;
                    int pos = rng.Next(dirZone - 4);
                    for (int b = 0; b < 4; b++) mut[pos + b] = (byte)rng.Next(256);
                    break;
                default:  // 尾随垃圾 / 任意 8 字节涂改
                    if (rng.Next(2) == 0)
                    {
                        int extra = 1 + rng.Next(32);
                        mut = new byte[baseBlob.Length + extra];
                        baseBlob.CopyTo(mut, 0);
                        for (int b = 0; b < extra; b++) mut[baseBlob.Length + b] = (byte)rng.Next(256);
                    }
                    else
                    {
                        mut = (byte[])baseBlob.Clone();
                        int at = rng.Next(mut.Length - 8);
                        for (int b = 0; b < 8; b++) mut[at + b] = (byte)rng.Next(256);
                    }
                    break;
            }

            try
            {
                // 半数跳 selfHash：让结构门独立扛住变体，而不是躲在整体哈希后面。
                if (!FgbBlobView.TryOpen(mut, out var v, out var rep, verifySelfHash: i % 2 == 0))
                {
                    rejected++;
                    if (rep.RejectedBy == FgbGate.None) { violations++; firstViolation ??= $"[{i}] 拒载但无门号"; }
                    continue;
                }
                accepted++;
                // 「正常载入」必须名副其实：全段可走读，NODE 可开则全列可触。
                ulong sink = 0;
                for (int s = 0; s < v.SectionCount; s++)
                    sink ^= FnvHash.Hash64(v.SectionAt(s));
                if (v.TryGetSection(AbiLayout.FgbSectionNode, out var nodePayload)
                    && FgbNodeSection.TryView(nodePayload, out var nv))
                {
                    for (int c = 0; c < Abi.NodeColumns.Length; c++)
                        sink ^= FnvHash.Hash64(nv.Column(c));
                }
                if (sink == 0xFFFFFFFFFFFFFFFFUL) Console.WriteLine("(不可能：防死码消除)");
            }
            catch (Exception ex)
            {
                violations++;
                firstViolation ??= $"[{i}] {ex.GetType().Name}: {ex.Message}";
            }
        }

        Check($"FGB fuzz: {Rounds} 变体零违例（要么拒载要么全段可读，绝不越界/半载/panic）", violations == 0);
        if (firstViolation != null) Console.WriteLine($"     首违例 {firstViolation}");
        Check("FGB fuzz: 语料两侧都有覆盖（有拒有收，fuzz 不是空转）", accepted > 0 && rejected > 0);
    }
}
