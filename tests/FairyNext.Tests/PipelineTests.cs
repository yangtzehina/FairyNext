using FairyNext.Backend.Mock;
using FairyNext.Contracts;
using FairyNext.Core;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-14 P6/P7/P8 排水打通用例（Program 的 partial 分片）——**第一条端到端渲染路径**。
///
/// 对齐 docs/architecture.md：
/// 平面二相位表 P6（paintOrder 脏父切片拼接 + world/worldVisual 沿脏子树一遍算 + 下行下钻）、
/// P7（五通道消化）、P8（序派生 + 合并区间上传）；
/// 平面三机制 3（五通道动作表）/ 10（面板粒度整流重编）/ 15（零脏帧短路 + 空转收据）；
/// 平面六不变量 1（**增量流 ≡ 全量重建流**，逐字节，差异按 Ch 定位）与 13（下行戳完备）。
///
/// 两条门在本包上线，各有正例与负例：
///  · **增量正确性门**：正例 = 一串编辑逐帧比对全绿；负例 = 用测试内的可控开关吞掉一条通道的排水
///    （<see cref="DropChannel"/>，不改产品代码），门必须红且指认到那条通道；
///  · **零脏帧空转收据**：静止若干帧后七条队列全零、后端零 draw、ticks 与 presents 分账。
/// </summary>
public static partial class Program
{
    private static void PipelineSuite()
    {
        // P6 结构定形：切片拼接
        SpliceEqualsFullRebuildElementwise();
        SpliceOnlyExpandsTheDirtySubtree();
        SpliceDegradesOverThreshold();
        SpliceDegradesOnRootOverflow();
        SpliceMovesSubtreeAndRetiresDroppedIndices();

        // P6 派生列：增量 vs 神谕
        DerivedIncrementMatchesOracle();
        DerivedMergesNestedDirtyRoots();
        DerivedOracleCatchesAMissingMark();

        // P6 下行通道
        DownStampSurvivesOutOfFrameWrites();
        DownChannelCascadesAndLandsOnLeaves();
        HiddenSubtreeIsNotDrilled();
        StructureGateCatchesAStaleHiddenLeaf();
        HiddenEditsLandAfterShow();

        // 面板根
        PanelRootLimitsRebuildToItsSubtree();

        // 接线（M1-14b-3：Attach 独占是硬检查）
        AttachIsExclusivePerKernel();

        // P7 五通道端到端
        ContentChannelRewritesInPlace();
        ContentChannelEscalatesOnSegmentKey();
        TransformChannelRestampsBakedLeaf();
        TransformChannelWritesSlotForRotatedLeaf();
        TransformTier2RestampPath();
        ColorChannelRecolorsFromBaseColor();
        VisibleChannelEscalatesToStructure();
        StructureChannelRebuildsOnce();

        // P7 原位改写的承重守卫（M1-14b-3：审计探针转正为常设回归）
        SharedSlotOwnerMoveEscalates();
        SlackShapeChangeEscalates();
        ClipCullFlipEscalates();

        // P8 提交
        SubmitOrderIsFixed();
        SortingOrderFollowsStructEpochOnly();
        MergedUploadIntervalStaysTight();
        MergedUploadStaysTightWithinASegment();

        // 门
        IncrementalGateGreenAcrossEdits();
        SlotRidingScrollStaysGreenAcrossFrames();
        ClippedCorpusKeepsGateGreen();
        ClipShapeDriftIsCaughtByGate();
        SpliceBackwardSubtreeMoveHoldsUnderFrameGate();
        DerivedOracleCatchesAStaleVisualCascade();
        IncrementalGateRedOnDroppedChannel();
        IncrementalGateLocatesContentChannel();
        ColorFieldIsAPureFunctionOfBaseColor();
        IdleFrameReceipt();
        CanonicalLocatorAgreesWithLayout();
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// M1-16 之前的布局占位。Layout 通道**必须有消费者**：没有消费者的通道不排水（明文纪律），
    /// 脏位永不清、队列跨帧留存，零脏帧就永远到不了。真实布局器在 P5 解出 rect 后经内部写口回写
    /// 并派生 Mark(Content|Transform, reason=LayoutDerived)；本占位不解 rect（authored 就是真值），
    /// 只补那次派生 Mark——少了它，「改宽高」这条路径在 P6 与 P7 都无人认领。
    /// </summary>
    private sealed class LayoutStub : IChannelDrain
    {
        public Ch Consumes => Ch.Layout;

        public void Drain(ref FrameContext ctx, Ch channel, ReadOnlySpan<NodeHandle> queue)
        {
            for (int i = 0; i < queue.Length; i++)
                ctx.Invalidation.Mark(queue[i], Ch.Content | Ch.Transform, InvalidateReason.LayoutDerived);
        }
    }

    /// <summary>
    /// 负例开关：**吞掉一条通道的排水**，其余原样转交。用来在测试内制造一次「漏标」——
    /// 产品代码一行不改，增量正确性门必须当场变红并指认是哪条通道。
    /// </summary>
    private sealed class DropChannel : IChannelDrain
    {
        private readonly IChannelDrain _inner;
        private readonly Ch _drop;

        internal DropChannel(IChannelDrain inner, Ch drop)
        {
            _inner = inner;
            _drop = drop;
        }

        public Ch Consumes => _inner.Consumes;

        public void Drain(ref FrameContext ctx, Ch channel, ReadOnlySpan<NodeHandle> queue)
        {
            if ((channel & _drop) != 0) return;
            _inner.Drain(ref ctx, channel, queue);
        }
    }

    /// <summary>一棵手建的树 + 内核 + 内容池 + 流 + 管线 + mock 后端 + 增量门。</summary>
    private sealed class PipeFixture
    {
        internal readonly NodeTable Table = new NodeTable(tree: 7);
        internal readonly Invalidation Inval;
        internal readonly UiKernel Kernel;
        internal readonly ContentTable Content = new ContentTable();
        internal readonly RenderStream Stream = new RenderStream("pipe-test");
        internal readonly MockBackend Backend = new MockBackend();
        internal readonly RenderPipeline Pipe;
        internal readonly IncrementalGate Gate;
        private FrameTime _time = FrameTime.First(0.016f, 0.016f);

        internal PipeFixture()
        {
            Inval = new Invalidation(Table);
            Kernel = new UiKernel(Table, Inval);
            Inval.Register(new LayoutStub());
            Pipe = new RenderPipeline(Kernel, Stream, Content, Backend) { DerivedOracle = true };
            Pipe.Attach();
            Gate = new IncrementalGate(Pipe);
            Backend.PhaseProbe = () => Kernel.CurrentPhase;   // 后端调用只该发生在 P7/P8
        }

        internal NodeHandle Leaf(in LeafSpec spec, float x, float y, float w, float h, NodeHandle parent = default)
        {
            NodeHandle n = Table.CreateNode(NodeType.Image);
            Table.SetContentRef(n, Content.AddLeaf(in spec));
            Table.SetPosition(n, x, y);
            Table.SetSize(n, w, h);
            Table.AddChild(parent.IsNone ? Table.Root : parent, n);
            return n;
        }

        internal NodeHandle Box(float x, float y, float w, float h, NodeHandle parent = default)
        {
            NodeHandle n = Table.CreateNode(NodeType.Component);
            Table.SetPosition(n, x, y);
            Table.SetSize(n, w, h);
            Table.AddChild(parent.IsNone ? Table.Root : parent, n);
            return n;
        }

        internal void Tick()
        {
            Kernel.Tick(in _time);
            _time = _time.Step(0.016f, 0.016f);
        }

        /// <summary>流结构不变量门（authored 父链判据）在 TickAndCheck 路径上累计的红灯数。</summary>
        internal int StructureGateFailures;
        internal string LastStructureGateError = string.Empty;

        internal IncrementalGateResult TickAndCheck()
        {
            Tick();
            // 第三方判据与增量门并跑：增量门的两条腿共享 Extract 与派生列，「两腿共享错误前提」
            // （2026-08 审计 CRITICAL：隐藏容器后后代叶仍被绘制）在恒等式下是绿的，
            // 只有独立爬 authored 位的这道门抓得住。红灯累计进 Sound()。
            if (!StreamStructureGate.Check(Table, Stream, out string structErr))
            {
                StructureGateFailures++;
                LastStructureGateError = structErr;
            }
            return Gate.Check();
        }

        /// <summary>七条上行队列是否都空（零脏帧的第一条判据）。</summary>
        internal bool AllQueuesEmpty()
        {
            for (int slot = 0; slot < ChMask.UpCount; slot++)
                if (Inval.QueueLength(ChMask.UpChannelAt(slot)) != 0) return false;
            return true;
        }

        /// <summary>
        /// 健全：后端没有协议违约、门七字段全零、派生列神谕与序神谕全绿。
        /// 不健全时把三份账直接打出来——门失败要能指认**哪条路径腐烂了**，不是一个 false。
        /// </summary>
        internal bool Sound()
        {
            bool structNow = StreamStructureGate.Check(Table, Stream, out string structErr);
            bool ok = Backend.Violations.Count == 0 && Backend.Gates.Pass && Pipe.DerivedOracleFailures == 0
                && Pipe.PaintOrderFailures == 0 && structNow && StructureGateFailures == 0;
            if (ok) return true;
            Console.WriteLine($"     [unsound] {Backend.Gates.Describe()} derivedFail={Pipe.DerivedOracleFailures}"
                + $" badNode={Pipe.DerivedBadIndex} paintOrderFail={Pipe.PaintOrderFailures}"
                + $" violations={Backend.Violations.Count}"
                + $" structGateFail={StructureGateFailures + (structNow ? 0 : 1)}");
            if (Pipe.PaintOrderFailures > 0) Console.WriteLine("     [paint-order] " + Pipe.LastPaintOrderError);
            if (!structNow) Console.WriteLine("     [struct-gate] " + structErr);
            else if (StructureGateFailures > 0) Console.WriteLine("     [struct-gate] " + LastStructureGateError);
            for (int i = 0; i < Backend.Violations.Count && i < 3; i++)
                Console.WriteLine("     [violation] " + Backend.Violations[i]);
            return false;
        }
    }

    private static LeafSpec PipeSolid(uint texture, uint color = 0xFFFFFFFFu) =>
        texture == 0
            ? LeafSpec.Solid(color)
            : LeafSpec.Image(new TexId(texture), SpriteRegion.Full(new Vector4(0f, 0f, 1f, 1f), 4f, 4f), color);

    /// <summary>指定混合类别的图片叶（混合**进段键**，于是它是切段的另一半判据）。</summary>
    private static LeafSpec PipeBlend(uint texture, BlendClass blend) =>
        LeafSpec.Image(new TexId(texture), SpriteRegion.Full(new Vector4(0f, 0f, 1f, 1f), 4f, 4f),
            0xFFFFFFFFu, blend);

    /// <summary>九宫格叶（<paramref name="left"/>=0 时左边框退化 ⇒ 6 片而不是 9 片）。</summary>
    private static LeafSpec PipeSliced(uint texture, float left, int slackHint)
    {
        var spec = LeafSpec.Image(new TexId(texture),
            SpriteRegion.Sliced(new Vector4(0f, 0f, 1f, 1f), 16f, 16f,
                new Vector4(left, 4f, 16f - left - 4f, 8f)));
        spec.SlackHint = slackHint;
        return spec;
    }

    // ── P6：paintOrder 切片拼接 ─────────────────────────────────────────────

    /// <summary>建一棵 1+4×(1+3) = 17 节点的树（两条腿按同一序列建，下标因此逐一对齐）。</summary>
    private static NodeTable SpliceTree(out NodeHandle[] kids)
    {
        var t = new NodeTable(tree: 5);
        kids = new NodeHandle[4];
        for (int i = 0; i < 4; i++)
        {
            kids[i] = t.CreateNode(NodeType.Component);
            t.AddChild(t.Root, kids[i]);
            for (int k = 0; k < 3; k++)
            {
                NodeHandle g = t.CreateNode(NodeType.Image);
                t.AddChild(kids[i], g);
            }
        }
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();
        return t;
    }

    private static void SpliceEqualsFullRebuildElementwise()
    {
        // 两棵按同一序列建的树 ⇒ 节点下标逐一对齐；施同一次结构编辑，
        // 一棵走切片拼接、一棵把阈值调 0 逼它整树重展，两份 paintOrder 必须逐元素相同。
        NodeTable a = SpliceTree(out NodeHandle[] ka);
        NodeTable b = SpliceTree(out NodeHandle[] kb);
        b.PaintSpliceMaxDirtyPercent = 0;

        Mutate(a, ka);
        Mutate(b, kb);
        a.Phase = FramePhase.P6_Settle;
        b.Phase = FramePhase.P6_Settle;
        int splicesBefore = a.PaintSplices;
        int fullsBefore = b.PaintFullRebuilds;
        a.ApplyStructure();
        b.ApplyStructure();

        ReadOnlySpan<int> oa = a.GetPaintOrder();
        ReadOnlySpan<int> ob = b.GetPaintOrder();
        bool same = oa.Length == ob.Length;
        for (int i = 0; same && i < oa.Length; i++) same = oa[i] == ob[i];
        bool gate = a.ValidatePaintOrder(out string err);
        Check("P6 切片拼接: 增量重展与整树重展逐元素相等 " + err,
            same && gate && a.PaintSplices == splicesBefore + 1 && b.PaintFullRebuilds == fullsBefore + 1);

        static void Mutate(NodeTable t, NodeHandle[] kids)
        {
            t.Phase = FramePhase.P3_Commands;
            NodeHandle extra = t.CreateNode(NodeType.Image);
            t.AddChild(kids[2], extra);                       // 尾巴整体后移一位
            t.SetChildIndex(t.ChildAt(kids[0], 0), 2);        // 同父内换序：长度不变
        }
    }

    private static void SpliceOnlyExpandsTheDirtySubtree()
    {
        NodeTable t = SpliceTree(out NodeHandle[] kids);
        t.Phase = FramePhase.P3_Commands;
        NodeHandle extra = t.CreateNode(NodeType.Image);
        t.AddChild(kids[3], extra);                            // 只脏最后一棵子树（4 个节点）
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();

        // 17 个旧元素里，脏切片是 kids[3] 的 4 个；其余 13 个原样搬运，展开只走 5 个节点。
        Check("P6 切片拼接: 只重新展开脏子树，其余原样搬运（成本正比于脏，不正比于树）",
            t.LastSpliceCopied == 13 && t.LastSpliceExpanded == 5
            && t.GetPaintOrder().Length == 18 && t.ValidatePaintOrder(out _));
    }

    private static void SpliceDegradesOverThreshold()
    {
        NodeTable t = SpliceTree(out NodeHandle[] kids);
        int fulls = t.PaintFullRebuilds;
        int splices = t.PaintSplices;

        t.Phase = FramePhase.P3_Commands;
        for (int i = 0; i < 3; i++)                            // 脏掉 3/4 的子树（12/17 > 50%）
        {
            NodeHandle extra = t.CreateNode(NodeType.Image);
            t.AddChild(kids[i], extra);
        }
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();
        bool degraded = t.PaintFullRebuilds == fulls + 1 && t.PaintSplices == splices;

        // 阈值之下的同款编辑仍走切片：退化是**判据**决定的，不是碰运气。
        t.Phase = FramePhase.P3_Commands;
        t.AddChild(kids[3], t.CreateNode(NodeType.Image));
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();

        Check("P6 切片拼接: 脏比例超阈值退化为整树重展（阈值内仍走切片）",
            degraded && t.PaintSplices == splices + 1 && t.ValidatePaintOrder(out _));
    }

    private static void SpliceDegradesOnRootOverflow()
    {
        var t = new NodeTable(tree: 6);
        var parents = new NodeHandle[NodeTable.PaintSpliceMaxRoots + 8];
        for (int i = 0; i < parents.Length; i++)
        {
            parents[i] = t.CreateNode(NodeType.Component);
            t.AddChild(t.Root, parents[i]);
            t.AddChild(parents[i], t.CreateNode(NodeType.Image));
        }
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();
        int fulls = t.PaintFullRebuilds;

        t.Phase = FramePhase.P3_Commands;
        for (int i = 0; i < parents.Length; i++) t.AddChild(parents[i], t.CreateNode(NodeType.Image));
        bool overflowed = t.StructDirtyOverflowed;
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();

        Check("P6 切片拼接: 脏父溢出即整树重展（簿记比 memcpy 贵时不装增量）",
            overflowed && t.PaintFullRebuilds == fulls + 1 && t.ValidatePaintOrder(out _));
    }

    private static void SpliceMovesSubtreeAndRetiresDroppedIndices()
    {
        NodeTable t = SpliceTree(out NodeHandle[] kids);
        NodeHandle moved = t.ChildAt(kids[0], 1);
        NodeHandle dropped = t.ChildAt(kids[1], 0);

        t.Phase = FramePhase.P3_Commands;
        t.AddChild(kids[3], moved);            // 子树搬家：旧位在 kids[0] 的切片里，新位在 kids[3] 的展开里
        t.RemoveFromParent(dropped);           // 脱链：反查必须回哨兵
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();

        Check("P6 切片拼接: 子树搬家两侧都对，脱链节点的 paintIndex 回哨兵",
            t.ValidatePaintOrder(out string err) && err.Length == 0
            && t.PaintIndexOf(dropped) == NodeTable.NotInTree
            && t.PaintIndexOf(moved) > t.PaintIndexOf(kids[3])
            && t.IsAlive(dropped));
    }

    // ── P6：world / worldVisual 增量 vs 神谕 ───────────────────────────────

    private static void DerivedIncrementMatchesOracle()
    {
        var t = new NodeTable(tree: 8);
        NodeHandle a = t.CreateNode(NodeType.Component);
        NodeHandle b = t.CreateNode(NodeType.Component);
        NodeHandle c = t.CreateNode(NodeType.Image);
        NodeHandle d = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, a);
        t.AddChild(a, b);
        t.AddChild(b, c);
        t.AddChild(t.Root, d);
        t.SetPosition(a, 10f, 20f);
        t.SetPosition(b, 3f, 4f);
        t.SetPosition(c, 1f, 2f);
        t.SetAlpha(a, 0.5f);
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();
        t.DrainDerivedFull();
        t.Phase = FramePhase.P3_Commands;

        t.SetPosition(a, 100f, 200f);          // 只脏 a 的子树
        t.SetScale(b, 2f, 3f);
        t.Phase = FramePhase.P6_Settle;
        int visited = t.DrainDerivedFrom(new[] { a, b });

        bool oracle = t.DerivedMatchesFullRecompute(out uint bad);
        Affine2D w = t.World(c);
        Check("P6 派生列: 增量重算与全量神谕逐位相等（world 复合 + worldVisual 级联）",
            oracle && bad == NodeTable.NoIndex && visited == 3
            && w.tx == 105f && w.m00 == 2f && Visual.Alpha(t.WorldVisual(c)) == 128);
    }

