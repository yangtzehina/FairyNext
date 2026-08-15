using FairyNext.Contracts;
using FairyNext.Core;
using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-06 NodeTable SoA + 代际句柄用例（Program 的 partial 分片）。
/// 逐条对齐 docs/architecture.md「平面一 · 数据宪法」不变量清单：
/// 1 代际单调门 / 2 resolved 唯一写者门 / 3 相位写门 / 4 等值切断收据 / 5 paintOrder ≡ 拓扑 /
/// 6 派生列相位所有权 / 7 resolved 槽静态集 / 9 Group 纯度 / 10 拓扑链自洽 /
/// 12 节点段地址稳定 / 13 同步读语义 / 15 单点几何。
/// </summary>
public static partial class Program
{
    /// <summary>debug 门是否编译在内（release 下 UiAssert.That 整体消失，门测试自动放行）。</summary>
    private static readonly bool DebugGates =
#if DEBUG
        true;
#else
        false;
#endif

    private sealed class MarkLog
    {
        public int Count;
        public Ch Last;
        public WriteSource LastSource;
        public uint LastIndex;

        public NodeTable.MarkHandler Hook => (t, i, ch, src) =>
        {
            Count++; Last = ch; LastSource = src; LastIndex = i;
            t.OrDirty(i, (uint)ch);   // 模拟 M1-07：树只存位，协议在失效平面
        };
    }

    private static void NodeTableSuite()
    {
        HandleDestroyIsImmediate();
        HandleGenFrozenUntilFrameEnd();
        HandleGenBumpsAtFrameEnd();
        HandleDeadBitAloneRejects();
        HandleCrossTreeRejected();
        HandleUnallocatedSlotRejected();

        TopologyRingSelfConsistent();
        TopologyAfterMiddleRemove();
        TopologyDestroyKillsSubtree();
        TopologyRemoveKeepsNodeAlive();
        TopologyInsertAndReorder();
        TopologyCycleGuard();
        TopologyChildByLocalId();

        PaintOrderEqualsDfsPreorder();
        PaintOrderReverseCursor();
        PaintOrderStructEpochOnlyOnStructure();
        PaintOrderStableWhileEpochUnchanged();
        PaintOrderSubtreeRange();
        PaintOrderPhaseGate();

        PivotRotationKnownValue();
        PivotIsFixedPoint();
        YDownConvention();
        SkewFormula();
        SetPivotKeepPosition();
        WorldComposeNested();
        WorldVisualCascade();

        EqualityCutoffNoMark();
        EqualityCutoffMarksOnChange();
        EqualityCutoffNaN();
        EqualityCutoffAlphaU8();
        WriteAuthoredRouting();
        WriteAuthoredPropIdsMatchAbi();

        ResolvedSharesAuthoredStorage();
        ResolvedWriterGate();
        ResolvedSlotDrivesLocalMatrix();

        SlabReusesFreedSegment();
        SlabKeepsTemplatesApart();
        GrowthKeepsHandlesValid();

        PhaseWriteGate();
        GroupPurityGate();
        SyncReadSemantics();
        DirtyWordIsStorageOnly();
    }

    // ── 工具 ────────────────────────────────────────────────────────────────

    /// <summary>跑 a，报告门是否响了（release 下门不存在，调用方按 DebugGates 放行）。</summary>
    private static bool AssertFires(Action a)
    {
        string? hit = null;
        var prev = UiAssert.Handler;
        UiAssert.Handler = m => hit ??= m;
        try { a(); }
        finally { UiAssert.Handler = prev; }
        return hit != null;
    }

    private static bool Near(float a, float b, float eps = 1e-4f) => MathF.Abs(a - b) <= eps;

    private static bool NearMat(in Affine2D a, in Affine2D b, float eps = 1e-4f) =>
        Near(a.m00, b.m00, eps) && Near(a.m01, b.m01, eps) &&
        Near(a.m10, b.m10, eps) && Near(a.m11, b.m11, eps) &&
        Near(a.tx, b.tx, eps) && Near(a.ty, b.ty, eps);

    /// <summary>root → a → (b, c) 的小树。</summary>
    private static NodeTable SmallTree(out NodeHandle a, out NodeHandle b, out NodeHandle c)
    {
        var t = new NodeTable();
        a = t.CreateNode(NodeType.Component);
        b = t.CreateNode(NodeType.Image);
        c = t.CreateNode(NodeType.Text);
        t.AddChild(t.Root, a);
        t.AddChild(a, b);
        t.AddChild(a, c);
        return t;
    }

