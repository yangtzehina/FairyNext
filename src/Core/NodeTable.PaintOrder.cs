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

    /// <summary>
    /// 一帧内允许记录的结构脏父数上限。撞上即置溢出旗、本帧退化为整树重展——
    /// 脏根多到这个量级时，逐根定位切片的簿记已经比一次 memcpy 重展贵。
    /// </summary>
    public const int PaintSpliceMaxRoots = 64;

    private int[] _paintOrder = Array.Empty<int>();
    private int[] _paintScratch = Array.Empty<int>();     // 切片拼接的写侧（旧数组读、新数组写）
    private int _paintCount;
    private uint _paintEpoch;

    // 结构脏父：结构编辑（AddChild/RemoveFromParent/SetChildIndex/Destroy）当场记下**父**下标，
    // P6 的 ApplyStructure 消费后清空。这份账与 Ch.Structure 队列**不是同一本**：
    // 队列归失效平面、在 P7 被 Extract 消费；本表归树自己、在 P6 定形序时消费。
    // 一件事两个消费者、两个相位，各记各的比互相偷看对方的队列干净。
    private uint[] _structDirty = Array.Empty<uint>();
    private int _structDirtyCount;
    private bool _structOverflow;

    // 区间归一化 scratch（切片拼接与派生列增量共用同一套算法，各持一份缓冲）
    private int[] _spliceStart = Array.Empty<int>();
    private int[] _spliceEnd = Array.Empty<int>();
    private uint[] _spliceRoot = Array.Empty<uint>();
    private int[] _derivedStart = Array.Empty<int>();
    private int[] _derivedEnd = Array.Empty<int>();

    // 增量正确性门（派生列半边）的神谕缓冲：只读比对，不写真列
    private Affine2D[] _oracleWorld = Array.Empty<Affine2D>();
    private uint[] _oracleVisual = Array.Empty<uint>();
    private bool[] _oracleStale = Array.Empty<bool>();

    /// <summary>
    /// 切片拼接的退化阈值（脏切片总长占 paintOrder 的百分比）。超过即整树重展：
    /// 重展是一次 DFS + 顺序写，脏得够多时它比「逐根定位 + 分段拷贝」更快也更简单。
    /// </summary>
    public int PaintSpliceMaxDirtyPercent { get; set; } = 50;

    /// <summary>切片拼接次数（诊断：增量路径真的在走）。</summary>
    public int PaintSplices { get; private set; }

    /// <summary>整树重展次数（诊断：退化路径的频次；持续高企 = 阈值或脏根口径要复核）。</summary>
    public int PaintFullRebuilds { get; private set; }

    /// <summary>最近一次切片拼接中**原样搬运**的元素数。</summary>
    public int LastSpliceCopied { get; private set; }

    /// <summary>最近一次切片拼接中**重新展开**的元素数。</summary>
    public int LastSpliceExpanded { get; private set; }

    /// <summary>会话累计的派生列增量重算节点数（world/worldVisual 各算一次访问）。</summary>
    public long DerivedRecomputed { get; private set; }

    /// <summary>待消费的结构脏父数（P6 之外读到的是本帧至今的累计）。</summary>
    public int StructDirtyRoots => _structDirtyCount;

    /// <summary>结构脏父是否已溢出（本帧只能整树重展）。</summary>
    public bool StructDirtyOverflowed => _structOverflow;

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
    /// paintOrder 反查的下标版（命中测试沿**上帧收敛的序**下行，每步句柄化太贵）。
    /// 上次 <see cref="ApplyStructure"/> 之后新建/重接的节点在这里读到 <see cref="NotInTree"/>
    /// 或旧位置——那正是「当帧新建节点当帧不可命中」这条明文契约的存储形态。
    /// </summary>
    internal uint PaintIndexAt(uint index) => _paintIndex[index];

    /// <summary>子树在 paintOrder 中的排他终点（与 <see cref="PaintIndexAt"/> 同一次收敛的快照）。</summary>
    internal uint PaintEndAt(uint index) => _paintEnd[index];

    /// <summary>
    /// 子树在 paintOrder 中的连续区间（DFS 前序使子树天然连续）。
    /// 序已定形时是 O(1)（读切片表 <c>paintEnd</c>）；未定形时退回走一遍当前树——
    /// 后者的 count 描述的是**新**树而 start 来自**旧**序，只可作粗判（Extract 的面板归属过滤按此用）。
    /// </summary>
    public bool TryGetSubtreeRange(NodeHandle h, out int start, out int count)
    {
        start = 0; count = 0;
        if (!TryResolve(h, out uint i)) return false;
        uint pi = _paintIndex[i];
        if (pi == NotInTree) return false;
        start = (int)pi;
        if (_paintEpoch == _structEpoch)
        {
            count = (int)(_paintEnd[i] - pi);
            return true;
        }
        int n = 0;
        for (uint cur = i; cur != NoIndex; cur = NextPreorder(cur, i)) n++;
        count = n;
        return true;
    }

    /// <summary>
    /// P6 钩子①（接缝：<c>ApplyStructure()</c>）：结构定形。
    ///
    /// 两条路，判据是脏比例（架构机制⑤：「脏父切片拼接」，脏得够多则退化为整树重展）：
    ///  · **切片拼接**——把结构编辑记下的脏父归一成互不相交的子树区间，未动的切片
    ///    原样 <see cref="Array.Copy"/> 搬过去，只有脏子树重新走一遍树；
    ///  · **整树重展**——脏比例超 <see cref="PaintSpliceMaxDirtyPercent"/>、脏根溢出、或本就没有旧序可搬。
    /// 两条路的产物必须逐元素相同：<see cref="ValidatePaintOrder"/> 是它们共同的神谕。
    /// </summary>
    public void ApplyStructure()
    {
        UiAssert.That(Phase == FramePhase.P6_Settle,
            "paintOrder 重展仅 P6 可做（不变量 6：派生列相位所有权）");
        if (_paintEpoch == _structEpoch) { ClearStructDirty(); return; }   // structEpoch 未变 ⇒ 数组 bit-identical
        if (TrySplicePaintOrder()) PaintSplices++;
        else { RebuildPaintOrderCore(); PaintFullRebuilds++; }
        ClearStructDirty();
    }

    /// <summary>
    /// P6 钩子②（接缝：<c>DrainDerived()</c>）的**全量版**：沿 paintOrder 一遍算 world + worldVisual。
    /// paintOrder 是 DFS 前序 ⇒ 父必先于子 ⇒ 单趟顺序写即可，不需要递归也不需要栈。
    ///
    /// M1-14 起增量版（<see cref="DrainDerivedFrom"/>）接管常规路径，本方法留作
    /// **增量正确性门的全量重算神谕**——只读的比对形态见 <see cref="DerivedMatchesFullRecompute"/>。
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
        for (int k = 0; k < _paintCount; k++) DeriveAt((uint)_paintOrder[k]);
        DerivedRecomputed += _paintCount;
    }

    /// <summary>
    /// P6 钩子②的**增量版**：沿脏子树重算 world + worldVisual，**一遍两算**。
    ///
    /// 输入是「脏根」句柄集（几何相关的三条上行通道 Transform/Layout/Content 的在队快照，
    /// 由 P6 的排水器窥视得到——窥视而不出队：消费权归 P7）。三步：
    ///  ① 每个脏根换成它在 paintOrder 中的连续区间 <c>[paintIndex, paintEnd)</c>；
    ///  ② 按起点排序后**归并嵌套**（子树区间是层叠族：要么不交、要么包含），于是重叠的脏根只算一次；
    ///  ③ 每个区间顺序扫一遍：前序保证父先于子，父值在扫到子之前必已写好；
    ///     区间根自己的父在区间外——它不脏，故其值就是上一帧收敛值，**正是**该用的那个。
    /// 返回重算的节点数。
    /// </summary>
    public int DrainDerivedFrom(ReadOnlySpan<NodeHandle> roots)
    {
        UiAssert.That(Phase == FramePhase.P6_Settle, "world/worldVisual 仅 P6 可写（不变量 6）");
        UiAssert.That(_paintEpoch == _structEpoch,
            "DrainDerivedFrom 于未定形的 paintOrder（先调 ApplyStructure）");

        int n = CollectDerivedRanges(roots);
        if (n < 0)
        {
            DrainDerivedFull();          // 区间族不层叠 = 序不是前序了：退回全量（正确性优先）
            return _paintCount;
        }
        int visited = 0;
        for (int i = 0; i < n; i++)
        {
            for (int k = _derivedStart[i]; k < _derivedEnd[i]; k++)
            {
                DeriveAt((uint)_paintOrder[k]);
                visited++;
            }
        }
        DerivedRecomputed += visited;
        return visited;
    }

    /// <summary>
    /// P6 下行下钻的级联写口（<see cref="DownVisitor"/> 每访问一个节点调一次）。
    /// 只算 worldVisual——下行通道（α/隐显/置灰/层）不动矩阵；父先于子由排水器的访问序保证。
    /// </summary>
    internal void CascadeVisualAt(uint index)
    {
        UiAssert.That(Phase == FramePhase.P6_Settle, "worldVisual 仅 P6 可写（不变量 6）");
        uint p = _parent[index];
        _worldVisual[index] = p == NoIndex
            ? _localVisual[index]
            : Visual.Cascade(_worldVisual[p], _localVisual[index]);
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

    /// <summary>结构编辑记一个脏父（切片拼接的输入）。溢出后只置旗，不再记——本帧退化整树重展。</summary>
    private void NoteStructDirty(uint parent)
    {
        if (_structOverflow) return;
        for (int i = 0; i < _structDirtyCount; i++) if (_structDirty[i] == parent) return;   // 去重
        if (_structDirtyCount >= PaintSpliceMaxRoots) { _structOverflow = true; return; }
        if (_structDirtyCount == _structDirty.Length)
            Array.Resize(ref _structDirty, _structDirty.Length == 0 ? 8 : _structDirty.Length * 2);
        _structDirty[_structDirtyCount++] = parent;
    }

    private void ClearStructDirty()
    {
        _structDirtyCount = 0;
        _structOverflow = false;
    }

    /// <summary>派生列的单点公式（全量版与增量版共用**同一个**写口，两条路径不可能算出两种结果）。</summary>
    private void DeriveAt(uint idx)
    {
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

    private bool TrySplicePaintOrder()
    {
        if (_structOverflow || _structDirtyCount == 0 || _paintCount == 0) return false;

        EnsureSpliceScratch(_structDirtyCount);
        int n = 0;
        for (int i = 0; i < _structDirtyCount; i++)
        {
            uint idx = _structDirty[i];
            // 已死的脏父：它自己被摘链时同样记了**它的**父，那条祖先记录覆盖本切片。
            if (!IsIndexAlive(idx)) continue;
            uint pi = _paintIndex[idx];
            // 不在旧序里的脏父：编辑发生在树外。要么整支与 paintOrder 无关，
            // 要么它后来被挂进了树——那次挂接同样记了在序的挂接点，由那条覆盖。
            if (pi == NotInTree) continue;
            _spliceStart[n] = (int)pi;
            _spliceEnd[n] = (int)_paintEnd[idx];
            _spliceRoot[n] = idx;
            n++;
        }
        n = NormalizeRanges(_spliceStart, _spliceEnd, _spliceRoot, n);
        if (n < 0) return false;
        if (n == 0)
        {
            _paintEpoch = _structEpoch;      // 树形没动过（结构编辑全在树外）
            LastSpliceCopied = _paintCount;
            LastSpliceExpanded = 0;
            return true;
        }

        int dirty = 0;
        for (int i = 0; i < n; i++) dirty += _spliceEnd[i] - _spliceStart[i];
        if (dirty * 100 > _paintCount * PaintSpliceMaxDirtyPercent) return false;

        // ① 先把全部脏切片的 paintIndex 清成哨兵：掉出树的节点从此响亮失败（PaintIndexOf 报 NotInTree），
        //    留在树里的稍后由展开/搬运写回。**清必须整体先于拼接**——子树从 A 搬到 B 时，
        //    B 的展开可能发生在 A 的清扫之前，边清边拼会把刚写好的新下标又抹掉。
        for (int i = 0; i < n; i++)
            for (int k = _spliceStart[i]; k < _spliceEnd[i]; k++)
                _paintIndex[(uint)_paintOrder[k]] = NotInTree;

        int w = 0, read = 0, copied = 0, expanded = 0;
        for (int i = 0; i < n; i++)
        {
            copied += CopySlice(read, _spliceStart[i], ref w);
            read = _spliceEnd[i];
            expanded += ExpandSubtree(_spliceRoot[i], ref w);
        }
        copied += CopySlice(read, _paintCount, ref w);

        // 切片表整表重算：三遍线性扫（置 1 → 反向累加规模 → 正向换算终点），全程不走树。
        // 为什么不按脏根逐条修：子树规模是**沿祖先链共享**的，脏一个叶就改了它全部祖先的终点，
        // 逐条去修那些链既不比线性扫便宜，又多一类「某条链忘了修」的漏。
        for (int k = 0; k < w; k++) _paintEnd[(uint)_paintScratch[k]] = 1u;
        CloseSubtreeEnds(_paintScratch, 0, w);

        int[] swap = _paintOrder;
        _paintOrder = _paintScratch;
        _paintScratch = swap;
        _paintCount = w;
        _paintEpoch = _structEpoch;
        LastSpliceCopied = copied;
        LastSpliceExpanded = expanded;
        return true;
    }

    /// <summary>把旧序的 [from, to) 原样搬进新序。位移为 0 时连反查表都不必动。</summary>
    private int CopySlice(int from, int to, ref int w)
    {
        int len = to - from;
        if (len <= 0) return 0;
        int delta = w - from;
        if (delta == 0)
        {
            Array.Copy(_paintOrder, from, _paintScratch, w, len);   // 未移位：下标与切片终点原样有效
        }
        else
        {
            for (int k = from; k < to; k++)
            {
                uint idx = (uint)_paintOrder[k];
                _paintScratch[k + delta] = (int)idx;
                _paintIndex[idx] = (uint)(k + delta);
            }
        }
        w += len;
        return len;
    }

    /// <summary>把一棵子树重新展开进新序（唯一一处走树的地方，成本正比于脏子树）。</summary>
    private int ExpandSubtree(uint root, ref int w)
    {
        int w0 = w;
        for (uint cur = root; cur != NoIndex; cur = NextPreorder(cur, root))
        {
            _paintScratch[w] = (int)cur;
            _paintIndex[cur] = (uint)w;
            w++;
        }
        return w - w0;
    }

    /// <summary>
    /// 子树规模 → 排他终点：反向一遍把每个节点的规模累加给父（父必在切片内，前序保证），
    /// 再正向一遍换算成绝对终点下标。两遍都是 O(切片)，不必为切片表再走一次树。
    /// </summary>
    private void CloseSubtreeEnds(int[] buf, int from, int to)
    {
        for (int k = to - 1; k > from; k--)
        {
            uint idx = (uint)buf[k];
            _paintEnd[_parent[idx]] += _paintEnd[idx];
        }
        for (int k = from; k < to; k++)
        {
            uint idx = (uint)buf[k];
            _paintEnd[idx] += (uint)k;
        }
    }

    private int CollectDerivedRanges(ReadOnlySpan<NodeHandle> roots)
    {
        EnsureDerivedScratch(roots.Length);
        int n = 0;
        for (int i = 0; i < roots.Length; i++)
        {
            if (!TryResolve(roots[i], out uint idx)) continue;      // 入队后死掉的：没有子树可算
            uint pi = _paintIndex[idx];
            if (pi == NotInTree) continue;                          // 不在树上：派生列无意义
            _derivedStart[n] = (int)pi;
            _derivedEnd[n] = (int)_paintEnd[idx];
            n++;
        }
        return NormalizeRanges(_derivedStart, _derivedEnd, null, n);
    }

    /// <summary>
    /// 区间归一：按起点插入排序 + 丢掉被前一条包含的。
    /// 子树区间是**层叠族**（laminar：任意两条要么不交、要么包含），所以「起点落在前一条内」
    /// 就等价于「被前一条包含」。层叠性被破坏说明 paintOrder 已不是前序——返回 -1 让调用方退回全量，
    /// 不在这里猜一个合并结果。
    /// </summary>
    private static int NormalizeRanges(int[] start, int[] end, uint[]? root, int n)
    {
        for (int i = 1; i < n; i++)
        {
            int s = start[i], e = end[i];
            uint r = root == null ? 0u : root[i];
            int j = i - 1;
            while (j >= 0 && start[j] > s)
            {
                start[j + 1] = start[j];
                end[j + 1] = end[j];
                if (root != null) root[j + 1] = root[j];
                j--;
            }
            start[j + 1] = s;
            end[j + 1] = e;
            if (root != null) root[j + 1] = r;
        }

        int w = 0;
        for (int i = 0; i < n; i++)
        {
            if (w > 0 && start[i] < end[w - 1])
            {
                if (end[i] > end[w - 1])
                {
                    UiAssert.That(false, "子树区间既相交又不包含——paintOrder 不再是 DFS 前序");
                    return -1;
                }
                continue;                                           // 被上一条包含：归并
            }
            start[w] = start[i];
            end[w] = end[i];
            if (root != null) root[w] = root[i];
            w++;
        }
        return w;
    }

    private void RebuildPaintOrderCore()
    {
        EnsurePaintBuffers();
        // 全量重展负担得起 O(capacity) 的清扫：不在树上的节点必须落到哨兵，
        // 否则「paintIndex[paintOrder[i]]==i」的门会被脱链节点的陈旧下标骗过。
        _paintIndex.AsSpan(0, _capacity).Fill(NotInTree);

        int n = 0;
        for (uint cur = Root.Index; cur != NoIndex; cur = NextPreorder(cur, Root.Index))
        {
            _paintOrder[n] = (int)cur;
            _paintIndex[cur] = (uint)n;
            _paintEnd[cur] = 1u;
            n++;
        }
        CloseSubtreeEnds(_paintOrder, 0, n);
        _paintCount = n;
        _paintEpoch = _structEpoch;
    }

    private void EnsurePaintBuffers()
    {
        if (_paintOrder.Length < _capacity) Array.Resize(ref _paintOrder, _capacity);
        if (_paintScratch.Length < _capacity) Array.Resize(ref _paintScratch, _capacity);
    }

    private void EnsureSpliceScratch(int n)
    {
        EnsurePaintBuffers();
        if (_spliceStart.Length >= n) return;
        int cap = _spliceStart.Length == 0 ? 8 : _spliceStart.Length;
        while (cap < n) cap *= 2;
        Array.Resize(ref _spliceStart, cap);
        Array.Resize(ref _spliceEnd, cap);
        Array.Resize(ref _spliceRoot, cap);
    }

    private void EnsureDerivedScratch(int n)
    {
        if (_derivedStart.Length >= n) return;
        int cap = _derivedStart.Length == 0 ? 8 : _derivedStart.Length;
        while (cap < n) cap *= 2;
        Array.Resize(ref _derivedStart, cap);
        Array.Resize(ref _derivedEnd, cap);
    }

    // ── 门：paintOrder ≡ 拓扑（不变量 5 的机器验法）─────────────────────────

    /// <summary>
    /// 抽样门：现场重展一遍 DFS 前序并与 paintOrder 逐元素比对，
    /// 同时验 <c>paintIndex[paintOrder[i]]==i</c>、切片表 <c>paintEnd</c> 与真实子树规模一致、
    /// 以及**掉出树的活节点必须落回哨兵**（切片拼接只清脏切片，漏清就会留下一个指向别人位置的反查）。
    /// debug 构建与测试调用。
    /// </summary>
    public bool ValidatePaintOrder(out string error)
    {
        var expect = new List<int>();
        var size = new Dictionary<int, int>();
        for (uint cur = Root.Index; cur != NoIndex; cur = NextPreorder(cur, Root.Index))
        {
            expect.Add((int)cur);
            int n = 0;
            for (uint s = cur; s != NoIndex; s = NextPreorder(s, cur)) n++;
            size[(int)cur] = n;
        }

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
            if (_paintEnd[expect[i]] != (uint)(i + size[expect[i]]))
            {
                error = $"paintEnd[{expect[i]}]={_paintEnd[expect[i]]} != {i + size[expect[i]]}（切片表与子树规模不符）";
                return false;
            }
        }

        var inTree = new HashSet<int>(expect);
        for (uint idx = 1; idx < (uint)_capacity; idx++)
        {
            if (!IsIndexAlive(idx) || inTree.Contains((int)idx)) continue;
            if (_paintIndex[idx] != NotInTree)
            {
                error = $"脱链的活节点 {idx} 仍带 paintIndex={_paintIndex[idx]}（哨兵漏清）";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 增量正确性门的**派生列半边**（架构不变量 13 的可执行形态）：
    /// 沿 paintOrder 从根**全量重算**一份 world/worldVisual 到内部缓冲，与当前列逐位比对。
    /// **只读**——神谕不写它要检查的列，否则一次比对就把证据抹掉了。
    ///
    /// 两条口径明文：
    ///  · <c>world</c> **全树都比**：矩阵不随下行通道变，增量路径按脏子树整段重算，不存在合法的陈旧；
    ///  · <c>worldVisual</c> **跳过局部隐藏节点的后代**：「隐藏子树免下钻」是下行通道的明文契约
    ///    （不变量 13），其后代的级联值按契约就是陈旧的，重新显示时由 Visible 排水补戳补算。
    ///    比它等于把一条设计里的优化判成 bug。
    /// </summary>
    /// <param name="badIndex">首个不一致的节点下标（全等时为 <see cref="NoIndex"/>）。</param>
    /// <returns>全等返回 true。</returns>
    public bool DerivedMatchesFullRecompute(out uint badIndex)
    {
        badIndex = NoIndex;
        EnsureOracleBuffers();
        for (int k = 0; k < _paintCount; k++)
        {
            uint idx = (uint)_paintOrder[k];
            uint p = _parent[idx];
            Affine2D local = LocalMatrixCore(idx);
            Affine2D world;
            uint visual;
            bool stale;
            if (p == NoIndex)
            {
                world = local;
                visual = _localVisual[idx];
                stale = false;
            }
            else
            {
                world = Affine2D.Compose(_oracleWorld[p], local);
                visual = Visual.Cascade(_oracleVisual[p], _localVisual[idx]);
                stale = _oracleStale[p] || (_localVisual[p] & Visual.Visible) == 0;
            }
            _oracleWorld[idx] = world;
            _oracleVisual[idx] = visual;
            _oracleStale[idx] = stale;

            if (!SameBits(in world, in _world[idx])) { badIndex = idx; return false; }
            if (!stale && visual != _worldVisual[idx]) { badIndex = idx; return false; }
        }
        return true;
    }

    private void EnsureOracleBuffers()
    {
        if (_oracleWorld.Length >= _capacity) return;
        Array.Resize(ref _oracleWorld, _capacity);
        Array.Resize(ref _oracleVisual, _capacity);
        Array.Resize(ref _oracleStale, _capacity);
    }

    /// <summary>逐位相等（不是 <see cref="BitEquals"/>：神谕宁可多报一次差异，也不能把 NaN 差异吃掉）。</summary>
    private static bool SameBits(in Affine2D a, in Affine2D b) =>
        BitConverter.SingleToInt32Bits(a.m00) == BitConverter.SingleToInt32Bits(b.m00) &&
        BitConverter.SingleToInt32Bits(a.m01) == BitConverter.SingleToInt32Bits(b.m01) &&
        BitConverter.SingleToInt32Bits(a.m10) == BitConverter.SingleToInt32Bits(b.m10) &&
        BitConverter.SingleToInt32Bits(a.m11) == BitConverter.SingleToInt32Bits(b.m11) &&
        BitConverter.SingleToInt32Bits(a.tx) == BitConverter.SingleToInt32Bits(b.tx) &&
        BitConverter.SingleToInt32Bits(a.ty) == BitConverter.SingleToInt32Bits(b.ty);
}
