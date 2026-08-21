using FairyNext.Backend.Mock;
using FairyNext.Core;
using FairyNext.Core.Layout;
using FairyNext.Core.Rendering;

namespace FairyNext.Tests;

/// <summary>
/// M1-15 的 Core/mock 半边（Program 的 partial 分片）：
///  · **多面板扇出件**（<see cref="PanelFanout"/>）——内核钩子单占下的多面板分发：
///    各面板各自 PanelRoot、各自后端、互不串台，增量门各自绿；面板增删对帧边界安全；
///  · **上传字节神谕**（<see cref="MockBackend.MirrorProbe"/>，2026-08 审计验收项）——
///    上传区间逐字节对拍 CPU 镜像 + 帧末全量对拍，漏上传/错字节都进 Violations。
/// Unity 顶点流后端本体不进主 runner（CI 无 Unity），验收记录见 unity/README.md。
/// </summary>
public static partial class Program
{
    private static void BackendM15Suite()
    {
        FanoutTwoPanelsBuildDisjointStreams();
        FanoutIncrementalEditStaysInItsPanel();
        FanoutStructureDirtyRebuildsOnlyItsPanel();
        FanoutDownCascadeLandsOnPanelLeaves();
        FanoutHooksStayExclusive();
        FanoutRejectsSharedBackend();
        FanoutAddIsFrameBoundarySafe();
        FanoutRemoveIsFrameBoundarySafe();
        UploadOracleArmsAndStaysGreen();
        UploadOracleCatchesACorruptedByte();
        UploadOracleCatchesAMissedInterval();
    }

    // ── 夹具：一棵树、一个内核、两个面板（各自流/后端/管线/门），经扇出件接线 ──

    private sealed class FanoutFixture
    {
        internal readonly NodeTable Table = new NodeTable(tree: 9);
        internal readonly Invalidation Inval;
        internal readonly UiKernel Kernel;
        internal readonly ContentTable Content = new ContentTable();
        internal readonly LayoutEngine Layout;
        internal readonly PanelFanout Fanout;

        internal readonly RenderStream StreamA = new RenderStream("panel-a");
        internal readonly RenderStream StreamB = new RenderStream("panel-b");
        internal readonly MockBackend BackendA = new MockBackend();
        internal readonly MockBackend BackendB = new MockBackend();
        internal readonly RenderPipeline PipeA;
        internal readonly RenderPipeline PipeB;
        internal readonly IncrementalGate GateA;
        internal readonly IncrementalGate GateB;
        internal readonly NodeHandle RootA;
        internal readonly NodeHandle RootB;
        private FrameTime _time = FrameTime.First(0.016f, 0.016f);

        internal FanoutFixture()
        {
            Inval = new Invalidation(Table);
            Kernel = new UiKernel(Table, Inval);
            Layout = new LayoutEngine(Kernel) { IdempotenceGate = true };
            Layout.Attach();

            RootA = Box(0f, 0f, 200f, 200f);
            RootB = Box(300f, 0f, 200f, 200f);

            PipeA = new RenderPipeline(Kernel, StreamA, Content, BackendA) { DerivedOracle = true };
            PipeA.Extract.PanelRoot = RootA;
            PipeB = new RenderPipeline(Kernel, StreamB, Content, BackendB) { DerivedOracle = true };
            PipeB.Extract.PanelRoot = RootB;

            BackendA.PhaseProbe = () => Kernel.CurrentPhase;
            BackendB.PhaseProbe = () => Kernel.CurrentPhase;
            BackendA.MirrorProbe = h => h.Equals(StreamA.Handle) ? StreamA : null;
            BackendB.MirrorProbe = h => h.Equals(StreamB.Handle) ? StreamB : null;

            Fanout = new PanelFanout(Kernel);
            Fanout.Attach();
            Fanout.Add(PipeA);
            Fanout.Add(PipeB);
            GateA = new IncrementalGate(PipeA);
            GateB = new IncrementalGate(PipeB);
        }

