namespace FairyNext.Core.Events;

/// <summary>
/// 监听器存储（架构平面四 B「监听器存储」）：**SoA 侧表 + chunk 池化块**。
///
/// 三条形态各对着一条成本：
///  1. <c>listenerHead</c> 与节点数组平行，<c>-1 = 无监听器</c>——多数节点只付 4B，
///     链收集的第一级剪枝是一次数组读（O(1)），不是字典查找；
///  2. 每块一个 <c>builtinMask</c>（内建事件 id &lt; 64 的位图）——第二级剪枝：
///     「这个节点有监听器，但没有**这个**事件的」在一次 <c>&amp;</c> 里判掉；
///  3. 块池化：节点销毁归还整块（<see cref="NodeTable.NodeDisposedHook"/> 独占接管），
///     稳态零分配。
///
/// EventBridge 整层不存在——它因双树而生。单树后冒泡链就是纯节点句柄序列，
/// 链收集从「每节点两次字典查找」降为一次数组读。
///
/// 剪枝纪律（事件·不变量 9）：**多算允许、漏算是 bug**。
/// <see cref="PruneMatchesFullScan"/> 是它的可执行神谕：凭 head/mask 跳过的节点全量对照必真无监听。
/// </summary>
public sealed class ListenerTable
{
    /// <summary>「无块」哨兵。</summary>
    public const int NoBlock = -1;

    private struct Block
    {
        internal ulong BuiltinMask;   // 内建 id 位图（二级剪枝）
        internal int Count;
        internal Entry[] Entries;
        internal int NextFree;        // 空闲链（回收后串起来）
        internal uint Owner;          // 节点下标（回收核对；0 = 未占用）
        internal bool Live;
    }

    private struct Entry
    {
        internal int EventId;
        internal ListenPhase Phase;
        internal Delegate Fn;
    }

    private readonly NodeTable _table;
    private int[] _head = Array.Empty<int>();
    private Block[] _blocks = Array.Empty<Block>();
    private int _blockCount;
    private int _freeHead = NoBlock;
    private int _liveBlocks;
    private int _entryCount;

