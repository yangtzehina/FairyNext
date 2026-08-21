namespace FairyNext.Core.Layout;

/// <summary>布局轴（每个约束算子只作用于一根轴；跨轴关系在 24 关系表里不存在）。</summary>
public enum LayoutAxis : byte
{
    /// <summary>水平（x / width）。</summary>
    X = 0,
    /// <summary>垂直（y / height）。</summary>
    Y = 1,
}

/// <summary>
/// 轴上的标量空间（架构平面四 A「约束图」：每轴 {Min, Center, Max, Size}）。
/// Min = 原点边（父空间 pos）、Max = pos + size、Center = pos + size/2、Size = 尺寸。
/// </summary>
public enum EdgeSel : byte
{
    Min = 0,
    Center = 1,
    Max = 2,
    Size = 3,
}

/// <summary>
/// 算子种类。24 种 RelationType 归约为单算子 EdgeFollow 的三个形态：
///  - <see cref="FollowDelta"/>：dst.edge = src.edge + offset（offset 建立时捕获）；
///  - <see cref="FollowPercent"/>：dst.edge = src.Min + ratio × src.Size（ratio 建立时捕获）——
///    与 fork RelationItem 的 percent 公式同形（`owner.xMin = pos + (owner.xMin − pos) * delta`
///    展开后正是「相对 target 起点的比例保持」；Size 形态 `dstSize = ratio × srcSize` 与 fork 的
///    `width = target.width + (rawWidth − oldW) × newW/oldW` 代数等价）。**求值读 src 的
///    (Min, Size) 而不读 srcEdge 位**——fork 对 Left_Left/Left_Center/Left_Right 的 percent
///    分支是同一条公式，锚点恒为 target 起点；
///  - <see cref="Pin"/>：dst.edge = 捕获常量（Ext 类关系的「对边钉住」半边：LeftExt_Right =
///    Follow(Min ← t.Max) + Pin(Max)，钉住值在建立/重捕获时取 authored 对应边——
///    fork 用「保持当前 xMin+width」实现同一语义，但那是事件驱动下的现值；本仓库从捕获值
///    重算，多写者下不漂移，且与「用户写 authored 即重捕获」一致）。
/// </summary>
public enum ConstraintKind : byte
{
    FollowDelta = 0,
    FollowPercent = 1,
    Pin = 2,
}

/// <summary>
/// 约束算子（架构平面四 A：8B，烘焙期拓扑排序，**数组索引即拓扑序**）。
/// 位域 <see cref="AxisEdges"/>：axis:1 | dstEdge:2 | srcEdge:2 | pivotCorrect:1（余 2 位保留写零）。
/// pivotCorrect 的修正**系数**由编译器烘焙（M1-20 消灭 pivotAsAnchor 时产出）；本包只保布局位，
/// <see cref="ConstraintGraphBuilder"/> 不提供置位入口——位存在而无人能置，语义落地前不会有静默错值。
/// </summary>
public struct ConstraintOp
{
    /// <summary>源节点（组件局部索引；0 = 容器自身，其边在子坐标系里是 (0, size)）。Pin 无源，置同 <see cref="DstNode"/>。</summary>
    public ushort SrcNode;
    /// <summary>目标节点（组件局部索引；不允许为 0——容器自身由外层图或 authored 定形）。</summary>
    public ushort DstNode;
    /// <summary><see cref="ConstraintKind"/>。</summary>
    public byte Kind;
    /// <summary>位域：axis:1 | dstEdge:2 | srcEdge:2 | pivotCorrect:1。</summary>
    public byte AxisEdges;
    /// <summary>同 (dstNode, axis) 的算子链（拉伸成对求值）：0 = 无，否则 = 伙伴算子下标 + 1。</summary>
    public ushort Next;

    /// <summary>打包位域（<see cref="AxisEdges"/> 的唯一构造点）。</summary>
    public static byte Pack(LayoutAxis axis, EdgeSel dstEdge, EdgeSel srcEdge, bool pivotCorrect) =>
        (byte)((byte)axis | ((byte)dstEdge << 1) | ((byte)srcEdge << 3) | (pivotCorrect ? 1 << 5 : 0));

    /// <summary>轴。</summary>
    public LayoutAxis Axis => (LayoutAxis)(AxisEdges & 1);

    /// <summary>目标边。</summary>
    public EdgeSel DstEdge => (EdgeSel)((AxisEdges >> 1) & 3);

    /// <summary>源边（FollowPercent 求值不读此位，见 <see cref="ConstraintKind"/>）。</summary>
    public EdgeSel SrcEdge => (EdgeSel)((AxisEdges >> 3) & 3);

    /// <summary>pivot 修正位（系数烘焙归 M1-20；本包恒 0）。</summary>
    public bool PivotCorrect => (AxisEdges & (1 << 5)) != 0;
}

