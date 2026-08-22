using FairyNext.Numerics;

namespace FairyNext.Core.Events;

/// <summary>
/// 修饰键位掩码（宿主约定的语义化封装；<see cref="InputPacket.Modifiers"/> 的解释在本平面）。
/// </summary>
[Flags]
public enum Modifiers : ushort
{
    /// <summary>无。</summary>
    None = 0,
    /// <summary>Shift。</summary>
    Shift = 1 << 0,
    /// <summary>Control。</summary>
    Ctrl = 1 << 1,
    /// <summary>Alt / Option。</summary>
    Alt = 1 << 2,
    /// <summary>Command / Win。</summary>
    Meta = 1 << 3,
}

/// <summary>增量轴的种类（硬件中立 canonical 单位，见 <see cref="AxisDelta"/>）。</summary>
public enum AxisKind : byte
{
    /// <summary>未定（协议违约）。</summary>
    None = 0,
    /// <summary>垂直滚动。</summary>
    ScrollY = 1,
    /// <summary>水平滚动。</summary>
    ScrollX = 2,
    /// <summary>旋转（毫度制）。</summary>
    Rotate = 3,
}

/// <summary>IME 合成态（<see cref="TextInput"/> 的伴随位；文档不被合成串污染，见平面四 B）。</summary>
public enum ImeState : byte
{
    /// <summary>非合成态的普通字符。</summary>
    None = 0,
    /// <summary>合成进行中（合成串不写文档）。</summary>
    Composing = 1,
    /// <summary>合成提交。</summary>
    Committed = 2,
}

/// <summary>
/// 指针载荷（readonly struct，按 <c>in</c> 传——**复制即安全**，不存在「事件对象终身复用」）。
/// </summary>
public readonly struct PointerInput
{
    /// <summary>stage 空间坐标（y 向下）。</summary>
    public readonly Vector2 StagePos;
    /// <summary>宿主屏幕坐标（运行时只透传）。</summary>
    public readonly Vector2 ScreenPos;
    /// <summary>指针 id（鼠标恒 0，多点触控各一）。</summary>
    public readonly int TouchId;
    /// <summary>键号（0 = 左 / 1 = 右 / 2 = 中）。</summary>
    public readonly byte Button;
    /// <summary>连击计数（1 = 单击，2 = 双击；见 <see cref="TouchSlot"/> 的四元组判定）。</summary>
    public readonly byte ClickCount;
    /// <summary>按住时长（秒；抬起/移动时为自按下起的时长）。</summary>
    public readonly float HoldTime;
    /// <summary>修饰键。</summary>
    public readonly Modifiers Mods;

    /// <summary>建载荷。</summary>
    public PointerInput(Vector2 stagePos, Vector2 screenPos, int touchId,
        byte button = 0, byte clickCount = 0, float holdTime = 0f, Modifiers mods = Modifiers.None)
    {
        StagePos = stagePos;
        ScreenPos = screenPos;
        TouchId = touchId;
        Button = button;
        ClickCount = clickCount;
        HoldTime = holdTime;
        Mods = mods;
    }
}

/// <summary>键盘载荷。</summary>
public readonly struct KeyInput
{
    /// <summary>宿主键码（运行时不解释，回放按数值重放）。</summary>
    public readonly int Key;
    /// <summary>修饰键。</summary>
    public readonly Modifiers Mods;

    /// <summary>建载荷。</summary>
    public KeyInput(int key, Modifiers mods = Modifiers.None)
    {
        Key = key;
        Mods = mods;
    }
}

/// <summary>字符载荷（<see cref="InputPacket.Id"/> 是 UTF-32 码点，故此处不是 char）。</summary>
public readonly struct TextInput
{
    /// <summary>UTF-32 码点。</summary>
    public readonly int Codepoint;
    /// <summary>IME 合成态。</summary>
    public readonly ImeState Ime;

    /// <summary>建载荷。</summary>
    public TextInput(int codepoint, ImeState ime = ImeState.None)
    {
        Codepoint = codepoint;
        Ime = ime;
    }
}

