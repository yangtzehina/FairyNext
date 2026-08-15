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