/// <summary>CSR 邻接（架构平面四 A）：srcNode → 受影响算子区间（<see cref="ConstraintGraph.FanOpIndices"/> 的切片），脏传播用。</summary>
public struct FanOut
{
    /// <summary>区间起点。</summary>
    public ushort Start;
    /// <summary>区间长度。</summary>
    public ushort Count;
}

/// <summary>
/// 封好的约束图（编译态；per-component-template，运行期只读）。
/// **约束图全烘焙，运行时不提供 AddRelation**（架构裁决）：插入会打乱「索引=拓扑序」不变量。
/// M1-20 的 FgbCompiler 把 .fui 的 relation 表编译成本形态并进 FGB blob 段；本包的
/// <see cref="ConstraintGraphBuilder"/> 是同一 Seal 逻辑的手建入口——约束环拒绝门的地基在这里。
/// </summary>
public sealed class ConstraintGraph
{
    /// <summary>算子表，**索引即拓扑序**：顺序遍历置位算子 = 拓扑求值，单遍收敛。</summary>
    public readonly ConstraintOp[] Ops;

    /// <summary>srcNode → FanOpIndices 区间。</summary>
    public readonly FanOut[] FanOutBySrc;

    /// <summary>FanOut 的算子下标池（每桶内升序 = 拓扑序）。</summary>
    public readonly ushort[] FanOpIndices;

    /// <summary>组件局部节点数（含局部 0 = 容器自身）。</summary>
    public readonly int NodeCount;

    /// <summary>
    /// 每个局部节点的布局归属掩码（<see cref="LayoutOwn"/> 位）：哪些几何分量由本图的算子写、
    /// 哪些轴绑定到容器（局部 0）——后者供「绑父轴不计包围」静态判定。
    /// </summary>
    public readonly byte[] NodeMasks;

    internal ConstraintGraph(ConstraintOp[] ops, FanOut[] fanOut, ushort[] fanOps, int nodeCount, byte[] nodeMasks)
    {
        Ops = ops;
        FanOutBySrc = fanOut;
        FanOpIndices = fanOps;
        NodeCount = nodeCount;
        NodeMasks = nodeMasks;
    }
}

/// <summary>几何分量归属位（resolved 槽的写者登记；布局是唯一写者，这里分的是布局**内部**哪层写哪个分量）。</summary>
public static class LayoutOwn
{
    /// <summary>x（位置，X 轴）。</summary>
    public const byte PosX = 1;
    /// <summary>width。</summary>
    public const byte SizeX = 2;
    /// <summary>y。</summary>
    public const byte PosY = 4;
    /// <summary>height。</summary>
    public const byte SizeY = 8;
    /// <summary>该节点 X 轴绑定到容器（局部 0）——「绑父轴不计包围」的判定位。</summary>
    public const byte BoundRootX = 16;
    /// <summary>该节点 Y 轴绑定到容器。</summary>
    public const byte BoundRootY = 32;
}

/// <summary>
/// <see cref="ConstraintGraphBuilder.Seal"/> 的结果。环 = 编译错（架构不变量 2）：
/// **CycleRejected 带环路径、不 throw 不静默**——M1-20 把它变成 FGM 诊断，这里先返回结构化结果。
/// </summary>
public sealed class ConstraintGraphResult
{
    /// <summary>封图成功。</summary>
    public readonly bool Ok;
    /// <summary>成功时的图；失败为 null。</summary>
    public readonly ConstraintGraph? Graph;
    /// <summary>失败原因（人读；成功为空串）。</summary>
    public readonly string Error;
    /// <summary>CycleRejected 时的环路径（沿依赖方向的局部节点序列，首尾同节点闭合）；其余为空。</summary>
    public readonly ushort[] CyclePath;

    internal ConstraintGraphResult(ConstraintGraph graph)
    {
        Ok = true; Graph = graph; Error = string.Empty; CyclePath = Array.Empty<ushort>();
    }

    internal ConstraintGraphResult(string error, ushort[]? cycle = null)
    {
        Ok = false; Graph = null; Error = error; CyclePath = cycle ?? Array.Empty<ushort>();
    }
}

/// <summary>
/// 约束图构建器（手建/编译器共用的封图逻辑）。声明序随意——<see cref="Seal"/> 做拓扑排序；
/// 分层拒环的「层」= 每图一个组件作用域（关系不跨组件边界，编辑器语义保证）：
/// 层内环在这里拒绝并输出路径；层间的表观环（子撑父尺寸 × 子绑父边）不进同一张图，
/// 由 P5 四层串行 +「绑父轴不计包围」断开——按裸节点全树图拒环会误杀大量合法版式（架构机制⑩）。
/// </summary>
public sealed class ConstraintGraphBuilder
{
    private readonly int _nodeCount;
    private readonly List<ConstraintOp> _ops = new List<ConstraintOp>();