    // ── 不变量 1：代际单调门 ────────────────────────────────────────────────

    private static void HandleDestroyIsImmediate()
    {
        var t = SmallTree(out var a, out _, out _);
        t.Destroy(a);
        Check("句柄纪律：Destroy 后同帧 IsAlive == false（标记即死）", !t.IsAlive(a));
    }

    private static void HandleGenFrozenUntilFrameEnd()
    {
        var t = SmallTree(out var a, out _, out _);
        ushort before = t.GenerationOf(a.Index);
        t.Destroy(a);
        ushort after = t.GenerationOf(a.Index);
        Check("代际单调门：P0–P8 内 gen 不变（Destroy 不换代）", before == after);
    }

    private static void HandleGenBumpsAtFrameEnd()
    {
        var t = SmallTree(out var a, out _, out _);
        ushort before = t.GenerationOf(a.Index);
        t.Destroy(a);
        t.EndFrame();
        ushort after = t.GenerationOf(a.Index);
        Check("代际单调门：EndFrame(P9) 统一 gen++ 且旧句柄解引用失败",
            after == (ushort)(before + 1) && !t.TryResolve(a, out _));
    }

    private static void HandleDeadBitAloneRejects()
    {
        // 同帧 Destroy 后事件链安全：gen 还没变（只验 gen 会放行），DEAD 已置（双验才失败）。
        var t = SmallTree(out var a, out _, out _);
        t.Destroy(a);
        bool genStillMatches = t.GenerationOf(a.Index) == a.Gen;
        Check("不变量 1：只验代际是漏洞——gen 相等但 DEAD 已置必须解引用失败",
            genStillMatches && !t.TryResolve(a, out _));
    }

    private static void HandleCrossTreeRejected()
    {
        var t = new NodeTable(tree: 3);
        var n = t.CreateNode();
        var alien = new NodeHandle(n.Index, n.Gen, 4);
        Check("句柄自含树域：跨树句柄解引用失败", t.IsAlive(n) && !t.IsAlive(alien));
    }

    private static void HandleUnallocatedSlotRejected()
    {
        var t = new NodeTable();
        // 手工构造指向未分配槽的句柄（gen 初值 1）：新槽自带 DEAD，必须失败。
        var forged = new NodeHandle(30, 1, 0);
        Check("未分配槽自带 DEAD：伪造句柄解引用失败",
            !t.IsAlive(forged) && !t.IsAlive(NodeHandle.None));
    }

    // ── 不变量 10：拓扑链自洽 ───────────────────────────────────────────────

    private static void TopologyRingSelfConsistent()
    {
        var t = SmallTree(out var a, out var b, out var c);
        var d = t.CreateNode(NodeType.Image);
        t.AddChild(a, d);
        bool ok = t.ValidateTopology(out string err);
        bool links = t.Parent(b).Equals(a) && t.Parent(c).Equals(a)
            && t.FirstChild(a).Equals(b) && t.LastChild(a).Equals(d)
            && t.NextSibling(b).Equals(c) && t.PrevSibling(c).Equals(b)
            && t.NextSibling(d).IsNone && t.PrevSibling(b).IsNone
            && t.ChildCount(a) == 3;
        Check("拓扑链自洽：parent/firstChild/nextSib/prevSib 双向一致 " + err, ok && links);
    }

    private static void TopologyAfterMiddleRemove()
    {
        var t = SmallTree(out var a, out var b, out var c);
        var d = t.CreateNode(NodeType.Image);
        t.AddChild(a, d);
        t.RemoveFromParent(c);                       // 摘中间那个
        bool ok = t.ValidateTopology(out string err);
        Check("拓扑链自洽：删中间子后链仍自洽且序保持 " + err,
            ok && t.ChildCount(a) == 2 && t.NextSibling(b).Equals(d)
            && t.PrevSibling(d).Equals(b) && t.LastChild(a).Equals(d));
    }

    private static void TopologyDestroyKillsSubtree()
    {
        var t = SmallTree(out var a, out var b, out var c);
        t.Destroy(a);
        Check("Destroy 整棵子树置 DEAD（后代句柄同时失效）",
            !t.IsAlive(a) && !t.IsAlive(b) && !t.IsAlive(c)
            && t.ChildCount(t.Root) == 0 && t.ValidateTopology(out _));
    }