    /// <summary>
    /// 接一棵树：独占 <see cref="NodeTable.NodeDisposedHook"/>（第二家接管即抛——
    /// 两个归还者会让同一块被回收两次，池链成环）。
    /// </summary>
    public ListenerTable(NodeTable table)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        if (table.NodeDisposedHook != null)
            throw new InvalidOperationException("本树域已有监听表接管 NodeDisposedHook（归还权是独占的）");
        table.NodeDisposedHook = OnNodeDisposed;
    }

    /// <summary>卸下钩子（测试拆装）。</summary>
    public void Detach()
    {
        if (_table.NodeDisposedHook == OnNodeDisposed) _table.NodeDisposedHook = null;
    }

    /// <summary>被接管的树。</summary>
    public NodeTable Table => _table;

    /// <summary>在册监听器总数（诊断：<c>EventStats.listenerCount</c>）。</summary>
    public int ListenerCount => _entryCount;

    /// <summary>已占用的块数（销毁后应回落——池不涨即归还生效）。</summary>
    public int LiveBlocks => _liveBlocks;

    /// <summary>池里的块总数（含空闲；稳态不再增长）。</summary>
    public int PooledBlocks => _blockCount;

    // ── 注册面 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 注册一条监听器。<paramref name="id"/> 的泛型参数决定 <paramref name="fn"/> 的载荷类型——
    /// 载荷错配在**编译期**就过不去（事件·不变量 1）。
    /// </summary>
    public void Add<T>(NodeHandle node, EventId<T> id, EventFn<T> fn, ListenPhase phase = ListenPhase.Bubble)
    {
        if (fn == null) { UiAssert.That(false, "AddListener 收到 null 回调"); return; }
        if (id.IsNone) { UiAssert.That(false, "AddListener 收到空 EventId"); return; }
        if (!_table.TryResolve(node, out uint idx))
        {
            UiAssert.That(false, "AddListener 于已失效句柄（gen 不符或已 DEAD）");
            return;
        }

        EnsureHead(idx);
        int b = _head[idx];
        if (b == NoBlock)
        {
            b = AllocBlock(idx);
            _head[idx] = b;
        }

        ref Block block = ref _blocks[b];
        if (block.Count == block.Entries.Length) Array.Resize(ref block.Entries, block.Entries.Length * 2);
        block.Entries[block.Count++] = new Entry { EventId = id.Raw, Phase = phase, Fn = fn };
        if ((uint)id.Raw < (uint)UiEvents.BuiltinLimit) block.BuiltinMask |= 1UL << id.Raw;
        _entryCount++;
    }

    /// <summary>注销一条监听器（同 id + 同相 + 同委托实例才算同一条）。返回是否真摘掉了一条。</summary>
    public bool Remove<T>(NodeHandle node, EventId<T> id, EventFn<T> fn, ListenPhase phase = ListenPhase.Bubble)
    {
        if (!_table.TryResolve(node, out uint idx)) return false;
        int b = BlockOf(idx);
        if (b == NoBlock) return false;

        ref Block block = ref _blocks[b];
        for (int i = 0; i < block.Count; i++)
        {
            ref Entry e = ref block.Entries[i];
            if (e.EventId != id.Raw || e.Phase != phase || !ReferenceEquals(e.Fn, fn)) continue;
            for (int k = i + 1; k < block.Count; k++) block.Entries[k - 1] = block.Entries[k];
            block.Entries[--block.Count] = default;
            _entryCount--;
            RecomputeMask(b);
            if (block.Count == 0) FreeBlock(idx, b);
            return true;
        }
        return false;
    }

    /// <summary>摘掉一个节点的全部监听器并归还块（显式版；销毁走 <see cref="OnNodeDisposed"/>）。</summary>
    public void RemoveAll(NodeHandle node)
    {
        if (!_table.TryResolve(node, out uint idx)) return;
        ReleaseAt(idx);
    }

    /// <summary>某节点在册的监听器条数。</summary>
    public int CountOf(NodeHandle node)
    {
        if (!_table.TryResolve(node, out uint idx)) return 0;
        int b = BlockOf(idx);
        return b == NoBlock ? 0 : _blocks[b].Count;
    }

    // ── 剪枝面（链收集的两级判据）──────────────────────────────────────────

    /// <summary>节点的块下标（<see cref="NoBlock"/> = 无监听器，一级剪枝）。</summary>
    internal int BlockOf(uint index) =>
        index < (uint)_head.Length ? _head[index] : NoBlock;

    /// <summary>块里是否可能有该事件的监听器（内建走 mask 位，用户事件不参与位图故一律进）。</summary>
    internal bool BlockMayHave(int block, int eventId)
    {
        if (block == NoBlock) return false;
        if ((uint)eventId >= (uint)UiEvents.BuiltinLimit) return true;
        return (_blocks[block].BuiltinMask & (1UL << eventId)) != 0;
    }

    /// <summary>「这个节点该不该进链」的合成判据（两级剪枝合一）。</summary>
    internal bool MayReceive(uint index, int eventId) => BlockMayHave(BlockOf(index), eventId);

    /// <summary>块内条数（派发循环每步重读——回调里增删监听器不会读越界）。</summary>
    internal int EntryCountOf(int block) =>
        block == NoBlock || !_blocks[block].Live ? 0 : _blocks[block].Count;

    /// <summary>读一条（越界返回 false）。</summary>
    internal bool TryEntry(int block, int i, out int eventId, out ListenPhase phase, out Delegate fn)
    {
        eventId = -1; phase = ListenPhase.Bubble; fn = null!;
        if (block == NoBlock || !_blocks[block].Live) return false;
        ref Block b = ref _blocks[block];
        if ((uint)i >= (uint)b.Count) return false;
        eventId = b.Entries[i].EventId;
        phase = b.Entries[i].Phase;
        fn = b.Entries[i].Fn;
        return true;
    }

    /// <summary>
    /// 剪枝无漏的全量神谕（事件·不变量 9）：对全表逐节点比对
    /// 「按 head/mask 判断的可收性」与「逐条扫描的真实可收性」。
    /// 多算允许（返回 true），漏算即 false 并指认节点。
    /// </summary>
    public bool PruneMatchesFullScan(int eventId, out uint badIndex)
    {
        badIndex = NodeTable.NoIndex;
        for (uint i = 1; i < (uint)_table.Capacity; i++)
        {
            if (!_table.IsIndexAlive(i)) continue;
            bool pruneSaysMaybe = MayReceive(i, eventId);
            bool reallyHas = false;
            int b = BlockOf(i);
            if (b != NoBlock)
            {
                ref Block block = ref _blocks[b];
                for (int k = 0; k < block.Count; k++)
                    if (block.Entries[k].EventId == eventId) { reallyHas = true; break; }
            }
            if (reallyHas && !pruneSaysMaybe) { badIndex = i; return false; }
        }
        return true;
    }

    // ── 块池 ────────────────────────────────────────────────────────────────

    private void EnsureHead(uint index)
    {
        if (index < (uint)_head.Length) return;
        int cap = _head.Length == 0 ? 64 : _head.Length;
        while (cap <= index) cap *= 2;
        int old = _head.Length;
        Array.Resize(ref _head, cap);
        for (int i = old; i < cap; i++) _head[i] = NoBlock;
    }

    private int AllocBlock(uint owner)
    {
        int b;
        if (_freeHead != NoBlock)
        {
            b = _freeHead;
            _freeHead = _blocks[b].NextFree;
        }
        else
        {
            if (_blockCount == _blocks.Length)
                Array.Resize(ref _blocks, _blocks.Length == 0 ? 8 : _blocks.Length * 2);
            b = _blockCount++;
            _blocks[b].Entries = new Entry[4];
        }
        ref Block block = ref _blocks[b];
        block.BuiltinMask = 0;
        block.Count = 0;
        block.NextFree = NoBlock;
        block.Owner = owner;
        block.Live = true;
        _liveBlocks++;
        return b;
    }

    private void FreeBlock(uint owner, int b)
    {
        ref Block block = ref _blocks[b];
        UiAssert.That(block.Owner == owner, "归还监听块时属主不符（池链要串错了）");
        for (int i = 0; i < block.Count; i++) block.Entries[i] = default;   // 委托引用不留在池里
        block.Count = 0;
        block.BuiltinMask = 0;
        block.Owner = 0;
        block.Live = false;
        block.NextFree = _freeHead;
        _freeHead = b;
        _liveBlocks--;
        if (owner < (uint)_head.Length) _head[owner] = NoBlock;
    }

    private void RecomputeMask(int b)
    {
        ref Block block = ref _blocks[b];
        ulong mask = 0;
        for (int i = 0; i < block.Count; i++)
        {
            int id = block.Entries[i].EventId;
            if ((uint)id < (uint)UiEvents.BuiltinLimit) mask |= 1UL << id;
        }
        block.BuiltinMask = mask;
    }

    private void ReleaseAt(uint index)
    {
        int b = BlockOf(index);
        if (b == NoBlock) return;
        _entryCount -= _blocks[b].Count;
        FreeBlock(index, b);
    }

    /// <summary>
    /// 节点销毁（P9 统一换代那一刻）：归还整块。
    /// 走的是**下标**而不是句柄——此刻节点已 DEAD，句柄解引用必失败。
    /// </summary>
    private void OnNodeDisposed(uint index) => ReleaseAt(index);
}
