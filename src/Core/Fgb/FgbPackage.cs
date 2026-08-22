using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FairyNext.Contracts;
using FairyNext.Core.Layout;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Core.Fgb;

/// <summary>装载选项。宿主给的**对照物**都在这里：文件名、全局档位设定、纹理通道、已装载包表。</summary>
public readonly struct FgbLoadOptions
{
    /// <summary>发布文件名（装载门 5 的语料）。null/空 = 该门跳过并在报告里记 skip。</summary>
    public readonly string? FileName;

    /// <summary>宿主的全局 scaleLevel 设定（&lt;0 = 不比，门 4 的该项跳过）。</summary>
    public readonly int ExpectedScaleLevel;

    /// <summary>宿主的全局 branchId 设定（&lt;0 = 不比）。</summary>
    public readonly int ExpectedBranchId;

    /// <summary>纹理装载通道（null = <see cref="NullAssetSource"/>）。</summary>
    public readonly IAssetSource? Textures;

    /// <summary>已装载包表（门 4 的 DEPS 逐项与 combinedRefHash 重算读它；null = 无表可查）。</summary>
    public readonly IFgbPackageRegistry? Packages;

    /// <summary>是否重算 selfHash（发布包可信任跳过；默认 true）。</summary>
    public readonly bool VerifySelfHash;

    /// <summary>
    /// 本包在宿主树里的 slab 域号（模板 id = <c>域号&lt;&lt;16 | 组件下标</c>）。
    /// 同一棵 <see cref="NodeTable"/> 里并存的两个包必须给不同的域号——
    /// 模板 id 是 slab bin 的键，撞键 = 两个不同长度的模板抢同一个空闲栈。
    /// </summary>
    public readonly ushort SlabDomain;

    public FgbLoadOptions(string? fileName = null, int expectedScaleLevel = -1, int expectedBranchId = -1,
        IAssetSource? textures = null, IFgbPackageRegistry? packages = null,
        bool verifySelfHash = true, ushort slabDomain = 0)
    {
        FileName = fileName;
        ExpectedScaleLevel = expectedScaleLevel;
        ExpectedBranchId = expectedBranchId;
        Textures = textures;
        Packages = packages;
        VerifySelfHash = verifySelfHash;
        SlabDomain = slabDomain;
    }
}

/// <summary>一个组件模板在包内的全部段区间（COMP 记录的解码形态）。</summary>
public readonly struct FgbComponentDef
{
    public readonly uint NameStr, NameHash;
    public readonly int NodeStart, NodeCount;
    public readonly int QuadStart, QuadCount;
    public readonly int SegStart, SegCount;
    public readonly int LeafStart, LeafCount;
    public readonly int ClipStart, ClipCount;
    public readonly int CnstOpStart, CnstOpCount, CnstFanStart, CnstIdxStart, CnstIdxCount;
    public readonly int LocalStart, LocalCount;
    public readonly int PlanStart, PlanCount;
    public readonly uint InstanceBytes;
    public readonly float SourceWidth, SourceHeight;
    public readonly ushort CtrlCount, Flags;

    internal FgbComponentDef(ReadOnlySpan<byte> rec)
    {
        NameStr = FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompNameStrOffset);
        NameHash = FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompNameHashOffset);
        NodeStart = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompNodeStartOffset);
        NodeCount = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompNodeCountOffset);
        QuadStart = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompQuadStartOffset);
        QuadCount = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompQuadCountOffset);
        SegStart = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompSegStartOffset);
        SegCount = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompSegCountOffset);
        LeafStart = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompLeafStartOffset);
        LeafCount = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompLeafCountOffset);
        ClipStart = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompClipStartOffset);
        ClipCount = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompClipCountOffset);
        CnstOpStart = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompCnstOpStartOffset);
        CnstOpCount = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompCnstOpCountOffset);
        CnstFanStart = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompCnstFanStartOffset);
        CnstIdxStart = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompCnstIdxStartOffset);
        CnstIdxCount = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompCnstIdxCountOffset);
        LocalStart = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompLocalStartOffset);
        LocalCount = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompLocalCountOffset);
        PlanStart = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompPlanStartOffset);
        PlanCount = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompPlanCountOffset);
        InstanceBytes = FgbRecordIo.ReadU32(rec, AbiLayout.FgbCompInstanceBytesOffset);
        SourceWidth = FgbRecordIo.ReadF32(rec, AbiLayout.FgbCompSourceWidthOffset);
        SourceHeight = FgbRecordIo.ReadF32(rec, AbiLayout.FgbCompSourceHeightOffset);
        CtrlCount = FgbRecordIo.ReadU16(rec, AbiLayout.FgbCompCtrlCountOffset);
        Flags = FgbRecordIo.ReadU16(rec, AbiLayout.FgbCompFlagsOffset);
    }

    /// <summary>有约束图（COMP.flags bit0）。</summary>
    public bool HasConstraints => (Flags & 1) != 0;
    /// <summary>有文本叶（COMP.flags bit1）——M1 的文本样式未进冻结面，见 <see cref="FgbPackage.TextLeavesUnbound"/>。</summary>
    public bool HasTextLeaves => (Flags & 2) != 0;
    /// <summary>有嵌套子组件步（COMP.flags bit2）。</summary>
    public bool HasNested => (Flags & 4) != 0;
}

/// <summary>一条后序扁平实例化步（PLAN 记录的解码形态）。</summary>
public readonly struct FgbPlanStep
{
    public readonly int CompIndex;
    public readonly uint ParentStep;
    public readonly ushort Kind;
    public readonly ushort HostLocalId;
    public readonly ushort ListItemCount;

    internal FgbPlanStep(ReadOnlySpan<byte> rec)
    {
        CompIndex = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbPlanCompIndexOffset);
        ParentStep = FgbRecordIo.ReadU32(rec, AbiLayout.FgbPlanParentStepOffset);
        Kind = FgbRecordIo.ReadU16(rec, AbiLayout.FgbPlanKindOffset);
        HostLocalId = FgbRecordIo.ReadU16(rec, AbiLayout.FgbPlanHostLocalIdOffset);
        ListItemCount = FgbRecordIo.ReadU16(rec, AbiLayout.FgbPlanListItemCountOffset);
    }

    /// <summary>顶层块（节点段 + 实例块一次分配）。</summary>
    public bool IsRoot => Kind == Abi.FgbPlanKindRoot;
}

/// <summary>
/// 一个已装载的 FGB 包（架构 <c>LoadedPackage</c> 的落地形态）——「零解析三操作」的宿主：
///  ① **验证**：<see cref="TryLoad"/> 里把容器门（M1-19 的门 1-3）与**段内**门跑完，
///     再跑身份门 4/5。失败面严格二值：拒载（<c>package == null</c>）或装载（可能带降级标记）。
///  ② **视图**：段一律以 <c>(offset,length)</c> 记住，取用时切 span / <c>MemoryMarshal.Cast</c>——
///     blob 一生不复制，没有任何「反序列化成对象图」的步骤。
///  ③ **绑定**：TREF → 运行期纹理编号（装载期，便宜）、PTCH → 就地回填、
///     纹理像素与 CONT 解码留到**首用**（<see cref="EnsureContentDecoded"/> / 实例 Realize）。
///
/// **blob 所有权**：<see cref="TryLoad"/> 收下数组的所有权。PTCH 是**就地**回填，
/// 于是绑定之后 blob 的字节已经不是文件里那份——同一个数组再装载一次会被门 3（selfHash）
/// 响亮拒掉。这是刻意的：宁可让「重复装载同一数组」响亮失败，也不让它悄悄用上一次的回填结果。
/// </summary>
public sealed class FgbPackage
{
    private readonly byte[] _blob;
    private readonly Sec _strt, _tref, _deps, _comp, _node, _plan, _cont, _locl, _cnst, _quad, _segs, _leaf, _clip, _ptch;
    private readonly int _cnstOps, _cnstFans, _cnstIdx, _cnstMasks;
    private readonly FgbTexRef[] _texSymbols;
    private readonly uint[] _texRuntimeIds;
    private readonly bool[] _texAcquired;
    private readonly IAssetSource _textures;
    private readonly ConstraintGraph?[] _graphs;
    private ContentRecord[]? _content;

    private readonly struct Sec
    {
        public readonly int Offset, Length;
        public readonly bool Present;
        public Sec(int offset, int length) { Offset = offset; Length = length; Present = true; }
    }