        internal NodeHandle Box(float x, float y, float w, float h, NodeHandle parent = default)
        {
            NodeHandle n = Table.CreateNode(NodeType.Component);
            Table.SetPosition(n, x, y);
            Table.SetSize(n, w, h);
            Table.AddChild(parent.IsNone ? Table.Root : parent, n);
            return n;
        }

        internal NodeHandle Leaf(in LeafSpec spec, float x, float y, float w, float h, NodeHandle parent)
        {
            NodeHandle n = Table.CreateNode(NodeType.Image);
            Table.SetContentRef(n, Content.AddLeaf(in spec));
            Table.SetPosition(n, x, y);
            Table.SetSize(n, w, h);
            Table.AddChild(parent, n);
            return n;
        }

        internal void Tick()
        {
            Kernel.Tick(in _time);
            _time = _time.Step(0.016f, 0.016f);
        }

        /// <summary>两面板都健全 + 两条增量门都绿（失败时打印哪一侧烂了）。</summary>
        internal bool Sound()
        {
            IncrementalGateResult a = GateA.Check();
            IncrementalGateResult b = GateB.Check();
            bool ok = BackendA.Violations.Count == 0 && BackendB.Violations.Count == 0
                && BackendA.Gates.Pass && BackendB.Gates.Pass
                && PipeA.DerivedOracleFailures == 0 && PipeB.DerivedOracleFailures == 0
                && PipeA.PaintOrderFailures == 0 && PipeB.PaintOrderFailures == 0
                && Layout.Stats.IdempotenceFailures == 0
                && a.Pass && b.Pass;
            if (ok) return true;
            Console.WriteLine($"     [unsound] A: violations={BackendA.Violations.Count} gate={a.Describe()}");
            Console.WriteLine($"     [unsound] B: violations={BackendB.Violations.Count} gate={b.Describe()}");
            for (int i = 0; i < BackendA.Violations.Count && i < 3; i++)
                Console.WriteLine("     [A violation] " + BackendA.Violations[i]);
            for (int i = 0; i < BackendB.Violations.Count && i < 3; i++)
                Console.WriteLine("     [B violation] " + BackendB.Violations[i]);
            return false;
        }
    }

    // ── 扇出件：分发与互不串台 ──────────────────────────────────────────────

    /// <summary>两面板各自 PanelRoot、各自后端：一次 Tick 各建各的流，字节互不串台。</summary>
    private static void FanoutTwoPanelsBuildDisjointStreams()
    {
        var f = new FanoutFixture();
        f.Leaf(PipeSolid(1), 10f, 10f, 50f, 50f, f.RootA);
        f.Leaf(PipeSolid(1), 70f, 10f, 50f, 50f, f.RootA);
        f.Leaf(PipeSolid(2), 10f, 10f, 50f, 50f, f.RootB);
        f.Tick();

        StreamSnapshot sa = f.BackendA.Snapshot(f.StreamA.Handle);
        StreamSnapshot sb = f.BackendB.Snapshot(f.StreamB.Handle);
        Check("扇出件: 两面板各建各的流（quad 数与后端快照互不串台）",
            f.StreamA.QuadCount == 2 && f.StreamB.QuadCount == 1
            && sa.Quads.Length == 2 && sb.Quads.Length == 1
            && f.BackendA.CallCount(MockCallKind.CreateStream) == 1
            && f.BackendB.CallCount(MockCallKind.CreateStream) == 1
            && f.Fanout.PanelCount == 2 && f.Sound());
    }

    /// <summary>A 面板的一次增量编辑：A 有上传、B 零上传且走零脏帧短路，两条门照绿。</summary>
    private static void FanoutIncrementalEditStaysInItsPanel()
    {
        var f = new FanoutFixture();
        NodeHandle leafA = f.Leaf(PipeSolid(1), 10f, 10f, 50f, 50f, f.RootA);
        f.Leaf(PipeSolid(2), 10f, 10f, 50f, 50f, f.RootB);
        f.Tick();
        f.Tick();
        f.BackendA.ClearLog();
        f.BackendB.ClearLog();

        f.Table.SetAlpha(leafA, 0.5f);
        f.Tick();
        Check("扇出件: 增量编辑只落属主面板（A 上传、B 零上传 + 零脏帧短路）",
            f.BackendA.CallCount(MockCallKind.UploadInstances) >= 1
            && f.BackendB.CallCount(MockCallKind.UploadInstances) == 0
            && !f.PipeB.LastStats.Dirty && f.PipeB.IdleFrames >= 1
            && f.PipeA.LastStats.Dirty && f.Sound());
    }

