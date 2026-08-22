namespace FairyNext.Core.Events;

/// <summary>派发相（链快照的两趟：capture 逆序下行、bubble 正序上行）。</summary>
public enum EventPhase : byte
{
    /// <summary>捕获相（根 → 目标）。</summary>
    Capture = 0,
    /// <summary>目标本身。</summary>
    Target = 1,
    /// <summary>冒泡相（目标 → 根）。</summary>
    Bubble = 2,
    /// <summary>直发（不走链：monitor 附加目标、CharInput 直发焦点）。</summary>
    Direct = 3,
}

/// <summary>监听相（注册时选；一个节点可对同一事件各注册一条）。</summary>
public enum ListenPhase : byte
{
    /// <summary>冒泡相监听（默认）。</summary>
    Bubble = 0,
    /// <summary>捕获相监听（modal 遮罩 / 手势抢占的真实用例）。</summary>
    Capture = 1,
}

/// <summary>
/// 派发上下文（架构事件·不变量 1【编译错】）：**ref struct**——
/// 存字段、闭包捕获、await 跨越全是编译错误。旧架构 <c>InputEvent</c> 类终身复用，
/// 「不能跨帧缓存引用」只是文档警告；这里类型系统把警告变成不可违反的事实。
///
/// 语义三件：
///  · <see cref="StopPropagation"/> 截断**本次派发的剩余全部节点**（含尚未跑的另一相）；
///  · <see cref="PreventDefault"/> 只置位，由基元自己解释（拖拽 DragStart 可被它拦下）；
///  · <see cref="CaptureTouch"/> 置标志，派发循环消费后把 <see cref="Sender"/> 注册进
///    该触点的 monitor 名单（此后离开宿主仍收 move/end——手势能成立的关键）。
/// </summary>
public ref struct EventCtx
{
    private readonly NodeTable _table;
    private byte _flags;

    private const byte FlagStopped = 1 << 0;
    private const byte FlagDefaultPrevented = 1 << 1;
    private const byte FlagCaptureRequested = 1 << 2;

    /// <summary>当前链节点（代际句柄；每步派发前已 <c>TryResolve</c> 双验）。</summary>
    public NodeHandle Sender;

    /// <summary>事件源（命中目标；整条链上恒定）。</summary>
    public NodeHandle Initiator;

    /// <summary>当前派发相。</summary>
    public EventPhase Phase;

    /// <summary>触点 id（非指针事件为 -1）。</summary>
    public int TouchId;

    internal EventCtx(NodeTable table, NodeHandle initiator, int touchId)
    {
        _table = table;
        _flags = 0;
        Sender = initiator;
        Initiator = initiator;
        Phase = EventPhase.Direct;
        TouchId = touchId;
    }

    /// <summary>树域（回调里改状态走正常 setter；P0 的写只 Mark，排水未开始）。</summary>
    public readonly NodeTable Table => _table;

    /// <summary>截断传播（本次派发的剩余节点全部不再收到）。</summary>
    public void StopPropagation() => _flags |= FlagStopped;

    /// <summary>阻止默认行为（基元自解释）。</summary>
    public void PreventDefault() => _flags |= FlagDefaultPrevented;

    /// <summary>
    /// 抢占触点：把 <see cref="Sender"/> 注册进本触点的 monitor 名单。
    /// 拖拽 / DragDrop / 手势识别器全部是这个基元之上的**库代码**，不占节点身份。
    /// </summary>
    public void CaptureTouch() => _flags |= FlagCaptureRequested;

    /// <summary>传播是否已被截断。</summary>
    public readonly bool IsPropagationStopped => (_flags & FlagStopped) != 0;

    /// <summary>默认行为是否已被阻止。</summary>
    public readonly bool IsDefaultPrevented => (_flags & FlagDefaultPrevented) != 0;

    /// <summary>本步是否请求了触点抢占（派发循环消费后清位）。</summary>
    internal readonly bool TouchCaptureRequested => (_flags & FlagCaptureRequested) != 0;

    /// <summary>消费抢占请求（派发循环每步调一次）。</summary>
    internal void ClearCaptureRequest() => _flags = (byte)(_flags & ~FlagCaptureRequested);
}

/// <summary>
/// 监听器签名：<c>ctx</c> 按 ref（ref struct 出不了本帧）、载荷按 <c>in</c>（复制即安全）。
/// </summary>
/// <typeparam name="T">载荷类型。</typeparam>
/// <param name="ctx">派发上下文。</param>
/// <param name="arg">载荷。</param>
public delegate void EventFn<T>(ref EventCtx ctx, in T arg);
