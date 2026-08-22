using FairyNext.Backend.Mock;
using FairyNext.Contracts;
using FairyNext.Core;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-23 孤岛②③④用例（Program 的 partial 分片）。
///
/// 对齐 docs/architecture.md「平面三 · 渲染平面」机制 9（孤岛协议）与三条随流契约：
///  · <see cref="IslandDesc.Visual"/> 并入 worldVisual 向下排水（祖先 α/置灰/隐藏时孤岛跟随）；
///  · <c>renderEveryN</c> 分频，滞后 N-1 帧是自愿契约；
///  · 无失效通道的外部内容必须显式 <see cref="IslandMount.MarkDirty"/>，且必须实现
///    <see cref="IIslandContent.StillAnimating"/> 自报以参与零脏帧判定（不变量 13 的第三前提）。
///
/// 两笔 2026-08 审计遗留账在本片结清：
///  ① <c>AnyIslandAnimating</c> 此前恒 false、无门——本片补「活孤岛 ⇒ 不短路」的正例与
///    「自报静止 ⇒ 短路恢复」的收据断言；
///  ② 孤岛表此前不在 <see cref="CanonicalStream"/> 规范形里——本片钉死「每个语义字段都在字节里」
///    与「分配序不是身份」两个方向，另有增量门的红灯用例。
/// </summary>
public static partial class Program
{
    private static void IslandSuite()
    {
        IslandCutsRunAndTakesOneOrder();
        IslandContentAttachesWithMountContext();
        IslandVisualFollowsAncestorAlpha();
        IslandUnderHiddenAncestorLeavesTheStream();
        IslandFollowsDownLayerDrill();
        CustomMaterialTakesClipInclude();
        CustomMaterialRefusingIncludeFallsBackToScissorLoudly();
        ExternalNativeSortingInterleavesWithRuns();
        SpineKindReachesTheBackend();
        StencilBracketsArePaired();
        StencilBracketsNestLifo();
        StencilBracketEndsShareTheCascadedVisual();
        StencilDepthOverflowIsLoud();
        AnimatingIslandBlocksTheZeroDirtyShortcut();
        StillIslandRestoresTheShortcut();
        ContentMarkDirtyDoesNotEscalateToStructure();
        RenderEveryNDividesTheRenderFrames();
        IslandFieldsAreInTheCanonicalForm();
        IslandSlotIsRenumberedNotRaw();
        IslandVisualDriftIsCaughtByTheIncrementalGate();
        StructureGateCoversIslands();
        MirrorAndBackendAgreeOnIslands();
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    /// <summary>可编排的孤岛内容：每个回调计一次数，最后一次 ctx 留着断言。</summary>
    private sealed class TestIslandContent : IIslandContent
    {
        internal bool Include = true;
        internal bool Animating;
        internal int Attaches, Detaches, Syncs, Dirties, RenderFrames;
        internal IslandContext Last;
        internal IslandMount? Mount;

        public bool AcceptsClipInclude => Include;
        public bool StillAnimating => Animating;

        public void OnAttach(in IslandContext ctx)
        {
            Attaches++;
            Mount = ctx.Mount;
            Last = ctx;
        }

        public void OnSync(in IslandContext ctx)
        {
            Syncs++;
            if (ctx.RenderThisFrame) RenderFrames++;
            Last = ctx;
        }

        public void MarkDirty() => Dirties++;

        public void OnDetach() => Detaches++;
    }

    /// <summary>开一个裁剪域的容器记录（域的 rect 恒等于节点自己的框）。</summary>
    private static ContentRecord IslandClipBox() => new ContentRecord
    {
        Kind = ExtractKind.None,
        OpensClip = true,
        Clip = new ClipShape { Soft = Vector2.zero, Radii = Vector4.zero },
        RenderEveryN = 1,
    };

    /// <summary>建一个孤岛节点（容器节点 + AddIsland 登记；返回节点句柄）。</summary>
    private static NodeHandle IslandNode(PipeFixture f, TestIslandContent content, IslandKind kind,
        float x, float y, float w, float h, NodeHandle parent = default,
        IslandNativeKind native = IslandNativeKind.None, int everyN = 1, string? name = null)
    {
        NodeHandle n = f.Box(x, y, w, h, parent);
        f.Pipe.AddIsland(n, kind, content, native, everyN, name);
        return n;
    }

    // ── 切 run 与序 ─────────────────────────────────────────────────────────

    /// <summary>孤岛切 run：前后 run 都完整、序不乱，且孤岛**占一个 run 序**（严格介于前后之间）。</summary>
    private static void IslandCutsRunAndTakesOneOrder()
    {
        var f = new PipeFixture();
        NodeHandle a = f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f);
        var content = new TestIslandContent();
        IslandNode(f, content, IslandKind.ExternalNative, 20f, 0f, 10f, 10f, name: "spine");
        NodeHandle b = f.Leaf(PipeSolid(1), 40f, 0f, 10f, 10f);
        IncrementalGateResult g = f.TickAndCheck();

        ReadOnlySpan<LeafRange> leaves = f.Stream.Leaves;
        IslandRecord isle = f.Stream.Island(0);
        RunOrder[] orders = f.Stream.BuildRunOrders();

        Check("孤岛①: 切 run 后前后 run 完整、序不乱，孤岛独占一个 run 序（严格介于前后之间）",
            f.Stream.IslandCount == 1 && f.Stream.RunCount == 2 && leaves.Length == 2
            && leaves[0].Node.Equals(a) && leaves[1].Node.Equals(b)
            && leaves[0].Run == 0 && leaves[1].Run == 1
            && orders[0].SortingOrder < isle.SortingOrder
            && isle.SortingOrder < orders[1].SortingOrder
            && f.Stream.Runs[0].ClosedByIsland == 0
            && g.Pass && f.Sound());
    }

