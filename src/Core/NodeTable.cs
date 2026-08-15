using FairyNext.Numerics;

namespace FairyNext.Core;

/// <summary>
/// slab 槽生命周期位。**刻意不与 localVisual 同列**：机制⑦ 的实例化是「NODE 段每列一次
/// Array.Copy」，任何塞进 localVisual 保留段的生命周期位都会被模板初值 memcpy 覆写，
/// 表现为「已死节点静默复活、TryResolve 放行僵尸」。多 1B/节点买断这一类。
/// </summary>
[Flags]
internal enum SlotFlags : byte
{
    None = 0,
    /// <summary>未分配 或 已 Destroy——两者对 TryResolve 等价（解引用必失败）。</summary>
    Dead = 1 << 0,
    /// <summary>槽由 <see cref="NodeTable.CreateNode"/> 单独分配：P9 单槽回 slab。段内节点无此位（整段回收）。</summary>
    OwnSlot = 1 << 1,
}

/// <summary>
/// 平面一 · 数据宪法的唯一实现：一棵树域的 SoA 节点表。
/// 依据 docs/architecture.md「平面一 · 数据宪法」核心数据结构 + 机制①②③⑤⑨⑪⑬ + 不变量 1/5/6/9/10/12/13/15。
/// 无 fork 移植成分——fork 的 GObject/DisplayObject 双树在本设计中不存在（机制①）。
///
/// 三条语义写在最前面，其余文档散在各方法头：
///  1. **逻辑真值同步、派生数据延迟**：AddChild/SetPosition 立即改真值列，同相位读即见（不变量 13）；
///     world/worldVisual/paintIndex 只在 P6 写（不变量 6），P0–P5 读到的是上帧收敛值。
///  2. **句柄纪律**：解引用一律 <see cref="TryResolve"/>，gen 相等 **∧** DEAD 未置才成功（不变量 1）。
///     Destroy = 摘链 + 置 DEAD（句柄立刻死），gen++ 统一在 <see cref="EndFrame"/>（P9）。
///  3. **等值切断**：所有 authored 写口写前走 <see cref="BitEquals"/>，值不变则不写、不 Mark（裁决 13）。
///
/// 本包（M1-06）**不解释 dirtyWord 位含义**：Ch 枚举、队列、方向语义全归失效平面（M1-07），
/// 树只提供存储列与 <see cref="InvalidationHook"/> 单一接线点。
/// </summary>
public sealed partial class NodeTable
{
    /// <summary>拓扑列的空值哨兵 = 槽 0。槽 0 永不分配，因而 0 既是「无」也永远解引用失败。</summary>
    public const uint NoIndex = 0;

    /// <summary><see cref="CreateNode"/> 使用的单槽模板 id（slab bin 0）。</summary>
    public const uint SingleSlotTemplate = 0;

    // ── 真值列（写即生效；写者 = 分流入口，机制⑨）─────────────────────────── 80B/节点
    private uint[] _parent = Array.Empty<uint>();
    private uint[] _firstChild = Array.Empty<uint>();
    private uint[] _nextSib = Array.Empty<uint>();
    private uint[] _prevSib = Array.Empty<uint>();
    private uint[] _ownerInst = Array.Empty<uint>();
    private ushort[] _localId = Array.Empty<ushort>();
    private ushort[] _typeId = Array.Empty<ushort>();
    private float[] _posX = Array.Empty<float>();
    private float[] _posY = Array.Empty<float>();
    private float[] _width = Array.Empty<float>();
    private float[] _height = Array.Empty<float>();
    private float[] _scaleX = Array.Empty<float>();
    private float[] _scaleY = Array.Empty<float>();
    private float[] _rotation = Array.Empty<float>();
    private float[] _skew = Array.Empty<float>();
    private float[] _pivotX = Array.Empty<float>();
    private float[] _pivotY = Array.Empty<float>();
    private uint[] _localVisual = Array.Empty<uint>();
    private uint[] _contentRef = Array.Empty<uint>();
    private uint[] _stateRef = Array.Empty<uint>();
    private uint[] _resolvedRef = Array.Empty<uint>();

    // ── 派生/簿记列（写者 = 法定相位，不变量 6）───────────────────────────── 38B/节点
    private Affine2D[] _world = Array.Empty<Affine2D>();
    private uint[] _worldVisual = Array.Empty<uint>();
    private uint[] _paintIndex = Array.Empty<uint>();
    private uint[] _dirtyWord = Array.Empty<uint>();      // 仅存储：位语义/队列/方向归失效平面
    private uint[] _subtreeStamp = Array.Empty<uint>();   // 仅存储：向下通道子树戳（平面二接缝）
    private ushort[] _gen = Array.Empty<ushort>();        // 仅 P9 递增

