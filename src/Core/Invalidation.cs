using System.Runtime.CompilerServices;

namespace FairyNext.Core;

/// <summary>
/// 失效理由（架构「平面二 · 时间宪法」机制⑫）。每次 Mark 必携，成本 +1 字节；
/// 诊断面按理由聚合，用来回答「这个 quad 本帧为何重写」。
/// **无默认值**是不变量 15 的执法形态：不存在无理由的失效。
/// </summary>
public enum InvalidateReason : byte
{
    /// <summary>用户代码经公共 setter 写入。</summary>
    UserWrite = 0,
    /// <summary>Binder apply（绑定行）写入。</summary>
    BindingRow = 1,
    /// <summary>controller 换页写 authored。</summary>
    GearPage = 2,
    /// <summary>时间轴采样写 anim 通道。</summary>
    Timeline = 3,
    /// <summary>纯 tween 直写小道。</summary>
    TweenDirect = 4,
    /// <summary>布局排水回写（P5 解出的 rect 落回 Content/Transform）。</summary>
    LayoutDerived = 5,
    /// <summary>P2 全局失效窗口的 fan-out。</summary>
    GlobalInvalidate = 6,
    /// <summary>
    /// P6 下行下钻的落叶 Mark（M1-14 补：级联值变了的叶要在 P7 重上色/重判隐显）。
    /// 文档的七值表没有它——因为文档没写下钻**如何**把变化交回上行队列。诊断面按理由聚合的口径
    /// 要求这条路径可归因：把它记成 UserWrite 会让「谁失效了我」在最常见的 grayed/α 级联上说谎。
    /// </summary>
    CascadeDown = 7,
}

/// <summary>
/// 全局失效源（**封闭两源**，架构机制⑥：不建通用注册表——两个 if 不值得一层抽象）。
/// 生效点只有一个：P2。
/// </summary>
public enum GlobalSource : byte
{
    /// <summary>字形库 generation 换代（append-only + generation，绑定期定向订阅）。</summary>
    GlyphStore = 0,
    /// <summary>屏幕尺寸变化（单值 + 根 Mark）。</summary>
    ScreenSize = 1,
}

/// <summary>
/// 通道方向掩码（<see cref="Ch"/> 的方向语义：上行=队列 / 下行=子树戳 / 派生=query-pull）。
/// 通道枚举本身保持「一位一属性域」的纯粹形态，方向分组放在这里。
/// </summary>
public static class ChMask
{
    /// <summary>上行通道（7 条，各一条句柄环形队列）。</summary>
    public const Ch Up = Ch.Content | Ch.Transform | Ch.Color | Ch.Visible | Ch.Structure | Ch.Layout | Ch.Text;

    /// <summary>下行通道（不入队列，置子树戳惰性下钻）。</summary>
    public const Ch Down = Ch.DownColor | Ch.DownVisible | Ch.DownLayer;

    /// <summary>派生位（置父链、遇脏即停，query-pull 消费）。</summary>
    public const Ch Derived = Ch.BoundsD;

    /// <summary>全部已定义通道位（Mark 收到位集外的位即协议违约）。</summary>
    public const Ch All = Up | Down | Derived;

    /// <summary>上行通道条数 = 队列条数。</summary>
    public const int UpCount = 7;

    /// <summary>单个上行通道位 → 队列槽位（0..6）；非单位上行位返回 -1。</summary>
    public static int UpSlot(Ch channel) => channel switch
    {
        Ch.Content => 0,
        Ch.Transform => 1,
        Ch.Color => 2,
        Ch.Visible => 3,
        Ch.Structure => 4,
        Ch.Layout => 5,
        Ch.Text => 6,
        _ => -1,
    };

    /// <summary>队列槽位 → 上行通道位。</summary>
    public static Ch UpChannelAt(int slot) =>
        (uint)slot < UpCount ? (Ch)(1 << slot) : Ch.None;
}

/// <summary>
/// 通道排水器（渲染流 / 布局图实现）。排水调度器按相位表逐通道调用；
/// **一个通道只能有一个排水器**（架构：每个位由唯一相位消费）。
/// 传入的 span 是本批快照：排水期间对同节点的再 Mark 会重新入队，落进下一批（不丢、只延迟）。
/// span 内的句柄在调用瞬间都是活的（已死者由调度器丢弃并计入诊断），但排水器自己若在回调中
/// 销毁节点，仍须对后续元素走 <see cref="NodeTable.TryResolve"/>。
/// </summary>
public interface IChannelDrain
{
    /// <summary>本排水器消费的通道位集（只允许上行位）。</summary>
    Ch Consumes { get; }

    /// <summary>排空一条通道的一批句柄。</summary>
    /// <param name="ctx">本帧上下文（帧号/相位/时间；M1-08 相位机补上的参数）。</param>
    /// <param name="channel">被排空的单个通道位。</param>
    /// <param name="queue">本批句柄（入队序 = Mark 序，FIFO）。</param>
    void Drain(ref FrameContext ctx, Ch channel, ReadOnlySpan<NodeHandle> queue);
}

/// <summary>
/// 下行通道下钻回调：<paramref name="index"/> 的级联视觉/层需要重算，
/// <paramref name="channels"/> 是本节点生效的下行位（自身位 ∪ 从下钻根继承的位）。
/// 保证父先于子（级联可单趟顺序写）。
/// </summary>
public delegate void DownVisitor(uint index, Ch channels);

/// <summary>
/// 一条上行通道的句柄环形队列（结构体化、稳态零 GC）。
/// 存**句柄**不存指针：入队后节点可能被 Destroy，排水时按代际校验丢弃（指针会悬空，句柄只会解引用失败）。
/// </summary>
internal struct ChQueue
{
    /// <summary>首次入队时分配的槽数（架构：初始各 256 槽按需倍增）。</summary>
    internal const int InitialCapacity = 256;

    private NodeHandle[]? _ring;
    private int _head;
    private int _count;

    /// <summary>在队条数。</summary>
    internal readonly int Count => _count;

    /// <summary>当前槽数（0 = 还没分配过）。</summary>
    internal readonly int Capacity => _ring?.Length ?? 0;

    /// <summary>入队（满则 2 倍扩容）。</summary>
    internal void Enqueue(NodeHandle h)
    {
        if (_ring == null || _count == _ring.Length) Grow();
        NodeHandle[] ring = _ring!;
        int tail = _head + _count;
        if (tail >= ring.Length) tail -= ring.Length;
        ring[tail] = h;
        _count++;
    }

    /// <summary>单条出队（FIFO）。整批排水走 <see cref="Contiguous"/>，本方法供增量消费者。</summary>
    internal bool TryDequeue(out NodeHandle h)
    {
        if (_ring == null || _count == 0) { h = default; return false; }
        h = _ring[_head];
        if (++_head == _ring.Length) _head = 0;
        if (--_count == 0) _head = 0;
        return true;
    }

