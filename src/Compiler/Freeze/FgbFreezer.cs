using System.Collections.Generic;
using System.Runtime.InteropServices;
using FairyNext.Compiler.Fgb;
using FairyNext.Compiler.Fui;
using FairyNext.Compiler.Shape;
using FairyNext.Contracts;
using FairyNext.Core;
using FairyNext.Core.Fgb;
using FairyNext.Core.Layout;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Compiler.Freeze;

/// <summary>
/// 冻结器（M1-20b）：已定形的树 → 编译期 Extract → canonical 去重 → FGB 段冻结 → 内存计划。
///
/// 「编译器 = 无头运行时」（承诺 8）在本半的落点是一句话：**这里没有第二套 Extract**。
/// 冻结用的是运行期同一个 <see cref="Extract"/> 实例类型，喂它的是运行期同一个
/// <see cref="ContentTable"/>（<c>IExtractSource</c> 的运行期实现），只是不过管线——
/// 离线路径自己把 <see cref="Extract.PruneAfterRebuild"/> 打开，补上管线 <c>DrainTail</c>
/// 那道尾剪枝。等价性金样比的就是两条路径产物的 <c>CanonicalStream</c> 字节。
///
/// 规范段序（M1-19 留缝 ①「容器不排序，段序 = AddSection 调用序，规范段序归编译器」的兑现）：
/// <code>
///   STRT → TREF → COMP → NODE → CONT → LOCL → CNST → QUAD → SEGS → LEAF → CLIP
///   身份与索引先行（谁是谁）→ 模板本体（长什么样）→ 求值图（怎么算）→ 渲染冻结（画什么）
/// </code>
/// 顺序本身进 blob 字节，故它是 golden 的一部分：改段序 = 改产物 = code review 里看得见。
/// </summary>
internal sealed class FgbFreezer
{
    private readonly ShapedPackage _shaped;
    private readonly CompileDiagnostics _diag;

    private readonly StringPool _strings = new StringPool();
    private readonly CanonicalTable _content = new CanonicalTable(AbiLayout.FgbContSize);
    private readonly CanonicalTable _texRefs = new CanonicalTable(AbiLayout.FgbTexRefSize);

    private readonly List<Frozen> _comps = new List<Frozen>();
    private readonly List<ConstraintOp> _ops = new List<ConstraintOp>();
    private readonly List<FanOut> _fans = new List<FanOut>();
    private readonly List<ushort> _fanIdx = new List<ushort>();
    private readonly List<byte> _masks = new List<byte>();
    private readonly List<QuadInstance> _quads = new List<QuadInstance>();
    private readonly List<ClipEntry> _clips = new List<ClipEntry>();
    private readonly List<byte> _segs = new List<byte>();
    private readonly List<byte> _leaves = new List<byte>();
    private readonly List<byte> _locals = new List<byte>();

    private int _totalNodes;

    private FgbFreezer(ShapedPackage shaped)
    {
        _shaped = shaped;
        _diag = shaped.Diagnostics;
    }

    /// <summary>一个组件在包级各段里的落位。</summary>
    internal sealed class Frozen
    {
        public ShapedComponent Sc = null!;
        public RenderStream Stream = null!;
        public ExtractReport Report;
        public Dictionary<ulong, ushort> LocalOfHandle = null!;
        public uint[] ContentMap = Array.Empty<uint>();
        public int NodeStart, NodeCount;
        public int QuadStart, QuadCount;
        public int SegStart, SegCount;
        public int LeafStart, LeafCount;
        public int ClipStart, ClipCount;
        public int OpStart, OpCount;
        public int FanStart, IdxStart, IdxCount;
        public int LocalStart, LocalCount;
        public int ResolvedSlots;
        public uint InstanceBytes;
        public uint NameStr, NameHash;
        public ushort Flags;
    }

    /// <summary>冻结一个已定形的包。Error 级自检失败抛 <see cref="FgmCompileException"/>。</summary>
    public static CompileResult Run(ShapedPackage shaped)
    {
        if (shaped == null) throw new ArgumentNullException(nameof(shaped));
        if (shaped.Package == null)
            throw new FgmCompileException(shaped.Diagnostics);
        var f = new FgbFreezer(shaped);
        return f.Freeze();
    }