    // ── 槽簿记（不进 NODE 段 memcpy，见 SlotFlags 文件头）────────────────────
    private SlotFlags[] _slot = Array.Empty<SlotFlags>();

    private int _capacity;
    private int _liveCount;

    // 帧末待处理集（P9 统一换代 / 回 slab）
    private uint[] _pending = Array.Empty<uint>();
    private int _pendingCount;
    private NodeSegment[] _pendingSegs = Array.Empty<NodeSegment>();
    private int _pendingSegCount;

    private readonly Slab _slab;

    /// <summary>树域 id（多 stage 各一棵树，句柄自含域——跨树句柄解引用必失败）。</summary>
    public ushort Tree { get; }

    /// <summary>树根（构造时创建，typeId=Component，localId=0；不可 Destroy）。</summary>
    public NodeHandle Root { get; }

    /// <summary>
    /// 当前法定相位。**接了内核的树域由 <see cref="UiKernel"/> 独占驱动**（M1-08）；
    /// 未接内核的树（单测/离线工具）沿用手写 setter，默认 P3（帧外 = 用户代码自由写状态的窗口）。
    /// 所有相位门（不变量 2/3/6/9）读本值。
    /// </summary>
    public FramePhase Phase
    {
        get => _phase;
        internal set
        {
            // 两个驱动者 = 写门可被绕过（把相位调回 P3 就能在 P7 里随便写），因此接了内核就不许旁路设相位。
            UiAssert.That(PhaseDriver == null,
                "本树域已接 UiKernel：相位只能由内核驱动（旁路设相位会绕过 P7/P8 写门）");
            // 断言之外**真拒绝**：release 下断言整体消失，若还照写，写门就成了纸面规则。
            if (PhaseDriver != null) return;
            _phase = value;
        }
    }

    private FramePhase _phase = FramePhase.P3_Commands;

    /// <summary>相位独占驱动者（<see cref="UiKernel"/> 构造时登记；null = 无内核）。</summary>
    internal object? PhaseDriver { get; set; }

    /// <summary>驱动者专用的相位写口（校验调用方就是登记过的驱动者）。</summary>
    internal void SetPhase(FramePhase phase, object driver)
    {
        UiAssert.That(ReferenceEquals(driver, PhaseDriver), "非驱动者改相位");
        _phase = phase;
    }

    /// <summary>
    /// 结构世代（机制⑤）：任何改变链式拓扑的编辑 +1。
    /// 契约方向是单向的——**structEpoch 未变 ⇒ paintOrder 数组 bit-identical**（不变量 5）；
    /// 反过来不成立（加了又删的抵消编辑会 +2 而数组不变，这是允许的保守侧）。
    /// P8 的 sortingOrder 只在本值变化时重推。
    /// </summary>
    public uint StructEpoch => _structEpoch;
    private uint _structEpoch;

    /// <summary>
    /// 表世代：**列数组搬家**（2 倍扩容）时 +1。不变量 12 的可执行版本——
    /// <see cref="World"/> 一类 ref 返回值只在本值不变期间有效，持有方按此自检。
    /// </summary>
    public uint TableEpoch { get; private set; }

    /// <summary>活节点数（含根）。诊断面用；不参与任何算法。</summary>
    public int LiveCount => _liveCount;

    /// <summary>当前列容量（= 每列数组长度，含保留槽 0）。</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// 失效协议的唯一接线点（M1-07 接管）。树在**写值成功后**调一次，不解释 <see cref="Ch"/> 位含义、
    /// 不区分上行/下行——方向语义归失效平面。等值切断已在调用前执行：**本钩子被调 ⇔ 值真的变了**。
    /// </summary>
    internal MarkHandler? InvalidationHook { get; set; }

    /// <summary>失效钩子签名。index 是表内下标（失效平面按下标存队列，句柄化由它自己决定）。</summary>
    /// <param name="table">发生写入的树。</param>
    /// <param name="index">节点下标。</param>
    /// <param name="channel">受影响通道（可多位；上行/下行的拆分归失效平面）。</param>
    /// <param name="source">写来源（裁决 1/4：系统写不经用户入口）。</param>
    internal delegate void MarkHandler(NodeTable table, uint index, Ch channel, WriteSource source);

