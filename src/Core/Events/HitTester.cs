using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Core.Events;

/// <summary>
/// 裁剪域的**只读消费面**（命中侧唯一的 clip 数据源）。
///
/// 14b-4 死字段裁决的执行形态：<c>worldVisual</c> 的 pad:u16 曾被草图当作 clip 域 id，
/// 它是保留段恒零、取值宏已删；clip 域的唯一数据面是 <see cref="Extract"/> 的 <c>_clipOf</c>。
/// 命中不许自己维护第二份裁剪账——它与渲染剔除必须**同源**，否则「看得见点不着 / 看不见点得着」
/// 是两套账各自算对、合起来错的典型形态。
/// </summary>
public interface IHitClipSource
{
    /// <summary>节点所在的裁剪域条目（<see cref="ClipBook.NoneEntry"/> = 不裁剪）。</summary>
    int ClipEntryOf(uint nodeIndex);

    /// <summary>
    /// 取一个条目的窗口：<paramref name="rect"/> 是 <c>xMin,yMin,xMax,yMax</c>，
    /// 表达在 <paramref name="slotFrame"/> 这个帧里（identity = 根空间）。
    /// </summary>
    bool TryGetClipWindow(int entry, out Vector4 rect, out Affine2D slotFrame);
}

/// <summary>一次命中的结果（目标 + 目标局部空间的点）。</summary>
public readonly struct HitResult
{
    /// <summary>命中的节点（<see cref="NodeHandle.None"/> = 未命中）。</summary>
    public readonly NodeHandle Node;
    /// <summary>命中点在该节点局部空间的坐标。</summary>
    public readonly Vector2 Local;

    internal HitResult(NodeHandle node, Vector2 local)
    {
        Node = node;
        Local = local;
    }

    /// <summary>是否命中。</summary>
    public bool IsHit => !Node.IsNone;

    /// <inheritdoc/>
    public override string ToString() => IsHit ? $"{Node} @ {Local}" : "miss";
}

/// <summary>
/// 命中测试（架构平面四 B「命中测试（含 local⊗slotMatrix）」）：**显式栈迭代下行**，无递归。
///
/// 一步的判据序（顺序即成本序，贵的在后）：
///  1. <c>visible &amp;&amp; touchable</c> 位门——直读 <c>localVisual</c> 列。
///     级联由**剪枝本身**实现：父不可点则整支不下行，这与 <c>Visual.Cascade</c> 的 AND 语义同结论，
///     且不依赖 worldVisual 的新鲜度（隐藏子树免下钻会让后代的 wv 合法陈旧）；
///  2. <see cref="HitMode.None"/>——命中黑洞，整支跳过；
///  3. 局部坐标 = 父局部点经**有效局部变换**求逆。节点绑了槽时
///     <c>有效局部 = local ⊗ slotMatrix</c>——滚动位移只写槽矩阵、不写节点 local，
///     命中若只看 local 就会点到滚走之前的位置（「滚动 = 写一个 float4」承诺的对偶义务）；
///  4. clip 剪枝——走 <see cref="IHitClipSource"/>（= Extract 的 <c>_clipOf</c>，同源）；
///  5. hitMode 策略：Rect 查 resolved 框，PixelTest 查 1bit 位图，Shape/Custom 走侧表。
///     **非 Rect 模式是门**：判否即整支不下行（fork <c>hitArea</c> 同语义）；
///  6. <c>touchChildren</c> 则按**上帧 paintOrder 的逆序**迭代孩子（后画的先吃点击）；
///  7. 全不中且 <c>opaque</c> 且自身区域内 ⇒ 返回自己。
///
/// **序是上一帧的**（不变量·门 6）：读的是 <see cref="NodeTable.GetPaintOrder"/> 上次收敛的数组
/// 与同一次收敛的 <c>paintIndex/paintEnd</c>，不是本帧的链式真值。一帧偏差是明文契约：
/// 「当帧新建节点当帧不可命中」「本帧改 z 序不改变本帧命中」由此成立，也因此 P0 永远读不到
/// 半成品的序。上次收敛之后被回收再分配的槽由 <c>paintIndex</c> 反查对不上而跳过。
/// </summary>
public sealed class HitTester
{
    private struct Frame
    {
        internal uint Index;
        internal Vector2 Local;
        internal int BlockStart;   // 子块在 _childScratch 的起点
        internal int Cursor;       // 下一个要试的孩子（自末尾向前）
        internal bool SelfIn;      // 自身命中区判定（兜底用）
    }

