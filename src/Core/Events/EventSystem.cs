using FairyNext.Numerics;

namespace FairyNext.Core.Events;

/// <summary>每帧事件账（架构「事件暴露 · 诊断」的 <c>EventStats</c>）。</summary>
public struct EventStats
{
    /// <summary>命中走过的节点数（本帧累计）。</summary>
    public int HitTestSteps;
    /// <summary>真跑了几次命中（帧号护栏命中缓存后的实数）。</summary>
    public int HitTests;
    /// <summary>命中缓存复用次数（同帧同点的第二次问询）。</summary>
    public int HitCacheHits;
    /// <summary>派发次数（一次 Dispatch 算一次，不管链多长）。</summary>
    public int Dispatches;
    /// <summary>真正被调用的监听器条数。</summary>
    public int Invocations;
    /// <summary>本帧最长链（节点数）。</summary>
    public int ChainLenMax;
    /// <summary>派发时因 gen/DEAD 双验失败被跳过的链节点数。</summary>
    public int DeadChainSkips;
    /// <summary>被入口拒绝的轴事件数（零值或非整数 canonical）。</summary>
    public int AxisRejected;
    /// <summary>在册监听器数（帧末快照）。</summary>
    public int ListenerCount;

    /// <summary>清零。</summary>
    public void Reset()
    {
        HitTestSteps = 0; HitTests = 0; HitCacheHits = 0; Dispatches = 0; Invocations = 0;
        ChainLenMax = 0; DeadChainSkips = 0; AxisRejected = 0; ListenerCount = 0;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"events hits={HitTests}(+{HitCacheHits} cached) steps={HitTestSteps} dispatch={Dispatches} " +
        $"invoke={Invocations} chainMax={ChainLenMax} deadSkips={DeadChainSkips} axisRejected={AxisRejected} " +
        $"listeners={ListenerCount}";
}

/// <summary>
/// 事件平面的门面（架构平面四 B 的事件半边）：**P0 输入消费者**——
/// 排空输入包 → 命中（上帧序）→ 链快照派发。
///
/// 接线：<see cref="Attach"/> 独占 <see cref="UiKernel.InputHandler"/>（M1-08 留的 P0 钩），
/// 第二家接管即抛——两个消费者会把同一批包排两遍。
///
/// 相位纪律：P0 的回调窗口与 P3 同款——**自由写状态，写只 Mark**（O(1) 幂等，排水未开始），
/// 于是「按下就变色」当帧可见（P7 排水在后），而回调里改树不影响本次派发路径（链已快照）。
/// 用户在回调里直改 VM 的重入风险由推荐路径 <c>BindCommand</c>（M2-01）解决，机制层不反转：
/// 同步回调是机制，命令队列是推荐业务路径。
///
/// <c>InputPacket</c> 的字段复用约定（M1-08 的 POD 已冻结，本平面明文化其解释面）：
///  · Pointer* 包：<c>Id</c> = 指针 id（鼠标恒 0），<c>Device</c> = **键号**（0 左 / 1 右 / 2 中），
///    <c>X/Y</c> = stage 坐标，<c>Time</c> = 宿主时间戳；
///  · Key* 包：<c>Id</c> = 宿主键码；Char 包：<c>Id</c> = UTF-32 码点；
///  · Wheel 包：<c>DeltaX/DeltaY</c> 必须是**非零整数** canonical 值，否则入口拒绝并计数
///    （事件·不变量 8：亚单位累积只存在于平台归一层）。
/// </summary>
public sealed class EventSystem
{
    /// <summary>固定触点槽数（架构：10 槽、零分配、常驻 ≈2KB）。</summary>
    public const int TouchSlotCount = 10;

    private struct ChainEntry
    {
        internal NodeHandle Node;
        internal uint Index;
    }

    private struct HitCache
    {
        internal ulong Frame;
        internal Vector2 Point;
        internal NodeHandle Node;
        internal Vector2 Local;
        internal bool Valid;
    }