    private CompileResult Freeze()
    {
        FuiPackage pkg = _shaped.Package!;

        // CONT 下标 0 = 「无内容」哨兵，与 ContentTable 的 0 号同义（contentRef 0 直接落它）。
        _content.Add(new byte[AbiLayout.FgbContSize]);

        // TREF：图集条目 id → 段键纹理 id。分配序与 ShapeContext.AtlasTex 同源
        // （M1-20a 留缝 ②）——QUAD 段键里的 texId 和这张表指的是同一批纹理。
        ulong selfPkgId = FnvHash.Hash64(pkg.Id ?? string.Empty);
        foreach (KeyValuePair<string, TexId> kv in _shaped.Textures)
        {
            Span<byte> rec = stackalloc byte[AbiLayout.FgbTexRefSize];
            FgbRecordIo.U64(rec, AbiLayout.FgbTexRefPkgIdOffset, selfPkgId);
            FgbRecordIo.U32(rec, AbiLayout.FgbTexRefItemStrOffset, _strings.Add(kv.Key));
            FgbRecordIo.U16(rec, AbiLayout.FgbTexRefKindOffset, 0);
            FgbRecordIo.U16(rec, AbiLayout.FgbTexRefTexIdOffset, (ushort)kv.Value.Value);
            _texRefs.Add(rec);
        }

        if (pkg.Dependencies.Length > 0)
            _diag.Add(FgmCodes.CrossPackageDeferred, FgmSeverity.Info, "",
                "包有 " + pkg.Dependencies.Length + " 条跨包依赖：combinedRefHash 需要链上各包的 sourceHash，"
                + "单包编译面给不出——头内该字段恒零、DEPS 段不写，装载期身份门归 M1-22 的多包编译面");

        for (int i = 0; i < _shaped.Components.Count; i++)
            FreezeComponent(_shaped.Components[i]);

        if (_diag.HasErrors) throw new FgmCompileException(_diag);

        // 不变量 8 的编译后置扫描（按构造不可能红；不变量是关于**产物**的断言，必须自带一次检查）。
        if (!_content.VerifyDistinct(out string contErr))
            _diag.Add(FgmCodes.FreezeSelfCheck, FgmSeverity.Error, "", "CONT " + contErr);
        if (!_texRefs.VerifyDistinct(out string texErr))
            _diag.Add(FgmCodes.FreezeSelfCheck, FgmSeverity.Error, "", "TREF " + texErr);
        if (_diag.HasErrors) throw new FgmCompileException(_diag);

        byte[] node = BuildNodeSection();
        byte[] blob = Assemble(pkg, node);
        string plan = MemoryPlan.Build(this, blob.Length);
        string graph = MemoryPlan.BuildReactiveGraph(this);

        var frozen = new List<FrozenComponent>(_comps.Count);
        for (int i = 0; i < _comps.Count; i++)
        {
            Frozen c = _comps[i];
            frozen.Add(new FrozenComponent(c.Sc.Item.Id, c.Sc.Item.Name ?? c.Sc.Item.Id,
                c.Stream.Snapshot(), c.Report, c.NodeStart, c.NodeCount, c.QuadStart, c.QuadCount,
                c.LeafStart, c.LeafCount, c.OpStart, c.OpCount, c.InstanceBytes));
        }
        return new CompileResult(blob, plan, graph, _diag, frozen);
    }

    // ── 逐组件 ──────────────────────────────────────────────────────────────

