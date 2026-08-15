using System.Diagnostics;
using FairyNext.Contracts;
using FairyNext.State;

namespace FairyNext.Core;

/// <summary>
/// 法定帧相位（设计书 §02，唯一词表）。所有子系统的钩子按此编号挂接；
/// 相位语义与重入契约见 docs/design/01-法定帧协议.md——本枚举是其代码化身，改动必须同步文档。
/// </summary>
public enum FramePhase : byte
{
    P0_Input = 0,          // 排空 PostInput → 命中（上帧序）→ 事件派发
    P1_Clock = 1,          // Clock 定时器按创建序推进（三时域）
    P2_GlobalInvalidate = 2, // 挂起的全局失效统一生效（字形库 generation / ScreenSize）
    P3_Commands = 3,       // CommandQueue FIFO（≤4 波，超限延帧+责任链）
    P4_State = 4,          // Binder.Flush → TickTimelines → Resolve（写 authored，不跑用户代码）
    P5_Layout = 5,         // 度量→包围→约束→流式 + 受控回调窗（不变式快路径 / ≤3 轮兜底）
    P6_Settle = 6,         // paintOrder 切片拼接（structEpoch++）；world + worldVisual 一遍算
    P7_RenderDrain = 7,    // 流消化五通道；禁用户代码（debug 断言 / release 入下帧）
    P8_Submit = 8,         // 序派生（paintOrder 下标×16）；合并区间上传；属性块集中写
    P9_FrameEnd = 9,       // 诊断锁存；句柄统一换代（标记即死→此刻 gen++）
}

/// <summary>
/// 脏通道（设计书 §4.3）。每个可变属性属于且仅属于一个通道；通道自带方向
/// （向上=队列，向下=子树戳）。「漏通道 = 编译期没有该 setter」。
/// </summary>
[Flags]
public enum Ch : ushort
{
    None = 0,
    Content = 1 << 0,    // 叶视觉内容：纹理 region/九宫/fill/帧号/文本已排版产物
    Transform = 1 << 1,  // x,y,scale,rotation,skew,pivot（节点局部变换）
    Color = 1 << 2,      // 局部 alpha、tint、grayed（局部值）
    Visible = 1 << 3,    // visible（含 controller 翻页驱动）
    Structure = 1 << 4,  // 增删子/换序/mask/孤岛准入变化
    Layout = 1 << 5,     // width,height,约束输入,autoSize
    Text = 1 << 6,       // 文本串/样式（排版产物再 Mark Content——三级惰性）
    BoundsD = 1 << 7,    // 派生位：子几何变化置父链、遇脏即停，query-pull 消费，**不入队列**
    DownColor = 1 << 8,  // 向下通道：子树戳惰性下钻
    DownVisible = 1 << 9,
    DownLayer = 1 << 10,
}

/// <summary>写来源（裁决 1/4）：系统写不经用户入口，_gearLocked 类锁在类型上不需要。</summary>
public enum WriteSource : byte
{
    User = 0,     // 用户代码（gear 属性 → 当前页 pageOverride COW）
    Binding = 1,  // Binder apply（不回写存档）
    Anim = 2,     // timeline/tween 经 anim 通道
    Layout = 3,   // 布局排水写 resolved（唯一写者）
}

/// <summary>
/// 64bit 代际句柄（承诺 1 / 裁决 10）：标记即死、P9 帧末统一换代；
/// TryResolve 必须同时验 gen 与 DEAD 位。跨帧缓存后失效 = 响亮失败。
/// </summary>
public readonly struct NodeHandle : IEquatable<NodeHandle>
{
    public readonly uint Index;
    public readonly ushort Gen;
    public readonly ushort Tree;

    public NodeHandle(uint index, ushort gen, ushort tree)
    {
        Index = index; Gen = gen; Tree = tree;
    }

    public static readonly NodeHandle None = default;
    public bool IsNone => Index == 0 && Gen == 0 && Tree == 0;

    public ulong Pack() => Index | ((ulong)Gen << 32) | ((ulong)Tree << 48);
    public static NodeHandle Unpack(ulong packed) =>
        new((uint)packed, (ushort)(packed >> 32), (ushort)(packed >> 48));

    public bool Equals(NodeHandle other) => Index == other.Index && Gen == other.Gen && Tree == other.Tree;
    public override bool Equals(object? obj) => obj is NodeHandle h && Equals(h);
    public override int GetHashCode() => HashCode.Combine(Index, Gen, Tree);
    public override string ToString() => IsNone ? "NodeHandle.None" : $"#{Tree}:{Index}@g{Gen}";
}