    private readonly NodeTable _table;
    private readonly ListenerTable _listeners;
    private readonly HitPolicyTable _policy;
    private readonly HitTester _hit;

    private readonly TouchSlot[] _slots = new TouchSlot[TouchSlotCount];
    private readonly HitCache[] _hitCache = new HitCache[TouchSlotCount];

    private ChainEntry[] _chain = new ChainEntry[32];
    private int _chainTop;
    private NodeHandle[] _rollScratch = new NodeHandle[32];

    private UiKernel? _kernel;
    private ulong _frameId;
    private int _activeTouchId = -1;      // 当前正在派发的触点（CaptureTouch 的落点）

    /// <summary>建一套事件平面（自带监听表、命中策略表、命中器）。</summary>
    public EventSystem(NodeTable table)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _listeners = new ListenerTable(table);
        _policy = new HitPolicyTable(table);
        _hit = new HitTester(table, _policy);
        Focus = table.Root;
        for (int i = 0; i < TouchSlotCount; i++) _slots[i].TouchId = int.MinValue;
    }

    /// <summary>树域。</summary>
    public NodeTable Table => _table;

    /// <summary>监听器表。</summary>
    public ListenerTable Listeners => _listeners;

    /// <summary>命中策略表。</summary>
    public HitPolicyTable HitPolicy => _policy;

    /// <summary>命中器（槽矩阵源与裁剪域源挂在它上面）。</summary>
    public HitTester Hit => _hit;

    /// <summary>本帧事件账。</summary>
    public EventStats Stats;

    /// <summary>上一帧锁存的事件账（P9 之后定格）。</summary>
    public EventStats LastFrameStats { get; private set; }

    /// <summary>双击判定的时间窗（秒；fork 同款 0.35）。</summary>
    public double DoubleClickInterval { get; set; } = 0.35;

    /// <summary>点击/双击的位移阈值（stage 单位，逐轴判；fork <c>_clickTestThreshold</c> = 50）。</summary>
    public float ClickTestThreshold { get; set; } = 50f;

    /// <summary>
    /// 焦点节点（键盘/字符的派发起点）。**M1 面**：只是一个可写属性，
    /// <c>SetFocus</c> 的 LCA diff + FocusIn/FocusOut + <c>(listId, dataKey)</c> 寻址归 M2-10。
    /// </summary>
    public NodeHandle Focus { get; set; }

    // ── 接线 ────────────────────────────────────────────────────────────────

    /// <summary>接管相位机的 P0 输入消费（独占）。</summary>
    public void Attach(UiKernel kernel)
    {
        if (kernel == null) throw new ArgumentNullException(nameof(kernel));
        if (!ReferenceEquals(kernel.Table, _table))
            throw new InvalidOperationException("EventSystem 与内核接的不是同一棵树");
        if (kernel.InputHandler != null)
            throw new InvalidOperationException("本内核的 P0 输入消费者已被占用（两个消费者会把同一批包排两遍）");
        _kernel = kernel;
        kernel.InputHandler = OnInput;
        kernel.BeforeFrame += OnBeforeFrame;
        kernel.AfterFrame += OnAfterFrame;
    }

    /// <summary>卸下接线。</summary>
    public void Detach()
    {
        UiKernel? k = _kernel;
        if (k == null) return;
        if (k.InputHandler == OnInput) k.InputHandler = null;
        k.BeforeFrame -= OnBeforeFrame;
        k.AfterFrame -= OnAfterFrame;
        _kernel = null;
    }

    private void OnBeforeFrame(ulong frameId)
    {
        _frameId = frameId;
        Stats.Reset();
    }

    private void OnAfterFrame(ulong frameId)
    {
        Stats.ListenerCount = _listeners.ListenerCount;
        LastFrameStats = Stats;
    }

    // ── 命中（帧号护栏 + 每指针缓存）──────────────────────────────────────