    private void FreezeComponent(ShapedComponent sc)
    {
        var c = new Frozen { Sc = sc };
        int n = sc.Locals.Count;

        // 自检：建树期节点是**顺序分配的连续槽**（根在槽 1，localId i 在槽 i+1），
        // NODE 段才能用一次列区间导出。不成立即编译器 bug，不是资产问题。
        uint first = sc.Locals.HandleOf(0).Index;
        for (ushort l = 0; l < n; l++)
        {
            if (sc.Locals.HandleOf(l).Index == first + l) continue;
            _diag.Add(FgmCodes.FreezeSelfCheck, FgmSeverity.Error, sc.Item.Name ?? sc.Item.Id,
                "节点槽区间不连续（localId " + l + " 在槽 " + sc.Locals.HandleOf(l).Index
                + "，期望 " + (first + l) + "）——NODE 段的列区间导出不成立");
            return;
        }

        c.LocalOfHandle = new Dictionary<ulong, ushort>(n);
        for (ushort l = 0; l < n; l++) c.LocalOfHandle[sc.Locals.HandleOf(l).Pack()] = l;

        // ① 冻结前置门（FGM903）：**离线 Extract 读的是 P6 的产物**——paintOrder 与
        // world/worldVisual。相位机里 P7 在 P6 之后所以自然满足；离线路径没有相位机替它排序，
        // 而失败形态是「沿一条陈旧的序按陈旧的矩阵发射」——**静默错**，产物看起来完全正常。
        // Shape 的末次 Tick 定过形（内核 SettleStep 未装时走 DrainDerivedFull 全量版），
        // 但「定过形没有」是关于**产物**的断言，不能靠上游的实现细节推出来：这里自己验一次。
        if (!sc.Table.DerivedMatchesFullRecompute(out uint badIndex))
        {
            _diag.Add(FgmCodes.FreezeSelfCheck, FgmSeverity.Error, sc.Item.Name ?? sc.Item.Id,
                "派生列未定形（首个不一致节点槽 " + badIndex + "）——编译期 Extract 会沿陈旧 world 发射");
            return;
        }
        if (!sc.Table.ValidatePaintOrder(out string orderError))
        {
            _diag.Add(FgmCodes.FreezeSelfCheck, FgmSeverity.Error, sc.Item.Name ?? sc.Item.Id,
                "paintOrder 未定形：" + orderError + "——编译期 Extract 会沿陈旧序发射");
            return;
        }

        // ② 编译期 Extract（**运行期同一个 Extract**；离线路径自开尾剪枝补上管线 DrainTail 那道）。
        var stream = new RenderStream(sc.Item.Id);
        var extract = new Extract(stream, sc.Table, sc.Content) { PruneAfterRebuild = true };
        c.Report = extract.Rebuild();
        c.Stream = stream;

        // ③ CONT：按节点走 contentRef（内容表里没被节点引用的记录不进 blob——不可达即不冻结）。
        // 「登记过没有」用独立的 mapped 位记，不拿 ContentMap 的 0 当哨兵：canonical id 0 是
        // 合法结果（全零记录 = 无内容），拿它当「还没登记」会让同一条 cid 被重复登记，
        // 去重率的分母（Inserts）随之虚高——内存计划是账本，账本不许有重复记账。
        c.ContentMap = new uint[sc.Content.Count];
        var mapped = new bool[sc.Content.Count];
        for (ushort l = 0; l < n; l++)
        {
            NodeHandle h = sc.Locals.HandleOf(l);
            uint cid = sc.Table.ContentRef(h);
            if (cid == 0 || cid >= (uint)sc.Content.Count || mapped[cid]) continue;
            mapped[cid] = true;
            c.ContentMap[cid] = (uint)_content.Add(ContentRecordOf(sc, h, sc.Content.At(cid)));
        }

        // ④ 段区间记账。
        c.NodeStart = _totalNodes;
        c.NodeCount = n;
        _totalNodes += n;

        c.QuadStart = _quads.Count;
        c.QuadCount = stream.QuadCount;
        for (int i = 0; i < stream.QuadCount; i++) _quads.Add(stream.Quads[i]);

        c.ClipStart = _clips.Count;
        ReadOnlySpan<ClipEntry> clips = stream.Clips.Entries;
        c.ClipCount = clips.Length;
        for (int i = 0; i < clips.Length; i++) _clips.Add(clips[i]);

        c.SegStart = _segs.Count / AbiLayout.FgbSegSize;
        c.SegCount = stream.SegmentCount;
        for (int i = 0; i < stream.SegmentCount; i++) AppendSegment(stream.Segments[i]);

        c.LeafStart = _leaves.Count / AbiLayout.FgbLeafSize;
        c.LeafCount = stream.LeafCount;
        for (int i = 0; i < stream.LeafCount; i++) AppendLeaf(c, stream.Leaves[i]);

        c.LocalStart = _locals.Count / AbiLayout.FgbLocalSize;
        c.LocalCount = n;
        AppendLocals(c);

        AppendConstraints(c);

        // ⑤ COMP 的标量面。
        c.NameStr = _strings.Add(sc.Item.Name ?? sc.Item.Id);
        c.NameHash = FnvHash.Hash32(sc.Item.Name ?? sc.Item.Id);
        // 实例块 = 头 + 控制器状态 + 编译期声明的定长 scratch（M1 后两者为零，见架构机制 5）。
        c.InstanceBytes = (uint)Abi.FgbInstanceHeaderBytes;
        if (c.OpCount > 0) c.Flags |= 1;
        if (sc.Text != null && sc.Text.EntryCount > 0) c.Flags |= 2;
        _comps.Add(c);
    }

