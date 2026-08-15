namespace FairyNext.Core;

/// <summary>
/// 宿主每帧交给内核的时间快照（架构「平面二 · 时间宪法」核心数据结构 <c>FrameTime</c>）。
/// 双域并存、一处声明：<see cref="ScaledDt"/> 吃 timeScale，<see cref="UnscaledDt"/> 不吃——
/// 旧架构 Timers 固定 unscaled 而 GTweener 默认 scaled 的双语义陷阱在这里被消灭。
///
/// **与架构文档的一处形态差异**：文档的 FrameTime 带 <c>ulong frameId</c>，这里没有。
/// frameId 由 <see cref="UiKernel"/> 独占递增（<see cref="UiKernel.FrameId"/>）——把它交给宿主传，
/// 「frameId 单调递增」就成了宿主的义务，而子树戳、回放带、诊断锁存全都建在这条单调性上。
/// 第三域 Manual 不在本结构里：它按定义只由 <see cref="Clock.StepManual"/> 显式推进（回放门的前提）。
/// </summary>
public readonly struct FrameTime
{
    /// <summary>本帧缩放时长（秒，吃 timeScale）。</summary>
    public readonly float ScaledDt;

    /// <summary>本帧真实时长（秒，不吃 timeScale）。UI 默认时域。</summary>
    public readonly float UnscaledDt;

    /// <summary>缩放时域的绝对时刻（秒）。单调不减，由宿主保证；内核在 P1 断言。</summary>
    public readonly double ScaledNow;

    /// <summary>真实时域的绝对时刻（秒）。单调不减。</summary>
    public readonly double UnscaledNow;

    /// <summary>直接给四个分量（宿主同时持有 dt 与绝对时刻时用）。</summary>
    public FrameTime(float scaledDt, float unscaledDt, double scaledNow, double unscaledNow)
    {
        ScaledDt = scaledDt;
        UnscaledDt = unscaledDt;
        ScaledNow = scaledNow;
        UnscaledNow = unscaledNow;
    }

    /// <summary>第一帧：绝对时刻 = 本帧 dt（宿主只有 dt 时的起点）。</summary>
    public static FrameTime First(float scaledDt, float unscaledDt) =>
        new FrameTime(scaledDt, unscaledDt, scaledDt, unscaledDt);

    /// <summary>下一帧：绝对时刻按 dt 累加（宿主只有 dt 时的推进；累加在一处做，杜绝两份时间线）。</summary>
    public FrameTime Step(float scaledDt, float unscaledDt) =>
        new FrameTime(scaledDt, unscaledDt, ScaledNow + scaledDt, UnscaledNow + unscaledDt);

    /// <inheritdoc/>
    public override string ToString() =>
        $"FrameTime(sDt={ScaledDt} uDt={UnscaledDt} sNow={ScaledNow} uNow={UnscaledNow})";
}

/// <summary>
/// 一帧的执行上下文（架构接口 <c>IChannelDrain.Drain(ref FrameContext ctx, …)</c> 的那个 ctx）。
/// <c>ref struct</c>：只能沿栈传递，不可能被子系统偷偷缓存过帧——「跨帧缓存后失效」这一类
/// 在类型上就不成立。M1-07 交付时 FrameContext 尚不存在，其 <c>Drain</c> 签名留了「M1-08 补参数」
/// 的接缝，本包补上。
///
/// 携带的是**本帧的全部环境**：帧号、当前相位、时间快照、树域与失效协议实例；
/// P3 期间另带波次号 <see cref="Wave"/>（其余相位恒 -1）。
/// </summary>
public ref struct FrameContext
{
    private readonly UiKernel? _kernel;
    private readonly NodeTable _table;
    private readonly Invalidation _invalidation;

    /// <summary>本帧帧号（内核单调递增；子树戳与回放带的排序键）。</summary>
    public readonly ulong FrameId;

    /// <summary>宿主交来的时间快照。</summary>
    public readonly FrameTime Time;

    /// <summary>当前法定相位（内核逐相位改写）。</summary>
    public FramePhase Phase;

    /// <summary>P3 命令波次（0 起）；非 P3 相位恒 -1。</summary>
    public int Wave;

    // FrameTime 按值传：ref struct 的构造函数收 in 参数会被 ref-safety 分析当成「可能捕获引用」，
    // 于是 default 之类的临时值传不进来。24B 的值拷贝换掉整类逃逸分析噪音，值得。
    internal FrameContext(UiKernel? kernel, NodeTable table, Invalidation invalidation,
        ulong frameId, FrameTime time, FramePhase phase)
    {
        _kernel = kernel;
        _table = table;
        _invalidation = invalidation;
        FrameId = frameId;
        Time = time;
        Phase = phase;
        Wave = -1;
    }

    /// <summary>驱动本帧的内核；<see cref="IsDetached"/> 的上下文为 null。</summary>
    public readonly UiKernel? Kernel => _kernel;

    /// <summary>本帧所属树域。</summary>
    public readonly NodeTable Table => _table;

    /// <summary>本帧的失效协议实例。</summary>
    public readonly Invalidation Invalidation => _invalidation;

    /// <summary>
    /// 是否为脱离内核的上下文：测试/工具直调 <see cref="Core.Invalidation.DrainChannel(Ch)"/> 时合成，
    /// 时间快照为 default、帧号取失效协议的当前帧号。真实帧永远不是 detached。
    /// </summary>
    public readonly bool IsDetached => _kernel == null;

    /// <summary>合成一个脱离内核的上下文（无宿主路径：单测与离线工具）。</summary>
    internal static FrameContext Detached(Invalidation invalidation) =>
        new FrameContext(null, invalidation.Table, invalidation,
            invalidation.FrameId, default, invalidation.Table.Phase);
}

