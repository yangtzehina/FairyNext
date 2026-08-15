namespace FairyNext.Core.Rendering;

/// <summary>
/// P6/P7/P8 的接线件（M1-14）：把树、失效协议、实例流、后端接成**第一条端到端渲染路径**。
/// 宿主只需要 <c>new RenderPipeline(...).Attach()</c>，此后 <see cref="UiKernel.Tick"/> 就会
/// 把一次 <c>SetAlpha</c> 变成一次实例上传。
///
/// 三个相位各自的职责（法定相位表原文 → 本类的落点）：
/// <code>
///   P6 结构与视觉定形  ApplyStructure（内核直调）
///                     → SettleStep：world/worldVisual 沿脏子树增量重算（一遍两算）
///                     → DownStep：下行通道按子树戳下钻，级联落叶回上行队列
///   P7 渲染排水        五通道（内核直调 IChannelDrain）
///                     → DrainTailStep：升级重编 + 包含剪枝 + 孤岛挂载 + 派生列神谕
///   P8 提交            SubmitStep：Submit → BuildStats → 画 → EndFrame（**顺序不可换**）
/// </code>
///
/// 一个实例服务**一条流 = 一个面板**（机制 10「流按面板拆分防大流抖动」）。多面板 = 多实例，
/// 各自的 <see cref="Extract.PanelRoot"/> 负责把别人的结构脏挡在门外。
/// </summary>
public sealed class RenderPipeline
{
    private readonly UiKernel _kernel;
    private readonly NodeTable _table;
    private readonly Invalidation _invalidation;
    private readonly RenderStream _stream;
    private readonly IExtractSource _source;
    private readonly Extract _extract;
    private readonly StreamDrain _drain;
    private IRenderBackend _backend;

    private NodeHandle[] _roots = new NodeHandle[32];
    private int _rootCount;
    private int _rebuildsAtSettle;
    private bool _attached;