    private FgbPackage(byte[] blob, in Ctx c)
    {
        _blob = blob;
        _strt = c.Strt; _tref = c.Tref; _deps = c.Deps; _comp = c.Comp; _node = c.Node; _plan = c.Plan;
        _cont = c.Cont; _locl = c.Locl; _cnst = c.Cnst; _quad = c.Quad; _segs = c.Segs; _leaf = c.Leaf;
        _clip = c.Clip; _ptch = c.Ptch;
        _cnstOps = c.CnstOps; _cnstFans = c.CnstFans; _cnstIdx = c.CnstIdx; _cnstMasks = c.CnstMasks;
        _texSymbols = c.TexSymbols;
        _texRuntimeIds = c.TexRuntimeIds;
        _texAcquired = new bool[c.TexSymbols.Length];
        _textures = c.Textures;
        Report = c.Report;
        Degraded = c.Report.Outcome == FgbLoadOutcome.Degraded;
        SlabDomain = c.SlabDomain;
        PkgId = c.PkgId;
        SourceHash = c.SourceHash;
        ScaleLevel = c.ScaleLevel;
        BranchId = c.BranchId;
        _graphs = new ConstraintGraph?[ComponentCount];
    }

    private struct Ctx
    {
        public Sec Strt, Tref, Deps, Comp, Node, Plan, Cont, Locl, Cnst, Quad, Segs, Leaf, Clip, Ptch;
        public int CnstOps, CnstFans, CnstIdx, CnstMasks;
        public FgbTexRef[] TexSymbols;
        public uint[] TexRuntimeIds;
        public IAssetSource Textures;
        public FgbLoadReport Report;
        public ushort SlabDomain;
        public ulong PkgId, SourceHash;
        public ushort ScaleLevel, BranchId;
    }

    // ── 身份与账 ────────────────────────────────────────────────────────────

    /// <summary>本次装载的报告（逐门判决 + 段清单 + patch 数 + 耗时）。</summary>
    public FgbLoadReport Report { get; }

    /// <summary>身份哈希失配：组件级降回运行时 Extract（画面照常，有声计数）。</summary>
    public bool Degraded { get; }

    /// <summary>slab 域号（模板 id 的高 16 位）。</summary>
    public ushort SlabDomain { get; }

    /// <summary>包 id 的 FNV-1a 64（头字段）。</summary>
    public ulong PkgId { get; }

    /// <summary>源 .fui 描述符字节哈希（四维身份 1/4）。</summary>
    public ulong SourceHash { get; }

    /// <summary>内容缩放档（四维身份 3/4）。</summary>
    public ushort ScaleLevel { get; }

    /// <summary>branch 变体（四维身份 4/4）。</summary>
    public ushort BranchId { get; }

    /// <summary>
    /// 解码 CONT 时遇到的**文本叶**条数。M1 的文本样式表未进冻结面（M1-20b 明码边界），
    /// 于是文本叶被解成「不产渲染单元」——**不画错**、有计数，正是机制 4 要求的那一级。
    /// </summary>
    public int TextLeavesUnbound { get; private set; }

    /// <summary>组件模板数。</summary>
    public int ComponentCount => _comp.Present ? _comp.Length / AbiLayout.FgbCompSize : 0;

    /// <summary>PLAN 步数。</summary>
    public int PlanStepCount => _plan.Present ? _plan.Length / AbiLayout.FgbPlanSize : 0;

    /// <summary>CONT 记录数（含 0 号「无内容」哨兵）。</summary>
    public int ContentCount => _cont.Present ? _cont.Length / AbiLayout.FgbContSize : 0;

    /// <summary>TREF 纹理符号数。</summary>
    public int TextureCount => _texSymbols.Length;

    /// <summary>DEPS 条数。</summary>
    public int DependencyCount => _deps.Present ? _deps.Length / AbiLayout.FgbDepSize : 0;

    /// <summary>CONT 是否已解码（**懒绑定的观测点**：没有任何实例被用过之前它是 false）。</summary>
    public bool ContentDecoded => _content != null;