    /// <summary>AddIsland → OnAttach：内容拿到槽/clip/深度区间与回调句柄，且**只挂一次**。</summary>
    private static void IslandContentAttachesWithMountContext()
    {
        var f = new PipeFixture();
        var content = new TestIslandContent();
        NodeHandle node = IslandNode(f, content, IslandKind.ExternalNative, 5f, 7f, 30f, 20f,
            native: IslandNativeKind.Spine, name: "hero");
        f.TickAndCheck();
        int attachesAfterFirst = content.Attaches;
        int syncsAfterFirst = content.Syncs;
        IncrementalGateResult g = f.TickAndCheck();      // 静止一帧：只能多一次 OnSync，不许再 OnAttach

        IslandContext ctx = content.Last;
        IslandRecord rec = f.Stream.Island(0);

        Check("孤岛①: OnAttach 一次、OnSync 每帧；ctx 带槽矩阵/裁剪/深度区间/回调句柄",
            attachesAfterFirst == 1 && content.Attaches == 1
            && syncsAfterFirst == 1 && content.Syncs == 2
            && ctx.Node.Equals(node) && ctx.NativeKind == IslandNativeKind.Spine
            && ctx.Kind == IslandKind.ExternalNative
            && ctx.SortingOrder == rec.SortingOrder
            && ctx.ZMin == rec.PaintOrderIndex * (float)Abi.PaintOrderStride
            && ctx.ZMax == ctx.ZMin + Abi.PaintOrderStride
            && ctx.Width == 30f && ctx.Height == 20f
            && ctx.ClipMode == IslandClipMode.None
            && content.Mount != null && content.Mount.Node.Equals(node)
            && f.Pipe.Islands.IsAttached(node)
            && g.Pass && f.Sound());
    }

    // ── visual 并入下行 ─────────────────────────────────────────────────────

    /// <summary>祖先 SetAlpha ⇒ 孤岛的级联视觉跟随，且**不整编**（级联不是结构变化）。</summary>
    private static void IslandVisualFollowsAncestorAlpha()
    {
        var f = new PipeFixture();
        NodeHandle box = f.Box(0f, 0f, 100f, 100f);
        var content = new TestIslandContent();
        IslandNode(f, content, IslandKind.CustomMaterial, 0f, 0f, 20f, 20f, box);
        f.TickAndCheck();
        int rebuilds = f.Pipe.Extract.Rebuilds;

        f.Table.SetAlpha(box, 0.5f);
        f.Table.SetGrayed(box, true);
        IncrementalGateResult g = f.TickAndCheck();

        IslandRecord rec = f.Stream.Island(0);
        Check("孤岛②: visual 并入下行——祖先 α/置灰级联到孤岛，无整编，增量门绿",
            f.Stream.IslandCount == 1
            && rec.Visual.Alpha > 0.4f && rec.Visual.Alpha < 0.6f && rec.Visual.Grayed
            && rec.Visual.Visible
            && content.Last.Visual.Grayed && content.Dirties >= 1
            && f.Pipe.Extract.Rebuilds == rebuilds
            && f.Pipe.Drain.IslandRecolors >= 1
            && g.Pass && f.Sound());
    }

