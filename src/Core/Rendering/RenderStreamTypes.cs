using System.Collections.Generic;
using FairyNext.Contracts;

namespace FairyNext.Core.Rendering;

// ============================================================================
// RenderStream 的 CPU 镜像记录类型（架构「平面三 · 渲染平面」核心数据结构的 SoA 半边）。
//
// 分工：RenderTypes.cs 住**跨界值类型**（QuadInstance/ClipEntry/SlotEntry/SegmentDesc —— 后端也看得见），
// 本文件住**只有 CPU 看得见的簿记**（叶区间、run、孤岛、裁剪域共享、降级账）。
// 一条线划在这里的理由：跨界类型的字节布局由 Abi 定义点管，改一个字段要跑 codegen；
// 簿记类型只服务 CPU 侧算法，改它不动 ABI，也不该让 ABI 门变红。
//
// 三条本文件执法的不变量：
//  · 不变量 2（栅栏无限盒）：<see cref="RunRecord"/> 类型上**没有** AABB 字段——见其文档；
//  · 不变量 4（slack 越界门）：<see cref="LeafRange.Slack"/> 是写区间上界，不是「额外余量」；
//  · 不变量 9（颜色纯函数）：<see cref="ColorTier"/> 是 GPU color 场的**唯一**产生式。
// ============================================================================

/// <summary>
/// 叶 → 实例区间（架构：<c>LeafRange {leafNodeId, seg, start, count, slack}</c>）。
///
/// <see cref="Count"/> 是**在用**实例数，<see cref="Slack"/> 是**预留**实例数：
/// content 通道的局部重写写区间必须 ⊆ <c>[Start, Start + Slack)</c>（不变量 4）。
/// 两者的差额是文本叶反复增删字时不惊动邻叶的缓冲——所以 slack 向上取 2 的幂：
/// 「11 个字变 13 个字」在 16 的预留里是原位重写，不是一次面板整编。
///
/// 预留区的尾部实例恒为 <c>default</c>（size 0、color 0、route 0），画不出任何像素，
/// 也天然满足 mock 后端的保留位写零检查——「预留」在流里是真实存在的空实例，不是一段被跳过的洞。
/// </summary>
public struct LeafRange
{
    /// <summary>叶节点句柄（诊断与反查；SoA 表没有对象身份，账要侧带）。</summary>
    public NodeHandle Node;
    /// <summary>所属段下标。</summary>
    public int Segment;
    /// <summary>所属 run 下标。</summary>
    public int Run;
    /// <summary>本叶首个实例下标。</summary>
    public int Start;
    /// <summary>在用实例数。</summary>
    public int Count;
    /// <summary>预留实例数（写区间上界；≥ <see cref="Count"/>）。</summary>
    public int Slack;
    /// <summary>本叶实例引用的 ClipEntry 下标（0 = 不裁剪）。</summary>
    public int ClipEntry;
    /// <summary>本叶实例骑的 transform 槽（0 = identity）。</summary>
    public int Slot;
    /// <summary>本叶在所属段内的纹理槽（定义域 = <see cref="Abi.SegmentMaxTextures"/>）。</summary>
    public int TexSlot;
    /// <summary>发射器给的 flags（fontAlpha/SDF/borderW…）；grayed 位由 Color 通道盖写。</summary>
    public uint EmitFlags;
    /// <summary>最近一次应用的级联视觉（Color/Visible 通道的输入，重算时的当前值）。</summary>
    public IslandVisual Visual;
    /// <summary>是否被 Visible 通道清零（α 归零，rgb 与几何原样留着）。</summary>
    public bool Hidden;

    /// <summary>写区间上界（不变量 4 的判据：写 <c>[Start, End)</c> 之外即越界）。</summary>
    public readonly int End => Start + Slack;

    /// <inheritdoc/>
    public readonly override string ToString() =>
        $"leaf {Node} [{Start},{Start + Count})/{Slack} seg={Segment} run={Run} clip={ClipEntry} slot={Slot}";
}

