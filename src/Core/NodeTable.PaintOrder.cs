using FairyNext.Numerics;

namespace FairyNext.Core;

/// <summary>
/// paintOrder 游标（正/逆序，struct 枚举器，零分配）。
/// 逆序是命中测试的读法（上层先命中）；正序是渲染提取的读法。
/// 元素是**节点下标**——渲染/命中平面按下标直读 SoA 列，句柄化由它们自己按需做。
/// </summary>
public ref struct PaintCursor
{
    private readonly ReadOnlySpan<int> _order;
    private readonly int _step;
    private int _i;

    internal PaintCursor(ReadOnlySpan<int> order, bool reverse)
    {
        _order = order;
        _step = reverse ? -1 : 1;
        _i = reverse ? order.Length : -1;
    }

    /// <summary>foreach 支持（按值复制自身，游标可重复取用）。</summary>
    public readonly PaintCursor GetEnumerator() => this;

    /// <summary>当前节点下标。</summary>
    public readonly int Current => _order[_i];

    /// <summary>推进。</summary>
    public bool MoveNext()
    {
        _i += _step;
        return _i >= 0 && _i < _order.Length;
    }
}

public sealed partial class NodeTable
{
    /// <summary>paintIndex 的「不在树上」哨兵（脱链节点与已死槽）。</summary>
    public const uint NotInTree = uint.MaxValue;

    private int[] _paintOrder = Array.Empty<int>();
    private int _paintCount;
    private uint _paintEpoch;

    /// <summary>
    /// 绘制序（机制⑤：全树 DFS 前序物化，元素 = 节点下标；根在 0 位）。
    /// 返回的是**上一次 <see cref="ApplyStructure"/> 收敛的序**——不做惰性重展：
    /// P0 命中按契约就该读上帧已收敛的序（「点到的就是看到的」），惰性重展会把这条契约悄悄改掉。
    /// </summary>
    public ReadOnlySpan<int> GetPaintOrder() => new ReadOnlySpan<int>(_paintOrder, 0, _paintCount);

    /// <summary>绘制序游标。<paramref name="reverse"/>=true 为命中测试用的逆序。</summary>
    public PaintCursor GetPaintCursor(bool reverse = false) => new PaintCursor(GetPaintOrder(), reverse);

    /// <summary>paintOrder 反查（不在树上返回 <see cref="NotInTree"/>）。仅 P6 写（不变量 6）。</summary>
    public uint PaintIndexOf(NodeHandle h) => TryResolve(h, out uint i) ? _paintIndex[i] : NotInTree;

    /// <summary>
    /// 子树在 paintOrder 中的连续区间（DFS 前序使子树天然连续）。
    /// M1-14 的切片拼接按这个区间做 memcpy；本包只用它做区间读。
    /// </summary>
    public bool TryGetSubtreeRange(NodeHandle h, out int start, out int count)
    {
        start = 0; count = 0;
        if (!TryResolve(h, out uint i)) return false;
        uint pi = _paintIndex[i];
        if (pi == NotInTree) return false;
        int n = 0;
        for (uint cur = i; cur != NoIndex; cur = NextPreorder(cur, i)) n++;
        start = (int)pi;
        count = n;
        return true;
    }

    /// <summary>
    /// P6 钩子①（接缝：<c>ApplyStructure()</c>）：结构定形。
    /// 本包只实现**全量重展**——架构文档明说脏比例超阈值即退化为整树重展（memcpy 速度），
    /// 先把退化路径做对；切片拼接（未动子树切片原样 memcpy）留 M1-14。
    /// </summary>
    public void ApplyStructure()
    {
        UiAssert.That(Phase == FramePhase.P6_Settle,
            "paintOrder 重展仅 P6 可做（不变量 6：派生列相位所有权）");
        if (_paintEpoch == _structEpoch) return;   // structEpoch 未变 ⇒ 数组 bit-identical（不变量 5）
        RebuildPaintOrderCore();
    }