    /// <summary>
    /// 隐藏容器下的孤岛**整只离开流**（不是画成透明）——踩的是 14b CRITICAL 那个面：
    /// 判据必须是 authored 父链传播，不是（合法陈旧的）worldVisual。
    /// </summary>
    private static void IslandUnderHiddenAncestorLeavesTheStream()
    {
        var f = new PipeFixture();
        NodeHandle box = f.Box(0f, 0f, 100f, 100f);
        var content = new TestIslandContent();
        IslandNode(f, content, IslandKind.ExternalNative, 0f, 0f, 20f, 20f, box);
        f.TickAndCheck();
        bool inStreamFirst = f.Stream.IslandCount == 1 && content.Detaches == 0;

        f.Table.SetVisible(box, false);
        IncrementalGateResult hidden = f.TickAndCheck();
        bool gone = f.Stream.IslandCount == 0 && content.Detaches == 1
            && !f.Pipe.Islands.IsAttached(f.Stream.Island(0).Node);

        f.Table.SetVisible(box, true);
        IncrementalGateResult shown = f.TickAndCheck();
        // 重编之后后端句柄必须**重新建出来**：BeginRebuild 把上一代句柄全拆了，
        // 只在首帧 attach 一次的接线会让重新显示的孤岛此后再也收不到 SyncIsland。
        IslandRecord back = f.Stream.Island(0);
        bool backAgain = f.Stream.IslandCount == 1 && content.Attaches == 2
            && !back.Handle.IsNone
            && f.Backend.IslandSyncOf(back.Handle).SortingOrder == back.SortingOrder;

        // 孤岛自己隐藏也是同一条路：可见性跃迁 = 进/出流 = 数量变化 = 一次整编。
        // 原位把 visual 清成不可见画面也对，但整编产物里**没有这条记录**，逐字节门当场变红。
        NodeHandle self = f.Stream.Island(0).Node;
        f.Table.SetVisible(self, false);
        IncrementalGateResult selfHidden = f.TickAndCheck();

        Check("孤岛②: 隐藏祖先下的孤岛整只离开流（内容收 OnDetach），重新显示时重新挂上；自己隐藏同理",
            inStreamFirst && gone && hidden.Pass && backAgain && shown.Pass
            && f.Stream.IslandCount == 0 && content.Detaches == 2
            && selfHidden.Pass && f.Sound());
    }

    /// <summary>
    /// <see cref="Ch.DownLayer"/> 下钻落到孤岛（M1-21 转登记的行为覆盖）：
    /// 「整棵子树翻层」的第一个真实消费者是孤岛——本用例钉死下钻确实访问到它并交回上行通道。
    /// 归属表里仍**零产品写者**（没有属性写 DownLayer），故 EventTests 的登记门原样转给 M2-12。
    /// </summary>
    private static void IslandFollowsDownLayerDrill()
    {
        var f = new PipeFixture();
        NodeHandle box = f.Box(0f, 0f, 100f, 100f);       // 子树里只有孤岛，没有叶
        var content = new TestIslandContent();
        IslandNode(f, content, IslandKind.ExternalNative, 0f, 0f, 20f, 20f, box);
        f.TickAndCheck();
        int rebuilds = f.Pipe.Extract.Rebuilds;

        f.Inval.MarkDown(box, Ch.DownLayer, InvalidateReason.GearPage);
        IncrementalGateResult g = f.TickAndCheck();

        Check("孤岛②: DownLayer 下钻落到孤岛并交回 Ch.Color/CascadeDown（子树里没有叶，标记只可能来自孤岛）",
            f.Inval.LastFrame.MarksOf(Ch.Color) >= 1
            && f.Inval.LastFrame.MarksOf(InvalidateReason.CascadeDown) >= 1
            && f.Stream.IslandCount == 1 && f.Pipe.Extract.Rebuilds == rebuilds
            && g.Pass && f.Sound());
    }

    // ── ②自定义材质：clip include / scissor 降级 ────────────────────────────

    /// <summary>材质接受 include ⇒ clip 走 shader 注入，零降级。</summary>
    private static void CustomMaterialTakesClipInclude()
    {
        var f = new PipeFixture();
        NodeHandle window = f.Box(0f, 0f, 60f, 60f);
        f.Table.SetContentRef(window, f.Content.Add(IslandClipBox()));
        var content = new TestIslandContent { Include = true };
        IslandNode(f, content, IslandKind.CustomMaterial, 5f, 5f, 20f, 20f, window);
        IncrementalGateResult g = f.TickAndCheck();

        IslandRecord rec = f.Stream.Island(0);
        Check("孤岛②: 材质接受 clip include ⇒ ShaderInclude 下发，零降级",
            rec.ClipMode == IslandClipMode.ShaderInclude && rec.ClipEntry != ClipBook.NoneEntry
            && content.Last.ClipMode == IslandClipMode.ShaderInclude
            && content.Last.ClipRect.z > content.Last.ClipRect.x
            && f.Stream.Degrades.CountOf(DegradeKind.ScissorFallback) == 0
            && g.Pass && f.Sound());
    }

    /// <summary>材质拒绝 include ⇒ scissor 降级，且**有声**（屏幕轴对齐，旋转裁剪下无静默正确解）。</summary>
    private static void CustomMaterialRefusingIncludeFallsBackToScissorLoudly()
    {
        var f = new PipeFixture();
        NodeHandle window = f.Box(0f, 0f, 60f, 60f);
        f.Table.SetContentRef(window, f.Content.Add(IslandClipBox()));
        var content = new TestIslandContent { Include = false };
        IslandNode(f, content, IslandKind.CustomMaterial, 5f, 5f, 20f, 20f, window);
        IncrementalGateResult g = f.TickAndCheck();

        IslandRecord rec = f.Stream.Island(0);
        Check("孤岛②: 材质拒绝 include ⇒ scissor 降级且计一次阶梯事件（无声降级是被禁的那一种）",
            rec.ClipMode == IslandClipMode.Scissor
            && content.Last.ClipMode == IslandClipMode.Scissor
            && f.Stream.Degrades.CountOf(DegradeKind.ScissorFallback) == 1
            && f.Backend.Gates.Degrade == 1
            && f.Backend.Violations.Count == 0
            && g.Pass);
    }

