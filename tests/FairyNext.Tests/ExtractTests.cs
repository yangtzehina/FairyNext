using FairyNext.Backend.Mock;
using FairyNext.Core;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-13 Extract + 整流重编用例（Program 的 partial 分片）。
///
/// 对齐 docs/architecture.md「平面三 · 渲染平面」：
/// 机制 1（相邻性排序：同段键前移但不越过 AABB 重叠者）、
/// 机制 9（孤岛占一个 run 序、切 run）、
/// 机制 10（Structure 消费 = **面板粒度**整流重编；作用域重编不进 MVP）、
/// 机制 5（含旋转的叶骑 transform 槽；轴对齐的烘进 rect）、
/// 机制 6（裁剪域继承共享；域外整只剔除）。
///
/// 「同一棵树两次 Extract 逐字节相等」是整编路径的确定性判据——它成立，
/// 才谈得上把编译期 Extract 与运行期 Extract 的产物做等价性金样（M1-20）。
/// </summary>
public static partial class Program
{
    private static void ExtractSuite()
    {
        ExtractWalksInPaintOrder();
        AdjacencySortMergesSegmentsWhenNothingOverlaps();
        AdjacencySortRefusesToCrossOverlap();
        HiddenLeavesAreSkipped();
        ClipDomainIsInheritedAndCullsOutsideLeaves();
        WorldTransformIsBakedIntoRect();
        RotatedLeafRidesASlot();
        NegativeScaleMirrorsUv();
        IslandCutsARunAndSortingStaysInside();
        RebuildIsBitIdentical();
        RebuildOnTheSameStreamIsStable();
        StructureDrainCoalescesToOneRebuild();
        StructureDrainIgnoresForeignPanels();
        RebuildReleasesItsOwnSlots();
        ExtractedStreamPassesTheMockGate();
        CrossFrameClipFallsBackToParentWindow();
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    /// <summary>一棵手建的树 + 内容池 + 流 + Extract。</summary>
    private sealed class ExFixture
    {
        internal readonly NodeTable Table = new NodeTable(tree: 3);
        internal readonly ContentTable Content = new ContentTable();
        internal readonly RenderStream Stream = new RenderStream("extract-test");
        internal readonly Extract Extract;

        internal ExFixture()
        {
            Extract = new Extract(Stream, Table, Content);
        }

        /// <summary>建一个叶节点并挂到 parent（默认根）下。</summary>
        internal NodeHandle Leaf(in LeafSpec spec, float x, float y, float w, float h, NodeHandle parent = default)
        {
            NodeHandle n = Table.CreateNode(NodeType.Image);
            Table.SetContentRef(n, Content.AddLeaf(in spec));
            Table.SetPosition(n, x, y);
            Table.SetSize(n, w, h);
            Table.AddChild(parent.IsNone ? Table.Root : parent, n);
            return n;
        }

        /// <summary>建一个容器节点（不产渲染单元；可开裁剪域）。</summary>
        internal NodeHandle Box(float x, float y, float w, float h, in ContentRecord rec, NodeHandle parent = default)
        {
            NodeHandle n = Table.CreateNode(NodeType.Component);
            Table.SetContentRef(n, Content.Add(in rec));
            Table.SetPosition(n, x, y);
            Table.SetSize(n, w, h);
            Table.AddChild(parent.IsNone ? Table.Root : parent, n);
            return n;
        }

        /// <summary>走 P6：paintOrder 定形 + world/worldVisual 一遍算（Extract 读的就是这两样）。</summary>
        internal void Settle()
        {
            Table.Phase = FramePhase.P6_Settle;
            Table.ApplyStructure();
            Table.DrainDerivedFull();
            Table.Phase = FramePhase.P3_Commands;
        }

        internal ExtractReport Rebuild()
        {
            Settle();
            return Extract.Rebuild();
        }
    }

    private static LeafSpec ExSolid(uint texture, uint color = 0xFFFFFFFFu) =>
        texture == 0
            ? LeafSpec.Solid(color)
            : LeafSpec.Image(new TexId(texture), SpriteRegion.Full(new Vector4(0f, 0f, 1f, 1f), 4f, 4f), color);

    /// <summary>同 <see cref="ExSolid"/> 但走 Add 混合——段键的另一半（**进段键，不是栅栏**）。</summary>
    private static LeafSpec ExAdd(uint texture) =>
        LeafSpec.Image(new TexId(texture), SpriteRegion.Full(new Vector4(0f, 0f, 1f, 1f), 4f, 4f),
            0xFFFFFFFFu, BlendClass.Add);

    private static ContentRecord ExClipBox(Vector2 soft = default) => new ContentRecord
    {
        Kind = ExtractKind.None,
        OpensClip = true,
        Clip = new ClipShape { Soft = soft, Radii = Vector4.zero },
        RenderEveryN = 1,
    };

    // ── 遍历序与相邻性排序 ─────────────────────────────────────────────────

    private static void ExtractWalksInPaintOrder()
    {
        var f = new ExFixture();
        NodeHandle a = f.Leaf(ExSolid(1), 0f, 0f, 10f, 10f);
        NodeHandle b = f.Leaf(ExSolid(1), 20f, 0f, 10f, 10f);
        NodeHandle c = f.Leaf(ExSolid(1), 40f, 0f, 10f, 10f);
        ExtractReport r = f.Rebuild();

        // 同一批纹理、互不重叠：排序不需要动任何东西，序就是 paintOrder 序。
        ReadOnlySpan<LeafRange> leaves = f.Stream.Leaves;
        bool order = leaves.Length == 3
            && leaves[0].Node.Equals(a) && leaves[1].Node.Equals(b) && leaves[2].Node.Equals(c);
        bool geometry = f.Stream.Quads[1].Rect.x == 20f && f.Stream.Quads[2].Rect.x == 40f;
        Check("Extract: 遍历序 = paintOrder 序（同键不重叠时排序零改动）",
            order && geometry && r.Reordered == 0 && r.Leaves == 3 && r.Segments == 1);
    }

    private static void AdjacencySortMergesSegmentsWhenNothingOverlaps()
    {
        var f = new ExFixture();
        NodeHandle a = f.Leaf(ExSolid(1), 0f, 0f, 10f, 10f);
        NodeHandle b = f.Leaf(ExAdd(2), 20f, 0f, 10f, 10f);
        NodeHandle a2 = f.Leaf(ExSolid(1), 40f, 0f, 10f, 10f);
        ExtractReport r = f.Rebuild();

        // A B A' 三者互不重叠 ⇒ A' 沉到 A 后面，段数从 3 收敛到 2。
        // 段键的另一半是 blendClass：Add 混合**进段键、不是栅栏**，所以它切段但不挡排序。
        ReadOnlySpan<LeafRange> leaves = f.Stream.Leaves;
        bool merged = leaves.Length == 3
            && leaves[0].Node.Equals(a) && leaves[1].Node.Equals(a2) && leaves[2].Node.Equals(b);
        Check("Extract: 相邻性排序把同段键前移（A B A' → A A' B，段 3 → 2）",
            merged && r.Segments == 2 && r.Reordered > 0);
    }

    private static void AdjacencySortRefusesToCrossOverlap()
    {
        var f = new ExFixture();
        NodeHandle a = f.Leaf(ExSolid(1), 0f, 0f, 30f, 30f);
        NodeHandle b = f.Leaf(ExAdd(2), 10f, 10f, 30f, 30f);   // 压住 A 也压住 A'
        NodeHandle a2 = f.Leaf(ExSolid(1), 20f, 20f, 30f, 30f);
        ExtractReport r = f.Rebuild();

        // A' 前移会穿过与它重叠的 B —— 遮挡关系会变，排序器因此停手，段数保持 3。
        ReadOnlySpan<LeafRange> leaves = f.Stream.Leaves;
        bool intact = leaves.Length == 3
            && leaves[0].Node.Equals(a) && leaves[1].Node.Equals(b) && leaves[2].Node.Equals(a2);
        Check("Extract: 相邻性排序不越过 AABB 重叠者（遮挡序不可改）",
            intact && r.Segments == 3 && r.Reordered == 0);
    }

    // ── 可见性与裁剪域 ─────────────────────────────────────────────────────

    private static void HiddenLeavesAreSkipped()
    {
        var f = new ExFixture();
        f.Leaf(ExSolid(1), 0f, 0f, 10f, 10f);
        NodeHandle hidden = f.Leaf(ExSolid(1), 20f, 0f, 10f, 10f);
        NodeHandle child = f.Leaf(ExSolid(1), 0f, 0f, 5f, 5f, hidden);
        f.Table.SetVisible(hidden, false);
        ExtractReport r = f.Rebuild();

        // worldVisual 的 visible 按 AND 级联 ⇒ 子节点也不可见，逐点判即等价于整棵子树跳过。
        Check("Extract: 不可见节点整支不进流（worldVisual 的 AND 级联，两个都跳）",
            r.Leaves == 1 && r.HiddenSkipped == 2 && f.Stream.QuadCount == 1
            && f.Table.IsAlive(child));
    }

    private static void ClipDomainIsInheritedAndCullsOutsideLeaves()
    {
        var f = new ExFixture();
        NodeHandle window = f.Box(10f, 10f, 40f, 40f, ExClipBox());
        NodeHandle inside = f.Leaf(ExSolid(1), 0f, 0f, 10f, 10f, window);
        NodeHandle partly = f.Leaf(ExSolid(1), 30f, 30f, 30f, 30f, window);
        f.Leaf(ExSolid(1), 100f, 100f, 10f, 10f, window);          // 整只在域外
        NodeHandle outsideDomain = f.Leaf(ExSolid(1), 200f, 0f, 10f, 10f);  // 不在窗口下
        ExtractReport r = f.Rebuild();

        ReadOnlySpan<LeafRange> leaves = f.Stream.Leaves;
        int domain = f.Stream.Clips.DomainCount;
        bool inherited = leaves.Length == 3
            && leaves[0].ClipEntry != ClipBook.NoneEntry
            && leaves[0].ClipEntry == leaves[1].ClipEntry;          // 子默认继承父域，不另分配
        bool sibling = leaves[2].Node.Equals(outsideDomain) && leaves[2].ClipEntry == ClipBook.NoneEntry;
        bool rect = f.Stream.Clips.Entry(leaves[0].ClipEntry).Rect == new Vector4(10f, 10f, 50f, 50f);
        // 继承共享要出实据：两个真正进流的子叶各记一次「用的是外层域」，一条预算都没多花。
        int shares = f.Stream.Diagnostics.ClipInherited;
        Check("Extract: clip 域按树继承共享（1 个域 + 2 次共享），域外叶整只剔除",
            inside.Index != 0 && partly.Index != 0 && domain == 1 && shares == 2
            && inherited && sibling && rect && r.ClipCulled == 1 && r.Leaves == 3);
    }

    // ── 落位：烘 rect vs 骑槽 ───────────────────────────────────────────────

    private static void WorldTransformIsBakedIntoRect()
    {
        var f = new ExFixture();
        NodeHandle box = f.Box(100f, 50f, 200f, 200f, default);
        f.Leaf(ExSolid(1), 5f, 7f, 10f, 20f, box);
        ExtractReport r = f.Rebuild();

        QuadInstance q = f.Stream.Quads[0];
        Check("Extract: 轴对齐的世界变换烘进 rect（骑 identity 槽，零槽消耗）",
            q.Rect.x == 105f && q.Rect.y == 57f && q.Rect.z == 10f && q.Rect.w == 20f
            && q.SlotIndex == 0u && r.SlotsClaimed == 0);
    }

    private static void RotatedLeafRidesASlot()
    {
        var f = new ExFixture();
        NodeHandle spin = f.Box(100f, 100f, 50f, 50f, default);
        f.Table.SetRotation(spin, 0.5f);
        NodeHandle l1 = f.Leaf(ExSolid(1), 0f, 0f, 10f, 10f, spin);
        NodeHandle l2 = f.Leaf(ExSolid(1), 0f, 0f, 10f, 10f, spin);
        ExtractReport r = f.Rebuild();

        QuadInstance q = f.Stream.Quads[0];
        Affine2D slot = f.Stream.Slots.Matrix((int)q.SlotIndex);
        Affine2D world = f.Table.World(l1);
        // rect 只有 min 角 + size，旋转在这个字段形态里不可表达 ⇒ 几何留本地、旋转进槽矩阵。
        bool rides = q.SlotIndex != 0u && q.Rect.x == 0f && q.Rect.y == 0f
            && NearMat(in slot, in world);
        bool shared = f.Stream.Quads[1].SlotIndex == q.SlotIndex;   // 同一矩阵的兄弟复用同一个槽
        Check("Extract: 含旋转的叶骑 transform 槽（rect 留本地、world 进槽、同矩阵复用）",
            l2.Index != 0 && rides && shared && r.SlotsClaimed == 1);
    }

    private static void NegativeScaleMirrorsUv()
    {
        var f = new ExFixture();
        NodeHandle flip = f.Box(0f, 0f, 20f, 20f, default);
        f.Table.SetScale(flip, -1f, 1f);
        f.Leaf(LeafSpec.Image(new TexId(1), SpriteRegion.Full(new Vector4(0f, 0f, 1f, 1f), 4f, 4f)),
            0f, 0f, 10f, 10f, flip);
        f.Rebuild();

        QuadInstance q = f.Stream.Quads[0];
        // rect 只记 min 角与 size，翻转必须由四角 UV 换位承担（实例流没有顶点序可用）。
        Check("Extract: 负缩放烘进 rect 时四角 UV 镜像（否则一张 scaleX=-1 的图会原样画出来）",
            q.Rect.x == -10f && q.Rect.z == 10f
            && q.UvA.x == 1f && q.UvA.z == 0f && q.UvB.x == 1f && q.UvB.z == 0f
            && q.UvA.y == 0f && q.UvB.y == 1f);
    }

    // ── 孤岛与 run ─────────────────────────────────────────────────────────

    private static void IslandCutsARunAndSortingStaysInside()
    {
        var f = new ExFixture();
        NodeHandle a = f.Leaf(ExSolid(1), 0f, 0f, 10f, 10f);
        NodeHandle b = f.Leaf(ExSolid(2), 20f, 0f, 10f, 10f);
        f.Box(40f, 0f, 10f, 10f, ContentRecord.OfIsland(IslandKind.ExternalNative, 1, "spine"));
        NodeHandle a2 = f.Leaf(ExSolid(1), 60f, 0f, 10f, 10f);
        ExtractReport r = f.Rebuild();

        ReadOnlySpan<LeafRange> leaves = f.Stream.Leaves;
        // A' 与 A 同键且互不重叠，但它们隔着一个孤岛栅栏——排序只在 run 内进行，故不得前移。
        bool split = leaves.Length == 3
            && leaves[0].Node.Equals(a) && leaves[1].Node.Equals(b) && leaves[2].Node.Equals(a2)
            && leaves[0].Run == leaves[1].Run && leaves[2].Run != leaves[0].Run;
        Check("Extract: 孤岛切 run，相邻性排序不跨栅栏",
            split && r.Islands == 1 && r.Runs == 2 && r.Reordered == 0);
    }

    // ── 整流重编的确定性 ───────────────────────────────────────────────────

    private static void BuildDeterminismTree(ExFixture f)
    {
        NodeHandle window = f.Box(5f, 5f, 60f, 60f, ExClipBox());
        f.Leaf(ExSolid(1, 0xFF203040u), 0f, 0f, 12f, 12f, window);
        f.Leaf(ExSolid(2), 20f, 0f, 12f, 12f, window);
        f.Leaf(ExSolid(1), 40f, 0f, 12f, 12f, window);
        NodeHandle spin = f.Box(80f, 0f, 20f, 20f, default);
        f.Table.SetRotation(spin, 0.25f);
        f.Leaf(ExSolid(3), 0f, 0f, 8f, 8f, spin);
        var cd = ExSolid(4);
        cd.Fill = RadialFillParams.Of(FillMethod.Radial360, (byte)Origin360.Top, 0.37f);
        f.Leaf(cd, 120f, 0f, 16f, 16f);
    }

    private static void RebuildIsBitIdentical()
    {
        var f1 = new ExFixture();
        BuildDeterminismTree(f1);
        f1.Rebuild();

        var f2 = new ExFixture();
        BuildDeterminismTree(f2);
        f2.Rebuild();

        byte[] a = CanonicalStream.Canonicalize(f1.Stream.Snapshot());
        byte[] b = CanonicalStream.Canonicalize(f2.Stream.Snapshot());
        int diff = CanonicalStream.FirstDifference(a, b);
        Check("Extract: 同一棵树两次 Extract 的规范化字节逐字节相等（整编确定性）",
            a.Length > 0 && diff < 0);
        if (diff >= 0) Console.WriteLine($"     首差异偏移 {diff}");
    }

    private static void RebuildOnTheSameStreamIsStable()
    {
        var f = new ExFixture();
        BuildDeterminismTree(f);
        f.Rebuild();
        QuadInstance[] first = f.Stream.Quads.ToArray();
        int segs = f.Stream.SegmentCount, runs = f.Stream.RunCount, clips = f.Stream.Clips.DomainCount;
        uint epoch = f.Stream.StructEpoch;

        f.Rebuild();
        QuadInstance[] second = f.Stream.Quads.ToArray();

        bool same = first.Length == second.Length;
        for (int i = 0; same && i < first.Length; i++) same = first[i].Equals(second[i]);
        Check("Extract: 同一条流连编两次内容不变（结构代 +1，其余逐实例相等）",
            same && first.Length > 0 && f.Stream.SegmentCount == segs && f.Stream.RunCount == runs
            && f.Stream.Clips.DomainCount == clips && f.Stream.StructEpoch == epoch + 1);
    }

    private static void RebuildReleasesItsOwnSlots()
    {
        var f = new ExFixture();
        NodeHandle spin = f.Box(0f, 0f, 20f, 20f, default);
        f.Table.SetRotation(spin, 0.3f);
        f.Leaf(ExSolid(1), 0f, 0f, 10f, 10f, spin);

        f.Rebuild();
        int live = f.Stream.Slots.LiveCount;      // 含槽 0：identity + 认领的那一个 = 2
        for (int i = 0; i < 40; i++) f.Rebuild();

        // 槽表**不**随 BeginRebuild 清空（Claim 归调用方），所以 Extract 自己认的必须自己还——
        // 不还的话每编一次漏一个槽，第 32 次重编（= 预算）就槽荒了。
        Check("Extract: 重编先还上一次认领的槽（连编 41 次活槽数不涨、零槽荒）",
            live == 2 && f.Stream.Slots.LiveCount == 2 && f.Stream.Slots.HighWater == 2
            && f.Stream.Slots.Starvation == 0);
    }

    // ── 与后端对拍 ─────────────────────────────────────────────────────────

    private static void ExtractedStreamPassesTheMockGate()
    {
        var f = new ExFixture();
        BuildDeterminismTree(f);
        f.Rebuild();

        var backend = new MockBackend();
        StreamHandle handle = f.Stream.Attach(backend);
        backend.BeginFrame(1);
        SubmitReport report = f.Stream.Submit(backend);
        FrameStats stats = f.Stream.BuildStats(1, in report);
        backend.BindMainSurface(new SurfaceDesc { Width = 200, Height = 200, ClearColor = 0 });
        backend.DrawStream(handle, PassHandle.None);
        backend.EndFrame(in stats);
        backend.SetBaseColors(handle, 0, f.Stream.BaseColors);

        byte[] mine = CanonicalStream.Canonicalize(f.Stream.Snapshot());
        byte[] theirs = CanonicalStream.Canonicalize(backend.Snapshot(handle));

        // 后端执法的一条正好落在本包身上：**保留位写零**——径向填充的 aux 保留段与 flags 保留段
        // 都在发射器手里，写脏一位就是把明天的新位域今天占掉。
        Check("Extract: 整编产物过 mock 门（镜像 == 后端字节、零违约、七字段全零）",
            CanonicalStream.FirstDifference(mine, theirs) < 0
            && backend.Violations.Count == 0 && backend.Gates.Pass && report.Quads > 0);
    }

    private static void CrossFrameClipFallsBackToParentWindow()
    {
        var f = new ExFixture();
        NodeHandle outer = f.Box(0f, 0f, 100f, 100f, ExClipBox());           // 轴对齐，落槽 0
        NodeHandle inner = f.Box(10f, 10f, 40f, 40f, ExClipBox(), outer);    // 旋转 ⇒ 骑自己的槽
        f.Table.SetRotation(inner, 0.4f);
        f.Leaf(ExSolid(1), 0f, 0f, 10f, 10f, inner);
        f.Rebuild();

        ReadOnlySpan<LeafRange> leaves = f.Stream.Leaves;
        int outerEntry = f.Stream.Clips.Resolve(leaves[0].ClipEntry);
        bool degraded = false;
        foreach (DegradeEvent e in f.Stream.Degrades.Pending)
        {
            if (e.Kind == DegradeKind.ClipCrossFrameToParent) degraded = true;
        }
        // 折叠要求两域同帧；不同帧时沿用父窗口——裁得更粗但不画错，且**有声**（不变量 5）。
        Check("Extract: 跨帧的子裁剪域沿用父窗口并走阶梯（不假装能折叠）",
            outer.Index != 0 && degraded && leaves.Length == 1
            && f.Stream.Clips.DomainCount == 1 && outerEntry != ClipBook.NoneEntry
            && f.Stream.Clips.Entry(outerEntry).Slot == 0u);
    }

    // ── Structure 通道：面板粒度整流重编 ───────────────────────────────────

    private static void StructureDrainCoalescesToOneRebuild()
    {
        var f = new ExFixture();
        NodeHandle a = f.Leaf(ExSolid(1), 0f, 0f, 10f, 10f);
        NodeHandle b = f.Leaf(ExSolid(1), 20f, 0f, 10f, 10f);
        NodeHandle c = f.Leaf(ExSolid(1), 40f, 0f, 10f, 10f);
        f.Settle();

        int before = f.Extract.Rebuilds;
        var ctx = default(FrameContext);
        var batch = new[] { a, b, c };
        f.Extract.Drain(ref ctx, Ch.Structure, batch);

        Check("整流重编: 一批结构脏折叠成一次重编（面板才是重编的粒度）",
            f.Extract.Rebuilds == before + 1 && f.Extract.ForeignMarks == 0
            && f.Stream.LeafCount == 3);
    }

    private static void StructureDrainIgnoresForeignPanels()
    {
        var f = new ExFixture();
        NodeHandle panel = f.Box(0f, 0f, 50f, 50f, default);
        NodeHandle mine = f.Leaf(ExSolid(1), 0f, 0f, 10f, 10f, panel);
        NodeHandle theirs = f.Leaf(ExSolid(1), 60f, 0f, 10f, 10f);
        f.Extract.PanelRoot = panel;
        f.Settle();

        int before = f.Extract.Rebuilds;
        var ctx = default(FrameContext);
        var foreign = new[] { theirs };
        f.Extract.Drain(ref ctx, Ch.Structure, foreign);
        bool untouched = f.Extract.Rebuilds == before && f.Extract.ForeignMarks == 1;

        var local = new[] { mine };
        f.Extract.Drain(ref ctx, Ch.Structure, local);
        Check("整流重编: 别的面板脏了不让本面板整编（流按面板拆分的消费端半边）",
            untouched && f.Extract.Rebuilds == before + 1 && f.Extract.ForeignMarks == 1);
    }
}