    private static void TopologyRemoveKeepsNodeAlive()
    {
        var t = SmallTree(out var a, out var b, out _);
        t.RemoveFromParent(a);
        Check("机制⑬：RemoveFromParent 只摘链不杀节点（子树仍活）",
            t.IsAlive(a) && t.IsAlive(b) && t.Parent(a).IsNone && t.ChildCount(t.Root) == 0);
    }

    private static void TopologyInsertAndReorder()
    {
        var t = SmallTree(out var a, out var b, out var c);
        var d = t.CreateNode(NodeType.Image);
        t.AddChildAt(a, d, 0);                       // 插到最前
        bool inserted = t.ChildAt(a, 0).Equals(d) && t.ChildAt(a, 1).Equals(b)
            && t.ChildAt(a, 2).Equals(c) && t.IndexOfChild(d) == 0;
        t.SetChildIndex(d, 2);                       // 挪到最后
        bool moved = t.ChildAt(a, 2).Equals(d) && t.IndexOfChild(d) == 2
            && t.FirstChild(a).Equals(b) && t.ValidateTopology(out _);
        Check("AddChildAt / SetChildIndex 序正确且链自洽", inserted && moved);
    }

    private static void TopologyCycleGuard()
    {
        var t = SmallTree(out var a, out var b, out _);
        bool fired = AssertFires(() => t.AddChild(b, a));
        // 成环是真拒绝而非仅 debug 门：环会让前序遍历死循环，release 下也必须挡住。
        bool intact = t.ValidateTopology(out _) && t.Parent(a).Equals(t.Root)
            && t.Parent(b).Equals(a) && t.ChildCount(b) == 0;
        Check("结构门：AddChild 成环被真拒绝（链保持不变）", intact && (!DebugGates || fired));
    }

    private static void TopologyChildByLocalId()
    {
        var t = new NodeTable();
        var seg = t.AllocSegment(templateId: 11, count: 5);
        var owner = t.HandleOf(seg.Base);
        var third = t.ChildByLocalId(owner, 3);
        Check("机制⑩：ChildByLocalId = nodeBase + localId（O(1)，零字符串）",
            third.Index == seg.Base + 3 && t.LocalIdOf(third) == 3);
    }

    // ── 不变量 5：paintOrder ≡ 拓扑 ─────────────────────────────────────────

    private static void PaintOrderEqualsDfsPreorder()
    {
        var t = SmallTree(out var a, out var b, out var c);
        var d = t.CreateNode(NodeType.Image);
        t.AddChild(b, d);                            // root → a → (b → d, c)
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();
        var order = t.GetPaintOrder().ToArray();
        bool expected = order.Length == 5
            && order[0] == (int)t.Root.Index && order[1] == (int)a.Index
            && order[2] == (int)b.Index && order[3] == (int)d.Index
            && order[4] == (int)c.Index;
        bool gate = t.ValidatePaintOrder(out string err);
        Check("paintOrder ≡ 拓扑 DFS 前序，且 paintIndex[paintOrder[i]]==i " + err, expected && gate);
    }

    private static void PaintOrderReverseCursor()
    {
        var t = SmallTree(out _, out _, out _);
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();
        var fwd = new List<int>();
        foreach (int i in t.GetPaintCursor()) fwd.Add(i);
        var rev = new List<int>();
        foreach (int i in t.GetPaintCursor(reverse: true)) rev.Add(i);
        rev.Reverse();
        bool same = fwd.Count == rev.Count && fwd.Count == 4;
        for (int k = 0; same && k < fwd.Count; k++) same = fwd[k] == rev[k];
        Check("PaintCursor 正/逆序枚举互为反转（命中测试读逆序）", same);
    }

    private static void PaintOrderStructEpochOnlyOnStructure()
    {
        var t = SmallTree(out var a, out _, out _);
        uint e0 = t.StructEpoch;
        t.SetPosition(a, 5f, 6f);
        t.SetSize(a, 100f, 50f);
        t.SetAlpha(a, 0.5f);
        t.SetVisible(a, false);
        uint e1 = t.StructEpoch;
        t.AddChild(t.Root, t.CreateNode());
        uint e2 = t.StructEpoch;
        t.RemoveFromParent(a);
        uint e3 = t.StructEpoch;
        Check("structEpoch 仅结构变化时递增（authored setter 不动它）",
            e1 == e0 && e2 == e1 + 1 && e3 == e2 + 1);
    }

