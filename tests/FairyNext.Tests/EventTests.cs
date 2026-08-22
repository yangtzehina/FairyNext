using FairyNext.Backend.Mock;
using FairyNext.Core;
using FairyNext.Core.Events;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-21 事件平面用例（Program 的 partial 分片）：命中与派发。
///
/// 对齐 docs/architecture.md 平面四 B 的事件半边：
/// 命中测试（迭代下行 / 上帧序 / <c>local ⊗ slotMatrix</c> / clip 剪枝 / hitMode 策略）、
/// 链快照派发与句柄双验、downChain 快照 click、CaptureTouch/monitor、ref struct 防呆；
/// 以及事件·不变量 1/2/3/4/5/6/8/9。
///
/// 两条 2026-08 审计遗留的验收项在本包结账：
///  ① **DownLayer 通道的排水路径不再零覆盖**——<see cref="DownLayerDrillLandsOnLeafColor"/>
///     钉住「Mark(DownLayer) → P6 下钻 → 落叶交回 Ch.Color/CascadeDown」这条真实路径；
///     产品写者归 M1-23/M2-12 的成文裁决由 <see cref="DownLayerHasNoProductWriterYet"/> 守着
///     （出现首个写者即红，逼出「级联落到命中/绘制序」的行为用例）。
///  ② **clip 剪枝与渲染剔除同源**——<see cref="ClipCullAndHitAgreeOnTheSameWindow"/>：
///     同一裁剪场景里「被渲染剔除的叶」与「命中不到的叶」必须是同一只，两侧同吃
///     <c>Extract._clipOf</c>（结构上就没有第二份裁剪账可对不齐）。
/// </summary>
public static partial class Program
{
    private static void EventSuite()
    {
        // 命中基础矩阵
        HitFindsTheLeafUnderNestedTranslation();
        HitFallsBackToOpaqueContainerOnly();
        HitRespectsRotationInverse();
        HitTakesTopmostSiblingFirst();
        HitSkipsInvisibleSubtreeWithStaleWorldVisual();
        HitSkipsUntouchableSubtree();
        HitModeNoneIsASubtreeBlackHole();

        // 骑槽（local ⊗ slotMatrix）
        HitFollowsTheSlotMatrix();

        // 上帧序（一帧偏差是明文契约）
        HitUsesLastFrameOrderForZChanges();
        FreshNodeIsNotHittableInTheSameFrame();

        // 位图命中
        PixelMaskHoleIsNotHit();
        PixelMaskIsAGateForChildren();
        PixelHitMatchesForkFormula();

        // clip
        ClipWindowPrunesTheSubtree();
        ClipCullAndHitAgreeOnTheSameWindow();

        // 派发
        CaptureThenBubbleOrder();
        StopPropagationCutsTheRest();
        ChainSnapshotSurvivesTreeEditsInCallbacks();
        DeadNodeGetsNoEventInTheSameFrame();
        PruneNeverMissesAListener();

        // 触摸状态机
        DownChainClickSurvivesTargetMoving();
        DoubleClickQuadruple();
        DragBeyondThresholdCancelsClick();
        CaptureTouchKeepsMonitorFed();

        // 接线与纪律
        InputHandlerIsExclusive();
        PhaseZeroCallbackWriteLandsThisFrame();
        AxisEntryRejectsZeroAndFractional();
        ListenerBlockIsReturnedOnDispose();
        EventCtxIsRefStructAndBuiltinIdsFit();

        // DownLayer 结账
        DownLayerDrillLandsOnLeafColor();
        DownLayerHasNoProductWriterYet();
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    /// <summary>一棵手建的树 + 相位机 + 事件平面（无渲染管线；P6 走全量派生）。</summary>
    private sealed class EvFixture
    {
        internal readonly NodeTable Table = new NodeTable(tree: 11);
        internal readonly Invalidation Inval;
        internal readonly UiKernel Kernel;
        internal readonly EventSystem Es;
        private FrameTime _time = FrameTime.First(0.016f, 0.016f);

        /// <summary>喂给输入包的宿主时间戳（秒）。</summary>
        internal double Now;

        internal EvFixture()
        {
            Inval = new Invalidation(Table);
            Kernel = new UiKernel(Table, Inval);
            Es = new EventSystem(Table);
            Es.Attach(Kernel);
        }

        internal NodeHandle Box(float x, float y, float w, float h, NodeHandle parent = default)
        {
            NodeHandle n = Table.CreateNode(NodeType.Component);
            Table.SetPosition(n, x, y);
            Table.SetSize(n, w, h);
            Table.AddChild(parent.IsNone ? Table.Root : parent, n);
            return n;
        }

        internal NodeHandle Leaf(float x, float y, float w, float h, NodeHandle parent = default)
        {
            NodeHandle n = Table.CreateNode(NodeType.Image);
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

        internal void Post(InputKind kind, float x, float y, int id = 0, byte button = 0,
            float dx = 0f, float dy = 0f) =>
            Kernel.PostInput(new InputPacket(kind, id, x, y, dx, dy, 0, button, Now));

        internal HitResult Hit(float x, float y) => Es.HitTest(new Vector2(x, y));
    }

    private static readonly LeafSpec EvSolid = LeafSpec.Solid(0xFFFFFFFFu);

    // ── 命中基础矩阵 ────────────────────────────────────────────────────────

    private static void HitFindsTheLeafUnderNestedTranslation()
    {
        var f = new EvFixture();
        NodeHandle box = f.Box(10f, 10f, 100f, 100f);
        NodeHandle leaf = f.Leaf(5f, 5f, 20f, 20f, box);
        f.Tick();

        HitResult a = f.Hit(20f, 20f);        // → box 局部 (10,10) → leaf 局部 (5,5)
        HitResult b = f.Hit(34.9f, 34.9f);    // leaf 右下角内
        HitResult c = f.Hit(36f, 36f);        // leaf 外、box 内（box 不 opaque）
        Check("hit 平移: 嵌套两级后命中叶且局部坐标正确",
            a.Node.Equals(leaf) && BitEquals.Eq(a.Local.x, 5f) && BitEquals.Eq(a.Local.y, 5f)
            && b.Node.Equals(leaf) && !c.IsHit);
    }

    private static void HitFallsBackToOpaqueContainerOnly()
    {
        var f = new EvFixture();
        NodeHandle box = f.Box(10f, 10f, 100f, 100f);
        f.Leaf(5f, 5f, 20f, 20f, box);
        f.Tick();

        bool missBefore = !f.Hit(80f, 80f).IsHit;
        f.Es.HitPolicy.SetOpaque(box, true);
        HitResult after = f.Hit(80f, 80f);
        Check("hit 兜底: 容器默认不吃空白，opaque 置位后才兜底",
            missBefore && after.Node.Equals(box));
    }

    private static void HitRespectsRotationInverse()
    {
        var f = new EvFixture();
        NodeHandle n = f.Leaf(100f, 100f, 40f, 40f);
        f.Table.SetRotation(n, MathF.PI / 2f);      // +90°：局部 (lx,ly) → 台上 (100-ly, 100+lx)
        f.Tick();

        HitResult inside = f.Hit(95f, 110f);        // 局部 (10,5)
        HitResult outside = f.Hit(110f, 95f);       // 局部 (-5,-10)
        Check("hit 旋转: 逆变换按唯一那份局部矩阵求逆",
            inside.Node.Equals(n) && MathF.Abs(inside.Local.x - 10f) < 1e-4f
            && MathF.Abs(inside.Local.y - 5f) < 1e-4f && !outside.IsHit);
    }

    private static void HitTakesTopmostSiblingFirst()
    {
        var f = new EvFixture();
        NodeHandle under = f.Leaf(0f, 0f, 50f, 50f);
        NodeHandle over = f.Leaf(0f, 0f, 50f, 50f);
        f.Tick();
        Check("hit 逆序: 重叠兄弟里后画的先吃点击",
            f.Hit(10f, 10f).Node.Equals(over) && !f.Hit(10f, 10f).Node.Equals(under));
    }

    private static void HitSkipsInvisibleSubtreeWithStaleWorldVisual()
    {
        // 14b CRITICAL 的修复面：隐藏子树免下钻会让后代的 worldVisual **合法陈旧**（仍报 visible）。
        // 命中若逐点判 wv 就会命中一棵看不见的子树——所以位门直读 authored localVisual、靠剪枝级联。
        var f = new PipeFixture();
        NodeHandle box = f.Box(0f, 0f, 100f, 100f);
        NodeHandle leaf = f.Leaf(in EvSolid, 10f, 10f, 20f, 20f, box);
        var es = new EventSystem(f.Table);
        es.Attach(f.Kernel);
        f.Tick();

        // ① 写完还没到 P6：worldVisual 整列还是上帧的（仍报可见），authored 位已经是 false。
        //    位门直读 authored ⇒ 立刻不可命中（不变量 13：写后同相位可见）。读 wv 的实现在这里就红。
        f.Table.SetVisible(box, false);
        bool boxWvStillVisible = (f.Table.WorldVisual(box) & Visual.Visible) != 0;
        bool missImmediately = !es.HitTest(new Vector2(15f, 15f)).IsHit;

        // ② 过了 P6：box 的 wv 收敛了，但**后代免下钻**，leaf 的 wv 合法陈旧地仍报可见。
        f.Tick();
        bool staleLeafWv = (f.Table.WorldVisual(leaf) & Visual.Visible) != 0;
        bool missAfterSettle = !es.HitTest(new Vector2(15f, 15f)).IsHit;

        Check("hit 隐藏子树: 派生列陈旧报可见（写后未 P6 / 后代免下钻），命中两次都必须落空",
            boxWvStillVisible && missImmediately && staleLeafWv && missAfterSettle && f.Sound());
    }

    private static void HitSkipsUntouchableSubtree()
    {
        var f = new EvFixture();
        NodeHandle box = f.Box(0f, 0f, 100f, 100f);
        NodeHandle leaf = f.Leaf(10f, 10f, 20f, 20f, box);
        f.Tick();

        bool before = f.Hit(15f, 15f).Node.Equals(leaf);
        f.Table.SetTouchable(box, false);
        bool after = !f.Hit(15f, 15f).IsHit;
        Check("hit touchable: 父不可点则整支不可点（AND 级联由剪枝实现）", before && after);
    }

    private static void HitModeNoneIsASubtreeBlackHole()
    {
        var f = new EvFixture();
        NodeHandle box = f.Box(0f, 0f, 100f, 100f);
        NodeHandle leaf = f.Leaf(10f, 10f, 20f, 20f, box);
        f.Tick();

        bool before = f.Hit(15f, 15f).Node.Equals(leaf);
        f.Es.HitPolicy.SetMode(box, HitMode.None);
        bool after = !f.Hit(15f, 15f).IsHit;
        Check("hit 黑洞: HitMode.None 让整棵子树不参与命中", before && after);
    }

    // ── local ⊗ slotMatrix ──────────────────────────────────────────────────

    private static void HitFollowsTheSlotMatrix()
    {
        // 「滚动位移只写槽矩阵、不写节点 local」的对偶义务：命中必须合成槽矩阵，
        // 否则点到的是滚走之前的位置。
        var f = new EvFixture();
        var slots = new SlotTable();
        f.Es.Hit.Slots = slots;

        NodeHandle box = f.Box(100f, 100f, 100f, 100f);
        NodeHandle child = f.Leaf(0f, 0f, 20f, 20f, box);
        f.Tick();

        bool beforeHit = f.Hit(105f, 105f).Node.Equals(child);

        int slot = slots.Claim(box);
        slots.Write(slot, new Affine2D(1f, 0f, 0f, 1f, -30f, 0f));   // 内容左移 30（= 滚动）
        f.Es.Hit.BindSlot(box, slot);

        bool movedAway = !f.Hit(105f, 105f).IsHit;
        HitResult moved = f.Hit(75f, 105f);
        Check("hit 骑槽: 有效局部 = local ⊗ slotMatrix，命中跟着槽走",
            beforeHit && movedAway && moved.Node.Equals(child)
            && MathF.Abs(moved.Local.x - 5f) < 1e-4f);
    }

    // ── 上帧序 ──────────────────────────────────────────────────────────────

    private static void HitUsesLastFrameOrderForZChanges()
    {
        var f = new EvFixture();
        NodeHandle a = f.Leaf(0f, 0f, 50f, 50f);
        NodeHandle b = f.Leaf(0f, 0f, 50f, 50f);
        f.Tick();
        bool topIsB = f.Hit(10f, 10f).Node.Equals(b);

        f.Table.SetChildIndex(a, 1);              // 把 a 提到最上（结构真值立即变）
        bool stillB = f.Hit(10f, 10f).Node.Equals(b);   // 本帧命中读的是**上帧**收敛的序
        f.Tick();
        bool nowA = f.Hit(10f, 10f).Node.Equals(a);
        Check("hit 上帧序: 本帧改 z 序不改变本帧命中，下一帧才生效", topIsB && stillB && nowA);
    }

    private static void FreshNodeIsNotHittableInTheSameFrame()
    {
        var f = new EvFixture();
        f.Tick();
        NodeHandle fresh = f.Leaf(0f, 0f, 50f, 50f);
        bool notYet = !f.Hit(10f, 10f).IsHit;
        f.Tick();
        bool nowHit = f.Hit(10f, 10f).Node.Equals(fresh);
        Check("hit 上帧序: 当帧新建的节点当帧不可命中（明文契约）", notYet && nowHit);
    }

    // ── 位图命中 ────────────────────────────────────────────────────────────

    /// <summary>4×4 的 1bit 位图：左半列（x&lt;2）实心、右半列透明。</summary>
    private static PixelHitMask EvMask4x4()
    {
        // 位序与 fork 同：pos = y*width + x，字节内低位在前。
        var bytes = new byte[2];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int pos = y * 4 + x;
                bytes[pos / 8] |= (byte)(1 << (pos % 8));
            }
        }
        return new PixelHitMask(bytes, 0, bytes.Length, 4, 1f, 0, 0, 4f, 4f);
    }