    /// <summary>
    /// 取整批的连续视图（必要时先解绕）。
    /// 解绕只在「部分出队后又入队到绕回」时发生一次搬家；整批排水后 <see cref="Clear"/> 把 head 归零，
    /// 稳态下永不绕回、永不搬家。
    /// </summary>
    internal Span<NodeHandle> Contiguous()
    {
        Unwrap();
        return _ring == null ? Span<NodeHandle>.Empty : new Span<NodeHandle>(_ring, 0, _count);
    }

    /// <summary>压缩后保留前 n 条（仅在 <see cref="Contiguous"/> 之后调用，此时 head==0）。</summary>
    internal void Retain(int n) => _count = n;

    /// <summary>清空（不清数组内容：句柄是值类型，不构成引用泄漏；缓冲留给下一帧复用）。</summary>
    internal void Clear() { _head = 0; _count = 0; }

    private void Unwrap()
    {
        if (_ring == null || _count == 0) { _head = 0; return; }
        if (_head == 0) return;
        if (_head + _count <= _ring.Length)
        {
            Array.Copy(_ring, _head, _ring, 0, _count);   // 重叠区间：Array.Copy 语义等同 memmove
            _head = 0;
            return;
        }
        var bigger = new NodeHandle[_ring.Length * 2];
        int first = _ring.Length - _head;
        Array.Copy(_ring, _head, bigger, 0, first);
        Array.Copy(_ring, 0, bigger, first, _count - first);
        _ring = bigger;
        _head = 0;
    }

    private void Grow()
    {
        int cap = _ring == null ? InitialCapacity : _ring.Length * 2;
        var bigger = new NodeHandle[cap];
        if (_ring != null && _count > 0)
        {
            int first = Math.Min(_count, _ring.Length - _head);
            Array.Copy(_ring, _head, bigger, 0, first);
            if (first < _count) Array.Copy(_ring, 0, bigger, first, _count - first);
        }
        _ring = bigger;
        _head = 0;
    }
}

/// <summary>
/// 失效诊断（架构机制⑫）：每通道 Mark 计数 + 按 <see cref="InvalidateReason"/> 聚合，
/// 外加协议各条路径的收据（去重命中、父链停止、隐藏免下钻、陈旧句柄丢弃……）。
/// 计数是**每帧**量：P9 由 <see cref="Invalidation.LatchFrame"/> 锁存进 LastFrame 并清零。
/// </summary>
public sealed class InvalidationDiag
{
    /// <summary>通道计数槽位数（Ch 是 ushort，低 16 位一位一槽）。</summary>
    public const int ChannelSlots = 16;

    private readonly long[] _byChannel = new long[ChannelSlots];
    private readonly long[] _byReason = new long[8];

    /// <summary>Mark 调用次数（含被等值切断之前的调用不计——切断发生在 setter 侧，根本不到这里）。</summary>
    public long Marks;
    /// <summary>上行入队条数（= 脏位原为 0 的位数）。</summary>
    public long Enqueued;
    /// <summary>去重命中次数（位已置 ⇒ 不入队）。</summary>
    public long Deduped;
    /// <summary>下行置戳的节点数（含祖先链补戳）。</summary>
    public long DownStamps;
    /// <summary>下行置戳时「本帧已戳即停」的次数。</summary>
    public long DownStampStops;
    /// <summary>BoundsD 父链访问到的节点数。</summary>
    public long BoundsSteps;
    /// <summary>BoundsD「遇脏即停」的次数。</summary>
    public long BoundsStops;
    /// <summary>BoundsD 被 query-pull 消费的子树数。</summary>
    public long BoundsPulled;
    /// <summary>下行排水访问到的节点数。</summary>
    public long DownVisits;
    /// <summary>下行排水跳过的隐藏子树数（免下钻）。</summary>
    public long HiddenSkips;
    /// <summary>排水时丢弃的陈旧句柄数（入队后节点死了）。</summary>
    public long StaleDropped;
    /// <summary>交付给排水器的句柄总数。</summary>
    public long Drained;
    /// <summary>因通道没有注册排水器而跳过的排水调用数（脏位与队列原样留到下一次）。</summary>
    public long NoDrainerSkips;
    /// <summary>全局失效挂起请求数。</summary>
    public long GlobalRequests;
    /// <summary>全局失效实际生效次数（只可能发生在 P2）。</summary>
    public long GlobalApplied;
    /// <summary>相位写门违例数（不变量 9）：<c>Phase &gt;= P7</c> 时发生的 Mark 次数。</summary>
    public long PhaseViolations;
    /// <summary>被写门顺延到下一帧的队列条目数（降级路径的收据：不丢、只延迟）。</summary>
    public long DeferredEntries;

    /// <summary>某通道的 Mark 计数（传单个通道位）。</summary>
    public long MarksOf(Ch channel)
    {
        int b = BitIndex((uint)channel);
        return b >= 0 ? _byChannel[b] : 0;
    }

    /// <summary>某理由的 Mark 计数。</summary>
    public long MarksOf(InvalidateReason reason)
    {
        int r = (int)reason;
        return (uint)r < (uint)_byReason.Length ? _byReason[r] : 0;
    }

    /// <summary>清零（P9 锁存后调用）。</summary>
    public void Reset()
    {
        Array.Clear(_byChannel, 0, _byChannel.Length);
        Array.Clear(_byReason, 0, _byReason.Length);
        Marks = Enqueued = Deduped = DownStamps = DownStampStops = 0;
        BoundsSteps = BoundsStops = BoundsPulled = 0;
        DownVisits = HiddenSkips = StaleDropped = Drained = NoDrainerSkips = 0;
        GlobalRequests = GlobalApplied = 0;
        PhaseViolations = DeferredEntries = 0;
    }

    /// <summary>拷贝一份（锁存用；不共享数组）。</summary>
    public void CopyFrom(InvalidationDiag src)
    {
        Array.Copy(src._byChannel, _byChannel, _byChannel.Length);
        Array.Copy(src._byReason, _byReason, _byReason.Length);
        Marks = src.Marks; Enqueued = src.Enqueued; Deduped = src.Deduped;
        DownStamps = src.DownStamps; DownStampStops = src.DownStampStops;
        BoundsSteps = src.BoundsSteps; BoundsStops = src.BoundsStops; BoundsPulled = src.BoundsPulled;
        DownVisits = src.DownVisits; HiddenSkips = src.HiddenSkips;
        StaleDropped = src.StaleDropped; Drained = src.Drained; NoDrainerSkips = src.NoDrainerSkips;
        GlobalRequests = src.GlobalRequests; GlobalApplied = src.GlobalApplied;
        PhaseViolations = src.PhaseViolations; DeferredEntries = src.DeferredEntries;
    }