    private static void PaintOrderStableWhileEpochUnchanged()
    {
        var t = SmallTree(out _, out _, out _);
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();
        var first = t.GetPaintOrder().ToArray();
        uint epoch = t.StructEpoch;
        t.ApplyStructure();                          // 无结构变化：不得重展
        var second = t.GetPaintOrder().ToArray();
        bool identical = first.Length == second.Length && epoch == t.StructEpoch;
        for (int k = 0; identical && k < first.Length; k++) identical = first[k] == second[k];
        Check("不变量 5：structEpoch 未变 ⇒ paintOrder 数组 bit-identical", identical);
    }

    private static void PaintOrderSubtreeRange()
    {
        var t = SmallTree(out var a, out var b, out _);
        var d = t.CreateNode(NodeType.Image);
        t.AddChild(b, d);
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();
        bool ok = t.TryGetSubtreeRange(a, out int start, out int count);
        Check("DFS 前序使子树在 paintOrder 中连续（M1-14 切片拼接的前提）",
            ok && start == 1 && count == 4);
    }

    private static void PaintOrderPhaseGate()
    {
        var t = SmallTree(out _, out _, out _);
        t.Phase = FramePhase.P4_State;
        bool fired = AssertFires(() => t.ApplyStructure());
        bool fired2 = AssertFires(() => t.DrainDerivedFull());
        Check("不变量 6：paintOrder / world 重算仅 P6 —— 他相位调用即断言",
            !DebugGates || (fired && fired2));
    }

    // ── 不变量 15：单点几何 ─────────────────────────────────────────────────

    private static void PivotRotationKnownValue()
    {
        var t = new NodeTable();
        var n = t.CreateNode(NodeType.Image);
        t.SetSize(n, 100f, 100f);
        t.SetPivot(n, 0.5f, 0.5f);
        t.SetRotation(n, MathF.PI / 2f);             // +90°：从 +x 转向 +y
        var m = t.LocalMatrix(n);
        var topLeft = m.TransformPoint(new Vector2(0f, 0f));
        var topRight = m.TransformPoint(new Vector2(100f, 0f));
        var center = m.TransformPoint(new Vector2(50f, 50f));
        Check("pivot 唯一公式：90° + 中心 pivot 的已知点变换（左上→右上，中心不动）",
            Near(topLeft.x, 100f) && Near(topLeft.y, 0f)
            && Near(topRight.x, 100f) && Near(topRight.y, 100f)
            && Near(center.x, 50f) && Near(center.y, 50f));
    }

    private static void PivotIsFixedPoint()
    {
        var t = new NodeTable();
        var n = t.CreateNode(NodeType.Image);
        t.SetPosition(n, 17f, -3f);
        t.SetSize(n, 80f, 40f);
        t.SetPivot(n, 0.25f, 0.75f);
        t.SetRotation(n, 0.7f);
        t.SetScale(n, 2f, 0.5f);
        var m = t.LocalMatrix(n);
        var p = m.TransformPoint(new Vector2(0.25f * 80f, 0.75f * 40f));
        Check("pivot 是旋转/缩放不动点：变换后仍落在 pos + p·size",
            Near(p.x, 17f + 20f) && Near(p.y, -3f + 30f));
    }

    private static void YDownConvention()
    {
        var t = new NodeTable();
        var n = t.CreateNode(NodeType.Image);
        t.SetRotation(n, MathF.PI / 2f);
        var m = t.LocalMatrix(n);
        var v = m.TransformPoint(new Vector2(1f, 0f));
        Check("y 向下约定：正角 = 从 +x 转向 +y（屏幕上顺时针），核心不做任何 y 翻转",
            Near(v.x, 0f) && Near(v.y, 1f));
    }

    private static void SkewFormula()
    {
        var t = new NodeTable();
        var n = t.CreateNode(NodeType.Image);
        t.SetSkew(n, MathF.Atan(0.5f));              // tan k = 0.5
        var m = t.LocalMatrix(n);
        var v = m.TransformPoint(new Vector2(0f, 10f));
        Check("单值 skew = 水平剪切 K=[[1,tan k],[0,1]]", Near(v.x, 5f) && Near(v.y, 10f));
    }