/// <summary>
/// 相位机（架构「平面二 · 时间宪法」门面 <c>UiKernel</c>）：**宿主唯一入口**。
/// 宿主对本运行时只调两个函数——<see cref="PostInput"/> 与 <see cref="Tick"/>（Unity 后端挂 LateUpdate）；
/// 一帧内部严格按 P0–P9 走完，每个相位的用户代码权限是相位表的一列而非旁注。
///
/// 本类持有的是**调度权**，不是业务：
///  - P0 排空输入（<see cref="InputHandler"/> 未装时不排空，留到下一帧——与「无消费者的通道不排水」同一条纪律）；
///  - P1 推进 <see cref="Clock"/> 三时域并触发到期定时器；
///  - P2 调 <see cref="Invalidation.ApplyPendingGlobal"/>（全局失效的**唯一**生效点，不变量 5）；
///  - P3 按 <see cref="Abi.CommandWaveLimit"/> 跑命令波次，超限延帧 + 责任链（不变量 10）；
///  - P4 调 <see cref="StateStep"/>（M2-01 的 Binder.Flush → TickTimelines → Resolve）；
///  - P5 排 Text（度量）与 Layout 通道（消费者 M1-18 / M1-16 才进驻）；
///  - P6 调 <see cref="NodeTable.ApplyStructure"/> + 派生列重算 + <see cref="Invalidation.DrainDown"/>；
///  - P7 冻结排水水位后排五通道（不变量 9 的降级路径在此生效）；
///  - P8 调 <see cref="SubmitStep"/>（M1-14 的合并区间上传与序派生）；
///  - P9 锁存诊断 + <see cref="NodeTable.EndFrame"/> 统一换代（不变量 11），随后 <see cref="AfterFrame"/>。
///
/// 与架构文档的三处形态差异（语义等价，接线时点不同）：
///  1. 文档写 <c>static class UiKernel</c>；这里是**每树域一个实例**——树域是多份的（多 stage 各一棵树），
///     静态全局态会把多棵树的相位、诊断、定时器混成一本账，也让测试互相污染（同 M1-07 对 Invalidation 的处理）。
///  2. frameId 不由宿主经 FrameTime 传入，由本类独占递增：单调性是子树戳/回放带/诊断锁存的地基，
///     交给宿主保证等于把地基外包。
///  3. <see cref="IChannelDrain.Drain"/> 多一个 <c>Ch channel</c> 参数（一个排水器可消费多条通道，
///     回调里必须知道排的是哪条）。
///
/// 帧外（两次 Tick 之间）<see cref="CurrentPhase"/> 报告 <see cref="FramePhase.P3_Commands"/>：
/// 宿主线性代码写 UI 状态的重入契约与 P3 完全相同（自由写，只 Mark），沿用 M1-06 定的默认值。
/// </summary>
public sealed class UiKernel
{
    private readonly NodeTable _table;
    private readonly Invalidation _invalidation;
    private readonly Clock _clock = new Clock();
    private readonly CommandQueue<InputPacket> _input = new CommandQueue<InputPacket>(64);
    private readonly FrameDiag _live = new FrameDiag();
    private readonly FrameDiag _latched = new FrameDiag();

    private ICommandPump[] _pumps = Array.Empty<ICommandPump>();
    private int _pumpCount;
    private ulong _frameId;
    private uint _inputSeq;
    private bool _inFrame;