    private readonly NodeTable _table;
    private readonly HitPolicyTable _policy;

    private Frame[] _stack = new Frame[32];
    private int _top;
    private int[] _childScratch = new int[64];
    private int _childTop;
    private int[] _chainScratch = new int[32];   // TryStageToLocal 专用（不与命中栈共用缓冲）

    private int[] _slotOf = Array.Empty<int>();   // 节点下标 → 槽 id（0 = IdentitySlot = 未绑）
    private Vector2 _stagePoint;

    /// <summary>接一棵树与一张命中策略表。</summary>
    public HitTester(NodeTable table, HitPolicyTable policy)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <summary>树域。</summary>
    public NodeTable Table => _table;

    /// <summary>命中策略表。</summary>
    public HitPolicyTable Policy => _policy;

    /// <summary>
    /// 槽矩阵数据源（渲染流的只读视图；架构「事件依赖 · 渲染流」那一条）。
    /// 未装时绑定的槽一律按 identity 处理——**不猜**：没有矩阵源就没有槽语义。
    /// </summary>
    public SlotTable? Slots { get; set; }

    /// <summary>裁剪域数据源（<see cref="Extract"/> 直接实现；未装 = 不做 clip 剪枝）。</summary>
    public IHitClipSource? ClipSource { get; set; }

    /// <summary>最近一次命中走过的节点数（诊断 <c>EventStats.hitTestSteps</c>）。</summary>
    public int LastSteps { get; private set; }

    /// <summary>会话累计的命中次数。</summary>
    public long HitTests { get; private set; }

    // ── 骑槽绑定 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 把节点绑到一个 transform 槽：此后它的**有效局部变换** = <c>local ⊗ slotMatrix</c>。
    /// M2-06 的 ScrollPane 是它的第一个产品写者（滚动位移只写槽矩阵）；
    /// M1 期它是命中侧的接缝形态，语义与签名先钉住。
    /// </summary>
    public void BindSlot(NodeHandle node, int slot)
    {
        if (!_table.TryResolve(node, out uint i))
        {
            UiAssert.That(false, "BindSlot 于已失效句柄");
            return;
        }
        EnsureSlots(i);
        _slotOf[i] = slot;
    }

    /// <summary>解绑（回到纯 local）。</summary>
    public void UnbindSlot(NodeHandle node)
    {
        if (_table.TryResolve(node, out uint i) && i < (uint)_slotOf.Length) _slotOf[i] = SlotTable.IdentitySlot;
    }

    /// <summary>节点绑定的槽（<see cref="SlotTable.IdentitySlot"/> = 未绑）。</summary>
    public int SlotOf(NodeHandle node) =>
        _table.TryResolve(node, out uint i) && i < (uint)_slotOf.Length ? _slotOf[i] : SlotTable.IdentitySlot;

    private void EnsureSlots(uint i)
    {
        if (i < (uint)_slotOf.Length) return;
        int cap = _slotOf.Length == 0 ? 32 : _slotOf.Length;
        while (cap <= i) cap *= 2;
        Array.Resize(ref _slotOf, cap);
    }

    /// <summary>节点的有效局部变换（<c>local ⊗ slotMatrix</c>；未绑槽即 local 本身）。</summary>
    public Affine2D EffectiveLocal(NodeHandle node) =>
        _table.TryResolve(node, out uint i) ? EffectiveLocalAt(i) : Affine2D.Identity;