    internal void Count(Ch channels, InvalidateReason reason)
    {
        Marks++;
        int r = (int)reason;
        if ((uint)r < (uint)_byReason.Length) _byReason[r]++;
        uint v = (uint)channels;
        for (int b = 0; v != 0 && b < ChannelSlots; b++, v >>= 1)
            if ((v & 1u) != 0) _byChannel[b]++;
    }

    private static int BitIndex(uint v)
    {
        if (v == 0 || (v & (v - 1)) != 0) return -1;    // 非单 bit
        int b = 0;
        while ((v & 1u) == 0) { v >>= 1; b++; }
        return b < ChannelSlots ? b : -1;
    }
}

/// <summary>调用点记录（debug 构建的环形缓冲元素；release 下永不写入）。</summary>
public readonly struct MarkSite
{
    /// <summary>被 Mark 的节点下标。</summary>
    public readonly uint Index;
    /// <summary>通道位集。</summary>
    public readonly Ch Channels;
    /// <summary>失效理由。</summary>
    public readonly InvalidateReason Reason;
    /// <summary>调用点成员名（<c>[CallerMemberName]</c>）。</summary>
    public readonly string Member;
    /// <summary>调用点行号（<c>[CallerLineNumber]</c>；经 NodeTable 钩子进来的为 0）。</summary>
    public readonly int Line;
    /// <summary>发生帧号。</summary>
    public readonly ulong Frame;

    internal MarkSite(uint index, Ch channels, InvalidateReason reason, string member, int line, ulong frame)
    {
        Index = index; Channels = channels; Reason = reason; Member = member; Line = line; Frame = frame;
    }

    /// <inheritdoc/>
    public override string ToString() => $"#{Index} {Channels} {Reason} @{Member}:{Line} f{Frame}";
}

/// <summary>
/// 失效协议（架构「平面二 · 时间宪法」机制②⑥⑫，不变量 2/5/13/15 的实现点）：
/// **全组唯一的 Mark 入口**，一棵树域一个实例。
///
/// 三条方向语义（通道自带方向，没有「补丁通道」）：
///  1. **上行**（Content/Transform/Color/Visible/Structure/Layout/Text）：每条一条句柄环形队列；
///     Mark 时脏位原为 0 才入队（天然去重），消费即清位，跨帧存活靠脏位而非队列。
///  2. **下行**（DownColor/DownVisible/DownLayer）：不入队，置子树戳 <c>stamp=frameId</c> 并沿祖先链补戳；
///     <see cref="DrainDown"/> 只下钻本帧被戳的分支，隐藏子树整棵免下钻、重新显示时由 Visible 排水补戳。
///  3. **派生**（BoundsD）：置父链、遇脏即停，不占队列，由 <see cref="ConsumeBounds"/> 按需 query-pull。
///
/// 两条纪律：
///  - **reason 必填**（不变量 15）：<see cref="Mark"/> 没有省略 reason 的重载，编译期就没有无理由的失效。
///  - **写前不做等值判断**：等值切断是 setter 侧的宪法条款（M1-06/M1-09），本平面被调用即代表值真的变了。
///
/// 与架构文档的两处形态差异（语义等价，接线时点不同）：
///  - 文档写 <c>static class Invalidation</c>；这里是**每树域一个实例**——树域是多份的（多 stage 各一棵树），
///    静态全局态会把跨树失效混成一本账，也让测试互相污染。M1-08 的 UiKernel 持有实例并按相位调度。
///  - <see cref="IChannelDrain.Drain"/> 的 <c>ref FrameContext ctx</c> 参数**已由 M1-08 补上**；
///    排水的调用时机同样归 <see cref="UiKernel"/>（P5 Text/Layout、P7 五通道），本类只管「谁排、排什么」。
/// </summary>
public sealed class Invalidation
{
    private readonly NodeTable _table;
    private readonly ChQueue[] _queue = new ChQueue[ChMask.UpCount];      // 入队缓冲
    private readonly ChQueue[] _batch = new ChQueue[ChMask.UpCount];      // 排水快照缓冲（与入队缓冲交换）
    private readonly IChannelDrain?[] _drains = new IChannelDrain?[ChMask.UpCount];
    private readonly int[] _drainCap = new int[ChMask.UpCount];           // 冻结期的可排水位（-1 = 不限）
    private readonly InvalidationDiag _diag = new InvalidationDiag();
    private readonly InvalidationDiag _lastFrame = new InvalidationDiag();
    private bool _frozen;

    private ulong _frameId;
    private uint _stampEpoch = 1;
    private bool _pendingGlyph;
    private bool _pendingScreen;
    private int _drainingSlot = -1;

    // 下钻用显式栈（无递归、稳态零 GC）
    private uint[] _walkIndex = new uint[64];
    private Ch[] _walkBits = new Ch[64];
    private int _walkTop;

    // 走查期间（DrainDown 回调里）的下行置戳挂起集（M1-14b 修复 1）：
    // 趟末代号推进（含回绕清列）之后统一按新代补戳。脏位照常即时置（见 StampDown）。
    private bool _drainingDown;
    private uint[] _pendingStamp = new uint[16];
    private int _pendingCount;

