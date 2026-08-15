namespace FairyNext.Core.Rendering;

/// <summary>
/// 渲染后端语义层（架构「平面三 · 渲染平面」机制 12；设计书 §4.2）。
///
/// 一句话职责：**把实例流的 CPU 镜像搬到 GPU，并按 pass 序提交**。
/// 它不排序、不切段、不裁决脏——那些是 <c>RenderStream</c>（M1-11）的事；
/// 后端收到的每一次调用都已经是「最小 GPU 动作」（架构机制 3 的五通道动作表）。
///
/// v1 三个实现共用本接口：
///  - <see cref="NullBackend"/>——未装后端时的法定占位（见下「未装后端」一节）；
///  - <c>MockBackend</c>（M1-10）——无 GPU 的行为门执法者，全部调用留痕 + 参考光栅；
///  - 顶点流后端——Unity 段 MeshRenderer（M1-15）与 WebGL2/GLES3（M3-03）共用一份四角展开。
/// StructuredBuffer 后端接口保留，凭 <see cref="FrameStats.UploadBytes"/> 实据立项。
///
/// ── 帧括号 ──────────────────────────────────────────────────────────────────
/// 一帧的合法调用序（相位见 docs/design/01-法定帧协议.md）：
/// <code>
///   BeginFrame(frameId)                                  // P7 开头
///     UploadInstances / UploadClips / WriteSlots         // P7 五通道排水的产物
///     SetSegments / SetRunOrders                         // P8 段表与序派生
///     SyncIsland*                                        // P7 收尾的孤岛同步
///     [BeginOffscreenPass → DrawStream(…, pass) → EndOffscreenPass]*   // 全部离屏 pass
///     BindMainSurface(surface)                           // ← 主表面首次绑定
///     DrawStream(stream, PassHandle.None)*               // 主表面绘制
///   EndFrame(stats) → FrameReceipt                       // P8 末
/// </code>
/// 括号外的上传是协议违约（后端可断言）。<see cref="BeginFrame"/> 之前建流是允许的——
/// 流的生命周期跨帧，帧括号只管**本帧的提交**。
///
/// ── 时序契约：离屏 pass 必须先于主表面绑定（v1.3 条款，不变量 14）────────────
/// **全部离屏 pass 必须在 <see cref="BindMainSurface"/> 之前提交完毕。**
/// 理由不是洁癖：tiled GPU 上主 pass 中途切 RT 会触发整块 tile 的 store/load flush，
/// 一次误序就是一次全屏带宽事故（Noesis 同型契约）。后端必须记录 pass 提交序，
/// 任何晚于主表面首次绑定的 <see cref="BeginOffscreenPass"/> 即断言。
/// 第二条同源约束是**依赖序内层先**：<see cref="OffscreenPassDesc.Consumer"/> 声明谁消费本 pass 的结果，
/// 被消费者必须先于消费者提交——嵌套滤镜里内层 RT 没画完，外层捕获到的就是上一帧的内容。
/// 两条都由 mock 后端在 CI 里执法，不指望真机上肉眼发现。
///
/// ── 未装后端 ────────────────────────────────────────────────────────────────
/// 「没有后端」不是一个状态：编译器 = 无头运行时（承诺 8）本来就要在没有 GPU 的进程里
/// 跑完 P0–P9，布局/文本/Extract 全程有效。所以未装后端时挂 <see cref="NullBackend"/>，
/// 流照常消化、收据照常出，只是 <see cref="FrameReceipt.Presents"/> 恒为 0。
/// **不允许**用 null 后端 + 到处 null 检查代替它——那等于把「无 GPU 路径」变成未测试路径。
///
/// ── 缓冲回收（v1.1，不变量 11）───────────────────────────────────────────────
/// <see cref="DestroyStream"/> 不立即销毁：句柄入 pending 队列，按帧队列深度
/// （≤ <see cref="Contracts.Abi.GpuFenceDepth"/>）CPU fence 到期才真释放。
/// P9 的句柄换代只裁决 CPU 侧生死，GPU 仍可能在读上一帧的缓冲。
/// pending 中的句柄收到任何 upload/draw = use-after-free，后端必须断言。
///
/// ── 零脏帧短路（v1.2，不变量 13）────────────────────────────────────────────
/// 自绘后端（WebGL2/顶点流自绘路径；Unity 后端 <see cref="BackendCaps.SelfDrawn"/> = false）
/// 在 <see cref="FrameStats.Dirty"/> 为 false 时跳过 draw 与 present，并在收据里记一笔空转。
/// 判据归渲染平面——后端不自己猜「这帧没变」。
/// </summary>
public interface IRenderBackend
{
    /// <summary>后端名（诊断/收据/bench 报头的 backendVer 组成部分）。</summary>
    string Name { get; }