    /// <summary>一条内容记录 → CONT 字节（文本叶另取文本串进 STRT）。</summary>
    private byte[] ContentRecordOf(ShapedComponent sc, NodeHandle node, in ContentRecord record)
    {
        byte[] rec = new byte[AbiLayout.FgbContSize];
        Span<byte> s = rec;
        FgbRecordIo.U16(s, AbiLayout.FgbContKindOffset, (ushort)record.Kind);
        // 裁剪域的**形状参数**（软边梯度 / 四角半径）不进 M1 的 CONT：能开裁剪域的内容形态
        // （容器 overflow=scroll）本身还没进编译面（归 M2-06），本字段只有「开不开」一位。
        // 有声不静默：真出现非直角硬边就报 FGM303，免得降级路径拿一个直角窗口画圆角。
        if (record.OpensClip
            && (record.Clip.Soft.x != 0f || record.Clip.Soft.y != 0f
                || record.Clip.Radii.x != 0f || record.Clip.Radii.y != 0f
                || record.Clip.Radii.z != 0f || record.Clip.Radii.w != 0f))
        {
            _diag.Add(FgmCodes.ContentNotInM1, FgmSeverity.Info, sc.Item.Name ?? sc.Item.Id,
                "裁剪域的软边/圆角参数未进 M1 冻结面（CONT 只记「开不开」）——形状参数归 M2-06");
        }
        if (record.Kind != ExtractKind.Leaf)
        {
            if (record.OpensClip) FgbRecordIo.U8(s, AbiLayout.FgbContFlagsOffset, 1 << 2);
            return rec;
        }

        LeafSpec leaf = record.Leaf;
        byte flags = 0;
        if (leaf.Region.HasGrid) flags |= 1 << 0;
        if (leaf.Region.ScaleByTile) flags |= 1 << 1;
        if (record.OpensClip) flags |= 1 << 2;
        if (leaf.Text != null) flags |= 1 << 3;

        FgbRecordIo.U8(s, AbiLayout.FgbContBlendOffset, (byte)leaf.Blend);
        FgbRecordIo.U8(s, AbiLayout.FgbContFlagsOffset, flags);
        FgbRecordIo.U32(s, AbiLayout.FgbContTexIdOffset, leaf.Texture.Value);
        FgbRecordIo.U32(s, AbiLayout.FgbContBaseColorOffset, leaf.BaseColor);
        FgbRecordIo.U32(s, AbiLayout.FgbContEmitFlagsOffset, leaf.EmitFlags);
        FgbRecordIo.I32(s, AbiLayout.FgbContSlackHintOffset, leaf.SlackHint);
        FgbRecordIo.U32(s, AbiLayout.FgbContTileGridIndiceOffset, unchecked((uint)leaf.Region.TileGridIndice));
        FgbRecordIo.U32(s, AbiLayout.FgbContFillPackOffset,
            (uint)leaf.Fill.Method | ((uint)leaf.Fill.Origin << 8) | (leaf.Fill.Clockwise ? 1u << 16 : 0u));
        FgbRecordIo.F32(s, AbiLayout.FgbContUvX0Offset, leaf.Region.Uv.x);
        FgbRecordIo.F32(s, AbiLayout.FgbContUvY0Offset, leaf.Region.Uv.y);
        FgbRecordIo.F32(s, AbiLayout.FgbContUvX1Offset, leaf.Region.Uv.z);
        FgbRecordIo.F32(s, AbiLayout.FgbContUvY1Offset, leaf.Region.Uv.w);
        FgbRecordIo.F32(s, AbiLayout.FgbContSourceWidthOffset, leaf.Region.SourceWidth);
        FgbRecordIo.F32(s, AbiLayout.FgbContSourceHeightOffset, leaf.Region.SourceHeight);
        FgbRecordIo.F32(s, AbiLayout.FgbContGridXOffset, leaf.Region.Grid.x);
        FgbRecordIo.F32(s, AbiLayout.FgbContGridYOffset, leaf.Region.Grid.y);
        FgbRecordIo.F32(s, AbiLayout.FgbContGridWOffset, leaf.Region.Grid.z);
        FgbRecordIo.F32(s, AbiLayout.FgbContGridHOffset, leaf.Region.Grid.w);
        FgbRecordIo.F32(s, AbiLayout.FgbContFillAmountOffset, leaf.Fill.Amount);
        if (leaf.Text != null)
        {
            FgbRecordIo.U32(s, AbiLayout.FgbContTextStrOffset, _strings.Add(sc.Text?.TextOf(node)));
            if (!_textStyleNoted)
            {
                _textStyleNoted = true;
                _diag.Add(FgmCodes.ContentNotInM1, FgmSeverity.Info, "",
                    "文本样式表未进 M1 冻结面（face 注册与预烘字形归 M2-09）：CONT 只记文本串与容量上界，"
                    + "组件级降回运行时 Extract 对文本叶在 M1 不成立");
            }
        }
        return rec;
    }