    /// <summary>
    /// 接管一棵树的失效协议：装上 <see cref="NodeTable.InvalidationHook"/>，
    /// 此后树里每一次「等值切断通过」的写入都落到本实例。一棵树只能有一个实例。
    /// </summary>
    public Invalidation(NodeTable table)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        UiAssert.That(table.InvalidationHook == null,
            "一棵树只能接一个失效协议实例（Mark 是全组唯一入口，两本账 = 漏失效）");
        table.InvalidationHook = OnTableWrite;
        table.ReattachHook = OnReattach;
        for (int slot = 0; slot < ChMask.UpCount; slot++) _drainCap[slot] = -1;
    }

    /// <summary>卸下钩子（测试拆装/树域回收用）。</summary>
    public void Detach()
    {
        if (_table.InvalidationHook == OnTableWrite) _table.InvalidationHook = null;
        if (_table.ReattachHook == OnReattach) _table.ReattachHook = null;
    }

    /// <summary>被接管的树。</summary>
    public NodeTable Table => _table;

    /// <summary>当前帧号（由相位机在帧首推进）。</summary>
    public ulong FrameId => _frameId;

    /// <summary>
    /// 当前的下钻代（子树戳值；0 是「未戳」哨兵，故递增时跳过 0）。
    ///
    /// **不是 frameId 截断**（M1-14 修正）。原形态把戳值钉成 frameId，落地即漏：用户代码写状态的
    /// 常规窗口是**帧外**（两次 Tick 之间，相位报 P3），那时 <see cref="BeginFrame"/> 还没跑，
    /// 戳上的是**上一帧**的值；下一帧 P6 按新 frameId 路由，从根第一步就判「本帧无事」，
    /// 整条下行通道静默丢失——而它的症状是「父置灰了、子节点还是原色」，没有任何账能指认。
    /// 正确的代是「**自上次下钻以来**」：写者一律戳当前代，<see cref="DrainDown"/> 消费完这一代后 +1。
    /// 于是帧内写与帧外写落在同一代里，都被下一次下钻收走。
    /// </summary>
    public uint FrameStamp => _stampEpoch;

    /// <summary>本帧诊断（每通道 Mark 计数 + 按理由聚合 + 各路径收据）。</summary>
    public InvalidationDiag Diagnostics => _diag;

    /// <summary>上一帧锁存的诊断（P9 由 <see cref="LatchFrame"/> 写入）。</summary>
    public InvalidationDiag LastFrame => _lastFrame;

    /// <summary>
    /// 帧首推进（相位机 M1-08 在 P1 之前调）。**不动下钻代**——代由 <see cref="DrainDown"/>
    /// 消费时推进（见 <see cref="FrameStamp"/>）：帧外写入与帧内写入必须落在同一代里，
    /// 否则帧外那批（用户代码最常见的窗口）永远等不到下钻。
    /// 老戳自然过期，常规推进**不需要**遍历清戳；u32 回绕（每 2^32 次下钻一次）那一趟由
    /// <see cref="DrainDown"/> 顺带整列清戳——不清的话陈年戳可能恰等于新代号，
    /// 「本代已戳即停」会在归纳不成立的节点上骗停置戳，整条下行通道丢失（2026-08 审计）。
    /// </summary>
    public void BeginFrame(ulong frameId) => _frameId = frameId;

    /// <summary>
    /// 测试钩：直接设定下钻代号（**只供回绕测试**——把代号逼到 MaxValue 要 2^32 次真下钻）。
    /// 0 是「未戳」哨兵，禁设。生产代码不得调用。
    /// </summary>
    internal void ForceStampEpochForTest(uint epoch)
    {
        UiAssert.That(epoch != 0, "下钻代号 0 是「未戳」哨兵，不可设为代号");
        if (epoch != 0) _stampEpoch = epoch;
    }

    /// <summary>P9 锁存：把本帧诊断拷进 <see cref="LastFrame"/> 并清零本帧计数。</summary>
    public void LatchFrame()
    {
        _lastFrame.CopyFrom(_diag);
        _diag.Reset();
    }

    // ────────────────────────────────────────────────────────────────────────
    // Mark：唯一入口（不变量 2/15）
    // ────────────────────────────────────────────────────────────────────────

#if DEBUG
    /// <summary>
    /// 置脏（**全组唯一入口**）。<paramref name="reason"/> 必填、无默认值（不变量 15）。
    /// 语义：位原为 0 才入队（幂等 O(1)，同帧重复 Mark 不改变队列长度）；
    /// 写前**不做**等值判断——等值切断在 setter 侧，调到这里就代表值真的变了。
    /// 一次调用可携多位（典型：<c>Ch.Color | Ch.DownColor</c>），上行/下行/派生各按自己的方向处理。
    /// </summary>
    /// <param name="node">目标节点句柄（已失效句柄 = 断言 + no-op）。</param>
    /// <param name="channels">通道位集（必须落在 <see cref="ChMask.All"/> 内）。</param>
    /// <param name="reason">失效理由（诊断按此聚合）。</param>
    /// <param name="site">调用点成员名——debug 构建自动填充，release 构建本参数**不存在**。</param>
    /// <param name="line">调用点行号——同上。</param>
    public void Mark(NodeHandle node, Ch channels, InvalidateReason reason,
        [CallerMemberName] string site = "", [CallerLineNumber] int line = 0)
#else
    /// <summary>
    /// 置脏（**全组唯一入口**）。<paramref name="reason"/> 必填、无默认值（不变量 15）。
    /// 语义：位原为 0 才入队（幂等 O(1)）；写前不做等值判断（切断在 setter 侧）。
    /// </summary>
    /// <param name="node">目标节点句柄（已失效句柄 = 断言 + no-op）。</param>
    /// <param name="channels">通道位集（必须落在 <see cref="ChMask.All"/> 内）。</param>
    /// <param name="reason">失效理由（诊断按此聚合）。</param>
    public void Mark(NodeHandle node, Ch channels, InvalidateReason reason)