    /// <summary>
    /// 实例布局版本（应等于 <see cref="Contracts.Abi.ShaderAbiVersion"/>）。
    /// 宿主接后端时比对：shader 与 CPU 侧 ABI 漂移在这里响一次，而不是以花屏的形态在真机上响。
    /// </summary>
    int ShaderAbiVersion { get; }

    /// <summary>能力位（启动期一次性查询；渲染平面据此选路径，不据此改语义）。</summary>
    BackendCaps Caps { get; }

    /// <summary>开一帧的提交括号（P7 开头）。重入 = 协议违约。</summary>
    /// <param name="frameId">帧号（单调递增，来自 <see cref="UiKernel.FrameId"/>）。</param>
    void BeginFrame(ulong frameId);

    /// <summary>建一条流（跨帧存活；可在帧括号外调）。</summary>
    StreamHandle CreateStream(in StreamDesc desc);

    /// <summary>
    /// 销毁流。**不立即释放**：入 fence pending 队列，深度 ≤ <see cref="Contracts.Abi.GpuFenceDepth"/>；
    /// 此后对该句柄的任何调用都是 use-after-free。
    /// </summary>
    void DestroyStream(StreamHandle stream);

    /// <summary>
    /// 上传实例区间（Content/Transform/Color/Visible 四通道排水的最终落点）。
    /// 调用方保证 <paramref name="firstQuad"/> 起的区间是**已合并的连续脏区间**——
    /// 「每段一次上传」是 P8 的承诺，不是后端的义务（后端不做区间合并）。
    /// </summary>
    /// <param name="stream">目标流。</param>
    /// <param name="firstQuad">区间首实例下标。</param>
    /// <param name="quads">区间内容（长度即区间长度）。</param>
    void UploadInstances(StreamHandle stream, int firstQuad, ReadOnlySpan<QuadInstance> quads);

    /// <summary>
    /// 上传裁剪条目区间。条目 0 = None 哨兵，调用方**必须**保证其内容恒为「不裁剪」；
    /// 条目数超 <see cref="Contracts.Abi.ClipEntryBudget"/> 时调用方须先上报
    /// <see cref="DegradeKind.ClipStarvation"/>（不变量 5：预算溢出必有声）。
    /// </summary>
    void UploadClips(StreamHandle stream, int firstEntry, ReadOnlySpan<ClipEntry> entries);

    /// <summary>
    /// 写 transform 槽区间（滚动/gear/tween 的物理落点）。
    /// 两条契约：① 写前位等比较，同值不写（不变量 16——零脏帧短路以此为前提）；
    /// ② <see cref="TransformSlotFlags.AxisAligned"/> 置位前重验矩阵无旋转/斜切（不变量 6）。
    /// 两条都由调用方保证、由 mock 后端执法。
    /// </summary>
    void WriteSlots(StreamHandle stream, int firstSlot, ReadOnlySpan<SlotEntry> slots);