    private Affine2D EffectiveLocalAt(uint i)
    {
        Affine2D local = _table.LocalMatrixAt(i);
        int slot = i < (uint)_slotOf.Length ? _slotOf[i] : SlotTable.IdentitySlot;
        if (slot == SlotTable.IdentitySlot) return local;
        SlotTable? slots = Slots;
        if (slots == null)
        {
            UiAssert.That(false, "节点绑了槽但命中器没有槽矩阵源（Slots 未装）——槽语义不猜");
            return local;
        }
        return Affine2D.Compose(local, slots.Matrix(slot));
    }

    // ── 命中 ────────────────────────────────────────────────────────────────

    /// <summary>对 stage 空间的一个点做一次命中。</summary>
    public HitResult Hit(Vector2 stagePoint)
    {
        _stagePoint = stagePoint;
        _top = 0;
        _childTop = 0;
        LastSteps = 0;
        HitTests++;

        uint root = _table.Root.Index;
        if (!TryEnter(root, stagePoint, out Vector2 rootLocal, out bool rootIn)) return default;
        PushFrame(root, rootLocal, rootIn);

        while (_top > 0)
        {
            int fi = _top - 1;
            if (_stack[fi].Cursor > _stack[fi].BlockStart)
            {
                int cp = _childScratch[--_stack[fi].Cursor];
                uint child = ChildAtPaint(cp);
                if (child == NodeTable.NoIndex) continue;
                if (TryEnter(child, _stack[fi].Local, out Vector2 childLocal, out bool childIn))
                    PushFrame(child, childLocal, childIn);
                continue;
            }

            // 孩子试尽：自身兜底（opaque ∧ 自身区域内）
            if (_stack[fi].SelfIn && _policy.OpaqueAt(_stack[fi].Index))
                return new HitResult(_table.HandleOf(_stack[fi].Index), _stack[fi].Local);

            PopFrame();
        }
        return default;
    }

    /// <summary>把 stage 点换算到某节点的局部空间（沿祖先链逐级求逆；不做任何命中判定）。</summary>
    public bool TryStageToLocal(NodeHandle node, Vector2 stagePoint, out Vector2 local)
    {
        local = default;
        if (!_table.TryResolve(node, out uint idx)) return false;

        // 先把祖先链攒起来（根在前），再自根向下逐级求逆——与命中下行同一条换算路径。
        int n = 0;
        for (uint cur = idx; cur != NodeTable.NoIndex; cur = _table.ParentIndex(cur)) n++;
        if (_chainScratch.Length < n) Array.Resize(ref _chainScratch, n);
        int w = n;
        for (uint cur = idx; cur != NodeTable.NoIndex; cur = _table.ParentIndex(cur)) _chainScratch[--w] = (int)cur;

        Vector2 p = stagePoint;
        for (int i = 0; i < n; i++)
        {
            if (!EffectiveLocalAt((uint)_chainScratch[i]).TryInvert(out Affine2D inv)) return false;
            p = inv.TransformPoint(p);
        }
        local = p;
        return true;
    }

    private uint ChildAtPaint(int paintPos)
    {
        ReadOnlySpan<int> order = _table.GetPaintOrder();
        if ((uint)paintPos >= (uint)order.Length) return NodeTable.NoIndex;
        uint idx = (uint)order[paintPos];
        if (!_table.IsIndexAlive(idx)) return NodeTable.NoIndex;
        // 上次收敛之后被回收再分配的槽：paintIndex 反查对不上 ⇒ 这个位置上的节点已经不是它了。
        if (_table.PaintIndexAt(idx) != (uint)paintPos) return NodeTable.NoIndex;
        return idx;
    }

