using System.Reflection;
using System.Runtime.InteropServices;
using FairyNext.Backend.Mock;
using FairyNext.Contracts;
using FairyNext.Core;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-11 RenderStream 核心用例（Program 的 partial 分片）。
/// 逐条对齐 docs/architecture.md「平面三 · 渲染平面」：
/// 不变量 1 ABI 定长（本片验的是**流镜像里的字节**，不是结构体声明）/ 2 栅栏无 AABB（类型层）/
/// 4 slack 越界门 / 5 预算溢出必有声 / 6 axis-aligned 位一致性 / 7 inner rect 剪枝安全 /
/// 8 ClipEntry 引用健全 / 9 颜色纯函数 / 10 序派生只随 structEpoch / 13 零脏帧合法性 / 16 槽写等值切断；
/// 机制 6 继承共享（预算按裁剪域数计）、机制 7 从基色重算、段键 = ≤4 纹理 × blendClass。
/// </summary>
public static partial class Program
{
    private static void RenderStreamSuite()
    {
        // ABI 一致性（字段偏移与 stride 全部走生成物常量）
        QuadFieldsRoundTripThroughGeneratedOffsets();
        MirrorStrideIsTheAbiStride();
        RouteIsPackedByGeneratedShifts();

        // 栅栏：无限盒
        RunRecordHasNoAabbField();
        IslandClosesRunAndOrderIsDerived();
        IslandHandlesAreReleasedOnRebuild();

        // SlotTable
        SlotZeroIsAlwaysIdentity();
        SlotWriteCutsOnBitEquality();
        SlotClaimReusesReleasedSlots();
        SlotBudgetOverflowTakesTheLadder();
        SlotAxisAlignedIsRejudgedEveryWrite();

        // ClipEntry：继承共享 + 折叠 + inner 剪枝 + 预算
        ClipChildWithoutOwnParamsInheritsParentId();
        ClipDivergentParamsAllocatesAndFolds();
        ClipBudgetCountsDomainsNotNodes();
        ClipBudgetOverflowDegradesToParentWindow();
        ClipInnerRectPrunesContainedQuads();
        ClipPruneRefusesCrossSlotAndSoftEdge();
        ClipInheritedChainResolvesToOwned();

        // 颜色 tier：从基色重算
        ColorTierRecomputesFromBaseColor();
        ColorTierSurvivesAlphaZeroRoundTrip();
        GrayedRidesFlagsNotColor();
        ColorFieldIsAPureFunctionOfBaseAndVisual();

        // 段键
        SameKeyLeavesShareOneSegment();
        FifthTextureCutsANewSegment();
        BlendClassCutsSegmentNotRun();

        // 内容 tier 与提交路径
        ContentRewriteStaysInsideSlack();
        SlackOverflowUpgradesToStructure();
        Tier2RefusesRotationAndTakesTheLadder();
        MirrorAndBackendAgreeByteForByte();
        ZeroDirtyFrameSubmitsNothing();
        RunOrdersOnlyOnStructEpochChange();
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private static NodeHandle RsNode(uint index) => new NodeHandle(index, 1, 1);

    /// <summary>发射器口径的 quad：只有几何与 UV，**route 与 color 留白**（由流盖写）。</summary>
    private static QuadInstance RsQuad(float x, float y, float w, float h) => new QuadInstance
    {
        Rect = new Vector4(x, y, w, h),
        UvA = new Vector4(0f, 0f, 1f, 0f),
        UvB = new Vector4(0f, 1f, 1f, 1f),
    };

    private static LeafDesc RsLeaf(uint node, uint tex, BlendClass blend = BlendClass.Normal) =>
        LeafDesc.For(RsNode(node), new TexId(tex), blend);

    /// <summary>建一条「一个叶 n 个 quad」的流（多数用例只需要这个形状）。</summary>
    private static RenderStream RsSingleLeaf(uint baseColor, int quads = 1, int slackHint = 0)
    {
        var stream = new RenderStream("fixture");
        var qs = new QuadInstance[quads];
        var bc = new uint[quads];
        for (int i = 0; i < quads; i++)
        {
            qs[i] = RsQuad(i * 10f, 0f, 8f, 8f);
            bc[i] = baseColor;
        }
        LeafDesc desc = RsLeaf(1, 1);
        desc.SlackHint = slackHint;
        stream.BeginRebuild();
        stream.AppendLeaf(desc, qs, bc);
        stream.EndRebuild();
        return stream;
    }

    private static byte AlphaOf(uint rgba) => (byte)(rgba >> 24);
    private static uint RgbOf(uint rgba) => rgba & 0x00FFFFFFu;

    // ── ABI 一致性 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 流镜像里的实例必须按**生成物的偏移表**回读得到写进去的值。
    /// 与 M1-10 的「结构体声明 vs 生成物」门不同：这条验的是数据真的落在那些字节上，
    /// 手抄第二份偏移在 C#↔HLSL 边界上表现为花屏而非编译错，所以两侧都得有门。
    /// </summary>
    private static void QuadFieldsRoundTripThroughGeneratedOffsets()
    {
        var stream = new RenderStream("abi");
        QuadInstance q = RsQuad(1.5f, 2.5f, 30f, 40f);
        q.Aux = 0xABCD1234u;
        q.Extra = new Vector4(9f, 8f, 7f, 6f);

        LeafDesc desc = RsLeaf(1, 5);
        desc.Slot = 0;
        stream.BeginRebuild();
        stream.AppendLeaf(desc, new[] { q }, new[] { Rgba(10, 20, 30, 255) });
        stream.EndRebuild();

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(stream.Quads);
        float rx = BitConverter.ToSingle(bytes.Slice(AbiLayout.QuadRectOffset, 4));
        float rw = BitConverter.ToSingle(bytes.Slice(AbiLayout.QuadRectOffset + 8, 4));
        float uva = BitConverter.ToSingle(bytes.Slice(AbiLayout.QuadUvAOffset + 8, 4));
        uint color = BitConverter.ToUInt32(bytes.Slice(AbiLayout.QuadColorOffset, 4));
        uint aux = BitConverter.ToUInt32(bytes.Slice(AbiLayout.QuadAuxOffset, 4));
        float ex = BitConverter.ToSingle(bytes.Slice(AbiLayout.QuadExtraOffset, 4));

        Check("ABI: 流镜像的 quad 按生成常量的偏移逐字段回读一致",
            bytes.Length == Abi.QuadInstanceSize && rx == 1.5f && rw == 30f && uva == 1f
            && color == Rgba(10, 20, 30, 255) && aux == 0xABCD1234u && ex == 9f);
    }

    /// <summary>相邻实例的间距 = <see cref="Abi.QuadInstanceSize"/>——stride 是 ABI 的，不是 sizeof 猜的。</summary>
    private static void MirrorStrideIsTheAbiStride()
    {
        var stream = new RenderStream("stride");
        var qs = new[] { RsQuad(0, 0, 1, 1), RsQuad(1, 0, 1, 1), RsQuad(2, 0, 1, 1) };
        var bc = new[] { Rgba(1, 0, 0, 255), Rgba(0, 2, 0, 255), Rgba(0, 0, 3, 255) };
        stream.BeginRebuild();
        stream.AppendLeaf(RsLeaf(1, 1), qs, bc);
        stream.EndRebuild();

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(stream.Quads);
        bool ok = bytes.Length == 3 * Abi.QuadInstanceSize;
        for (int i = 0; i < 3 && ok; i++)
        {
            uint color = BitConverter.ToUInt32(
                bytes.Slice(i * Abi.QuadInstanceSize + AbiLayout.QuadColorOffset, 4));
            ok = color == bc[i];
        }
        Check("ABI: 镜像 stride == Abi.QuadInstanceSize（80B），逐实例按 stride 命中", ok);
    }

    /// <summary>route 三分量由流盖写，位域取自生成物；保留位恒零（append-only 纪律的运行期半边）。</summary>
    private static void RouteIsPackedByGeneratedShifts()
    {
        var stream = new RenderStream("route");
        int domainA = stream.Clips.Push(ClipBook.NoneEntry,
            ClipParams.Rectangle(0, 0, 100, 100), RsNode(9), out _);
        int domainB = stream.Clips.Push(ClipBook.NoneEntry,
            ClipParams.Rectangle(0, 0, 50, 50), RsNode(10), out _);
        int slot = stream.ClaimSlot(RsNode(11));

        stream.BeginRebuild();
        // 重编清了 clip 表，重新推同样的两个域（id 稳定：1、2）
        domainA = stream.Clips.Push(ClipBook.NoneEntry, ClipParams.Rectangle(0, 0, 100, 100), RsNode(9), out _);
        domainB = stream.Clips.Push(ClipBook.NoneEntry, ClipParams.Rectangle(0, 0, 50, 50), RsNode(10), out _);
        stream.AppendLeaf(RsLeaf(1, 1), new[] { RsQuad(0, 0, 4, 4) }, new[] { Rgba(9, 9, 9, 255) });

        LeafDesc second = RsLeaf(2, 2);
        second.Slot = slot;
        second.ClipEntry = domainB;
        stream.AppendLeaf(second, new[] { RsQuad(0, 0, 4, 4) }, new[] { Rgba(9, 9, 9, 255) });
        stream.EndRebuild();

        QuadInstance q = stream.Quads[1];
        uint raw = q.Route;
        Check("ABI: route = slot | clipIndex | texSlot 按生成位移打包，保留位为零",
            domainA == 1 && domainB == 2 && slot == 1
            && ((raw >> AbiLayout.RouteSlotShift) & AbiLayout.RouteSlotMask) == (uint)slot
            && ((raw >> AbiLayout.RouteClipIndexShift) & AbiLayout.RouteClipIndexMask) == (uint)domainB
            && ((raw >> AbiLayout.RouteTexSlotShift) & AbiLayout.RouteTexSlotMask) == 1u
            && AbiLayout.RouteReserved(raw) == 0u);
    }

    // ── 栅栏：无限盒 ────────────────────────────────────────────────────────

    /// <summary>
    /// 不变量 2 的类型层执法：<see cref="RunRecord"/> 上不存在 AABB/包围盒字段。
    /// 「紧栅栏」不可表达要靠类型缺字段，不是靠注释——旧 fork 三次让紧盒子变陈旧，
    /// 每次都是「盒子对、内容早动了」的静默画错。
    /// </summary>
    private static void RunRecordHasNoAabbField()
    {
        FieldInfo[] fields = typeof(RunRecord).GetFields(BindingFlags.Public | BindingFlags.Instance);
        bool clean = fields.Length > 0;
        foreach (FieldInfo f in fields)
        {
            string n = f.Name.ToLowerInvariant();
            if (n.Contains("aabb") || n.Contains("bound") || n.Contains("rect")
                || n.Contains("extent") || n.Contains("box")) clean = false;
            if (f.FieldType == typeof(Rect) || f.FieldType == typeof(Vector4)
                || f.FieldType == typeof(Vector2)) clean = false;
        }
        Check("渲染不变量 2: RunRecord 类型上没有 AABB 字段（栅栏一律无限盒）", clean);
    }

    /// <summary>孤岛关闭一个 run 并占一个 run 序；序号是 paintOrder 下标 ×16 派生的，不独立维护。</summary>
    private static void IslandClosesRunAndOrderIsDerived()
    {
        var stream = new RenderStream("island");
        stream.BeginRebuild();
        stream.AppendLeaf(RsLeaf(1, 1), new[] { RsQuad(0, 0, 4, 4) }, new[] { Rgba(1, 1, 1, 255) });
        int island = stream.AddIsland(new IslandDesc
        {
            Kind = IslandKind.ExternalNative,
            RenderEveryN = 1,
            Visual = IslandVisual.Opaque,
            DebugName = "spine",
        });
        stream.AppendLeaf(RsLeaf(2, 1), new[] { RsQuad(9, 0, 4, 4) }, new[] { Rgba(1, 1, 1, 255) });
        stream.EndRebuild();

        RunOrder[] orders = stream.BuildRunOrders();
        Check("机制 2/9: 孤岛关闭 run 且占一个 run 序；sortingOrder = paintOrder 下标 × 16 派生",
            island == 0 && stream.RunCount == 2 && stream.Runs[0].ClosedByIsland == 0
            && stream.Runs[1].ClosedByIsland == -1
            && orders.Length == 2 && orders[0].SortingOrder == 0
            && orders[1].SortingOrder == Abi.PaintOrderStride
            && stream.SegmentCount == 2);   // run 边界一律切段
    }

    /// <summary>
    /// 孤岛句柄随整流重编在后端拆除：悄悄丢句柄 = GPU 侧泄漏一个没人再引用也没人再销毁的东西。
    /// 顺带验「外部内容自报仍在动」参与脏判据（不变量 13 的第三个前提）。
    /// </summary>
    private static void IslandHandlesAreReleasedOnRebuild()
    {
        var backend = new MockBackend();
        var stream = new RenderStream("islands");
        stream.BeginRebuild();
        stream.AppendLeaf(RsLeaf(1, 1), new[] { RsQuad(0, 0, 8, 8) }, new[] { Rgba(1, 1, 1, 255) });
        stream.AddIsland(new IslandDesc
        {
            Kind = IslandKind.StencilMask,
            RenderEveryN = 1,
            Visual = IslandVisual.Opaque,
            DebugName = "mask",
        });
        stream.EndRebuild();

        StreamHandle handle = stream.Attach(backend);
        stream.AttachIslands(backend);
        IslandHandle island = stream.Islands[0].Handle;

        backend.BeginFrame(1);
        SubmitReport report = stream.Submit(backend);
        stream.SyncIsland(backend, 0, IslandVisual.Opaque, stillAnimating: true);
        FrameStats stats = stream.BuildStats(1, in report);
        bool animatingCountsDirty = stats.Dirty && stream.AnyIslandAnimating();
        backend.EndFrame(in stats);

        stream.BeginRebuild(backend);
        stream.AppendLeaf(RsLeaf(1, 1), new[] { RsQuad(0, 0, 8, 8) }, new[] { Rgba(1, 1, 1, 255) });
        stream.EndRebuild();

        Check("机制 9: 孤岛句柄随重编在后端拆除（不泄漏、不 use-after-free），自报仍在动进脏判据",
            !handle.IsNone && !island.IsNone && animatingCountsDirty
            && stream.IslandCount == 0 && backend.CallCount(MockCallKind.DestroyIsland) == 1
            && backend.AllIslandsStill() && backend.Violations.Count == 0);
    }

    // ── SlotTable ───────────────────────────────────────────────────────────

    /// <summary>槽 0 是 identity 哨兵：不可写、不可归还，任何路径下都恒为单位阵。</summary>
    private static void SlotZeroIsAlwaysIdentity()
    {
        var table = new SlotTable();
        bool wrote = true, released = true;
        Quiet(() =>
        {
            wrote = table.Write(SlotTable.IdentitySlot, Affine2D.TRS(new Vector2(5, 5), 0f, Vector2.one));
            released = table.Release(SlotTable.IdentitySlot);
        });
        Affine2D m = table.Matrix(SlotTable.IdentitySlot);
        Check("机制 5: 槽 0 恒为 identity（写/归还都被拒）",
            !wrote && !released && m.m00 == 1f && m.m11 == 1f && m.tx == 0f && m.ty == 0f
            && table.IsLive(0) && table.LiveCount == 1);
    }

    /// <summary>不变量 16: 写前位等比较，同值不置脏——零脏帧短路以此为前提。</summary>
    private static void SlotWriteCutsOnBitEquality()
    {
        var table = new SlotTable();
        int slot = table.Claim(RsNode(3));
        table.ClearDirty();

        var m = Affine2D.TRS(new Vector2(12f, -4f), 0f, Vector2.one);
        bool first = table.Write(slot, m);
        bool dirtyAfterFirst = table.HasDirty;
        table.ClearDirty();
        bool second = table.Write(slot, m);

        Check("不变量 16: 槽写位等切断（同值不写、不置脏，切断次数入收据）",
            slot == 1 && first && dirtyAfterFirst && !second && !table.HasDirty
            && table.WritesCut == 1 && table.WritesApplied == 1);
    }

    /// <summary>归还的槽按低下标优先复用，且复位为 identity——复用者拿到的必须是确定初值。</summary>
    private static void SlotClaimReusesReleasedSlots()
    {
        var table = new SlotTable();
        int a = table.Claim(RsNode(1));
        int b = table.Claim(RsNode(2));
        int c = table.Claim(RsNode(3));
        table.Write(b, Affine2D.TRS(new Vector2(7f, 7f), 0f, Vector2.one));
        table.Release(b);
        int reused = table.Claim(RsNode(4));

        Check("机制 5: 槽复用按低下标优先，归还即复位 identity",
            a == 1 && b == 2 && c == 3 && reused == 2
            && table.Matrix(reused).tx == 0f && table.OwnerOf(reused).Equals(RsNode(4))
            && table.LiveCount == 4);
    }

    /// <summary>不变量 5: 槽荒退回 identity 槽 + 明文阶梯事件 + 高水位；静默返回 0 是被禁的那一种。</summary>
    private static void SlotBudgetOverflowTakesTheLadder()
    {
        var log = new DegradeLog();
        var table = new SlotTable(log);
        int last = 0;
        for (int i = 1; i < Abi.TransformSlotBudget; i++) last = table.Claim(RsNode((uint)i));
        int overflow = table.Claim(RsNode(999));

        Check("不变量 5: 槽荒 → 退 identity 槽 + SlotStarvation 阶梯 + 高水位锁存",
            last == Abi.TransformSlotBudget - 1 && overflow == SlotTable.IdentitySlot
            && table.Starvation == 1 && table.HighWater == Abi.TransformSlotBudget
            && log.CountOf(DegradeKind.SlotStarvation) == 1 && log.Pending.Count == 1);
    }

    /// <summary>不变量 6: axis-aligned 位在**每次写**时重判（不是建槽时判一次）。</summary>
    private static void SlotAxisAlignedIsRejudgedEveryWrite()
    {
        var table = new SlotTable();
        int slot = table.Claim(RsNode(1));
        bool alignedAtBirth = (table.Entry(slot).Flags & TransformSlotFlags.AxisAligned) != 0;

        table.Write(slot, Affine2D.TRS(Vector2.zero, 0.7f, Vector2.one));
        bool clearedByRotation = (table.Entry(slot).Flags & TransformSlotFlags.AxisAligned) == 0;

        table.Write(slot, Affine2D.TRS(new Vector2(3f, 3f), 0f, new Vector2(2f, 2f)));
        bool restored = (table.Entry(slot).Flags & TransformSlotFlags.AxisAligned) != 0;

        Check("不变量 6: axis-aligned 位每次写重判（旋转清位，回到轴对齐复位）",
            alignedAtBirth && clearedByRotation && restored && table.Entry(slot).MatrixIsAxisAligned);
    }

    // ── ClipEntry ───────────────────────────────────────────────────────────

    /// <summary>机制 6: 子节点没有自己的裁剪参数 ⇒ 直接引用父条目 id，**零分配**。</summary>
    private static void ClipChildWithoutOwnParamsInheritsParentId()
    {
        var book = new ClipBook();
        int parent = book.Push(ClipBook.NoneEntry, ClipParams.Rectangle(0, 0, 200, 100), RsNode(1), out _);
        int childA = book.Inherit(parent);
        int childB = book.Inherit(parent);

        Check("机制 6: Inherited 子默认引用父条目 id（零分配，域数不涨）",
            parent == 1 && childA == parent && childB == parent
            && book.DomainCount == 1 && book.InheritedShares == 2);
    }

    /// <summary>异值才自有一条；自有条目的 rect = 自身 ∩ 外层（折叠让一个实例只引用一个 clipIndex）。</summary>
    private static void ClipDivergentParamsAllocatesAndFolds()
    {
        var book = new ClipBook();
        int parent = book.Push(ClipBook.NoneEntry, ClipParams.Rectangle(0, 0, 100, 100), RsNode(1), out _);
        int child = book.Push(parent, ClipParams.Rectangle(50, 50, 200, 200), RsNode(2), out _);
        Vector4 folded = book.Entry(child).Rect;

        Check("机制 6: 裁剪参数异值才 Owned 自有，且 rect 与外层折叠（∩）",
            child == 2 && book.DomainCount == 2 && book.Share(child).Kind == ClipShareKind.Owned
            && folded.x == 50f && folded.y == 50f && folded.z == 100f && folded.w == 100f);
    }

    /// <summary>
    /// v1.3 条款：预算按**裁剪域数**计，不按节点数计。
    /// 200 个节点挂在同一个域上仍是 1 条；同参数的域互相去重也不涨。
    /// </summary>
    private static void ClipBudgetCountsDomainsNotNodes()
    {
        var book = new ClipBook();
        var p = ClipParams.Rectangle(0, 0, 300, 300);
        int domain = book.Push(ClipBook.NoneEntry, p, RsNode(1), out _);
        for (int i = 0; i < 200; i++) book.Inherit(domain);
        for (int i = 0; i < 50; i++) book.Push(ClipBook.NoneEntry, p, RsNode((uint)(100 + i)), out _);

        Check("机制 6 / 预算 16: 条目按裁剪域数计（250 节点仍是 1 域），同参数去重",
            book.DomainCount == 1 && book.InheritedShares == 200 && book.DedupeHits == 50
            && book.Count <= book.Budget);
    }

    /// <summary>不变量 5: 域超预算 → 退回**父窗口**（正确但更粗）+ ClipStarvation 阶梯 + 高水位。</summary>
    private static void ClipBudgetOverflowDegradesToParentWindow()
    {
        var log = new DegradeLog();
        var book = new ClipBook(log);
        int parent = book.Push(ClipBook.NoneEntry, ClipParams.Rectangle(0, 0, 1000, 1000), RsNode(1), out _);
        for (int i = 1; i < book.MaxDomains; i++)
            book.Push(parent, ClipParams.Rectangle(i, i, 900, 900), RsNode((uint)(10 + i)), out _);

        int overflow = book.Push(parent, ClipParams.Rectangle(1, 2, 3, 4), RsNode(777), out bool degraded);

        Check("不变量 5: 裁剪域超预算 → 父窗口降级 + ClipStarvation 阶梯 + 条目表不越 ABI 预算",
            degraded && overflow == parent && book.DomainCount == book.MaxDomains
            && book.Count == Abi.ClipEntryBudget && book.Starvation == 1
            && log.CountOf(DegradeKind.ClipStarvation) == 1);
    }

    /// <summary>不变量 7: AABB 完全落在 inner rect 里的实例摘掉 clipIndex，边界上的不摘。</summary>
    private static void ClipInnerRectPrunesContainedQuads()
    {
        var stream = new RenderStream("prune");
        stream.BeginRebuild();
        int domain = stream.Clips.Push(ClipBook.NoneEntry,
            ClipParams.Rectangle(0, 0, 100, 100), RsNode(1), out _);
        LeafDesc desc = RsLeaf(1, 1);
        desc.ClipEntry = domain;
        stream.AppendLeaf(desc,
            new[] { RsQuad(10, 10, 20, 20), RsQuad(90, 90, 20, 20) },
            new[] { Rgba(1, 1, 1, 255), Rgba(1, 1, 1, 255) });
        stream.EndRebuild();

        int pruned = stream.PruneContainedClips();
        Check("不变量 7: inner rect 包含剪枝——域内实例摘 clipIndex，跨边界的不摘",
            domain == 1 && pruned == 1 && stream.Quads[0].ClipIndex == 0u
            && stream.Quads[1].ClipIndex == (uint)domain && stream.Clips.Pruned == 1);
    }

    /// <summary>
    /// 剪枝的两条安全边：① 跨帧（quad 与条目不同槽）一律不剪——那是拿两个坐标系的矩形比大小；
    /// ② 软边/圆角把 inner 收窄，压在渐变带上的实例不剪（剪了就丢了那圈淡出）。
    /// </summary>
    private static void ClipPruneRefusesCrossSlotAndSoftEdge()
    {
        var book = new ClipBook();
        var onSlot1 = new ClipParams
        {
            Rect = new Vector4(0, 0, 100, 100),
            Soft = Vector2.zero,
            Radii = Vector4.zero,
            Slot = 1,
        };
        int slotted = book.Push(ClipBook.NoneEntry, onSlot1, RsNode(1), out _);
        bool crossSlot = book.CanPrune(slotted, new Vector4(10, 10, 20, 20), 0);
        bool sameSlot = book.CanPrune(slotted, new Vector4(10, 10, 20, 20), 1);

        var soft = new ClipParams
        {
            Rect = new Vector4(0, 0, 100, 100),
            Soft = new Vector2(10f, 10f),
            Radii = new Vector4(0, 0, 0, 0),
            Slot = 0,
        };
        int softDomain = book.Push(ClipBook.NoneEntry, soft, RsNode(2), out _);
        bool onFade = book.CanPrune(softDomain, new Vector4(5, 5, 20, 20), 0);
        bool wellInside = book.CanPrune(softDomain, new Vector4(30, 30, 20, 20), 0);
        Vector4 inner = book.Inner(softDomain);

        Check("不变量 7: 剪枝拒绝跨帧比较，软边/圆角把 inner 收窄后边上的实例不剪",
            !crossSlot && sameSlot && !onFade && wellInside
            && inner.x == 10f && inner.z == 90f);
    }

    /// <summary>不变量 8: Inherited 链无环且终于 Owned（别名是挂载期拼接的承接件）。</summary>
    private static void ClipInheritedChainResolvesToOwned()
    {
        var book = new ClipBook();
        int owned = book.Push(ClipBook.NoneEntry, ClipParams.Rectangle(0, 0, 64, 64), RsNode(1), out _);
        int alias1 = book.Alias(owned, RsNode(2));
        int alias2 = book.Alias(alias1, RsNode(3));

        Check("不变量 8: Inherited 链解析到 Owned 条目，别名内容与目标一致",
            book.Share(alias1).Kind == ClipShareKind.Inherited
            && book.Share(alias2).Kind == ClipShareKind.Inherited
            && book.Resolve(alias2) == owned && book.Resolve(owned) == owned
            && book.Entry(alias2).Rect == book.Entry(owned).Rect);
    }

    // ── 颜色 tier ───────────────────────────────────────────────────────────

    /// <summary>机制 7: color 场 = 基色 × worldVisual，rgb 直通、α 重乘。</summary>
    private static void ColorTierRecomputesFromBaseColor()
    {
        uint bas = Rgba(200, 100, 50, 255);
        uint half = ColorTier.Apply(bas, 0.5f, true);
        uint hidden = ColorTier.Apply(bas, 1f, false);
        uint nan = ColorTier.Apply(bas, float.NaN, true);

        Check("机制 7: color = f(baseColor, worldVisual)——rgb 直通、α 重乘、NaN 落透明",
            RgbOf(half) == RgbOf(bas) && AlphaOf(half) == 128
            && AlphaOf(hidden) == 0 && RgbOf(hidden) == RgbOf(bas)
            && AlphaOf(nan) == 0);
    }

    /// <summary>
    /// fork 的病例：<c>bakedAlpha ≤ 0</c> 时没有可恢复的基准，比例重标退化成整叶重建。
    /// 从基色重算没有这个分支——α 归零再恢复必须**逐字节**回到原色。
    /// </summary>
    private static void ColorTierSurvivesAlphaZeroRoundTrip()
    {
        uint bas = Rgba(200, 100, 50, 255);
        RenderStream stream = RsSingleLeaf(bas, quads: 2);
        uint opaque = stream.Quads[0].Color;

        stream.RecolorLeaf(0, new IslandVisual { Alpha = 0f, Visible = true, Grayed = false });
        uint faded = stream.Quads[0].Color;

        stream.SetLeafVisible(0, false);
        uint invisible = stream.Quads[0].Color;

        stream.SetLeafVisible(0, true);
        stream.RecolorLeaf(0, IslandVisual.Opaque);
        uint restored = stream.Quads[0].Color;

        Check("机制 7: α 归零（以及隐藏）再恢复不丢色——基色重算没有退化分支",
            opaque == bas && AlphaOf(faded) == 0 && RgbOf(faded) == RgbOf(bas)
            && AlphaOf(invisible) == 0 && restored == bas
            && stream.Quads[1].Color == bas);
    }

    /// <summary>grayed 是 flags 的一位（shader 求亮度），不是被烘进 color 的另一种颜色。</summary>
    private static void GrayedRidesFlagsNotColor()
    {
        RenderStream stream = RsSingleLeaf(Rgba(200, 100, 50, 255));
        uint before = stream.Quads[0].Color;

        stream.RecolorLeaf(0, new IslandVisual { Alpha = 1f, Visible = true, Grayed = true });
        uint grayedColor = stream.Quads[0].Color;
        bool flagSet = ColorTier.IsGrayed(stream.Quads[0].Flags);

        stream.RecolorLeaf(0, IslandVisual.Opaque);
        bool flagCleared = !ColorTier.IsGrayed(stream.Quads[0].Flags);

        Check("机制 3/7: grayed 落 flags 位、不动 color 场；恢复即清位",
            grayedColor == before && flagSet && flagCleared
            && AbiLayout.FlagsGrayedShift == 4);
    }

    /// <summary>
    /// 不变量 9: 增量 Color 排水的结果必须逐实例等于「从基色全量重算」的结果。
    /// 反复起落 α 之后仍然相等，才说明没有精度累积（fork 的比例重标会漂）。
    /// </summary>
    private static void ColorFieldIsAPureFunctionOfBaseAndVisual()
    {
        uint bas = Rgba(37, 211, 89, 255);
        RenderStream stream = RsSingleLeaf(bas, quads: 3);
        float[] ladder = { 1f, 0.3f, 0f, 0.77f, 0.5f, 1f };
        foreach (float a in ladder)
            stream.RecolorLeaf(0, new IslandVisual { Alpha = a, Visible = true, Grayed = false });

        var final = new IslandVisual { Alpha = 0.42f, Visible = true, Grayed = false };
        stream.RecolorLeaf(0, final);

        bool pure = true;
        for (int i = 0; i < stream.QuadCount; i++)
            if (stream.Quads[i].Color != ColorTier.Apply(stream.BaseColors[i], in final)) pure = false;

        stream.RecolorLeaf(0, IslandVisual.Opaque);
        bool noDrift = stream.Quads[0].Color == bas;

        Check("不变量 9: GPU color 场 ≡ f(baseColor, worldVisual)，反复起落 α 无漂移",
            pure && noDrift);
    }

    // ── 段键 ────────────────────────────────────────────────────────────────

    /// <summary>同纹理同 blendClass 的相邻叶并进一个段（段切换 = 一次 draw 的代价）。</summary>
    private static void SameKeyLeavesShareOneSegment()
    {
        var stream = new RenderStream("seg");
        stream.BeginRebuild();
        for (uint i = 0; i < 3; i++)
            stream.AppendLeaf(RsLeaf(i + 1, 7), new[] { RsQuad(i * 10f, 0, 8, 8) }, new[] { Rgba(1, 1, 1, 255) });
        stream.EndRebuild();

        Check("段键: 同纹理同 blendClass 的叶合并为一段（texSlot 共享）",
            stream.SegmentCount == 1 && stream.Segments[0].TexCount == 1
            && stream.Segments[0].Count == 3 && stream.Leaves[2].TexSlot == 0
            && stream.Quads[2].TexSlot == 0u);
    }

    /// <summary>段键的纹理集上限 = <see cref="Abi.SegmentMaxTextures"/>；第 5 张切段。</summary>
    private static void FifthTextureCutsANewSegment()
    {
        var stream = new RenderStream("seg4");
        stream.BeginRebuild();
        for (uint i = 0; i < 5; i++)
            stream.AppendLeaf(RsLeaf(i + 1, i + 1), new[] { RsQuad(i * 10f, 0, 8, 8) }, new[] { Rgba(1, 1, 1, 255) });
        stream.EndRebuild();

        bool slotsAscend = true;
        for (int i = 0; i < 4; i++) if (stream.Leaves[i].TexSlot != i) slotsAscend = false;

        Check("段键: ≤4 纹理/段，第 5 张纹理切新段（texSlot 是 2bit 的定义域）",
            stream.SegmentCount == 2 && stream.Segments[0].TexCount == Abi.SegmentMaxTextures
            && slotsAscend && stream.Leaves[4].TexSlot == 0
            && stream.Segments[1].Start == 4 && stream.RunCount == 1);
    }

    /// <summary>裁决：**混合模式进段键、不是栅栏**——Add 自成一段，但不切 run。</summary>
    private static void BlendClassCutsSegmentNotRun()
    {
        var stream = new RenderStream("blend");
        stream.BeginRebuild();
        stream.AppendLeaf(RsLeaf(1, 3, BlendClass.Normal), new[] { RsQuad(0, 0, 8, 8) }, new[] { Rgba(1, 1, 1, 255) });
        stream.AppendLeaf(RsLeaf(2, 3, BlendClass.Add), new[] { RsQuad(9, 0, 8, 8) }, new[] { Rgba(1, 1, 1, 255) });
        stream.AppendLeaf(RsLeaf(3, 3, BlendClass.Normal), new[] { RsQuad(18, 0, 8, 8) }, new[] { Rgba(1, 1, 1, 255) });
        stream.EndRebuild();

        Check("段键: blendClass 进段键（Add 自成一段），但**不是栅栏**——run 不因它切开",
            stream.SegmentCount == 3 && stream.RunCount == 1
            && stream.Segments[1].Blend == BlendClass.Add
            && stream.Segments[0].RunIndex == stream.Segments[2].RunIndex);
    }

    // ── 内容 tier 与提交路径 ────────────────────────────────────────────────

    /// <summary>content 原位重写：slack 内变长变短都不动邻叶，变短的尾巴清干净。</summary>
    private static void ContentRewriteStaysInsideSlack()
    {
        var stream = new RenderStream("content");
        stream.BeginRebuild();
        LeafDesc text = RsLeaf(1, 2);
        text.SlackHint = 3;                       // 向上取 2 的幂 ⇒ 4
        stream.AppendLeaf(text, new[] { RsQuad(0, 0, 8, 8), RsQuad(9, 0, 8, 8) },
            new[] { Rgba(1, 1, 1, 255), Rgba(1, 1, 1, 255) });
        stream.AppendLeaf(RsLeaf(2, 2), new[] { RsQuad(50, 0, 8, 8) }, new[] { Rgba(9, 9, 9, 255) });
        stream.EndRebuild();

        uint neighbour = stream.Quads[4].Color;
        bool grew = stream.RewriteLeafContent(0,
            new[] { RsQuad(0, 0, 4, 4), RsQuad(5, 0, 4, 4), RsQuad(10, 0, 4, 4) },
            new[] { Rgba(2, 2, 2, 255), Rgba(2, 2, 2, 255), Rgba(2, 2, 2, 255) });
        bool shrank = stream.RewriteLeafContent(0, new[] { RsQuad(0, 0, 6, 6) }, new[] { Rgba(3, 3, 3, 255) });

        Check("不变量 4: content 重写落在 [start, start+slack)，变短清尾、邻叶不动",
            grew && shrank && stream.Leaf(0).Slack == 4 && stream.Leaf(0).Count == 1
            && stream.Quads[0].Rect.z == 6f && stream.Quads[1].Color == 0u
            && stream.Quads[2].Rect.z == 0f && stream.Quads[4].Color == neighbour);
    }

    /// <summary>不变量 4 的另一半：发射数超 slack **必须**升级 Structure，禁止越写邻叶。</summary>
    private static void SlackOverflowUpgradesToStructure()
    {
        var stream = new RenderStream("slack");
        stream.BeginRebuild();
        LeafDesc text = RsLeaf(1, 2);
        text.SlackHint = 2;
        stream.AppendLeaf(text, new[] { RsQuad(0, 0, 8, 8) }, new[] { Rgba(1, 1, 1, 255) });
        stream.AppendLeaf(RsLeaf(2, 2), new[] { RsQuad(50, 0, 8, 8) }, new[] { Rgba(9, 9, 9, 255) });
        stream.EndRebuild();
        uint neighbour = stream.Quads[2].Color;

        var five = new QuadInstance[5];
        var bc = new uint[5];
        for (int i = 0; i < 5; i++) { five[i] = RsQuad(i * 4f, 0, 3, 3); bc[i] = Rgba(4, 4, 4, 255); }
        bool ok = stream.RewriteLeafContent(0, five, bc);

        Check("不变量 4: 超 slack 的发射被拒并升级 Structure（邻叶一个字节都没动）",
            !ok && stream.Degrades.CountOf(DegradeKind.SlackOverflowToStructure) == 1
            && stream.Leaf(0).Count == 1 && stream.Quads[2].Color == neighbour);
    }

    /// <summary>tier-2 原位重 stamp 只表达轴对齐增量；含旋转即走阶梯，不硬算近似值。</summary>
    private static void Tier2RefusesRotationAndTakesTheLadder()
    {
        RenderStream stream = RsSingleLeaf(Rgba(1, 2, 3, 255), quads: 2);
        bool moved = stream.RestampLeaf(0, Affine2D.TRS(new Vector2(100f, 20f), 0f, new Vector2(2f, 2f)));
        Vector4 r = stream.Quads[0].Rect;
        bool rotated = stream.RestampLeaf(0, Affine2D.TRS(Vector2.zero, 0.5f, Vector2.one));

        Check("机制 3: tier-2 轴对齐增量原位重 stamp；含旋转 → Tier2ToStructure 阶梯",
            moved && r.x == 100f && r.z == 16f && !rotated
            && stream.Degrades.CountOf(DegradeKind.Tier2ToStructure) == 1);
    }

    /// <summary>
    /// 提交路径的判据门：**后端收到的字节 ≡ 镜像持有的字节**。
    /// 两份快照走同一份规范化（<see cref="CanonicalStream"/>），字节不等即报首差异偏移。
    /// </summary>
    private static void MirrorAndBackendAgreeByteForByte()
    {
        var backend = new MockBackend();
        var stream = new RenderStream("mirror");

        int slot = stream.ClaimSlot(RsNode(50));
        stream.BeginRebuild();
        int domain = stream.Clips.Push(ClipBook.NoneEntry,
            ClipParams.Rectangle(0, 0, 200, 200), RsNode(51), out _);
        LeafDesc a = RsLeaf(1, 1);
        a.ClipEntry = domain;
        a.Slot = slot;
        stream.AppendLeaf(a, new[] { RsQuad(10, 10, 20, 20) }, new[] { Rgba(255, 0, 0, 255) });
        stream.AppendLeaf(RsLeaf(2, 2), new[] { RsQuad(40, 10, 20, 20) }, new[] { Rgba(0, 255, 0, 200) });
        stream.EndRebuild();

        StreamHandle handle = stream.Attach(backend);
        stream.WriteSlot(slot, Affine2D.TRS(new Vector2(4f, -6f), 0f, Vector2.one));

        backend.BeginFrame(1);
        SubmitReport report = stream.Submit(backend);
        FrameStats stats = stream.BuildStats(1, in report);
        backend.BindMainSurface(new SurfaceDesc { Width = 64, Height = 64, ClearColor = 0 });
        backend.DrawStream(handle, PassHandle.None);
        backend.EndFrame(in stats);

        backend.SetBaseColors(handle, 0, stream.BaseColors);   // baseColor 不上 GPU，靠侧信道补齐快照
        byte[] mine = CanonicalStream.Canonicalize(stream.Snapshot());
        byte[] theirs = CanonicalStream.Canonicalize(backend.Snapshot(handle));

        Check("提交路径: 镜像快照与后端快照规范化后逐字节相等，且无违约、门全零",
            CanonicalStream.FirstDifference(mine, theirs) < 0
            && report.Calls == 5 && report.Quads == 2 && report.Segments && report.Orders
            && backend.Violations.Count == 0 && backend.Gates.Pass && stats.Dirty);
    }

    /// <summary>不变量 13: 五通道全空 ∧ structEpoch 未变 ∧ 孤岛静止 ⇒ 提交路径一次调用都不发。</summary>
    private static void ZeroDirtyFrameSubmitsNothing()
    {
        var backend = new MockBackend();
        var stream = new RenderStream("idle");
        stream.BeginRebuild();
        stream.AppendLeaf(RsLeaf(1, 1), new[] { RsQuad(0, 0, 8, 8) }, new[] { Rgba(1, 2, 3, 255) });
        stream.EndRebuild();
        StreamHandle handle = stream.Attach(backend);

        backend.BeginFrame(1);
        SubmitReport first = stream.Submit(backend);
        FrameStats s1 = stream.BuildStats(1, in first);
        backend.BindMainSurface(new SurfaceDesc { Width = 16, Height = 16, ClearColor = 0 });
        backend.DrawStream(handle, PassHandle.None);
        backend.EndFrame(in s1);

        backend.BeginFrame(2);
        SubmitReport idle = stream.Submit(backend);
        FrameStats s2 = stream.BuildStats(2, in idle);
        FrameReceipt receipt = backend.EndFrame(in s2);

        Check("不变量 13: 零脏帧提交零调用、零上传，收据记一次空转（ticks > presents）",
            !first.IsIdle && idle.IsIdle && !s2.Dirty && !stream.HasPendingWork
            && receipt.Ticks == 2 && receipt.Presents == 1 && !receipt.Presented
            && backend.Violations.Count == 0);
    }

    /// <summary>不变量 10: 序派生重推**当且仅当** structEpoch 变；颜色改动不触发重推。</summary>
    private static void RunOrdersOnlyOnStructEpochChange()
    {
        var backend = new MockBackend();
        var stream = new RenderStream("orders");
        stream.BeginRebuild();
        stream.AppendLeaf(RsLeaf(1, 1), new[] { RsQuad(0, 0, 8, 8) }, new[] { Rgba(1, 2, 3, 255) });
        stream.EndRebuild();
        StreamHandle handle = stream.Attach(backend);
        uint epoch0 = stream.StructEpoch;

        backend.BeginFrame(1);
        SubmitReport r1 = stream.Submit(backend);
        backend.EndFrame(stream.BuildStats(1, in r1));

        backend.BeginFrame(2);
        stream.RecolorLeaf(0, new IslandVisual { Alpha = 0.5f, Visible = true, Grayed = false });
        SubmitReport r2 = stream.Submit(backend);
        backend.EndFrame(stream.BuildStats(2, in r2));

        backend.BeginFrame(3);
        stream.BeginRebuild();
        stream.AppendLeaf(RsLeaf(1, 1), new[] { RsQuad(0, 0, 8, 8) }, new[] { Rgba(1, 2, 3, 255) });
        stream.AppendLeaf(RsLeaf(2, 1), new[] { RsQuad(9, 0, 8, 8) }, new[] { Rgba(1, 2, 3, 255) });
        stream.EndRebuild();
        SubmitReport r3 = stream.Submit(backend);
        backend.EndFrame(stream.BuildStats(3, in r3));

        Check("不变量 10: 序重推当且仅当 structEpoch 变（颜色排水不碰序）",
            r1.Orders && !r2.Orders && r2.Quads == 1 && r3.Orders
            && stream.StructEpoch == epoch0 + 1 && handle.Index == 1
            && backend.Violations.Count == 0);
    }
}
