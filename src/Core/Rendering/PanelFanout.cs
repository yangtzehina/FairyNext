using System.Collections.Generic;

namespace FairyNext.Core.Rendering;

/// <summary>
/// 多面板扇出件（M1-15；架构「平面三」机制 10 的多面板半边）：**内核相位钩子的唯一占用者**，
/// 把一次相位调用分发给登记在册的每条 <see cref="RenderPipeline"/>。
///
/// 为什么需要它：内核的四个渲染钩子与七条上行通道的消费权都是**硬独占**的
/// （14b-3 裁决：静默覆盖 = 先接的管线整帧停摆、其后端 BeginFrame 永不 EndFrame）。
/// 多面板共享一棵树与一个内核时，第二条管线的 <see cref="RenderPipeline.Attach"/> 按裁决必须 throw——
/// 于是「谁占钩子」只能有一个答案：本类占，面板注册到它名下。
///
/// ── 分工：表级一次，面板级逐个 ─────────────────────────────────────────────
/// <code>
///   BeforeFrame   逐面板开各自后端的帧括号（Add 时已执法「面板后端互异」，括号不重入）
///   P6 Settle     派生列重算跑一次（world/worldVisual 是树的账，不是面板的账）；
///                 面板级只定格各自的整编基线
///   P6 Down       CascadeVisualAt 一次；落叶逐面板（叶的属主面板才落账，其余无声跳过）
///   P7 五通道     本类是排水器：每个句柄按 PanelRoot 子树**路由**给属主面板——
///                 不属于任何面板的句柄消费后丢弃并计 OrphanMarks
///   P7 收尾/P8    逐面板 DrainTail / Submit（各自的流、各自的后端、各自的收据）
/// </code>
///
/// 为什么五通道是**路由**不是组播：<see cref="StreamDrain"/> 对「不在我流里的节点」按契约升级
/// Structure（NotInStream/VisibleFlip）——那是单面板下「叶要进流」的正确动作，组播下却会让
/// A 面板的每次编辑都整编一遍 B 面板。路由把「别的面板脏了不该让本面板整编」（机制 10）
/// 落到消费端。面板子树按契约**互不嵌套**（嵌套时先注册者赢，后果是双画），本类不巡检——
/// 那是宿主的接线错误，流结构不变量门（平面六）会在字节层面响。
///
/// ── 面板增删的帧边界安全 ───────────────────────────────────────────────────
/// 帧内（P0–P9 途中）到达的 <see cref="Add"/>/<see cref="Remove"/> 不立即生效：入 pending，
/// 下一帧 BeforeFrame 统一落地。立即生效的两种死法都在后端帧括号上：帧中加入的面板没收到
/// BeginFrame 就会被 Submit；帧中摘除的面板收了 BeginFrame 却永远等不到 EndFrame。
/// 帧外调用立即生效。新加入面板的流从未编过，落地时给其 PanelRoot 补一个 Structure 脏——
/// 面板子树若建于注册之前，那些结构脏早已被消费（或根本无人消费），不补就是一条空流。
/// </summary>
public sealed class PanelFanout : IChannelDrain
{
    private readonly UiKernel _kernel;
    private readonly NodeTable _table;
    private readonly Invalidation _invalidation;

    private readonly List<RenderPipeline> _panels = new List<RenderPipeline>();
    private readonly List<(bool Add, RenderPipeline Panel)> _pending = new List<(bool, RenderPipeline)>();
    private bool _attached;

    private NodeHandle[] _roots = new NodeHandle[32];
    private int _rootCount;

    private NodeHandle[][] _routeBuf = Array.Empty<NodeHandle[]>();
    private int[] _routeCount = Array.Empty<int>();

