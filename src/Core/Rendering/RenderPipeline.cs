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
/// 多实例共驱**同一个内核**时不各自 <see cref="Attach"/>（相位钩子硬独占，第二个就 throw）——
/// 由 <see cref="PanelFanout"/> 占钩子并逐面板分发（M1-15）。
/// </summary>
public sealed class RenderPipeline
{
    /// <summary>接线形态：独立占内核钩子（Solo）或登记在 <see cref="PanelFanout"/> 名下（Fanout）。</summary>
    private enum AttachMode : byte
    {
        None = 0,
        Solo = 1,
        Fanout = 2,
    }

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
    private AttachMode _mode;

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
    /// 是否每帧跑一次派生列神谕（<see cref="NodeTable.DerivedMatchesFullRecompute"/>）
    /// 与序神谕（<see cref="NodeTable.ValidatePaintOrder"/>）。
    /// 诊断/测试构建打开：帧时间约 ×1.5 再加序神谕的 O(树²) 重展比对，发布版关掉即零成本。
    /// </summary>
    public bool DerivedOracle { get; set; }

    /// <summary>派生列神谕的失败次数（非零 = P6 的增量重算漏了脏根）。</summary>
    public long DerivedOracleFailures { get; private set; }

    /// <summary>派生列神谕首个不一致的节点下标（<see cref="NodeTable.NoIndex"/> = 全等）。</summary>
    public uint DerivedBadIndex { get; private set; } = NodeTable.NoIndex;

    /// <summary>
    /// 序神谕（<see cref="NodeTable.ValidatePaintOrder"/>）的失败次数。
    /// 2026-08 审计：它此前只被结构编辑的**定向用例**调用，切片拼接的簿记错误
    /// （如「清扫整体先于拼接」被改成边清边拼）在帧级从未被逐帧核对——
    /// 现在 <see cref="DerivedOracle"/> 开着时每帧在 P7 收尾顺带跑一次。
    /// </summary>
    public long PaintOrderFailures { get; private set; }

    /// <summary>最近一次序神谕失败的人读描述（全绿为空串）。</summary>
    public string LastPaintOrderError { get; private set; } = string.Empty;

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
    /// 装钩子 + 注册排水器。钩子是**独占**的（内核一个相位一个钩子），占用检查是
    /// **无条件硬检查**（throw，不是 <see cref="UiAssert"/>）——2026-08 审计：属性直赋的
    /// 静默覆盖让同一内核挂第二条管线时，第一条整帧停摆且其后端每帧 BeginFrame 永不
    /// EndFrame，Debug 也不断言（<c>UiAssert.That</c> 是 <c>[Conditional("DEBUG")]</c>，
    /// 且旧检查只看本实例的 <c>_attached</c>，跨实例根本不设防）。Release 也必须拦：
    /// 这是接线错误，不是运行状态。换管线先 <see cref="Detach"/>；
    /// 多面板多流共驱一个内核的扇出件归 M1-15（届时由扇出件独占钩子、向各管线分发）。
    /// </summary>
    /// <exception cref="InvalidOperationException">本实例已 Attach，或内核的渲染钩子已被占用。</exception>
    public void Attach()
    {
        if (_mode == AttachMode.Fanout)
            throw new InvalidOperationException(
                "RenderPipeline 已登记在 PanelFanout 名下——面板由扇出件驱动，不再单独 Attach。");
        if (_mode != AttachMode.None)
            throw new InvalidOperationException("RenderPipeline 重复 Attach（换后端或重接前先 Detach）");
        if (_kernel.SettleStep != null || _kernel.DownStep != null
            || _kernel.DrainTailStep != null || _kernel.SubmitStep != null)
            throw new InvalidOperationException(
                "UiKernel 的渲染钩子已被占用：一个内核同时只能接一条 RenderPipeline。"
                + "静默覆盖会让先接的管线整帧停摆（其后端每帧 BeginFrame 永不 EndFrame）——"
                + "先对旧管线调 Detach；多面板共驱一个内核走 PanelFanout（M1-15）。");
        _kernel.SettleStep = SettleStep;
        _kernel.DownStep = DownVisit;
        _kernel.DrainTailStep = DrainTail;
        _kernel.SubmitStep = SubmitStep;
        _kernel.BeforeFrame += OnBeforeFrame;
        _invalidation.Register(_drain);
        _invalidation.Register(_extract);
        _mode = AttachMode.Solo;
    }

    /// <summary>
    /// 卸线（测试拆装用；流与后端不动）。登记在 <see cref="PanelFanout"/> 名下时**抛异常**：
    /// 那时内核钩子归扇出件所有，这里的置空会把扇出件（连同其余面板）整个拆哑——
    /// 摘一个面板的正确入口是 <c>fanout.Remove(pipeline)</c>。
    /// </summary>
    public void Detach()
    {
        if (_mode == AttachMode.Fanout)
            throw new InvalidOperationException(
                "面板由 PanelFanout 托管：用 fanout.Remove(pipeline) 摘除。"
                + "Detach 会置空扇出件占用的内核钩子，连带拆哑其余面板。");
        if (_mode != AttachMode.Solo) return;
        _kernel.SettleStep = null;
        _kernel.DownStep = null;
        _kernel.DrainTailStep = null;
        _kernel.SubmitStep = null;
        _kernel.BeforeFrame -= OnBeforeFrame;
        _invalidation.Unregister(_drain);
        _invalidation.Unregister(_extract);
        _mode = AttachMode.None;
    }