    /// <summary>已装载像素的纹理数（懒装载的观测点）。</summary>
    public int TexturesAcquired
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _texAcquired.Length; i++) if (_texAcquired[i]) n++;
            return n;
        }
    }

    // ── 段视图（零拷贝）─────────────────────────────────────────────────────

    /// <summary>整 blob（回填后的字节；测试的读回对账读它）。</summary>
    public ReadOnlySpan<byte> Blob => _blob;

    private ReadOnlySpan<byte> Span(in Sec s) => s.Present ? _blob.AsSpan(s.Offset, s.Length) : default;

    /// <summary>COMP 段。</summary>
    public ReadOnlySpan<byte> CompSection => Span(in _comp);
    /// <summary>NODE 段。</summary>
    public ReadOnlySpan<byte> NodeSection => Span(in _node);
    /// <summary>PLAN 段。</summary>
    public ReadOnlySpan<byte> PlanSection => Span(in _plan);
    /// <summary>CONT 段。</summary>
    public ReadOnlySpan<byte> ContSection => Span(in _cont);
    /// <summary>LOCL 段。</summary>
    public ReadOnlySpan<byte> LoclSection => Span(in _locl);
    /// <summary>CNST 段。</summary>
    public ReadOnlySpan<byte> CnstSection => Span(in _cnst);
    /// <summary>QUAD 段。</summary>
    public ReadOnlySpan<byte> QuadSection => Span(in _quad);
    /// <summary>SEGS 段。</summary>
    public ReadOnlySpan<byte> SegsSection => Span(in _segs);
    /// <summary>LEAF 段。</summary>
    public ReadOnlySpan<byte> LeafSection => Span(in _leaf);
    /// <summary>CLIP 段。</summary>
    public ReadOnlySpan<byte> ClipSection => Span(in _clip);
    /// <summary>TREF 段。</summary>
    public ReadOnlySpan<byte> TrefSection => Span(in _tref);
    /// <summary>PTCH 段。</summary>
    public ReadOnlySpan<byte> PtchSection => Span(in _ptch);
    /// <summary>DEPS 段。</summary>
    public ReadOnlySpan<byte> DepsSection => Span(in _deps);

    /// <summary>第 i 个组件模板。</summary>
    public FgbComponentDef Component(int i) =>
        new FgbComponentDef(CompSection.Slice(i * AbiLayout.FgbCompSize, AbiLayout.FgbCompSize));

    /// <summary>第 i 条实例化步。</summary>
    public FgbPlanStep PlanStep(int i) =>
        new FgbPlanStep(PlanSection.Slice(i * AbiLayout.FgbPlanSize, AbiLayout.FgbPlanSize));

    /// <summary>按组件名哈希找模板（<c>AssetServer.Resolve</c> 的 M1 形态）。</summary>
    public bool TryResolve(uint nameHash, out int compIndex)
    {
        for (int i = 0; i < ComponentCount; i++)
        {
            if (Component(i).NameHash != nameHash) continue;
            compIndex = i;
            return true;
        }
        compIndex = -1;
        return false;
    }

    /// <summary>
    /// 模板 id（slab bin 键）= <c>最高位 | 域号&lt;&lt;16 | 组件下标</c>。
    /// **最高位必须置**：0 是 <see cref="NodeTable.SingleSlotTemplate"/>（单槽 bin，段长恒 1），
    /// 域 0 的组件 0 撞上它就是「两个不同段长抢同一个空闲栈」——slab 的断言会当场响，
    /// 但那时错已经发生在编号上，不在分配器里。
    /// </summary>
    public uint TemplateIdOf(int compIndex) => 0x8000_0000u | ((uint)SlabDomain << 16) | (uint)compIndex;

    /// <summary>STRT 取串（0 号哨兵 = 空串；越界同样出空串——读侧不因索引坏掉而抛）。</summary>
    public string StringAt(uint index)
    {
        if (!_strt.Present) return string.Empty;
        ReadOnlySpan<byte> s = Span(in _strt);
        int count = (int)FgbRecordIo.ReadU32(s, AbiLayout.FgbStrtHeaderCountOffset);
        if (index == 0 || index >= (uint)count) return string.Empty;
        int e = FgbStrtSection.EntryOffset((int)index);
        int off = (int)FgbRecordIo.ReadU32(s, e + AbiLayout.FgbStrtEntryOffsetOffset);
        int len = (int)FgbRecordIo.ReadU32(s, e + AbiLayout.FgbStrtEntryLengthOffset);
        int pool = FgbStrtSection.PoolOffset(count);
        return Encoding.UTF8.GetString(s.Slice(pool + off, len).ToArray());
    }

    /// <summary>第 i 个纹理符号。</summary>
    public FgbTexRef TextureSymbol(int i) => _texSymbols[i];

    /// <summary>第 i 个纹理符号在本次运行里的编号（PTCH 回填进段的就是它）。</summary>
    public uint TextureRuntimeId(int i) => _texRuntimeIds[i];

    // ── 绑定的「首用」半边 ──────────────────────────────────────────────────

    /// <summary>
    /// 解码 CONT 段为运行期 <see cref="ContentRecord"/>（**首用时**才做，一包一次）。
    /// 这一步是「绑定」里唯一带解释的动作，故它也是 arm-not-mount 的落点：
    /// 装载只记住段在哪，真把字节读成记录要等第一个实例被画。
    /// </summary>
    public void EnsureContentDecoded()
    {
        if (_content != null) return;
        int n = ContentCount;
        var recs = new ContentRecord[n < 1 ? 1 : n];
        ReadOnlySpan<byte> s = ContSection;
        for (int i = 1; i < n; i++)
            recs[i] = DecodeContent(s.Slice(i * AbiLayout.FgbContSize, AbiLayout.FgbContSize));
        _content = recs;
    }

    /// <summary>读一条已解码内容记录（未解码时先解码；越界出「无内容」）。</summary>
    public ContentRecord ContentAt(uint id)
    {
        EnsureContentDecoded();
        ContentRecord[] c = _content!;
        return id < (uint)c.Length ? c[id] : default;
    }

    /// <summary>
    /// 装载一个纹理符号的像素（首用时）。false = 通道装不上——调用方按机制 4 记账，
    /// 不把「装不上」画成一块随机采样。
    /// </summary>
    public bool AcquireTexture(int i)
    {
        if ((uint)i >= (uint)_texSymbols.Length) return false;
        if (_texAcquired[i]) return true;
        if (!_textures.TryAcquire(_texRuntimeIds[i])) return false;
        _texAcquired[i] = true;
        return true;
    }

    /// <summary>按运行期编号装载（Realize 拿到的是段键里的编号，不是 TREF 下标）。</summary>
    public bool AcquireTextureByRuntimeId(uint texId)
    {
        if (texId == 0u) return true;                     // 纯色叶：没有纹理要装
        for (int i = 0; i < _texRuntimeIds.Length; i++)
            if (_texRuntimeIds[i] == texId) return AcquireTexture(i);
        return false;
    }

    /// <summary>
    /// 组件的约束图（CNST 段 → 运行期 <see cref="ConstraintGraph"/>，一组件一次，缓存）。
    /// **组件内相对**在这里归位：FanOut.Start 与算子下标池里的值都加上 <c>COMP.cnstIdxStart</c>/
    /// <c>cnstOpStart</c> 的差，换算成本组件切片内的下标（图本身只认组件内编号）。
    /// </summary>
    public ConstraintGraph? ConstraintGraphOf(int compIndex)
    {
        if (_graphs[compIndex] != null) return _graphs[compIndex];
        FgbComponentDef d = Component(compIndex);
        if (!d.HasConstraints || d.CnstOpCount == 0) return null;

        ReadOnlySpan<byte> s = CnstSection;
        ReadOnlySpan<ConstraintOp> ops = MemoryMarshal.Cast<byte, ConstraintOp>(
            s.Slice(FgbCnstSection.OpsOffset, _cnstOps * FgbCnstSection.OpBytes));
        ReadOnlySpan<FanOut> fans = MemoryMarshal.Cast<byte, FanOut>(
            s.Slice(FgbCnstSection.FansOffset(_cnstOps), _cnstFans * FgbCnstSection.FanBytes));
        ReadOnlySpan<ushort> idx = MemoryMarshal.Cast<byte, ushort>(
            s.Slice(FgbCnstSection.IndicesOffset(_cnstOps, _cnstFans), _cnstIdx * FgbCnstSection.IndexBytes));
        ReadOnlySpan<byte> masks = s.Slice(
            FgbCnstSection.MasksOffset(_cnstOps, _cnstFans, _cnstIdx), _cnstMasks);

        var myOps = ops.Slice(d.CnstOpStart, d.CnstOpCount).ToArray();
        var myFans = new FanOut[d.NodeCount];
        for (int i = 0; i < d.NodeCount; i++) myFans[i] = fans[d.CnstFanStart + i];
        var myIdx = idx.Slice(d.CnstIdxStart, d.CnstIdxCount).ToArray();
        var myMasks = masks.Slice(d.CnstFanStart, d.NodeCount).ToArray();

        var g = new ConstraintGraph(myOps, myFans, myIdx, d.NodeCount, myMasks);
        _graphs[compIndex] = g;
        return g;
    }

    // ── ①验证 + ②视图 + ③绑定 ──────────────────────────────────────────────

    /// <summary>
    /// 装载一个 FGB blob。<paramref name="blob"/> 的**所有权交给本方法**（PTCH 就地回填，见类型头）。
    /// false ⇒ <paramref name="package"/> 为 null 且 <paramref name="report"/> 带门号；
    /// true ⇒ 全段可达（<paramref name="package"/>.Degraded 可能为真：装得上但身份哈希失配）。
    /// **永不抛**：任意字节序列的可接受结果只有这两种（fuzz 常设门盯的就是这句）。
    /// </summary>
    public static bool TryLoad(byte[] blob, in FgbLoadOptions options,
        out FgbPackage? package, out FgbLoadReport report)
    {
        long t0 = Stopwatch.GetTimestamp();
        package = null;
        if (blob == null)
        {
            report = new FgbLoadReport();
            report.Note(FgbGate.Truncated, FgbGateClass.Structural, true, false, "blob 为 null");
            return false;
        }

        if (!FgbBlobView.TryOpen(blob, out FgbBlobView view, out report, options.VerifySelfHash))
        {
            report.ElapsedTicks = Stopwatch.GetTimestamp() - t0;
            return false;
        }

        var c = default(Ctx);
        c.Report = report;
        c.Textures = options.Textures ?? new NullAssetSource();
        c.SlabDomain = options.SlabDomain;
        c.PkgId = view.PkgId;
        c.SourceHash = view.SourceHash;
        c.ScaleLevel = view.ScaleLevel;
        c.BranchId = view.BranchId;
        c.TexSymbols = Array.Empty<FgbTexRef>();
        c.TexRuntimeIds = Array.Empty<uint>();

        if (!Locate(view, ref c, report)) { report.ElapsedTicks = Stopwatch.GetTimestamp() - t0; return false; }
        if (!ValidateSections(blob, ref c, report)) { report.ElapsedTicks = Stopwatch.GetTimestamp() - t0; return false; }
        if (!ValidateComponents(blob, ref c, report)) { report.ElapsedTicks = Stopwatch.GetTimestamp() - t0; return false; }
        if (!ValidatePlan(blob, ref c, report)) { report.ElapsedTicks = Stopwatch.GetTimestamp() - t0; return false; }
        if (!ValidatePatches(blob, ref c, report)) { report.ElapsedTicks = Stopwatch.GetTimestamp() - t0; return false; }
        if (!GateIdentity(view, blob, ref c, in options, report)) { report.ElapsedTicks = Stopwatch.GetTimestamp() - t0; return false; }

        // 绑定的两半：先把 TREF 的符号换成运行期编号，再据此就地回填 PTCH（顺序不可换）。
        BindTextures(blob, ref c);
        var pkg = new FgbPackage(blob, in c);
        pkg.Bind(report);
        if (report.Outcome == FgbLoadOutcome.Degraded) report.DegradedComponents = pkg.ComponentCount;
        report.ComponentCount = pkg.ComponentCount;
        report.PlanSteps = pkg.PlanStepCount;
        report.DepCount = pkg.DependencyCount;
        report.ElapsedTicks = Stopwatch.GetTimestamp() - t0;
        package = pkg;
        return true;
    }

    private static bool Locate(FgbBlobView view, ref Ctx c, FgbLoadReport report)
    {
        Sec Get(uint fourcc) =>
            view.TryGetSectionRange(fourcc, out int off, out int len) ? new Sec(off, len) : default;

        c.Strt = Get(AbiLayout.FgbSectionStrt);
        c.Tref = Get(AbiLayout.FgbSectionTref);
        c.Deps = Get(AbiLayout.FgbSectionDeps);
        c.Comp = Get(AbiLayout.FgbSectionComp);
        c.Node = Get(AbiLayout.FgbSectionNode);
        c.Plan = Get(AbiLayout.FgbSectionPlan);
        c.Cont = Get(AbiLayout.FgbSectionCont);
        c.Locl = Get(AbiLayout.FgbSectionLocl);
        c.Cnst = Get(AbiLayout.FgbSectionCnst);
        c.Quad = Get(AbiLayout.FgbSectionQuad);
        c.Segs = Get(AbiLayout.FgbSectionSegs);
        c.Leaf = Get(AbiLayout.FgbSectionLeaf);
        c.Clip = Get(AbiLayout.FgbSectionClip);
        c.Ptch = Get(AbiLayout.FgbSectionPtch);

        // 必备段：少一个就没有「组件」这个概念，装上去也无从实例化——结构性拒载。
        string missing = "";
        if (!c.Comp.Present) missing += "COMP ";
        if (!c.Node.Present) missing += "NODE ";
        if (!c.Plan.Present) missing += "PLAN ";
        if (!c.Cont.Present) missing += "CONT ";
        if (!c.Locl.Present) missing += "LOCL ";
        if (!c.Cnst.Present) missing += "CNST ";
        if (!c.Strt.Present) missing += "STRT ";
        if (missing.Length > 0)
        {
            report.Note(FgbGate.SectionMissing, FgbGateClass.Structural, true, false, "必备段缺席：" + missing.Trim());
            return false;
        }
        report.Note(FgbGate.SectionMissing, FgbGateClass.Structural, false, false, "七个必备段齐");
        return true;
    }

    private static bool ValidateSections(byte[] blob, ref Ctx c, FgbLoadReport report)
    {
        bool Stride(in Sec s, int width, string name, out string err)
        {
            err = "";
            if (!s.Present) return true;
            if (width > 0 && s.Length % width == 0) return true;
            err = name + " 段 " + s.Length + "B 不是记录宽 " + width + "B 的整倍数";
            return false;
        }

        string e;
        if (!Stride(in c.Comp, AbiLayout.FgbCompSize, "COMP", out e)
            || !Stride(in c.Plan, AbiLayout.FgbPlanSize, "PLAN", out e)
            || !Stride(in c.Cont, AbiLayout.FgbContSize, "CONT", out e)
            || !Stride(in c.Locl, AbiLayout.FgbLocalSize, "LOCL", out e)
            || !Stride(in c.Leaf, AbiLayout.FgbLeafSize, "LEAF", out e)
            || !Stride(in c.Segs, AbiLayout.FgbSegSize, "SEGS", out e)
            || !Stride(in c.Tref, AbiLayout.FgbTexRefSize, "TREF", out e)
            || !Stride(in c.Deps, AbiLayout.FgbDepSize, "DEPS", out e)
            || !Stride(in c.Ptch, AbiLayout.FgbPatchSize, "PTCH", out e)
            || !Stride(in c.Quad, Abi.QuadInstanceSize, "QUAD", out e)
            || !Stride(in c.Clip, Abi.ClipEntrySize, "CLIP", out e))
        {
            report.Note(FgbGate.SectionShape, FgbGateClass.Structural, true, false, e);
            return false;
        }

        // CONT 至少要有 0 号哨兵：contentRef 0 落它。
        if (c.Cont.Length < AbiLayout.FgbContSize)
        {
            report.Note(FgbGate.SectionShape, FgbGateClass.Structural, true, false, "CONT 段无 0 号哨兵记录");
            return false;
        }

        // STRT：头 + 条目 + 池的三段账必须严丝合缝，且每条条目的窗口落在池内。
        ReadOnlySpan<byte> strt = blob.AsSpan(c.Strt.Offset, c.Strt.Length);
        if (strt.Length < FgbStrtSection.HeaderBytes)
        {
            report.Note(FgbGate.SectionShape, FgbGateClass.Structural, true, false, "STRT 段短于段头");
            return false;
        }
        uint strCount = FgbRecordIo.ReadU32(strt, AbiLayout.FgbStrtHeaderCountOffset);
        uint poolBytes = FgbRecordIo.ReadU32(strt, AbiLayout.FgbStrtHeaderPoolBytesOffset);
        // 先门后算：两个计数都是外部输入，乘法在水位之内才不会溢 int。
        if (strCount > (uint)(strt.Length / AbiLayout.FgbStrtEntrySize) + 1u || poolBytes > (uint)strt.Length)
        {
            report.Note(FgbGate.SectionShape, FgbGateClass.Structural, true, false,
                "STRT count/poolBytes 越段长水位（" + strCount + "/" + poolBytes + " vs " + strt.Length + "B）");
            return false;
        }
        if (strt.Length != FgbStrtSection.PayloadBytes((int)strCount, (int)poolBytes))
        {
            report.Note(FgbGate.SectionShape, FgbGateClass.Structural, true, false,
                "STRT 段长 " + strt.Length + "B != 布局账 "
                + FgbStrtSection.PayloadBytes((int)strCount, (int)poolBytes) + "B");
            return false;
        }
        for (int i = 0; i < (int)strCount; i++)
        {
            int off = FgbStrtSection.EntryOffset(i);
            long so = FgbRecordIo.ReadU32(strt, off + AbiLayout.FgbStrtEntryOffsetOffset);
            long sl = FgbRecordIo.ReadU32(strt, off + AbiLayout.FgbStrtEntryLengthOffset);
            if (so > poolBytes || sl > poolBytes - so)
            {
                report.Note(FgbGate.SectionShape, FgbGateClass.Structural, true, false,
                    "STRT 条目[" + i + "] 窗口 [" + so + "," + (so + sl) + ") 越池 " + poolBytes + "B");
                return false;
            }
        }

        // CNST：四个计数 → 布局是纯函数，段长必须精确等于它。
        ReadOnlySpan<byte> cnst = blob.AsSpan(c.Cnst.Offset, c.Cnst.Length);
        if (cnst.Length < FgbCnstSection.HeaderBytes)
        {
            report.Note(FgbGate.SectionShape, FgbGateClass.Structural, true, false, "CNST 段短于段头");
            return false;
        }
        long op = FgbRecordIo.ReadU32(cnst, AbiLayout.FgbCnstHeaderOpCountOffset);
        long fan = FgbRecordIo.ReadU32(cnst, AbiLayout.FgbCnstHeaderFanCountOffset);
        long ix = FgbRecordIo.ReadU32(cnst, AbiLayout.FgbCnstHeaderIndexCountOffset);
        long mk = FgbRecordIo.ReadU32(cnst, AbiLayout.FgbCnstHeaderMaskCountOffset);
        long cap = cnst.Length;
        if (op * FgbCnstSection.OpBytes > cap || fan * FgbCnstSection.FanBytes > cap
            || ix * FgbCnstSection.IndexBytes > cap || mk > cap
            || cnst.Length != FgbCnstSection.PayloadBytes((int)op, (int)fan, (int)ix, (int)mk)
            || mk != fan)
        {
            report.Note(FgbGate.SectionShape, FgbGateClass.Structural, true, false,
                "CNST 四数组账不符：ops=" + op + " fans=" + fan + " idx=" + ix + " masks=" + mk
                + " 段长 " + cnst.Length + "B");
            return false;
        }
        c.CnstOps = (int)op; c.CnstFans = (int)fan; c.CnstIdx = (int)ix; c.CnstMasks = (int)mk;

        // 算子下标池里的值是**组件内**算子号，上界只能到本包算子总数；越界 = 求值时读别人的算子。
        ReadOnlySpan<ushort> pool = MemoryMarshal.Cast<byte, ushort>(
            cnst.Slice(FgbCnstSection.IndicesOffset(c.CnstOps, c.CnstFans), c.CnstIdx * FgbCnstSection.IndexBytes));
        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i] < c.CnstOps) continue;
            report.Note(FgbGate.SectionShape, FgbGateClass.Structural, true, false,
                "CNST 下标池[" + i + "] = " + pool[i] + " 越算子总数 " + c.CnstOps);
            return false;
        }

        report.Note(FgbGate.SectionShape, FgbGateClass.Structural, false, false,
            "十四段记录宽整除 + STRT/CNST 布局账相符");
        return true;
    }

    private static bool ValidateComponents(byte[] blob, ref Ctx c, FgbLoadReport report)
    {
        ReadOnlySpan<byte> nodePayload = blob.AsSpan(c.Node.Offset, c.Node.Length);
        if (!FgbNodeSection.TryView(nodePayload, out FgbNodeView nodes))
        {
            report.Note(FgbGate.NodeSectionShape, FgbGateClass.Structural, true, false,
                "NODE 段 " + c.Node.Length + "B 与「16B 头 + 逐列对齐」的精确尺寸不符");
            return false;
        }
        report.Note(FgbGate.NodeSectionShape, FgbGateClass.Structural, false, false,
            nodes.NodeCount + " 行 × " + Abi.NodeColumns.Length + " 列，尺寸精确");

        int comps = c.Comp.Length / AbiLayout.FgbCompSize;
        int quads = c.Quad.Present ? c.Quad.Length / Abi.QuadInstanceSize : 0;
        int segs = c.Segs.Present ? c.Segs.Length / AbiLayout.FgbSegSize : 0;
        int leaves = c.Leaf.Present ? c.Leaf.Length / AbiLayout.FgbLeafSize : 0;
        int clips = c.Clip.Present ? c.Clip.Length / Abi.ClipEntrySize : 0;
        int locals = c.Locl.Length / AbiLayout.FgbLocalSize;
        int conts = c.Cont.Length / AbiLayout.FgbContSize;
        int steps = c.Plan.Length / AbiLayout.FgbPlanSize;

        bool Bad(string why)
        {
            report.Note(FgbGate.CompRange, FgbGateClass.Structural, true, false, why);
            return false;
        }

        // 组件下标要能塞进模板 id 的低 16 位（slab bin 键的编号面）。
        if (comps > 0xFFFF) return Bad("COMP 段 " + comps + " 条越模板 id 的 16 位编号面");

        int running = 0;
        for (int i = 0; i < comps; i++)
        {
            var d = new FgbComponentDef(blob.AsSpan(c.Comp.Offset + i * AbiLayout.FgbCompSize, AbiLayout.FgbCompSize));
            if (d.NodeCount <= 0 || d.NodeCount > FgbNodeSection.MaxNodeCount)
                return Bad("COMP[" + i + "] nodeCount " + d.NodeCount + " 非法");
            // 包级 NODE 是**全部组件拼成的一张扁平表**：区间必须首尾相接且收口于总行数。
            if (d.NodeStart != running) return Bad("COMP[" + i + "] nodeStart " + d.NodeStart + " != 前缀和 " + running);
            running += d.NodeCount;
            if (running > nodes.NodeCount) return Bad("COMP[" + i + "] 节点区间越 NODE 总行数 " + nodes.NodeCount);
            // LOCL / FanOut 桶 / 掩码逐节点一条 ⇒ 起点必等于 NODE 行起点（读侧就是这样直接下标寻址的）。
            if (d.LocalStart != d.NodeStart || d.LocalCount != d.NodeCount)
                return Bad("COMP[" + i + "] LOCL 区间与 NODE 行区间不同源");
            if (d.CnstFanStart != d.NodeStart)
                return Bad("COMP[" + i + "] cnstFanStart " + d.CnstFanStart + " != nodeStart " + d.NodeStart);
            if (d.LocalStart + d.LocalCount > locals) return Bad("COMP[" + i + "] LOCL 区间越段");
            if (d.CnstFanStart + d.NodeCount > c.CnstFans) return Bad("COMP[" + i + "] FanOut 桶区间越段");
            if (d.CnstOpStart < 0 || d.CnstOpCount < 0 || d.CnstOpStart + d.CnstOpCount > c.CnstOps)
                return Bad("COMP[" + i + "] 算子区间越段");
            if (d.CnstIdxStart < 0 || d.CnstIdxCount < 0 || d.CnstIdxStart + d.CnstIdxCount > c.CnstIdx)
                return Bad("COMP[" + i + "] 下标池区间越段");
            if (d.QuadStart < 0 || d.QuadCount < 0 || d.QuadStart + d.QuadCount > quads)
                return Bad("COMP[" + i + "] QUAD 区间越段");
            if (d.SegStart < 0 || d.SegCount < 0 || d.SegStart + d.SegCount > segs)
                return Bad("COMP[" + i + "] SEGS 区间越段");
            if (d.LeafStart < 0 || d.LeafCount < 0 || d.LeafStart + d.LeafCount > leaves)
                return Bad("COMP[" + i + "] LEAF 区间越段");
            if (d.ClipStart < 0 || d.ClipCount < 0 || d.ClipStart + d.ClipCount > clips)
                return Bad("COMP[" + i + "] CLIP 区间越段");
            if (d.PlanCount < 1 || d.PlanStart < 0 || d.PlanStart + d.PlanCount > steps)
                return Bad("COMP[" + i + "] PLAN 区间越段");
            if (d.InstanceBytes < (uint)Abi.FgbInstanceHeaderBytes || d.InstanceBytes > MaxInstanceBytes)
                return Bad("COMP[" + i + "] instanceBytes " + d.InstanceBytes + " 越 ["
                    + Abi.FgbInstanceHeaderBytes + ", " + MaxInstanceBytes + "] 水位"
                    + "（先门后分配：这个数直接决定一次 new byte[]）");

            // 逐行检查：localId 必须就是行内序号（ChildByLocalId 的 nodeBase + localId 寻址靠它），
            // 拓扑列的**组件内 1 基**值必须 ≤ nodeCount（这条就是「memcpy + 基址回填不会越出本块」
            // 的装载期保证——少了它，一个被改坏的 parentRel 会在回填后指到别的实例的槽上）。
            ReadOnlySpan<byte> lid = nodes.Column(AbiLayout.NodeColLocalId)
                .Slice(d.NodeStart * AbiLayout.NodeColLocalIdSize, d.NodeCount * AbiLayout.NodeColLocalIdSize);
            ReadOnlySpan<ushort> lidv = MemoryMarshal.Cast<byte, ushort>(lid);
            for (int k = 0; k < d.NodeCount; k++)
                if (lidv[k] != k) return Bad("COMP[" + i + "] 行 " + k + " 的 localId = " + lidv[k] + " != 行内序号");

            for (int col = 0; col < Abi.NodeColumns.Length; col++)
            {
                if (!Abi.NodeColumns[col].Rebase) continue;
                ReadOnlySpan<uint> v = MemoryMarshal.Cast<byte, uint>(
                    nodes.Column(col).Slice(d.NodeStart * 4, d.NodeCount * 4));
                for (int k = 0; k < d.NodeCount; k++)
                    if (v[k] > (uint)d.NodeCount)
                        return Bad("COMP[" + i + "] 列 " + Abi.NodeColumns[col].Name + " 行 " + k
                            + " 的相对下标 " + v[k] + " 越本组件 " + d.NodeCount + " 行");
            }

            ReadOnlySpan<uint> cref = MemoryMarshal.Cast<byte, uint>(
                nodes.Column(AbiLayout.NodeColContentRef).Slice(d.NodeStart * 4, d.NodeCount * 4));
            for (int k = 0; k < d.NodeCount; k++)
                if (cref[k] >= (uint)conts)
                    return Bad("COMP[" + i + "] 行 " + k + " 的 contentRef " + cref[k] + " 越 CONT 段 " + conts + " 条");

            // CNST 的三处**组件内相对**编号必须落在本组件的切片内。段级的界（下标池 < 包内算子总数）
            // 已经在 ValidateSections 验过，但那一道拦不住「指到隔壁组件的算子」——
            // 求值现场按组件切片直接下标寻址，越界在那里是数组越界异常，不是错画面。
            if (d.CnstOpCount > 0)
            {
                ReadOnlySpan<byte> cn = blob.AsSpan(c.Cnst.Offset, c.Cnst.Length);
                ReadOnlySpan<ConstraintOp> ops = MemoryMarshal.Cast<byte, ConstraintOp>(
                    cn.Slice(FgbCnstSection.OpsOffset + d.CnstOpStart * FgbCnstSection.OpBytes,
                        d.CnstOpCount * FgbCnstSection.OpBytes));
                for (int k = 0; k < ops.Length; k++)
                {
                    if (ops[k].SrcNode >= (ushort)d.NodeCount || ops[k].DstNode >= (ushort)d.NodeCount)
                        return Bad("COMP[" + i + "] 算子 " + k + " 的 src/dst 局部 id 越 " + d.NodeCount + " 个节点");
                    // Next 是「伙伴算子号 + 1」（0 = 无伙伴），求值现场按 Next−1 直接下标。
                    if (ops[k].Next != 0 && ops[k].Next - 1 >= ops.Length)
                        return Bad("COMP[" + i + "] 算子 " + k + " 的伙伴链 " + ops[k].Next
                            + " 指向本组件 " + ops.Length + " 个算子之外");
                }
                ReadOnlySpan<FanOut> fans = MemoryMarshal.Cast<byte, FanOut>(
                    cn.Slice(FgbCnstSection.FansOffset(c.CnstOps) + d.CnstFanStart * FgbCnstSection.FanBytes,
                        d.NodeCount * FgbCnstSection.FanBytes));
                for (int k = 0; k < fans.Length; k++)
                    if (fans[k].Start + fans[k].Count > d.CnstIdxCount)
                        return Bad("COMP[" + i + "] FanOut[" + k + "] 区间 [" + fans[k].Start + ","
                            + (fans[k].Start + fans[k].Count) + ") 越本组件下标池 " + d.CnstIdxCount);
                ReadOnlySpan<ushort> pool = MemoryMarshal.Cast<byte, ushort>(
                    cn.Slice(FgbCnstSection.IndicesOffset(c.CnstOps, c.CnstFans)
                        + d.CnstIdxStart * FgbCnstSection.IndexBytes,
                        d.CnstIdxCount * FgbCnstSection.IndexBytes));
                for (int k = 0; k < pool.Length; k++)
                    if (pool[k] >= (ushort)d.CnstOpCount)
                        return Bad("COMP[" + i + "] 下标池[" + k + "] = " + pool[k] + " 越本组件算子数 " + d.CnstOpCount);
            }
        }
        if (running != nodes.NodeCount)
            return Bad("COMP 的 nodeCount 之和 " + running + " != NODE 段 " + nodes.NodeCount + " 行");

        // 拓扑必须是一棵**以局部 0 为根的树**。这条不是洁癖：memcpy 出来的链会被 P6 的
        // paintOrder 展开直接走，一个不闭合的兄弟环或一条自环父指针在那里是**死循环**，
        // 不是错画面。装载器是信任边界的最后一层，环路只能死在这里。
        for (int i = 0; i < comps; i++)
        {
            var d = new FgbComponentDef(blob.AsSpan(c.Comp.Offset + i * AbiLayout.FgbCompSize, AbiLayout.FgbCompSize));
            if (!ValidateTopology(in nodes, in d, out string terr))
            {
                report.Note(FgbGate.NodeTopology, FgbGateClass.Structural, true, false, "COMP[" + i + "] " + terr);
                return false;
            }
        }
        report.Note(FgbGate.NodeTopology, FgbGateClass.Structural, false, false,
            comps + " 个模板的拓扑列构成以局部 0 为根的树（父/首子/兄弟环三方自洽 + 自根可达全部节点）");

        // LEAF 的 contentRef 同样只能落在 CONT 段内（降级路径读它）。
        for (int i = 0; i < leaves; i++)
        {
            uint cr = FgbRecordIo.ReadU32(
                blob.AsSpan(c.Leaf.Offset + i * AbiLayout.FgbLeafSize, AbiLayout.FgbLeafSize),
                AbiLayout.FgbLeafContentRefOffset);
            if (cr >= (uint)conts) return Bad("LEAF[" + i + "] contentRef " + cr + " 越 CONT 段");
        }

        report.Note(FgbGate.CompRange, FgbGateClass.Structural, false, false,
            comps + " 个模板的九组区间在界 + 逐行 localId/相对下标/contentRef 在界");
        return true;
    }

    /// <summary>实例块字节的水位（先门后分配：这个数是一次 <c>new byte[]</c> 的长度）。</summary>
    private const uint MaxInstanceBytes = 1u << 20;

    /// <summary>
    /// 一个模板的拓扑三列自洽：父列 → 子数；首子 + 兄弟环走一遍必须恰好覆盖那些子且闭合；
    /// 最后自根 DFS 必须**恰好**触达全部 n 个节点（这一条把「互为父子」一类环路挡在外面）。
    /// 全程 O(n)、无递归（DFS 用显式栈——被改坏的深链不该把装载器爆栈）。
    /// </summary>
    private static bool ValidateTopology(in FgbNodeView nodes, in FgbComponentDef d, out string error)
    {
        int n = d.NodeCount;
        ReadOnlySpan<uint> parent = MemoryMarshal.Cast<byte, uint>(
            nodes.Column(AbiLayout.NodeColParent).Slice(d.NodeStart * 4, n * 4));
        ReadOnlySpan<uint> first = MemoryMarshal.Cast<byte, uint>(
            nodes.Column(AbiLayout.NodeColFirstChild).Slice(d.NodeStart * 4, n * 4));
        ReadOnlySpan<uint> next = MemoryMarshal.Cast<byte, uint>(
            nodes.Column(AbiLayout.NodeColNextSib).Slice(d.NodeStart * 4, n * 4));
        ReadOnlySpan<uint> prev = MemoryMarshal.Cast<byte, uint>(
            nodes.Column(AbiLayout.NodeColPrevSib).Slice(d.NodeStart * 4, n * 4));

        error = "";
        if (parent[0] != 0u || next[0] != 0u || prev[0] != 0u)
        {
            error = "局部 0 必须是根（parent/nextSib/prevSib 全为哨兵 0）";
            return false;
        }
        var childCount = new int[n];
        for (int l = 1; l < n; l++)
        {
            uint p = parent[l];
            if (p == 0u) { error = "局部 " + l + " 无父：模板里只有局部 0 可以没有父"; return false; }
            childCount[p - 1]++;
        }
        var seen = new bool[n];
        for (int p = 0; p < n; p++)
        {
            if (childCount[p] == 0)
            {
                if (first[p] != 0u) { error = "局部 " + p + " 无子却有 firstChild"; return false; }
                continue;
            }
            if (first[p] == 0u) { error = "局部 " + p + " 有 " + childCount[p] + " 个子却无 firstChild"; return false; }
            int head = (int)first[p] - 1;
            int cur = head;
            for (int k = 0; k < childCount[p]; k++)
            {
                if (seen[cur]) { error = "局部 " + cur + " 出现在两条兄弟环里"; return false; }
                seen[cur] = true;
                if (parent[cur] != (uint)(p + 1)) { error = "局部 " + cur + " 在 " + p + " 的环里但父不是它"; return false; }
                uint nx = next[cur];
                if (nx == 0u) { error = "局部 " + cur + " 的 nextSib 是哨兵：兄弟链是**环**，不许断"; return false; }
                if (prev[(int)nx - 1] != (uint)(cur + 1)) { error = "局部 " + cur + " 的 next/prev 不互逆"; return false; }
                cur = (int)nx - 1;
            }
            if (cur != head) { error = "局部 " + p + " 的兄弟环长度与子数 " + childCount[p] + " 不符"; return false; }
        }
        if (seen[0]) { error = "局部 0 出现在某条兄弟环里（根不该是任何人的子）"; return false; }

        // 自根 DFS：环路会让某些节点根本走不到，故判据是「恰好 n 个」。
        var stack = new int[n];
        var reached = new bool[n];
        int top = 0, count = 0;
        stack[top++] = 0;
        reached[0] = true;
        while (top > 0)
        {
            int node = stack[--top];
            count++;
            uint fc = first[node];
            if (fc == 0u) continue;
            int head = (int)fc - 1;
            int cur = head;
            do
            {
                if (!reached[cur])
                {
                    reached[cur] = true;
                    if (top >= n) { error = "DFS 栈溢出：拓扑不是树"; return false; }
                    stack[top++] = cur;
                }
                cur = (int)next[cur] - 1;
            } while (cur != head);
        }
        if (count != n) { error = "自根只触达 " + count + "/" + n + " 个节点（拓扑有环或有孤岛）"; return false; }
        return true;
    }

    private static bool ValidatePlan(byte[] blob, ref Ctx c, FgbLoadReport report)
    {
        int comps = c.Comp.Length / AbiLayout.FgbCompSize;
        int steps = c.Plan.Length / AbiLayout.FgbPlanSize;
        var nodeCounts = new int[comps];
        var planStart = new int[comps];
        var planCount = new int[comps];
        for (int i = 0; i < comps; i++)
        {
            var d = new FgbComponentDef(blob.AsSpan(c.Comp.Offset + i * AbiLayout.FgbCompSize, AbiLayout.FgbCompSize));
            nodeCounts[i] = d.NodeCount;
            planStart[i] = d.PlanStart;
            planCount[i] = d.PlanCount;
        }

        bool Bad(string why)
        {
            report.Note(FgbGate.PlanShape, FgbGateClass.Structural, true, false, why);
            return false;
        }

        var all = new FgbPlanStep[steps];
        for (int i = 0; i < steps; i++)
        {
            all[i] = new FgbPlanStep(blob.AsSpan(c.Plan.Offset + i * AbiLayout.FgbPlanSize, AbiLayout.FgbPlanSize));
            if ((uint)all[i].CompIndex >= (uint)comps) return Bad("PLAN[" + i + "] compIndex 越 COMP 段");
            if (all[i].Kind != Abi.FgbPlanKindRoot && all[i].Kind != Abi.FgbPlanKindNested)
                return Bad("PLAN[" + i + "] kind " + all[i].Kind + " 不在封闭集内");
        }

        for (int ci = 0; ci < comps; ci++)
        {
            int lo = planStart[ci], hi = planStart[ci] + planCount[ci];
            for (int i = lo; i < hi; i++)
            {
                FgbPlanStep s = all[i];
                bool last = i == hi - 1;
                if (last)
                {
                    // 后序 ⇒ 组件自己的顶层步在末位，且没有宿主。
                    if (!s.IsRoot || s.CompIndex != ci || s.ParentStep != Abi.FgbPlanNoParent)
                        return Bad("PLAN[" + i + "] 应为 COMP[" + ci + "] 的顶层步（末位、kind=root、无宿主）");
                    continue;
                }
                if (s.IsRoot) return Bad("PLAN[" + i + "] 顶层步出现在组件计划中段（后序性质要求它在末位）");
                // **不变量 9**：引用的子步下标 < 自身 ⇔ 宿主步下标 > 自身，且宿主在同一份计划内。
                if (s.ParentStep <= (uint)i || s.ParentStep >= (uint)hi)
                    return Bad("PLAN[" + i + "] parentStep " + s.ParentStep + " 破坏后序性质或越出本计划 ["
                        + lo + "," + hi + ")");
                int hostComp = all[s.ParentStep].CompIndex;
                if (s.HostLocalId >= (ushort)nodeCounts[hostComp])
                    return Bad("PLAN[" + i + "] hostLocalId " + s.HostLocalId + " 越宿主 COMP["
                        + hostComp + "] 的 " + nodeCounts[hostComp] + " 个局部 id");
            }
        }
        report.Note(FgbGate.PlanShape, FgbGateClass.Structural, false, false,
            steps + " 步：后序性质成立、compIndex/hostLocalId 在界（不变量 9）");
        return true;
    }

    private static bool ValidatePatches(byte[] blob, ref Ctx c, FgbLoadReport report)
    {
        int trefs = c.Tref.Present ? c.Tref.Length / AbiLayout.FgbTexRefSize : 0;
        int conts = c.Cont.Length / AbiLayout.FgbContSize;
        int segs = c.Segs.Present ? c.Segs.Length / AbiLayout.FgbSegSize : 0;
        int patches = c.Ptch.Present ? c.Ptch.Length / AbiLayout.FgbPatchSize : 0;

        for (int i = 0; i < patches; i++)
        {
            ReadOnlySpan<byte> rec = blob.AsSpan(c.Ptch.Offset + i * AbiLayout.FgbPatchSize, AbiLayout.FgbPatchSize);
            uint texRef = FgbRecordIo.ReadU32(rec, AbiLayout.FgbPatchTexRefOffset);
            uint target = FgbRecordIo.ReadU32(rec, AbiLayout.FgbPatchTargetOffset);
            ushort section = FgbRecordIo.ReadU16(rec, AbiLayout.FgbPatchSectionOffset);
            ushort slot = FgbRecordIo.ReadU16(rec, AbiLayout.FgbPatchSlotOffset);
            string? why = null;
            if (texRef >= (uint)trefs) why = "texRef " + texRef + " 越 TREF 段 " + trefs + " 条";
            else if (section == Abi.FgbPatchSectionCont)
            {
                if (target == 0u || target >= (uint)conts) why = "CONT 目标 " + target + " 越段或落在 0 号哨兵";
                else if (slot != 0) why = "CONT 目标的 slot 必须为 0";
            }
            else if (section == Abi.FgbPatchSectionSegs)
            {
                if (target >= (uint)segs) why = "SEGS 目标 " + target + " 越段 " + segs + " 条";
                else if (slot >= Abi.SegmentMaxTextures) why = "SEGS 纹理槽 " + slot + " 越 " + Abi.SegmentMaxTextures;
            }
            else why = "section " + section + " 不在封闭集内";

            if (why != null)
            {
                report.Note(FgbGate.PatchRange, FgbGateClass.Structural, true, false, "PTCH[" + i + "] " + why);
                return false;
            }
        }
        report.Note(FgbGate.PatchRange, FgbGateClass.Structural, false, false, patches + " 条回填目标在界");
        return true;
    }

    /// <summary>
    /// 门 4（DEPS 逐项 + combinedRefHash 重算 + 档位比对）与门 5（文件名身份）。
    /// **分档在这里落地**：门 4 全部是哈希级失配 ⇒ 降级 + 计数；门 5 是部署错配 ⇒ 拒载。
    /// </summary>
    private static bool GateIdentity(FgbBlobView view, byte[] blob, ref Ctx c,
        in FgbLoadOptions options, FgbLoadReport report)
    {
        // ── 门 5：文件名以 id 定身份 ──────────────────────────────────────────
        if (string.IsNullOrEmpty(options.FileName))
        {
            report.Note(FgbGate.FileName, FgbGateClass.Structural, false, true, "调用方未给文件名");
        }
        else if (!FgbFileName.TryParse(options.FileName!, out FgbFileName fn, out string fnErr))
        {
            report.Note(FgbGate.FileName, FgbGateClass.Structural, true, false, fnErr);
            return false;
        }
        else
        {
            ulong idHash = FnvHash.Hash64(fn.Id);
            if (idHash != view.PkgId)
            {
                report.Note(FgbGate.FileName, FgbGateClass.Structural, true, false,
                    "文件名 id '" + fn.Id + "'（0x" + idHash.ToString("X16") + "）!= 头内 pkgId 0x"
                    + view.PkgId.ToString("X16"));
                return false;
            }
            if (fn.ScaleLevel != view.ScaleLevel || fn.BranchId != view.BranchId)
            {
                report.Note(FgbGate.FileName, FgbGateClass.Structural, true, false,
                    "文件名变体 .s" + fn.ScaleLevel + ".b" + fn.BranchId + " != 头内 .s"
                    + view.ScaleLevel + ".b" + view.BranchId);
                return false;
            }
            report.Note(FgbGate.FileName, FgbGateClass.Structural, false, false, "id/sN/bX 与头内一致");
        }

        // ── 门 4a：档位与宿主全局设定 ────────────────────────────────────────
        if (options.ExpectedScaleLevel < 0 && options.ExpectedBranchId < 0)
        {
            report.Note(FgbGate.IdentityMismatch, FgbGateClass.Degrade, false, true, "调用方未给全局档位设定");
        }
        else
        {
            bool bad = (options.ExpectedScaleLevel >= 0 && options.ExpectedScaleLevel != view.ScaleLevel)
                || (options.ExpectedBranchId >= 0 && options.ExpectedBranchId != view.BranchId);
            report.Note(FgbGate.IdentityMismatch, FgbGateClass.Degrade, bad, false,
                bad ? "头内 .s" + view.ScaleLevel + ".b" + view.BranchId + " != 全局设定 .s"
                      + options.ExpectedScaleLevel + ".b" + options.ExpectedBranchId + "（组件级降回运行时 Extract）"
                    : "档位与全局设定一致");
        }

        // ── 门 4b：DEPS 逐项 + combinedRefHash **重算** ───────────────────────
        int deps = c.Deps.Present ? c.Deps.Length / AbiLayout.FgbDepSize : 0;
        report.DepCount = deps;
        if (deps == 0)
        {
            report.Note(FgbGate.DepsMismatch, FgbGateClass.Degrade, false, true, "无跨包依赖");
            return true;
        }

        int unverified = 0, missing = 0, mismatch = 0;
        ulong chain = FnvHash.OffsetBasis64;
        for (int i = 0; i < deps; i++)
        {
            ReadOnlySpan<byte> rec = blob.AsSpan(c.Deps.Offset + i * AbiLayout.FgbDepSize, AbiLayout.FgbDepSize);
            ulong pkgId = FgbRecordIo.ReadU64(rec, AbiLayout.FgbDepPkgIdOffset);
            ulong expected = FgbRecordIo.ReadU64(rec, AbiLayout.FgbDepExpectedSourceHashOffset);
            ulong actual = 0ul;
            bool present = options.Packages != null && options.Packages.TryGetSourceHash(pkgId, out actual);
            if (expected == 0ul)
            {
                // 「未知」不等于「不符」：单包编译面给不出被引用包的 sourceHash（FGM304）。
                unverified++;
            }
            else if (!present) missing++;
            else if (actual != expected) mismatch++;
            chain = FnvHash.Hash64Continue(chain, Bytes8(present ? actual : expected));
        }
        report.DepsUnverified = unverified;

        ulong headCombined = view.CombinedRefHash;
        bool combinedBad = headCombined != 0ul && headCombined != chain;
        bool bad4 = missing > 0 || mismatch > 0 || combinedBad;
        report.Note(FgbGate.DepsMismatch, FgbGateClass.Degrade, bad4, false,
            "deps=" + deps + " unverified=" + unverified + " missing=" + missing + " mismatch=" + mismatch
            + (combinedBad ? " combinedRefHash 重算不符" : headCombined == 0ul ? " combinedRefHash 头内为零（单包编译面）" : ""));
        return true;
    }

    private static byte[] Bytes8(ulong v)
    {
        var b = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        return b;
    }

    /// <summary>
    /// 绑定（三操作之 ③）：TREF → 运行期纹理编号，PTCH → **就地**把包内编号换成运行期编号。
    /// 这里没有像素、没有对象图——只有 O(TREF 数 + patch 数) 次整数写。
    /// </summary>
    private void Bind(FgbLoadReport report)
    {
        int patches = _ptch.Present ? _ptch.Length / AbiLayout.FgbPatchSize : 0;
        for (int i = 0; i < patches; i++)
        {
            ReadOnlySpan<byte> rec = _blob.AsSpan(_ptch.Offset + i * AbiLayout.FgbPatchSize, AbiLayout.FgbPatchSize);
            int texRef = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbPatchTexRefOffset);
            int target = (int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbPatchTargetOffset);
            ushort section = FgbRecordIo.ReadU16(rec, AbiLayout.FgbPatchSectionOffset);
            ushort slot = FgbRecordIo.ReadU16(rec, AbiLayout.FgbPatchSlotOffset);
            uint runtimeId = _texRuntimeIds[texRef];
            if (section == Abi.FgbPatchSectionCont)
            {
                FgbRecordIo.U32(_blob.AsSpan(_cont.Offset + target * AbiLayout.FgbContSize, AbiLayout.FgbContSize),
                    AbiLayout.FgbContTexIdOffset, runtimeId);
            }
            else
            {
                int off = slot == 0 ? AbiLayout.FgbSegTex0Offset
                    : slot == 1 ? AbiLayout.FgbSegTex1Offset
                    : slot == 2 ? AbiLayout.FgbSegTex2Offset : AbiLayout.FgbSegTex3Offset;
                FgbRecordIo.U32(_blob.AsSpan(_segs.Offset + target * AbiLayout.FgbSegSize, AbiLayout.FgbSegSize),
                    off, runtimeId);
            }
        }
        report.PatchCount = patches;
    }

    /// <summary>TREF 解码 + 符号绑定（<see cref="Bind"/> 之前跑，回填要用它的结果）。</summary>
    private static void BindTextures(byte[] blob, ref Ctx c)
    {
        int n = c.Tref.Present ? c.Tref.Length / AbiLayout.FgbTexRefSize : 0;
        var syms = new FgbTexRef[n];
        var ids = new uint[n];
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> rec = blob.AsSpan(c.Tref.Offset + i * AbiLayout.FgbTexRefSize, AbiLayout.FgbTexRefSize);
            ulong pkgId = FgbRecordIo.ReadU64(rec, AbiLayout.FgbTexRefPkgIdOffset);
            uint itemStr = FgbRecordIo.ReadU32(rec, AbiLayout.FgbTexRefItemStrOffset);
            ushort kind = FgbRecordIo.ReadU16(rec, AbiLayout.FgbTexRefKindOffset);
            ushort localTex = FgbRecordIo.ReadU16(rec, AbiLayout.FgbTexRefTexIdOffset);
            syms[i] = new FgbTexRef(pkgId, ReadString(blob, in c.Strt, itemStr), kind, localTex);
            ids[i] = c.Textures.TryResolve(in syms[i], out uint id) ? id : 0u;
        }
        c.TexSymbols = syms;
        c.TexRuntimeIds = ids;
    }

    private static string ReadString(byte[] blob, in Sec strt, uint index)
    {
        if (!strt.Present || index == 0) return string.Empty;
        ReadOnlySpan<byte> s = blob.AsSpan(strt.Offset, strt.Length);
        int count = (int)FgbRecordIo.ReadU32(s, AbiLayout.FgbStrtHeaderCountOffset);
        if (index >= (uint)count) return string.Empty;
        int e = FgbStrtSection.EntryOffset((int)index);
        int off = (int)FgbRecordIo.ReadU32(s, e + AbiLayout.FgbStrtEntryOffsetOffset);
        int len = (int)FgbRecordIo.ReadU32(s, e + AbiLayout.FgbStrtEntryLengthOffset);
        int pool = FgbStrtSection.PoolOffset(count);
        return Encoding.UTF8.GetString(s.Slice(pool + off, len).ToArray());
    }

    // ── CONT 解码 ───────────────────────────────────────────────────────────

    private ContentRecord DecodeContent(ReadOnlySpan<byte> rec)
    {
        ushort kind = FgbRecordIo.ReadU16(rec, AbiLayout.FgbContKindOffset);
        byte flags = FgbRecordIo.ReadU8(rec, AbiLayout.FgbContFlagsOffset);
        var r = new ContentRecord
        {
            Kind = (ExtractKind)kind,
            RenderEveryN = 1,
            OpensClip = (flags & (1 << 2)) != 0,
            Clip = ClipShape.Rect,
        };
        if (kind != (ushort)ExtractKind.Leaf) return r;

        if ((flags & (1 << 3)) != 0)
        {
            // 文本叶：M1 的文本样式表未进冻结面（M1-20b 明码边界）。按机制 4 —— 不产渲染单元 +
            // 计数，绝不把它当成一张贴图画出来（那才是「近似」，正是阶梯禁止的那一级）。
            TextLeavesUnbound++;
            r.Kind = ExtractKind.None;
            return r;
        }

        uint fillPack = FgbRecordIo.ReadU32(rec, AbiLayout.FgbContFillPackOffset);
        var leaf = new LeafSpec
        {
            Texture = new TexId(FgbRecordIo.ReadU32(rec, AbiLayout.FgbContTexIdOffset)),
            Blend = (BlendClass)FgbRecordIo.ReadU8(rec, AbiLayout.FgbContBlendOffset),
            BaseColor = FgbRecordIo.ReadU32(rec, AbiLayout.FgbContBaseColorOffset),
            EmitFlags = FgbRecordIo.ReadU32(rec, AbiLayout.FgbContEmitFlagsOffset),
            SlackHint = FgbRecordIo.ReadI32(rec, AbiLayout.FgbContSlackHintOffset),
            Region = new SpriteRegion
            {
                Uv = new Vector4(
                    FgbRecordIo.ReadF32(rec, AbiLayout.FgbContUvX0Offset),
                    FgbRecordIo.ReadF32(rec, AbiLayout.FgbContUvY0Offset),
                    FgbRecordIo.ReadF32(rec, AbiLayout.FgbContUvX1Offset),
                    FgbRecordIo.ReadF32(rec, AbiLayout.FgbContUvY1Offset)),
                SourceWidth = FgbRecordIo.ReadF32(rec, AbiLayout.FgbContSourceWidthOffset),
                SourceHeight = FgbRecordIo.ReadF32(rec, AbiLayout.FgbContSourceHeightOffset),
                Grid = new Vector4(
                    FgbRecordIo.ReadF32(rec, AbiLayout.FgbContGridXOffset),
                    FgbRecordIo.ReadF32(rec, AbiLayout.FgbContGridYOffset),
                    FgbRecordIo.ReadF32(rec, AbiLayout.FgbContGridWOffset),
                    FgbRecordIo.ReadF32(rec, AbiLayout.FgbContGridHOffset)),
                TileGridIndice = unchecked((int)FgbRecordIo.ReadU32(rec, AbiLayout.FgbContTileGridIndiceOffset)),
                ScaleByTile = (flags & (1 << 1)) != 0,
            },
            Fill = new RadialFillParams
            {
                Method = (FillMethod)(fillPack & 0xFFu),
                Origin = (byte)((fillPack >> 8) & 0xFFu),
                Clockwise = ((fillPack >> 16) & 1u) != 0u,
                Amount = FgbRecordIo.ReadF32(rec, AbiLayout.FgbContFillAmountOffset),
            },
            Text = null,
            TextRef = 0u,
        };
        r.Leaf = leaf;
        return r;
    }

}