    private bool _textStyleNoted;

    private void AppendSegment(in SegmentDesc seg)
    {
        byte[] rec = new byte[AbiLayout.FgbSegSize];
        Span<byte> s = rec;
        FgbRecordIo.U32(s, AbiLayout.FgbSegTex0Offset, seg.Tex0.Value);
        FgbRecordIo.U32(s, AbiLayout.FgbSegTex1Offset, seg.Tex1.Value);
        FgbRecordIo.U32(s, AbiLayout.FgbSegTex2Offset, seg.Tex2.Value);
        FgbRecordIo.U32(s, AbiLayout.FgbSegTex3Offset, seg.Tex3.Value);
        FgbRecordIo.U8(s, AbiLayout.FgbSegTexCountOffset, seg.TexCount);
        FgbRecordIo.U8(s, AbiLayout.FgbSegBlendOffset, (byte)seg.Blend);
        FgbRecordIo.U32(s, AbiLayout.FgbSegQuadStartOffset, (uint)seg.Start);
        FgbRecordIo.U32(s, AbiLayout.FgbSegQuadCountOffset, (uint)seg.Count);
        FgbRecordIo.U32(s, AbiLayout.FgbSegRunOffset, (uint)seg.RunIndex);
        _segs.AddRange(rec);
    }

    private void AppendLeaf(Frozen c, in LeafRange leaf)
    {
        byte[] rec = new byte[AbiLayout.FgbLeafSize];
        Span<byte> s = rec;
        ushort local = c.LocalOfHandle.TryGetValue(leaf.Node.Pack(), out ushort l) ? l : (ushort)0;
        uint contentRef = 0;
        uint cid = c.Sc.Table.ContentRef(leaf.Node);
        if (cid != 0 && cid < (uint)c.ContentMap.Length) contentRef = c.ContentMap[cid];
        FgbRecordIo.U16(s, AbiLayout.FgbLeafLocalIdOffset, local);
        FgbRecordIo.U16(s, AbiLayout.FgbLeafTexSlotOffset, (ushort)leaf.TexSlot);
        FgbRecordIo.U32(s, AbiLayout.FgbLeafQuadStartOffset, (uint)leaf.Start);
        FgbRecordIo.U32(s, AbiLayout.FgbLeafQuadCountOffset, (uint)leaf.Count);
        FgbRecordIo.U32(s, AbiLayout.FgbLeafQuadSlackOffset, (uint)leaf.Slack);
        FgbRecordIo.U32(s, AbiLayout.FgbLeafSegmentOffset, (uint)leaf.Segment);
        FgbRecordIo.U32(s, AbiLayout.FgbLeafRunOffset, (uint)leaf.Run);
        FgbRecordIo.U32(s, AbiLayout.FgbLeafClipEntryOffset, (uint)leaf.ClipEntry);
        FgbRecordIo.U32(s, AbiLayout.FgbLeafSlotOffset, (uint)leaf.Slot);
        FgbRecordIo.U32(s, AbiLayout.FgbLeafContentRefOffset, contentRef);
        FgbRecordIo.U32(s, AbiLayout.FgbLeafEmitFlagsOffset, leaf.EmitFlags);
        _leaves.AddRange(rec);
    }

    private void AppendLocals(Frozen c)
    {
        FuiChild[] children = c.Sc.Source.Children;
        for (ushort l = 0; l < c.LocalCount; l++)
        {
            byte[] rec = new byte[AbiLayout.FgbLocalSize];
            Span<byte> s = rec;
            string? editorId = c.Sc.Locals.EditorIdOf(l);
            string? name = l > 0 && l - 1 < children.Length ? children[l - 1].Name : null;
            ushort flags = 0;
            if (l > 0 && l - 1 < children.Length && children[l - 1].Type == FuiObjectType.Group) flags |= 1;
            FgbRecordIo.U16(s, AbiLayout.FgbLocalLocalIdOffset, l);
            FgbRecordIo.U16(s, AbiLayout.FgbLocalFlagsOffset, flags);
            FgbRecordIo.U32(s, AbiLayout.FgbLocalEditorIdStrOffset, _strings.Add(editorId));
            FgbRecordIo.U32(s, AbiLayout.FgbLocalNameStrOffset, _strings.Add(name));
            FgbRecordIo.U32(s, AbiLayout.FgbLocalEditorIdHashOffset,
                editorId == null ? 0u : FnvHash.Hash32(editorId));
            _locals.AddRange(rec);
        }
    }