    /// <summary>建表并创建根节点。</summary>
    /// <param name="tree">树域 id。</param>
    /// <param name="initialCapacity">初始槽数（含保留槽 0）；之后按 2 倍扩容。</param>
    public NodeTable(ushort tree = 0, int initialCapacity = 64)
    {
        Tree = tree;
        EnsureCapacity(initialCapacity < 2 ? 2 : initialCapacity);
        _slab = new Slab(this);
        Root = CreateNode(NodeType.Component);
        _structEpoch = 1;
        RebuildPaintOrderCore();
    }

    // ────────────────────────────────────────────────────────────────────────
    // 句柄纪律（不变量 1）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 解引用（全组唯一入口）：树域相符 ∧ 下标在界 ∧ **gen 相等** ∧ **DEAD 未置** 四条全中才成功。
    /// 只验代际是漏洞——同帧 Destroy 的节点 gen 还没换（P9 才换），只验 gen 会放行僵尸。
    /// </summary>
    public bool TryResolve(NodeHandle h, out uint index)
    {
        uint i = h.Index;
        if (h.Tree == Tree && i != NoIndex && i < (uint)_capacity
            && _gen[i] == h.Gen && (_slot[i] & SlotFlags.Dead) == 0)
        {
            index = i;
            return true;
        }
        index = NoIndex;
        return false;
    }

    /// <summary>句柄是否指向活节点（= <see cref="TryResolve"/> 成功）。Destroy 后立刻为 false。</summary>
    public bool IsAlive(NodeHandle h) => TryResolve(h, out _);

    /// <summary>下标 → 句柄。已死/越界槽返回 <see cref="NodeHandle.None"/>。</summary>
    public NodeHandle HandleOf(uint index) =>
        index != NoIndex && index < (uint)_capacity && (_slot[index] & SlotFlags.Dead) == 0
            ? new NodeHandle(index, _gen[index], Tree)
            : NodeHandle.None;

    /// <summary>诊断读：槽的代际号（不校验存活）。测试用于钉「P0–P8 内 gen 不变」。</summary>
    public ushort GenerationOf(uint index) => index < (uint)_capacity ? _gen[index] : (ushort)0;

    // ────────────────────────────────────────────────────────────────────────
    // 生命周期
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 建单个节点（槽从 slab 的单槽 bin 取，P9 单独回收）。
    /// 组件实例化走 <see cref="AllocSegment"/>（机制⑦），不走这里。
    /// </summary>
    public NodeHandle CreateNode(ushort typeId = NodeType.Component, ushort localId = 0, uint ownerInst = 0)
    {
        uint idx = _slab.AllocSingle();
        InitSlot(idx, typeId, localId, ownerInst);
        _slot[idx] |= SlotFlags.OwnSlot;
        return new NodeHandle(idx, _gen[idx], Tree);
    }

    /// <summary>
    /// 标记即死（机制②）：链上摘除 + **整棵子树**置 DEAD，句柄立刻 <c>IsAlive==false</c>；
    /// gen++ 与回 slab 统一推迟到 <see cref="EndFrame"/>（P9）。
    /// 推迟换代正是同帧安全性的来源：事件回调里销毁节点后，同帧尚在进行的事件链与迭代器
    /// 解引用一律因 DEAD 失败（可辨认、不可复活），不需要任何「延迟销毁列表」。
    /// </summary>
    public void Destroy(NodeHandle h, WriteSource src = WriteSource.User)
    {
        if (!TryResolve(h, out uint idx))
        {
            UiAssert.That(false, "Destroy 于已失效句柄（gen 不符或已 DEAD）");
            return;
        }
        UiAssert.That(idx != Root.Index, "树根不可 Destroy（树域的生命周期归宿主）");
        if (idx == Root.Index) return;

        uint parent = DetachCore(idx);
        if (parent != NoIndex)
        {
            _structEpoch++;                 // 摘链改变了树形，paintOrder 必须重展
            Mark(parent, Ch.Structure, src);
        }
        MarkSubtreeDead(idx);
    }

    /// <summary>
    /// 整段销毁（组件实例回收路径）：段内全部节点置 DEAD，段在 P9 整体回 slab。
    /// M1-22 的 Pool.Return 走这条；单节点 Destroy 不会把段还回去（段是分配单位）。
    /// 挂在段节点下的**段外**子树同样被标记即死——否则它们的 parent 指针会悬在
    /// 已回收的槽上，等槽被复用时变成跨实例的假父子关系。
    /// </summary>
    public void DestroySegment(in NodeSegment seg, WriteSource src = WriteSource.User)
    {
        UiAssert.That(seg.Count > 0 && seg.Base != NoIndex, "DestroySegment 收到空段");
        for (int k = 0; k < seg.Count; k++)
        {
            uint idx = seg.Base + (uint)k;
            if ((_slot[idx] & SlotFlags.Dead) != 0) continue;   // 已被同段祖先的子树扫掉
            uint parent = DetachCore(idx);
            if (parent != NoIndex)
            {
                _structEpoch++;
                Mark(parent, Ch.Structure, src);
            }
            MarkSubtreeDead(idx);
        }
        PushPendingSegment(seg);
    }