    private static void PixelMaskHoleIsNotHit()
    {
        var f = new EvFixture();
        NodeHandle n = f.Leaf(0f, 0f, 40f, 40f);
        f.Es.HitPolicy.SetPixelMask(n, EvMask4x4());
        f.Tick();

        // 逻辑框 40×40 映射到 4×4 源像素：局部 (5,5) → 源 (0.5,0.5) → 位 (0,0) 实心
        bool solid = f.Hit(5f, 5f).Node.Equals(n);
        // 局部 (35,5) → 源 (3.5,0.5) → 位 (3,0) 透明
        bool hole = !f.Hit(35f, 5f).IsHit;
        Check("hit 位图: 透明洞不命中、实心处命中", solid && hole);
    }

    private static void PixelMaskIsAGateForChildren()
    {
        var f = new EvFixture();
        NodeHandle box = f.Box(0f, 0f, 40f, 40f);
        NodeHandle child = f.Leaf(30f, 0f, 10f, 10f, box);   // 落在位图的透明半边
        f.Es.HitPolicy.SetPixelMask(box, EvMask4x4());
        f.Es.HitPolicy.SetOpaque(box, true);                 // 与 fork 同：hitArea 不代替 opaque
        f.Tick();
        Check("hit 位图: 位图是门——透明洞里的孩子也点不着",
            !f.Hit(35f, 5f).IsHit && f.Hit(5f, 5f).Node.Equals(box)
            && f.Table.IsAlive(child));
    }