/// <summary>
/// 硬件中立增量轴载荷（不变量·入口 8）：**有符号整数 canonical 单位**，旋转类毫度制。
/// 零值与亚单位在入口就被拒绝——亚单位累积只存在于平台归一层，浮点增量不进本平面。
/// </summary>
public readonly struct AxisDelta
{
    /// <summary>轴种类。</summary>
    public readonly AxisKind Kind;
    /// <summary>canonical 整数增量（非零）。</summary>
    public readonly int Delta;

    /// <summary>建载荷。</summary>
    public AxisDelta(AxisKind kind, int delta)
    {
        Kind = kind;
        Delta = delta;
    }
}

/// <summary>
/// 类型化事件 id（架构平面四 B）：泛型参数携带载荷类型，
/// <c>AddListener</c> / <c>Dispatch</c> 的载荷错配是**编译错误**，不是运行期 cast 异常。
/// 4B，无引用字段。
/// </summary>
/// <typeparam name="T">载荷类型（readonly struct）。</typeparam>
public readonly struct EventId<T> : IEquatable<EventId<T>>
{
    /// <summary>原始 id（内建 &lt; 64 参与 <c>builtinMask</c> 剪枝；用户事件 ≥ 64）。</summary>
    public readonly int Raw;

    internal EventId(int raw) => Raw = raw;

    /// <summary>空 id（未注册）。</summary>
    public static EventId<T> None => new EventId<T>(-1);

    /// <summary>是否为空 id。</summary>
    public bool IsNone => Raw < 0;

    /// <summary>是否落在内建区间 [0, 64)。</summary>
    public bool IsBuiltin => (uint)Raw < (uint)UiEvents.BuiltinLimit;

    /// <inheritdoc/>
    public bool Equals(EventId<T> other) => Raw == other.Raw;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is EventId<T> o && Equals(o);
    /// <inheritdoc/>
    public override int GetHashCode() => Raw;
    /// <inheritdoc/>
    public override string ToString() => $"EventId<{typeof(T).Name}>#{Raw}";
}

/// <summary>
/// 内建事件词表（**烘焙常量区 [0, 64)**，append-only：id 一经入库永不重编号——
/// 录带与编译产物里的 eventId 是字节级契约）。
///
/// 位宽是硬约束：内建 id 必须 &lt; 64 才装得进 <c>ListenerBlock.BuiltinMask</c> 这一个 u64，
/// 溢出由 <see cref="BuiltinMaskFits"/> 在**编译期**拦下（常量除零 = CS0020），不是运行期断言
/// （架构事件·不变量 2【编译错·静态】）。
/// </summary>
public static class UiEvents
{
    /// <summary>内建区间上限（= <c>builtinMask</c> 的位宽）。</summary>
    public const int BuiltinLimit = 64;

    /// <summary>已占用的内建 id 数（新增内建事件在此 +1，并追加到 <see cref="Names"/>）。</summary>
    public const int BuiltinCount = 12;

    /// <summary>
    /// 静态断言：<see cref="BuiltinCount"/> 超过 <see cref="BuiltinLimit"/> 时本常量退化为
    /// **常量除零**，编译直接失败（CS0020）。恒为 1，测试按此读值确认门还在。
    /// </summary>
    public const int BuiltinMaskFits = 1 / (BuiltinCount <= BuiltinLimit ? 1 : 0);

    /// <summary>指针按下（冒泡）。</summary>
    public static readonly EventId<PointerInput> TouchBegin = new EventId<PointerInput>(0);
    /// <summary>指针移动（默认发给根 + 各 monitor；见 CaptureTouch 基元）。</summary>
    public static readonly EventId<PointerInput> TouchMove = new EventId<PointerInput>(1);
    /// <summary>指针抬起（冒泡自当前 target）。</summary>
    public static readonly EventId<PointerInput> TouchEnd = new EventId<PointerInput>(2);
    /// <summary>指针取消（系统抢走触点）。</summary>
    public static readonly EventId<PointerInput> TouchCancel = new EventId<PointerInput>(3);
    /// <summary>点击（downChain 快照判定，见 <see cref="TouchSlot"/>）。</summary>
    public static readonly EventId<PointerInput> Click = new EventId<PointerInput>(4);
    /// <summary>指针进入（LCA 链 diff，无重复派发）。</summary>
    public static readonly EventId<PointerInput> RollOver = new EventId<PointerInput>(5);
    /// <summary>指针离开。</summary>
    public static readonly EventId<PointerInput> RollOut = new EventId<PointerInput>(6);
    /// <summary>键按下（焦点节点起冒泡；焦点路由随 M2-10）。</summary>
    public static readonly EventId<KeyInput> KeyDown = new EventId<KeyInput>(7);
    /// <summary>键抬起。</summary>
    public static readonly EventId<KeyInput> KeyUp = new EventId<KeyInput>(8);
    /// <summary>字符输入（**直发**焦点节点，不冒泡）。</summary>
    public static readonly EventId<TextInput> CharInput = new EventId<TextInput>(9);
    /// <summary>增量轴（滚轮/触控板/手柄轴，canonical 整数单位）。</summary>
    public static readonly EventId<AxisDelta> Axis = new EventId<AxisDelta>(10);
    /// <summary>右键点击。</summary>
    public static readonly EventId<PointerInput> RightClick = new EventId<PointerInput>(11);