    private static void DerivedMergesNestedDirtyRoots()
    {
        var t = new NodeTable(tree: 9);
        NodeHandle a = t.CreateNode(NodeType.Component);
        NodeHandle b = t.CreateNode(NodeType.Component);
        NodeHandle c = t.CreateNode(NodeType.Image);
        NodeHandle d = t.CreateNode(NodeType.Component);
        NodeHandle e = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, a);
        t.AddChild(a, b);
        t.AddChild(b, c);
        t.AddChild(t.Root, d);
        t.AddChild(d, e);
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();
        t.DrainDerivedFull();
        long baseline = t.DerivedRecomputed;

        // 三个脏根：b 嵌在 a 的子树里（归并掉），d 独立。a 子树 3 + d 子树 2 = 5 个节点。
        t.SetPosition(a, 1f, 1f);
        t.SetPosition(b, 2f, 2f);
        t.SetPosition(d, 3f, 3f);
        int visited = t.DrainDerivedFrom(new[] { b, a, d });

        Check("P6 派生列: 嵌套脏根归并、多脏根各算一次（b ⊂ a ⇒ 5 个节点不是 8 个）",
            visited == 5 && t.DerivedRecomputed == baseline + 5
            && t.DerivedMatchesFullRecompute(out _));
    }

    private static void DerivedOracleCatchesAMissingMark()
    {
        var f = new PipeFixture();
        NodeHandle box = f.Box(10f, 10f, 50f, 50f);
        f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f, box);
        f.Tick();
        bool cleanFirst = f.Pipe.DerivedOracleFailures == 0;

        // 负例：摘掉失效钩子后写位置——列变了、Mark 没发生，P6 因此收不到脏根。
        // 「漏标」在增量架构里就是这个形状，派生列神谕必须当场抓住。
        f.Inval.Detach();
        f.Table.SetPosition(box, 999f, 999f);
        f.Tick();

        Check("P6 派生列: 神谕抓住漏标（摘钩子后写位置 ⇒ world 陈旧 ⇒ 门红并指名节点）",
            cleanFirst && f.Pipe.DerivedOracleFailures == 1
            && f.Pipe.DerivedBadIndex != NodeTable.NoIndex);
    }

    // ── P6：下行通道 ───────────────────────────────────────────────────────

    private static void DownStampSurvivesOutOfFrameWrites()
    {
        var t = new NodeTable(tree: 11);
        var inv = new Invalidation(t);
        NodeHandle a = t.CreateNode(NodeType.Component);
        NodeHandle b = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, a);
        t.AddChild(a, b);
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();

        inv.BeginFrame(1);
        inv.DrainDown((idx, bits) => { });          // 第一帧：空下钻，代号推进

        // **帧外**写（两次 Tick 之间，相位报 P3——用户代码最常见的窗口）。
        // 戳值若钉成 frameId，这一批就带着上一帧的号，下一帧 P6 从根第一步就判「本帧无事」，
        // 整条下行通道静默丢失。代号按「自上次下钻以来」取值才收得住。
        t.Phase = FramePhase.P3_Commands;
        t.SetAlpha(a, 0.5f);

        inv.BeginFrame(2);
        t.Phase = FramePhase.P6_Settle;
        var seen = new List<uint>();
        int visited = inv.DrainDown((idx, bits) => seen.Add(idx));

        Check("P6 下行: 帧外写入同样被下一次下钻收走（子树戳是下钻代，不是 frameId）",
            visited == 2 && seen.Contains(a.Index) && seen.Contains(b.Index)
            && !seen.Contains(t.Root.Index));
    }

    private static void DownChannelCascadesAndLandsOnLeaves()
    {
        var f = new PipeFixture();
        NodeHandle box = f.Box(0f, 0f, 50f, 50f);
        NodeHandle leaf = f.Leaf(PipeSolid(0, 0xFF204080u), 0f, 0f, 10f, 10f, box);
        f.Tick();
        uint before = f.Stream.Quads[0].Color;

        long downBefore = f.Pipe.DownVisits;
        f.Table.SetAlpha(box, 0.5f);             // 容器变半透：下行通道下钻 → 落叶 → P7 重上色
        IncrementalGateResult r = f.TickAndCheck();

        uint after = f.Stream.Quads[0].Color;
        Check("P6 下行: 容器 α 经子树戳下钻，级联落叶回上行队列，P7 从基色重算（rgb 不动）",
            before == 0xFF204080u && after == 0x80204080u
            && f.Pipe.DownVisits > downBefore
            && Visual.Alpha(f.Table.WorldVisual(leaf)) == 128
            && r.Pass && f.Sound());
    }

    private static void HiddenSubtreeIsNotDrilled()
    {
        var f = new PipeFixture();
        NodeHandle box = f.Box(0f, 0f, 50f, 50f);
        NodeHandle inner = f.Box(0f, 0f, 20f, 20f, box);
        f.Leaf(PipeSolid(0), 0f, 0f, 10f, 10f, inner);
        f.Tick();

        f.Table.SetVisible(box, false);          // 隐藏容器：整支出流（数量变化 ⇒ 整编）
        IncrementalGateResult hide = f.TickAndCheck();
        long skips = f.Inval.LastFrame.HiddenSkips;
        // 2026-08 审计（断言改强的那条）：这里从前只断言重新显示后的 LeafCount==1，隐藏那一步的
        // 叶数从未被断言——Extract 逐点判陈旧 worldVisual 时 leaves=1 照画、增量门照绿（两腿共读
        // 同一份陈旧列），本用例守着「整支出流」的注释却放行了整支照画。隐藏后必须 0 叶 0 quad。
        bool leftStream = f.Stream.LeafCount == 0 && f.Stream.QuadCount == 0;

        f.Table.SetVisible(box, true);           // 重新显示：Visible 排水补戳，整支回流
        IncrementalGateResult show = f.TickAndCheck();

        Check("P6 下行: 隐藏子树免下钻（契约），隐藏一步整支出流（0 叶），重新显示补戳后整支回流且门仍绿",
            hide.Pass && leftStream && show.Pass && skips >= 1
            && f.Stream.LeafCount == 1 && f.Sound());
    }

    /// <summary>
    /// 流结构不变量门的负例：制造一次真正的漏标（摘钩子后隐藏容器——Mark 丢失，流不重编，
    /// 叶成为被隐藏祖先罩住的幽灵），authored 父链判据必须当场红。它是独立于增量门两条腿的
    /// 第三方判据：两腿共享 Extract 时，这个形状恒等式恒真，只有本门抓得住。
    /// </summary>
    private static void StructureGateCatchesAStaleHiddenLeaf()
    {
        var f = new PipeFixture();
        NodeHandle box = f.Box(0f, 0f, 50f, 50f);
        f.Leaf(PipeSolid(0), 0f, 0f, 10f, 10f, box);
        f.Tick();
        bool green = StreamStructureGate.Check(f.Table, f.Stream, out _);

        f.Inval.Detach();                        // 漏标的机器形态：写发生、Mark 不发生
        f.Table.SetVisible(box, false);
        f.Tick();                                // 整帧照跑：无脏可排，流原样留着那个叶

        bool red = !StreamStructureGate.Check(f.Table, f.Stream, out string err);
        Check($"门 · 流结构不变量（负例）: 漏标的隐藏容器 ⇒ 残叶被 authored 父链判据抓住 {err}",
            green && red && f.Stream.LeafCount == 1);
    }

    /// <summary>
    /// M1-14b 端到端验收：隐藏容器 → 隐藏期间改子树内叶的 α → 重新显示。
    /// 一条链踩三处接缝：隐藏免下钻（脏位在隐藏子树里过夜，14b-2 的时间宪法修复保证它不搁浅）、
    /// 重新显示的 DownVisible 整支补钻、Extract 可见性父链传播（14b-1，隐藏期间叶不在流里）。
    /// 最终字节必须是隐藏期间写入的那个 α，全程增量门与派生列神谕绿。
    /// </summary>
    private static void HiddenEditsLandAfterShow()
    {
        var f = new PipeFixture();
        NodeHandle box = f.Box(0f, 0f, 50f, 50f);
        NodeHandle inner = f.Box(0f, 0f, 20f, 20f, box);
        NodeHandle x = f.Leaf(PipeSolid(0, 0xFF204080u), 0f, 0f, 10f, 10f, inner);
        f.Tick();
        bool baseOk = f.Stream.QuadCount == 1 && f.Stream.Quads[0].Color == 0xFF204080u;

        f.Table.SetVisible(box, false);
        IncrementalGateResult hide = f.TickAndCheck();
        bool gone = f.Stream.LeafCount == 0 && f.Stream.QuadCount == 0;

        f.Table.SetAlpha(x, 0.5f);                   // 隐藏期间写：级联值在隐藏子树里过夜
        IncrementalGateResult during = f.TickAndCheck();

        f.Table.SetVisible(box, true);               // 重新显示：DownVisible 整支补钻 + 整编回流
        IncrementalGateResult show = f.TickAndCheck();

        Check("端到端: 隐藏期间的 α 写在重新显示后落到字节（0x80204080），全程门绿",
            baseOk && hide.Pass && gone && during.Pass && show.Pass
            && f.Stream.LeafCount == 1 && f.Stream.Quads[0].Color == 0x80204080u
            && Visual.Alpha(f.Table.WorldVisual(x)) == 128 && f.Sound());
    }

    // ── 面板根 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 2026-08 审计 MEDIUM：Drain/InPanel 按面板子树过滤脏句柄，Rebuild 却沿 GetPaintOrder()
    /// 整树发射——每条「面板流」装整棵树。单面板时增量门的 oracle Extract 同样整树发射，
    /// 恒等式看不出来（两腿共享前提的又一例），必须用「兄弟子树不入流」直接断言。
    /// </summary>
    private static void PanelRootLimitsRebuildToItsSubtree()
    {
        var f = new PipeFixture();
        NodeHandle mine = f.Box(0f, 0f, 100f, 100f);
        NodeHandle a = f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f, mine);
        NodeHandle other = f.Box(200f, 0f, 100f, 100f);
        f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f, other);
        f.Leaf(PipeSolid(1), 20f, 0f, 10f, 10f, other);
        f.Pipe.Extract.PanelRoot = mine;
        IncrementalGateResult r = f.TickAndCheck();

        // IncrementalGate.Check 已同步 PanelRoot（两腿同面板），门下仍须绿。
        Check("面板根: Rebuild 只发射 PanelRoot 子树（兄弟子树的叶不进本流），增量门下仍绿",
            f.Stream.LeafCount == 1 && f.Stream.Leaf(0).Node.Equals(a)
            && r.Pass && f.Sound());
    }

    // ── 接线：Attach 独占 ──────────────────────────────────────────────────

    /// <summary>
    /// 2026-08 审计修复：Attach 用属性直赋接管四个相位钩子，同一内核挂第二条管线会**静默覆盖**——
    /// 第一条整帧停摆且其后端每帧 BeginFrame 永不 EndFrame，Debug 也不断言（旧检查只看本实例的
    /// <c>_attached</c>，跨实例不设防）。现在是无条件硬检查（throw，Release 也拦），Detach 释放后可重接。
    /// </summary>
    private static void AttachIsExclusivePerKernel()
    {
        var f = new PipeFixture();
        NodeHandle n = f.Leaf(PipeSolid(0, 0xFF204080u), 0f, 0f, 10f, 10f);
        f.Tick();

        var backend2 = new MockBackend();
        var pipe2 = new RenderPipeline(f.Kernel, new RenderStream("pipe-2"), f.Content, backend2);
        bool threw = false;
        try { pipe2.Attach(); }
        catch (InvalidOperationException) { threw = true; }

        // 抢挂被拒后第一条管线不受打扰：编辑照常落字节、门照常绿。
        f.Table.SetAlpha(n, 0.5f);
        bool firstIntact = f.TickAndCheck().Pass && f.Stream.Quads[0].Color == 0x80204080u;

        // 同实例重复 Attach 同样必炸（静默 no-op 会骗过「谁在驱动」的判断）。
        bool rethrew = false;
        try { f.Pipe.Attach(); }
        catch (InvalidOperationException) { rethrew = true; }

        // Detach 释放钩子后可重接：换 pipe2 驱动同一棵树，走一帧就有完整帧括号（Begin/End 配平）。
        f.Pipe.Detach();
        pipe2.Attach();
        f.Tick();
        bool handedOver = backend2.Ticks == 1 && backend2.Violations.Count == 0;

        Check("接线: 第二条管线 Attach 同一内核必炸（硬检查，非 UiAssert），Detach 释放后可重接",
            threw && firstIntact && rethrew && handedOver && f.Sound());
    }

    // ── P7：五通道端到端 ───────────────────────────────────────────────────

    private static void ContentChannelRewritesInPlace()
    {
        var f = new PipeFixture();
        NodeHandle n = f.Leaf(PipeSliced(1, 4f, slackHint: 16), 0f, 0f, 40f, 40f);
        f.Tick();
        int quads = f.Stream.QuadCount;
        int rebuilds = f.Pipe.Extract.Rebuilds;
        f.Backend.ClearLog();

        // 换成「左边框为 0」的九宫格：切片数 9 → 6，但预留区仍是 16 ⇒ 原位重写，不整编。
        uint id = f.Content.AddLeaf(PipeSliced(1, 0f, slackHint: 16));
        f.Table.SetContentRef(n, id);
        IncrementalGateResult r = f.TickAndCheck();

        LeafRange leaf = f.Stream.Leaf(0);
        Check("P7 Content: 叶内原位重写（9 片 → 6 片仍在预留区内，不惊动邻叶也不整编）",
            quads == 16 && leaf.Count == 6 && leaf.Slack == 16
            && f.Pipe.Extract.Rebuilds == rebuilds
            && f.Pipe.Drain.ContentRewrites >= 1
            && f.Backend.CallCount(MockCallKind.UploadInstances) == 1
            && r.Pass && f.Sound());
    }

    private static void ContentChannelEscalatesOnSegmentKey()
    {
        var f = new PipeFixture();
        NodeHandle n = f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f);
        f.Tick();
        int rebuilds = f.Pipe.Extract.Rebuilds;

        f.Table.SetContentRef(n, f.Content.AddLeaf(PipeSolid(2)));   // 换纹理 = 换段键
        IncrementalGateResult r = f.TickAndCheck();

        Check("P7 Content: 段键变化不能原位改，升级 Structure 整编一次（同帧闭合）",
            f.Pipe.Extract.Rebuilds == rebuilds + 1
            && f.Pipe.Drain.CauseCount(EscalateCause.SegmentKey) == 1
            && f.Pipe.Escalated == 1 && r.Pass && f.Sound());
    }

    private static void TransformChannelRestampsBakedLeaf()
    {
        var f = new PipeFixture();
        NodeHandle n = f.Leaf(PipeSolid(0), 5f, 7f, 10f, 20f);
        f.Tick();
        int rebuilds = f.Pipe.Extract.Rebuilds;
        f.Backend.ClearLog();

        f.Table.SetPosition(n, 105f, 207f);
        IncrementalGateResult r = f.TickAndCheck();

        QuadInstance q = f.Stream.Quads[0];
        MockCall upload = f.Backend.LastCall(MockCallKind.UploadInstances);
        Check("P7 Transform: 轴对齐叶按新 world 重新落位（rect 更新、槽 0、一次区间上传、不整编）",
            q.Rect.x == 105f && q.Rect.y == 207f && q.SlotIndex == 0u
            && f.Pipe.Extract.Rebuilds == rebuilds
            && upload.Kind == MockCallKind.UploadInstances && upload.Count == 1
            && r.Pass && f.Sound());
    }

    private static void TransformChannelWritesSlotForRotatedLeaf()
    {
        var f = new PipeFixture();
        NodeHandle n = f.Leaf(PipeSolid(0), 5f, 5f, 10f, 10f);
        f.Table.SetRotation(n, 0.5f);
        f.Tick();
        int slot = (int)f.Stream.Quads[0].SlotIndex;
        int rebuilds = f.Pipe.Extract.Rebuilds;
        int writes = f.Pipe.Drain.SlotWrites;
        f.Backend.ClearLog();

        f.Table.SetPosition(n, 60f, 80f);
        IncrementalGateResult r = f.TickAndCheck();

        Affine2D m = f.Stream.Slots.Matrix(slot);
        Affine2D world = f.Table.World(n);
        Check("P7 Transform: 骑槽的叶只写槽矩阵一项（零重建），rect 留本地",
            slot != SlotTable.IdentitySlot && f.Pipe.Drain.SlotWrites == writes + 1
            && f.Pipe.Extract.Rebuilds == rebuilds
            && m.tx == world.tx && m.ty == world.ty
            && f.Stream.Quads[0].Rect.x == 0f
            && f.Backend.CallCount(MockCallKind.WriteSlots) == 1
            && r.Pass && f.Sound());
    }

    private static void TransformTier2RestampPath()
    {
        var f = new PipeFixture();
        NodeHandle n = f.Leaf(PipeSolid(0), 4f, 8f, 10f, 20f);
        f.Tick();
        f.Pipe.Drain.PreferTier2Restamp = true;    // 内容源不可复跑时的那条路（M1-18 文本叶 / M2 tween 小道）

        f.Table.SetPosition(n, 14f, 28f);
        f.Tick();
        QuadInstance moved = f.Stream.Quads[0];

        f.Table.SetRotation(n, 0.3f);              // 增量含旋转：rect 表达不了 ⇒ 升级 Structure
        f.Tick();

        Check("P7 Transform: tier-2 原位重 stamp 搬 rect；增量含旋转即升级（不硬算近似值）",
            moved.Rect.x == 14f && moved.Rect.y == 28f && moved.Rect.z == 10f
            && f.Pipe.Drain.Restamps == 1
            && f.Pipe.Drain.CauseCount(EscalateCause.SlotShape) >= 1
            && f.Stream.Quads[0].SlotIndex != 0u && f.Sound());
    }

    private static void ColorChannelRecolorsFromBaseColor()
    {
        var f = new PipeFixture();
        NodeHandle n = f.Leaf(PipeSolid(0, 0xFF3366AAu), 0f, 0f, 10f, 10f);
        f.Tick();
        uint baseColor = f.Stream.BaseColors[0];

        f.Table.SetAlpha(n, 0f);
        IncrementalGateResult zeroed = f.TickAndCheck();
        uint transparent = f.Stream.Quads[0].Color;

        f.Table.SetAlpha(n, 1f);
        IncrementalGateResult restored = f.TickAndCheck();

        // α 归零再恢复必须逐字节回到原色：基色是纯函数的定义域，不是被反复乘除的当前色。
        Check("P7 Color: 从未乘基色重算（α 归零再恢复不丢色，rgb 全程不动）",
            baseColor == 0xFF3366AAu && transparent == 0x003366AAu
            && f.Stream.Quads[0].Color == 0xFF3366AAu
            && f.Pipe.Drain.Recolors >= 2 && zeroed.Pass && restored.Pass && f.Sound());
    }

    private static void VisibleChannelEscalatesToStructure()
    {
        var f = new PipeFixture();
        f.Leaf(PipeSolid(0), 0f, 0f, 10f, 10f);
        NodeHandle b = f.Leaf(PipeSolid(0), 20f, 0f, 10f, 10f);
        f.Tick();
        bool two = f.Stream.LeafCount == 2;

        f.Table.SetVisible(b, false);
        IncrementalGateResult hidden = f.TickAndCheck();
        bool gone = f.Stream.LeafCount == 1;

        f.Table.SetVisible(b, true);
        IncrementalGateResult shown = f.TickAndCheck();

        // 隐一个已在流里的叶，原位把 α 归零画面也对——但整编产物里**没有这个实例**。
        // 逐字节门下两条路径必须同形，所以可见性跃迁一律走「数量变化 → 标 Structure」。
        Check("P7 Visible: 可见性跃迁 = 叶进出流 = 整编（不留一个零 α 的幽灵实例）",
            two && gone && f.Stream.LeafCount == 2
            && f.Pipe.Drain.CauseCount(EscalateCause.VisibleFlip) >= 2
            && hidden.Pass && shown.Pass && f.Sound());
    }

    private static void StructureChannelRebuildsOnce()
    {
        var f = new PipeFixture();
        f.Leaf(PipeSolid(0), 0f, 0f, 10f, 10f);
        f.Tick();
        int rebuilds = f.Pipe.Extract.Rebuilds;

        NodeHandle x = f.Leaf(PipeSolid(0), 20f, 0f, 10f, 10f);
        NodeHandle y = f.Leaf(PipeSolid(0), 40f, 0f, 10f, 10f);
        IncrementalGateResult r = f.TickAndCheck();

        Check("P7 Structure: 一帧内多条结构脏折叠成一次整编（面板才是重编的粒度）",
            x.Index != 0 && y.Index != 0
            && f.Pipe.Extract.Rebuilds == rebuilds + 1
            && f.Stream.LeafCount == 3 && r.Pass && f.Sound());
    }

    // ── P7：原位改写的承重守卫（2026-08 审计探针转正）───────────────────────
    //
    // StreamDrain 原位路径的四道守卫里，审计前只有 SegmentKey 有覆盖：其余三道**逐条删掉全套仍绿**。
    // 每道守卫挡的都是「原位写出画面对/字节错（或干脆画错）的流」，下面三条各钉一道，全部挂门跑。

    /// <summary>
    /// SoleUserOfSlot 守卫：旋转容器下两个 local (0,0) 的叶 world 全等 ⇒ 整编 ReuseSlot 共槽。
    /// 移动持有者时若原位写共享槽，兄弟被连带搬到别人的 world（审计实测：错画且本帧无任何
    /// rebuild，错画持续）。必须 SlotShape 升级整编，让两叶各分各的槽。
    /// </summary>
    private static void SharedSlotOwnerMoveEscalates()
    {
        var f = new PipeFixture();
        NodeHandle spin = f.Box(100f, 100f, 50f, 50f);
        f.Table.SetRotation(spin, 0.5f);
        NodeHandle a = f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f, spin);
        NodeHandle b = f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f, spin);
        f.Tick();
        bool shared = f.Stream.LeafCount == 2
            && f.Stream.Leaf(0).Slot != SlotTable.IdentitySlot
            && f.Stream.Leaf(0).Slot == f.Stream.Leaf(1).Slot;
        int causes = f.Pipe.Drain.CauseCount(EscalateCause.SlotShape);

        f.Table.SetPosition(a, 5f, 5f);              // 动共享槽的持有者
        IncrementalGateResult r = f.TickAndCheck();

        int la = f.Pipe.Drain.LeafOf(a), lb = f.Pipe.Drain.LeafOf(b);
        Affine2D ma = f.Stream.Slots.Matrix(f.Stream.Leaf(la).Slot);
        Affine2D mb = f.Stream.Slots.Matrix(f.Stream.Leaf(lb).Slot);
        Affine2D wa = f.Table.World(a), wb = f.Table.World(b);
        Check("P7 守卫 SoleUserOfSlot: 共享槽的持有者移动 ⇒ SlotShape 升级整编，兄弟不被连带搬走",
            shared && f.Pipe.Drain.CauseCount(EscalateCause.SlotShape) >= causes + 1
            && la >= 0 && lb >= 0 && f.Stream.Leaf(la).Slot != f.Stream.Leaf(lb).Slot
            && SameAffine(in ma, in wa) && SameAffine(in mb, in wb)
            && r.Pass && f.Sound());
    }

    private static bool SameAffine(in Affine2D a, in Affine2D b) =>
        a.m00 == b.m00 && a.m01 == b.m01 && a.m10 == b.m10
        && a.m11 == b.m11 && a.tx == b.tx && a.ty == b.ty;

    /// <summary>
    /// SlackShape 守卫：slackHint=0 的九宫格 9 → 6 片，预留区形状从 9 变 6。硬写进旧区间会留下
    /// 3 个归零实例**永久驻留上传**（整编产物里没有它们，逐字节门当场分叉）。
    /// </summary>
    private static void SlackShapeChangeEscalates()
    {
        var f = new PipeFixture();
        NodeHandle n = f.Leaf(PipeSliced(1, 4f, slackHint: 0), 0f, 0f, 40f, 40f);
        f.Leaf(PipeSolid(1), 60f, 0f, 10f, 10f);                     // 邻叶：Start 必须跟着收缩
        f.Tick();
        bool nine = f.Stream.Leaf(0).Count == 9 && f.Stream.Leaf(0).Slack == 9
            && f.Stream.QuadCount == 10 && f.Stream.Leaf(1).Start == 9;
        int rebuilds = f.Pipe.Extract.Rebuilds;

        f.Table.SetContentRef(n, f.Content.AddLeaf(PipeSliced(1, 0f, slackHint: 0)));   // 左边框 0 ⇒ 6 片
        IncrementalGateResult r = f.TickAndCheck();

        Check("P7 守卫 SlackShape: 预留区形状变（9→6）⇒ 升级整编，无归零实例驻留、邻叶 Start 收缩",
            nine && f.Pipe.Drain.CauseCount(EscalateCause.SlackShape) == 1
            && f.Pipe.Extract.Rebuilds == rebuilds + 1
            && f.Stream.QuadCount == 7 && f.Stream.Leaf(0).Slack == 6
            && f.Stream.Leaf(1).Start == 6
            && r.Pass && f.Sound());
    }

    /// <summary>
    /// ClipCull 守卫：OpensClip 域内的叶被挪到整只在域外。硬写会让域外实例留在流里
    /// （GPU 裁掉了所以「画面对」，但整编产物里没有这个实例——字节错，且上传白花）。
    /// </summary>
    private static void ClipCullFlipEscalates()
    {
        var f = new PipeFixture();
        NodeHandle window = f.Box(0f, 0f, 60f, 60f);
        f.Table.SetContentRef(window, f.Content.Add(ExClipBox()));
        f.Leaf(PipeSolid(1), 40f, 40f, 30f, 30f, window);            // 压边留在流里
        NodeHandle wander = f.Leaf(PipeSolid(1), 30f, 5f, 40f, 20f, window);
        f.Tick();
        bool two = f.Stream.LeafCount == 2;
        int causes = f.Pipe.Drain.CauseCount(EscalateCause.ClipCull);

        f.Table.SetPosition(wander, 200f, 200f);                     // 整只挪出域
        IncrementalGateResult r = f.TickAndCheck();

        Check("P7 守卫 ClipCull: 域外剔除结论翻转 ⇒ 升级整编（域外实例出流，2 叶 → 1 叶）",
            two && f.Pipe.Drain.CauseCount(EscalateCause.ClipCull) >= causes + 1
            && f.Stream.LeafCount == 1
            && r.Pass && f.Sound());
    }

    // ── P8：提交 ───────────────────────────────────────────────────────────

    private static void SubmitOrderIsFixed()
    {
        var f = new PipeFixture();
        f.Leaf(PipeSolid(0), 0f, 0f, 10f, 10f);
        f.Backend.ClearLog();
        f.Tick();

        // 顺序不可换：BeginFrame → 上传/段表/序 → 绑主表面 → 提交 → EndFrame。
        // stats.Dirty 由提交收据决定，所以 BuildStats 必须在 Submit 之后、EndFrame 之前。
        int begin = IndexOfCall(f, MockCallKind.BeginFrame);
        int upload = IndexOfCall(f, MockCallKind.UploadInstances);
        int segs = IndexOfCall(f, MockCallKind.SetSegments);
        int orders = IndexOfCall(f, MockCallKind.SetRunOrders);
        int bind = IndexOfCall(f, MockCallKind.BindMainSurface);
        int draw = IndexOfCall(f, MockCallKind.DrawStream);
        int end = IndexOfCall(f, MockCallKind.EndFrame);

        Check("P8 提交: BeginFrame → 上传 → 段表 → 序 → 绑面 → 提交 → EndFrame（相位探针零违例）",
            begin == 0 && upload > begin && segs > upload && orders > segs
            && bind > orders && draw > bind && end > draw
            && f.Backend.Gates.PhaseViolation == 0 && f.Sound());
    }

    private static int IndexOfCall(PipeFixture f, MockCallKind kind)
    {
        for (int i = 0; i < f.Backend.Calls.Count; i++) if (f.Backend.Calls[i].Kind == kind) return i;
        return -1;
    }

    private static void SortingOrderFollowsStructEpochOnly()
    {
        var f = new PipeFixture();
        NodeHandle n = f.Leaf(PipeSolid(0), 0f, 0f, 10f, 10f);
        f.Tick();
        f.Backend.ClearLog();

        f.Table.SetPosition(n, 5f, 5f);       // 内容/变换变化：序是派生量，不该重推
        f.Tick();
        f.Table.SetAlpha(n, 0.5f);
        f.Tick();
        int quiet = f.Backend.CallCount(MockCallKind.SetRunOrders);

        f.Leaf(PipeSolid(0), 30f, 0f, 10f, 10f);   // 结构变了：整编 ⇒ structEpoch++ ⇒ 重推一次
        f.Tick();

        Check("P8 序派生: sortingOrder 重推当且仅当 structEpoch 变（后端全序校验同时把关）",
            quiet == 0 && f.Backend.CallCount(MockCallKind.SetRunOrders) == 1
            && f.Sound());
    }

    private static void MergedUploadIntervalStaysTight()
    {
        var f = new PipeFixture();
        // 三个互相重叠的叶，混合类别交替 ⇒ 相邻性排序动不了它们、段键也并不到一起
        // ⇒ 三个段各一个实例（纹理只交替是不够的：段键的纹理位是**集合**，≤4 张会并进同一段）。
        NodeHandle a = f.Leaf(PipeBlend(1, BlendClass.Normal), 0f, 0f, 30f, 30f);
        f.Leaf(PipeBlend(2, BlendClass.Add), 5f, 5f, 30f, 30f);
        NodeHandle c = f.Leaf(PipeBlend(3, BlendClass.Normal), 10f, 10f, 30f, 30f);
        f.Tick();
        bool split = f.Stream.SegmentCount == 3 && f.Stream.QuadCount == 3;

        f.Backend.ClearLog();
        f.Table.SetAlpha(a, 0.5f);            // 稀疏两点：段 0 与段 2，中间那段一个字节都不该动
        f.Table.SetAlpha(c, 0.5f);
        f.Tick();
        SubmitReport sparse = f.Pipe.LastReport;

        var uploads = new List<MockCall>();
        for (int i = 0; i < f.Backend.Calls.Count; i++)
            if (f.Backend.Calls[i].Kind == MockCallKind.UploadInstances) uploads.Add(f.Backend.Calls[i]);

        Check("P8 合并区间: 每段一次上传、区间贴着脏点（稀疏脏点不夹带中间那段）",
            split && uploads.Count == 2 && sparse.Quads == 2
            && uploads[0].First == 0 && uploads[0].Count == 1
            && uploads[1].First == 2 && uploads[1].Count == 1
            && f.Sound());
    }

    /// <summary>
    /// 2026-08 审计：MergedUploadIntervalStaysTight 的夹具是三段各 1 quad——「整段上传」与
    /// 「贴脏点上传」在 1-quad 段上不可区分（审计实测：改成整段上传全套仍绿，6-quad 段放大 6×）。
    /// 本用例用 6-quad 单段钉住**段内**紧致：单点脏只上传 1 实例、字节数 = 1 × 实例大小。
    /// </summary>
    private static void MergedUploadStaysTightWithinASegment()
    {
        var f = new PipeFixture();
        var leaves = new NodeHandle[6];
        for (int i = 0; i < 6; i++) leaves[i] = f.Leaf(PipeSolid(1), i * 20f, 0f, 10f, 10f);
        f.Tick();
        bool one = f.Stream.SegmentCount == 1 && f.Stream.QuadCount == 6;

        f.Backend.ClearLog();
        f.Table.SetAlpha(leaves[2], 0.5f);           // 段中间的单点脏
        IncrementalGateResult r = f.TickAndCheck();

        MockCall upload = f.Backend.LastCall(MockCallKind.UploadInstances);
        Check("P8 合并区间: 6-quad 单段的单点脏贴点上传（1 实例、80B，不整段重发）",
            one && f.Backend.CallCount(MockCallKind.UploadInstances) == 1
            && upload.First == 2 && upload.Count == 1
            && f.Pipe.LastReport.Quads == 1
            && f.Pipe.LastStats.UploadBytes == Abi.QuadInstanceSize
            && r.Pass && f.Sound());
    }

    // ── 门：增量正确性 ─────────────────────────────────────────────────────

    private static void IncrementalGateGreenAcrossEdits()
    {
        var f = new PipeFixture();
        NodeHandle panel = f.Box(10f, 10f, 200f, 200f);
        NodeHandle a = f.Leaf(PipeSolid(1, 0xFF102030u), 0f, 0f, 20f, 20f, panel);
        NodeHandle b = f.Leaf(PipeSliced(2, 4f, 16), 30f, 0f, 40f, 40f, panel);
        NodeHandle spin = f.Box(120f, 0f, 30f, 30f, panel);
        f.Table.SetRotation(spin, 0.4f);
        NodeHandle c = f.Leaf(PipeSolid(3), 0f, 0f, 12f, 12f, spin);

        bool ok = f.TickAndCheck().Pass;
        ok &= Step(() => f.Table.SetPosition(a, 4f, 6f));            // Transform（烘 rect）
        ok &= Step(() => f.Table.SetAlpha(b, 0.25f));                // Color
        ok &= Step(() => f.Table.SetGrayed(panel, true));            // 下行级联落叶
        ok &= Step(() => f.Table.SetPosition(spin, 130f, 10f));      // 容器变换 ⇒ 升级整编
        ok &= Step(() => f.Table.SetSize(a, 25f, 25f));              // Layout → 派生 Mark → 重新发射
        ok &= Step(() => f.Table.SetContentRef(b, f.Content.AddLeaf(PipeSliced(2, 0f, 16))));
        ok &= Step(() => f.Table.AddChild(panel, f.Leaf(PipeSolid(1), 60f, 60f, 10f, 10f)));
        ok &= Step(() => f.Table.SetVisible(c, false));              // 叶出流
        ok &= Step(() => f.Table.SetVisible(c, true));               // 叶回流
        ok &= Step(() => f.Table.RemoveFromParent(a));               // 结构删除
        ok &= Step(() => { });                                       // 静止帧同样要绿

        Check($"门 · 增量正确性（正例）: 11 帧编辑序列逐帧「增量流 ≡ 全量重建流」 {f.Gate.Last.Describe()}",
            ok && f.Gate.Checks == 12 && f.Gate.Failures == 0 && f.Sound());

        bool Step(Action edit)
        {
            edit();
            return f.TickAndCheck().Pass;
        }
    }

    /// <summary>
    /// 门的正例补失地①（2026-08 审计）：全套里 Transform 骑槽写只被进入 1 次、门正例从不走它——
    /// 「滚动 = 写一个矩阵槽」的旗舰承诺在门正例里零覆盖。旋转叶连续多帧移动：每帧一次真槽写
    /// （SlotWrites 逐帧 +1，不是整编替它擦掉）、零整编、门逐帧绿。
    /// </summary>
    private static void SlotRidingScrollStaysGreenAcrossFrames()
    {
        var f = new PipeFixture();
        NodeHandle n = f.Leaf(PipeSolid(1), 5f, 5f, 10f, 10f);
        f.Table.SetRotation(n, 0.4f);
        bool ok = f.TickAndCheck().Pass;
        int rebuilds = f.Pipe.Extract.Rebuilds;
        int writes = f.Pipe.Drain.SlotWrites;
        int slot = (int)f.Stream.Quads[0].SlotIndex;

        for (int i = 1; i <= 4; i++)
        {
            f.Table.SetPosition(n, 5f + i * 10f, 5f);
            ok &= f.TickAndCheck().Pass;
            ok &= f.Pipe.Drain.SlotWrites == writes + i;          // 真走了槽写路径
        }
        Affine2D m = f.Stream.Slots.Matrix(slot);
        Affine2D w = f.Table.World(n);
        Check("门 · 正例（骑槽滚动）: 旋转叶连续 4 帧移动=每帧一次槽写、零整编、门逐帧绿",
            ok && slot != SlotTable.IdentitySlot
            && f.Pipe.Extract.Rebuilds == rebuilds
            && SameAffine(in m, in w) && f.Sound());
    }

    /// <summary>
    /// 门的正例补失地②（2026-08 审计）：门的语料里没有裁剪域——规范形的 clip 字段整条删掉全套仍绿。
    /// OpensClip 域 + 域内叶的增量改写（Transform 原位重写 / Color / 剪枝重判）逐帧过门；
    /// 剪枝的**单一落点**（P7 尾）在此顺带验收：Extract.PruneAfterRebuild 默认关、门下必须绿
    /// （审计：从前神谕腿镜像开关而非镜像管线，这个形状是假红）。
    /// </summary>
    private static void ClippedCorpusKeepsGateGreen()
    {
        var f = new PipeFixture();
        NodeHandle window = f.Box(0f, 0f, 60f, 60f);
        f.Table.SetContentRef(window, f.Content.Add(ExClipBox()));
        NodeHandle inner = f.Leaf(PipeSolid(1), 5f, 5f, 10f, 10f, window);   // 整只在内 ⇒ 尾剪枝摘 clipIndex
        NodeHandle edge = f.Leaf(PipeSolid(1), 50f, 50f, 20f, 20f, window);  // 压边 ⇒ clip 采样保留
        NodeHandle spun = f.Leaf(PipeSolid(1), 20f, 20f, 10f, 10f, window);
        f.Table.SetRotation(spun, 0.5f);                                     // 骑槽 ⇒ 跨帧不剪不剔

        bool ok = f.TickAndCheck().Pass;
        ok &= Step(() => f.Table.SetPosition(edge, 45f, 45f));   // 裁剪内叶原位重写（仍压边）
        ok &= Step(() => f.Table.SetAlpha(edge, 0.5f));          // Color
        ok &= Step(() => f.Table.SetPosition(inner, 8f, 8f));    // 仍整只在内 ⇒ 重写后尾剪枝重新摘

        int li = f.Pipe.Drain.LeafOf(inner), le = f.Pipe.Drain.LeafOf(edge), ls = f.Pipe.Drain.LeafOf(spun);
        bool routes = li >= 0 && le >= 0 && ls >= 0
            && f.Stream.Quads[f.Stream.Leaf(li).Start].ClipIndex == 0u
            && f.Stream.Quads[f.Stream.Leaf(le).Start].ClipIndex != 0u
            && f.Stream.Quads[f.Stream.Leaf(ls).Start].ClipIndex != 0u;

        Check("门 · 正例（裁剪域）: OpensClip 语料 + 域内增量改写逐帧绿；剪枝单一落点（P6 开关默认关）门下不假红",
            ok && routes && !f.Pipe.Extract.PruneAfterRebuild && f.Sound());

        bool Step(Action edit)
        {
            edit();
            return f.TickAndCheck().Pass;
        }
    }

    /// <summary>
    /// 裁剪语料的负例（杀「删规范形 clip 字段」的变异）：吞掉 Content 排水后换窗口的软边——
    /// 几何与 quad 逐字节不变，**唯一**的差异在 clip 条目的 soft 字段。规范形若不带 clip 字段，
    /// 这个漏标永远抓不到；门必须红并定位到 Clips.soft。
    /// </summary>
    private static void ClipShapeDriftIsCaughtByGate()
    {
        var f = new PipeFixture();
        NodeHandle window = f.Box(0f, 0f, 60f, 60f);
        f.Table.SetContentRef(window, f.Content.Add(ExClipBox()));
        f.Leaf(PipeSolid(1), 50f, 50f, 20f, 20f, window);            // 压边：clip 条目保持被引用
        f.Tick();
        bool green = f.Gate.Check().Pass;

        f.Inval.Unregister(f.Pipe.Drain);
        f.Inval.Register(new DropChannel(f.Pipe.Drain, Ch.Content));
        f.Table.SetContentRef(window, f.Content.Add(ExClipBox(new Vector2(4f, 4f))));   // 换软边
        f.Tick();
        IncrementalGateResult red = f.Gate.Check();

        Check($"门 · 增量正确性: 裁剪域形状漂移 ⇒ 门红并定位到 Clips.soft {red.Describe()}",
            green && !red.Pass
            && red.Site.Section == CanonicalSection.Clips && red.Site.Field == "soft");
    }

    /// <summary>
    /// 帧级序神谕的正例兼「边清边拼」变异的杀手（2026-08 审计：ValidatePaintOrder 从未逐帧跑过）。
    /// 子树从**后面的切片**搬进**前面的展开区**——切片拼接若不「清扫整体先于拼接」，
    /// 目的地展开先写好的新 paintIndex 会被源切片的清扫抹成哨兵：定向拼接用例（前→后搬）撞不到，
    /// 只有帧尾的序神谕（DerivedOracle 开着时随帧跑）与流结构门抓得住。
    /// </summary>
    private static void SpliceBackwardSubtreeMoveHoldsUnderFrameGate()
    {
        var f = new PipeFixture();
        for (int i = 0; i < 8; i++) f.Leaf(PipeSolid(1), i * 12f, 40f, 10f, 10f);   // 撑大树，脏比例落在切片阈值内
        NodeHandle boxA = f.Box(0f, 0f, 50f, 50f);
        f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f, boxA);
        NodeHandle boxB = f.Box(100f, 0f, 50f, 50f);
        NodeHandle b1 = f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f, boxB);
        f.Tick();
        int splices = f.Table.PaintSplices;

        f.Table.AddChild(boxA, b1);          // 后切片子树前移
        IncrementalGateResult r = f.TickAndCheck();

        Check("P6 切片拼接(帧级): 后切片子树前移走切片路径，帧尾序神谕与结构门都绿（清扫整体先于拼接）",
            f.Table.PaintSplices == splices + 1
            && f.Table.PaintIndexOf(b1) != NodeTable.NotInTree
            && f.Stream.LeafCount == 10
            && r.Pass && f.Pipe.PaintOrderFailures == 0 && f.Sound());
    }

    /// <summary>
    /// 派生列神谕 worldVisual 半边的专属负例（2026-08 审计：把 visual 比对整行关掉全套仍绿）。
    /// 摘钩子后写容器 α——world 列一位都不动，唯一的差异在 worldVisual 级联；
    /// 与 <see cref="DerivedOracleCatchesAMissingMark"/>（world 半边）成对。
    /// </summary>
    private static void DerivedOracleCatchesAStaleVisualCascade()
    {
        var f = new PipeFixture();
        NodeHandle box = f.Box(10f, 10f, 50f, 50f);
        f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f, box);
        f.Tick();
        bool cleanFirst = f.Pipe.DerivedOracleFailures == 0;

        f.Inval.Detach();
        f.Table.SetAlpha(box, 0.5f);
        f.Tick();

        Check("P6 派生列: 神谕的 worldVisual 半边抓住 α 级联漏标（world 全等、visual 陈旧 ⇒ 红并指名节点）",
            cleanFirst && f.Pipe.DerivedOracleFailures == 1
            && f.Pipe.DerivedBadIndex == box.Index);
    }

    private static void IncrementalGateRedOnDroppedChannel()
    {
        var f = new PipeFixture();
        NodeHandle n = f.Leaf(PipeSolid(0, 0xFF3366AAu), 0f, 0f, 10f, 10f);
        f.Tick();
        bool green = f.Gate.Check().Pass;

        // 负例：把 Color 通道的排水整条吞掉（产品代码一行不改）。
        f.Inval.Unregister(f.Pipe.Drain);
        f.Inval.Register(new DropChannel(f.Pipe.Drain, Ch.Color));

        f.Table.SetAlpha(n, 0.5f);
        f.Tick();
        IncrementalGateResult red = f.Gate.Check();

        Check($"门 · 增量正确性（负例）: 吞掉 Color 排水 ⇒ 门红并定位到 Color 通道 {red.Describe()}",
            green && !red.Pass && red.FirstDifference >= 0
            && (red.Channel & Ch.Color) != 0
            && red.Site.Section == CanonicalSection.Quads && red.Site.Field == "color"
            && f.Gate.Failures == 1);
    }

    private static void IncrementalGateLocatesContentChannel()
    {
        var f = new PipeFixture();
        NodeHandle n = f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f);
        f.Tick();
        bool green = f.Gate.Check().Pass;

        f.Inval.Unregister(f.Pipe.Drain);
        f.Inval.Register(new DropChannel(f.Pipe.Drain, Ch.Content));

        // 同纹理、同尺寸、只换 UV：差异必然落在 quad 的 uv 字段上，正是 Content 的活。
        var moved = LeafSpec.Image(new TexId(1),
            SpriteRegion.Full(new Vector4(0.5f, 0.5f, 1f, 1f), 4f, 4f));
        f.Table.SetContentRef(n, f.Content.AddLeaf(in moved));
        f.Tick();
        IncrementalGateResult red = f.Gate.Check();

        Check($"门 · 增量正确性: 差异按 Ch 通道定位（uv 漂移 ⇒ Content） {red.Describe()}",
            green && !red.Pass && (red.Channel & Ch.Content) != 0
            && red.Site.Field.StartsWith("uv", StringComparison.Ordinal));
    }

    private static void ColorFieldIsAPureFunctionOfBaseColor()
    {
        var f = new PipeFixture();
        NodeHandle a = f.Leaf(PipeSolid(0, 0xFF112233u), 0f, 0f, 10f, 10f);
        NodeHandle b = f.Leaf(PipeSolid(0, 0x80445566u), 20f, 0f, 10f, 10f);
        f.Tick();
        f.Table.SetAlpha(a, 0.3f);
        f.Table.SetGrayed(b, true);
        f.Tick();

        byte[] before = CanonicalStream.Canonicalize(f.Stream.Snapshot());
        f.Stream.RecolorAll();                 // 神谕的另一个半边：全量从基色重算
        byte[] after = CanonicalStream.Canonicalize(f.Stream.Snapshot());

        Check("不变量 9: GPU color 场 ≡ f(baseColor, worldVisual)——全量重算逐字节不改一位",
            CanonicalStream.FirstDifference(before, after) < 0 && before.Length > 0 && f.Sound());
    }

    private static void IdleFrameReceipt()
    {
        var f = new PipeFixture();
        f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f);
        f.Leaf(PipeSolid(2), 20f, 0f, 10f, 10f);
        f.Tick();                               // 首帧：建流 + 整编 + 全量上传
        ulong presentsAfterFirst = f.Backend.Presents;
        ulong ticksAfterFirst = f.Backend.Ticks;
        ulong pipePresentsAfterFirst = f.Pipe.Presents;

        // 静止 5 帧**串着增量门**跑（2026-08 审计：本用例此前不过门，管线侧的 Presents 也无人断言——
        // 「静止帧偷偷重画」只要 mock 侧 stats.Dirty 还是 false 就抓不到）。
        bool idleGreen = true;
        for (int i = 0; i < 5; i++) idleGreen &= f.TickAndCheck().Pass;

        Check("门 · 零脏帧空转收据: 静止帧七条队列全零、后端零 draw、两侧 ticks/presents 分账、门下仍绿 " +
              f.Pipe.DescribeReceipt(),
            idleGreen && f.AllQueuesEmpty()
            && f.Backend.Ticks == ticksAfterFirst + 5
            && f.Backend.Presents == presentsAfterFirst
            && f.Pipe.Ticks == 6 && f.Pipe.Presents == pipePresentsAfterFirst
            && f.Pipe.Presents == 1                             // 首帧画了一次，此后一次都不许多
            && f.Pipe.IdleFrames == 5
            && f.Pipe.LastReport.IsIdle && !f.Pipe.LastStats.Dirty
            && f.Backend.CallCount(MockCallKind.DrawStream) == 1
            && f.Sound());
    }

    private static void CanonicalLocatorAgreesWithLayout()
    {
        var f = new PipeFixture();
        NodeHandle window = f.Box(0f, 0f, 60f, 60f);
        f.Table.SetContentRef(window, f.Content.Add(new ContentRecord
        {
            Kind = ExtractKind.None,
            OpensClip = true,
            Clip = new ClipShape { Soft = Vector2.zero, Radii = Vector4.zero },
            RenderEveryN = 1,
        }));
        NodeHandle spun = f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f, window);
        f.Table.SetRotation(spun, 0.5f);
        f.Leaf(PipeSolid(1), 20f, 20f, 10f, 10f, window);
        f.Tick();

        StreamSnapshot snap = f.Stream.Snapshot();
        byte[] bytes = CanonicalStream.Canonicalize(snap);
        // 定位表与写入序是两份手写布局：逐分区抽样核对，错位在这里响而不是在门失败的报告里说谎。
        CanonicalSite q0 = CanonicalStream.Locate(snap, 9);            // 头 9B 之后是第 0 个实例的 rect
        CanonicalSite q0Color = CanonicalStream.Locate(snap, 9 + 64);
        CanonicalSite q1 = CanonicalStream.Locate(snap, 9 + 85);
        CanonicalSite tail = CanonicalStream.Locate(snap, bytes.Length - 1);
        CanonicalSite beyond = CanonicalStream.Locate(snap, bytes.Length + 4);

        Check("规范形定位: 分区/字段/通道与写入序一致（门失败时报告指的是同一个字节）",
            q0.Section == CanonicalSection.Quads && q0.Index == 0 && q0.Field == "rect"
            && q0Color.Field == "color" && (q0Color.Channel & Ch.Color) != 0
            && q1.Section == CanonicalSection.Quads && q1.Index == 1
            && tail.Section == CanonicalSection.BaseColor
            && beyond.Section == CanonicalSection.Beyond
            && f.Sound());
    }
}