    /// <summary>B 面板的结构脏只整编 B：A 的整编数与升级数纹丝不动（路由，不是组播）。</summary>
    private static void FanoutStructureDirtyRebuildsOnlyItsPanel()
    {
        var f = new FanoutFixture();
        f.Leaf(PipeSolid(1), 10f, 10f, 50f, 50f, f.RootA);
        f.Leaf(PipeSolid(2), 10f, 10f, 50f, 50f, f.RootB);
        f.Tick();
        f.Tick();
        int ra = f.PipeA.Extract.Rebuilds, rb = f.PipeB.Extract.Rebuilds;
        int ea = f.PipeA.Drain.Escalations;

        f.Leaf(PipeSolid(2), 70f, 10f, 50f, 50f, f.RootB);
        f.Tick();
        Check("扇出件: 结构脏按 PanelRoot 路由（B 整编 +1，A 整编与升级零增）",
            f.PipeB.Extract.Rebuilds == rb + 1 && f.PipeA.Extract.Rebuilds == ra
            && f.PipeA.Drain.Escalations == ea
            && f.StreamB.QuadCount == 2 && f.StreamA.QuadCount == 1 && f.Sound());
    }

    /// <summary>
    /// 下行级联经扇出件落叶：容器 α 变化 → P6 下钻（表级一次）→ 落叶只落属主面板 →
    /// P7 Color 重上色。落叶半步（StepDownLeaf）被吞的话，活流停在旧色而神谕腿按新 worldVisual
    /// 重编——增量门当场红，所以这条正例就是那半步的守卫。
    /// </summary>
    private static void FanoutDownCascadeLandsOnPanelLeaves()
    {
        var f = new FanoutFixture();
        f.Leaf(PipeSolid(1), 10f, 10f, 50f, 50f, f.RootA);
        f.Leaf(PipeSolid(2), 10f, 10f, 50f, 50f, f.RootB);
        f.Tick();
        f.Tick();
        f.BackendA.ClearLog();

        f.Table.SetAlpha(f.RootA, 0.5f);      // 容器：走 DownColor 子树戳，不是叶的直标
        f.Tick();
        StreamSnapshot sa = f.BackendA.Snapshot(f.StreamA.Handle);
        Check("扇出件: 下行级联落叶只落属主面板（容器 α 半透 → 叶 color 场重算并上传）",
            sa.Quads.Length == 1 && (sa.Quads[0].Color >> 24) == 0x80u
            && f.BackendA.CallCount(MockCallKind.UploadInstances) >= 1
            && f.Sound());
    }

    /// <summary>扇出件占钩子后独占不松动：第二占用者（管线/扇出件）与面板的单独 Attach/Detach 全 throw。</summary>
    private static void FanoutHooksStayExclusive()
    {
        var f = new FanoutFixture();
        bool soloThrows = Throws(() =>
            new RenderPipeline(f.Kernel, new RenderStream("solo"), f.Content, new MockBackend()).Attach());
        bool secondFanoutThrows = Throws(() => new PanelFanout(f.Kernel).Attach());
        bool panelAttachThrows = Throws(() => f.PipeA.Attach());
        bool panelDetachThrows = Throws(() => f.PipeA.Detach());
        Check("扇出件: 钩子独占（第二管线/第二扇出件/面板单独 Attach/Detach 全拦）",
            soloThrows && secondFanoutThrows && panelAttachThrows && panelDetachThrows);
    }