    // ── 扇出件接缝（PanelFanout 专用；语义 = Attach 的五个落点拆成可单独调度的步）──

    /// <summary>相位机（扇出件核对「面板与扇出件同内核」用）。</summary>
    internal UiKernel Kernel => _kernel;

    /// <summary>登记进扇出件（独占：已 Attach 或已登记即抛；此后 <see cref="Attach"/>/<see cref="Detach"/> 被封）。</summary>
    internal void JoinFanout()
    {
        if (_mode != AttachMode.None)
            throw new InvalidOperationException(_mode == AttachMode.Solo
                ? "RenderPipeline 已单独 Attach——进扇出件前先 Detach"
                : "RenderPipeline 已登记在另一个 PanelFanout 名下");
        _mode = AttachMode.Fanout;
    }

    /// <summary>从扇出件摘除（只回 None，不碰内核钩子——钩子归扇出件）。</summary>
    internal void LeaveFanout()
    {
        if (_mode == AttachMode.Fanout) _mode = AttachMode.None;
    }

    /// <summary>帧首：开本面板后端的帧括号（扇出件保证各面板后端互异，括号不重入）。</summary>
    internal void StepBeginFrame(ulong frameId) => OnBeforeFrame(frameId);

    /// <summary>P6 头：定格「本帧 settle 时已重编几次」的基线（DrainTail 判「本帧已整编过」用）。</summary>
    internal void StepSettleBaseline() => _rebuildsAtSettle = _extract.Rebuilds;

    /// <summary>
    /// P6 下钻的**落叶半步**（表级的 <see cref="NodeTable.CascadeVisualAt"/> 由调用方做一次，
    /// 本步只管「该节点在我的流里有叶 ⇒ 把级联变化交回上行通道」；不在我的流里 = 无声跳过，
    /// 于是多面板下同一次下钻只有叶的属主面板落账）。
    /// </summary>
    internal void StepDownLeaf(uint index, Ch channels)
    {
        NodeHandle node = _table.HandleOf(index);
        if (node.IsNone || _drain.LeafOf(node) < 0) return;   // 不在流里：进/出流是结构的事

        Ch up = Ch.None;
        if ((channels & (Ch.DownColor | Ch.DownLayer)) != 0) up |= Ch.Color;
        if ((channels & Ch.DownVisible) != 0) up |= Ch.Visible;
        if (up != Ch.None) _invalidation.Mark(node, up, InvalidateReason.CascadeDown);
    }

    /// <summary>P7 收尾（扇出件逐面板调；语义与独立接线的 DrainTail 完全相同）。</summary>
    internal void StepDrainTail(ref FrameContext ctx) => DrainTail(ref ctx);

    /// <summary>P8 提交（扇出件逐面板调；语义与独立接线的 SubmitStep 完全相同）。</summary>
    internal void StepSubmit(ref FrameContext ctx) => SubmitStep(ref ctx);

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
        StepSettleBaseline();
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
        StepDownLeaf(index, channels);
    }

    // ── P7 收尾：升级重编 + 剪枝 + 孤岛 + 神谕 ──────────────────────────────

    private void DrainTail(ref FrameContext ctx)
    {
        if (DerivedOracle)
        {
            if (_table.DerivedMatchesFullRecompute(out uint bad)) DerivedBadIndex = NodeTable.NoIndex;
            else { DerivedBadIndex = bad; DerivedOracleFailures++; }

            // 序神谕同点接入：paintOrder 在 P6 定形，此刻正是「本帧真被消费的那份序」。
            // 定向用例只撞它自己搭的编辑序，切片拼接的簿记错（例：清扫不再整体先于拼接，
            // 后切片子树前移时新下标被旧切片的清扫抹掉）要靠逐帧核对才收得住。
            if (!_table.ValidatePaintOrder(out string paintErr))
            {
                PaintOrderFailures++;
                LastPaintOrderError = paintErr;
            }
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

        // 包含剪枝的**唯一落点**（2026-08 审计裁决）。要在 quad 落定之后跑：新几何未必还在
        // 内含矩形里，而原位重写会按叶的 clipEntry 重新盖上 clipIndex——剪枝结论不会被偷偷继承，
        // 所以有变化就重跑（幂等）。P6 的 Extract.Rebuild 里那道（PruneAfterRebuild）默认关：
        // 结构帧跑两次时第二遍恒剪 0 条纯空扫，而 P6 剪是拿上一帧几何下结论。增量门的神谕腿
        // 镜像的是**这道尾剪枝**（IncrementalGate.Check 里重放它），不是 Extract 的开关。
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