    /// <summary>
    /// P9 帧尾钩子（机制②/不变量 1）：**句柄统一换代**——本帧被标记即死的节点此刻 gen++，
    /// 节点段回 slab、槽内容清零。相位机接线在 M1-08；本包供宿主/测试直调。
    /// 换代跳过 0：gen==0 的槽会与 <see cref="NodeHandle.None"/> 的形状撞车。
    /// </summary>
    public void EndFrame()
    {
        for (int k = 0; k < _pendingCount; k++)
        {
            uint idx = _pending[k];
            ushort g = (ushort)(_gen[idx] + 1);
            if (g == 0) g = 1;
            _gen[idx] = g;

            bool own = (_slot[idx] & SlotFlags.OwnSlot) != 0;
            ClearSlot(idx);
            _slot[idx] = SlotFlags.Dead;
            if (own) _slab.FreeSingle(idx);
        }
        _pendingCount = 0;

        for (int k = 0; k < _pendingSegCount; k++)
        {
            _slab.FreeSegment(_pendingSegs[k]);
            _pendingSegs[k] = default;
        }
        _pendingSegCount = 0;
    }

    private void MarkSubtreeDead(uint root)
    {
        for (uint cur = root; cur != NoIndex; cur = NextPreorder(cur, root))
            MarkDeadOne(cur);
    }

    private void MarkDeadOne(uint idx)
    {
        if ((_slot[idx] & SlotFlags.Dead) != 0) return;
        _slot[idx] |= SlotFlags.Dead;
        _liveCount--;
        if (_pendingCount == _pending.Length)
            Array.Resize(ref _pending, _pending.Length == 0 ? 16 : _pending.Length * 2);
        _pending[_pendingCount++] = idx;
    }