    /// <summary>建一个扇出件（尚未占钩子；<see cref="Attach"/> 才占）。</summary>
    /// <param name="kernel">相位机（其树域即全部面板共享的树域）。</param>
    public PanelFanout(UiKernel kernel)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _table = kernel.Table;
        _invalidation = kernel.Invalidation;
    }

    // ── 只读面 ──────────────────────────────────────────────────────────────

    /// <summary>已落地的面板数（不含 pending）。</summary>
    public int PanelCount => _panels.Count;

    /// <summary>待帧边界落地的增删数。</summary>
    public int PendingChanges => _pending.Count;

    /// <summary>取第 <paramref name="index"/> 个面板。</summary>
    public RenderPipeline PanelAt(int index) => _panels[index];

    /// <summary>会话累计：P6 增量重算的节点数（表级账，只在本类记一份）。</summary>
    public long DerivedNodes { get; private set; }

    /// <summary>会话累计：P6 下行下钻访问到的节点数（表级账）。</summary>
    public long DownVisits { get; private set; }

    /// <summary>会话累计：不属于任何在册面板的上行句柄数（消费后丢弃——与单面板下「面板外的脏」同语义）。</summary>
    public long OrphanMarks { get; private set; }

    /// <inheritdoc/>
    public Ch Consumes => Ch.Content | Ch.Transform | Ch.Color | Ch.Visible | Ch.Structure;

    // ── 接线 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 占内核钩子 + 注册为五通道排水器。占用检查与 <see cref="RenderPipeline.Attach"/>
    /// 同一等级：**无条件硬检查**（配置错误，release 也拦）。
    /// </summary>
    /// <exception cref="InvalidOperationException">本实例已 Attach，或内核的渲染钩子已被占用。</exception>
    public void Attach()
    {
        if (_attached)
            throw new InvalidOperationException("PanelFanout 重复 Attach");
        if (_kernel.SettleStep != null || _kernel.DownStep != null
            || _kernel.DrainTailStep != null || _kernel.SubmitStep != null)
            throw new InvalidOperationException(
                "UiKernel 的渲染钩子已被占用：先对旧占用者（RenderPipeline 或另一个 PanelFanout）Detach。");
        _kernel.SettleStep = SettleStep;
        _kernel.DownStep = DownStep;
        _kernel.DrainTailStep = DrainTailStep;
        _kernel.SubmitStep = SubmitStep;
        _kernel.BeforeFrame += OnBeforeFrame;
        _invalidation.Register(this);
        _attached = true;
    }

    /// <summary>卸线：先摘全部面板（含 pending），再还钩子。帧内调用即抛（帧括号会被撕开）。</summary>
    public void Detach()
    {
        if (!_attached) return;
        if (_kernel.InFrame)
            throw new InvalidOperationException("PanelFanout.Detach 不得在帧内调用（面板后端的帧括号会被撕开）");
        for (int i = 0; i < _pending.Count; i++)
            if (_pending[i].Add) _pending[i].Panel.LeaveFanout();
        _pending.Clear();
        for (int i = _panels.Count - 1; i >= 0; i--) _panels[i].LeaveFanout();
        _panels.Clear();
        _kernel.SettleStep = null;
        _kernel.DownStep = null;
        _kernel.DrainTailStep = null;
        _kernel.SubmitStep = null;
        _kernel.BeforeFrame -= OnBeforeFrame;
        _invalidation.Unregister(this);
        _attached = false;
    }

    /// <summary>
    /// 登记一个面板。校验立即做（同内核 / 未接线 / **后端互异**——两面板共用一个后端实例
    /// 意味着一帧两次 BeginFrame，帧括号当场违约）；生效点看时机：帧外立即，帧内入 pending
    /// 待下一帧 BeforeFrame。登记即独占：面板此后不再接受单独 Attach/Detach。
    /// </summary>
    /// <exception cref="InvalidOperationException">面板不属于本内核、已接线，或与在册面板共用后端。</exception>
    public void Add(RenderPipeline panel)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        if (!_attached) throw new InvalidOperationException("PanelFanout 未 Attach，先接线再登记面板");
        if (!ReferenceEquals(panel.Kernel, _kernel))
            throw new InvalidOperationException("面板与扇出件不在同一个内核（各树域各自接线，扇出件不跨树）");
        for (int i = 0; i < _panels.Count; i++)
        {
            if (ReferenceEquals(_panels[i].Backend, panel.Backend))
                throw new InvalidOperationException(
                    "两个面板共用同一个后端实例：一帧会开两次 BeginFrame（帧括号重入违约）。"
                    + "多面板 = 每面板一个后端实例；共享 GPU 设备是后端实现内部的事。");
        }
        for (int i = 0; i < _pending.Count; i++)
        {
            if (_pending[i].Add && ReferenceEquals(_pending[i].Panel.Backend, panel.Backend))
                throw new InvalidOperationException("两个面板共用同一个后端实例（与 pending 面板撞后端）");
        }

        panel.JoinFanout();      // 立即独占：pending 期间也不许被单独 Attach
        if (_kernel.InFrame) _pending.Add((true, panel));
        else ApplyAdd(panel);
    }

    /// <summary>摘除一个面板（帧内 = 本帧照常走完、下一帧 BeforeFrame 落地；帧外立即）。</summary>
    public void Remove(RenderPipeline panel)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        if (_kernel.InFrame) _pending.Add((false, panel));
        else ApplyRemove(panel);
    }

    private void ApplyAdd(RenderPipeline panel)
    {
        _panels.Add(panel);
        EnsureRouteBuffers(_panels.Count);
        // 新面板的流从未编过：其子树的结构脏可能早在注册前就被消费（或无人消费而滞留成孤儿）。
        // 给面板根补一个 Structure 脏，首个参与帧就把流建出来——语义上这就是一次接线期的结构变化。
        NodeHandle root = panel.Extract.PanelRoot;
        if (!root.IsNone) _invalidation.Mark(root, Ch.Structure, InvalidateReason.UserWrite);
    }

    private void ApplyRemove(RenderPipeline panel)
    {
        if (_panels.Remove(panel)) panel.LeaveFanout();
    }

    private void OnBeforeFrame(ulong frameId)
    {
        if (_pending.Count > 0)
        {
            // 按请求序落地（Add 后 Remove = 净零，序保证语义直观）。
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Add) ApplyAdd(_pending[i].Panel);
                else ApplyRemove(_pending[i].Panel);
            }
            _pending.Clear();
        }
        for (int i = 0; i < _panels.Count; i++) _panels[i].StepBeginFrame(frameId);
    }

    // ── 相位分发 ────────────────────────────────────────────────────────────

    /// <summary>P6 派生列：表级重算一次（脏根取三条几何通道的在队快照，窥视不出队），面板级只定基线。</summary>
    private void SettleStep(ref FrameContext ctx)
    {
        for (int i = 0; i < _panels.Count; i++) _panels[i].StepSettleBaseline();

        _rootCount = 0;
        CollectRoots(Ch.Transform);
        CollectRoots(Ch.Layout);
        CollectRoots(Ch.Content);
        if (_rootCount == 0) return;
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

    /// <summary>P6 下钻：worldVisual 重算一次（表级），落叶逐面板（属主自认）。</summary>
    private void DownStep(uint index, Ch channels)
    {
        _table.CascadeVisualAt(index);
        DownVisits++;
        for (int i = 0; i < _panels.Count; i++) _panels[i].StepDownLeaf(index, channels);
    }

    private void DrainTailStep(ref FrameContext ctx)
    {
        for (int i = 0; i < _panels.Count; i++) _panels[i].StepDrainTail(ref ctx);
    }

    private void SubmitStep(ref FrameContext ctx)
    {
        for (int i = 0; i < _panels.Count; i++) _panels[i].StepSubmit(ref ctx);
    }

    // ── 五通道路由 ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Drain(ref FrameContext ctx, Ch channel, ReadOnlySpan<NodeHandle> queue)
    {
        int n = _panels.Count;
        if (n == 0)
        {
            OrphanMarks += queue.Length;
            return;
        }
        EnsureRouteBuffers(n);

        for (int i = 0; i < queue.Length; i++)
        {
            int owner = OwnerOf(queue[i]);
            if (owner < 0) { OrphanMarks++; continue; }
            NodeHandle[] buf = _routeBuf[owner];
            int count = _routeCount[owner];
            if (count == buf.Length)
            {
                Array.Resize(ref buf, buf.Length * 2);
                _routeBuf[owner] = buf;
            }
            buf[count] = queue[i];
            _routeCount[owner] = count + 1;
        }

        for (int p = 0; p < n; p++)
        {
            int count = _routeCount[p];
            if (count == 0) continue;
            _routeCount[p] = 0;
            var mine = new ReadOnlySpan<NodeHandle>(_routeBuf[p], 0, count);
            // Structure 归 Extract（一批折叠成一次整编），其余四条归 StreamDrain——
            // 与单面板接线的消费者分工逐字相同，扇出件只做了路由。
            if (channel == Ch.Structure) _panels[p].Extract.Drain(ref ctx, channel, mine);
            else _panels[p].Drain.Drain(ref ctx, channel, mine);
        }
    }

    /// <summary>句柄的属主面板下标（-1 = 不属于任何在册面板）。先注册者先判——面板子树按契约互不嵌套。</summary>
    private int OwnerOf(NodeHandle node)
    {
        for (int i = 0; i < _panels.Count; i++)
            if (_panels[i].Extract.InPanel(node)) return i;
        return -1;
    }

    private void EnsureRouteBuffers(int panels)
    {
        if (_routeBuf.Length >= panels) return;
        int old = _routeBuf.Length;
        Array.Resize(ref _routeBuf, panels);
        Array.Resize(ref _routeCount, panels);
        for (int i = old; i < panels; i++) _routeBuf[i] = new NodeHandle[16];
    }
}