    /// <summary>进入一个节点：位门 → 求逆 → clip 剪枝 → hitMode 门。false = 整支不下行。</summary>
    private bool TryEnter(uint idx, Vector2 parentLocal, out Vector2 local, out bool selfIn)
    {
        local = default;
        selfIn = false;
        LastSteps++;

        if (!_table.IsIndexAlive(idx)) return false;

        uint lv = _table.LocalVisualAt(idx);
        if ((lv & Visual.Visible) == 0 || (lv & Visual.Touchable) == 0) return false;

        HitMode mode = _policy.ModeAt(idx);
        if (mode == HitMode.None) return false;

        if (!EffectiveLocalAt(idx).TryInvert(out Affine2D inv)) return false;   // 退化变换（scale 0）不可命中
        local = inv.TransformPoint(parentLocal);

        if (!PassesClip(idx)) return false;

        _table.ResolvedAt(idx, out _, out _, out float w, out float h);
        var content = new Rect(0f, 0f, w, h);
        switch (mode)
        {
            case HitMode.Rect:
                selfIn = w != 0f && h != 0f && content.Contains(local);
                return true;                        // Rect 不是门：容器框外的孩子照样可命中（fork 同）
            case HitMode.Shape:
                selfIn = _policy.ShapeAt(idx).Contains(local);
                return selfIn;                      // 形状域是门
            case HitMode.PixelTest:
                selfIn = PixelHitTest.Hit(_policy.MaskAt(idx), in content, local);
                return selfIn;                      // 位图是门：透明洞里的孩子也点不着
            case HitMode.Custom:
                IHitArea? area = _policy.CustomAt(idx);
                selfIn = area != null && area.HitTest(in content, local);
                return selfIn;
            default:
                UiAssert.That(false, $"未知 hitMode {mode}");
                return false;
        }
    }

    /// <summary>clip 剪枝：点不在本节点所属裁剪窗口内 ⇒ 整支不下行（与渲染的域外剔除同源）。</summary>
    private bool PassesClip(uint idx)
    {
        IHitClipSource? src = ClipSource;
        if (src == null) return true;
        int entry = src.ClipEntryOf(idx);
        if (entry == ClipBook.NoneEntry) return true;
        if (!src.TryGetClipWindow(entry, out Vector4 rect, out Affine2D frame)) return true;

        Vector2 p = _stagePoint;
        if (!IsIdentity(in frame))
        {
            if (!frame.TryInvert(out Affine2D inv)) return true;   // 退化槽：不裁（宁可多命中，不静默吞点击）
            p = inv.TransformPoint(p);
        }
        return p.x >= rect.x && p.x < rect.z && p.y >= rect.y && p.y < rect.w;
    }

    private static bool IsIdentity(in Affine2D m) =>
        m.m00 == 1f && m.m11 == 1f && m.m01 == 0f && m.m10 == 0f && m.tx == 0f && m.ty == 0f;

    private void PushFrame(uint idx, Vector2 local, bool selfIn)
    {
        if (_top == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
        int blockStart = _childTop;
        int count = _policy.TouchChildrenAt(idx) ? CollectChildren(idx) : 0;
        _stack[_top++] = new Frame
        {
            Index = idx,
            Local = local,
            BlockStart = blockStart,
            Cursor = blockStart + count,
            SelfIn = selfIn,
        };
    }

    private void PopFrame()
    {
        _top--;
        _childTop = _stack[_top].BlockStart;    // 子块随帧出栈（scratch 就是一条栈）
    }

    /// <summary>
    /// 把一个节点在**上帧 paintOrder** 里的孩子（各自的子树起点）压进 scratch 块，
    /// 顺序 = 子序；命中按逆序取（后画的先吃点击）。
    /// </summary>
    private int CollectChildren(uint idx)
    {
        uint pi = _table.PaintIndexAt(idx);
        if (pi == NodeTable.NotInTree) return 0;
        uint pe = _table.PaintEndAt(idx);
        ReadOnlySpan<int> order = _table.GetPaintOrder();
        if (pe > (uint)order.Length) pe = (uint)order.Length;

        int count = 0;
        for (uint k = pi + 1; k < pe; )
        {
            uint c = (uint)order[(int)k];
            uint ce = k + 1;
            if (_table.IsIndexAlive(c) && _table.PaintIndexAt(c) == k)
            {
                uint end = _table.PaintEndAt(c);
                if (end > k + 1 && end <= pe) ce = end;
                if (_childTop == _childScratch.Length) Array.Resize(ref _childScratch, _childScratch.Length * 2);
                _childScratch[_childTop++] = (int)k;
                count++;
            }
            k = ce;
        }
        return count;
    }
}