    /// <summary>
    /// 对 stage 点做一次命中，带**每指针每帧**的缓存（事件·不变量 3）。
    ///
    /// 护栏的可执行形态是「同帧同点不重复命中」而不是「同帧至多一次」：一帧里先 move 再 down
    /// 是常态，位置变了必须重命中，否则 down 会派发到 move 之前的目标。位置没变的第二次问询
    /// （典型：<see cref="TouchTarget"/>）复用缓存，计入 <see cref="EventStats.HitCacheHits"/>。
    /// </summary>
    public HitResult HitTest(int touchId, Vector2 stagePoint)
    {
        int s = SlotIndexOf(touchId);
        if (s >= 0)
        {
            ref HitCache c = ref _hitCache[s];
            if (c.Valid && c.Frame == _frameId
                && BitEquals.Eq(c.Point.x, stagePoint.x) && BitEquals.Eq(c.Point.y, stagePoint.y))
            {
                Stats.HitCacheHits++;
                return new HitResult(_table.IsAlive(c.Node) ? c.Node : NodeHandle.None, c.Local);
            }
        }

        HitResult r = _hit.Hit(stagePoint);
        Stats.HitTests++;
        Stats.HitTestSteps += _hit.LastSteps;
        if (s >= 0)
            _hitCache[s] = new HitCache { Frame = _frameId, Point = stagePoint, Node = r.Node, Local = r.Local, Valid = true };
        return r;
    }

    /// <summary>不带触点身份的一次命中（工具/测试用；不进缓存）。</summary>
    public HitResult HitTest(Vector2 stagePoint)
    {
        HitResult r = _hit.Hit(stagePoint);
        Stats.HitTests++;
        Stats.HitTestSteps += _hit.LastSteps;
        return r;
    }

    /// <summary>某触点当前的命中目标（与主命中共享同一缓存；惯性期由 ScrollPane 查询）。</summary>
    public NodeHandle TouchTarget(int touchId)
    {
        int s = SlotIndexOf(touchId);
        if (s < 0) return NodeHandle.None;
        NodeHandle t = _slots[s].Target;
        return _table.IsAlive(t) ? t : NodeHandle.None;
    }

    /// <summary>读一个触点槽（诊断/测试；未占用的 id 返回 default）。</summary>
    public TouchSlot SlotOf(int touchId)
    {
        int s = SlotIndexOf(touchId);
        return s < 0 ? default : _slots[s];
    }