    /// <summary>建图（<paramref name="nodeCount"/> 含局部 0 = 容器）。</summary>
    public ConstraintGraphBuilder(int nodeCount)
    {
        UiAssert.That(nodeCount >= 2 && nodeCount <= ushort.MaxValue, "约束图节点数须在 [2, 65535]");
        _nodeCount = nodeCount;
    }

    /// <summary>已声明的算子数。</summary>
    public int OpCount => _ops.Count;

    /// <summary>声明一条 Follow（dst.edge 跟随 src.edge；percent 见 <see cref="ConstraintKind.FollowPercent"/>）。</summary>
    public void Follow(ushort dst, EdgeSel dstEdge, ushort src, EdgeSel srcEdge, LayoutAxis axis, bool percent = false)
    {
        _ops.Add(new ConstraintOp
        {
            SrcNode = src,
            DstNode = dst,
            Kind = (byte)(percent ? ConstraintKind.FollowPercent : ConstraintKind.FollowDelta),
            AxisEdges = ConstraintOp.Pack(axis, dstEdge, srcEdge, false),
        });
    }

    /// <summary>声明一条 Pin（dst.edge 钉在捕获值；Ext 类关系的对边半边）。</summary>
    public void Pin(ushort dst, EdgeSel edge, LayoutAxis axis)
    {
        _ops.Add(new ConstraintOp
        {
            SrcNode = dst,           // 无源：置同 dst，FanOut 不为它建边
            DstNode = dst,
            Kind = (byte)ConstraintKind.Pin,
            AxisEdges = ConstraintOp.Pack(axis, edge, edge, false),
        });
    }