/// <summary>
/// run = 栅栏括号切出的排序区间（架构机制 2）。
///
/// **类型上没有 AABB 字段，也永远不加**——「紧栅栏」在本仓库不可表达（渲染平面不变量 2）。
/// 旧 fork 三次让紧包围盒变陈旧（mask tween / Animator 驱动 / 滤镜 extend），
/// 每次都是「盒子对，内容早动了」的静默画错。没有失效通道能推送这类变化时，
/// 唯一不会陈旧的盒子是无限盒；把字段从类型里删掉比在注释里提醒强得多——
/// 后者挡不住下一个「顺手加个 bounds 做剔除」的补丁。
///
/// <see cref="PaintOrderIndex"/> 是**序派生的唯一输入**：
/// sortingOrder = 本值 × <see cref="Abi.PaintOrderStride"/>，只在 structEpoch 变时整表重推
/// （不变量 10）。run 自己不维护第二本序号账。
/// </summary>
public struct RunRecord
{
    /// <summary>本 run 首个段下标。</summary>
    public int FirstSegment;
    /// <summary>本 run 的段数。</summary>
    public int SegmentCount;
    /// <summary>本 run 首个实例下标。</summary>
    public int FirstQuad;
    /// <summary>本 run 的实例数。</summary>
    public int QuadCount;
    /// <summary>绘制序下标（sortingOrder 的唯一输入；跨 run 严格递增）。</summary>
    public int PaintOrderIndex;
    /// <summary>关闭本 run 的孤岛下标（-1 = 末 run，没有闭合者）。</summary>
    public int ClosedByIsland;

    /// <summary>派生序号（= <see cref="PaintOrderIndex"/> × <see cref="Abi.PaintOrderStride"/>）。</summary>
    public readonly int SortingOrder => PaintOrderIndex * Abi.PaintOrderStride;

    /// <inheritdoc/>
    public readonly override string ToString() =>
        $"run segs[{FirstSegment},{FirstSegment + SegmentCount}) quads[{FirstQuad},{FirstQuad + QuadCount}) " +
        $"order={SortingOrder}{(ClosedByIsland >= 0 ? " ↦island#" + ClosedByIsland : "")}";
}

/// <summary>
/// 孤岛的 CPU 记录（架构机制 9）。孤岛一律**无限盒栅栏**、占一个 run 序
/// （<see cref="PaintOrderIndex"/> 是它自己的那一个）；本记录是流侧的账：
/// 谁、插在哪、骑哪个槽、裁剪怎么下发、本帧是否自报仍在动（零脏帧短路的第三个前提，不变量 13）。
/// 内容对象不在这里——它住 <see cref="IslandTable"/>，按 <see cref="Node"/> 反查。
/// SoA 表与内容对象分家的理由与叶一样：流要能在没有任何内容对象的进程里（编译器 = 无头运行时）
/// 原样存在。
/// </summary>
public struct IslandRecord
{
    /// <summary>类别。</summary>
    public IslandKind Kind;
    /// <summary>③外部原生的具名种类。</summary>
    public IslandNativeKind NativeKind;
    /// <summary>④stencil 括号位（成对，可嵌套）。</summary>
    public IslandBracket Bracket;
    /// <summary>④stencil 嵌套深度（1 起；即模板 ref 值）。</summary>
    public int StencilDepth;
    /// <summary>裁剪下发方式（②的 include/scissor 二分）。</summary>
    public IslandClipMode ClipMode;
    /// <summary>孤岛节点（可见性判据与内容反查的身份）。</summary>
    public NodeHandle Node;
    /// <summary>插在哪个 run 之前。</summary>
    public int RunBefore;
    /// <summary>本孤岛独占的绘制序下标（sortingOrder = 本值 × <see cref="Abi.PaintOrderStride"/>）。</summary>
    public int PaintOrderIndex;
    /// <summary>绑定的 transform 槽。</summary>
    public int Slot;
    /// <summary>绑定的 ClipEntry 下标。</summary>
    public int ClipEntry;
    /// <summary>级联视觉（并入 worldVisual 向下排水——祖先隐藏时孤岛跟随）。</summary>
    public IslandVisual Visual;
    /// <summary>RT 分频（1 = 每帧）。</summary>
    public int RenderEveryN;
    /// <summary>后端句柄（<see cref="IRenderBackend.CreateIsland"/> 的产物）。</summary>
    public IslandHandle Handle;
    /// <summary>内容自报「仍在动」。</summary>
    public bool StillAnimating;
    /// <summary>诊断名。</summary>
    public string? DebugName;