    private static void PixelHitMatchesForkFormula()
    {
        // 逐位对 fork PixelHitTest.HitTest（Assets/Scripts/Core/HitTest/PixelHitTest.cs 62-80 @ 08a2d56）：
        // x = floor((local.x * sourceWidth / rect.width - offsetX) * scale)，y 同式；
        // 位取 (pixels[offset + pos/8] >> pos%8) & 1。
        PixelHitMask m = EvMask4x4();
        var rect = new Rect(0f, 0f, 8f, 8f);        // 逻辑框 8 → 源 4：每源像素 2 逻辑单位
        bool p00 = PixelHitTest.Hit(in m, in rect, new Vector2(1f, 1f));    // → (0,0) 实心
        bool p20 = PixelHitTest.Hit(in m, in rect, new Vector2(5f, 1f));    // → (2,0) 透明
        bool p13 = PixelHitTest.Hit(in m, in rect, new Vector2(3f, 7f));    // → (1,3) 实心
        bool outside = PixelHitTest.Hit(in m, in rect, new Vector2(9f, 1f));// 框外
        bool empty = PixelHitTest.Hit(default, in rect, new Vector2(1f, 1f));

        // 框外早退不是冗余：offset 为负时（位图覆盖到节点框左边以外），框外的点会被
        // 换算**回**位图界内——少了第一行就会命中一块根本不在节点上的像素。
        var shifted = new PixelHitMask(m.Pixels, 0, m.Length, 4, 1f, -2, 0, 4f, 4f);
        var unit = new Rect(0f, 0f, 4f, 4f);
        bool leftOfBox = PixelHitTest.Hit(in shifted, in unit, new Vector2(-1f, 1f));

        Check("hit 位图: 判定式与 fork 同值（含框外、负 offset 折回、空位图）",
            p00 && !p20 && p13 && !outside && !empty && !leftOfBox);
    }