    /// <summary>两面板共用一个后端实例 = 一帧两次 BeginFrame：登记时就拦，不留到真机。</summary>
    private static void FanoutRejectsSharedBackend()
    {
        var f = new FanoutFixture();
        var pipeC = new RenderPipeline(f.Kernel, new RenderStream("panel-c"), f.Content, f.BackendA);
        Check("扇出件: 面板后端互异是硬检查（共享后端实例登记即 throw）",
            Throws(() => f.Fanout.Add(pipeC)));
    }

    /// <summary>帧内 Add 不立即生效：本帧新面板零后端调用，下一帧 BeforeFrame 落地并把流建出来。</summary>
    private static void FanoutAddIsFrameBoundarySafe()
    {
        var f = new FanoutFixture();
        f.Leaf(PipeSolid(1), 10f, 10f, 50f, 50f, f.RootA);
        f.Tick();

        NodeHandle rootC = f.Box(600f, 0f, 200f, 200f);
        var streamC = new RenderStream("panel-c");
        var backendC = new MockBackend { PhaseProbe = () => f.Kernel.CurrentPhase };
        var pipeC = new RenderPipeline(f.Kernel, streamC, f.Content, backendC) { DerivedOracle = true };
        pipeC.Extract.PanelRoot = rootC;
        backendC.MirrorProbe = h => h.Equals(streamC.Handle) ? streamC : null;
        f.Leaf(PipeSolid(3), 5f, 5f, 40f, 40f, rootC);

        bool added = false;
        f.Kernel.PhaseWatch = p =>
        {
            if (p == FramePhase.P3_Commands && !added) { f.Fanout.Add(pipeC); added = true; }
        };
        f.Tick();
        f.Kernel.PhaseWatch = null;
        bool deferred = backendC.CallCount(MockCallKind.BeginFrame) == 0
            && streamC.QuadCount == 0 && f.Fanout.PendingChanges == 1 && f.Fanout.PanelCount == 2;

        f.Tick();
        Check("扇出件: 帧内 Add 延到帧边界（本帧零后端调用，下一帧建流且帧括号完整）",
            deferred && f.Fanout.PanelCount == 3 && f.Fanout.PendingChanges == 0
            && backendC.CallCount(MockCallKind.BeginFrame) == 1
            && backendC.CallCount(MockCallKind.EndFrame) == 1
            && streamC.QuadCount == 1
            && backendC.Violations.Count == 0 && f.Sound());
    }

    /// <summary>帧内 Remove 让面板走完本帧（括号闭合），下一帧起消失；此后它的脏成孤儿有账。</summary>
    private static void FanoutRemoveIsFrameBoundarySafe()
    {
        var f = new FanoutFixture();
        f.Leaf(PipeSolid(1), 10f, 10f, 50f, 50f, f.RootA);
        NodeHandle leafB = f.Leaf(PipeSolid(2), 10f, 10f, 50f, 50f, f.RootB);
        f.Tick();
        f.Tick();
        int endBefore = f.BackendB.CallCount(MockCallKind.EndFrame);

        bool removed = false;
        f.Kernel.PhaseWatch = p =>
        {
            if (p == FramePhase.P3_Commands && !removed) { f.Fanout.Remove(f.PipeB); removed = true; }
        };
        f.Tick();
        f.Kernel.PhaseWatch = null;
        bool ranThisFrame = f.BackendB.CallCount(MockCallKind.EndFrame) == endBefore + 1
            && f.BackendB.Violations.Count == 0 && f.Fanout.PendingChanges == 1;

        long orphansBefore = f.Fanout.OrphanMarks;
        f.Table.SetAlpha(leafB, 0.3f);      // B 即将离册：这条脏该成孤儿而不是砸进 A
        f.Tick();
        Check("扇出件: 帧内 Remove 走完本帧再落地（括号闭合；离册面板的脏成孤儿有账）",
            ranThisFrame && f.Fanout.PanelCount == 1
            && f.BackendB.CallCount(MockCallKind.EndFrame) == endBefore + 1
            && f.Fanout.OrphanMarks > orphansBefore
            && f.BackendA.Violations.Count == 0 && f.GateA.Check().Pass);
    }