    /// <summary>
    /// 整表替换段表（P8 段属性块集中写）。
    /// 段属性走**显式声明**而不是「谁先用谁写」的隐契约——后端拿到的就是本帧的完整段表。
    /// </summary>
    void SetSegments(StreamHandle stream, ReadOnlySpan<SegmentDesc> segments);

    /// <summary>
    /// 下发派生序（P8）。<paramref name="structEpoch"/> 是**重推的唯一许可证**：
    /// 序号 = paintOrder 下标 × <see cref="Contracts.Abi.PaintOrderStride"/> 派生，
    /// 当且仅当 structEpoch 变才重推（不变量 10）。epoch 没变却重推 = 后端断言。
    /// </summary>
    void SetRunOrders(StreamHandle stream, uint structEpoch, ReadOnlySpan<RunOrder> runs);

    /// <summary>建孤岛（占一个 run 序，无限盒栅栏）。</summary>
    IslandHandle CreateIsland(StreamHandle stream, in IslandDesc desc);

    /// <summary>
    /// 孤岛每帧同步（P7 收尾：槽矩阵 / visual / clip 下发）。
    /// <see cref="IslandSync.StillAnimating"/> 参与零脏帧判定——外部内容不自报就没有跳帧资格。
    /// </summary>
    void SyncIsland(IslandHandle island, in IslandSync sync);

    /// <summary>拆孤岛（与流同一条 fence 回收纪律）。</summary>
    void DestroyIsland(IslandHandle island);

    /// <summary>
    /// 开一个离屏 pass。**必须先于 <see cref="BindMainSurface"/>**（v1.3 时序契约）；
    /// 且必须先于 <see cref="OffscreenPassDesc.Consumer"/> 所指的那个 pass。
    /// </summary>
    PassHandle BeginOffscreenPass(in OffscreenPassDesc desc);

    /// <summary>关一个离屏 pass（与 <see cref="BeginOffscreenPass"/> 严格配对、后进先出）。</summary>
    void EndOffscreenPass(PassHandle pass);

    /// <summary>
    /// 绑定主表面。**本帧的离屏 pass 到此为止**——之后再开离屏 pass 即违约。
    /// 一帧只绑一次（重复绑定 = 协议违约）。
    /// </summary>
    void BindMainSurface(in SurfaceDesc surface);

    /// <summary>
    /// 把一条流提交进某个 pass。<paramref name="target"/> = <see cref="PassHandle.None"/> ⇒ 主表面
    /// （此时必须已 <see cref="BindMainSurface"/>）。
    /// </summary>
    void DrawStream(StreamHandle stream, PassHandle target);

    /// <summary>
    /// 上报一次降级阶梯事件（架构机制 4）。**每一级要么正确、要么明确不显示并计数**，
    /// 计数汇进 <c>GateReport.Degrade</c>：任一非零 = 靠降级画对 = 门不过。
    /// </summary>
    /// <param name="kind">降级类别。</param>
    /// <param name="detail">人读的原因（哪个节点/哪条预算），进诊断面。</param>
    void ReportDegrade(DegradeKind kind, string detail);

    /// <summary>
    /// 收帧（P8 末）：交出本帧的渲染账，取回收据。
    /// 自绘后端在 <see cref="FrameStats.Dirty"/> 为 false 时跳过 present，
    /// 收据里的 ticks/presents 差额就是空转账（架构机制 15）。
    /// </summary>
    FrameReceipt EndFrame(in FrameStats stats);
}

/// <summary>
/// 未装后端时的法定占位（不是「空实现」的委婉说法——它有明确契约）：
/// 全部调用合法且无副作用，<see cref="EndFrame"/> 照常返回收据但 presents 恒为 0。
///
/// 用途一：FgbCompiler = 无头运行时（承诺 8）——编译期跑完整 P0–P9，没有也不需要 GPU。
/// 用途二：单测里只关心树/布局/失效的用例，不必为了 Tick 一帧去装 mock。
/// **不是 mock 后端的替代**：它不留痕、不执法、不出像素；行为门一律进 <c>MockBackend</c>。
/// </summary>
public sealed class NullBackend : IRenderBackend
{
    /// <summary>共享实例（无状态，除帧计数外；<see cref="Instance"/> 的计数是全进程累计，只作诊断）。</summary>
    public static readonly NullBackend Instance = new NullBackend();