    /// <summary>
    /// 封图：校验 → 分组（(dstNode, axis) 每组 ≤2 算子、边不重）→ 组间依赖拓扑排序（Kahn，
    /// 按组创建序取零入度 ⇒ 输出确定）→ 环拒绝（带路径）→ 发射拓扑序算子表 + FanOut CSR + 归属掩码。
    /// </summary>
    public ConstraintGraphResult Seal()
    {
        int n = _ops.Count;

        // ── 校验与分组 ──────────────────────────────────────────────────────
        var groupOf = new Dictionary<uint, int>();          // (dst<<1|axis) → 组号
        var groupOps = new List<List<int>>();               // 组号 → 声明序算子下标
        for (int i = 0; i < n; i++)
        {
            ConstraintOp op = _ops[i];
            if (op.DstNode == 0)
                return new ConstraintGraphResult("算子 " + i + "：dst 不允许为容器自身（局部 0）——容器由外层图或 authored 定形");
            if (op.DstNode >= _nodeCount || op.SrcNode >= _nodeCount)
                return new ConstraintGraphResult("算子 " + i + "：节点局部索引越界");
            if (op.Kind != (byte)ConstraintKind.Pin && op.SrcNode == op.DstNode)
                return new ConstraintGraphResult(
                    "CycleRejected：自环 " + op.DstNode + " → " + op.DstNode,
                    new[] { op.DstNode, op.DstNode });

            uint key = ((uint)op.DstNode << 1) | (uint)op.Axis;
            if (!groupOf.TryGetValue(key, out int g))
            {
                g = groupOps.Count;
                groupOf.Add(key, g);
                groupOps.Add(new List<int>(2));
            }
            List<int> members = groupOps[g];
            if (members.Count == 2)
                return new ConstraintGraphResult("节点 " + op.DstNode + " 的 " + op.Axis + " 轴被绑标量超过 2 个（每轴 0~2：1=平移，2=拉伸）");
            if (members.Count == 1 && _ops[members[0]].DstEdge == op.DstEdge)
                return new ConstraintGraphResult("节点 " + op.DstNode + " 的 " + op.Axis + "." + op.DstEdge + " 被声明了两次（同边双写没有次序语义）");
            members.Add(i);
        }

        int groupCount = groupOps.Count;

        // ── 组间依赖（src 也是某组的 dst ⇒ 那组先求值）────────────────────────
        var edges = new List<int>[groupCount];              // g → 后继组
        int[] indeg = new int[groupCount];
        for (int g = 0; g < groupCount; g++) edges[g] = new List<int>(2);
        for (int g = 0; g < groupCount; g++)
        {
            List<int> members = groupOps[g];
            for (int k = 0; k < members.Count; k++)
            {
                ConstraintOp op = _ops[members[k]];
                if (op.Kind == (byte)ConstraintKind.Pin) continue;
                uint srcKey = ((uint)op.SrcNode << 1) | (uint)op.Axis;
                if (!groupOf.TryGetValue(srcKey, out int h) || h == g) continue;
                if (!edges[h].Contains(g)) { edges[h].Add(g); indeg[g]++; }
            }
        }

        // ── Kahn（零入度按组创建序取 ⇒ 声明打乱不改输出的组内序，输出序由依赖 + 创建序共同钉住）──
        int[] order = new int[groupCount];
        bool[] done = new bool[groupCount];
        int emitted = 0;
        while (emitted < groupCount)
        {
            int pick = -1;
            for (int g = 0; g < groupCount; g++)
                if (!done[g] && indeg[g] == 0) { pick = g; break; }
            if (pick < 0) break;                            // 剩余全部有入度 = 有环
            done[pick] = true;
            order[emitted++] = pick;
            List<int> outs = edges[pick];
            for (int k = 0; k < outs.Count; k++) indeg[outs[k]]--;
        }

        if (emitted < groupCount)
            return new ConstraintGraphResult(BuildCycleError(groupOps, edges, done, out ushort[] path), path);

        // ── 发射：拓扑序算子表（组内按声明序、成对相邻并链 Next）────────────────
        var ops = new ConstraintOp[n];
        int w = 0;
        for (int t = 0; t < groupCount; t++)
        {
            List<int> members = groupOps[order[t]];
            int head = w;
            for (int k = 0; k < members.Count; k++) ops[w++] = _ops[members[k]];
            if (members.Count == 2)
            {
                ops[head].Next = (ushort)(head + 2);        // 伙伴下标 + 1
                ops[head + 1].Next = (ushort)(head + 1);
            }
        }

        // ── FanOut CSR（Pin 无源不进桶；桶内升序 = 拓扑序）───────────────────
        var counts = new int[_nodeCount];
        for (int i = 0; i < w; i++)
            if (ops[i].Kind != (byte)ConstraintKind.Pin) counts[ops[i].SrcNode]++;
        var fan = new FanOut[_nodeCount];
        int total = 0;
        for (int s = 0; s < _nodeCount; s++)
        {
            fan[s].Start = (ushort)total;
            total += counts[s];
        }
        var fanOps = new ushort[total];
        for (int i = 0; i < w; i++)
        {
            if (ops[i].Kind == (byte)ConstraintKind.Pin) continue;
            int s = ops[i].SrcNode;
            fanOps[fan[s].Start + fan[s].Count++] = (ushort)i;
        }

        // ── 归属掩码 ────────────────────────────────────────────────────────
        var masks = new byte[_nodeCount];
        for (int i = 0; i < w; i++)
        {
            ConstraintOp op = ops[i];
            bool x = op.Axis == LayoutAxis.X;
            masks[op.DstNode] |= op.DstEdge == EdgeSel.Size
                ? (x ? LayoutOwn.SizeX : LayoutOwn.SizeY)
                : (x ? LayoutOwn.PosX : LayoutOwn.PosY);
            if (op.Next != 0)                               // 两标量 = 拉伸：位置与尺寸都归本图
                masks[op.DstNode] |= x ? (byte)(LayoutOwn.PosX | LayoutOwn.SizeX) : (byte)(LayoutOwn.PosY | LayoutOwn.SizeY);
            if (op.Kind != (byte)ConstraintKind.Pin && op.SrcNode == 0)
                masks[op.DstNode] |= x ? LayoutOwn.BoundRootX : LayoutOwn.BoundRootY;
        }

        return new ConstraintGraphResult(new ConstraintGraph(ops, fan, fanOps, _nodeCount, masks));
    }

    /// <summary>从未出队的组里沿前驱走出一条环，输出局部节点路径（首尾闭合）。</summary>
    private string BuildCycleError(List<List<int>> groupOps, List<int>[] edges, bool[] done, out ushort[] path)
    {
        int groupCount = groupOps.Count;
        // 反向邻接（只看未完成组）
        var pred = new int[groupCount];
        for (int g = 0; g < groupCount; g++) pred[g] = -1;
        for (int g = 0; g < groupCount; g++)
        {
            if (done[g]) continue;
            List<int> outs = edges[g];
            for (int k = 0; k < outs.Count; k++)
                if (!done[outs[k]] && pred[outs[k]] < 0) pred[outs[k]] = g;
        }
        int start = -1;
        for (int g = 0; g < groupCount; g++)
            if (!done[g]) { start = g; break; }

        // 沿前驱走 groupCount 步必入环；再走一圈收集环
        int cur = start;
        for (int steps = 0; steps < groupCount; steps++) cur = pred[cur];
        var cycle = new List<ushort>();
        int c = cur;
        do
        {
            cycle.Add(_ops[groupOps[c][0]].DstNode);
            c = pred[c];
        } while (c != cur);
        cycle.Reverse();
        cycle.Add(cycle[0]);                                // 闭合
        path = cycle.ToArray();
        return "CycleRejected：层内环 " + string.Join(" → ", path);
    }
}