    // ── 上传字节神谕（MirrorProbe）────────────────────────────────────────────

    /// <summary>搭一条「探针已装、首帧已干净提交」的流（三条神谕用例共用的起点）。</summary>
    private static RenderStream OracleStream(out MockBackend backend)
    {
        var b = new MockBackend();
        var s = new RenderStream("oracle");
        b.MirrorProbe = h => h.Equals(s.Handle) ? s : null;

        s.BeginRebuild();
        s.OpenRun();
        s.AppendLeaf(RsLeaf(1, 1),
            new[] { RsQuad(0f, 0f, 8f, 8f), RsQuad(10f, 0f, 8f, 8f) },
            new[] { Rgba(10, 20, 30, 255), Rgba(40, 50, 60, 255) });
        s.EndRebuild();
        s.Attach(b);

        b.BeginFrame(1);
        SubmitReport rep = s.Submit(b);
        FrameStats stats = s.BuildStats(1, in rep);
        b.EndFrame(in stats);
        backend = b;
        return s;
    }

    /// <summary>正例：全量帧 + 增量帧都过两道神谕，且神谕**确实跑了**（计数非零，不是静默未布防）。</summary>
    private static void UploadOracleArmsAndStaysGreen()
    {
        RenderStream s = OracleStream(out MockBackend b);

        var half = new IslandVisual { Alpha = 0.5f, Visible = true, Grayed = false };
        s.RecolorLeaf(0, in half);
        b.BeginFrame(2);
        SubmitReport rep = s.Submit(b);
        FrameStats stats = s.BuildStats(2, in rep);
        b.EndFrame(in stats);

        Check("上传神谕: 全量帧 + 增量帧全绿且已布防（两道计数非零、零违约）",
            b.Violations.Count == 0 && b.UploadOracleChecks >= 2 && b.MirrorSweeps == 2
            && rep.Quads == 2);
    }

    /// <summary>负例①：上传时错一个字节 → 区间比对与帧末全量对拍**都**红，并指认 quad 与偏移。</summary>
    private static void UploadOracleCatchesACorruptedByte()
    {
        RenderStream s = OracleStream(out MockBackend b);

        var prev = UiAssert.Handler;
        UiAssert.Handler = _ => { };
        try
        {
            QuadInstance bad = s.Quads[0];
            bad.Color ^= 1u;                              // 错一个字节
            b.BeginFrame(2);
            b.UploadInstances(s.Handle, 0, new[] { bad });
            bool intervalCaught = HasViolation(b, "上传字节与 CPU 镜像不符");
            b.EndFrame(new FrameStats { FrameId = 2, Dirty = true });
            Check("上传神谕: 错一字节必红（上传区间比对 + 帧末全量对拍两道都指认）",
                intervalCaught && HasViolation(b, "帧末后端实例与 CPU 镜像不符"));
        }
        finally { UiAssert.Handler = prev; }
    }

    /// <summary>负例②：镜像变了却没上传（漏区间）→ 帧末全量对拍红——增量门两腿共读镜像抓不到的正是它。</summary>
    private static void UploadOracleCatchesAMissedInterval()
    {
        RenderStream s = OracleStream(out MockBackend b);

        var prev = UiAssert.Handler;
        UiAssert.Handler = _ => { };
        try
        {
            var half = new IslandVisual { Alpha = 0.25f, Visible = true, Grayed = false };
            s.RecolorLeaf(0, in half);                    // 镜像前进
            b.BeginFrame(2);                              // ……但没有任何上传
            b.EndFrame(new FrameStats { FrameId = 2, Dirty = true });
            Check("上传神谕: 漏上传的区间在帧末现形（后端停在旧值 vs 镜像已前进）",
                HasViolation(b, "帧末后端实例与 CPU 镜像不符"));
        }
        finally { UiAssert.Handler = prev; }
    }

    private static bool HasViolation(MockBackend b, string fragment)
    {
        for (int i = 0; i < b.Violations.Count; i++)
            if (b.Violations[i].Contains(fragment, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool Throws(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return true; }
        return false;
    }
}
