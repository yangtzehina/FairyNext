using FairyNext.Contracts;
using FairyNext.Core;
using FairyNext.Core.Layout;

namespace FairyNext.Tests;

/// <summary>
/// M1-16 布局用例（Program 的 partial 分片）：约束图（拓扑序/分层拒环/offset 捕获）、
/// P5 四层骨架（包围/约束/流式 + parentUsesSize 受控窗）、resolved 唯一写者纪律，
/// 以及本包上线的两道门——**P5 幂等断言**与**布局差分神谕（L2）**，各配正例与负例。
///
/// 对齐 docs/architecture.md 平面四 A：机制⑨（EdgeFollow 单算子拓扑序单遍）、机制⑩（四层排水
/// 与分层拒环、parentUsesSize）、机制⑪（受控窗 ≤3 轮兜底 + P5 幂等）；不变量 2/7/8/9/15。
/// 与 fork 的数值级对拍（24 关系×pivot 矩阵）归 M2-14 像素门——本处的神谕是增量 vs 全量的自洽。
/// </summary>
public static partial class Program
{
    private static void LayoutSuite()
    {
        // 约束图：编译态表示与封图
        OpBitfieldRoundTrip();
        SealSortsChainTopologically();
        DiamondDependencySolvesInOnePass();
        FanOutReachesAllFollowers();
        DirectCycleIsRejectedWithPath();
        IndirectCycleIsRejectedWithPath();
        SelfLoopIsRejected();
        OverboundAndDuplicateEdgeAreRejected();

        // offset 捕获语义（对齐 fork RelationItem，实现按架构文档）
        OffsetCapturedAtArmSurvivesTargetMoves();
        ContainerEdgeFollowTracksResize();
        UserWriteRecapturesOffset();
        PercentKeepsRatioAcrossResize();
        StretchPairPinsOppositeEdge();
        SizeDeltaFollow();

        // LinearLayout golden
        HorizontalFlowGolden();
        WrapBoundaryGolden();
        VerticalFlowGolden();

        // parentUsesSize 与受控窗
        ParentUsesSizeAutoHeight();
        BoundToParentAxisIsExcludedFromExtent();
        ReverseDependencyConvergesInWindow();
        MicroDrainOverflowIsLoudAndLossless();

        // 门：P5 幂等断言
        IdempotenceGateGreenAcrossEdits();
        IdempotenceGateRedOnNonConvergentWrite();

        // 门：布局差分神谕（L2）
        DifferentialOracleGreenOnRandomForest();
        DifferentialOracleRedOnMutedDependency();

        // 接缝与纪律
        SlotlessDrainKeepsDerivedMarks();
        ResolvedStaysApartFromAuthored();
        ResolvedWriterGateFiresOnWrongSourceAndPhase();
        LayoutEngineAttachIsExclusive();
        TransformPeekDoesNotConsumeQueue();
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    /// <summary>树 + 内核 + 布局引擎（无渲染管线）。布局单跑时必须有人消费五条渲染通道——
    /// 否则引擎 P5 的 LayoutDerived 派生标记跨帧滞留在 Transform 队列里，下一帧被误读成用户写
    /// （见 LayoutEngine 类注释的依赖前提）；<see cref="Sink"/> 替 P7 扮演消费者。</summary>
    private sealed class LayoutFixture
    {
        internal readonly NodeTable Table = new NodeTable(tree: 9);
        internal readonly Invalidation Inval;
        internal readonly UiKernel Kernel;
        internal readonly LayoutEngine Layout;
        private FrameTime _time = FrameTime.First(0.016f, 0.016f);

        private sealed class Sink : IChannelDrain
        {
            public Ch Consumes => Ch.Content | Ch.Transform | Ch.Color | Ch.Visible | Ch.Structure;
            public void Drain(ref FrameContext ctx, Ch channel, ReadOnlySpan<NodeHandle> queue) { }
        }

        internal LayoutFixture(bool idem = true, bool diff = true, bool sink = true)
        {
            Inval = new Invalidation(Table);
            Kernel = new UiKernel(Table, Inval);
            Layout = new LayoutEngine(Kernel) { IdempotenceGate = idem, DifferentialGate = diff };
            Layout.Attach();
            if (sink) Inval.Register(new Sink());
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

        internal ResolvedGeom R(NodeHandle h) => Table.GetResolved(h);

        internal bool GatesGreen => Layout.Stats.IdempotenceFailures == 0 && Layout.Stats.DifferentialFailures == 0;
    }

    /// <summary>确定性随机源（xorshift64*；语料 seed 固定可复现）。</summary>
    private struct LayoutRng
    {
        private ulong _s;
        internal LayoutRng(ulong seed) { _s = seed * 2685821657736338717UL + 0x9E3779B97F4A7C15UL; }

        internal uint Next()
        {
            _s ^= _s << 13; _s ^= _s >> 7; _s ^= _s << 17;
            return (uint)(_s >> 32);
        }

        internal int Range(int n) => (int)(Next() % (uint)n);
    }

    // ── 约束图：编译态表示与封图 ─────────────────────────────────────────────

    private static void OpBitfieldRoundTrip()
    {
        byte packed = ConstraintOp.Pack(LayoutAxis.Y, EdgeSel.Max, EdgeSel.Center, pivotCorrect: true);
        var op = new ConstraintOp { SrcNode = 7, DstNode = 9, Kind = (byte)ConstraintKind.FollowDelta, AxisEdges = packed, Next = 3 };
        byte packed2 = ConstraintOp.Pack(LayoutAxis.X, EdgeSel.Min, EdgeSel.Size, pivotCorrect: false);
        var op2 = new ConstraintOp { AxisEdges = packed2 };
        Check("布局 · ConstraintOp 位域打包往返（axis/dstEdge/srcEdge/pivotCorrect/next）",
            op.Axis == LayoutAxis.Y && op.DstEdge == EdgeSel.Max && op.SrcEdge == EdgeSel.Center
            && op.PivotCorrect && op.Next == 3
            && op2.Axis == LayoutAxis.X && op2.DstEdge == EdgeSel.Min && op2.SrcEdge == EdgeSel.Size
            && !op2.PivotCorrect);
    }

    private static void SealSortsChainTopologically()
    {
        // 链式 A→B→C，故意按依赖反序声明：Seal 后数组索引必须是拓扑序（B 先于 C）。
        var b = new ConstraintGraphBuilder(4);
        b.Follow(3, EdgeSel.Min, 2, EdgeSel.Min, LayoutAxis.X);      // C ← B（先声明）
        b.Follow(2, EdgeSel.Min, 1, EdgeSel.Min, LayoutAxis.X);      // B ← A
        ConstraintGraphResult r = b.Seal();
        bool sorted = r.Ok && r.Graph!.Ops[0].DstNode == 2 && r.Graph.Ops[1].DstNode == 3;

        var f = new LayoutFixture();
        NodeHandle a = f.Box(10f, 0f, 5f, 5f);
        NodeHandle bb = f.Box(20f, 0f, 5f, 5f);
        NodeHandle c = f.Box(40f, 0f, 5f, 5f);
        f.Layout.Arm(r.Graph!, new[] { f.Table.Root, a, bb, c });
        f.Tick();
        f.Table.SetPosition(a, 50f, 0f);
        f.Tick();
        Check("布局 · 拓扑序：链式依赖反序声明照样单遍收敛（主四层一遍、零微排水轮）",
            sorted && f.R(bb).X == 60f && f.R(c).X == 80f
            && f.Layout.Stats.DrainPasses == 0 && !f.Layout.Stats.PendingWork && f.GatesGreen);
    }

    private static void DiamondDependencySolvesInOnePass()
    {
        // 菱形：B←A.Min、C←A.Max、D 拉伸（Min←B.Max，Max←C.Min）。
        var b = new ConstraintGraphBuilder(5);
        b.Follow(4, EdgeSel.Min, 2, EdgeSel.Max, LayoutAxis.X);      // D.Min ← B.Max（先声明 D，逼排序干活）
        b.Follow(4, EdgeSel.Max, 3, EdgeSel.Min, LayoutAxis.X);
        b.Follow(2, EdgeSel.Min, 1, EdgeSel.Min, LayoutAxis.X);
        b.Follow(3, EdgeSel.Min, 1, EdgeSel.Max, LayoutAxis.X);
        ConstraintGraphResult r = b.Seal();

        var f = new LayoutFixture();
        NodeHandle a = f.Box(10f, 0f, 20f, 5f);
        NodeHandle nb = f.Box(40f, 0f, 10f, 5f);
        NodeHandle nc = f.Box(70f, 0f, 10f, 5f);
        NodeHandle nd = f.Box(50f, 0f, 15f, 5f);
        f.Layout.Arm(r.Graph!, new[] { f.Table.Root, a, nb, nc, nd });
        f.Tick();
        f.Table.SetPosition(a, 30f, 0f);
        f.Tick();
        ResolvedGeom d = f.R(nd);
        Check("布局 · 菱形依赖：拉伸对坐在两条支路汇点上，单遍收敛",
            r.Ok && f.R(nb).X == 60f && f.R(nc).X == 90f && d.X == 70f && d.W == 15f
            && f.Layout.Stats.DrainPasses == 0 && f.GatesGreen);
    }

    private static void FanOutReachesAllFollowers()
    {
        var b = new ConstraintGraphBuilder(5);
        b.Follow(2, EdgeSel.Min, 1, EdgeSel.Min, LayoutAxis.X);
        b.Follow(3, EdgeSel.Min, 1, EdgeSel.Max, LayoutAxis.X);
        b.Follow(4, EdgeSel.Min, 1, EdgeSel.Center, LayoutAxis.Y);
        ConstraintGraphResult r = b.Seal();

        var f = new LayoutFixture(idem: false, diff: false);         // 关门：本用例数 opsEvaluated
        NodeHandle a = f.Box(10f, 10f, 20f, 20f);
        NodeHandle n1 = f.Box(15f, 0f, 5f, 5f);
        NodeHandle n2 = f.Box(40f, 0f, 5f, 5f);
        NodeHandle n3 = f.Box(0f, 30f, 5f, 5f);
        f.Layout.Arm(r.Graph!, new[] { f.Table.Root, a, n1, n2, n3 });
        f.Tick();
        long before = f.Layout.Stats.OpsEvaluated;
        f.Table.SetPosition(a, 20f, 14f);                            // 一次移动 → 三个跟随者全到位
        f.Tick();
        Check("布局 · 多扇出：FanOut 把一次 src 移动传给全部读者（恰好三次求值）",
            r.Ok && f.R(n1).X == 25f && f.R(n2).X == 50f && f.R(n3).Y == 34f
            && f.Layout.Stats.OpsEvaluated - before == 3);
    }

    private static void DirectCycleIsRejectedWithPath()
    {
        var b = new ConstraintGraphBuilder(3);
        b.Follow(1, EdgeSel.Min, 2, EdgeSel.Min, LayoutAxis.X);
        b.Follow(2, EdgeSel.Min, 1, EdgeSel.Min, LayoutAxis.X);
        ConstraintGraphResult r = b.Seal();
        Check("布局 · 分层拒环（直接环）：CycleRejected 带闭合路径、不 throw 不静默",
            !r.Ok && r.Graph == null && r.Error.Contains("CycleRejected")
            && r.CyclePath.Length == 3 && r.CyclePath[0] == r.CyclePath[2]
            && Array.IndexOf(r.CyclePath, (ushort)1) >= 0 && Array.IndexOf(r.CyclePath, (ushort)2) >= 0);
    }

    private static void IndirectCycleIsRejectedWithPath()
    {
        var b = new ConstraintGraphBuilder(4);
        b.Follow(2, EdgeSel.Min, 1, EdgeSel.Min, LayoutAxis.X);      // B ← A
        b.Follow(3, EdgeSel.Min, 2, EdgeSel.Max, LayoutAxis.X);      // C ← B
        b.Follow(1, EdgeSel.Min, 3, EdgeSel.Min, LayoutAxis.X);      // A ← C：隔层环
        ConstraintGraphResult r = b.Seal();
        Check("布局 · 分层拒环（隔层环）：三节点间接环整条路径可指认",
            !r.Ok && r.Error.Contains("CycleRejected")
            && r.CyclePath.Length == 4 && r.CyclePath[0] == r.CyclePath[3]
            && Array.IndexOf(r.CyclePath, (ushort)1) >= 0
            && Array.IndexOf(r.CyclePath, (ushort)2) >= 0
            && Array.IndexOf(r.CyclePath, (ushort)3) >= 0);
    }

    private static void SelfLoopIsRejected()
    {
        var b = new ConstraintGraphBuilder(3);
        b.Follow(1, EdgeSel.Min, 1, EdgeSel.Max, LayoutAxis.X);
        ConstraintGraphResult r = b.Seal();
        Check("布局 · 分层拒环（自环）：src==dst 即拒",
            !r.Ok && r.Error.Contains("CycleRejected") && r.CyclePath.Length == 2
            && r.CyclePath[0] == 1 && r.CyclePath[1] == 1);
    }

    private static void OverboundAndDuplicateEdgeAreRejected()
    {
        var b1 = new ConstraintGraphBuilder(4);                      // 每轴 >2 标量
        b1.Follow(1, EdgeSel.Min, 2, EdgeSel.Min, LayoutAxis.X);
        b1.Follow(1, EdgeSel.Max, 3, EdgeSel.Min, LayoutAxis.X);
        b1.Follow(1, EdgeSel.Center, 2, EdgeSel.Max, LayoutAxis.X);
        var b2 = new ConstraintGraphBuilder(3);                      // 同边双写
        b2.Follow(1, EdgeSel.Min, 2, EdgeSel.Min, LayoutAxis.X);
        b2.Follow(1, EdgeSel.Min, 2, EdgeSel.Max, LayoutAxis.X);
        var b3 = new ConstraintGraphBuilder(2);                      // dst = 容器自身
        b3.Follow(0, EdgeSel.Min, 1, EdgeSel.Min, LayoutAxis.X);
        Check("布局 · 封图校验：每轴 >2 标量 / 同边双写 / dst=容器 各自拒绝",
            !b1.Seal().Ok && !b2.Seal().Ok && !b3.Seal().Ok);
    }

    // ── offset 捕获语义 ──────────────────────────────────────────────────────

    private static void OffsetCapturedAtArmSurvivesTargetMoves()
    {
        // 建立时捕获偏移（child.Min − sib.Max = 10），此后目标怎么动都保持这 10。
        var b = new ConstraintGraphBuilder(3);
        b.Follow(2, EdgeSel.Min, 1, EdgeSel.Max, LayoutAxis.X);
        ConstraintGraph g = b.Seal().Graph!;

        var f = new LayoutFixture();
        NodeHandle sib = f.Box(10f, 0f, 20f, 10f);
        NodeHandle child = f.Box(40f, 0f, 10f, 10f);
        f.Layout.Arm(g, new[] { f.Table.Root, sib, child });
        f.Tick();
        bool armed = f.R(child).X == 40f;
        f.Table.SetPosition(sib, 25f, 0f);                           // Max 30 → 45
        f.Tick();
        bool follow1 = f.R(child).X == 55f;
        f.Table.SetPosition(sib, 0f, 0f);                            // Max → 20
        f.Tick();
        Check("布局 · offset 捕获：建立后移动目标，跟随者保持建立时的偏移",
            armed && follow1 && f.R(child).X == 30f && f.GatesGreen);
    }

    private static void ContainerEdgeFollowTracksResize()
    {
        // 容器（局部 0）的边在子坐标系里是 (0, size)：右贴边子节点随容器 resize 保持 −10 偏移。
        var b = new ConstraintGraphBuilder(2);
        b.Follow(1, EdgeSel.Min, 0, EdgeSel.Max, LayoutAxis.X);
        ConstraintGraph g = b.Seal().Graph!;

        var f = new LayoutFixture();
        NodeHandle panel = f.Box(0f, 0f, 100f, 50f);
        NodeHandle child = f.Box(90f, 0f, 10f, 10f, panel);
        f.Layout.Arm(g, new[] { panel, child });
        f.Tick();
        f.Table.SetSize(panel, 150f, 50f);
        f.Tick();
        Check("布局 · 容器边跟随：resize 容器，右贴边子节点保持捕获偏移",
            f.R(child).X == 140f && f.GatesGreen);
    }

    private static void UserWriteRecapturesOffset()
    {
        var b = new ConstraintGraphBuilder(2);
        b.Follow(1, EdgeSel.Min, 0, EdgeSel.Max, LayoutAxis.X);
        ConstraintGraph g = b.Seal().Graph!;

        var f = new LayoutFixture();
        NodeHandle panel = f.Box(0f, 0f, 150f, 50f);
        NodeHandle child = f.Box(140f, 0f, 10f, 10f, panel);
        f.Layout.Arm(g, new[] { panel, child });
        f.Tick();
        long recapBefore = f.Layout.Stats.Recaptures;
        f.Table.SetPosition(child, 70f, 0f);                         // 用户写 authored → 重捕获（offset = 70−150 = −80）
        f.Tick();
        bool snapped = f.R(child).X == 70f && f.Layout.Stats.Recaptures > recapBefore;
        f.Table.SetSize(panel, 200f, 50f);
        f.Tick();
        Check("布局 · 重捕获：用户写 authored 后 resolved 跟到新值，且此后按新偏移跟随",
            snapped && f.R(child).X == 120f && f.Table.GetPosition(child).x == 70f && f.GatesGreen);
    }

    private static void PercentKeepsRatioAcrossResize()
    {
        var b = new ConstraintGraphBuilder(2);
        b.Follow(1, EdgeSel.Min, 0, EdgeSel.Size, LayoutAxis.X, percent: true);
        b.Follow(1, EdgeSel.Size, 0, EdgeSel.Size, LayoutAxis.Y, percent: true);
        ConstraintGraph g = b.Seal().Graph!;

        var f = new LayoutFixture();
        NodeHandle panel = f.Box(0f, 0f, 100f, 100f);
        NodeHandle child = f.Box(25f, 0f, 10f, 30f, panel);          // ratioX=0.25、ratioH=0.3
        f.Layout.Arm(g, new[] { panel, child });
        f.Tick();
        f.Table.SetSize(panel, 200f, 200f);
        f.Tick();
        ResolvedGeom c = f.R(child);
        Check("布局 · percent：比例在建立时捕获，resize 后按 src 起点 + ratio×size 重算（位级）",
            c.X == 0f + 0.25f * 200f && c.H == 0.3f * 200f && f.GatesGreen);
    }

    private static void StretchPairPinsOppositeEdge()
    {
        // LeftExt_Right 的归约形态：Min 跟随目标 Max、对边 Pin——目标右移，宽度缩、右边不动。
        var b = new ConstraintGraphBuilder(3);
        b.Follow(2, EdgeSel.Min, 1, EdgeSel.Max, LayoutAxis.X);
        b.Pin(2, EdgeSel.Max, LayoutAxis.X);
        ConstraintGraph g = b.Seal().Graph!;

        var f = new LayoutFixture();
        NodeHandle sib = f.Box(10f, 0f, 10f, 10f);                   // Max = 20
        NodeHandle child = f.Box(30f, 0f, 50f, 10f);                 // Min 30（偏移 10）、Max 80（钉）
        f.Layout.Arm(g, new[] { f.Table.Root, sib, child });
        f.Tick();
        f.Table.SetPosition(sib, 30f, 0f);                           // Max → 40
        f.Tick();
        ResolvedGeom c = f.R(child);
        Check("布局 · 拉伸对（Ext 归约）：Min 跟随 + Max 钉住 ⇒ 尺寸导出",
            c.X == 50f && c.W == 30f && c.X + c.W == 80f && f.GatesGreen);
    }

    private static void SizeDeltaFollow()
    {
        var b = new ConstraintGraphBuilder(3);
        b.Follow(2, EdgeSel.Size, 1, EdgeSel.Size, LayoutAxis.X);
        ConstraintGraph g = b.Seal().Graph!;

        var f = new LayoutFixture();
        NodeHandle sib = f.Box(0f, 0f, 100f, 10f);
        NodeHandle child = f.Box(0f, 20f, 80f, 10f);                 // 尺寸差 −20
        f.Layout.Arm(g, new[] { f.Table.Root, sib, child });
        f.Tick();
        f.Table.SetSize(sib, 140f, 10f);
        f.Tick();
        Check("布局 · Width 关系（Size delta）：保持建立时的尺寸差",
            f.R(child).W == 120f && f.GatesGreen);
    }

    // ── LinearLayout golden ─────────────────────────────────────────────────

    private static void HorizontalFlowGolden()
    {
        var f = new LayoutFixture();
        NodeHandle panel = f.Box(0f, 0f, 200f, 50f);
        NodeHandle c1 = f.Box(99f, 99f, 30f, 10f, panel);            // 初始位置随意：流式层拥有位置
        NodeHandle c2 = f.Box(0f, 0f, 50f, 20f, panel);
        NodeHandle c3 = f.Box(7f, 7f, 20f, 15f, panel);
        f.Layout.RegisterLinear(panel, new LinearLayoutDesc { Gap = 5f });
        f.Tick();
        Check("布局 · 水平流式 golden：混合尺寸 + 间距，位置逐项精确",
            f.R(c1).X == 0f && f.R(c2).X == 35f && f.R(c3).X == 90f
            && f.R(c1).Y == 0f && f.R(c2).Y == 0f && f.R(c3).Y == 0f && f.GatesGreen);
    }

    private static void WrapBoundaryGolden()
    {
        var f = new LayoutFixture();
        NodeHandle panel = f.Box(0f, 0f, 100f, 200f);
        NodeHandle c1 = f.Box(0f, 0f, 40f, 10f, panel);
        NodeHandle c2 = f.Box(0f, 0f, 60f, 20f, panel);              // 40+60 恰满 100：**恰满不折**（严格越界才折）
        NodeHandle c3 = f.Box(0f, 0f, 40f, 15f, panel);              // 100+40>100 → 折行
        NodeHandle c4 = f.Box(0f, 0f, 120f, 30f, panel);             // 比容器还宽 → 独占一行，不空转
        f.Layout.RegisterLinear(panel, new LinearLayoutDesc { Wrap = true, LineGap = 4f });
        f.Tick();
        Check("布局 · wrap 边界 golden：恰满不折、越界才折（行首超宽件独占一行）、行高按行内最大",
            f.R(c1).X == 0f && f.R(c1).Y == 0f
            && f.R(c2).X == 40f && f.R(c2).Y == 0f
            && f.R(c3).X == 0f && f.R(c3).Y == 24f                   // 行高 20 + 行距 4
            && f.R(c4).X == 0f && f.R(c4).Y == 43f                   // 24 + 15 + 4
            && f.GatesGreen);
    }

    private static void VerticalFlowGolden()
    {
        var f = new LayoutFixture();
        NodeHandle panel = f.Box(0f, 0f, 50f, 200f);
        NodeHandle c1 = f.Box(0f, 0f, 10f, 30f, panel);
        NodeHandle c2 = f.Box(0f, 0f, 20f, 50f, panel);
        f.Layout.RegisterLinear(panel, new LinearLayoutDesc { Vertical = true, Gap = 8f });
        f.Tick();
        Check("布局 · 垂直流式 golden",
            f.R(c1).Y == 0f && f.R(c2).Y == 38f && f.R(c1).X == 0f && f.R(c2).X == 0f && f.GatesGreen);
    }

    // ── parentUsesSize 与受控窗 ─────────────────────────────────────────────

    private static void ParentUsesSizeAutoHeight()
    {
        var f = new LayoutFixture();
        NodeHandle panel = f.Box(0f, 0f, 100f, 1f);
        NodeHandle c1 = f.Box(0f, 0f, 60f, 10f, panel);
        NodeHandle c2 = f.Box(0f, 0f, 60f, 20f, panel);              // 60+60>100 → 第二行
        f.Layout.RegisterLinear(panel, new LinearLayoutDesc { Wrap = true, LineGap = 4f, ParentUsesSize = true });
        f.Tick();
        bool sized = f.R(panel).H == 34f;                            // 行 1 高 10 + 4 + 行 2 高 20
        f.Table.SetSize(c2, 60f, 40f);                               // 子变高 → 父随之
        f.Tick();
        Check("布局 · parentUsesSize：wrap 容器交叉轴自撑为内容高度，子 resize 随动",
            sized && f.R(panel).H == 54f && !f.Layout.Stats.PendingWork && f.GatesGreen);
    }

    private static void BoundToParentAxisIsExcludedFromExtent()
    {
        // 「绑父轴不计包围」：B 的宽度按 percent 绑容器宽，容器 X 轴自撑时 B 不计入 X 包围。
        var b = new ConstraintGraphBuilder(3);
        b.Follow(2, EdgeSel.Size, 0, EdgeSel.Size, LayoutAxis.X, percent: true);
        ConstraintGraph g = b.Seal().Graph!;

        var f = new LayoutFixture();
        NodeHandle panel = f.Box(0f, 0f, 100f, 20f);
        NodeHandle a = f.Box(0f, 0f, 30f, 10f, panel);
        NodeHandle bb = f.Box(0f, 0f, 50f, 10f, panel);              // ratio = 0.5
        f.Layout.RegisterLinear(panel, new LinearLayoutDesc { ParentUsesSize = true });
        f.Layout.Arm(g, new[] { panel, a, bb });
        f.Tick();
        Check("布局 · 绑父轴不计包围：容器自撑只数 A（30），B 再按新宽的一半导出，迁移警告计数非零",
            f.R(panel).W == 30f && f.R(bb).W == 15f
            && f.Layout.Stats.ParentAxisBoundSkips > 0
            && !f.Layout.Stats.PendingWork && f.GatesGreen);
    }

    private static void ReverseDependencyConvergesInWindow()
    {
        // 子定父尺寸 + 父 again 定子：auto 容器套 auto 容器，外层宽再喂一条约束。
        var f = new LayoutFixture();
        NodeHandle outer = f.Box(0f, 0f, 1f, 1f);
        NodeHandle inner = f.Box(0f, 0f, 1f, 1f, outer);
        NodeHandle leaf = f.Box(0f, 0f, 40f, 12f, inner);
        NodeHandle follower = f.Box(0f, 30f, 10f, 10f);
        f.Layout.RegisterLinear(outer, new LinearLayoutDesc { ParentUsesSize = true });
        f.Layout.RegisterLinear(inner, new LinearLayoutDesc { ParentUsesSize = true });
        var b = new ConstraintGraphBuilder(3);
        b.Follow(2, EdgeSel.Size, 1, EdgeSel.Size, LayoutAxis.X);    // follower.W 跟 outer.W
        f.Layout.Arm(b.Seal().Graph!, new[] { f.Table.Root, outer, follower });
        f.Tick();
        // 尺寸差在**布防时**捕获（authored：follower=10、outer=1 ⇒ 差 +9），此后恒随 outer 平移。
        bool settled = f.R(inner).W == 40f && f.R(outer).W == 40f && f.R(follower).W == 49f;
        f.Table.SetSize(leaf, 70f, 12f);                             // 叶变宽 → 两层容器 → 约束跟随
        f.Tick();
        Check("布局 · 反向依赖受控窗收敛：叶→内容器→外容器→跟随者，≤3 轮微排水收敛且无余活",
            settled && f.R(inner).W == 70f && f.R(outer).W == 70f && f.R(follower).W == 79f
            && f.Layout.Stats.DrainPasses <= Abi.LayoutMicroDrainLimit
            && !f.Layout.Stats.PendingWork && f.GatesGreen);
    }

    private static void MicroDrainOverflowIsLoudAndLossless()
    {
        // 五级乒乓链：C_k 自撑（包围层）→ child_{k+1}.W 跟 C_k.W（约束层）→ C_{k+1} 自撑……
        // 每级要一轮（层间交替），主遍 + 3 轮兜底盖不满五级 ⇒ 超限必须有声，余活下一帧续完。
        var f = new LayoutFixture();
        const int levels = 5;
        var panels = new NodeHandle[levels];
        var kids = new NodeHandle[levels];
        for (int k = 0; k < levels; k++)
        {
            panels[k] = f.Box(0f, 20f * k, 1f, 10f);
            kids[k] = f.Box(0f, 0f, 10f, 10f, panels[k]);
            f.Layout.RegisterLinear(panels[k], new LinearLayoutDesc { ParentUsesSize = true });
            if (k > 0)
            {
                var b = new ConstraintGraphBuilder(3);
                b.Follow(1, EdgeSel.Size, 2, EdgeSel.Size, LayoutAxis.X);   // kid_k.W ← panel_{k-1}.W
                f.Layout.Arm(b.Seal().Graph!, new[] { f.Table.Root, kids[k], panels[k - 1] });
            }
        }
        f.Tick();                                                    // 初解（同为多轮，可能已记一次超限）
        f.Tick();
        long overflowsBefore = f.Layout.Stats.MicroDrainOverflows;
        f.Table.SetSize(kids[0], 64f, 10f);                          // 从链头掀一波
        f.Tick();
        bool loud = f.Layout.Stats.MicroDrainOverflows == overflowsBefore + 1
            && f.Layout.Stats.PendingWork
            && f.Layout.Stats.LastOverflowNote.Contains("超限")
            && f.Layout.Stats.LastDifferentialSkipped;               // 超限帧差分按契约跳过
        f.Tick();                                                    // 余活续排：不丢、只延迟
        // 每级尺寸差 +9 在布防时捕获（kid authored 10 − panel authored 1）：链尾 = 64 + 4×9 = 100。
        Check("布局 · 微排水超限有声：计数 + 告警 + 差分跳过，余活下一帧续完（不是死循环也不是静默截断）",
            loud && !f.Layout.Stats.PendingWork
            && f.R(panels[levels - 1]).W == 100f && f.R(kids[levels - 1]).W == 100f
            && f.Layout.Stats.IdempotenceFailures == 0 && f.Layout.Stats.DifferentialFailures == 0);
    }

    // ── 门：P5 幂等断言 ─────────────────────────────────────────────────────

    private static void IdempotenceGateGreenAcrossEdits()
    {
        var f = new LayoutFixture(idem: true, diff: false);
        NodeHandle panel = f.Box(0f, 0f, 120f, 60f);
        NodeHandle c1 = f.Box(0f, 0f, 30f, 10f, panel);
        NodeHandle c2 = f.Box(0f, 0f, 40f, 10f, panel);
        f.Layout.RegisterLinear(panel, new LinearLayoutDesc { Wrap = true, Gap = 2f, LineGap = 2f });
        var b = new ConstraintGraphBuilder(2);
        b.Follow(1, EdgeSel.Size, 0, EdgeSel.Size, LayoutAxis.Y, percent: true);
        NodeHandle tall = f.Box(0f, 0f, 10f, 30f);
        f.Layout.Arm(b.Seal().Graph!, new[] { f.Table.Root, tall });
        f.Tick();
        f.Table.SetSize(panel, 66f, 60f);
        f.Tick();
        f.Table.SetSize(c2, 44f, 12f);
        f.Tick();
        f.Table.SetPosition(c1, 5f, 5f);                             // 流式层拥有位置：会被摆回
        f.Tick();
        Check("门 · P5 幂等断言（正例）：连番编辑后每帧第二遍全量重解零变化",
            f.Layout.Stats.IdempotenceFailures == 0 && !f.Layout.Stats.PendingWork);
    }

    private static void IdempotenceGateRedOnNonConvergentWrite()
    {
        var f = new LayoutFixture(idem: true, diff: false);
        NodeHandle a = f.Box(10f, 0f, 10f, 10f);
        NodeHandle child = f.Box(30f, 0f, 10f, 10f);
        var b = new ConstraintGraphBuilder(3);
        b.Follow(2, EdgeSel.Min, 1, EdgeSel.Max, LayoutAxis.X);
        f.Layout.Arm(b.Seal().Graph!, new[] { f.Table.Root, a, child });
        f.Tick();
        bool greenFirst = f.Layout.Stats.IdempotenceFailures == 0;

        f.Layout.TestAccumulateOpIndex = 0;                          // 负例：算子 0 改写「当前值 + 1」
        f.Table.SetPosition(a, 20f, 0f);
        f.Tick();
        Check("门 · P5 幂等断言（负例）：不收敛写（dst = 当前值 + 1）当帧变红并指名节点",
            greenFirst && f.Layout.Stats.IdempotenceFailures > 0
            && f.Layout.Stats.LastGateError.Contains("幂等"));
    }

    // ── 门：布局差分神谕（L2） ───────────────────────────────────────────────

    /// <summary>
    /// 随机语料（**不等 FGB**：随机手建树 + 随机约束 + 随机尺寸，seed 固定可复现）。
    /// 每棵树跑 4 帧随机编辑，帧级差分神谕 + 幂等门全程随行。
    /// </summary>
    private static void DifferentialOracleGreenOnRandomForest()
    {
        const int trees = 100;
        long failures = 0;
        int oracleRuns = 0;
        string firstError = string.Empty;

        for (int t = 0; t < trees; t++)
        {
            var rng = new LayoutRng(0xFA1717EEUL + (ulong)t);
            var f = new LayoutFixture(idem: true, diff: true);
            var all = new List<NodeHandle>();

            int groupCount = 2 + rng.Range(3);
            NodeHandle attach = default;
            for (int gi = 0; gi < groupCount; gi++)
            {
                NodeHandle parent = f.Box(rng.Range(200), rng.Range(200), 40 + rng.Range(160), 40 + rng.Range(160),
                    rng.Range(3) == 0 ? attach : default);           // 随机深度：偶尔挂到上一组下面
                attach = parent;
                all.Add(parent);
                int kids = 2 + rng.Range(4);
                var byLocal = new NodeHandle[kids + 1];
                byLocal[0] = parent;
                for (int k = 0; k < kids; k++)
                {
                    byLocal[k + 1] = f.Box(rng.Range(150), rng.Range(150), 10 + rng.Range(70), 10 + rng.Range(70), parent);
                    all.Add(byLocal[k + 1]);
                }
                int mode = rng.Range(3);
                if (mode == 0)
                {
                    // 随机约束：src 局部索引 < dst ⇒ 天然无环；每 (dst,axis) 至多 2 且边不重由查重保证。
                    var b = new ConstraintGraphBuilder(kids + 1);
                    var used = new HashSet<int>();
                    int opTries = 1 + rng.Range(2 * kids);
                    for (int a = 0; a < opTries; a++)
                    {
                        int dst = 1 + rng.Range(kids);
                        int axis = rng.Range(2);
                        int edge = rng.Range(4);
                        if (!used.Add((dst << 4) | (axis << 2) | edge)) continue;
                        int countKey = (dst << 4) | (axis << 2);
                        int have = 0;
                        for (int e = 0; e < 4; e++) if (used.Contains(countKey | e)) have++;
                        if (have > 2) { used.Remove((dst << 4) | (axis << 2) | edge); continue; }
                        int src = rng.Range(dst);                    // 0..dst-1：容器或更早的兄弟
                        bool percent = rng.Range(4) == 0;
                        b.Follow((ushort)dst, (EdgeSel)edge, (ushort)src,
                            (EdgeSel)rng.Range(4), (LayoutAxis)axis, percent);
                    }
                    ConstraintGraphResult r = b.Seal();
                    if (r.Ok && r.Graph!.Ops.Length > 0) f.Layout.Arm(r.Graph, byLocal);
                }
                else if (mode == 1)
                {
                    f.Layout.RegisterLinear(parent, new LinearLayoutDesc
                    {
                        Vertical = rng.Range(2) == 1,
                        Wrap = rng.Range(2) == 1,
                        Gap = rng.Range(4),
                        LineGap = rng.Range(3),
                        ParentUsesSize = rng.Range(2) == 1,
                    });
                }
            }

            for (int frame = 0; frame < 4; frame++)
            {
                if (frame > 0)
                {
                    NodeHandle n = all[rng.Range(all.Count)];
                    if (rng.Range(2) == 0) f.Table.SetPosition(n, rng.Range(200), rng.Range(200));
                    else f.Table.SetSize(n, 5 + rng.Range(150), 5 + rng.Range(150));
                }
                f.Tick();
                if (!f.Layout.Stats.LastDifferentialSkipped && !f.Layout.Stats.PendingWork) oracleRuns++;
            }
            long bad = f.Layout.Stats.DifferentialFailures + f.Layout.Stats.IdempotenceFailures;
            failures += bad;
            if (bad > 0 && firstError.Length == 0)
                firstError = "tree " + t + ": " + f.Layout.Stats.LastGateError;
        }

        Check($"门 · 布局差分神谕（正例）: {trees} 棵随机树 × 4 帧，增量腿 ≡ 全量腿逐位（神谕实跑 {oracleRuns} 帧）{firstError}",
            failures == 0 && oracleRuns >= 300);
    }

    private static void DifferentialOracleRedOnMutedDependency()
    {
        var f = new LayoutFixture(idem: false, diff: true);
        NodeHandle a = f.Box(10f, 0f, 10f, 10f);
        NodeHandle child = f.Box(30f, 0f, 10f, 10f);
        var b = new ConstraintGraphBuilder(3);
        b.Follow(2, EdgeSel.Min, 1, EdgeSel.Max, LayoutAxis.X);
        f.Layout.Arm(b.Seal().Graph!, new[] { f.Table.Root, a, child });
        f.Tick();
        bool greenFirst = f.Layout.Stats.DifferentialFailures == 0;

        // 负例：增量腿漏一个依赖——测试内开关吞掉 a 的 FanOut 传播，产品代码一行不改。
        f.Table.TryResolve(a, out uint aIdx);
        f.Layout.TestMuteFanOutIndex = aIdx;
        f.Table.SetPosition(a, 40f, 0f);
        f.Tick();
        Check("门 · 布局差分神谕（负例）：吞掉一条 FanOut 依赖 ⇒ 门红并指名节点",
            greenFirst && f.Layout.Stats.DifferentialFailures > 0
            && f.Layout.Stats.LastGateError.Contains("差分神谕")
            && f.Layout.Stats.LastGateError.Contains("#"));
    }

    // ── 接缝与纪律 ──────────────────────────────────────────────────────────

    private static void SlotlessDrainKeepsDerivedMarks()
    {
        // LayoutStub 接缝（M1-14b-2/M1-14 明文）：无槽节点 authored 即真值，改宽高必须
        // 落一次 Mark(Content|Transform, LayoutDerived)——P6 的脏根窥视与 P7 的消化靠它认领。
        var f = new LayoutFixture();
        NodeHandle n = f.Box(0f, 0f, 10f, 10f);
        f.Tick();
        f.Table.SetSize(n, 20f, 10f);
        f.Tick();
        Check("布局 · 无槽排水保留派生标记（接管 LayoutStub 的接缝语义）",
            f.Inval.LastFrame.MarksOf(InvalidateReason.LayoutDerived) == 1
            && f.Inval.LastFrame.MarksOf(Ch.Content) >= 1 && f.Inval.LastFrame.MarksOf(Ch.Transform) >= 1);
    }

    private static void ResolvedStaysApartFromAuthored()
    {
        // authored/resolved 双列：布局写 resolved，GetPosition 读 authored 逻辑真值（机制③）。
        var b = new ConstraintGraphBuilder(3);
        b.Follow(2, EdgeSel.Min, 1, EdgeSel.Max, LayoutAxis.X);
        var f = new LayoutFixture();
        NodeHandle sib = f.Box(10f, 0f, 20f, 10f);
        NodeHandle child = f.Box(40f, 0f, 10f, 10f);
        f.Layout.Arm(b.Seal().Graph!, new[] { f.Table.Root, sib, child });
        f.Tick();
        f.Table.SetPosition(sib, 25f, 0f);
        f.Tick();
        Check("布局 · resolved 与 authored 分离：布局位移只进 resolved，authored 逻辑值原样、失效归因 LayoutDerived",
            f.R(child).X == 55f && f.Table.GetPosition(child).x == 40f
            && f.Inval.LastFrame.MarksOf(InvalidateReason.LayoutDerived) >= 1);
    }

    private static void ResolvedWriterGateFiresOnWrongSourceAndPhase()
    {
        // 唯一写者门（不变量 2）按 M1-09 的形态延伸：运行期 WriteSource 分流 + 相位门。
        var f = new LayoutFixture();
        NodeHandle n = f.Box(0f, 0f, 10f, 10f);
        var b = new ConstraintGraphBuilder(2);
        b.Follow(1, EdgeSel.Min, 0, EdgeSel.Max, LayoutAxis.X);
        f.Layout.Arm(b.Seal().Graph!, new[] { f.Table.Root, f.Box(0f, 0f, 8f, 8f, n) });
        var fired = new List<string>();
        UiAssert.Handler = fired.Add;
        try
        {
            NodeHandle slotted = f.Table.FirstChild(n);
            f.Table.SetResolved(slotted, 1f, 2f, 3f, 4f, WriteSource.User);   // 帧外（P3 窗口）+ 错来源
        }
        finally { UiAssert.Handler = null; }
        Check("布局 · resolved 唯一写者门：非 Layout 来源 + 非 P5 相位双双断言（gear/tween 在类型上无此写口）",
            fired.Count >= 2
            && fired.Exists(m => m.Contains("唯一写者") && m.Contains("Layout"))
            && fired.Exists(m => m.Contains("仅 P5")));
    }

    private static void LayoutEngineAttachIsExclusive()
    {
        var f = new LayoutFixture();
        bool crossThrew = false, selfThrew = false;
        var second = new LayoutEngine(f.Kernel);
        try { second.Attach(); } catch (InvalidOperationException) { crossThrew = true; }
        try { f.Layout.Attach(); } catch (InvalidOperationException) { selfThrew = true; }
        f.Layout.Detach();
        second.Attach();                                             // 释放后可重接
        Check("布局 · Attach 硬独占（M1-14b 同款）：跨实例占用 throw、重复 Attach throw、Detach 后可重接",
            crossThrew && selfThrew);
    }

    private static void TransformPeekDoesNotConsumeQueue()
    {
        // 窥视纪律：布局借 Transform 的「谁被写了」，消费权仍归 P7。
        // 判据取在 P6 入口：P5 已窥视并反应，队列仍满员（sib 的用户写 + 布局派生的 child 标记），
        // 帧尾才被 P7 的消费者清空。
        var b = new ConstraintGraphBuilder(3);
        b.Follow(2, EdgeSel.Min, 1, EdgeSel.Max, LayoutAxis.X);
        var f = new LayoutFixture();
        NodeHandle sib = f.Box(10f, 0f, 20f, 10f);
        NodeHandle child = f.Box(40f, 0f, 10f, 10f);
        f.Layout.Arm(b.Seal().Graph!, new[] { f.Table.Root, sib, child });
        f.Tick();
        int lenAtP6 = -1;
        f.Kernel.PhaseWatch = phase =>
        {
            if (phase == FramePhase.P6_Settle) lenAtP6 = f.Inval.QueueLength(Ch.Transform);
        };
        f.Table.SetPosition(sib, 25f, 0f);
        f.Tick();
        Check("布局 · Transform 窥视不消费：P5 反应了（跟随生效）而 P6 入口队列仍满员，P7 才清空",
            f.R(child).X == 55f && lenAtP6 >= 2 && f.Inval.QueueLength(Ch.Transform) == 0);
    }
}