    /// <summary>
    /// P6 钩子②（接缝：<c>DrainDerived()</c>）的**全量版**：沿 paintOrder 一遍算 world + worldVisual。
    /// paintOrder 是 DFS 前序 ⇒ 父必先于子 ⇒ 单趟顺序写即可，不需要递归也不需要栈。
    /// M1-14 上增量版（脏根 span + 向下通道子树戳）后，本方法留作
    /// **增量正确性门的全量重算神谕**（增量结果必须与它逐位相等）。
    /// </summary>
    public void DrainDerivedFull()
    {
        UiAssert.That(Phase == FramePhase.P6_Settle,
            "world/worldVisual 仅 P6 可写（不变量 6）");
        // 序未定形就算派生 = 沿一条陈旧（或空）的 paintOrder 算：循环次数不对，结果**静默错**
        // 而不是崩溃——比崩溃难查得多。相位机的 P6 是「先 ApplyStructure 再 DrainDerived」，
        // 任何绕过内核直调本方法的路径（测试、工具、神谕）必须自己先定形。
        UiAssert.That(_paintEpoch == _structEpoch,
            "DrainDerivedFull 于未定形的 paintOrder（先调 ApplyStructure：结构变更后序必须先收敛）");
        DrainDerivedCore();
    }

    /// <summary>
    /// 世界变换（2×3 仿射）。**仅 P6 排水后有效**——P0–P5 读到的是上帧收敛值（机制①的二分）。
    /// 返回 ref readonly：持有期间不得触碰任何会扩容的 API，按 <see cref="TableEpoch"/> 自检（不变量 12）。
    /// </summary>
    public ref readonly Affine2D World(NodeHandle h)
    {
        if (!TryResolve(h, out uint i))
        {
            UiAssert.That(false, "World 于失效句柄");
            return ref _identityWorld;
        }
        return ref _world[i];
    }

    /// <summary>级联视觉（α 积 / visible AND / grayed OR / clip 域）。**仅 P6 排水后有效**。</summary>
    public uint WorldVisual(NodeHandle h) => TryResolve(h, out uint i) ? _worldVisual[i] : 0u;

    /// <summary>失效句柄查 World 时的返回锚（readonly：ref readonly 返回不可能被写穿）。</summary>
    private static readonly Affine2D _identityWorld = Affine2D.Identity;

    private void RebuildPaintOrderCore()
    {
        if (_paintOrder.Length < _capacity) Array.Resize(ref _paintOrder, _capacity);
        // 全量重展负担得起 O(capacity) 的清扫：不在树上的节点必须落到哨兵，
        // 否则「paintIndex[paintOrder[i]]==i」的门会被脱链节点的陈旧下标骗过。
        _paintIndex.AsSpan(0, _capacity).Fill(NotInTree);

        int n = 0;
        for (uint cur = Root.Index; cur != NoIndex; cur = NextPreorder(cur, Root.Index))
        {
            _paintOrder[n] = (int)cur;
            _paintIndex[cur] = (uint)n;
            n++;
        }
        _paintCount = n;
        _paintEpoch = _structEpoch;
    }

    private void DrainDerivedCore()
    {
        for (int k = 0; k < _paintCount; k++)
        {
            uint idx = (uint)_paintOrder[k];
            uint p = _parent[idx];
            Affine2D local = LocalMatrixCore(idx);
            if (p == NoIndex)
            {
                _world[idx] = local;
                _worldVisual[idx] = _localVisual[idx];
            }
            else
            {
                _world[idx] = Affine2D.Compose(_world[p], local);
                _worldVisual[idx] = Visual.Cascade(_worldVisual[p], _localVisual[idx]);
            }
        }
    }

    // ── 门：paintOrder ≡ 拓扑（不变量 5 的机器验法）─────────────────────────

    /// <summary>
    /// 抽样门：现场重展一遍 DFS 前序并与 paintOrder 逐元素比对，
    /// 同时验 <c>paintIndex[paintOrder[i]]==i</c>。debug 构建与测试调用。
    /// </summary>
    public bool ValidatePaintOrder(out string error)
    {
        var expect = new List<int>();
        for (uint cur = Root.Index; cur != NoIndex; cur = NextPreorder(cur, Root.Index))
            expect.Add((int)cur);

        if (expect.Count != _paintCount)
        {
            error = $"paintOrder 长度 {_paintCount} != 拓扑 DFS 前序长度 {expect.Count}";
            return false;
        }
        for (int i = 0; i < expect.Count; i++)
        {
            if (_paintOrder[i] != expect[i])
            {
                error = $"paintOrder[{i}]={_paintOrder[i]} != DFS 前序 {expect[i]}";
                return false;
            }
            if (_paintIndex[expect[i]] != (uint)i)
            {
                error = $"paintIndex[{expect[i]}]={_paintIndex[expect[i]]} != {i}";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }
}