    private void AppendConstraints(Frozen c)
    {
        ConstraintGraph? g = c.Sc.Constraints;
        c.OpStart = _ops.Count;
        c.FanStart = _fans.Count;
        c.IdxStart = _fanIdx.Count;
        if (g == null)
        {
            // 无关系的组件仍占 nodeCount 个空桶与掩码：FanOut/掩码按局部 id 直接下标寻址，
            // 缺桶 = 读侧要多一条分支，占零桶 = 读侧无分支。
            c.OpCount = 0;
            c.IdxCount = 0;
            for (int i = 0; i < c.NodeCount; i++) { _fans.Add(default); _masks.Add(0); }
            return;
        }
        c.OpCount = g.Ops.Length;
        for (int i = 0; i < g.Ops.Length; i++) _ops.Add(g.Ops[i]);
        for (int i = 0; i < g.FanOutBySrc.Length; i++) _fans.Add(g.FanOutBySrc[i]);
        for (int i = 0; i < g.FanOpIndices.Length; i++) _fanIdx.Add(g.FanOpIndices[i]);
        for (int i = 0; i < g.NodeMasks.Length; i++) _masks.Add(g.NodeMasks[i]);
        c.IdxCount = g.FanOpIndices.Length;
    }

    // ── NODE 段（包级扁平 + 相对化 + contentRef 重映射）────────────────────

    private byte[] BuildNodeSection()
    {
        byte[] payload = new byte[FgbNodeSection.PayloadBytes(_totalNodes)];
        Span<byte> s = payload;
        FgbNodeSection.WriteHeader(s, _totalNodes);
        for (int i = 0; i < _comps.Count; i++)
        {
            Frozen c = _comps[i];
            uint first = c.Sc.Locals.HandleOf(0).Index;
            FgbNodeSection.WriteSlice(s, _totalNodes, c.NodeStart, c.Sc.Table, first, c.NodeCount);
            for (int col = 0; col < Abi.NodeColumns.Length; col++)
            {
                if (!Abi.NodeColumns[col].Rebase) continue;
                Relativize(ColumnSlice(s, col, c), first);
            }
            RemapContentRef(ColumnSlice(s, AbiLayout.NodeColContentRef, c), c.ContentMap);
            c.ResolvedSlots = CountNonZero(ColumnSlice(s, AbiLayout.NodeColResolvedRef, c));
        }
        return payload;
    }

    private Span<byte> ColumnSlice(Span<byte> payload, int column, Frozen c)
    {
        int w = Abi.NodeColumns[column].ElementSize;
        return payload.Slice(FgbNodeSection.ColumnOffset(column, _totalNodes) + c.NodeStart * w, c.NodeCount * w);
    }

    /// <summary>
    /// 拓扑列的相对化（M1-19 留缝 ② 的兑现，消费 <see cref="AbiNodeColumn.Rebase"/> 位）：
    /// 绝对槽下标 → **组件内 1 基下标**，哨兵 0（= 无）原样保留。
    /// 实例化（M1-22）的逆变换是 <c>abs = instanceBase + rel − 1</c>；模板因此不绑包内位置。
    ///
    /// 边界写明：本包的编译世界里组件根恒在槽 1（建树是顺序分配的第一批槽），
    /// 于是 <paramref name="firstAbs"/> == 1 使本变换在**数值上是恒等**——它按 firstAbs
    /// 参数化并有独立用例（firstAbs ≠ 1）钉住，真语料随 M1-22 的实例化基址回填出现。
    ///
    /// 字节序：这里读写的是 <c>NodeTable.ExportColumn</c> 用 <c>MemoryMarshal</c> blit 出来的
    /// 列（宿主序），故补丁也走 <c>MemoryMarshal</c>——同一段字节不许两种解释。
    /// </summary>
    internal static void Relativize(Span<byte> column, uint firstAbs)
    {
        Span<uint> v = MemoryMarshal.Cast<byte, uint>(column);
        for (int i = 0; i < v.Length; i++)
        {
            uint abs = v[i];
            v[i] = abs == 0u ? 0u : abs - firstAbs + 1u;
        }
    }

