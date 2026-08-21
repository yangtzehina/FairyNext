using FairyNext.Contracts;
using FairyNext.Numerics;

namespace FairyNext.Core.Layout;

/// <summary>
/// 文本度量接缝（架构平面四 A 跨平面接缝原文：<c>float2 ITextMeasure.Measure(NodeHandle, float availWidth)</c>）。
/// 仅 P5 度量层调用、只量不出网格；实现随 M1-18 TextCore 进驻，本包只钉签名。
/// </summary>
public interface ITextMeasure
{
    /// <summary>量一个文本节点在给定可用宽度下的排版尺寸（纯函数、跨设备 bit-identical）。</summary>
    Vector2 Measure(NodeHandle node, float availWidth);
}

/// <summary>
/// 布局诊断面（架构平面四 A 诊断接缝的 <c>LayoutStats</c>；itemsRendered/itemsReused/compensations
/// 三项归 M2-07 虚拟列表）。计数为会话累计，<see cref="DrainPasses"/> 与 <see cref="PendingWork"/> 为帧级。
/// </summary>
public sealed class LayoutStats
{
    /// <summary>求过值的约束算子数（会话累计）。</summary>
    public long OpsEvaluated;
    /// <summary>内容包围重算次数（parentUsesSize 容器，会话累计）。</summary>
    public long BoundsRecomputed;
    /// <summary>流式布局摆放的子节点数（会话累计）。</summary>
    public long LinearPlaced;
    /// <summary>offset/ratio 重捕获次数（用户/gear/transition 写 authored 触发，会话累计）。</summary>
    public long Recaptures;
    /// <summary>本帧微排水轮次（0 = 主四层一遍收敛；&gt;0 = 反向依赖收敛用了兜底轮）。</summary>
    public int DrainPasses;
    /// <summary>帧末仍有余活（超限截停的「不丢、只延迟」半边：脏位留存，下一帧 P5 继续）。</summary>
    public bool PendingWork;
    /// <summary>微排水超限次数（不变量 9：&gt;<see cref="Abi.LayoutMicroDrainLimit"/> 轮 → 诊断告警 + 计数）。</summary>
    public long MicroDrainOverflows;
    /// <summary>P5 幂等断言失败次数（不变量 8）。</summary>
    public long IdempotenceFailures;
    /// <summary>布局差分神谕（L2）失败次数。</summary>
    public long DifferentialFailures;
    /// <summary>「绑父轴不计包围」触发次数（静态断环的迁移警告面，不变量 15 尾句）。</summary>
    public long ParentAxisBoundSkips;
    /// <summary>流式子节点迟到分配 resolved 槽的次数（编译期槽集合由 M1-20 定死后应归零）。</summary>
    public long LateSlotAllocs;
    /// <summary>最近一次超限的人读描述。</summary>
    public string LastOverflowNote = string.Empty;
    /// <summary>最近一次门失败的人读描述（幂等/差分共用）。</summary>
    public string LastGateError = string.Empty;
    /// <summary>本帧差分神谕因 <see cref="PendingWork"/> 跳过（超限帧两腿按契约不可比）。</summary>
    public bool LastDifferentialSkipped;

    internal void BeginFrame()
    {
        DrainPasses = 0;
        LastDifferentialSkipped = false;
    }
}

/// <summary>
/// P5 布局引擎（M1-16）：架构平面四 A「状态与布局」的布局半边——**resolved 列的唯一写者**。
///
/// 四层骨架（机制⑩，严格串行，每轮同序）：
///  1. **度量**：<see cref="TextMeasure"/> 接缝（M1-18 进驻，本包空层）；
///  2. **包围**：parentUsesSize 容器按树深自底向上重算内容包围并自撑（「绑父轴不计包围」静态断环）；
///  3. **约束**：置位算子按索引序（=拓扑序）单遍求值，<c>dst.resolvedEdge = f(src.resolvedEdge, offset)</c>，
///     写 resolved 经 FanOut 只向前置位（机制⑨，回环在类型上不可能）；
///  4. **流式**：LinearLayout 摆放（水平/垂直 + wrap）。
/// 四层之后是受控窗兜底：反向依赖（子定父尺寸 → 父再定子）在 ≤<see cref="Abi.LayoutMicroDrainLimit"/>
/// 轮微排水内收敛；超限**有声**——诊断告警 + 计数 + 余活留到下一帧（不丢、只延迟），不死循环也不静默截断。
///
/// 写者纪律（全局裁决）：本引擎只经 <see cref="NodeTable.SetResolved"/> 写 resolved（P5 相位门 +
/// WriteSource.Layout 双检），**永不写 authored**；gear/transition/tween 只碰 authored——它们在类型上
/// 没有 resolved 写口。M1-09 的归属表管 authored 侧的通道封闭，resolved 侧的写门就是这条运行期
/// WriteSource 分流，两者是同一形态的两半，不另立第三种。
///
/// 接线（接管 M1-14 的 LayoutStub 接缝）：
///  - 消费 <see cref="Ch.Layout"/> 队列（宽高 authored 写）；无 resolved 槽的节点 authored 即真值，
///    只补派生 <c>Mark(Content|Transform, LayoutDerived)</c>——P6 的脏根窥视与 P7 的消化都靠它认领
///    「改宽高」这条路径（LayoutStub 的全部语义，原样保留）；
///  - **窥视**（不消费）<see cref="Ch.Transform"/> 队列取 authored 位置写：其消费权归 P7，
///    这里只借「谁被用户写了」这个事实做重捕获与脏传播（与 RenderPipeline.SettleStep 同一读法）；
///  - 真正的求解挂在 <see cref="UiKernel.LayoutStep"/>（P5 两条排水之后）。
///
/// 依赖前提：与渲染管线同挂——Transform 队列每帧被 P7 消费。若无消费者，本引擎 SetResolved 的
/// LayoutDerived 标记会跨帧滞留在队列里、下一帧被误读成用户写触发重捕获（布局无 Transform 队列的
/// reason 信息可辨）。测试里布局单跑时须注册吸收排水器。
/// </summary>
public sealed class LayoutEngine : IChannelDrain
{
    private readonly UiKernel _kernel;
    private readonly NodeTable _table;
    private readonly Invalidation _inval;
    private bool _attached;