    /// <summary>派生序号（= <see cref="PaintOrderIndex"/> × <see cref="Abi.PaintOrderStride"/>）。</summary>
    public readonly int SortingOrder => PaintOrderIndex * Abi.PaintOrderStride;

    /// <inheritdoc/>
    public readonly override string ToString() =>
        $"island {Kind}{(NativeKind == IslandNativeKind.None ? "" : "/" + NativeKind)}"
        + $"{(Bracket == IslandBracket.None ? "" : " " + Bracket + "@" + StencilDepth)}"
        + $" node={Node.Index} run<{RunBefore} order={SortingOrder} slot={Slot} clip={ClipEntry}/{ClipMode}";
}

/// <summary>
/// 叶发射描述（<see cref="RenderStream.AppendLeaf"/> 的输入）。
/// 发射器（M1-13）只管几何与 UV；**route 三分量（slot/clipIndex/texSlot）一律由流盖写**——
/// 段是流切的、槽是流分的、裁剪域是流的 ClipBook 记的，发射器不该也无法知道这三个数。
/// </summary>
public struct LeafDesc
{
    /// <summary>叶节点句柄。</summary>
    public NodeHandle Node;
    /// <summary>纹理身份（<see cref="TexId.None"/> 也占一个段纹理槽，见 <see cref="RenderStream.AppendLeaf"/>）。</summary>
    public TexId Texture;
    /// <summary>混合类别（**进段键，不是栅栏**）。</summary>
    public BlendClass Blend;
    /// <summary>裁剪域条目下标（0 = 不裁剪）。</summary>
    public int ClipEntry;
    /// <summary>transform 槽下标（0 = identity）。</summary>
    public int Slot;
    /// <summary>预留实例数下限（0 = 不留 slack，写区间 = 实际长度）；向上取 2 的幂。</summary>
    public int SlackHint;
    /// <summary>级联视觉（Color 通道的输入；**默认值是全透明**，用 <see cref="For"/> 建就不会踩）。</summary>
    public IslandVisual Visual;
    /// <summary>发射器 flags（fontAlpha/SDF/borderW…）。grayed 位归 Color 通道，写在这里会被盖掉。</summary>
    public uint EmitFlags;

    /// <summary>
    /// 常规叶（不透明可见、无 slack、不裁剪、identity 槽）。
    /// <c>default(LeafDesc)</c> 的 <see cref="Visual"/> 是 α=0 且不可见——那是合法但几乎不是本意的值，
    /// 所以正常路径一律从本工厂起手。
    /// </summary>
    public static LeafDesc For(NodeHandle node, TexId texture, BlendClass blend = BlendClass.Normal) =>
        new LeafDesc
        {
            Node = node,
            Texture = texture,
            Blend = blend,
            ClipEntry = 0,
            Slot = 0,
            SlackHint = 0,
            Visual = IslandVisual.Opaque,
            EmitFlags = 0,
        };
}

/// <summary>
/// 颜色 tier（架构机制 7 / 不变量 9）：**GPU color 场 ≡ f(baseColor, worldVisual)**，
/// 单向纯函数，没有退化分支。
///
/// 旧 fork 的做法是按 <c>alpha / bakedAlpha</c> 的比例重标当前色，于是有两个病：
///  1. <c>bakedAlpha ≤ 0</c> 时没有可恢复的基准 —— 退化成一次全叶重建（读原生网格、重装配）；
///  2. 反复乘除让色值漂移（α 起落十几次之后，红不再是同一个红）。
/// 从未乘 α 的基色重算两个病都不存在：α 归零只是把**这一次**的乘数取 0，基色仍在 CPU 镜像里，
/// 恢复就是再乘一次。「alpha 归零再恢复不丢色」因此是本设计的判据用例，不是边角情形。
/// </summary>
public static class ColorTier
{
    // RGBA8 的通道位序（bit0-7=r … bit24-31=a）的定义点是 Numerics 的 Color32.Pack——
    // 与 GPU 的 RGBA8 unorm 字节序一致。这里只引用那条约定，不另立一套。
    private const uint RgbMask = 0x00FFFFFFu;
    private const int AlphaShift = 24;