    /// <summary>contentRef 列 → canonical CONT id（去重把「同内容」变成「同 id」，引用必须跟着改）。</summary>
    internal static void RemapContentRef(Span<byte> column, uint[] map)
    {
        Span<uint> v = MemoryMarshal.Cast<byte, uint>(column);
        for (int i = 0; i < v.Length; i++)
            v[i] = v[i] < (uint)map.Length ? map[v[i]] : 0u;
    }

    private static int CountNonZero(Span<byte> column)
    {
        Span<uint> v = MemoryMarshal.Cast<byte, uint>(column);
        int n = 0;
        for (int i = 0; i < v.Length; i++) if (v[i] != 0u) n++;
        return n;
    }

    // ── 段装配 ──────────────────────────────────────────────────────────────

    private byte[] Assemble(FuiPackage pkg, byte[] node)
    {
        var w = new FgbWriter();
        Add(w, "STRT", AbiLayout.FgbSectionStrt, BuildStrt());
        Add(w, "TREF", AbiLayout.FgbSectionTref, _texRefs.ToPayload());
        Add(w, "COMP", AbiLayout.FgbSectionComp, BuildComp());
        Add(w, "NODE", AbiLayout.FgbSectionNode, node);
        Add(w, "CONT", AbiLayout.FgbSectionCont, _content.ToPayload());
        Add(w, "LOCL", AbiLayout.FgbSectionLocl, _locals.ToArray());
        Add(w, "CNST", AbiLayout.FgbSectionCnst, BuildCnst());
        Add(w, "QUAD", AbiLayout.FgbSectionQuad, MemoryMarshal.AsBytes(_quads.ToArray().AsSpan()).ToArray());
        Add(w, "SEGS", AbiLayout.FgbSectionSegs, _segs.ToArray());
        Add(w, "LEAF", AbiLayout.FgbSectionLeaf, _leaves.ToArray());
        Add(w, "CLIP", AbiLayout.FgbSectionClip, MemoryMarshal.AsBytes(_clips.ToArray().AsSpan()).ToArray());
        // combinedRefHash 恒零：见上方 FGM304——链上各包的 sourceHash 不在单包编译面内。
        return w.Finish(pkg.SourceHash, 0ul, (ushort)_shaped.ScaleLevel, (ushort)_shaped.BranchId);
    }

    private void Add(FgbWriter w, string name, uint fourcc, byte[] payload)
    {
        w.AddSection(fourcc, payload);
        Sections.Add((name, fourcc, payload.Length));
    }

    private byte[] BuildStrt()
    {
        byte[] payload = new byte[FgbStrtSection.PayloadBytes(_strings.Count, _strings.PoolBytes)];
        Span<byte> s = payload;
        FgbRecordIo.U32(s, AbiLayout.FgbStrtHeaderCountOffset, (uint)_strings.Count);
        FgbRecordIo.U32(s, AbiLayout.FgbStrtHeaderPoolBytesOffset, (uint)_strings.PoolBytes);
        int pool = FgbStrtSection.PoolOffset(_strings.Count);
        uint at = 0;
        for (int i = 0; i < _strings.Count; i++)
        {
            ReadOnlySpan<byte> b = _strings.BytesAt((uint)i);
            int e = FgbStrtSection.EntryOffset(i);
            FgbRecordIo.U32(s, e + AbiLayout.FgbStrtEntryOffsetOffset, at);
            FgbRecordIo.U32(s, e + AbiLayout.FgbStrtEntryLengthOffset, (uint)b.Length);
            b.CopyTo(s.Slice(pool + (int)at, b.Length));
            at += (uint)b.Length;
        }
        return payload;
    }