    // ── clip ────────────────────────────────────────────────────────────────

    private static ContentRecord EvClipBox() => new ContentRecord
    {
        Kind = ExtractKind.None,
        OpensClip = true,
        Clip = new ClipShape { Soft = Vector2.zero, Radii = Vector4.zero },
        RenderEveryN = 1,
    };

    private static void ClipWindowPrunesTheSubtree()
    {
        // 嵌套裁剪：外域 (0,0,50,50) ∩ 内域 (10,10,70,70) 折叠成 (10,10,50,50)。
        // 叶铺满内域（根空间 10..70），于是「叶里但折叠窗口外」的点是真实存在的——
        // 命中若不吃 clip，(55,45) 就会点中一块**画不出来**的像素。
        var f = new ExFixture();
        NodeHandle outer = f.Box(0f, 0f, 50f, 50f, EvClipBox());
        NodeHandle inner = f.Box(10f, 10f, 60f, 60f, EvClipBox(), outer);
        NodeHandle leaf = f.Leaf(ExSolid(1), 0f, 0f, 60f, 60f, inner);
        f.Rebuild();

        var policy = new HitPolicyTable(f.Table);
        var tester = new HitTester(f.Table, policy) { ClipSource = f.Extract, Slots = f.Stream.Slots };
        Check("hit clip: 嵌套折叠窗口内命中、窗口外整支剪掉",
            tester.Hit(new Vector2(45f, 45f)).Node.Equals(leaf)
            && !tester.Hit(new Vector2(55f, 45f)).IsHit
            && !tester.Hit(new Vector2(45f, 5f)).IsHit);
    }