    // ── 约束实例 ────────────────────────────────────────────────────────────
    private sealed class Instance
    {
        internal readonly ConstraintGraph G;
        internal readonly NodeHandle[] ByLocal;
        internal readonly float[] Offsets;      // 每算子一槽：Delta 偏移 / Percent ratio / Pin 钉值
        internal readonly ulong[] DirtyOps;     // 位集，索引 = 拓扑序

        internal Instance(ConstraintGraph g, NodeHandle[] byLocal)
        {
            G = g;
            ByLocal = byLocal;
            Offsets = new float[g.Ops.Length];
            DirtyOps = new ulong[(g.Ops.Length + 63) >> 6];
        }
    }

    private readonly struct Member
    {
        internal readonly Instance Inst;
        internal readonly ushort Local;
        internal Member(Instance inst, ushort local) { Inst = inst; Local = local; }
    }

    private readonly List<Instance> _instances = new List<Instance>();
    private readonly Dictionary<uint, List<Member>> _members = new Dictionary<uint, List<Member>>();

    // ── 流式容器 ────────────────────────────────────────────────────────────
    private sealed class LinearBox
    {
        internal NodeHandle Container;
        internal uint Index;
        internal LinearLayoutDesc Desc;
        internal int Depth;
        internal bool FlowDirty;
        internal bool ExtentDirty;
    }

    private readonly List<LinearBox> _boxes = new List<LinearBox>();          // 按 Depth 降序（包围层自底向上）
    private readonly Dictionary<uint, LinearBox> _boxByIndex = new Dictionary<uint, LinearBox>();

    // ── 槽与归属 ────────────────────────────────────────────────────────────
    private readonly Dictionary<uint, byte> _ownMask = new Dictionary<uint, byte>();  // 布局内部的分量归属
    private readonly List<uint> _slotNodes = new List<uint>();
    private readonly HashSet<uint> _slotSet = new HashSet<uint>();

    // ── 帧内收集 ────────────────────────────────────────────────────────────
    private readonly List<NodeHandle> _pendingAuthored = new List<NodeHandle>();
    private NodeHandle[] _peekCopy = new NodeHandle[64];

    // ── 差分神谕的旁缓冲（全量腿写这里，绝不写活列——神谕不写它要检查的列）────
    private bool _scratchMode;
    private readonly Dictionary<uint, int> _scratchOrd = new Dictionary<uint, int>();
    private float[] _sx = Array.Empty<float>();
    private float[] _sy = Array.Empty<float>();
    private float[] _sw = Array.Empty<float>();
    private float[] _sh = Array.Empty<float>();

    private int _writeChanges;                  // 计数窗口内 WriteGeom 的实际变化数
    private uint _firstChangeIndex = NodeTable.NoIndex;

    // ── 负例注入口（仅测试；产品路径恒中性）───────────────────────────────────
    /// <summary>差分神谕负例：该下标节点的几何变化不做 FanOut 脏传播（增量腿漏一个依赖）。</summary>
    internal uint TestMuteFanOutIndex = NodeTable.NoIndex;
    /// <summary>幂等门负例：实例 0 的该算子写「当前值 + 1」（不收敛值；-1 = 关闭）。</summary>
    internal int TestAccumulateOpIndex = -1;

    /// <summary>诊断面。</summary>
    public LayoutStats Stats { get; } = new LayoutStats();

    /// <summary>
    /// P5 幂等断言开关（不变量 8，DerivedOracle 同款帧级诊断门）：收敛帧末把全部算子/容器
    /// 再解一遍，resolved 必须零变化（同 authored + 同约束图 ⇒ bit-identical）。
    /// 失败计 <see cref="LayoutStats.IdempotenceFailures"/> 并指名首个变化节点。
    /// </summary>
    public bool IdempotenceGate { get; set; }

    /// <summary>
    /// 布局差分神谕自动开关（L2）：收敛帧末跑一次 <see cref="CheckDifferential"/>，
    /// 失败计 <see cref="LayoutStats.DifferentialFailures"/>。
    /// </summary>
    public bool DifferentialGate { get; set; }

    /// <summary>文本度量接缝（M1-18 装填；null = 度量层空转）。</summary>
    public ITextMeasure? TextMeasure { get; set; }

    /// <summary>已布防的约束实例数。</summary>
    public int InstanceCount => _instances.Count;

    /// <summary>本引擎分配过 resolved 槽的节点数。</summary>
    public int SlotNodeCount => _slotNodes.Count;