    // ── ③外部原生：序对齐与具名 kind ────────────────────────────────────────

    /// <summary>
    /// 穿插场景（叶 → 岛 → 叶 → 岛 → 叶）：孤岛序号与 run 序**严格交替递增**。
    /// 外部渲染器（Unity SortingGroup）读的就是这个数——序号来源不同的两个渲染器一定穿插错乱。
    /// </summary>
    private static void ExternalNativeSortingInterleavesWithRuns()
    {
        var f = new PipeFixture();
        f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f);
        var c1 = new TestIslandContent();
        IslandNode(f, c1, IslandKind.ExternalNative, 20f, 0f, 10f, 10f, name: "i1");
        f.Leaf(PipeSolid(1), 40f, 0f, 10f, 10f);
        var c2 = new TestIslandContent();
        IslandNode(f, c2, IslandKind.ExternalNative, 60f, 0f, 10f, 10f, name: "i2");
        f.Leaf(PipeSolid(1), 80f, 0f, 10f, 10f);
        IncrementalGateResult g = f.TickAndCheck();

        RunOrder[] runs = f.Stream.BuildRunOrders();
        IslandRecord i1 = f.Stream.Island(0), i2 = f.Stream.Island(1);
        IslandSync s1 = f.Backend.IslandSyncOf(i1.Handle);
        IslandSync s2 = f.Backend.IslandSyncOf(i2.Handle);