    /// <summary>接一条流、一棵树（经内核）与一个后端。</summary>
    /// <param name="kernel">相位机（其树域即本管线的树域）。</param>
    /// <param name="stream">本面板的实例流。</param>
    /// <param name="source">内容源（Extract 与增量排水器共用同一个）。</param>
    /// <param name="backend">渲染后端（<c>NullBackend</c> 也是合法的一个）。</param>
    public RenderPipeline(UiKernel kernel, RenderStream stream, IExtractSource source, IRenderBackend backend)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _table = kernel.Table;
        _invalidation = kernel.Invalidation;
        _extract = new Extract(stream, _table, source, backend);
        _drain = new StreamDrain(stream, _table, source);
    }

    // ── 只读面 ──────────────────────────────────────────────────────────────

    /// <summary>本面板的流。</summary>
    public RenderStream Stream => _stream;

    /// <summary>整流重编器（Structure 通道的消费者）。</summary>
    public Extract Extract => _extract;

    /// <summary>四通道增量排水器。</summary>
    public StreamDrain Drain => _drain;

    /// <summary>内容源。</summary>
    public IExtractSource Source => _source;

    /// <summary>树域。</summary>
    public NodeTable Table => _table;

    /// <summary>后端（换后端要先 <see cref="Detach"/>）。</summary>
    public IRenderBackend Backend => _backend;

    /// <summary>主表面描述（<see cref="Present"/> 为真且本帧脏时用它绑定）。</summary>
    public SurfaceDesc Surface { get; set; } = new SurfaceDesc { Width = 1280, Height = 720, ClearColor = 0u };

    /// <summary>是否提交绘制（false = 只跑到上传为止；离线路径与编译器用）。</summary>
    public bool Present { get; set; } = true;

    /// <summary>
    /// 是否每帧跑一次派生列神谕（<see cref="NodeTable.DerivedMatchesFullRecompute"/>）。
    /// 诊断/测试构建打开：帧时间约 ×1.5，发布版关掉即零成本。
    /// </summary>
    public bool DerivedOracle { get; set; }

    /// <summary>派生列神谕的失败次数（非零 = P6 的增量重算漏了脏根）。</summary>
    public long DerivedOracleFailures { get; private set; }

    /// <summary>派生列神谕首个不一致的节点下标（<see cref="NodeTable.NoIndex"/> = 全等）。</summary>
    public uint DerivedBadIndex { get; private set; } = NodeTable.NoIndex;

    /// <summary>会话累计：P6 增量重算的节点数。</summary>
    public long DerivedNodes { get; private set; }

    /// <summary>会话累计：P6 下行下钻访问到的节点数。</summary>
    public long DownVisits { get; private set; }

    /// <summary>会话累计：P7 收尾因升级而整编的次数。</summary>
    public int Escalated { get; private set; }

    /// <summary>走过的帧数（= 后端 tick 数）。</summary>
    public ulong Ticks { get; private set; }

    /// <summary>真的提交了绘制的帧数（零脏帧短路生效时 &lt; <see cref="Ticks"/>）。</summary>
    public ulong Presents { get; private set; }

    /// <summary>零脏帧数（提交零调用、后端零 draw）。</summary>
    public ulong IdleFrames { get; private set; }

    /// <summary>最近一帧的提交收据。</summary>
    public SubmitReport LastReport { get; private set; }

    /// <summary>最近一帧的渲染账。</summary>
    public FrameStats LastStats { get; private set; }

    /// <summary>
    /// 空转收据（架构机制 15）：<c>ticks vs presents</c> 一行文本。
    /// 「这帧为什么重画」从猜测变审计的最小形态——它同时是等值切断是否真正生效的端到端探针。
    /// </summary>
    public string DescribeReceipt() =>
        $"receipt ticks={Ticks} presents={Presents} idle={IdleFrames} " +
        $"rebuilds={_extract.Rebuilds} escalated={Escalated} " +
        $"splices={_table.PaintSplices}/{_table.PaintSplices + _table.PaintFullRebuilds} " +
        $"derived={DerivedNodes} down={DownVisits}";

    // ── 接线 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 装钩子 + 注册排水器。钩子是**独占**的（内核一个相位一个钩子），
    /// 排水器同样（一个通道一个消费者），所以重复 Attach 会在失效平面那侧断言。
    /// </summary>
    public void Attach()
    {
        UiAssert.That(!_attached, "RenderPipeline 重复 Attach");
        _kernel.SettleStep = SettleStep;
        _kernel.DownStep = DownVisit;
        _kernel.DrainTailStep = DrainTail;
        _kernel.SubmitStep = SubmitStep;
        _kernel.BeforeFrame += OnBeforeFrame;
        _invalidation.Register(_drain);
        _invalidation.Register(_extract);
        _attached = true;
    }

    /// <summary>卸线（测试拆装用；流与后端不动）。</summary>
    public void Detach()
    {
        if (!_attached) return;
        _kernel.SettleStep = null;
        _kernel.DownStep = null;
        _kernel.DrainTailStep = null;
        _kernel.SubmitStep = null;
        _kernel.BeforeFrame -= OnBeforeFrame;
        _invalidation.Unregister(_drain);
        _invalidation.Unregister(_extract);
        _attached = false;
    }

    private void OnBeforeFrame(ulong frameId) => _backend.BeginFrame(frameId);

    // ── P6：结构与视觉定形 ──────────────────────────────────────────────────

    /// <summary>
    /// P6 派生列增量重算。脏根取自三条**几何**上行通道的在队快照：
    /// <see cref="Ch.Transform"/>（局部变换）、<see cref="Ch.Layout"/>（宽高，未接布局器时留在队里）、
    /// <see cref="Ch.Content"/>（布局回写的 resolved 尺寸——pivot 非零时它改的是局部矩阵）。
    ///
    /// 取三条而不是一条是**静态超集**那条款的现场应用：多算的代价是重算一棵小子树，
    /// 漏算的代价是画在上一帧的位置上。Color/Visible 不在其中——它们不动矩阵，
    /// 其级联由下行通道在 <see cref="DownVisit"/> 里落地。
    ///
    /// **窥视不出队**：这三条通道的消费权在 P5/P7，这里只借用「谁脏了」这个事实。
    /// </summary>
    private void SettleStep(ref FrameContext ctx)
    {
        _rebuildsAtSettle = _extract.Rebuilds;
        _rootCount = 0;
        CollectRoots(Ch.Transform);
        CollectRoots(Ch.Layout);
        CollectRoots(Ch.Content);
        if (_rootCount == 0) return;                 // 几何没脏：派生列原样有效（零脏帧走的就是这条）
        DerivedNodes += _table.DrainDerivedFrom(new ReadOnlySpan<NodeHandle>(_roots, 0, _rootCount));
    }

    private void CollectRoots(Ch channel)
    {
        ReadOnlySpan<NodeHandle> queue = _invalidation.Peek(channel);
        if (queue.Length == 0) return;
        if (_rootCount + queue.Length > _roots.Length)
        {
            int cap = _roots.Length;
            while (cap < _rootCount + queue.Length) cap *= 2;
            Array.Resize(ref _roots, cap);
        }
        for (int i = 0; i < queue.Length; i++) _roots[_rootCount++] = queue[i];
    }

    /// <summary>
    /// P6 下行下钻访问器（父先于子由 <see cref="Invalidation.DrainDown"/> 保证）。两件事：
    ///  ① 重算本节点的 worldVisual——下行通道改的就是级联视觉；
    ///  ② **落叶**：本节点若在流里有叶，把变化交回对应的上行通道，让 P7 去重上色/重判隐显。
    ///     下行通道自己不碰流：它是「谁需要重算」的路由，不是「怎么改字节」的动作。
    /// </summary>
    private void DownVisit(uint index, Ch channels)
    {
        _table.CascadeVisualAt(index);
        DownVisits++;

        NodeHandle node = _table.HandleOf(index);
        if (node.IsNone || _drain.LeafOf(node) < 0) return;   // 不在流里：进/出流是结构的事

        Ch up = Ch.None;
        if ((channels & (Ch.DownColor | Ch.DownLayer)) != 0) up |= Ch.Color;
        if ((channels & Ch.DownVisible) != 0) up |= Ch.Visible;
        if (up != Ch.None) _invalidation.Mark(node, up, InvalidateReason.CascadeDown);
    }

    // ── P7 收尾：升级重编 + 剪枝 + 孤岛 + 神谕 ──────────────────────────────

    private void DrainTail(ref FrameContext ctx)
    {
        if (DerivedOracle)
        {
            if (_table.DerivedMatchesFullRecompute(out uint bad)) DerivedBadIndex = NodeTable.NoIndex;
            else { DerivedBadIndex = bad; DerivedOracleFailures++; }
        }

        if (_drain.StructurePending)
        {
            _drain.ClearPending();
            // Structure 通道本帧已经整编过就不再来一次：一批脏折叠成一次是机制 10 的原话。
            if (_extract.Rebuilds == _rebuildsAtSettle)
            {
                _extract.Rebuild();
                Escalated++;
            }
        }

        // 包含剪枝要在 quad 落定之后跑：新几何未必还在内含矩形里，而原位重写会按叶的
        // clipEntry 重新盖上 clipIndex——剪枝结论不会被偷偷继承，所以有变化就重跑（幂等）。
        // 零脏帧不跑：一个字节都没动过，剪枝结论按定义也没动，而空转收据要求静止帧的 CPU 侧也安静。
        if (_stream.HasPendingWork) _stream.PruneContainedClips();

        // 叶反查表与落位基准在此定格：此刻 world 还是本帧 P6 收敛的那份，也正是流里 quad 用的那份。
        // 拖到下一帧惰性重建就会拿新 world 当旧基准（见 StreamDrain.SyncMap）。
        _drain.SyncMap();

        if (!_stream.Handle.IsNone) _stream.AttachIslands(_backend);
    }

    // ── P8：提交 ────────────────────────────────────────────────────────────

    /// <summary>
    /// P8。<c>Submit → BuildStats → 绘制 → EndFrame</c> **顺序不可换**：
    /// stats 的 <c>Dirty</c> 由本帧的提交收据决定（后端不自己猜「这帧没变」），
    /// 而零脏帧短路（跳过 draw 与 present）与 mock 的「Dirty=false 却有上传」探针都读这一个判据。
    /// </summary>
    private void SubmitStep(ref FrameContext ctx)
    {
        EnsureAttached();

        SubmitReport report = _stream.Submit(_backend);
        FrameStats stats = _stream.BuildStats(ctx.FrameId, in report);

        bool draw = stats.Dirty && Present;
        if (draw)
        {
            SurfaceDesc surface = Surface;
            _backend.BindMainSurface(in surface);
            _backend.DrawStream(_stream.Handle, PassHandle.None);
        }

        _backend.EndFrame(in stats);

        LastReport = report;
        LastStats = stats;
        Ticks++;
        if (draw) Presents++;
        if (!stats.Dirty) IdleFrames++;      // Dirty 的定义已含「提交零调用」，这里不重复判一次
    }

    private void EnsureAttached()
    {
        if (!_stream.Handle.IsNone) return;
        _stream.Attach(_backend);
        _stream.AttachIslands(_backend);
    }
}