    private ulong _ticks;
    private ulong _frameId;

    /// <inheritdoc/>
    public string Name => "null";

    /// <inheritdoc/>
    public int ShaderAbiVersion => Contracts.Abi.ShaderAbiVersion;

    /// <inheritdoc/>
    public BackendCaps Caps => new BackendCaps
    {
        SupportsOffscreen = true,
        SupportsFence = false,
        FenceQueueDepth = 0,
        SelfDrawn = false,
        MaxTextureSlots = Contracts.Abi.SegmentMaxTextures,
    };

    /// <inheritdoc/>
    public void BeginFrame(ulong frameId) { _frameId = frameId; }

    /// <inheritdoc/>
    public StreamHandle CreateStream(in StreamDesc desc)
    {
        _ = desc;
        return new StreamHandle(1, 0);
    }

    /// <inheritdoc/>
    public void DestroyStream(StreamHandle stream) { _ = stream; }

    /// <inheritdoc/>
    public void UploadInstances(StreamHandle stream, int firstQuad, ReadOnlySpan<QuadInstance> quads)
    {
        _ = stream; _ = firstQuad; _ = quads.Length;
    }

    /// <inheritdoc/>
    public void UploadClips(StreamHandle stream, int firstEntry, ReadOnlySpan<ClipEntry> entries)
    {
        _ = stream; _ = firstEntry; _ = entries.Length;
    }

    /// <inheritdoc/>
    public void WriteSlots(StreamHandle stream, int firstSlot, ReadOnlySpan<SlotEntry> slots)
    {
        _ = stream; _ = firstSlot; _ = slots.Length;
    }

    /// <inheritdoc/>
    public void SetSegments(StreamHandle stream, ReadOnlySpan<SegmentDesc> segments)
    {
        _ = stream; _ = segments.Length;
    }

    /// <inheritdoc/>
    public void SetRunOrders(StreamHandle stream, uint structEpoch, ReadOnlySpan<RunOrder> runs)
    {
        _ = stream; _ = structEpoch; _ = runs.Length;
    }

    /// <inheritdoc/>
    public IslandHandle CreateIsland(StreamHandle stream, in IslandDesc desc)
    {
        _ = stream; _ = desc.Kind;
        return new IslandHandle(1, 0);
    }

    /// <inheritdoc/>
    public void SyncIsland(IslandHandle island, in IslandSync sync)
    {
        _ = island; _ = sync.ClipIndex;
    }

    /// <inheritdoc/>
    public void DestroyIsland(IslandHandle island) { _ = island; }

    /// <inheritdoc/>
    public PassHandle BeginOffscreenPass(in OffscreenPassDesc desc)
    {
        _ = desc.Kind;
        return PassHandle.None;
    }

    /// <inheritdoc/>
    public void EndOffscreenPass(PassHandle pass) { _ = pass; }

    /// <inheritdoc/>
    public void BindMainSurface(in SurfaceDesc surface) { _ = surface.Width; }

    /// <inheritdoc/>
    public void DrawStream(StreamHandle stream, PassHandle target) { _ = stream; _ = target; }

    /// <inheritdoc/>
    public void ReportDegrade(DegradeKind kind, string detail) { _ = kind; _ = detail; }

    /// <inheritdoc/>
    public FrameReceipt EndFrame(in FrameStats stats)
    {
        _ticks++;
        return new FrameReceipt(stats.FrameId != 0 ? stats.FrameId : _frameId, false, _ticks, 0, 0);
    }
}