    private static void SetPivotKeepPosition()
    {
        var t = new NodeTable();
        var n = t.CreateNode(NodeType.Image);
        t.SetPosition(n, 10f, 20f);
        t.SetSize(n, 100f, 50f);
        t.SetRotation(n, 0.5f);
        t.SetScale(n, 2f, 1.5f);
        var before = t.LocalMatrix(n);
        t.SetPivot(n, 0.5f, 0.5f, keepPosition: true);
        var after = t.LocalMatrix(n);
        Check("机制⑪：SetPivot(keepPosition) 显式换算 authored 位置，局部矩阵不变",
            NearMat(before, after) && !Near(t.GetPosition(n).x, 10f));
    }

    private static void WorldComposeNested()
    {
        var t = new NodeTable();
        var p = t.CreateNode(NodeType.Component);
        var c = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, p);
        t.AddChild(p, c);
        t.SetPosition(p, 100f, 0f);
        t.SetRotation(p, MathF.PI / 2f);
        t.SetPosition(c, 10f, 5f);
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();
        t.DrainDerivedFull();
        var w = t.World(c);
        Check("P6 一遍两算：world[c] = world[parent] ⊗ local(c)",
            Near(w.tx, 95f) && Near(w.ty, 10f) && Near(w.m00, 0f) && Near(w.m01, -1f));
    }

    private static void WorldVisualCascade()
    {
        var t = new NodeTable();
        var p = t.CreateNode(NodeType.Component);
        var c = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, p);
        t.AddChild(p, c);
        t.SetAlpha(p, 0.5f);
        t.SetGrayed(p, true);
        t.SetVisible(c, false);
        t.Phase = FramePhase.P6_Settle;
        t.ApplyStructure();
        t.DrainDerivedFull();
        uint wv = t.WorldVisual(c);
        Check("worldVisual 级联：α 积 / visible AND / grayed OR",
            Visual.Alpha(wv) == 128 && (wv & Visual.Visible) == 0 && (wv & Visual.Grayed) != 0);
    }

    // ── 不变量 4：等值切断收据 ──────────────────────────────────────────────

    private static void EqualityCutoffNoMark()
    {
        var t = SmallTree(out var a, out _, out _);
        var log = new MarkLog();
        t.InvalidationHook = log.Hook;
        t.SetPosition(a, 3f, 4f);
        int afterFirst = log.Count;
        uint dirtyAfterFirst = t.GetDirty(a.Index);
        t.ClearDirty(a.Index, uint.MaxValue);
        t.SetPosition(a, 3f, 4f);                    // 同值
        t.SetSize(a, 0f, 0f);                        // 同值（初值就是 0）
        t.SetAlpha(a, 1f);                           // 同值（初值 255）
        t.SetVisible(a, true);                       // 同值
        Check("等值切断：写同值不置脏、不触发 Mark（裁决 13）",
            afterFirst == 1 && dirtyAfterFirst != 0
            && log.Count == afterFirst && t.GetDirty(a.Index) == 0);
    }

    private static void EqualityCutoffMarksOnChange()
    {
        var t = SmallTree(out var a, out _, out _);
        var log = new MarkLog();
        t.InvalidationHook = log.Hook;
        t.SetSize(a, 100f, 50f, WriteSource.Layout);
        Check("值真的变了才 Mark：通道 = Ch.Layout，来源按 WriteSource 分流",
            log.Count == 1 && log.Last == Ch.Layout && log.LastSource == WriteSource.Layout
            && log.LastIndex == a.Index && (t.GetDirty(a.Index) & (uint)Ch.Layout) != 0);
    }

    private static void EqualityCutoffNaN()
    {
        var t = SmallTree(out var a, out _, out _);
        var log = new MarkLog();
        t.SetPosition(a, float.NaN, 0f);
        t.InvalidationHook = log.Hook;
        t.SetPosition(a, float.NaN, 0f);             // NaN 视为相等（否则该属性永脏、每帧空转）
        Check("等值切断比较语义：float 位等且 NaN 视为相等", log.Count == 0);
    }

    private static void EqualityCutoffAlphaU8()
    {
        var t = SmallTree(out var a, out _, out _);
        var log = new MarkLog();
        t.SetAlpha(a, 0.5f);
        t.InvalidationHook = log.Hook;
        t.SetAlpha(a, 0.5f + 0.0005f);               // 落回同一个 u8
        bool noMark = log.Count == 0;
        t.SetAlpha(a, 0.25f);
        Check("等值切断在存储值（u8 α）上进行，不在入参上",
            noMark && log.Count == 1 && (log.Last & Ch.DownColor) != 0);
    }

    private static void WriteAuthoredRouting()
    {
        var t = SmallTree(out var a, out _, out _);
        var log = new MarkLog();
        t.InvalidationHook = log.Hook;
        bool w1 = t.WriteAuthored(a, 128, 42f, WriteSource.Anim);       // X
        bool w2 = t.WriteAuthored(a, 128, 42f, WriteSource.Anim);       // 同值
        bool w3 = t.WriteAuthored(a, 1, 200f, WriteSource.Layout);      // Width
        bool bad = AssertFires(() => t.WriteAuthored(a, 250, 1f, WriteSource.User));
        Check("WriteAuthored 内部写口：PropId 分派 + 等值切断 + 未归属 PropId 断言",
            w1 && !w2 && w3 && Near(t.GetPosition(a).x, 42f)
            && Near(t.GetSize(a).x, 200f) && log.Count == 2 && (!DebugGates || bad));
    }

    private static void WriteAuthoredPropIdsMatchAbi()
    {
        // 分派表的 id 必须与 ABI 单源一致（M1-09 codegen 接管后本 switch 由生成代码替换）。
        byte Id(string name)
        {
            foreach (var p in Abi.PropIds) if (p.Name == name) return p.Id;
            return 0;
        }
        Check("WriteAuthored 的 PropId 与 Abi.PropIds 同源",
            Id("Width") == 1 && Id("Height") == 2 && Id("Alpha") == 64 && Id("Visible") == 65
            && Id("Grayed") == 66 && Id("X") == 128 && Id("Y") == 129
            && Id("ScaleX") == 130 && Id("ScaleY") == 131 && Id("Rotation") == 132);
    }

    // ── 不变量 2/7：resolved ────────────────────────────────────────────────

    private static void ResolvedSharesAuthoredStorage()
    {
        var t = SmallTree(out var a, out _, out _);
        t.SetPosition(a, 7f, 8f);
        t.SetSize(a, 30f, 40f);
        var r = t.GetResolved(a);
        Check("机制③：resolvedRef==0 ⇒ 读 resolved 即读 authored（同一存储、零拷贝）",
            t.ResolvedRef(a) == 0 && Near(r.X, 7f) && Near(r.Y, 8f) && Near(r.W, 30f) && Near(r.H, 40f));
    }

    private static void ResolvedWriterGate()
    {
        var t = SmallTree(out var a, out _, out _);
        t.AllocResolvedSlot(a);
        t.Phase = FramePhase.P5_Layout;
        bool srcGate = AssertFires(() => t.SetResolved(a, 1f, 1f, 1f, 1f, WriteSource.User));
        t.Phase = FramePhase.P4_State;
        bool phaseGate = AssertFires(() => t.SetResolved(a, 1f, 1f, 1f, 1f, WriteSource.Layout));
        var b = t.CreateNode();
        t.Phase = FramePhase.P5_Layout;
        bool slotGate = AssertFires(() => t.SetResolved(b, 1f, 1f, 1f, 1f, WriteSource.Layout));
        bool dup = AssertFires(() => t.AllocResolvedSlot(a));
        Check("resolved 唯一写者门：src==Layout ∧ P5 ∧ 有槽，三缺一即断言（含孤儿槽/重复分配）",
            !DebugGates || (srcGate && phaseGate && slotGate && dup));
    }

    private static void ResolvedSlotDrivesLocalMatrix()
    {
        var t = SmallTree(out var a, out _, out _);
        t.SetPosition(a, 1f, 2f);
        t.SetSize(a, 10f, 10f);
        t.AllocResolvedSlot(a);
        t.Phase = FramePhase.P5_Layout;
        t.SetResolved(a, 50f, 60f, 20f, 20f, WriteSource.Layout);
        var m = t.LocalMatrix(a);
        var authored = t.GetPosition(a);
        Check("布局产出经 resolved 槽落到几何：local 用 resolved，GetPosition 仍读 authored",
            Near(m.tx, 50f) && Near(m.ty, 60f) && Near(authored.x, 1f) && Near(authored.y, 2f));
    }

    // ── 机制⑦：slab 与地址稳定 ─────────────────────────────────────────────

    private static void SlabReusesFreedSegment()
    {
        var t = new NodeTable();
        var seg = t.AllocSegment(templateId: 21, count: 4);
        uint baseIdx = seg.Base;
        t.DestroySegment(seg);
        t.EndFrame();
        var again = t.AllocSegment(templateId: 21, count: 4);
        Check("slab 复用：段回收后同模板再分配拿到同一段（复用命中率 ≈100%）",
            again.Base == baseIdx && again.Count == 4);
    }

    private static void SlabKeepsTemplatesApart()
    {
        var t = new NodeTable();
        var s1 = t.AllocSegment(31, 3);
        var s2 = t.AllocSegment(32, 3);
        t.DestroySegment(s1);
        t.EndFrame();
        var s3 = t.AllocSegment(32, 3);              // 32 的空闲栈是空的 → 走 bump，不许拿到 31 的段
        var s4 = t.AllocSegment(31, 3);              // 31 的空闲栈有货 → 拿回原段
        Check("slab 每模板一个空闲栈：不同模板不串段",
            s2.Base == s1.Base + 3 && s3.Base != s1.Base && s4.Base == s1.Base);
    }

    private static void GrowthKeepsHandlesValid()
    {
        var t = new NodeTable(tree: 0, initialCapacity: 2);
        var first = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, first);
        t.SetPosition(first, 11f, 22f);
        uint epoch0 = t.TableEpoch;
        int cap0 = t.Capacity;
        for (int i = 0; i < 200; i++) t.AddChild(t.Root, t.CreateNode(NodeType.Image));
        var pos = t.GetPosition(first);
        Check("2 倍扩容：句柄 = 下标，扩容不移动语义（旧句柄与列值都活着），TableEpoch 记录列数组搬家",
            t.IsAlive(first) && Near(pos.x, 11f) && Near(pos.y, 22f)
            && t.Capacity > cap0 && t.TableEpoch > epoch0 && t.ValidateTopology(out _));
    }

    // ── 相位门 / 纯度 / 同步读 ──────────────────────────────────────────────

    private static void PhaseWriteGate()
    {
        var t = SmallTree(out var a, out _, out _);
        t.Phase = FramePhase.P7_RenderDrain;
        bool fired = AssertFires(() => t.SetPosition(a, 99f, 99f));
        bool structFired = AssertFires(() => t.AddChild(t.Root, t.CreateNode()));
        // 写值本身照做（release 下只延迟 Mark，不丢写入）——降级路径在断言之外。
        bool valueWritten = Near(t.GetPosition(a).x, 99f);
        t.Phase = FramePhase.P3_Commands;
        Check("不变量 3：P7/P8 authored/结构写 debug 断言，且写入不丢",
            valueWritten && (!DebugGates || (fired && structFired)));
    }

    private static void GroupPurityGate()
    {
        var t = new NodeTable();
        var g = t.CreateNode(NodeType.Group);
        bool s = AssertFires(() => t.SetScale(g, 2f, 2f));
        bool r = AssertFires(() => t.SetRotation(g, 1f));
        bool c = AssertFires(() => t.SetContentRef(g, 7));
        Check("不变量 9：Group 纯度（contentRef==0 ∧ scale==1 ∧ rotation==0）",
            !DebugGates || (s && r && c));
    }

    private static void SyncReadSemantics()
    {
        var t = new NodeTable();
        var n = t.CreateNode(NodeType.Image);
        t.AddChild(t.Root, n);
        t.SetPosition(n, 5f, 6f);
        // 不变量 13：同相位立即可见——任何平面把逻辑读做成延迟，这条就红。
        Check("同步读语义：AddChild / SetPosition 后同相位立即可见（逻辑真值同步）",
            t.FirstChild(t.Root).Equals(n) && t.Parent(n).Equals(t.Root)
            && Near(t.GetPosition(n).x, 5f) && Near(t.GetPosition(n).y, 6f));
    }

    private static void DirtyWordIsStorageOnly()
    {
        var t = SmallTree(out var a, out _, out _);
        t.OrDirty(a.Index, 0b1011);
        uint got = t.GetDirty(a.Index);
        t.ClearDirty(a.Index, 0b0010);
        t.SetSubtreeStamp(a.Index, 77);
        Check("树只持 dirtyWord/subtreeStamp 存储列并提供 O(1) 位存取（位语义归失效平面）",
            got == 0b1011 && t.GetDirty(a.Index) == 0b1001 && t.GetSubtreeStamp(a.Index) == 77);
    }
}