    /// <summary>
    /// 基色 × 级联 α → GPU color 场。<paramref name="visible"/> 为假 ⇒ α 归零而 rgb 原样保留
    /// （Visible 通道的「原位清零」；恢复时不需要任何别的账）。
    /// α 非有限或 ≤0 一并落到透明分支——NaN 不该变成一个随机字节。
    /// </summary>
    public static uint Apply(uint baseColor, float alpha, bool visible)
    {
        if (!visible || !(alpha > 0f)) return baseColor & RgbMask;
        float a = (baseColor >> AlphaShift) * (alpha < 1f ? alpha : 1f);
        uint packed = (uint)(a + 0.5f);
        if (packed > 255u) packed = 255u;
        return (baseColor & RgbMask) | (packed << AlphaShift);
    }

    /// <summary>基色 × 级联视觉三元（<see cref="IslandVisual"/> 就是 worldVisual 的落叶结果）。</summary>
    public static uint Apply(uint baseColor, in IslandVisual visual) =>
        Apply(baseColor, visual.Alpha, visual.Visible);

    /// <summary>置/清 flags 的 grayed 位（位移取自 ABI 生成物，不手抄）。</summary>
    public static uint WithGrayed(uint flags, bool grayed) =>
        grayed
            ? flags | (AbiLayout.FlagsGrayedMask << AbiLayout.FlagsGrayedShift)
            : flags & ~(AbiLayout.FlagsGrayedMask << AbiLayout.FlagsGrayedShift);

    /// <summary>flags 的 grayed 位是否置起。</summary>
    public static bool IsGrayed(uint flags) => AbiLayout.FlagsGrayed(flags) != 0;
}

/// <summary>一次降级阶梯事件（架构机制 4 的可计数形态）。</summary>
public readonly struct DegradeEvent
{
    /// <summary>降级类别。</summary>
    public readonly DegradeKind Kind;
    /// <summary>人读原因（哪个节点 / 撞了哪条预算）。</summary>
    public readonly string Detail;

    /// <summary>建事件。</summary>
    public DegradeEvent(DegradeKind kind, string detail)
    {
        Kind = kind;
        Detail = detail;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Kind}: {Detail}";
}

/// <summary>
/// 降级阶梯的账本（不变量 5：**预算溢出必有声**）。
///
/// 为什么不直接调后端：预算是 CPU 侧的账（槽表满了、裁剪域满了），发现的时刻往往在
/// P5/P6/P7 的中途，而后端调用只在 P7/P8 的帧括号里合法。攒一本账、在 P8 提交时一次性
/// 转给 <see cref="IRenderBackend.ReportDegrade"/>，两个纪律就都不破。
/// mock 后端在 EndFrame 处核对「预算超限而无阶梯事件 = 失败」，本类是那一侧的对手方。
/// </summary>
public sealed class DegradeLog
{
    private readonly List<DegradeEvent> _events = new List<DegradeEvent>();
    private readonly uint[] _byKind = new uint[16];

    /// <summary>本轮尚未转交后端的事件（有序）。</summary>
    public IReadOnlyList<DegradeEvent> Pending => _events;

    /// <summary>会话累计事件数（<see cref="Flush"/> 不清零——它是水位，不是缓冲）。</summary>
    public int TotalCount { get; private set; }

    /// <summary>某类别的会话累计次数。</summary>
    public uint CountOf(DegradeKind kind)
    {
        int i = (int)kind;
        return (uint)i < (uint)_byKind.Length ? _byKind[i] : 0u;
    }

    /// <summary>记一次降级。<see cref="DegradeKind.None"/> 是协议违约，不入账。</summary>
    public void Report(DegradeKind kind, string detail)
    {
        if (kind == DegradeKind.None)
        {
            UiAssert.That(false, "DegradeLog.Report 收到 DegradeKind.None（阶梯清单是封闭枚举）");
            return;
        }
        _events.Add(new DegradeEvent(kind, detail));
        int i = (int)kind;
        if ((uint)i < (uint)_byKind.Length) _byKind[i]++;
        TotalCount++;
    }