/// <summary>输入包种类（封闭集；新增种类 append-only——回放带按数值解释包）。</summary>
public enum InputKind : byte
{
    /// <summary>无（未初始化包 = 协议违约）。</summary>
    None = 0,
    /// <summary>指针按下。</summary>
    PointerDown = 1,
    /// <summary>指针抬起。</summary>
    PointerUp = 2,
    /// <summary>指针移动。</summary>
    PointerMove = 3,
    /// <summary>指针取消（系统抢走触点）。</summary>
    PointerCancel = 4,
    /// <summary>滚轮/触控板滚动（增量在 DeltaX/DeltaY）。</summary>
    Wheel = 5,
    /// <summary>键按下。</summary>
    KeyDown = 6,
    /// <summary>键抬起。</summary>
    KeyUp = 7,
    /// <summary>字符输入（Id = UTF-32 码点）。</summary>
    Char = 8,
    /// <summary>焦点获得。</summary>
    FocusGained = 9,
    /// <summary>焦点失去。</summary>
    FocusLost = 10,
}

/// <summary>
/// 宿主输入包（<see cref="UiKernel.PostInput"/> 的唯一载荷）。
/// **定长 POD、无引用字段**：这是 M1-24 输入带能做 RLE、能逐字节比对、能跨进程回放的前提。
/// IME 组合串一类变长载荷不进本结构（M2-10 走 <c>ITextInputHost</c> 侧信道），
/// 否则录带就得处理堆对象，回放确定性门立刻失去意义。
/// </summary>
public readonly struct InputPacket
{
    /// <summary>包种类。</summary>
    public readonly InputKind Kind;
    /// <summary>设备号（多指针/多手柄区分）。</summary>
    public readonly byte Device;
    /// <summary>修饰键位掩码（宿主约定，运行时不解释）。</summary>
    public readonly ushort Modifiers;
    /// <summary>指针 id / 键码 / 码点，按 <see cref="Kind"/> 复用。</summary>
    public readonly int Id;
    /// <summary>逻辑坐标 x（stage 空间）。</summary>
    public readonly float X;
    /// <summary>逻辑坐标 y（stage 空间，y 向下）。</summary>
    public readonly float Y;
    /// <summary>增量 x（移动/滚轮）。</summary>
    public readonly float DeltaX;
    /// <summary>增量 y（移动/滚轮）。</summary>
    public readonly float DeltaY;
    /// <summary>宿主时间戳（秒；运行时只透传，不参与调度）。</summary>
    public readonly double Time;
    /// <summary>入队序（<see cref="UiKernel.PostInput"/> 分配，单调递增）——录带的排序键。</summary>
    public readonly uint Seq;

    /// <summary>建包（Seq 由内核在 PostInput 时补）。</summary>
    public InputPacket(InputKind kind, int id = 0, float x = 0f, float y = 0f,
        float deltaX = 0f, float deltaY = 0f, ushort modifiers = 0, byte device = 0, double time = 0.0)
    {
        Kind = kind;
        Device = device;
        Modifiers = modifiers;
        Id = id;
        X = x;
        Y = y;
        DeltaX = deltaX;
        DeltaY = deltaY;
        Time = time;
        Seq = 0;
    }

    private InputPacket(in InputPacket src, uint seq)
    {
        Kind = src.Kind;
        Device = src.Device;
        Modifiers = src.Modifiers;
        Id = src.Id;
        X = src.X;
        Y = src.Y;
        DeltaX = src.DeltaX;
        DeltaY = src.DeltaY;
        Time = src.Time;
        Seq = seq;
    }

    /// <summary>补上入队序（内核内部；录带看到的就是补过序的包）。</summary>
    internal InputPacket WithSeq(uint seq) => new InputPacket(in this, seq);

    /// <inheritdoc/>
    public override string ToString() => $"{Kind}#{Id}@({X},{Y}) seq={Seq}";
}

/// <summary>相位钩子（子系统挂进法定相位的统一签名）。</summary>
/// <param name="ctx">本帧上下文（相位、帧号、时间、树域）。</param>
public delegate void PhaseHook(ref FrameContext ctx);

/// <summary>P0 输入消费者（M1-21 的命中+派发挂这里；未挂时输入不排空，留到下一帧）。</summary>
/// <param name="packet">输入包（入队序 = 派发序）。</param>
/// <param name="ctx">本帧上下文。</param>
public delegate void InputSink(in InputPacket packet, ref FrameContext ctx);

/// <summary>
/// 录带钩子（M1-24 InputTape 的**唯一挂点**）：<see cref="UiKernel.PostInput"/> 入队前调一次。
/// 参数是 (投递时的帧号, 已补序的包)——包在**下一个 P0** 被消费，回放按 (帧号, Seq) 重放即可。
/// </summary>
/// <param name="frameId">投递时的帧号。</param>
/// <param name="packet">已补 <see cref="InputPacket.Seq"/> 的包。</param>
public delegate void InputTapHook(ulong frameId, in InputPacket packet);
