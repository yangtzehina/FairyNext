namespace FairyNext.Core;

public sealed partial class NodeTable
{
    // ────────────────────────────────────────────────────────────────────────
    // 链式拓扑真值（parent/firstChild/nextSib/prevSib，机制① / 不变量 10）
    //
    // 兄弟链是**环形双向链**：末子的 nextSib 指回首子，首子的 prevSib 指向末子。
    // 为什么不是 null 终止：数据宪法把拓扑钉死为 4 列 16B（没有 lastChild 列的预算），
    // 而 AddChild 追加必须 O(1)——环形是唯一能在 4 列内做到的形态。
    // 副作用是**不变量 10「prevSib/nextSib 互指」被加强而不是削弱**：环内每个节点
    // 都满足 next[prev[c]]==c ∧ prev[next[c]]==c，无端点例外。
    // 公共 API（NextSibling/PrevSibling）在端点返回 None，环形是纯内部表示。
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>父节点（无父返回 None）。</summary>
    public NodeHandle Parent(NodeHandle h) =>
        TryResolve(h, out uint i) ? HandleOf(_parent[i]) : NodeHandle.None;

    /// <summary>首子（无子返回 None）。</summary>
    public NodeHandle FirstChild(NodeHandle h) =>
        TryResolve(h, out uint i) ? HandleOf(_firstChild[i]) : NodeHandle.None;

    /// <summary>末子（无子返回 None）。环形链使之为 O(1)。</summary>
    public NodeHandle LastChild(NodeHandle h)
    {
        if (!TryResolve(h, out uint i)) return NodeHandle.None;
        uint first = _firstChild[i];
        return first == NoIndex ? NodeHandle.None : HandleOf(_prevSib[first]);
    }

    /// <summary>下一个兄弟；已是末子返回 None（环形表示不外泄）。</summary>
    public NodeHandle NextSibling(NodeHandle h)
    {
        if (!TryResolve(h, out uint i)) return NodeHandle.None;
        uint n = NextInRing(i);
        return n == NoIndex ? NodeHandle.None : HandleOf(n);
    }

    /// <summary>上一个兄弟；已是首子返回 None。</summary>
    public NodeHandle PrevSibling(NodeHandle h)
    {
        if (!TryResolve(h, out uint i)) return NodeHandle.None;
        uint p = _parent[i];
        if (p == NoIndex || _firstChild[p] == i) return NodeHandle.None;
        return HandleOf(_prevSib[i]);
    }

    /// <summary>子节点数（O(子数)，链式表示无计数列）。</summary>
    public int ChildCount(NodeHandle h)
    {
        if (!TryResolve(h, out uint i)) return 0;
        uint first = _firstChild[i];
        if (first == NoIndex) return 0;
        int n = 0;
        uint c = first;
        do { n++; c = _nextSib[c]; } while (c != first);
        return n;
    }

    /// <summary>第 index 个子节点（O(index)）。越界返回 None。</summary>
    public NodeHandle ChildAt(NodeHandle h, int index)
    {
        if (index < 0 || !TryResolve(h, out uint i)) return NodeHandle.None;
        uint c = ChildAtCore(i, index);
        return c == NoIndex ? NodeHandle.None : HandleOf(c);
    }

    /// <summary>子节点在父的子序中的下标；无父或不在链上返回 -1。</summary>
    public int IndexOfChild(NodeHandle h)
    {
        if (!TryResolve(h, out uint i)) return -1;
        uint p = _parent[i];
        if (p == NoIndex) return -1;
        uint first = _firstChild[p];
        int n = 0;
        for (uint c = first; ; c = _nextSib[c], n++)
        {
            if (c == i) return n;
            if (_nextSib[c] == first) return -1;
        }
    }

    /// <summary>
    /// 机制⑩ 的 O(1) 寻址：<c>nodeBase + localId</c>，零字符串零字典。
    /// nodeBase 由 owner 自身的 localId 反推（owner 落在自己的模板段内），
    /// 命中后校验 localId 与 ownerInst 相符——落空即模板段错位/跨实例块寻址，是编译器或装载 bug。
    /// </summary>
    public NodeHandle ChildByLocalId(NodeHandle owner, ushort localId)
    {
        if (!TryResolve(owner, out uint o))
        {
            UiAssert.That(false, "ChildByLocalId 于已失效句柄");
            return NodeHandle.None;
        }
        uint nodeBase = o - _localId[o];
        uint idx = nodeBase + localId;
        if (idx >= (uint)_capacity || (_slot[idx] & SlotFlags.Dead) != 0)
        {
            UiAssert.That(false, "localId 寻址越界或落在已死槽：localId=" + localId);
            return NodeHandle.None;
        }
        UiAssert.That(_localId[idx] == localId, "localId 寻址落空（模板段错位）：localId=" + localId);
        UiAssert.That(_ownerInst[idx] == _ownerInst[o], "localId 寻址跨实例块：localId=" + localId);
        return new NodeHandle(idx, _gen[idx], Tree);
    }