#endif
    {
        if (!_table.TryResolve(node, out uint idx))
        {
            UiAssert.That(false, "Mark 于已失效句柄（gen 不符或已 DEAD）");
            return;
        }
#if DEBUG
        RecordSite(idx, channels, reason, site, line);
#endif
        MarkCore(idx, channels, reason);
    }

    /// <summary>
    /// 下行专用入口（架构门面的 <c>MarkDown</c>）：只接受下行位。
    /// 本仓库比文档多要一个 <paramref name="reason"/>——诊断面按理由聚合要求**所有**失效路径可归因，
    /// 下行也不例外（严格超集，不违反不变量 15）。
    /// </summary>
    public void MarkDown(NodeHandle node, Ch downChannels, InvalidateReason reason)
    {
        UiAssert.That((downChannels & ~ChMask.Down) == 0, "MarkDown 只接受下行通道位");
        Mark(node, downChannels & ChMask.Down, reason);
    }

    /// <summary>
    /// 「重新显示补戳」：把整棵子树的下行重算重新挂上（架构机制②——这是旧架构第五通道
    /// <c>_NotifyDescendantStreams</c> 补丁的结构化替代）。
    ///
    /// 常规隐显不需要调它：<c>SetVisible</c> 自己就 Mark 了 <c>Visible|DownVisible</c>，
    /// 下钻在同帧 P6 就会看见。本方法给「级联语境由结构变化间接翻转」的路径用
    /// （典型：把子树挂到一棵可见的树上）——真调用点是 <see cref="NodeTable.ReattachHook"/>
    /// 经 <see cref="OnReattach"/>（M1-14b 修复 2）：重接已戳子树时补挂整棵子树的下行重算。
    /// </summary>
    public void RestampOnShow(NodeHandle node, InvalidateReason reason) =>
        Mark(node, ChMask.Down, reason);

    /// <summary>
    /// 重接钩子（<see cref="NodeTable.ReattachHook"/>：AddChildAt 把节点接到**新父**下时调）。
    /// 已戳子树挂到未戳父下会打破下行路由的归纳基础「已戳 ⇒ 祖先全已戳」——下一趟从根
    /// 判「本代无事」，整代下行脏位丢失（2026-08 审计实测：SetAlpha 后搬家，DownColor 永久滞留）。
    ///
    /// 只补新父链救不了摘链隔代的形状：摘链期间的下行写把戳停在摘链根为止，本代被消费作废后
    /// 子树**内部**的戳停留在旧代，路由进不去。子树戳非零（= 子树里曾有过下行变化，可能有
    /// 未消费的）即 <see cref="RestampOnShow"/> 整棵补挂，是两种形状共同的最小正确修法；
    /// 戳为零（从未有过下行变化）零成本早退——建树期的海量 AddChild 走这条。
    /// </summary>
    private void OnReattach(uint index, WriteSource source)
    {
        if (_table.GetSubtreeStamp(index) == 0) return;
        RestampOnShow(_table.HandleOf(index), ReasonOf(source));
        // RestampOnShow 的置戳在 index 自己「本代已戳」时立即早停——那正是被重接打破的归纳，
        // 新父链必须显式补戳。走查期间不补：挂起集趟末按新代整链补，彼时无陈戳可骗停。
        if (_drainingDown) return;
        uint parent = _table.ParentIndex(index);
        if (parent != NodeTable.NoIndex) StampChain(parent);
    }

    /// <summary>下标版 Mark（内部路径：调用方已解引用过，不再重复校验代际）。</summary>
    internal void MarkIndex(uint index, Ch channels, InvalidateReason reason) =>
        MarkCore(index, channels, reason);

    private void MarkCore(uint index, Ch channels, InvalidateReason reason)
    {
        UiAssert.That(channels != Ch.None, "Mark 收到空通道集（通道归属封闭：没有归属的属性不该有 setter）");
        UiAssert.That((channels & ~ChMask.All) == 0, "Mark 收到未定义的通道位");
        // 不变量 9：P7/P8 禁改。断言之外照常入队——降级路径必须在断言之外，
        // 「不丢、只延迟」由 M1-08 的排水水位冻结（<see cref="FreezeForRenderDrain"/>）保证：
        // 位照置、队照入，只是本帧不再排到它们。
        bool violation = _table.Phase >= FramePhase.P7_RenderDrain;
        UiAssert.That(!violation,
            "相位门：P7/P8 的 Mark 是违约写（不变量 9）——记录照做，但只能在下一帧排水");
        if (violation) _diag.PhaseViolations++;

        _diag.Count(channels, reason);

        Ch up = channels & ChMask.Up;
        if (up != Ch.None) EnqueueUp(index, up);

        Ch down = channels & ChMask.Down;
        if (down != Ch.None) StampDown(index, down);

        if ((channels & Ch.BoundsD) != 0) MarkBoundsChain(index);
    }

    private void EnqueueUp(uint index, Ch up)
    {
        uint bits = (uint)up;
        uint dirty = _table.GetDirty(index);
        uint fresh = bits & ~dirty;                  // 位原为 0 才入队 = 天然去重
        _diag.Deduped += PopCount(bits & dirty);
        if (fresh == 0) return;

        _table.OrDirty(index, fresh);
        NodeHandle h = _table.HandleOf(index);
        for (int slot = 0; slot < ChMask.UpCount; slot++)
        {
            if ((fresh & (1u << slot)) == 0) continue;
            _queue[slot].Enqueue(h);
            _diag.Enqueued++;
        }
    }

    /// <summary>
    /// 下行置戳：本节点置位 + 自身与**祖先链**全部盖上当前代的戳。
    /// 祖先补戳不是可选项——P6 从根按戳路由，不给祖先盖戳的子树根本走不到。
    /// 「本代已戳即停」的归纳基础：一个节点被盖戳时它的祖先必然同批被盖，故已戳 ⇒ 祖先全已戳。
    ///
    /// **走查期间（<see cref="DrainDown"/> 回调里）的置戳入挂起集、趟末补戳**（M1-14b 修复 1）：
    /// 走查正在消费当前代，趟末就把这一代作废——此刻盖当前代的戳是「出生即过期」：
    /// 目标分支若已出栈或被剪掉，下一趟从根第一步就判「本代无事」，脏位永久搁浅
    /// （2026-08 审计实测 BUSY 场景）。脏位照常即时置（本趟还够得着的分支照常消费），
    /// 挂起集在代号推进（含回绕清列）**之后**统一按新代补戳——走查期间的写与走查之后的写
    /// 落在同一代里，都被下一次下钻收走。
    /// </summary>
    private void StampDown(uint index, Ch down)
    {
        _table.OrDirty(index, (uint)down);
        if (_drainingDown) { PushPendingStamp(index); return; }
        StampChain(index);
    }

    /// <summary>自身与祖先链盖当前代戳，「本代已戳即停」（<see cref="StampDown"/> 的置戳半边）。</summary>
    private void StampChain(uint index)
    {
        for (uint cur = index; cur != NodeTable.NoIndex; cur = _table.ParentIndex(cur))
        {
            if (_table.GetSubtreeStamp(cur) == _stampEpoch) { _diag.DownStampStops++; return; }
            _table.SetSubtreeStamp(cur, _stampEpoch);
            _diag.DownStamps++;
        }
    }

    private void PushPendingStamp(uint index)
    {
        if (_pendingCount == _pendingStamp.Length)
            Array.Resize(ref _pendingStamp, _pendingStamp.Length * 2);
        _pendingStamp[_pendingCount++] = index;
    }

    /// <summary>
    /// BoundsD 派生位：自身与父链逐级置位，**遇脏即停**，不入任何队列。
    /// 停得住的依据与下行戳同构：置位集合对「取祖先」封闭（本节点脏 ⇒ 全部祖先脏），
    /// 所以碰到第一个已脏节点时，它上面的全都已经脏了。
    /// <see cref="ConsumeBounds"/> 按整棵子树清位正是为了维持这条封闭性。
    /// </summary>
    private void MarkBoundsChain(uint index)
    {
        const uint bit = (uint)Ch.BoundsD;
        for (uint cur = index; cur != NodeTable.NoIndex; cur = _table.ParentIndex(cur))
        {
            if ((_table.GetDirty(cur) & bit) != 0) { _diag.BoundsStops++; return; }
            _table.OrDirty(cur, bit);
            _diag.BoundsSteps++;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 查询面
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>某通道队列的在队条数（诊断/测试；不含下行与 BoundsD——它们不入队列）。</summary>
    public int QueueLength(Ch channel)
    {
        int slot = ChMask.UpSlot(channel);
        return slot < 0 ? 0 : _queue[slot].Count;
    }

    /// <summary>
    /// 窥视一条上行通道的在队快照（**不出队、不清位**）。
    ///
    /// 给 P6 用：派生列的增量重算要知道「哪些子树的几何脏了」，而那些条目的**消费权归 P7**——
    /// 在 P6 出队等于把 P7 的活干掉一半，流永远等不到那批句柄。窥视是唯一自洽的读法：
    /// 一条失效可以被多个相位**读**，但只能被一个相位**消费**。
    ///
    /// 返回的 span 指向内部环形缓冲：拿着它的期间不得再 Mark（会扩容搬家）。
    /// 调用方要跨 Mark 使用就先拷走——P6 的排水器正是先拷成脏根表再动手。
    /// </summary>
    public ReadOnlySpan<NodeHandle> Peek(Ch channel)
    {
        int slot = ChMask.UpSlot(channel);
        UiAssert.That(slot >= 0, "Peek 只接受单个上行通道位");
        return slot < 0 ? ReadOnlySpan<NodeHandle>.Empty : _queue[slot].Contiguous();
    }

    /// <summary>节点是否带有给定通道位中的任意一位。</summary>
    public bool IsDirty(NodeHandle node, Ch channels) =>
        _table.TryResolve(node, out uint i) && (_table.GetDirty(i) & (uint)channels) != 0;

    /// <summary>节点的脏位全集（诊断用）。</summary>
    public Ch DirtyOf(NodeHandle node) =>
        _table.TryResolve(node, out uint i) ? (Ch)(_table.GetDirty(i) & (uint)ChMask.All) : Ch.None;

    /// <summary>节点的子树戳是否为本帧（= 本帧其子树里有下行变化）。</summary>
    public bool IsStampedThisFrame(NodeHandle node) =>
        _table.TryResolve(node, out uint i) && _table.GetSubtreeStamp(i) == _stampEpoch;

    /// <summary>BoundsD 是否脏（query-pull 的探询半边，不清位）。</summary>
    public bool IsBoundsDirty(NodeHandle node) => IsDirty(node, Ch.BoundsD);

    /// <summary>
    /// BoundsD 的 query-pull 消费：脏则**清掉整棵子树**的 BoundsD 并返回 true（调用方随即重算包围）。
    /// 为什么是整棵子树而不是单点：置位集合必须对「取祖先」封闭，只清自己会留下
    /// 「后代还脏 ⇒ 后续 Mark 在后代处就停住 ⇒ 祖先再也收不到通知」的漏报洞。
    /// 消费方本来就要走一遍子树算包围，这一趟清位是同一趟的成本。
    /// </summary>
    public bool ConsumeBounds(NodeHandle node)
    {
        if (!_table.TryResolve(node, out uint root)) return false;
        const uint bit = (uint)Ch.BoundsD;
        if ((_table.GetDirty(root) & bit) == 0) return false;

        _walkTop = 0;
        PushWalk(root, Ch.None);
        while (_walkTop > 0)
        {
            PopWalk(out uint idx, out _);
            _table.ClearDirty(idx, bit);
            for (uint c = _table.LastChildIndex(idx); c != NodeTable.NoIndex; c = _table.PrevSiblingIndex(c))
                PushWalk(c, Ch.None);
        }
        _diag.BoundsPulled++;
        return true;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 上行排水调度（骨架：本包不实现任何具体排水器）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 注册排水器。一个通道只能有一个消费者（架构：每个位由唯一相位消费）；重复注册即断言。
    /// 排水的**调用时机**归相位机（M1-08）：本类只负责「谁排、排什么」，不假设「何时排」。
    /// </summary>
    public void Register(IChannelDrain drain)
    {
        if (drain == null) { UiAssert.That(false, "Register 收到 null 排水器"); return; }
        Ch consumes = drain.Consumes;
        UiAssert.That((consumes & ~ChMask.Up) == 0,
            "IChannelDrain 只消费上行通道（下行走子树戳、BoundsD 走 query-pull）");
        for (int slot = 0; slot < ChMask.UpCount; slot++)
        {
            if (((uint)consumes & (1u << slot)) == 0) continue;
            UiAssert.That(_drains[slot] == null,
                "通道 " + ChMask.UpChannelAt(slot) + " 已有排水器（一个通道只能有一个消费者）");
            _drains[slot] = drain;
        }
    }

    /// <summary>注销排水器（只摘自己占的槽）。</summary>
    public void Unregister(IChannelDrain drain)
    {
        for (int slot = 0; slot < ChMask.UpCount; slot++)
            if (ReferenceEquals(_drains[slot], drain)) _drains[slot] = null;
    }

    /// <summary>查通道的排水器（未注册返回 null）。</summary>
    public IChannelDrain? DrainerOf(Ch channel)
    {
        int slot = ChMask.UpSlot(channel);
        return slot < 0 ? null : _drains[slot];
    }

    /// <summary>
    /// 冻结排水水位（**P7 入口由相位机调**，不变量 9 的 release 降级路径）。
    /// 冻结后每条通道只准排「冻结瞬间已在队」的那些条目；P7/P8 期间新入队的失效
    /// 位照置、队照入，但本帧不再排到它们，下一帧照常排——不丢、只延迟。
    ///
    /// 为什么不能靠双缓冲兜住：双缓冲只挡住**同一通道**排水期间的再 Mark。
    /// P7 要按 content→transform→color→visible→structure 顺序排五条通道，
    /// content 排水器写出的 transform 失效若不冻结，同帧后半段就被消化了——
    /// 「P7 里改属性碰巧能用」这个旧世界的未定义行为会原样复活。
    /// </summary>
    public void FreezeForRenderDrain()
    {
        for (int slot = 0; slot < ChMask.UpCount; slot++) _drainCap[slot] = _queue[slot].Count;
        _frozen = true;
    }

    /// <summary>解冻（P9 由相位机调；下一帧照常全量排水）。</summary>
    public void Unfreeze()
    {
        _frozen = false;
        for (int slot = 0; slot < ChMask.UpCount; slot++) _drainCap[slot] = -1;
    }

    /// <summary>当前是否处于冻结窗口（P7 起、P9 止）。</summary>
    public bool IsFrozen => _frozen;

    /// <summary>
    /// 排空一条上行通道（无内核路径：合成一个 detached 上下文）。
    /// 相位机走 <see cref="DrainChannel(ref FrameContext, Ch)"/>——ctx 从帧上下文来，不是这里现造的。
    /// </summary>
    public int DrainChannel(Ch channel)
    {
        FrameContext ctx = FrameContext.Detached(this);
        return DrainChannel(ref ctx, channel);
    }

    /// <summary>
    /// 排空一条上行通道，返回交付给排水器的条数。
    ///
    /// 时序（四步不可换序）：
    ///  ① **交换**入队缓冲与快照缓冲——排水期间新产生的 Mark 落在入队缓冲，不会写穿正在被读的快照；
    ///  ② 冻结窗口内**切掉水位以上的尾巴**并原样退回入队缓冲（脏位不清 ⇒ 下一帧照排，不变量 9）；
    ///  ③ **消费即清位**——清位在回调之前，所以排水器在回调里对同节点的再 Mark 会重新入队；
    ///  ④ 丢弃陈旧句柄（入队后节点死了）后再交付连续 span。
    ///
    /// 通道**没有注册排水器时不排水**：脏位与队列原样留到下一次（跨帧存活靠脏位，队列因去重不会膨胀）。
    /// </summary>
    public int DrainChannel(ref FrameContext ctx, Ch channel)
    {
        int slot = ChMask.UpSlot(channel);
        UiAssert.That(slot >= 0, "DrainChannel 只接受单个上行通道位");
        if (slot < 0) return 0;
        UiAssert.That(_drainingSlot != slot, "同一通道排水重入");

        IChannelDrain? drainer = _drains[slot];
        if (drainer == null) { _diag.NoDrainerSkips++; return 0; }
        if (_queue[slot].Count == 0) return 0;

        int cap = _frozen ? _drainCap[slot] : -1;
        if (cap == 0) { _diag.DeferredEntries += _queue[slot].Count; return 0; }

        ChQueue tmp = _queue[slot];
        _queue[slot] = _batch[slot];
        _batch[slot] = tmp;

        Span<NodeHandle> buf = _batch[slot].Contiguous();
        int take = buf.Length;
        if (cap >= 0 && cap < take)
        {
            take = cap;
            for (int k = take; k < buf.Length; k++) _queue[slot].Enqueue(buf[k]);   // 退回：位仍脏，下一帧排
            _diag.DeferredEntries += buf.Length - take;
        }
        if (_frozen) _drainCap[slot] = 0;      // 冻结窗口内一条通道只放行一次水位内的量

        uint bit = 1u << slot;
        int n = 0;
        for (int k = 0; k < take; k++)
        {
            if (!_table.TryResolve(buf[k], out uint idx)) { _diag.StaleDropped++; continue; }
            _table.ClearDirty(idx, bit);
            buf[n++] = buf[k];
        }
        _batch[slot].Retain(n);

        if (n > 0)
        {
            _drainingSlot = slot;
            try { drainer.Drain(ref ctx, channel, buf.Slice(0, n)); }
            finally { _drainingSlot = -1; }
        }
        _batch[slot].Clear();
        _diag.Drained += n;
        return n;
    }

    /// <summary>
    /// 按位序批量排空多条上行通道（无内核路径；ctx 版见 <see cref="DrainChannels(ref FrameContext, Ch)"/>）。
    /// </summary>
    public int DrainChannels(Ch channels)
    {
        FrameContext ctx = FrameContext.Detached(this);
        return DrainChannels(ref ctx, channels);
    }

    /// <summary>
    /// 按位序批量排空多条上行通道，返回总条数。
    /// 位序只是便利；**通道与相位的对应关系是相位表说了算**（Text/Layout 在 P5、五通道在 P7），
    /// 调用方按相位表逐条调 <see cref="DrainChannel(ref FrameContext, Ch)"/> 才是正规走法。
    /// </summary>
    public int DrainChannels(ref FrameContext ctx, Ch channels)
    {
        int total = 0;
        for (int slot = 0; slot < ChMask.UpCount; slot++)
        {
            Ch c = ChMask.UpChannelAt(slot);
            if ((channels & c) != 0) total += DrainChannel(ref ctx, c);
        }
        return total;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 下行排水：子树戳路由 + 整子树下钻（P6）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 下行通道排水（P6）。从根出发：
    ///  - 只进入 <c>subtreeStamp == FrameStamp</c> 的分支（**路由**：自上次下钻以来未被戳过的子树整棵不看）；
    ///  - 碰到带下行脏位的节点即「下钻根」，其整棵子树都要重算，位沿途继承给后代（**下钻**）；
    ///  - 局部不可见的节点自己重算一次后**整棵子树免下钻**——隐藏期间无人读它的级联值，
    ///    重新显示时由 <c>Visible</c> 通道的 DownVisible 位（或 <see cref="RestampOnShow"/>）补一次全子树下钻；
    ///  - 访问序保证父先于子，级联可单趟顺序写。
    /// 返回访问到的节点数。
    /// </summary>
    public int DrainDown(DownVisitor visit)
    {
        if (visit == null) { UiAssert.That(false, "DrainDown 收到 null 访问器"); return 0; }
        UiAssert.That(_table.Phase == FramePhase.P6_Settle,
            "下行通道只在 P6 下钻（不变量 13：下行戳完备）");

        int visited = 0;
        _walkTop = 0;
        _drainingDown = true;                            // 走查窗口：期间的置戳入挂起集（修复 1）
        try
        {
            PushWalk(_table.Root.Index, Ch.None);
            while (_walkTop > 0)
            {
                PopWalk(out uint idx, out Ch inherited);
                if (!_table.IsIndexAlive(idx)) continue;

                Ch own = (Ch)_table.GetDirty(idx) & ChMask.Down;
                Ch bits = inherited | own;
                bool stamped = _table.GetSubtreeStamp(idx) == _stampEpoch;
                if (bits == Ch.None && !stamped) continue;      // 本分支本代无事

                if (own != Ch.None) _table.ClearDirty(idx, (uint)own);   // 消费即清
                if (bits != Ch.None)
                {
                    visit(idx, bits);
                    visited++;
                    _diag.DownVisits++;
                }

                if (!_table.IsIndexVisible(idx)) { _diag.HiddenSkips++; continue; }   // 隐藏子树免下钻

                // 逆序压栈 ⇒ 出栈即 DFS 前序（父先于子、兄弟按子序）
                for (uint c = _table.LastChildIndex(idx); c != NodeTable.NoIndex; c = _table.PrevSiblingIndex(c))
                    PushWalk(c, bits);
            }
        }
        finally { _drainingDown = false; }

        // 这一代消费完了：推进代号，此后（P7/P8 的违约写、以及**帧外**的常规用户写）落在下一代，
        // 由下一次下钻收走。推进点必须在这里而不是帧首——见 FrameStamp 的文档。
        // u32 回绕那一趟顺带整列清戳（每 2^32 次下钻一次 O(n)，修复 3）：不清的话陈年戳可能恰等于
        // 新代号，StampChain 的「本代已戳即停」在归纳不成立的节点上骗停——祖先没被盖戳，
        // 下行通道整条丢失。「最坏只多一次冗余下钻」对不清列的回绕不成立（2026-08 审计）。
        if (_stampEpoch == uint.MaxValue)
        {
            _stampEpoch = 1u;
            _table.ResetSubtreeStamps();
        }
        else _stampEpoch++;

        // 走查期间挂起的置戳：按新代整链补戳（在回绕清列**之后**，清列清不掉它们）。
        for (int k = 0; k < _pendingCount; k++)
        {
            uint idx = _pendingStamp[k];
            if (_table.IsIndexAlive(idx)) StampChain(idx);
        }
        _pendingCount = 0;
        return visited;
    }

    private void PushWalk(uint index, Ch bits)
    {
        if (_walkTop == _walkIndex.Length)
        {
            Array.Resize(ref _walkIndex, _walkIndex.Length * 2);
            Array.Resize(ref _walkBits, _walkBits.Length * 2);
        }
        _walkIndex[_walkTop] = index;
        _walkBits[_walkTop] = bits;
        _walkTop++;
    }

    private void PopWalk(out uint index, out Ch bits)
    {
        _walkTop--;
        index = _walkIndex[_walkTop];
        bits = _walkBits[_walkTop];
    }

    // ────────────────────────────────────────────────────────────────────────
    // 全局失效：封闭两源的挂起集，生效点只有 P2（不变量 5）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 字形库换代的 fan-out（由字形库自己在 P2 执行：对绑定期登记的订阅文本节点定向 Mark）。
    /// 本平面不建订阅注册表——fan-out 归源，本平面只管「什么时候准放行」。
    /// </summary>
    public Action? GlyphStoreFanOut { get; set; }

    /// <summary>ScreenSize 变化的 fan-out（单值 + 根 Mark，由屏幕尺寸源执行）。</summary>
    public Action? ScreenSizeFanOut { get; set; }

    /// <summary>
    /// 请求全局失效。**任何时刻都只挂起**（含排水期间）；生效点只有下一个 P2。
    /// 旧代资源双缓冲存活到依赖清空——那是源自己的事，本平面只承诺翻转时点。
    /// </summary>
    public void RequestGlobalInvalidate(GlobalSource source)
    {
        switch (source)
        {
            case GlobalSource.GlyphStore: _pendingGlyph = true; break;
            case GlobalSource.ScreenSize: _pendingScreen = true; break;
            default: UiAssert.That(false, "未知全局失效源（封闭两源：GlyphStore / ScreenSize）"); return;
        }
        _diag.GlobalRequests++;
    }

    /// <summary>该源是否有挂起的全局失效。</summary>
    public bool IsGlobalPending(GlobalSource source) => source switch
    {
        GlobalSource.GlyphStore => _pendingGlyph,
        GlobalSource.ScreenSize => _pendingScreen,
        _ => false,
    };

    /// <summary>是否有任何挂起的全局失效。</summary>
    public bool HasPendingGlobal => _pendingGlyph || _pendingScreen;

    /// <summary>
    /// P2 全局失效窗口：把挂起的源统一放行（按枚举序：GlyphStore → ScreenSize），返回生效条数。
    /// **只准在 P2 调**（不变量 5）——相位断言在此，相位机接线在 M1-08。
    /// 此后到帧尾 generation 冻结：排水期间禁翻转，于是「图集在遍历中途重建、前半棵树 UV 已错」
    /// 这一类同帧双遍历事故在结构上不存在，代价是换代固定晚一帧（延迟表里唯一的跨帧行）。
    /// </summary>
    public int ApplyPendingGlobal()
    {
        UiAssert.That(_table.Phase == FramePhase.P2_GlobalInvalidate,
            "全局失效只在 P2 生效（不变量 5）；其他相位只准挂起");
        // 断言之外还要**真拒绝**：release 下若放行，就等于承认存在第二个换代时点，
        // 而「排水期间 generation 冻结」正是「永不画错半棵树」的全部依据。挂起集原样留到下一个 P2。
        if (_table.Phase != FramePhase.P2_GlobalInvalidate) return 0;

        int applied = 0;
        if (_pendingGlyph)
        {
            _pendingGlyph = false;
            GlyphStoreFanOut?.Invoke();
            applied++;
        }
        if (_pendingScreen)
        {
            _pendingScreen = false;
            ScreenSizeFanOut?.Invoke();
            applied++;
        }
        _diag.GlobalApplied += applied;
        return applied;
    }

    // ────────────────────────────────────────────────────────────────────────
    // M1-06 接线：NodeTable 的等值切断通过后才到这里
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="WriteSource"/>（4 值粗分类，树只知道这些） → <see cref="InvalidateReason"/>（7 值细分类）。
    /// 差额（GearPage / TweenDirect / GlobalInvalidate）不经树的粗分类通道：它们的写入方直接调
    /// <see cref="Mark"/> 给出精确理由。M1-09 的 setter codegen 接管后，生成代码同样直接给精确理由，
    /// 本映射只服务于手写 NodeTable 写口。
    /// </summary>
    public static InvalidateReason ReasonOf(WriteSource source) => source switch
    {
        WriteSource.User => InvalidateReason.UserWrite,
        WriteSource.Binding => InvalidateReason.BindingRow,
        WriteSource.Anim => InvalidateReason.Timeline,
        WriteSource.Layout => InvalidateReason.LayoutDerived,
        _ => InvalidateReason.UserWrite,
    };

    private void OnTableWrite(NodeTable table, uint index, Ch channels, WriteSource source)
    {
        UiAssert.That(ReferenceEquals(table, _table), "失效钩子收到别的树域的写入");
        InvalidateReason reason = ReasonOf(source);
#if DEBUG
        RecordSite(index, channels, reason, SourceSite(source), 0);
#endif
        MarkCore(index, channels, reason);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 调用点环形缓冲（机制⑫；release 构建整体不存在）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>调用点环形缓冲容量（debug 构建）。</summary>
    public const int SiteRingCapacity = 256;

#if DEBUG
    private readonly MarkSite[] _sites = new MarkSite[SiteRingCapacity];
    private int _siteWrite;
    private int _siteCount;

    private void RecordSite(uint index, Ch channels, InvalidateReason reason, string member, int line)
    {
        _sites[_siteWrite] = new MarkSite(index, channels, reason, member, line, _frameId);
        _siteWrite = (_siteWrite + 1) % SiteRingCapacity;
        if (_siteCount < SiteRingCapacity) _siteCount++;
    }

    private static string SourceSite(WriteSource source) => source switch
    {
        WriteSource.User => "NodeTable:User",
        WriteSource.Binding => "NodeTable:Binding",
        WriteSource.Anim => "NodeTable:Anim",
        WriteSource.Layout => "NodeTable:Layout",
        _ => "NodeTable",
    };
#endif

    /// <summary>环形缓冲里现存的调用点条数（release 构建恒为 0）。</summary>
    public int SiteCount =>
#if DEBUG
        _siteCount;
#else
        0;
#endif

    /// <summary>取调用点记录，<paramref name="back"/>=0 是最近一次（release 构建恒返回 default）。</summary>
    public MarkSite SiteAt(int back)
    {
#if DEBUG
        if (back < 0 || back >= _siteCount) return default;
        int i = _siteWrite - 1 - back;
        if (i < 0) i += SiteRingCapacity;
        return _sites[i];
#else
        _ = back;
        return default;
#endif
    }

    private static int PopCount(uint v)
    {
        int n = 0;
        while (v != 0) { v &= v - 1; n++; }
        return n;
    }
}