    /// <summary>接一个内核（树域与失效协议从内核取）。</summary>
    public LayoutEngine(UiKernel kernel)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _table = kernel.Table;
        _inval = kernel.Invalidation;
    }

    /// <summary>消费 Layout 通道（IChannelDrain 面；Text 通道归 M1-18 的文本系统）。</summary>
    public Ch Consumes => Ch.Layout;

    /// <summary>
    /// 装钩子 + 注册排水器。占用检查是**无条件硬检查**（M1-14b 裁决同款）：接线错误不是
    /// 运行期可吸收的状态，release 也拦。换引擎先 <see cref="Detach"/>。
    /// </summary>
    /// <exception cref="InvalidOperationException">本实例已 Attach，或内核 P5 布局钩子已被占用。</exception>
    public void Attach()
    {
        if (_attached)
            throw new InvalidOperationException("LayoutEngine 重复 Attach（重接前先 Detach）");
        if (_kernel.LayoutStep != null)
            throw new InvalidOperationException(
                "UiKernel 的 P5 布局钩子已被占用：一个内核同时只能接一个 LayoutEngine——"
                + "静默覆盖会让先接引擎的余活与门全部停摆。先对旧引擎调 Detach。");
        _kernel.LayoutStep = Step;
        _inval.Register(this);
        _attached = true;
    }

    /// <summary>卸线（测试拆装用；实例与槽登记不动）。</summary>
    public void Detach()
    {
        if (!_attached) return;
        _kernel.LayoutStep = null;
        _inval.Unregister(this);
        _attached = false;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 布防（arm）：约束实例与流式容器登记。
    // 槽集合的「编译期定死 + 实例化批量分配」归 M1-20/M1-22；手建路径在布防时分配。
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 布防一个约束图实例。offset/ratio 按架构原文在此捕获：**从 authored 捕获 dst 边、
    /// 从 resolved 读 src 边**（offset = authoredEdge − target.resolvedEdge）。
    /// 全部算子入队初解（首个 P5 把 percent 等式归一到求值形态——幂等断言的前提：
    /// <c>(e−min)/size×size+min</c> 在 float 上不等于 <c>e</c>，authored 原值不是不动点）。
    /// </summary>
    /// <returns>实例序号。</returns>
    public int Arm(ConstraintGraph graph, ReadOnlySpan<NodeHandle> byLocal)
    {
        if (graph == null) throw new ArgumentNullException(nameof(graph));
        UiAssert.That(byLocal.Length == graph.NodeCount, "Arm：byLocal 长度必须等于图的 NodeCount");

        var inst = new Instance(graph, byLocal.ToArray());
        for (int local = 0; local < graph.NodeCount; local++)
        {
            if (!_table.TryResolve(inst.ByLocal[local], out uint idx))
            {
                UiAssert.That(false, "Arm：局部 " + local + " 的句柄已失效");
                continue;
            }
            byte mask = graph.NodeMasks[local];
            if ((mask & 0x0F) != 0)
            {
                EnsureSlot(inst.ByLocal[local], idx);
                MergeOwn(idx, (byte)(mask & 0x0F));
            }
            if (!_members.TryGetValue(idx, out List<Member>? list))
            {
                list = new List<Member>(1);
                _members.Add(idx, list);
            }
            list.Add(new Member(inst, (ushort)local));
        }

        for (int i = 0; i < graph.Ops.Length; i++)
        {
            CaptureOp(inst, i);
            inst.DirtyOps[i >> 6] |= 1ul << (i & 63);
        }
        _instances.Add(inst);
        return _instances.Count - 1;
    }

    /// <summary>
    /// 登记一个流式容器。子节点的位置归流式层（PosX|PosY），parentUsesSize 时容器自撑轴的尺寸
    /// 归包围层；现有子节点在此分配 resolved 槽（此后结构性加子走迟到分配 + 计数）。
    /// </summary>
    public void RegisterLinear(NodeHandle container, in LinearLayoutDesc desc)
    {
        if (!_table.TryResolve(container, out uint idx))
        {
            UiAssert.That(false, "RegisterLinear：容器句柄已失效");
            return;
        }
        UiAssert.That(!_boxByIndex.ContainsKey(idx), "RegisterLinear：容器重复登记");
        if (_boxByIndex.ContainsKey(idx)) return;

        int depth = 0;
        for (uint p = _table.ParentIndex(idx); p != NodeTable.NoIndex; p = _table.ParentIndex(p)) depth++;

        var box = new LinearBox
        {
            Container = container,
            Index = idx,
            Desc = desc,
            Depth = depth,
            FlowDirty = true,
            ExtentDirty = desc.ParentUsesSize,
        };
        byte auto = desc.AutoSizeMask();
        if (auto != 0)
        {
            EnsureSlot(container, idx);
            MergeOwn(idx, auto);
        }
        for (NodeHandle c = _table.FirstChild(container); !c.IsNone; c = _table.NextSibling(c))
        {
            if (!_table.TryResolve(c, out uint ci)) continue;
            EnsureSlot(c, ci);
            MergeOwn(ci, (byte)(LayoutOwn.PosX | LayoutOwn.PosY));
        }

        int at = _boxes.Count;                       // Depth 降序插入：包围层自底向上一遍就位
        while (at > 0 && _boxes[at - 1].Depth < depth) at--;
        _boxes.Insert(at, box);
        _boxByIndex.Add(idx, box);
    }

    private void EnsureSlot(NodeHandle h, uint idx)
    {
        if (_slotSet.Contains(idx)) return;
        if (_table.ResolvedRef(h) == 0) _table.AllocResolvedSlot(h);
        _slotSet.Add(idx);
        _slotNodes.Add(idx);
    }

    private void MergeOwn(uint idx, byte bits)
    {
        _ownMask.TryGetValue(idx, out byte cur);
        _ownMask[idx] = (byte)(cur | bits);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 接线面：Ch.Layout 排水（簿记 + LayoutStub 语义）与 P5 求解钩子
    // ────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Drain(ref FrameContext ctx, Ch channel, ReadOnlySpan<NodeHandle> queue)
    {
        for (int i = 0; i < queue.Length; i++)
        {
            NodeHandle h = queue[i];
            if (!_table.TryResolve(h, out uint idx)) continue;
            // 无槽节点 authored 即真值：宽高变化的视觉落地要走 Content/Transform，
            // 这次派生 Mark 是「改宽高」路径在 P6/P7 的唯一认领人（LayoutStub 语义原样保留）。
            if (_table.ResolvedRef(h) == 0)
                ctx.Invalidation.Mark(h, Ch.Content | Ch.Transform, InvalidateReason.LayoutDerived);
            if (IsTracked(idx)) _pendingAuthored.Add(h);
        }
    }

    private bool IsTracked(uint idx)
    {
        if (_members.ContainsKey(idx) || _boxByIndex.ContainsKey(idx) || _slotSet.Contains(idx)) return true;
        uint parent = _table.ParentIndex(idx);
        return parent != NodeTable.NoIndex && _boxByIndex.ContainsKey(parent);
    }

    /// <summary>P5 求解（<see cref="UiKernel.LayoutStep"/>；Text/Layout 两条排水之后、P6 之前）。</summary>
    private void Step(ref FrameContext ctx)
    {
        Stats.BeginFrame();

        // 层 1：度量（M1-18 进驻；TextMeasure==null 时空层，Text 通道由文本系统自己消费）。

        // authored 变化收集：Layout 排水缓冲 + Transform 队列窥视（消费权归 P7，只借事实）。
        int peeked = CopyPeek(Ch.Transform);
        for (int i = 0; i < peeked; i++)
        {
            if (_table.TryResolve(_peekCopy[i], out uint idx) && IsTracked(idx))
                AuthoredChanged(_peekCopy[i], idx);
        }
        for (int i = 0; i < _pendingAuthored.Count; i++)
        {
            if (_table.TryResolve(_pendingAuthored[i], out uint idx))
                AuthoredChanged(_pendingAuthored[i], idx);
        }
        _pendingAuthored.Clear();

        // 层 2-4 + 受控窗兜底轮次。
        RunRounds();

        // 帧级门（超限帧不判：余活按契约留给下一帧，此刻两腿/两遍按定义不可比——
        // CheckDifferential 自带跳过并把 LastDifferentialSkipped 立起来，跳过是有账的）。
        if (IdempotenceGate && !Stats.PendingWork) RunIdempotenceCheck();
        if (DifferentialGate && !CheckDifferential(out string err))
        {
            Stats.DifferentialFailures++;
            Stats.LastGateError = err;
        }
    }

    private int CopyPeek(Ch channel)
    {
        ReadOnlySpan<NodeHandle> queue = _inval.Peek(channel);
        if (queue.Length > _peekCopy.Length)
        {
            int cap = _peekCopy.Length;
            while (cap < queue.Length) cap *= 2;
            _peekCopy = new NodeHandle[cap];
        }
        queue.CopyTo(_peekCopy);                     // 先拷走：随后的 SetResolved 会 Mark（环形缓冲可能搬家）
        return queue.Length;
    }

    /// <summary>
    /// authored 写落到布局参与节点（机制⑨ 的入口半边）：
    ///  ① 有槽节点把**非布局归属**的分量从 authored 刷进 resolved（用户写非受控分量立即生效）；
    ///  ② 它作为 dst 的算子**重捕获** offset/ratio（WriteSource∈{User,Gear,Transition} 的架构原文——
    ///     布局自己从不写 authored，故队列里看到的 authored 写恒属此类）并重新入解；
    ///  ③ 按 FanOut 把它作为 src 的算子置脏、把所在流式容器置脏（静态超集：多算一轮小，漏算错帧）。
    /// </summary>
    private void AuthoredChanged(NodeHandle h, uint idx)
    {
        _ownMask.TryGetValue(idx, out byte own);
        if (_slotSet.Contains(idx))
        {
            _table.AuthoredAt(idx, out float ax, out float ay, out float aw, out float ah);
            _table.ResolvedAt(idx, out float rx, out float ry, out float rw, out float rh);
            float nx = (own & LayoutOwn.PosX) != 0 ? rx : ax;
            float ny = (own & LayoutOwn.PosY) != 0 ? ry : ay;
            float nw = (own & LayoutOwn.SizeX) != 0 ? rw : aw;
            float nh = (own & LayoutOwn.SizeY) != 0 ? rh : ah;
            WriteGeom(idx, nx, ny, nw, nh, out bool moved, out bool sized);
            _ = moved; _ = sized;                    // 变化事实由下方保守 Touch 统一传播
        }
        if (_members.TryGetValue(idx, out List<Member>? list))
        {
            for (int m = 0; m < list.Count; m++)
            {
                Instance inst = list[m].Inst;
                ushort local = list[m].Local;
                ConstraintOp[] ops = inst.G.Ops;
                for (int i = 0; i < ops.Length; i++)
                {
                    if (ops[i].DstNode != local) continue;
                    CaptureOp(inst, i);
                    inst.DirtyOps[i >> 6] |= 1ul << (i & 63);
                    Stats.Recaptures++;
                }
            }
        }
        Touch(idx, moved: true, sized: true);        // 保守超集：authored 写视为几何双变
    }

    /// <summary>
    /// 几何变化的脏传播（机制⑨ 的传播半边）：FanOut 置位读它的算子；尺寸变化置本容器
    /// （wrap 宽变）与父容器的流式脏；任何几何变化置父容器（parentUsesSize 者）的包围脏。
    /// parentUsesSize=false 的容器不设包围脏——**子几何脏不向父上溯**（layout 通道的上溯剪断条件）。
    /// </summary>
    private void Touch(uint idx, bool moved, bool sized)
    {
        if (!moved && !sized) return;
        if (idx != TestMuteFanOutIndex && _members.TryGetValue(idx, out List<Member>? list))
        {
            for (int m = 0; m < list.Count; m++)
            {
                Instance inst = list[m].Inst;
                FanOut fan = inst.G.FanOutBySrc[list[m].Local];
                for (int k = 0; k < fan.Count; k++)
                {
                    int op = inst.G.FanOpIndices[fan.Start + k];
                    inst.DirtyOps[op >> 6] |= 1ul << (op & 63);
                }
            }
        }
        if (sized && _boxByIndex.TryGetValue(idx, out LinearBox? own)) own.FlowDirty = true;
        uint parent = _table.ParentIndex(idx);
        if (parent != NodeTable.NoIndex && _boxByIndex.TryGetValue(parent, out LinearBox? pbox))
        {
            if (sized) pbox.FlowDirty = true;
            if (pbox.Desc.ParentUsesSize) pbox.ExtentDirty = true;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 轮次与三个执行层
    // ────────────────────────────────────────────────────────────────────────

    private void RunRounds()
    {
        RunPass(full: false);                        // 主四层一遍（正常帧到此收敛）
        int micro = 0;
        while (HasWork())
        {
            if (micro == Abi.LayoutMicroDrainLimit)
            {
                Stats.MicroDrainOverflows++;
                Stats.LastOverflowNote = "P5 微排水超限（>" + Abi.LayoutMicroDrainLimit
                    + " 轮）：反向依赖链未在受控窗内收敛，余活留到下一帧（不丢、只延迟）";
                break;
            }
            micro++;
            RunPass(full: false);
        }
        Stats.DrainPasses = micro;
        Stats.PendingWork = HasWork();
    }

    private bool HasWork()
    {
        for (int i = 0; i < _instances.Count; i++)
        {
            ulong[] words = _instances[i].DirtyOps;
            for (int w = 0; w < words.Length; w++)
                if (words[w] != 0) return true;
        }
        for (int b = 0; b < _boxes.Count; b++)
        {
            LinearBox box = _boxes[b];
            if (box.FlowDirty || (box.ExtentDirty && box.Desc.ParentUsesSize)) return true;
        }
        return false;
    }

    /// <summary>一遍三层（full=true：全量遍历、不碰脏簿记——幂等门与差分全量腿共用）。</summary>
    private void RunPass(bool full)
    {
        BoundsPass(full);
        ConstraintPass(full);
        FlowPass(full);
    }

    private void BoundsPass(bool full)
    {
        for (int b = 0; b < _boxes.Count; b++)       // 列表按 Depth 降序 = 自底向上
        {
            LinearBox box = _boxes[b];
            if (!box.Desc.ParentUsesSize) continue;
            if (!full)
            {
                if (!box.ExtentDirty) continue;
                box.ExtentDirty = false;
            }
            if (!_table.TryResolve(box.Container, out _)) continue;
            ComputeExtent(box, out float ex, out float ey);
            byte auto = box.Desc.AutoSizeMask();
            GeomOf(box.Index, out float x, out float y, out float w, out float h);
            float nw = (auto & LayoutOwn.SizeX) != 0 ? ex : w;
            float nh = (auto & LayoutOwn.SizeY) != 0 ? ey : h;
            WriteGeom(box.Index, x, y, nw, nh, out bool moved, out bool sized);
            Stats.BoundsRecomputed++;
            if (!_scratchMode) Touch(box.Index, moved, sized);
        }
    }

    /// <summary>
    /// 内容包围（右/下扩展量，原点计 0）。「绑父轴不计包围」：该轴绑定到容器（局部 0）的子节点
    /// 不计入该轴包围——「尺寸依赖内容、内容依赖布局」的环在此静态断开（另一轴照常计入）。
    /// </summary>
    private void ComputeExtent(LinearBox box, out float ex, out float ey)
    {
        ex = 0f; ey = 0f;
        for (NodeHandle c = _table.FirstChild(box.Container); !c.IsNone; c = _table.NextSibling(c))
        {
            if (!_table.TryResolve(c, out uint ci)) continue;
            GeomOf(ci, out float x, out float y, out float w, out float h);
            bool skipX = false, skipY = false;
            if (_members.TryGetValue(ci, out List<Member>? list))
            {
                for (int m = 0; m < list.Count; m++)
                {
                    if (!list[m].Inst.ByLocal[0].Equals(box.Container)) continue;
                    byte mask = list[m].Inst.G.NodeMasks[list[m].Local];
                    if ((mask & LayoutOwn.BoundRootX) != 0) skipX = true;
                    if ((mask & LayoutOwn.BoundRootY) != 0) skipY = true;
                }
            }
            if (!skipX) ex = MathF.Max(ex, x + w);
            else Stats.ParentAxisBoundSkips++;
            if (!skipY) ey = MathF.Max(ey, y + h);
            else Stats.ParentAxisBoundSkips++;
        }
    }

    private void ConstraintPass(bool full)
    {
        for (int ii = 0; ii < _instances.Count; ii++)
        {
            Instance inst = _instances[ii];
            ConstraintOp[] ops = inst.G.Ops;
            for (int i = 0; i < ops.Length; i++)
            {
                if (full)
                {
                    if (ops[i].Next != 0 && ops[i].Next - 1 < i) continue;    // 伙伴由链头求值
                    EvalGroup(inst, ii, i);
                    continue;
                }
                if ((inst.DirtyOps[i >> 6] & (1ul << (i & 63))) == 0) continue;
                int head = i;
                if (ops[i].Next != 0 && ops[i].Next - 1 < i) head = ops[i].Next - 1;
                // 消费即清（成对同清：一组一次求值）
                inst.DirtyOps[head >> 6] &= ~(1ul << (head & 63));
                if (ops[head].Next != 0)
                {
                    int partner = ops[head].Next - 1;
                    inst.DirtyOps[partner >> 6] &= ~(1ul << (partner & 63));
                }
                EvalGroup(inst, ii, head);
            }
        }
    }

    /// <summary>求值一组（1 算子 = 平移/改尺寸；2 算子 = 拉伸，两标量解 (pos, size) 线性方程）。</summary>
    private void EvalGroup(Instance inst, int instOrd, int head)
    {
        ConstraintOp op = inst.G.Ops[head];
        if (!_table.TryResolve(inst.ByLocal[op.DstNode], out uint dstIdx)) return;
        bool xAxis = op.Axis == LayoutAxis.X;

        GeomOf(dstIdx, out float gx, out float gy, out float gw, out float gh);
        float pos = xAxis ? gx : gy;
        float size = xAxis ? gw : gh;

        int partner = op.Next != 0 ? op.Next - 1 : -1;
        float npos, nsize;
        if (partner < 0)
        {
            float v = TargetValue(inst, head);
            switch (op.DstEdge)
            {
                case EdgeSel.Size: npos = pos; nsize = v; break;
                case EdgeSel.Min: npos = v; nsize = size; break;
                case EdgeSel.Center: npos = v - size * 0.5f; nsize = size; break;
                default: npos = v - size; nsize = size; break;      // Max
            }
            Stats.OpsEvaluated++;
        }
        else
        {
            float v1 = TargetValue(inst, head);
            float v2 = TargetValue(inst, partner);
            RowOf(op.DstEdge, out float a1, out float b1);
            RowOf(inst.G.Ops[partner].DstEdge, out float a2, out float b2);
            float det = a1 * b2 - a2 * b1;                          // 边不重 ⇒ det ≠ 0（Seal 校验）
            npos = (v1 * b2 - v2 * b1) / det;
            nsize = (a1 * v2 - a2 * v1) / det;
            Stats.OpsEvaluated += 2;
        }

        // 幂等门负例注入：不收敛写（当前值 + 1）。产品路径 TestAccumulateOpIndex 恒 -1（关闭）。
        if (TestAccumulateOpIndex >= 0 && !_scratchMode && instOrd == 0
            && (head == TestAccumulateOpIndex || partner == TestAccumulateOpIndex))
            npos = pos + 1f;

        float nx = xAxis ? npos : gx;
        float ny = xAxis ? gy : npos;
        float nw = xAxis ? nsize : gw;
        float nh = xAxis ? gh : nsize;
        WriteGeom(dstIdx, nx, ny, nw, nh, out bool moved, out bool sized);
        if (!_scratchMode) Touch(dstIdx, moved, sized);
    }

    private static void RowOf(EdgeSel edge, out float a, out float b)
    {
        switch (edge)
        {
            case EdgeSel.Min: a = 1f; b = 0f; break;
            case EdgeSel.Center: a = 1f; b = 0.5f; break;
            case EdgeSel.Max: a = 1f; b = 1f; break;
            default: a = 0f; b = 1f; break;                         // Size
        }
    }

    private float TargetValue(Instance inst, int opIdx)
    {
        ConstraintOp op = inst.G.Ops[opIdx];
        switch ((ConstraintKind)op.Kind)
        {
            case ConstraintKind.Pin:
                return inst.Offsets[opIdx];
            case ConstraintKind.FollowDelta:
                return ReadSrcEdge(inst, op.SrcNode, op.Axis, op.SrcEdge) + inst.Offsets[opIdx];
            default:                                                // FollowPercent：锚 = src 起点，比例 = ratio
            {
                float min = ReadSrcEdge(inst, op.SrcNode, op.Axis, EdgeSel.Min);
                float size = ReadSrcEdge(inst, op.SrcNode, op.Axis, EdgeSel.Size);
                return op.DstEdge == EdgeSel.Size
                    ? size * inst.Offsets[opIdx]
                    : min + inst.Offsets[opIdx] * size;
            }
        }
    }

    /// <summary>读 src 标量。局部 0 = 容器自身：其边在**子坐标系**里是 (0, size)（关系不跨组件边界）。</summary>
    private float ReadSrcEdge(Instance inst, ushort local, LayoutAxis axis, EdgeSel edge)
    {
        if (!_table.TryResolve(inst.ByLocal[local], out uint idx)) return 0f;
        GeomOf(idx, out float x, out float y, out float w, out float h);
        float size = axis == LayoutAxis.X ? w : h;
        if (local == 0)
        {
            switch (edge)
            {
                case EdgeSel.Min: return 0f;
                case EdgeSel.Center: return size * 0.5f;
                default: return size;                               // Max 与 Size 同值
            }
        }
        float pos = axis == LayoutAxis.X ? x : y;
        switch (edge)
        {
            case EdgeSel.Min: return pos;
            case EdgeSel.Center: return pos + size * 0.5f;
            case EdgeSel.Max: return pos + size;
            default: return size;
        }
    }

    private void FlowPass(bool full)
    {
        for (int b = 0; b < _boxes.Count; b++)
        {
            LinearBox box = _boxes[b];
            if (!full)
            {
                if (!box.FlowDirty) continue;
                box.FlowDirty = false;
            }
            if (!_table.TryResolve(box.Container, out _)) continue;
            PlaceBox(box);
        }
    }

    /// <summary>
    /// 流式摆放（fork 流式布局同款折行判据：行内已有内容且再放会越界才折行——
    /// 单个超宽子节点独占一行而不是空转）。只写位置，不改子尺寸。
    /// </summary>
    private void PlaceBox(LinearBox box)
    {
        GeomOf(box.Index, out _, out _, out float cw, out float ch);
        float mainLimit = box.Desc.Vertical ? ch : cw;
        float mainPos = 0f, crossPos = 0f, lineMax = 0f;
        bool lineHas = false;

        for (NodeHandle c = _table.FirstChild(box.Container); !c.IsNone; c = _table.NextSibling(c))
        {
            if (!_table.TryResolve(c, out uint ci)) continue;
            if (!_scratchMode && _table.ResolvedRef(c) == 0)
            {
                EnsureSlot(c, ci);                   // 结构性迟到的子：有声分配（M1-20 后应归零）
                MergeOwn(ci, (byte)(LayoutOwn.PosX | LayoutOwn.PosY));
                Stats.LateSlotAllocs++;
            }
            GeomOf(ci, out _, out _, out float w, out float h);
            float main = box.Desc.Vertical ? h : w;
            float cross = box.Desc.Vertical ? w : h;
            if (box.Desc.Wrap && lineHas && mainPos + main > mainLimit)
            {
                mainPos = 0f;
                crossPos += lineMax + box.Desc.LineGap;
                lineMax = 0f;
            }
            float nx = box.Desc.Vertical ? crossPos : mainPos;
            float ny = box.Desc.Vertical ? mainPos : crossPos;
            WriteGeom(ci, nx, ny, w, h, out bool moved, out bool sized);
            Stats.LinearPlaced++;
            if (!_scratchMode) Touch(ci, moved, sized);
            mainPos += main + box.Desc.Gap;
            lineMax = MathF.Max(lineMax, cross);
            lineHas = true;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 捕获与几何读写（活列 / 旁缓冲双模）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>捕获一条算子的 offset/ratio/钉值（dst 读 authored、src 读 resolved，架构原文）。</summary>
    private void CaptureOp(Instance inst, int opIdx)
    {
        ConstraintOp op = inst.G.Ops[opIdx];
        if (!_table.TryResolve(inst.ByLocal[op.DstNode], out uint dstIdx)) return;
        _table.AuthoredAt(dstIdx, out float ax, out float ay, out float aw, out float ah);
        bool xAxis = op.Axis == LayoutAxis.X;
        float apos = xAxis ? ax : ay;
        float asize = xAxis ? aw : ah;
        float authEdge;
        switch (op.DstEdge)
        {
            case EdgeSel.Min: authEdge = apos; break;
            case EdgeSel.Center: authEdge = apos + asize * 0.5f; break;
            case EdgeSel.Max: authEdge = apos + asize; break;
            default: authEdge = asize; break;
        }
        switch ((ConstraintKind)op.Kind)
        {
            case ConstraintKind.Pin:
                inst.Offsets[opIdx] = authEdge;
                break;
            case ConstraintKind.FollowDelta:
                inst.Offsets[opIdx] = authEdge - ReadSrcEdge(inst, op.SrcNode, op.Axis, op.SrcEdge);
                break;
            default:                                                // FollowPercent
            {
                float size = ReadSrcEdge(inst, op.SrcNode, op.Axis, EdgeSel.Size);
                if (size == 0f) { inst.Offsets[opIdx] = 0f; break; }    // fork 同判：零基尺寸塌到锚点
                float min = ReadSrcEdge(inst, op.SrcNode, op.Axis, EdgeSel.Min);
                inst.Offsets[opIdx] = op.DstEdge == EdgeSel.Size ? asize / size : (authEdge - min) / size;
                break;
            }
        }
    }

    /// <summary>几何读：差分全量腿读旁缓冲（槽节点）；其余读活 resolved（无槽 = authored 同存储）。</summary>
    private void GeomOf(uint idx, out float x, out float y, out float w, out float h)
    {
        if (_scratchMode && _scratchOrd.TryGetValue(idx, out int o))
        {
            x = _sx[o]; y = _sy[o]; w = _sw[o]; h = _sh[o];
            return;
        }
        _table.ResolvedAt(idx, out x, out y, out w, out h);
    }

    /// <summary>
    /// 几何写：活模式经 <see cref="NodeTable.SetResolved"/>（P5 相位门 + WriteSource.Layout 唯一写者门 +
    /// 等值切断 + 派生 Mark 一体）；旁缓冲模式只写数组、零 Mark（神谕不写活列）。
    /// </summary>
    private void WriteGeom(uint idx, float x, float y, float w, float h, out bool moved, out bool sized)
    {
        if (_scratchMode)
        {
            if (!_scratchOrd.TryGetValue(idx, out int o))
            {
                moved = sized = false;               // 未经活求解的迟到子（调用契约：活求解先行）
                return;
            }
            moved = !BitEquals.Eq(_sx[o], x) || !BitEquals.Eq(_sy[o], y);
            sized = !BitEquals.Eq(_sw[o], w) || !BitEquals.Eq(_sh[o], h);
            _sx[o] = x; _sy[o] = y; _sw[o] = w; _sh[o] = h;
        }
        else
        {
            _table.ResolvedAt(idx, out float cx, out float cy, out float cw, out float ch);
            moved = !BitEquals.Eq(cx, x) || !BitEquals.Eq(cy, y);
            sized = !BitEquals.Eq(cw, w) || !BitEquals.Eq(ch, h);
            if (moved || sized) _table.SetResolved(_table.HandleOf(idx), x, y, w, h, WriteSource.Layout);
        }
        if (moved || sized)
        {
            _writeChanges++;
            if (_firstChangeIndex == NodeTable.NoIndex) _firstChangeIndex = idx;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 两道门（本包核心价值）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>P5 幂等断言：收敛态必须是全量一遍的不动点（同 authored + 同图 ⇒ resolved 零变化）。</summary>
    private void RunIdempotenceCheck()
    {
        _writeChanges = 0;
        _firstChangeIndex = NodeTable.NoIndex;
        RunPass(full: true);
        if (_writeChanges == 0) return;
        Stats.IdempotenceFailures++;
        Stats.LastGateError = "P5 幂等断言失败：同帧第二遍产生 " + _writeChanges
            + " 处 resolved 变化，首个节点 #" + _firstChangeIndex
            + "（存在不收敛写：求值不是 (authored, 约束图, offsets) 的纯函数）";
    }

    /// <summary>
    /// 布局差分神谕（L2）：**增量重排 vs 从 authored 全量重排**逐位比对。
    /// 两腿是真正独立的算法路径——增量腿走 FanOut 脏传播（本帧已跑），全量腿此刻把全部槽节点
    /// 从 authored 初值起在旁缓冲里解到不动点（无脏簿记、全量遍历、绝不写活列），
    /// 然后逐槽逐分量按位比对。共享的只有逐算子的纯求值函数（float 表达式必须同一份，
    /// 否则求值路径差异会把门变成本机绿）。
    /// 超限帧（<see cref="LayoutStats.PendingWork"/>）跳过：增量腿按契约把余活留给下帧，两腿不可比。
    /// </summary>
    /// <returns>true = 两腿逐位一致（或本帧跳过）。</returns>
    public bool CheckDifferential(out string error)
    {
        error = string.Empty;
        if (Stats.PendingWork)
        {
            Stats.LastDifferentialSkipped = true;
            return true;
        }

        int n = _slotNodes.Count;
        if (_sx.Length < n)
        {
            _sx = new float[n]; _sy = new float[n]; _sw = new float[n]; _sh = new float[n];
        }
        _scratchOrd.Clear();
        int live = 0;
        for (int i = 0; i < n; i++)
        {
            uint idx = _slotNodes[i];
            if (!_table.IsIndexAlive(idx)) continue;
            _table.AuthoredAt(idx, out _sx[live], out _sy[live], out _sw[live], out _sh[live]);
            _scratchOrd.Add(idx, live);
            live++;
        }

        _scratchMode = true;
        try
        {
            int pass = 0;
            while (true)
            {
                _writeChanges = 0;
                _firstChangeIndex = NodeTable.NoIndex;
                RunPass(full: true);
                if (_writeChanges == 0) break;
                if (++pass > 64)
                {
                    error = "布局差分神谕：全量腿 64 遍未到不动点（系统含未被静态断开的反馈）";
                    return false;
                }
            }
        }
        finally { _scratchMode = false; }

        foreach (KeyValuePair<uint, int> kv in _scratchOrd)
        {
            uint idx = kv.Key;
            int o = kv.Value;
            _table.ResolvedAt(idx, out float x, out float y, out float w, out float h);
            if (BitEquals.Eq(x, _sx[o]) && BitEquals.Eq(y, _sy[o])
                && BitEquals.Eq(w, _sw[o]) && BitEquals.Eq(h, _sh[o])) continue;
            error = "布局差分神谕失败：节点 #" + idx
                + " 增量腿 (" + x + "," + y + "," + w + "," + h + ")"
                + " ≠ 全量腿 (" + _sx[o] + "," + _sy[o] + "," + _sw[o] + "," + _sh[o] + ")"
                + "——增量腿漏了一条依赖的传播，或两腿求值表达式分叉";
            return false;
        }
        return true;
    }
}