    // ── 结构编辑 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 追加为末子（O(1)）。child 若已有父，先自动摘除（与 fork 的 AddChild 语义一致）。
    /// 结构真值立即生效（不变量 13：同相位 GetChild 立即可见）；paintOrder 的重展留到 P6。
    /// </summary>
    public bool AddChild(NodeHandle parent, NodeHandle child, WriteSource src = WriteSource.User)
        => AddChildAt(parent, child, int.MaxValue, src);

    /// <summary>
    /// 插入到指定下标（index &gt;= 子数则追加）。O(index)。
    /// </summary>
    public bool AddChildAt(NodeHandle parent, NodeHandle child, int index, WriteSource src = WriteSource.User)
    {
        if (!ResolveForStructure(parent, out uint p) || !ResolveForStructure(child, out uint c)) return false;
        // 成环检查是**真检查**而不是 debug 门：环会让前序遍历（paintOrder 重展 / 子树标死）
        // 变成死循环，release 下挂起比慢 O(深度≈10) 贵得多。
        if (p == c || IsAncestorOrSelf(c, p))
        {
            UiAssert.That(false, "AddChild 成环：child 是 parent 的祖先（或就是它自己）");
            return false;
        }
        UiAssert.That(index >= 0, "AddChildAt 下标为负");
        if (index < 0) return false;

        uint old = DetachCore(c);
        LinkAt(p, c, index);
        _structEpoch++;

        if (old != NoIndex && old != p)
        {
            NoteStructDirty(old);
            Mark(old, Ch.Structure, src);
        }
        NoteStructDirty(p);
        Mark(p, Ch.Structure, src);
        // 子树的 world 需要在 P6 重算：结构位归父、变换位归子，两条都要给失效平面。
        Mark(c, Ch.Transform, src);
        return true;
    }

    /// <summary>脱离父节点（节点仍存活，只是不在树上——机制⑬：摘树只能来自显式调用）。</summary>
    public bool RemoveFromParent(NodeHandle child, WriteSource src = WriteSource.User)
    {
        if (!ResolveForStructure(child, out uint c)) return false;
        uint p = DetachCore(c);
        if (p == NoIndex) return false;
        _structEpoch++;
        NoteStructDirty(p);
        Mark(p, Ch.Structure, src);
        return true;
    }

    /// <summary>换序（同父内移动到指定下标）。</summary>
    public bool SetChildIndex(NodeHandle child, int index, WriteSource src = WriteSource.User)
    {
        if (!ResolveForStructure(child, out uint c)) return false;
        uint p = _parent[c];
        if (p == NoIndex)
        {
            UiAssert.That(false, "SetChildIndex 于无父节点");
            return false;
        }
        UiAssert.That(index >= 0, "SetChildIndex 下标为负");
        if (index < 0) return false;
        DetachCore(c);
        LinkAt(p, c, index);
        _structEpoch++;
        NoteStructDirty(p);
        Mark(p, Ch.Structure, src);
        return true;
    }

    /// <summary>a 是否为 b 的祖先或自身。</summary>
    public bool IsAncestorOf(NodeHandle a, NodeHandle b) =>
        TryResolve(a, out uint ai) && TryResolve(b, out uint bi) && IsAncestorOrSelf(ai, bi);

    // ── 内部链维护 ──────────────────────────────────────────────────────────

    private bool ResolveForStructure(NodeHandle h, out uint idx)
    {
        if (!TryResolve(h, out idx))
        {
            UiAssert.That(false, "结构编辑于已失效句柄（gen 不符或已 DEAD）");
            return false;
        }
        UiAssert.That(Phase < FramePhase.P7_RenderDrain,
            "相位写门：P7/P8 禁改结构（不变量 3；release 下写入照做、Mark 延迟）");
        return true;
    }

    private bool IsAncestorOrSelf(uint ancestor, uint node)
    {
        for (uint n = node; n != NoIndex; n = _parent[n])
            if (n == ancestor) return true;
        return false;
    }