    /// <summary>把攒下的事件转交后端并清空缓冲（P8 提交路径调）。返回转交条数。</summary>
    public int Flush(IRenderBackend backend)
    {
        if (backend == null) { _events.Clear(); return 0; }
        int n = _events.Count;
        for (int i = 0; i < n; i++) backend.ReportDegrade(_events[i].Kind, _events[i].Detail);
        _events.Clear();
        return n;
    }

    /// <summary>丢弃未转交的事件（测试夹具用；水位不动）。</summary>
    public void Discard() => _events.Clear();
}

/// <summary>
/// 流的诊断面（架构接缝：<c>StreamDiag</c> 是诊断平面公开 API）。
/// 全部是**水位与计数**，不是布尔——降级能把像素画对，只有计数能说明性能路径是不是悄悄换了。
/// </summary>
public struct StreamDiag
{
    /// <summary>实例数。</summary>
    public int QuadCount;
    /// <summary>叶数。</summary>
    public int LeafCount;
    /// <summary>段数（= 段切换次数）。</summary>
    public int SegmentCount;
    /// <summary>run 数。</summary>
    public int RunCount;
    /// <summary>孤岛数。</summary>
    public int IslandCount;
    /// <summary>结构代。</summary>
    public uint StructEpoch;
    /// <summary>活槽数（含槽 0）。</summary>
    public int SlotLive;
    /// <summary>槽表历史高水位。</summary>
    public int SlotHighWater;
    /// <summary>槽荒次数（Claim 撞预算）。</summary>
    public int SlotStarvation;
    /// <summary>被位等切断的槽写次数（不变量 16 的正面收据：切断真的在发生）。</summary>
    public int SlotWritesCut;
    /// <summary>裁剪域数（不含哨兵 0）。</summary>
    public int ClipDomains;
    /// <summary>裁剪域历史高水位。</summary>
    public int ClipHighWater;
    /// <summary>裁剪域超预算次数。</summary>
    public int ClipStarvation;
    /// <summary>「子引用父条目」的次数（继承共享省下的条目数）。</summary>
    public int ClipInherited;
    /// <summary>inner rect 包含剪枝摘除 clipIndex 的实例数。</summary>
    public int ClipPruned;
    /// <summary>会话累计降级事件数。</summary>
    public int DegradeEvents;

    /// <inheritdoc/>
    public override string ToString() =>
        $"stream quads={QuadCount} leaves={LeafCount} segs={SegmentCount} runs={RunCount} epoch={StructEpoch} " +
        $"slots={SlotLive}/{SlotHighWater} clips={ClipDomains}/{ClipHighWater} pruned={ClipPruned} degrade={DegradeEvents}";
}

/// <summary>
/// 一次 <see cref="RenderStream.Submit"/> 的收据。零脏帧的合法形态是**全字段为零**——
/// 不变量 13 的「跳过 present」以此为前提，所以提交路径必须能被逐字段断言，而不是靠肉眼看日志。
/// </summary>
public readonly struct SubmitReport
{
    /// <summary>后端调用次数（上传 + 段表 + 序派生）。</summary>
    public readonly int Calls;
    /// <summary>上传的实例数。</summary>
    public readonly int Quads;
    /// <summary>上传的裁剪条目数。</summary>
    public readonly int Clips;
    /// <summary>写的槽数。</summary>
    public readonly int Slots;
    /// <summary>是否下发了段表。</summary>
    public readonly bool Segments;
    /// <summary>是否重推了派生序。</summary>
    public readonly bool Orders;
    /// <summary>转交后端的降级事件数。</summary>
    public readonly int Degrades;

    /// <summary>建收据（流内部用）。</summary>
    public SubmitReport(int calls, int quads, int clips, int slots, bool segments, bool orders, int degrades)
    {
        Calls = calls; Quads = quads; Clips = clips; Slots = slots;
        Segments = segments; Orders = orders; Degrades = degrades;
    }

    /// <summary>什么都没发生（零脏帧的合法收据）。</summary>
    public bool IsIdle => Calls == 0;

    /// <inheritdoc/>
    public override string ToString() =>
        $"submit calls={Calls} quads={Quads} clips={Clips} slots={Slots} segs={Segments} orders={Orders} degrade={Degrades}";
}