    /// <summary>在用的触点槽数。</summary>
    public int ActiveTouchCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < TouchSlotCount; i++) if (_slots[i].Active) n++;
            return n;
        }
    }

    // ── 派发 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 链派发（capture 下行 + bubble 上行）。返回被调用的监听器条数。
    ///
    /// 三条纪律：
    ///  · **先快照后派发**——链在派发前一次收完（只收 <c>listenerHead != -1</c> 且过 mask 的节点），
    ///    派发过程中的 reparent/remove 不改变本次路径（事件·不变量 5）；
    ///  · **每步双验**——<c>TryResolve</c> 同时验 gen 与 DEAD 位，标记即死的节点当帧不再收事件
    ///    （事件·不变量 4；gen++ 统一在 P9，只验 gen 会放行僵尸）；
    ///  · 块下标每步现读——回调里 <c>RemoveAll</c> 归还了块也不会读到别人的条目。
    /// </summary>
    public int Dispatch<T>(NodeHandle target, EventId<T> id, in T arg, int touchId = -1) =>
        DispatchCore(target, id.Raw, in arg, touchId, bubbles: true);

    /// <summary>直发（不冒泡；monitor 附加目标与 CharInput 走这条）。</summary>
    public int DispatchDirect<T>(NodeHandle target, EventId<T> id, in T arg, int touchId = -1) =>
        DispatchCore(target, id.Raw, in arg, touchId, bubbles: false);

    private int DispatchCore<T>(NodeHandle target, int eventId, in T arg, int touchId, bool bubbles)
    {
        if (target.IsNone || eventId < 0) return 0;
        Stats.Dispatches++;

        int baseTop = _chainTop;                 // 嵌套派发安全：链缓冲是一条栈
        int len = 0;
        for (NodeHandle h = target; !h.IsNone; h = _table.Parent(h))
        {
            if (!_table.TryResolve(h, out uint idx)) break;
            if (_listeners.MayReceive(idx, eventId))
            {
                if (_chainTop == _chain.Length) Array.Resize(ref _chain, _chain.Length * 2);
                _chain[_chainTop++] = new ChainEntry { Node = h, Index = idx };
                len++;
            }
            if (!bubbles) break;
        }
        if (len > Stats.ChainLenMax) Stats.ChainLenMax = len;
        if (len == 0) { _chainTop = baseTop; return 0; }

        int savedTouch = _activeTouchId;
        _activeTouchId = touchId;
        var ctx = new EventCtx(_table, target, touchId);
        // 直发（monitor 附加目标 / CharInput）报 Direct，链派发的目标节点报 Target：
        // 回调据此区分「我是被冒泡到的」与「我是被单独喂的」，monitor 基元的语义全靠这一位。
        EventPhase selfPhase = bubbles ? EventPhase.Target : EventPhase.Direct;
        int invoked = 0;
        try
        {
            // capture 相：链是「目标 → 根」序，逆着跑就是根 → 目标
            for (int i = len - 1; i >= 0 && !ctx.IsPropagationStopped; i--)
                invoked += InvokeNode(baseTop + i, i == 0 ? selfPhase : EventPhase.Capture,
                    ListenPhase.Capture, eventId, ref ctx, in arg);

            // bubble 相：目标 → 根
            for (int i = 0; i < len && !ctx.IsPropagationStopped; i++)
                invoked += InvokeNode(baseTop + i, i == 0 ? selfPhase : EventPhase.Bubble,
                    ListenPhase.Bubble, eventId, ref ctx, in arg);
        }
        finally
        {
            _chainTop = baseTop;
            _activeTouchId = savedTouch;
        }
        Stats.Invocations += invoked;
        return invoked;
    }

    private int InvokeNode<T>(int chainPos, EventPhase phase, ListenPhase want, int eventId,
        ref EventCtx ctx, in T arg)
    {
        ChainEntry entry = _chain[chainPos];
        // 双验：gen 相符 ∧ DEAD 未置。派发中被销毁的节点在这里静默退出，不是异常。
        if (!_table.TryResolve(entry.Node, out uint idx))
        {
            if (want == ListenPhase.Bubble) Stats.DeadChainSkips++;   // 一个节点只记一次（两相跑两趟）
            return 0;
        }

        int invoked = 0;
        ctx.Sender = entry.Node;
        ctx.Phase = phase;
        for (int i = 0; i < _listeners.EntryCountOf(_listeners.BlockOf(idx)); i++)
        {
            if (!_listeners.TryEntry(_listeners.BlockOf(idx), i, out int eid, out ListenPhase lp, out Delegate fn))
                break;
            if (eid != eventId || lp != want) continue;
            if (fn is EventFn<T> typed)
            {
                typed(ref ctx, in arg);
                invoked++;
            }
            else
            {
                UiAssert.That(false,
                    $"事件 {UiEvents.NameOf(eventId)} 的监听器载荷类型与派发不符（{fn.GetType().Name}）");
            }
            if (ctx.TouchCaptureRequested)
            {
                ctx.ClearCaptureRequest();
                RegisterMonitor(_activeTouchId, ctx.Sender);
            }
            if (ctx.IsPropagationStopped) break;
        }
        return invoked;
    }

    private void RegisterMonitor(int touchId, NodeHandle node)
    {
        int s = SlotIndexOf(touchId);
        if (s < 0)
        {
            UiAssert.That(false, "CaptureTouch 发生在非指针事件的派发里（没有触点可抢）");
            return;
        }
        _slots[s].AddMonitor(node);
    }

    // ── P0：排空输入包 ─────────────────────────────────────────────────────

    private void OnInput(in InputPacket packet, ref FrameContext ctx)
    {
        UiAssert.That(ctx.Phase == FramePhase.P0_Input, "输入消费只发生在 P0");
        switch (packet.Kind)
        {
            case InputKind.PointerDown: OnPointerDown(in packet); break;
            case InputKind.PointerMove: OnPointerMove(in packet); break;
            case InputKind.PointerUp: OnPointerUp(in packet); break;
            case InputKind.PointerCancel: OnPointerCancel(in packet); break;
            case InputKind.Wheel: OnWheel(in packet); break;
            case InputKind.KeyDown: DispatchKey(UiEvents.KeyDown, in packet); break;
            case InputKind.KeyUp: DispatchKey(UiEvents.KeyUp, in packet); break;
            case InputKind.Char:
                DispatchDirect(Focus, UiEvents.CharInput, new TextInput(packet.Id));
                break;
            case InputKind.FocusGained:
            case InputKind.FocusLost:
                // 宿主窗口级焦点：M1 不改节点焦点（节点焦点路由归 M2-10），只作为包被消费掉。
                break;
            default:
                UiAssert.That(false, $"未知输入包种类 {packet.Kind}");
                break;
        }
    }

    private void DispatchKey(EventId<KeyInput> id, in InputPacket p) =>
        Dispatch(Focus, id, new KeyInput(p.Id, (Modifiers)p.Modifiers));

    private void OnPointerDown(in InputPacket p)
    {
        int s = AcquireSlot(p.Id);
        if (s < 0) return;
        var pos = new Vector2(p.X, p.Y);
        HitResult hr = HitTest(p.Id, pos);

        ref TouchSlot slot = ref _slots[s];
        slot.Began = true;
        slot.Button = p.Device;
        slot.Pos = pos;
        slot.DownPos = pos;
        slot.DownTime = p.Time;
        slot.Time = p.Time;
        slot.ClickCancelled = false;
        slot.ClearMonitors();
        slot.Target = hr.Node;
        slot.CaptureDownChain(_table, hr.Node);

        UpdateRoll(ref slot, hr.Node, in p);
        if (!hr.Node.IsNone) Dispatch(hr.Node, UiEvents.TouchBegin, Payload(in slot, in p), p.Id);
    }

    private void OnPointerMove(in InputPacket p)
    {
        int s = AcquireSlot(p.Id);
        if (s < 0) return;
        var pos = new Vector2(p.X, p.Y);
        HitResult hr = HitTest(p.Id, pos);

        ref TouchSlot slot = ref _slots[s];
        slot.Pos = pos;
        slot.Time = p.Time;
        slot.Target = hr.Node;
        if (slot.Began &&
            (MathF.Abs(pos.x - slot.DownPos.x) > ClickTestThreshold ||
             MathF.Abs(pos.y - slot.DownPos.y) > ClickTestThreshold))
            slot.ClickCancelled = true;

        UpdateRoll(ref slot, hr.Node, in p);

        PointerInput arg = Payload(in slot, in p);
        // 移动**平时只发给根**；monitor 逐个作为附加目标插队收到，且只收自身相、不冒泡。
        Dispatch(_table.Root, UiEvents.TouchMove, arg, p.Id);
        DispatchMonitors(ref slot, UiEvents.TouchMove, in arg, p.Id, NodeHandle.None);
    }

    private void OnPointerUp(in InputPacket p)
    {
        int s = SlotIndexOf(p.Id);
        if (s < 0) return;
        ref TouchSlot slot = ref _slots[s];
        var pos = new Vector2(p.X, p.Y);
        HitResult hr = HitTest(p.Id, pos);
        slot.Pos = pos;
        slot.Time = p.Time;
        slot.Target = hr.Node;
        slot.Began = false;

        // 连击四元组（fork Stage.cs 1746-1771 @ oracle 08a2d56）：时间窗 + 逐轴位移 + 同键。
        bool cancelled = slot.DownChainLen == 0 || slot.ClickCancelled
            || MathF.Abs(pos.x - slot.DownPos.x) > ClickTestThreshold
            || MathF.Abs(pos.y - slot.DownPos.y) > ClickTestThreshold;
        if (cancelled)
        {
            slot.ClickCancelled = true;
            slot.LastClickTime = 0.0;
            slot.ClickCount = 1;
        }
        else
        {
            if (p.Time - slot.LastClickTime < DoubleClickInterval
                && MathF.Abs(pos.x - slot.LastClickPos.x) < ClickTestThreshold
                && MathF.Abs(pos.y - slot.LastClickPos.y) < ClickTestThreshold
                && slot.LastClickButton == slot.Button)
                slot.ClickCount = slot.ClickCount == 2 ? (byte)1 : (byte)(slot.ClickCount + 1);
            else
                slot.ClickCount = 1;
            slot.LastClickTime = p.Time;
            slot.LastClickPos = pos;
            slot.LastClickButton = slot.Button;
        }

        PointerInput arg = Payload(in slot, in p);
        NodeHandle endTarget = hr.Node.IsNone ? _table.Root : hr.Node;
        Dispatch(endTarget, UiEvents.TouchEnd, arg, p.Id);
        DispatchMonitors(ref slot, UiEvents.TouchEnd, in arg, p.Id, endTarget);

        NodeHandle clickTarget = slot.ClickTest(_table);
        if (!clickTarget.IsNone)
        {
            EventId<PointerInput> id = slot.Button == 1 ? UiEvents.RightClick : UiEvents.Click;
            Dispatch(clickTarget, id, arg, p.Id);
        }
        EndTouch(s);
    }

    private void OnPointerCancel(in InputPacket p)
    {
        int s = SlotIndexOf(p.Id);
        if (s < 0) return;
        ref TouchSlot slot = ref _slots[s];
        slot.Time = p.Time;
        slot.Began = false;
        slot.ClickCancelled = true;
        PointerInput arg = Payload(in slot, in p);
        NodeHandle target = _table.IsAlive(slot.Target) ? slot.Target : _table.Root;
        Dispatch(target, UiEvents.TouchCancel, arg, p.Id);
        DispatchMonitors(ref slot, UiEvents.TouchCancel, in arg, p.Id, target);
        UpdateRoll(ref slot, NodeHandle.None, in p);
        EndTouch(s);
    }

    private void OnWheel(in InputPacket p)
    {
        // 入口拒绝零值与非整数 canonical（事件·不变量 8）——亚单位累积只存在于平台归一层。
        TryAxis(in p, p.DeltaY, AxisKind.ScrollY);
        TryAxis(in p, p.DeltaX, AxisKind.ScrollX);
    }

    private void TryAxis(in InputPacket p, float raw, AxisKind kind)
    {
        if (raw == 0f) return;                       // 零值不是事件：它只会污染手势判定
        if (!float.IsFinite(raw) || MathF.Floor(raw) != raw)
        {
            Stats.AxisRejected++;
            UiAssert.That(false,
                $"AxisDelta 入口拒绝非整数 canonical 值 {raw}（{kind}）——亚单位累积归平台归一层");
            return;
        }
        var pos = new Vector2(p.X, p.Y);
        HitResult hr = HitTest(p.Id, pos);
        NodeHandle target = hr.Node.IsNone ? _table.Root : hr.Node;
        Dispatch(target, UiEvents.Axis, new AxisDelta(kind, (int)raw), p.Id);
    }

    private PointerInput Payload(in TouchSlot slot, in InputPacket p) =>
        new PointerInput(slot.Pos, new Vector2(p.X, p.Y), p.Id, slot.Button, slot.ClickCount,
            (float)(p.Time - slot.DownTime), (Modifiers)p.Modifiers);

    private void DispatchMonitors(ref TouchSlot slot, EventId<PointerInput> id, in PointerInput arg,
        int touchId, NodeHandle bubbledFrom)
    {
        for (int i = 0; i < slot.MonitorLen; i++)
        {
            NodeHandle m = slot.Monitors[i];
            if (!_table.IsAlive(m)) continue;
            if (!bubbledFrom.IsNone && IsAncestorOrSelf(m, bubbledFrom)) continue;   // 冒泡链上已收过
            DispatchDirect(m, id, arg, touchId);
        }
    }

    private bool IsAncestorOrSelf(NodeHandle maybeAncestor, NodeHandle node)
    {
        for (NodeHandle h = node; !h.IsNone; h = _table.Parent(h))
            if (h.Equals(maybeAncestor)) return true;
        return false;
    }

    /// <summary>
    /// RollOver/RollOut 的链 diff：新旧 target 两条父链求 LCA，只对**差集**派发，无重复。
    /// 派发是直发（每个节点收自己那一条），这与 DOM 的 mouseenter/mouseleave 同形——
    /// 冒泡版会让共同祖先在每次子节点间移动时反复收到 out/over。
    /// </summary>
    private void UpdateRoll(ref TouchSlot slot, NodeHandle newTarget, in InputPacket p)
    {
        NodeHandle old = slot.LastRollOver;
        if (old.Equals(newTarget)) return;
        if (!_table.IsAlive(old)) old = NodeHandle.None;

        PointerInput arg = Payload(in slot, in p);

        // 旧链：old → 根，逐个查是否是 new 的祖先；第一个是的就是 LCA。
        for (NodeHandle h = old; !h.IsNone; h = _table.Parent(h))
        {
            if (!newTarget.IsNone && IsAncestorOrSelf(h, newTarget)) break;   // 到 LCA 为止
            DispatchDirect(h, UiEvents.RollOut, arg, p.Id);
        }

        // 新链：先攒（根在前），再自 LCA 之下往下发。
        int n = 0;
        for (NodeHandle h = newTarget; !h.IsNone; h = _table.Parent(h))
        {
            if (!old.IsNone && IsAncestorOrSelf(h, old)) break;
            if (n == _rollScratch.Length) Array.Resize(ref _rollScratch, _rollScratch.Length * 2);
            _rollScratch[n++] = h;
        }
        for (int i = n - 1; i >= 0; i--) DispatchDirect(_rollScratch[i], UiEvents.RollOver, arg, p.Id);

        slot.LastRollOver = newTarget;
    }

    // ── 触点槽池 ────────────────────────────────────────────────────────────

    private int SlotIndexOf(int touchId)
    {
        for (int i = 0; i < TouchSlotCount; i++)
            if (_slots[i].Active && _slots[i].TouchId == touchId) return i;
        return -1;
    }

    /// <summary>
    /// 取（或开）一个触点槽。抬起后槽**不立即回收**——鼠标抬起之后仍在原处悬停，
    /// 回收会把 <see cref="TouchSlot.LastRollOver"/> 一并丢掉，下一次移动就重发一遍 RollOver。
    /// 槽荒时淘汰「最久没动过的非按下槽」；全部处于按下期才是真的用尽（有声丢包）。
    /// </summary>
    private int AcquireSlot(int touchId)
    {
        int existing = SlotIndexOf(touchId);
        if (existing >= 0) return existing;
        for (int i = 0; i < TouchSlotCount; i++)
        {
            if (_slots[i].Active) continue;
            _slots[i].Reset(touchId);
            _hitCache[i] = default;
            return i;
        }

        int victim = -1;
        for (int i = 0; i < TouchSlotCount; i++)
        {
            if (_slots[i].Began) continue;
            if (victim < 0 || _slots[i].Time < _slots[victim].Time) victim = i;
        }
        if (victim >= 0)
        {
            _slots[victim].Reset(touchId);
            _hitCache[victim] = default;
            return victim;
        }

        // 10 槽全在按下期：**不装作成功**——丢包并有声（同「槽荒不硬烘包围盒」的纪律）。
        UiAssert.That(false, $"触点槽用尽（{TouchSlotCount} 槽全在按下期），触点 {touchId} 被丢弃");
        return -1;
    }

    /// <summary>抬起/取消后的收尾：按下期结束，快照清空，槽本身留着承接悬停。</summary>
    private void EndTouch(int s)
    {
        _slots[s].Began = false;
        _slots[s].ClearMonitors();
        _slots[s].DownChainLen = 0;
    }
}