    private void PushPendingSegment(in NodeSegment seg)
    {
        if (_pendingSegCount == _pendingSegs.Length)
            Array.Resize(ref _pendingSegs, _pendingSegs.Length == 0 ? 8 : _pendingSegs.Length * 2);
        _pendingSegs[_pendingSegCount++] = seg;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 容量：2 倍扩容，句柄 = 下标，扩容不移动语义（列数组本身搬家 → TableEpoch++）
    // ────────────────────────────────────────────────────────────────────────

    private void EnsureCapacity(int needed)
    {
        if (needed <= _capacity) return;
        int cap = _capacity == 0 ? 8 : _capacity;
        while (cap < needed) cap *= 2;
        int old = _capacity;

        Array.Resize(ref _parent, cap);
        Array.Resize(ref _firstChild, cap);
        Array.Resize(ref _nextSib, cap);
        Array.Resize(ref _prevSib, cap);
        Array.Resize(ref _ownerInst, cap);
        Array.Resize(ref _localId, cap);
        Array.Resize(ref _typeId, cap);
        Array.Resize(ref _posX, cap);
        Array.Resize(ref _posY, cap);
        Array.Resize(ref _width, cap);
        Array.Resize(ref _height, cap);
        Array.Resize(ref _scaleX, cap);
        Array.Resize(ref _scaleY, cap);
        Array.Resize(ref _rotation, cap);
        Array.Resize(ref _skew, cap);
        Array.Resize(ref _pivotX, cap);
        Array.Resize(ref _pivotY, cap);
        Array.Resize(ref _localVisual, cap);
        Array.Resize(ref _contentRef, cap);
        Array.Resize(ref _stateRef, cap);
        Array.Resize(ref _resolvedRef, cap);
        Array.Resize(ref _world, cap);
        Array.Resize(ref _worldVisual, cap);
        Array.Resize(ref _paintIndex, cap);
        Array.Resize(ref _dirtyWord, cap);
        Array.Resize(ref _subtreeStamp, cap);
        Array.Resize(ref _gen, cap);
        Array.Resize(ref _slot, cap);

        for (int i = old; i < cap; i++)
        {
            // 新槽必须自带 DEAD：否则手工构造的 (idx, gen=0) 句柄会在未分配槽上解引用成功。
            _slot[i] = SlotFlags.Dead;
            _gen[i] = 1;
            _paintIndex[i] = NotInTree;
        }

        _capacity = cap;
        TableEpoch++;
    }

    /// <summary>把槽初始化成一个活节点（新建与段分配共用）。</summary>
    private void InitSlot(uint idx, ushort typeId, ushort localId, uint ownerInst)
    {
        ClearSlot(idx);
        _typeId[idx] = typeId;
        _localId[idx] = localId;
        _ownerInst[idx] = ownerInst;
        _scaleX[idx] = 1f;
        _scaleY[idx] = 1f;
        _localVisual[idx] = Visual.DefaultLocal;
        _worldVisual[idx] = Visual.DefaultLocal;
        _world[idx] = Affine2D.Identity;
        _slot[idx] = SlotFlags.None;   // 活
        _liveCount++;
    }

    /// <summary>清空一个槽的全部列（回收时执行：陈旧数据不许活到下一次分配）。</summary>
    private void ClearSlot(uint idx)
    {
        _parent[idx] = 0; _firstChild[idx] = 0; _nextSib[idx] = 0; _prevSib[idx] = 0;
        _ownerInst[idx] = 0; _localId[idx] = 0; _typeId[idx] = NodeType.None;
        _posX[idx] = 0f; _posY[idx] = 0f; _width[idx] = 0f; _height[idx] = 0f;
        _scaleX[idx] = 0f; _scaleY[idx] = 0f; _rotation[idx] = 0f; _skew[idx] = 0f;
        _pivotX[idx] = 0f; _pivotY[idx] = 0f;
        _localVisual[idx] = 0; _contentRef[idx] = 0; _stateRef[idx] = 0; _resolvedRef[idx] = 0;
        _world[idx] = default; _worldVisual[idx] = 0; _paintIndex[idx] = NotInTree;
        _dirtyWord[idx] = 0; _subtreeStamp[idx] = 0;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 失效平面接缝：树只持存储列并提供 O(1) 位存取，不解释位含义
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>读脏字（失效平面 M1-07 的存储列）。</summary>
    internal uint GetDirty(uint index) => _dirtyWord[index];

    /// <summary>置位（Mark = 原子 OR，已置位即在队——去重语义归失效平面）。</summary>
    internal void OrDirty(uint index, uint bits) => _dirtyWord[index] |= bits;

    /// <summary>清位（消费即清）。</summary>
    internal void ClearDirty(uint index, uint bits) => _dirtyWord[index] &= ~bits;

    /// <summary>读向下通道子树戳。</summary>
    internal uint GetSubtreeStamp(uint index) => _subtreeStamp[index];

    /// <summary>写向下通道子树戳（P6 只下钻 stamp==frameId 的子树）。</summary>
    internal void SetSubtreeStamp(uint index, uint frameId) => _subtreeStamp[index] = frameId;

    // ── 下标级拓扑读（失效平面的父链置位 / 子树下钻按下标走，句柄化每步都做太贵）──
    // 架构文档「平面二 ↔ 节点核心」接缝：本平面依赖 NodeTable.Parent/IsAlive 做父链置位与代际校验。
    // 这些是同一批读的 O(1) 下标版，**只读**：树的写口仍然只有 authored/结构那几个。

    /// <summary>父下标（无父 = <see cref="NoIndex"/>）。</summary>
    internal uint ParentIndex(uint index) => _parent[index];

    /// <summary>首子下标（无子 = <see cref="NoIndex"/>）。</summary>
    internal uint FirstChildIndex(uint index) => _firstChild[index];

    /// <summary>末子下标（无子 = <see cref="NoIndex"/>）。环形兄弟链使之为 O(1)。</summary>
    internal uint LastChildIndex(uint index)
    {
        uint first = _firstChild[index];
        return first == NoIndex ? NoIndex : _prevSib[first];
    }

    /// <summary>前一个兄弟下标；已是首子（或无父）= <see cref="NoIndex"/>（环形表示不外泄）。</summary>
    internal uint PrevSiblingIndex(uint index)
    {
        uint p = _parent[index];
        if (p == NoIndex || _firstChild[p] == index) return NoIndex;
        return _prevSib[index];
    }

    /// <summary>局部可见位（下行通道「隐藏子树免下钻」的判据；读的是 localVisual 而非级联值）。</summary>
    internal bool IsIndexVisible(uint index) => (_localVisual[index] & Visual.Visible) != 0;

    /// <summary>下标是否指向活槽（下标路径的 DEAD 校验；句柄路径仍走 <see cref="TryResolve"/>）。</summary>
    internal bool IsIndexAlive(uint index) =>
        index != NoIndex && index < (uint)_capacity && (_slot[index] & SlotFlags.Dead) == 0;

    private void Mark(uint index, Ch channel, WriteSource source) =>
        InvalidationHook?.Invoke(this, index, channel, source);
}