    private byte[] BuildComp()
    {
        byte[] payload = new byte[_comps.Count * AbiLayout.FgbCompSize];
        for (int i = 0; i < _comps.Count; i++)
        {
            Frozen c = _comps[i];
            Span<byte> s = payload.AsSpan(i * AbiLayout.FgbCompSize, AbiLayout.FgbCompSize);
            FgbRecordIo.U32(s, AbiLayout.FgbCompNameStrOffset, c.NameStr);
            FgbRecordIo.U32(s, AbiLayout.FgbCompNameHashOffset, c.NameHash);
            FgbRecordIo.U32(s, AbiLayout.FgbCompNodeStartOffset, (uint)c.NodeStart);
            FgbRecordIo.U32(s, AbiLayout.FgbCompNodeCountOffset, (uint)c.NodeCount);
            FgbRecordIo.U32(s, AbiLayout.FgbCompQuadStartOffset, (uint)c.QuadStart);
            FgbRecordIo.U32(s, AbiLayout.FgbCompQuadCountOffset, (uint)c.QuadCount);
            FgbRecordIo.U32(s, AbiLayout.FgbCompSegStartOffset, (uint)c.SegStart);
            FgbRecordIo.U32(s, AbiLayout.FgbCompSegCountOffset, (uint)c.SegCount);
            FgbRecordIo.U32(s, AbiLayout.FgbCompLeafStartOffset, (uint)c.LeafStart);
            FgbRecordIo.U32(s, AbiLayout.FgbCompLeafCountOffset, (uint)c.LeafCount);
            FgbRecordIo.U32(s, AbiLayout.FgbCompClipStartOffset, (uint)c.ClipStart);
            FgbRecordIo.U32(s, AbiLayout.FgbCompClipCountOffset, (uint)c.ClipCount);
            FgbRecordIo.U32(s, AbiLayout.FgbCompCnstOpStartOffset, (uint)c.OpStart);
            FgbRecordIo.U32(s, AbiLayout.FgbCompCnstOpCountOffset, (uint)c.OpCount);
            FgbRecordIo.U32(s, AbiLayout.FgbCompCnstFanStartOffset, (uint)c.FanStart);
            FgbRecordIo.U32(s, AbiLayout.FgbCompCnstIdxStartOffset, (uint)c.IdxStart);
            FgbRecordIo.U32(s, AbiLayout.FgbCompCnstIdxCountOffset, (uint)c.IdxCount);
            FgbRecordIo.U32(s, AbiLayout.FgbCompLocalStartOffset, (uint)c.LocalStart);
            FgbRecordIo.U32(s, AbiLayout.FgbCompLocalCountOffset, (uint)c.LocalCount);
            FgbRecordIo.U32(s, AbiLayout.FgbCompInstanceBytesOffset, c.InstanceBytes);
            FgbRecordIo.F32(s, AbiLayout.FgbCompSourceWidthOffset, c.Sc.Source.SourceWidth);
            FgbRecordIo.F32(s, AbiLayout.FgbCompSourceHeightOffset, c.Sc.Source.SourceHeight);
            FgbRecordIo.U16(s, AbiLayout.FgbCompCtrlCountOffset, 0);
            FgbRecordIo.U16(s, AbiLayout.FgbCompFlagsOffset, c.Flags);
        }
        return payload;
    }

    private byte[] BuildCnst()
    {
        byte[] payload = new byte[FgbCnstSection.PayloadBytes(_ops.Count, _fans.Count, _fanIdx.Count, _masks.Count)];
        Span<byte> s = payload;
        FgbRecordIo.U32(s, AbiLayout.FgbCnstHeaderOpCountOffset, (uint)_ops.Count);
        FgbRecordIo.U32(s, AbiLayout.FgbCnstHeaderFanCountOffset, (uint)_fans.Count);
        FgbRecordIo.U32(s, AbiLayout.FgbCnstHeaderIndexCountOffset, (uint)_fanIdx.Count);
        FgbRecordIo.U32(s, AbiLayout.FgbCnstHeaderMaskCountOffset, (uint)_masks.Count);
        MemoryMarshal.AsBytes(_ops.ToArray().AsSpan())
            .CopyTo(s.Slice(FgbCnstSection.OpsOffset));
        MemoryMarshal.AsBytes(_fans.ToArray().AsSpan())
            .CopyTo(s.Slice(FgbCnstSection.FansOffset(_ops.Count)));
        MemoryMarshal.AsBytes(_fanIdx.ToArray().AsSpan())
            .CopyTo(s.Slice(FgbCnstSection.IndicesOffset(_ops.Count, _fans.Count)));
        _masks.ToArray().AsSpan()
            .CopyTo(s.Slice(FgbCnstSection.MasksOffset(_ops.Count, _fans.Count, _fanIdx.Count)));
        return payload;
    }

    // ── 内存计划的读口（MemoryPlan 是纯格式化；账本住这里）──────────────────

    internal readonly List<(string Name, uint Fourcc, int Bytes)> Sections = new List<(string, uint, int)>();
    internal ShapedPackage Shaped => _shaped;
    internal StringPool Strings => _strings;
    internal CanonicalTable Content => _content;
    internal CanonicalTable TexRefs => _texRefs;
    internal int TotalNodes => _totalNodes;
    internal int TotalQuads => _quads.Count;
    internal int TotalClips => _clips.Count;
    internal int TotalOps => _ops.Count;
    internal IReadOnlyList<Frozen> Comps => _comps;

}