        Check("孤岛③: SortingGroup 序与 run 序同源——穿插场景下严格交替递增，后端收到同一个数",
            f.Stream.IslandCount == 2 && runs.Length == 3
            && runs[0].SortingOrder < i1.SortingOrder && i1.SortingOrder < runs[1].SortingOrder
            && runs[1].SortingOrder < i2.SortingOrder && i2.SortingOrder < runs[2].SortingOrder
            && s1.SortingOrder == i1.SortingOrder && s2.SortingOrder == i2.SortingOrder
            && g.Pass && f.Sound());
    }

    /// <summary>Spine 作为具名 kind 落到后端（核心侧只有一个 byte，不引任何 Spine 依赖）。</summary>
    private static void SpineKindReachesTheBackend()
    {
        var f = new PipeFixture();
        var spine = new TestIslandContent();
        IslandNode(f, spine, IslandKind.ExternalNative, 0f, 0f, 10f, 10f,
            native: IslandNativeKind.Spine, name: "hero");
        var plain = new TestIslandContent();
        IslandNode(f, plain, IslandKind.CustomMaterial, 20f, 0f, 10f, 10f,
            native: IslandNativeKind.Spine, name: "mat");   // ②不吃具名 kind：登记时就归零
        IncrementalGateResult g = f.TickAndCheck();

        ReadOnlySpan<IslandRecord> backend = f.Backend.Snapshot(f.Stream.Handle).Islands;
        Check("孤岛③: Spine 具名 kind 随描述到后端；②不吃具名 kind（登记时归零）",
            backend.Length == 2
            && backend[0].NativeKind == IslandNativeKind.Spine
            && backend[1].NativeKind == IslandNativeKind.None
            && f.Stream.Island(0).NativeKind == IslandNativeKind.Spine
            && f.Stream.Island(1).NativeKind == IslandNativeKind.None
            && g.Pass && f.Sound());
    }

    // ── ④stencil 括号 ───────────────────────────────────────────────────────

    /// <summary>括号成对：Enter 在子树之前、Exit 在子树之后，两条记录同节点同深度。</summary>
    private static void StencilBracketsArePaired()
    {
        var f = new PipeFixture();
        var content = new TestIslandContent();
        NodeHandle mask = IslandNode(f, content, IslandKind.StencilMask, 0f, 0f, 50f, 50f, name: "mask");
        f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f, mask);
        f.Leaf(PipeSolid(1), 20f, 0f, 10f, 10f);          // 域外的兄弟：必须在 Exit 之后
        IncrementalGateResult g = f.TickAndCheck();

        IslandRecord enter = f.Stream.Island(0), exit = f.Stream.Island(1);
        ReadOnlySpan<LeafRange> leaves = f.Stream.Leaves;
        int insideRun = leaves[0].Run, outsideRun = leaves[1].Run;

        Check("孤岛④: stencil 括号成对——Enter 在内容之前、Exit 在内容之后，同节点同深度",
            f.Stream.IslandCount == 2
            && enter.Bracket == IslandBracket.Enter && exit.Bracket == IslandBracket.Exit
            && enter.Node.Equals(mask) && exit.Node.Equals(mask)
            && enter.StencilDepth == 1 && exit.StencilDepth == 1
            && enter.SortingOrder < f.Stream.Runs[insideRun].SortingOrder
            && f.Stream.Runs[insideRun].SortingOrder < exit.SortingOrder
            && exit.SortingOrder < f.Stream.Runs[outsideRun].SortingOrder
            && content.Attaches == 1 && content.Syncs == 1     // Exit 半边不进内容的账
            && g.Pass && f.Sound());
    }

    /// <summary>嵌套按 LIFO：Enter1 Enter2 Exit2 Exit1，深度 1,2,2,1。</summary>
    private static void StencilBracketsNestLifo()
    {
        var f = new PipeFixture();
        var outer = new TestIslandContent();
        NodeHandle a = IslandNode(f, outer, IslandKind.StencilMask, 0f, 0f, 50f, 50f, name: "outer");
        var inner = new TestIslandContent();
        NodeHandle b = IslandNode(f, inner, IslandKind.StencilMask, 0f, 0f, 30f, 30f, a, name: "inner");
        f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f, b);
        IncrementalGateResult g = f.TickAndCheck();

        ReadOnlySpan<IslandRecord> isles = f.Stream.Islands;
        bool shape = isles.Length == 4
            && isles[0].Bracket == IslandBracket.Enter && isles[0].StencilDepth == 1 && isles[0].Node.Equals(a)
            && isles[1].Bracket == IslandBracket.Enter && isles[1].StencilDepth == 2 && isles[1].Node.Equals(b)
            && isles[2].Bracket == IslandBracket.Exit && isles[2].StencilDepth == 2 && isles[2].Node.Equals(b)
            && isles[3].Bracket == IslandBracket.Exit && isles[3].StencilDepth == 1 && isles[3].Node.Equals(a);
        bool ordered = isles[0].SortingOrder < isles[1].SortingOrder
            && isles[1].SortingOrder < isles[2].SortingOrder
            && isles[2].SortingOrder < isles[3].SortingOrder;

        Check("孤岛④: 嵌套 stencil 按 LIFO 闭合（Enter1 Enter2 Exit2 Exit1，深度 1,2,2,1）",
            shape && ordered && g.Pass && f.Sound());
    }

    /// <summary>
    /// 括号两端共享级联视觉：Enter 改了而 Exit 没改 ⇒ 同一个孤岛的两条记录对同一帧给出两种说法，
    /// 增量门当场变红（神谕腿是从树重编出来的，两端一致）。
    /// </summary>
    private static void StencilBracketEndsShareTheCascadedVisual()
    {
        var f = new PipeFixture();
        NodeHandle box = f.Box(0f, 0f, 100f, 100f);
        var content = new TestIslandContent();
        NodeHandle mask = IslandNode(f, content, IslandKind.StencilMask, 0f, 0f, 50f, 50f, box, name: "mask");
        f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f, mask);
        f.TickAndCheck();
        int rebuilds = f.Pipe.Extract.Rebuilds;

        f.Table.SetAlpha(box, 0.25f);
        IncrementalGateResult g = f.TickAndCheck();

        IslandRecord enter = f.Stream.Island(0), exit = f.Stream.Island(1);
        Check("孤岛④: 级联视觉同时落到括号两端（Enter/Exit 是同一个孤岛的两个端点）",
            f.Stream.IslandCount == 2
            && enter.Visual.Equals(exit.Visual)
            && enter.Visual.Alpha > 0.2f && enter.Visual.Alpha < 0.3f
            && f.Pipe.Extract.Rebuilds == rebuilds
            && g.Pass && f.Sound());
    }

    /// <summary>嵌套超预算 ⇒ 该孤岛不进流并计一次阶梯（模板值回绕的症状无法与「没画」区分）。</summary>
    private static void StencilDepthOverflowIsLoud()
    {
        var f = new PipeFixture();
        NodeHandle parent = default;
        var contents = new TestIslandContent[IslandTable.StencilDepthBudget + 1];
        for (int i = 0; i < contents.Length; i++)
        {
            contents[i] = new TestIslandContent();
            parent = IslandNode(f, contents[i], IslandKind.StencilMask, 0f, 0f, 50f, 50f, parent,
                name: "mask" + i);
        }
        f.TickAndCheck();

        int deepest = 0;
        ReadOnlySpan<IslandRecord> isles = f.Stream.Islands;
        for (int i = 0; i < isles.Length; i++) if (isles[i].StencilDepth > deepest) deepest = isles[i].StencilDepth;

        Check("孤岛④: stencil 嵌套超预算 ⇒ 不进流 + 一次 StencilDepthOverflow 阶梯（不静默回绕）",
            deepest == IslandTable.StencilDepthBudget
            && isles.Length == IslandTable.StencilDepthBudget * 2
            && f.Stream.Degrades.CountOf(DegradeKind.StencilDepthOverflow) == 1
            && contents[IslandTable.StencilDepthBudget].Attaches == 0
            && f.Backend.Violations.Count == 0);
    }

    // ── 零脏帧短路的第三前提 ────────────────────────────────────────────────

    /// <summary>活孤岛自报仍在动 ⇒ stats.Dirty 为真、present 照涨（不许短路）。</summary>
    private static void AnimatingIslandBlocksTheZeroDirtyShortcut()
    {
        var f = new PipeFixture();
        f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f);
        var content = new TestIslandContent();
        IslandNode(f, content, IslandKind.ExternalNative, 20f, 0f, 10f, 10f, name: "spine");
        f.Tick();                                   // 首帧：建流 + 整编 + 全量上传
        f.Tick();                                   // 静止帧：短路生效
        ulong presentsBefore = f.Pipe.Presents;
        ulong idleBefore = f.Pipe.IdleFrames;

        content.Animating = true;
        f.Tick();
        bool dirtyFrame = f.Pipe.LastStats.Dirty && f.Pipe.LastReport.IsIdle
            && f.Pipe.Presents == presentsBefore + 1 && f.Pipe.IdleFrames == idleBefore;

        Check("不变量 13 第三前提: 孤岛自报仍在动 ⇒ 提交零调用但 stats.Dirty 为真、present 照涨 "
              + f.Pipe.DescribeReceipt(),
            dirtyFrame && f.Stream.AnyIslandAnimating() && !f.Backend.AllIslandsStill()
            && f.Pipe.LastIslandSync.Animating == 1 && f.Sound());
    }

    /// <summary>自报静止 ⇒ 短路恢复（收据里 ticks 照涨、presents 不涨）。</summary>
    private static void StillIslandRestoresTheShortcut()
    {
        var f = new PipeFixture();
        f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f);
        var content = new TestIslandContent { Animating = true };
        IslandNode(f, content, IslandKind.ExternalNative, 20f, 0f, 10f, 10f, name: "spine");
        f.Tick();
        f.Tick();
        f.Tick();
        ulong presentsWhileAnimating = f.Pipe.Presents;
        ulong ticksWhileAnimating = f.Pipe.Ticks;
        bool noShortcutWhileAnimating = f.Pipe.IdleFrames == 0 && presentsWhileAnimating == 3;

        content.Animating = false;
        bool green = true;
        for (int i = 0; i < 4; i++) green &= f.TickAndCheck().Pass;

        Check("不变量 13 第三前提: 孤岛停了 ⇒ 短路恢复（ticks 照涨、presents 不涨、七条队列全零）"
              + " " + f.Pipe.DescribeReceipt(),
            noShortcutWhileAnimating && green
            && f.Pipe.Ticks == ticksWhileAnimating + 4
            && f.Pipe.Presents == presentsWhileAnimating
            && f.Pipe.IdleFrames == 4
            && f.AllQueuesEmpty() && f.Backend.AllIslandsStill()
            && !f.Pipe.LastStats.Dirty && f.Sound());
    }

    /// <summary>
    /// 内容自报脏（内容 → 运行时）走 Content 通道，落成一次孤岛同步——
    /// **不是**一次面板整编：把每一次 Spine 帧推进判成结构变化，等于把最常见的外部动效变成最贵的路径。
    /// </summary>
    private static void ContentMarkDirtyDoesNotEscalateToStructure()
    {
        var f = new PipeFixture();
        f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f);
        var content = new TestIslandContent();
        IslandNode(f, content, IslandKind.ExternalNative, 20f, 0f, 10f, 10f, name: "spine");
        f.Tick();
        f.Tick();
        int rebuilds = f.Pipe.Extract.Rebuilds;
        ulong presents = f.Pipe.Presents;
        int escalations = f.Pipe.Drain.Escalations;

        content.Mount!.MarkDirty();
        IncrementalGateResult g = f.TickAndCheck();

        Check("孤岛③: 内容自报脏走 Content 通道 ⇒ 本帧不短路，但**不整编**（外部动效不是结构变化）",
            f.Pipe.Extract.Rebuilds == rebuilds && f.Pipe.Drain.Escalations == escalations
            && f.Pipe.Drain.IslandDirtyTouches == 1
            && f.Pipe.Islands.DirtyMarks == 1 && content.Mount.DirtyMarks == 1
            && f.Pipe.LastStats.Dirty && f.Pipe.Presents == presents + 1
            && g.Pass && f.Sound());
    }

    /// <summary>renderEveryN 分频：N=2 ⇒ 隔帧才轮到重画（滞后 N-1 帧是自愿契约）。</summary>
    private static void RenderEveryNDividesTheRenderFrames()
    {
        var f = new PipeFixture();
        var every2 = new TestIslandContent();
        IslandNode(f, every2, IslandKind.CustomMaterial, 0f, 0f, 10f, 10f, everyN: 2, name: "rt");
        var every1 = new TestIslandContent();
        IslandNode(f, every1, IslandKind.CustomMaterial, 20f, 0f, 10f, 10f, everyN: 1, name: "live");
        for (int i = 0; i < 4; i++) f.Tick();

        Check("孤岛②: renderEveryN 分频——N=2 的孤岛每帧收 OnSync，但只在半数帧轮到重画",
            every1.Syncs == 4 && every1.RenderFrames == 4
            && every2.Syncs == 4 && every2.RenderFrames == 2
            && f.Pipe.LastIslandSync.Synced == 2 && f.Sound());
    }

    // ── 规范形与增量门 ──────────────────────────────────────────────────────

    /// <summary>
    /// 孤岛表**进规范形**：每个语义字段都真的在字节里（删掉任何一个，本用例即红）。
    /// 手建两条只差一个字段的流，逐字段比对——规范形是「同画面同字节」，不是「差不多」。
    /// </summary>
    private static void IslandFieldsAreInTheCanonicalForm()
    {
        byte[] baseline = IslandCanonical(d => { });
        bool kind = Differs(baseline, IslandCanonical(d => d.Kind = IslandKind.StencilMask));
        bool native = Differs(baseline, IslandCanonical(d => d.NativeKind = IslandNativeKind.Spine));
        bool bracket = Differs(baseline, IslandCanonical(d => d.Bracket = IslandBracket.Enter));
        bool depth = Differs(baseline, IslandCanonical(d => d.StencilDepth = 3));
        bool clipMode = Differs(baseline, IslandCanonical(d => d.ClipMode = IslandClipMode.Scissor));
        bool everyN = Differs(baseline, IslandCanonical(d => d.RenderEveryN = 4));
        bool alpha = Differs(baseline, IslandCanonical(d =>
            d.Visual = new IslandVisual { Alpha = 0.5f, Visible = true, Grayed = false }));
        bool grayed = Differs(baseline, IslandCanonical(d =>
            d.Visual = new IslandVisual { Alpha = 1f, Visible = true, Grayed = true }));
        bool visible = Differs(baseline, IslandCanonical(d =>
            d.Visual = new IslandVisual { Alpha = 1f, Visible = false, Grayed = false }));
        // 反面：诊断名与节点句柄**不是**像素（句柄含代际位，进哈希会让重建即差异）。
        bool nameIsNotPixels = !Differs(baseline, IslandCanonical(d => d.DebugName = "another"));
        bool nodeIsNotPixels = !Differs(baseline, IslandCanonical(d => d.Node = new NodeHandle(9, 3, 1)));

        Check("规范形: 孤岛的九个语义字段逐个进字节；DebugName 与节点句柄有意缺席",
            kind && native && bracket && depth && clipMode && everyN && alpha && grayed && visible
            && nameIsNotPixels && nodeIsNotPixels);
    }

    /// <summary>孤岛引用的槽走**首用序重编号**：分配序漂移不是差异（否则增量门在孤岛上假红）。</summary>
    private static void IslandSlotIsRenumberedNotRaw()
    {
        byte[] tight = IslandSlotCanonical(padSlots: 0);
        byte[] drifted = IslandSlotCanonical(padSlots: 3);
        Check("规范形: 孤岛的槽下标按首用序重编号——分配序漂移（前面先占 3 个槽）不改一个字节",
            CanonicalStream.FirstDifference(tight, drifted) < 0 && tight.Length > 0);
    }

    /// <summary>孤岛属性漂移 ⇒ 增量门必红，且定位到孤岛段（门在孤岛维度上不再是空的）。</summary>
    private static void IslandVisualDriftIsCaughtByTheIncrementalGate()
    {
        var f = new PipeFixture();
        var content = new TestIslandContent();
        IslandNode(f, content, IslandKind.ExternalNative, 0f, 0f, 20f, 20f, name: "spine");
        IncrementalGateResult green = f.TickAndCheck();

        // 直接改活流的孤岛 visual（模拟「级联漏了一条孤岛」）：树没变，神谕腿会重算出正确值。
        f.Stream.SetIslandVisual(0, new IslandVisual { Alpha = 0.25f, Visible = true, Grayed = false });
        IncrementalGateResult red = f.Gate.Check();

        Check("门 · 增量正确性: 孤岛属性漂移当场变红并定位到孤岛段（改前全绿）",
            green.Pass && !red.Pass
            && red.Site.Section == CanonicalSection.Islands
            && red.Site.Field == "visual"
            && (red.Site.Channel & Ch.Color) != 0);
    }

    /// <summary>流结构不变量门覆盖孤岛：被隐藏祖先罩住的孤岛还在流里 ⇒ 门红（M1-14b 登记结账）。</summary>
    private static void StructureGateCoversIslands()
    {
        var f = new PipeFixture();
        NodeHandle box = f.Box(0f, 0f, 100f, 100f);
        var content = new TestIslandContent();
        IslandNode(f, content, IslandKind.ExternalNative, 0f, 0f, 20f, 20f, box);
        f.TickAndCheck();
        bool greenWhileVisible = StreamStructureGate.Check(f.Table, f.Stream, out _);

        // 只隐藏、不 tick：流此刻仍持有那条孤岛记录——这正是门要抓的状态。
        f.Table.SetVisible(box, false);
        bool red = !StreamStructureGate.Check(f.Table, f.Stream, out string err);

        f.TickAndCheck();                       // 整编把它摘掉，门恢复绿
        bool greenAfterRebuild = StreamStructureGate.Check(f.Table, f.Stream, out _);

        Check("门 · 流结构不变量: 隐藏祖先罩住的**孤岛**被抓（叶之外的第二类单元也进门）",
            greenWhileVisible && red && err.Contains("孤岛") && greenAfterRebuild
            && f.Stream.IslandCount == 0);
    }

    /// <summary>镜像 == 后端字节（含孤岛段）：后端收到的孤岛与 CPU 镜像持有的逐字节相等。</summary>
    private static void MirrorAndBackendAgreeOnIslands()
    {
        var f = new PipeFixture();
        f.Leaf(PipeSolid(1), 0f, 0f, 10f, 10f);
        var content = new TestIslandContent();
        IslandNode(f, content, IslandKind.ExternalNative, 20f, 0f, 10f, 10f,
            native: IslandNativeKind.DragonBones, name: "db");
        f.TickAndCheck();
        f.Backend.SetBaseColors(f.Stream.Handle, 0, f.Stream.BaseColors);

        byte[] mine = CanonicalStream.Canonicalize(f.Stream.Snapshot());
        byte[] theirs = CanonicalStream.Canonicalize(f.Backend.Snapshot(f.Stream.Handle));

        Check("门 · 上传字节: 镜像与后端的规范形逐字节相等（孤岛段同样对得上）",
            CanonicalStream.FirstDifference(mine, theirs) < 0
            && f.Stream.IslandCount == 1 && mine.Length > 0 && f.Sound());
    }

    // ── 规范形用例的手建流 ──────────────────────────────────────────────────

    private static bool Differs(byte[] a, byte[] b) => CanonicalStream.FirstDifference(a, b) >= 0;

    /// <summary>一条「一个叶 + 一个孤岛」的手建流的规范形；<paramref name="tweak"/> 改孤岛描述的一个字段。</summary>
    private static byte[] IslandCanonical(Action<IslandDescBox> tweak)
    {
        var box = new IslandDescBox
        {
            Kind = IslandKind.ExternalNative,
            NativeKind = IslandNativeKind.None,
            Bracket = IslandBracket.None,
            StencilDepth = 0,
            ClipMode = IslandClipMode.None,
            RenderEveryN = 1,
            Visual = IslandVisual.Opaque,
            Node = new NodeHandle(3, 1, 0),
            DebugName = "isle",
        };
        tweak(box);

        var stream = new RenderStream("canon-island");
        stream.BeginRebuild();
        stream.AppendLeaf(IslandLeafDesc(), new[] { IslandQuad(0f, 0f, 8f, 8f) }, new[] { 0xFFFFFFFFu });
        stream.AddIsland(new IslandDesc
        {
            Kind = box.Kind,
            NativeKind = box.NativeKind,
            Bracket = box.Bracket,
            StencilDepth = box.StencilDepth,
            ClipMode = box.ClipMode,
            Node = box.Node,
            Slot = SlotTable.IdentitySlot,
            ClipIndex = ClipBook.NoneEntry,
            Visual = box.Visual,
            RenderEveryN = box.RenderEveryN,
            DebugName = box.DebugName,
        });
        stream.EndRebuild();
        return CanonicalStream.Canonicalize(stream.Snapshot());
    }

    /// <summary>孤岛骑一个真槽的手建流；<paramref name="padSlots"/> 先占掉几个槽制造分配序漂移。</summary>
    private static byte[] IslandSlotCanonical(int padSlots)
    {
        var stream = new RenderStream("canon-island-slot");
        var owner = new NodeHandle(5, 1, 0);
        for (int i = 0; i < padSlots; i++) stream.ClaimSlot(owner);
        int slot = stream.ClaimSlot(owner);
        stream.WriteSlot(slot, Affine2D.TRS(new Vector2(3f, 4f), 0.25f, Vector2.one));

        stream.BeginRebuild();
        stream.AppendLeaf(IslandLeafDesc(), new[] { IslandQuad(0f, 0f, 8f, 8f) }, new[] { 0xFFFFFFFFu });
        stream.AddIsland(new IslandDesc
        {
            Kind = IslandKind.ExternalNative,
            Slot = slot,
            ClipIndex = ClipBook.NoneEntry,
            Visual = IslandVisual.Opaque,
            RenderEveryN = 1,
            Node = owner,
            DebugName = "isle",
        });
        stream.EndRebuild();
        return CanonicalStream.Canonicalize(stream.Snapshot());
    }

    /// <summary><see cref="IslandCanonical"/> 的可变载体（Action 改不了 struct 的副本）。</summary>
    private sealed class IslandDescBox
    {
        internal IslandKind Kind;
        internal IslandNativeKind NativeKind;
        internal IslandBracket Bracket;
        internal int StencilDepth;
        internal IslandClipMode ClipMode;
        internal int RenderEveryN;
        internal IslandVisual Visual;
        internal NodeHandle Node;
        internal string? DebugName;
    }

    private static LeafDesc IslandLeafDesc() =>
        LeafDesc.For(new NodeHandle(1, 1, 0), new TexId(1));

    private static QuadInstance IslandQuad(float x, float y, float w, float h) => new QuadInstance
    {
        Rect = new Vector4(x, y, w, h),
        UvA = new Vector4(0f, 0f, 1f, 0f),
        UvB = new Vector4(0f, 1f, 1f, 1f),
    };
}