    private static void ClipCullAndHitAgreeOnTheSameWindow()
    {
        // 14b 登记的验收项②：渲染剔除的叶，命中也必须剔除——两侧同吃 Extract._clipOf。
        var f = new ExFixture();
        NodeHandle clip = f.Box(0f, 0f, 50f, 50f, EvClipBox());
        NodeHandle inside = f.Leaf(ExSolid(1), 10f, 10f, 20f, 20f, clip);
        NodeHandle outside = f.Leaf(ExSolid(1), 60f, 10f, 20f, 20f, clip);   // 整只在窗口外
        ExtractReport r = f.Rebuild();

        var tester = new HitTester(f.Table, new HitPolicyTable(f.Table))
        {
            ClipSource = f.Extract,
            Slots = f.Stream.Slots,
        };

        bool insideDrawn = false, outsideDrawn = false;
        ReadOnlySpan<LeafRange> leaves = f.Stream.Leaves;
        for (int i = 0; i < leaves.Length; i++)
        {
            if (leaves[i].Node.Equals(inside)) insideDrawn = true;
            if (leaves[i].Node.Equals(outside)) outsideDrawn = true;
        }

        bool insideHit = tester.Hit(new Vector2(15f, 15f)).Node.Equals(inside);
        bool outsideHit = tester.Hit(new Vector2(65f, 15f)).IsHit;

        Check("clip 同判: 渲染剔除的叶命中也剔除（两侧同源 _clipOf）",
            r.ClipCulled == 1 && insideDrawn && !outsideDrawn
            && insideHit && !outsideHit
            && ReferenceEquals(tester.ClipSource, f.Extract));
    }

    // ── 派发 ────────────────────────────────────────────────────────────────