    /// <summary>
    /// 接管一棵树的相位驱动。一棵树只能接一个内核（两个驱动者 = 写门可被绕过）。
    /// </summary>
    /// <param name="table">树域。</param>
    /// <param name="invalidation">该树的失效协议实例；null 时内核自建一个（树上不能已有别的实例）。</param>
    public UiKernel(NodeTable table, Invalidation? invalidation = null)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        UiAssert.That(table.PhaseDriver == null, "一棵树只能接一个 UiKernel（相位驱动权是独占的）");
        _invalidation = invalidation ?? new Invalidation(table);
        UiAssert.That(ReferenceEquals(_invalidation.Table, table), "内核与失效协议实例接的不是同一棵树");
        table.PhaseDriver = this;
    }

    /// <summary>卸下相位驱动权（测试拆装/树域回收用；失效协议实例不动）。</summary>
    public void Detach()
    {
        if (ReferenceEquals(_table.PhaseDriver, this)) _table.PhaseDriver = null;
    }

    // ── 只读面 ──────────────────────────────────────────────────────────────

    /// <summary>被驱动的树域。</summary>
    public NodeTable Table => _table;

    /// <summary>该树域的失效协议实例。</summary>
    public Invalidation Invalidation => _invalidation;

    /// <summary>三时域时钟（定时器归本平面；tween/时间轴归状态层的 P4）。</summary>
    public Clock Clock => _clock;

    /// <summary>当前法定相位；帧外报告 <see cref="FramePhase.P3_Commands"/>。</summary>
    public FramePhase CurrentPhase => _table.Phase;

    /// <summary>本帧（帧内）或上一帧（帧外）的帧号。**单调递增**，第一帧为 1。</summary>
    public ulong FrameId => _frameId;

    /// <summary>是否正在 <see cref="Tick"/> 内（相位机不可重入）。</summary>
    public bool InFrame => _inFrame;

    /// <summary>上一帧的诊断锁存（P9 定格；帧内读到的是上一帧的完整账）。</summary>
    public FrameDiag Diagnostics => _latched;

    /// <summary>本帧正在累积的活账（帧内不完整，仅供帧内钩子看进度；正式读数走 <see cref="Diagnostics"/>）。</summary>
    public FrameDiag LiveDiagnostics => _live;

    /// <summary>尚未被 P0 排空的输入包数。</summary>
    public int PendingInputCount => _input.count;

    // ── 钩子与接缝 ──────────────────────────────────────────────────────────

    /// <summary>帧首钩子（参数 = 本帧帧号）。此刻相位窗口等同 P3：可自由写状态。</summary>
    public event Action<ulong>? BeforeFrame;

    /// <summary>帧尾钩子（参数 = 本帧帧号）。在 P9 锁存**之后**调用，钩子读到的就是本帧的完整诊断。</summary>
    public event Action<ulong>? AfterFrame;

    /// <summary>相位进入回调（诊断/测试挂点：每帧按 P0…P9 各调一次）。</summary>
    public Action<FramePhase>? PhaseWatch { get; set; }

    /// <summary>P0 输入消费者（M1-21 的命中 + 事件派发）。未装时输入不排空。</summary>
    public InputSink? InputHandler { get; set; }

    /// <summary>录带钩子（M1-24 的唯一挂点）：见 <see cref="InputTapHook"/>。</summary>
    public InputTapHook? InputTap { get; set; }

    /// <summary>P4 状态相位（M2-01：Binder.Flush → TickTimelines → Resolve，三步之间不插任何其他系统）。</summary>
    public PhaseHook? StateStep { get; set; }

    /// <summary>
    /// P6 派生列重算（world / worldVisual）。未装时内核调 <see cref="NodeTable.DrainDerivedFull"/> 全量版；
    /// M1-14 装上增量版后，全量版留作增量正确性门的神谕。
    /// </summary>
    public PhaseHook? SettleStep { get; set; }

    /// <summary>P6 下行通道下钻访问器（M1-14 的级联写者）。未装时不下钻——脏位与子树戳留到下一帧。</summary>
    public DownVisitor? DownStep { get; set; }

    /// <summary>
    /// P7 收尾（M1-14）：五条通道**全部排完之后**、相位仍是 P7 的那一刻。
    ///
    /// 为什么必须有这个钩子：升级到 Structure 的动作（slack 溢出 / 段键变化 / 可见性跃迁）
    /// 是排到第五条通道才知道的，而它不能走 <see cref="Invalidation.Mark"/> 回队——
    /// P7 的排水水位已冻结，回队的条目本帧不再被排（<see cref="Invalidation.FreezeForRenderDrain"/>），
    /// 画面要停一帧；更别说 P7 的 Mark 本身就是一次相位违例计数。
    /// 于是「排完五条再整编一次」落在这里：仍在 P7 窗口内，仍先于 P8 提交。
    /// </summary>
    public PhaseHook? DrainTailStep { get; set; }

    /// <summary>P8 提交（M1-14：序派生 + 合并区间上传 + 段属性块集中写）。</summary>
    public PhaseHook? SubmitStep { get; set; }

    // ── 命令泵（P3）──────────────────────────────────────────────────────────

    /// <summary>登记命令泵（一种命令类型一个泵；重复登记即断言）。</summary>
    public void RegisterCommandPump(ICommandPump pump)
    {
        if (pump == null) { UiAssert.That(false, "RegisterCommandPump 收到 null"); return; }
        for (int i = 0; i < _pumpCount; i++)
            UiAssert.That(!ReferenceEquals(_pumps[i], pump), "命令泵重复登记");
        if (_pumpCount == _pumps.Length)
            Array.Resize(ref _pumps, _pumps.Length == 0 ? 4 : _pumps.Length * 2);
        _pumps[_pumpCount++] = pump;
    }

    /// <summary>注销命令泵。</summary>
    public void UnregisterCommandPump(ICommandPump pump)
    {
        for (int i = 0; i < _pumpCount; i++)
        {
            if (!ReferenceEquals(_pumps[i], pump)) continue;
            for (int k = i + 1; k < _pumpCount; k++) _pumps[k - 1] = _pumps[k];
            _pumps[--_pumpCount] = null!;
            return;
        }
    }

    /// <summary>已登记的命令泵数。</summary>
    public int CommandPumpCount => _pumpCount;

    // ── 输入单入口 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 投递输入包（**宿主的第二个也是最后一个入口**：指针/键盘/IME/手柄同一队列）。
    /// 任何时刻可调；一律在**下一个 P0** 被排空——「当帧新建节点当帧不可命中」这条明文契约
    /// 的另一半就是它（P0 命中读的是上帧收敛的序）。
    /// 入队前调一次 <see cref="InputTap"/>，这是录带的唯一挂点。
    /// </summary>
    public void PostInput(in InputPacket packet)
    {
        InputPacket stamped = packet.WithSeq(_inputSeq++);
        InputTap?.Invoke(_frameId, in stamped);
        _input.Enqueue(stamped);
    }

    /// <summary>
    /// 回放注入（M1-24）：与 <see cref="PostInput"/> 同路，但**不触发录带钩子**——
    /// 回放时再录一遍等于把带子写成两倍长。Seq 沿用带里的原值。
    /// </summary>
    public void PostInputFromTape(in InputPacket packet)
    {
        _input.Enqueue(packet);
        if (packet.Seq >= _inputSeq) _inputSeq = packet.Seq + 1;
    }

    // ── 帧 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 走完一帧 P0–P9（宿主唯一入口）。不可重入：帧内再调即断言并**真拒绝**。
    /// </summary>
    /// <param name="time">宿主时间快照（两域绝对时刻须单调不减）。</param>
    public void Tick(in FrameTime time)
    {
        UiAssert.That(!_inFrame, "Tick 重入：一帧只能有一个相位机在走（钩子里不许再 Tick）");
        if (_inFrame) return;

        _inFrame = true;
        try
        {
            _frameId++;
            _live.Reset();
            _live.FrameId = _frameId;
            _invalidation.BeginFrame(_frameId);
            for (int i = 0; i < _pumpCount; i++) _pumps[i].BeginFrame(_frameId);
            BeforeFrame?.Invoke(_frameId);

            var ctx = new FrameContext(this, _table, _invalidation, _frameId, time, FramePhase.P0_Input);
            RunPhase(ref ctx, FramePhase.P0_Input);
            RunPhase(ref ctx, FramePhase.P1_Clock);
            RunPhase(ref ctx, FramePhase.P2_GlobalInvalidate);
            RunPhase(ref ctx, FramePhase.P3_Commands);
            RunPhase(ref ctx, FramePhase.P4_State);
            RunPhase(ref ctx, FramePhase.P5_Layout);
            RunPhase(ref ctx, FramePhase.P6_Settle);
            RunPhase(ref ctx, FramePhase.P7_RenderDrain);
            RunPhase(ref ctx, FramePhase.P8_Submit);
            RunPhase(ref ctx, FramePhase.P9_FrameEnd);
        }
        finally
        {
            _invalidation.Unfreeze();
            _table.SetPhase(FramePhase.P3_Commands, this);   // 帧外窗口 = P3 的重入契约
            _inFrame = false;
        }
    }

    private void RunPhase(ref FrameContext ctx, FramePhase phase)
    {
        _table.SetPhase(phase, this);
        ctx.Phase = phase;
        PhaseWatch?.Invoke(phase);

        long t0 = Stopwatch.GetTimestamp();
        switch (phase)
        {
            case FramePhase.P0_Input: P0Input(ref ctx); break;
            case FramePhase.P1_Clock: P1Clock(ref ctx); break;
            case FramePhase.P2_GlobalInvalidate: P2GlobalInvalidate(); break;
            case FramePhase.P3_Commands: P3Commands(ref ctx); break;
            case FramePhase.P4_State: StateStep?.Invoke(ref ctx); break;
            case FramePhase.P5_Layout: P5Layout(ref ctx); break;
            case FramePhase.P6_Settle: P6Settle(ref ctx); break;
            case FramePhase.P7_RenderDrain: P7RenderDrain(ref ctx); break;
            case FramePhase.P8_Submit: SubmitStep?.Invoke(ref ctx); break;
            case FramePhase.P9_FrameEnd: P9FrameEnd(t0); break;
            default: UiAssert.That(false, "未知相位"); break;
        }
        if (phase != FramePhase.P9_FrameEnd) _live.AddPhaseTicks(phase, Stopwatch.GetTimestamp() - t0);
    }

    /// <summary>P0：排空输入队列 → 命中（上帧收敛序）→ 事件链派发。</summary>
    private void P0Input(ref FrameContext ctx)
    {
        int n = _input.count;
        if (n == 0) return;
        InputSink? sink = InputHandler;
        if (sink == null) { _live.InputsHeld = n; return; }   // 无消费者不排空（同通道排水的纪律）

        // 只排「进 P0 瞬间已在队」的快照：P0 回调里投递的包属于下一帧，不做同帧递归派发。
        for (int i = 0; i < n; i++)
        {
            if (!_input.TryDequeue(out InputPacket p)) break;
            _live.InputsDrained++;
            sink(in p, ref ctx);
        }
        _live.InputsHeld = _input.count;
    }

    /// <summary>P1：Scaled/Unscaled 跟宿主时刻，Manual 只认显式步进；随后按 (deadline, 插入序) 触发。</summary>
    private void P1Clock(ref FrameContext ctx)
    {
        _clock.Advance(in ctx.Time);
        _live.TimersFired = _clock.Fire();
    }

    /// <summary>P2：挂起的全局失效在此统一生效（唯一生效点，不变量 5）。</summary>
    private void P2GlobalInvalidate() => _live.GlobalsApplied = _invalidation.ApplyPendingGlobal();

    /// <summary>
    /// P3：命令 FIFO 按波次排空。每波 = 进波瞬间的在队快照；波内新入队的构成下一波。
    /// 撞上 <see cref="Abi.CommandWaveLimit"/> 即停：余量原样留在队里下一帧继续跑（延帧，不丢），
    /// 并把责任链拷进诊断——不变量 10 要求超限告警必须能指认「谁生成了谁」。
    /// </summary>
    private void P3Commands(ref FrameContext ctx)
    {
        if (_pumpCount == 0) return;

        int wave = 0;
        while (wave < Abi.CommandWaveLimit)
        {
            if (PendingCommands() == 0) break;
            ctx.Wave = wave;
            int executed = 0;
            for (int i = 0; i < _pumpCount; i++) executed += _pumps[i].DrainWave(ref ctx);
            wave++;
            _live.CommandWaves = wave;
            _live.CommandsExecuted += executed;
            if (executed == 0) break;                       // 队里有东西却没人执行：不空转
        }
        ctx.Wave = -1;

        int left = PendingCommands();
        if (left == 0) return;

        _live.CommandsDeferred = left;
        _live.CommandWaveOverflow = true;
        for (int i = 0; i < _pumpCount; i++)
        {
            ICommandPump pump = _pumps[i];
            int links = pump.ChainCount;
            for (int k = 0; k < links; k++) _live.AddLink(pump.ChainAt(k));
        }
        UiAssert.That(_live.ChainCount > 0,
            "波次超限却没有责任链（不变量 10）——无责任链的超限记录本身是诊断实现 bug");
    }

    private int PendingCommands()
    {
        int n = 0;
        for (int i = 0; i < _pumpCount; i++) n += _pumps[i].PendingCount;
        return n;
    }

    /// <summary>P5：度量（Text 通道）先于布局（Layout 通道）——三级惰性的第一级在这里落地。</summary>
    private void P5Layout(ref FrameContext ctx)
    {
        _live.DrainedHandles += _invalidation.DrainChannel(ref ctx, Ch.Text);
        _live.DrainedHandles += _invalidation.DrainChannel(ref ctx, Ch.Layout);
    }

    /// <summary>P6：paintOrder 定形 → 派生列一遍算 → 向下通道按子树戳下钻。</summary>
    private void P6Settle(ref FrameContext ctx)
    {
        _table.ApplyStructure();
        if (SettleStep != null) SettleStep(ref ctx);
        else _table.DrainDerivedFull();
        DownVisitor? down = DownStep;
        if (down != null) _live.DownVisited = _invalidation.DrainDown(down);
    }

    /// <summary>
    /// P7：先冻结排水水位，再按 content → transform → color → visible → structure 排五通道，
    /// 最后调 <see cref="DrainTailStep"/>（升级重编 / 包含剪枝 / 孤岛同步都在那一刻，相位仍是 P7）。
    /// 冻结之后写进来的失效（违约的用户代码、排水器彼此的连锁）本帧不再被消化，下一帧照常排。
    /// </summary>
    private void P7RenderDrain(ref FrameContext ctx)
    {
        _invalidation.FreezeForRenderDrain();
        _live.DrainedHandles += _invalidation.DrainChannel(ref ctx, Ch.Content);
        _live.DrainedHandles += _invalidation.DrainChannel(ref ctx, Ch.Transform);
        _live.DrainedHandles += _invalidation.DrainChannel(ref ctx, Ch.Color);
        _live.DrainedHandles += _invalidation.DrainChannel(ref ctx, Ch.Visible);
        _live.DrainedHandles += _invalidation.DrainChannel(ref ctx, Ch.Structure);
        DrainTailStep?.Invoke(ref ctx);
    }

    /// <summary>
    /// P9：诊断锁存 → 句柄统一换代 → 解冻 → AfterFrame。
    /// 顺序不可换：锁存必须先于 <see cref="AfterFrame"/>（钩子读的就是本帧的完整账），
    /// 因此 P9 的相位计时**不含 AfterFrame 钩子自身**——那部分算在宿主头上。
    /// </summary>
    private void P9FrameEnd(long t0)
    {
        _invalidation.LatchFrame();
        _table.EndFrame();
        _live.AddPhaseTicks(FramePhase.P9_FrameEnd, Stopwatch.GetTimestamp() - t0);
        _live.Invalidation.CopyFrom(_invalidation.LastFrame);
        _latched.CopyFrom(_live);
        _invalidation.Unfreeze();
        AfterFrame?.Invoke(_frameId);
    }
}