    private uint ChildAtCore(uint parent, int index)
    {
        uint first = _firstChild[parent];
        if (first == NoIndex) return NoIndex;
        uint c = first;
        for (int k = 0; k < index; k++)
        {
            c = _nextSib[c];
            if (c == first) return NoIndex;
        }
        return c;
    }

    /// <summary>环内下一个兄弟；已是末子（或无父）返回 0。</summary>
    private uint NextInRing(uint i)
    {
        uint p = _parent[i];
        if (p == NoIndex) return NoIndex;
        uint n = _nextSib[i];
        return n == _firstChild[p] ? NoIndex : n;
    }

    /// <summary>摘链（不动 structEpoch、不 Mark）。返回原父，无父返回 0。</summary>
    private uint DetachCore(uint c)
    {
        uint p = _parent[c];
        if (p == NoIndex) return NoIndex;

        uint next = _nextSib[c];
        uint prev = _prevSib[c];
        if (next == c)
        {
            _firstChild[p] = NoIndex;           // 独子
        }
        else
        {
            _nextSib[prev] = next;
            _prevSib[next] = prev;
            if (_firstChild[p] == c) _firstChild[p] = next;
        }
        _parent[c] = NoIndex;
        _nextSib[c] = NoIndex;
        _prevSib[c] = NoIndex;
        return p;
    }

    /// <summary>挂链到 parent 的第 index 位（index &gt;= 子数则追加）。要求 c 已摘链。</summary>
    private void LinkAt(uint p, uint c, int index)
    {
        uint first = _firstChild[p];
        if (first == NoIndex)
        {
            _firstChild[p] = c;
            _nextSib[c] = c;
            _prevSib[c] = c;
        }
        else
        {
            uint at = index == int.MaxValue ? NoIndex : ChildAtCore(p, index);
            if (at == NoIndex)
            {
                uint last = _prevSib[first];    // 追加
                _nextSib[last] = c;
                _prevSib[c] = last;
                _nextSib[c] = first;
                _prevSib[first] = c;
            }
            else
            {
                uint before = _prevSib[at];     // 插入到 at 之前
                _nextSib[before] = c;
                _prevSib[c] = before;
                _nextSib[c] = at;
                _prevSib[at] = c;
                if (at == first) _firstChild[p] = c;
            }
        }
        _parent[c] = p;
    }

    /// <summary>前序推进：cur 的下一个前序节点，走出 stopRoot 即返回 0。全表三处遍历共用。</summary>
    private uint NextPreorder(uint cur, uint stopRoot)
    {
        uint child = _firstChild[cur];
        if (child != NoIndex) return child;
        uint node = cur;
        while (node != stopRoot)
        {
            uint next = NextInRing(node);
            if (next != NoIndex) return next;
            node = _parent[node];
            if (node == NoIndex) break;
        }
        return NoIndex;
    }

    // ── 结构自洽校验（不变量 10 的机器验法；debug 门与测试调用）─────────────

    /// <summary>
    /// 全树链自洽校验：parent/firstChild 一致、prevSib/nextSib 互指、环闭合、无重复访问。
    /// 结构操作后 debug 构建可直接调用；测试按不变量 10 逐条断言。
    /// </summary>
    public bool ValidateTopology(out string error)
    {
        var seen = new HashSet<uint>();
        for (uint cur = Root.Index; cur != NoIndex; cur = NextPreorder(cur, Root.Index))
        {
            if (!seen.Add(cur)) { error = $"节点 {cur} 被访问两次（链成环或交叉）"; return false; }
            if ((_slot[cur] & SlotFlags.Dead) != 0) { error = $"已死节点 {cur} 仍在树上"; return false; }

            uint first = _firstChild[cur];
            if (first == NoIndex) continue;

            uint c = first;
            int guard = 0;
            do
            {
                if (_parent[c] != cur) { error = $"子 {c} 的 parent 不是 {cur}"; return false; }
                if (_nextSib[_prevSib[c]] != c) { error = $"节点 {c}: next[prev[c]] != c"; return false; }
                if (_prevSib[_nextSib[c]] != c) { error = $"节点 {c}: prev[next[c]] != c"; return false; }
                c = _nextSib[c];
                if (++guard > _capacity) { error = $"父 {cur} 的兄弟环未闭合"; return false; }
            } while (c != first);
        }
        error = string.Empty;
        return true;
    }
}