    private static void CaptureThenBubbleOrder()
    {
        var f = new EvFixture();
        NodeHandle a = f.Box(0f, 0f, 100f, 100f);
        NodeHandle b = f.Box(0f, 0f, 80f, 80f, a);
        NodeHandle leaf = f.Leaf(0f, 0f, 20f, 20f, b);
        f.Tick();

        var log = new List<string>();
        f.Es.Listeners.Add(a, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) => log.Add("cap:a"), ListenPhase.Capture);
        f.Es.Listeners.Add(b, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) => log.Add("cap:b"), ListenPhase.Capture);
        f.Es.Listeners.Add(leaf, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) => log.Add("tgt:leaf"), ListenPhase.Capture);
        f.Es.Listeners.Add(leaf, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) => log.Add("bub:leaf"));
        f.Es.Listeners.Add(b, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) => log.Add("bub:b"));
        f.Es.Listeners.Add(a, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) => log.Add("bub:a"));

        f.Post(InputKind.PointerDown, 5f, 5f);
        f.Tick();

        Check("dispatch 两相: capture 根→目标，bubble 目标→根",
            string.Join(",", log) == "cap:a,cap:b,tgt:leaf,bub:leaf,bub:b,bub:a");
    }

    private static void StopPropagationCutsTheRest()
    {
        var f = new EvFixture();
        NodeHandle a = f.Box(0f, 0f, 100f, 100f);
        NodeHandle leaf = f.Leaf(0f, 0f, 20f, 20f, a);
        f.Tick();

        var log = new List<string>();
        f.Es.Listeners.Add(leaf, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) =>
        {
            log.Add("leaf");
            c.StopPropagation();
        });
        f.Es.Listeners.Add(a, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) => log.Add("a"));

        f.Post(InputKind.PointerDown, 5f, 5f);
        f.Tick();
        Check("dispatch 截断: StopPropagation 之后的链节点不再收到", log.Count == 1 && log[0] == "leaf");
    }

    private static void ChainSnapshotSurvivesTreeEditsInCallbacks()
    {
        // 回调里把祖先从树上摘走：链已快照，本次派发路径不变；被摘的节点仍在，故照常收到。
        var f = new EvFixture();
        NodeHandle a = f.Box(0f, 0f, 100f, 100f);
        NodeHandle b = f.Box(0f, 0f, 80f, 80f, a);
        NodeHandle leaf = f.Leaf(0f, 0f, 20f, 20f, b);
        f.Tick();

        var log = new List<string>();
        NodeTable table = f.Table;
        f.Es.Listeners.Add(leaf, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) =>
        {
            log.Add("leaf");
            table.RemoveFromParent(b);          // 派发中改树
        });
        f.Es.Listeners.Add(b, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) => log.Add("b"));
        f.Es.Listeners.Add(a, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) => log.Add("a"));

        f.Post(InputKind.PointerDown, 5f, 5f);
        f.Tick();
        Check("dispatch 链快照: 回调里改树不改变本次派发路径",
            string.Join(",", log) == "leaf,b,a" && table.Parent(b).IsNone);
    }

    private static void DeadNodeGetsNoEventInTheSameFrame()
    {
        // 事件·不变量 4：双验 gen **且** DEAD 位——标记即死的节点当帧不再收事件（gen++ 在 P9）。
        var f = new EvFixture();
        NodeHandle a = f.Box(0f, 0f, 100f, 100f);
        NodeHandle b = f.Box(0f, 0f, 80f, 80f, a);
        NodeHandle leaf = f.Leaf(0f, 0f, 20f, 20f, b);
        f.Tick();

        var log = new List<string>();
        NodeTable table = f.Table;
        bool genUnchangedAtDestroy = false;
        f.Es.Listeners.Add(leaf, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) =>
        {
            log.Add("leaf");
            ushort before = table.GenerationOf(b.Index);
            table.Destroy(b);                    // 标记即死：句柄立刻不可解引用
            // 只验 gen 会放行僵尸：此刻 gen 还没换（P9 才换），拦住它的是 DEAD 位。
            genUnchangedAtDestroy = table.GenerationOf(b.Index) == before && !table.IsAlive(b);
        });
        f.Es.Listeners.Add(b, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) => log.Add("b"));
        f.Es.Listeners.Add(a, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) => log.Add("a"));

        f.Post(InputKind.PointerDown, 5f, 5f);
        f.Tick();
        Check("dispatch 双验: 帧中被销毁的节点当帧不再收事件（gen 未变、DEAD 已置）",
            string.Join(",", log) == "leaf,a" && genUnchangedAtDestroy
            && f.Es.LastFrameStats.DeadChainSkips == 1);
    }

    private static void PruneNeverMissesAListener()
    {
        // 事件·不变量 9：凭 head/builtinMask 跳过的节点，全量对照必真无监听。
        var f = new EvFixture();
        NodeHandle a = f.Box(0f, 0f, 100f, 100f);
        NodeHandle b = f.Leaf(0f, 0f, 20f, 20f, a);
        f.Tick();

        f.Es.Listeners.Add(a, UiEvents.Click, (ref EventCtx c, in PointerInput p) => { });
        f.Es.Listeners.Add(b, UiEvents.TouchEnd, (ref EventCtx c, in PointerInput p) => { });
        EventId<PointerInput> user = EventRegistry.Register<PointerInput>("m1-21.test.user");
        f.Es.Listeners.Add(b, user, (ref EventCtx c, in PointerInput p) => { });

        bool ok = f.Es.Listeners.PruneMatchesFullScan(UiEvents.Click.Raw, out uint bad1)
            && f.Es.Listeners.PruneMatchesFullScan(UiEvents.TouchEnd.Raw, out uint bad2)
            && f.Es.Listeners.PruneMatchesFullScan(UiEvents.TouchMove.Raw, out uint bad3)
            && f.Es.Listeners.PruneMatchesFullScan(user.Raw, out uint bad4)
            && bad1 == NodeTable.NoIndex && bad2 == NodeTable.NoIndex
            && bad3 == NodeTable.NoIndex && bad4 == NodeTable.NoIndex;
        // 用户事件 ≥64 不进位图：剪枝对它必须保守放行（多算允许）
        bool userNotPruned = f.Es.Listeners.MayReceive(b.Index, user.Raw);
        bool builtinPruned = !f.Es.Listeners.MayReceive(a.Index, UiEvents.TouchMove.Raw);
        Check("dispatch 剪枝: 两级剪枝多算允许、漏算即红（全量神谕）", ok && userNotPruned && builtinPruned);
    }

    // ── 触摸状态机 ──────────────────────────────────────────────────────────

    private static void DownChainClickSurvivesTargetMoving()
    {
        // 按钮缩放动画：按下后目标缩小/移开，抬起时指针已不在它上面——ClickTest 优先 downChain[0]。
        var f = new EvFixture();
        NodeHandle btn = f.Leaf(0f, 0f, 50f, 50f);
        f.Tick();

        var clicks = new List<string>();
        f.Es.Listeners.Add(btn, UiEvents.Click, (ref EventCtx c, in PointerInput p) => clicks.Add("click:" + p.ClickCount));

        f.Post(InputKind.PointerDown, 10f, 10f);
        f.Tick();
        f.Table.SetSize(btn, 5f, 5f);            // 目标缩小，抬起点已在框外
        f.Now += 0.05;
        f.Post(InputKind.PointerUp, 12f, 12f);
        f.Tick();

        Check("click downChain: 目标缩走后仍点中按下时的目标",
            clicks.Count == 1 && clicks[0] == "click:1");
    }

    private static void DoubleClickQuadruple()
    {
        var f = new EvFixture();
        NodeHandle btn = f.Leaf(0f, 0f, 50f, 50f);
        f.Tick();

        var counts = new List<int>();
        f.Es.Listeners.Add(btn, UiEvents.Click, (ref EventCtx c, in PointerInput p) => counts.Add(p.ClickCount));

        for (int i = 0; i < 3; i++)
        {
            f.Post(InputKind.PointerDown, 10f, 10f);
            f.Tick();
            f.Now += 0.05;
            f.Post(InputKind.PointerUp, 10f, 10f);
            f.Tick();
            f.Now += 0.05;
        }
        // 第四下拖到时间窗之外
        f.Now += 1.0;
        f.Post(InputKind.PointerDown, 10f, 10f);
        f.Tick();
        f.Post(InputKind.PointerUp, 10f, 10f);
        f.Tick();

        Check("click 双击: (时间窗, 逐轴位移, 同键) 四元组 + 第三下回 1 + 超时归 1",
            counts.Count == 4 && counts[0] == 1 && counts[1] == 2 && counts[2] == 1 && counts[3] == 1);
    }

    private static void DragBeyondThresholdCancelsClick()
    {
        var f = new EvFixture();
        NodeHandle btn = f.Leaf(0f, 0f, 400f, 400f);
        f.Tick();

        int clicks = 0;
        f.Es.Listeners.Add(btn, UiEvents.Click, (ref EventCtx c, in PointerInput p) => clicks++);

        f.Post(InputKind.PointerDown, 10f, 10f);
        f.Tick();
        f.Post(InputKind.PointerMove, 200f, 10f);    // 位移 > 阈值 50
        f.Tick();
        f.Post(InputKind.PointerUp, 200f, 10f);
        f.Tick();
        Check("click 取消: 位移超阈值即不是点击（列表滚动误触）",
            clicks == 0 && f.Es.SlotOf(0).ClickCancelled);
    }

    private static void CaptureTouchKeepsMonitorFed()
    {
        // CaptureTouch：离开宿主仍收 move/end，且**只收自身相、不冒泡**。
        var f = new EvFixture();
        NodeHandle host = f.Leaf(0f, 0f, 20f, 20f);
        NodeHandle other = f.Leaf(100f, 0f, 20f, 20f);
        f.Tick();

        var log = new List<string>();
        f.Es.Listeners.Add(host, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) => c.CaptureTouch());
        f.Es.Listeners.Add(host, UiEvents.TouchMove, (ref EventCtx c, in PointerInput p) =>
            log.Add("move@" + (c.Phase == EventPhase.Direct ? "direct" : "chain")));
        f.Es.Listeners.Add(host, UiEvents.TouchEnd, (ref EventCtx c, in PointerInput p) => log.Add("end"));
        f.Es.Listeners.Add(other, UiEvents.TouchMove, (ref EventCtx c, in PointerInput p) => log.Add("other"));

        f.Post(InputKind.PointerDown, 5f, 5f);
        f.Tick();
        bool registered = f.Es.SlotOf(0).MonitorCount == 1 && f.Es.SlotOf(0).MonitorAt(0).Equals(host);

        f.Post(InputKind.PointerMove, 105f, 5f);     // 指针已离开宿主、落在 other 上
        f.Tick();
        f.Post(InputKind.PointerUp, 105f, 5f);
        f.Tick();

        Check("monitor: CaptureTouch 后离开宿主仍收 move/end，且是直发相",
            registered && string.Join(",", log) == "move@direct,end"
            && f.Es.SlotOf(0).MonitorCount == 0);
    }

    // ── 接线与纪律 ──────────────────────────────────────────────────────────

    private static void InputHandlerIsExclusive()
    {
        var f = new EvFixture();
        var second = new EventSystem(new NodeTable(tree: 12));
        bool threw = false;
        try { second.Attach(f.Kernel); }
        catch (InvalidOperationException) { threw = true; }
        Check("接线: P0 输入消费者独占（两个消费者会把同一批包排两遍）", threw);
    }

    private static void PhaseZeroCallbackWriteLandsThisFrame()
    {
        // 相位纪律：P0 回调的写只 Mark（排水未开始），故同帧 P6/P7 就落地——「按下就动」当帧可见。
        var f = new PipeFixture();
        NodeHandle leaf = f.Leaf(in EvSolid, 0f, 0f, 20f, 20f);
        var es = new EventSystem(f.Table);
        es.Attach(f.Kernel);
        f.Tick();

        NodeTable table = f.Table;
        es.Listeners.Add(leaf, UiEvents.TouchBegin, (ref EventCtx c, in PointerInput p) =>
            table.SetPosition(leaf, 40f, 0f));

        f.Kernel.PostInput(new InputPacket(InputKind.PointerDown, 0, 5f, 5f));
        IncrementalGateResult g = f.TickAndCheck();

        Check("相位: P0 回调里的写当帧落地（写只 Mark，排水在后）",
            BitEquals.Eq(f.Table.World(leaf).tx, 40f) && g.Pass && f.Sound());
    }

    private static void AxisEntryRejectsZeroAndFractional()
    {
        var f = new EvFixture();
        NodeHandle leaf = f.Leaf(0f, 0f, 50f, 50f);
        f.Tick();

        int hits = 0;
        int delta = 0;
        f.Es.Listeners.Add(leaf, UiEvents.Axis, (ref EventCtx c, in AxisDelta a) => { hits++; delta += a.Delta; });

        var prev = UiAssert.Handler;
        UiAssert.Handler = _ => { };
        try
        {
            f.Post(InputKind.Wheel, 10f, 10f, dy: 0f);         // 零值：不是事件
            f.Post(InputKind.Wheel, 10f, 10f, dy: 0.5f);       // 亚单位：入口拒绝并计数
            f.Post(InputKind.Wheel, 10f, 10f, dy: -3f);        // 合法 canonical
            f.Tick();
        }
        finally { UiAssert.Handler = prev; }

        Check("axis 入口: 零值与非整数被拒并计数，整数照发",
            hits == 1 && delta == -3 && f.Es.LastFrameStats.AxisRejected == 1);
    }

    private static void ListenerBlockIsReturnedOnDispose()
    {
        var f = new EvFixture();
        NodeHandle n = f.Leaf(0f, 0f, 20f, 20f);
        f.Tick();
        f.Es.Listeners.Add(n, UiEvents.Click, (ref EventCtx c, in PointerInput p) => { });
        int liveAfterAdd = f.Es.Listeners.LiveBlocks;
        int pooled = f.Es.Listeners.PooledBlocks;

        f.Table.Destroy(n);
        f.Tick();                                    // P9 统一换代那一刻归还

        NodeHandle m = f.Leaf(0f, 0f, 20f, 20f);
        f.Tick();
        f.Es.Listeners.Add(m, UiEvents.Click, (ref EventCtx c, in PointerInput p) => { });

        Check("监听块: 节点销毁归还整块，池不涨（稳态零分配）",
            liveAfterAdd == 1 && f.Es.Listeners.LiveBlocks == 1
            && f.Es.Listeners.PooledBlocks == pooled && f.Es.Listeners.ListenerCount == 1);
    }

    private static void EventCtxIsRefStructAndBuiltinIdsFit()
    {
        // 事件·不变量 1/2 的可执行半边：
        //  · EventCtx 是 ref struct ⇒ 存字段/闭包捕获/await 跨越由**语言规则**拦下（CS8345/CS1628）；
        //    真编译负例在 tools/PropGen.Tests（那边有 Roslyn），这里钉住类型形态本身；
        //  · 内建 id < 64 是编译期常量除零门，BuiltinMaskFits 恒 1 说明门还在。
        Check("类型: EventCtx 是 ref struct + 内建 id 静态装得进 builtinMask",
            typeof(EventCtx).IsByRefLike && typeof(EventCtx).IsValueType
            && UiEvents.BuiltinMaskFits == 1
            && UiEvents.BuiltinCount == UiEvents.Names.Length
            && UiEvents.BuiltinCount == UiEvents.Payloads.Length
            && UiEvents.RightClick.Raw < UiEvents.BuiltinLimit
            && EventRegistry.Register<PointerInput>("m1-21.test.user").Raw >= UiEvents.BuiltinLimit);
    }

    // ── DownLayer 结账（2026-08 审计遗留验收项①）────────────────────────────

    private static void DownLayerDrillLandsOnLeafColor()
    {
        // 该通道的排水路径此前**零覆盖**：无人 Mark ⇒ RenderPipeline.StepDownLeaf 里
        // 「DownLayer ⇒ 交回 Ch.Color」那一支从未被走过。本用例把它走通并钉死落点。
        var f = new PipeFixture();
        NodeHandle box = f.Box(0f, 0f, 100f, 100f);
        NodeHandle leaf = f.Leaf(in EvSolid, 0f, 0f, 20f, 20f, box);
        f.Tick();

        f.Inval.MarkDown(box, Ch.DownLayer, InvalidateReason.GearPage);
        bool stamped = f.Inval.IsStampedThisFrame(box);
        IncrementalGateResult g = f.TickAndCheck();

        Check("DownLayer 覆盖: 下钻访问到叶并把变化交回 Ch.Color/CascadeDown",
            stamped && f.Inval.LastFrame.DownVisits >= 2
            && f.Inval.LastFrame.MarksOf(InvalidateReason.CascadeDown) >= 1
            && f.Inval.LastFrame.MarksOf(Ch.Color) >= 1
            && f.Table.IsAlive(leaf) && g.Pass && f.Sound());
    }

    private static void DownLayerHasNoProductWriterYet()
    {
        // **成文裁决的执法点**（M1-21）：DownLayer 的首个**产品**写者不在本包——
        // 命中缓存是帧内的（帧号护栏），不需要失效通道；绘制序的层归 Ch.Structure；
        // 真正会写它的是「整棵子树翻层」——孤岛 visual 并入下行（M1-23）与滤镜 RT 域翻层
        // （M2-12，fork 的 Container.SetChildrenLayer 同源）。
        // 本门守着这条裁决：一旦有属性把 DownLayer 列进 Marks，它就红——红的时候请同时补上
        // 「DownLayer 级联落到命中/绘制序」的行为用例，然后改本门。
        int writers = 0;
        string names = "";
        NodePropInfo[] all = NodeProps.All;
        for (int i = 0; i < all.Length; i++)
        {
            if ((all[i].Marks & Ch.DownLayer) == 0) continue;
            writers++;
            names += all[i].Name + " ";
        }
        Check("DownLayer 裁决: 归属表里零产品写者（出现首个写者即红，见用例注释）"
            + (writers > 0 ? " <- " + names : ""), writers == 0);
    }
}