    /// <summary>内建 id → 名（诊断/回放可读化；下标 = id）。</summary>
    public static readonly string[] Names =
    {
        "TouchBegin", "TouchMove", "TouchEnd", "TouchCancel", "Click",
        "RollOver", "RollOut", "KeyDown", "KeyUp", "CharInput", "Axis", "RightClick",
    };

    /// <summary>内建 id → 载荷类型（<see cref="EventRegistry"/> 用它挡住同名异型注册）。</summary>
    public static readonly Type[] Payloads =
    {
        typeof(PointerInput), typeof(PointerInput), typeof(PointerInput), typeof(PointerInput),
        typeof(PointerInput), typeof(PointerInput), typeof(PointerInput),
        typeof(KeyInput), typeof(KeyInput), typeof(TextInput), typeof(AxisDelta), typeof(PointerInput),
    };

    /// <summary>id → 名（越界返回 <c>"user#id"</c>）。</summary>
    public static string NameOf(int raw) =>
        (uint)raw < (uint)Names.Length ? Names[raw] : EventRegistry.NameOf(raw);
}

/// <summary>
/// 用户事件登记处：分配 **≥ 64** 的 id（内建区留给烘焙常量）。
/// 同名同型重复登记**幂等**（返回同一 id）——事件 id 与 <c>typeof</c> 一样是进程级身份，
/// 组件库在静态构造里各自 Register 不该互相打架；同名异型是协议违约（断言 + 抛）。
/// </summary>
public static class EventRegistry
{
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, int> Ids = new Dictionary<string, int>(StringComparer.Ordinal);
    private static readonly List<string> NamesById = new List<string>();
    private static readonly List<Type> TypesById = new List<Type>();

    /// <summary>已登记的用户事件数。</summary>
    public static int Count
    {
        get { lock (Gate) return NamesById.Count; }
    }

    /// <summary>登记（或取回）一个用户事件 id。</summary>
    /// <typeparam name="T">载荷类型。</typeparam>
    /// <param name="name">全局唯一名（建议带库前缀）。</param>
    public static EventId<T> Register<T>(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            UiAssert.That(false, "EventRegistry.Register 收到空名");
            return EventId<T>.None;
        }
        lock (Gate)
        {
            if (Ids.TryGetValue(name, out int existing))
            {
                int slot = existing - UiEvents.BuiltinLimit;
                bool same = TypesById[slot] == typeof(T);
                UiAssert.That(same,
                    $"事件 \"{name}\" 已按载荷 {TypesById[slot].Name} 登记，不能改登记为 {typeof(T).Name}");
                if (!same) throw new InvalidOperationException($"事件名 {name} 的载荷类型冲突");
                return new EventId<T>(existing);
            }
            int raw = UiEvents.BuiltinLimit + NamesById.Count;
            Ids.Add(name, raw);
            NamesById.Add(name);
            TypesById.Add(typeof(T));
            return new EventId<T>(raw);
        }
    }

    /// <summary>用户事件 id → 名（未登记返回 <c>"user#id"</c>）。</summary>
    public static string NameOf(int raw)
    {
        lock (Gate)
        {
            int slot = raw - UiEvents.BuiltinLimit;
            return (uint)slot < (uint)NamesById.Count ? NamesById[slot] : "user#" + raw;
        }
    }
}
