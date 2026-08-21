using System.Collections.Generic;

namespace FairyNext.Core.Text;

/// <summary>字形店诊断（每计数一条路径收据；会话累计，不随帧清零）。</summary>
public sealed class GlyphStoreDiag
{
    /// <summary>开页数。</summary>
    public long PagesOpened;
    /// <summary>退休页数（含核按钮）。</summary>
    public long PagesRetired;
    /// <summary>位置账追加条数。</summary>
    public long PlacementsAppended;
    /// <summary>入店去重命中（同 key 已在店 ⇒ 不重分配）。</summary>
    public long DedupHits;
    /// <summary>淘汰申请次数（<see cref="GlyphStore.RequestEviction"/> 调用数）。</summary>
    public long EvictionRequests;
    /// <summary>核按钮申请次数。</summary>
    public long RebuildRequests;
    /// <summary>P2 提交时二次验门刷掉的页数（申请后、生效前又被引用/触碰的页）。</summary>
    public long GateRejectsAtCommit;
    /// <summary>换代 fan-out 实际 Mark 的订阅叶数。</summary>
    public long FanOutMarks;
    /// <summary>fan-out 时丢弃的陈旧订阅句柄数（登记后节点死了）。</summary>
    public long StaleSubscribersDropped;
}

/// <summary>
/// GlyphStore（三本账之二三：**页内位置账 + 页账**，全局 append-only；
/// 度量账在 <see cref="GlyphMetricsTable"/>——本类**不持有它**，光栅半区想影响度量在类型上无路）。
///
/// 架构承诺（平面四 B「append-only 字形库与定向失效」「字形库管理三件套」）的 M1 落地：
///
///  1. **代内 append-only**：页满开新页永不重排；locator 一经发出，代内终身有效
///     （文本·不变量 4）。同 key 重复入店命中既有条目，账本长度不动。
///  2. **页型是页的属性**：位图页与 SDF 页同店分账（<see cref="GlyphPageKind"/> 在页记录上），
///     各型独立的当前开放页；「一类字形挤占全库」由分账水位暴露（WebRender 教训）。
///  3. **页淘汰双门**（M1-17 工作包裁决形态）：引用计数归零 **且** 代际距离足够老
///     （自最后触碰起的帧距 ≥ 阈值——页账的 idleFrames 语义，帧距即「代际距离」的度量）。
///     淘汰是**整页**不是单字形：位置账按页派生死活，无逐字形回收路径可走。
///  4. **generation 仅 P2**：申请（<see cref="RequestEviction"/>/<see cref="RequestRebuild"/>）
///     任何时刻只挂起；生效点唯一 = 下一个 P2 的 <see cref="Invalidation.ApplyPendingGlobal"/>
///     经 <see cref="Invalidation.GlyphStoreFanOut"/> 回到本类。窗口内一次性完成
///     退休 + 换代 + 订阅叶 fan-out Mark——任何相位都看不见「半新半旧」的页。
///  5. **提交时二次验门**：申请与生效隔着半帧，期间页可能又被引用/触碰；常规淘汰在 P2
///     重查双门，不过门的页原样留店（收据 <see cref="GlyphStoreDiag.GateRejectsAtCommit"/>）。
///     核按钮（整库重建）不验门：引用中的页也退，其订阅叶全量定向失效——这正是
///     「换代帧所有引用该页的文本叶按全局失效重排」的执法路径。
///
/// 像素内容不在本包：账本记「谁在哪页哪格」，栅格化器与 P8 预算上传随 M2-09/M1-18 进驻。
/// </summary>
public sealed class GlyphStore : IGlyphRasterSource
{
    /// <summary>页边长（texel）。locator 的 u16 页内坐标以此为定义域。</summary>
    public const int PageSize = 1024;

    /// <summary>淘汰门 2 的默认阈值：自最后触碰起 ≥ 60 帧（60fps 下 1 秒）才算「够老」。</summary>
    public const uint DefaultEvictMinIdleFrames = 60;

    private sealed class PageRecord
    {
        public GlyphPageKind Kind;
        public int ShelfX, ShelfY, ShelfH;    // shelf 打包游标（append-only：只前进）
        public bool Retired;
        public uint BornGeneration;
        public ulong LastUsedFrame;
        public readonly List<NodeHandle> Refs = new List<NodeHandle>();   // 引用登记；refCount = Count
    }

    private struct PlacementRecord
    {
        public GlyphRasterKey Key;
        public GlyphLocator Locator;          // 条目不可变；死活派生自所在页（整页淘汰的结构保证）
    }

    private readonly List<PageRecord> _pages = new List<PageRecord>();
    private readonly List<PlacementRecord> _placements = new List<PlacementRecord>();
    private readonly Dictionary<GlyphRasterKey, int> _placementIndex = new Dictionary<GlyphRasterKey, int>();
    private readonly int[] _openPage = { -1, -1 };   // 每页型一个当前开放页（下标 = GlyphPageKind）

    private readonly List<(int Page, uint MinIdle)> _pendingEvict = new List<(int, uint)>();   // 阈值随申请记：提交重验用申请时的门
    private bool _pendingRebuild;

    private uint _generation = 1;
    private ulong _frame;
    private Invalidation? _inv;

    /// <summary>店代。**只在 P2 窗口递增**；P2 之外任何读点读到的值帧内恒定。</summary>
    public uint Generation => _generation;

    /// <summary>页账条数（含已退休页——账 append-only，退休是状态不是删除）。</summary>
    public int PageCount => _pages.Count;

    /// <summary>位置账条数（append-only）。</summary>
    public int PlacementCount => _placements.Count;

    /// <summary>诊断收据。</summary>
    public GlyphStoreDiag Diag { get; } = new GlyphStoreDiag();

    /// <summary>
    /// 店钟推进（页龄的时间基）。由宿主在帧首调（挂 <see cref="UiKernel.BeforeFrame"/> 的形态）；
    /// 单调——回拨即断言（页龄算差值，回拨会让老页瞬间「变年轻」骗过淘汰门 2）。
    /// </summary>
    public void BeginFrame(ulong frameId)
    {
        UiAssert.That(frameId >= _frame, "GlyphStore 店钟回拨（页龄=帧差，回拨骗过淘汰门 2）");
        if (frameId >= _frame) _frame = frameId;
    }

    /// <summary>
    /// 接上失效协议：占用 <see cref="Invalidation.GlyphStoreFanOut"/> 钩子（**硬独占**，
    /// M1-14b Attach 同款——第二个占用者 = 两本换代账，throw 不是断言）。
    /// </summary>
    public void AttachInvalidation(Invalidation inv)
    {
        if (inv == null) throw new ArgumentNullException(nameof(inv));
        if (inv.GlyphStoreFanOut != null)
            throw new InvalidOperationException(
                "Invalidation.GlyphStoreFanOut 已被占用：一棵树域只能接一家 GlyphStore（换代账唯一）");
        if (_inv != null)
            throw new InvalidOperationException("GlyphStore 已接过失效协议：先 DetachInvalidation");
        _inv = inv;
        inv.GlyphStoreFanOut = ApplyGlobalWindow;
    }

    /// <summary>卸下钩子（测试拆装用）。</summary>
    public void DetachInvalidation()
    {
        if (_inv == null) return;
        if (_inv.GlyphStoreFanOut == (Action)ApplyGlobalWindow) _inv.GlyphStoreFanOut = null;   // 委托相等按目标+方法比，不能 ReferenceEquals（每次转换是新实例）
        _inv = null;
    }

    // ── 光栅半区 ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public ResidencyState TryGetLocator(in GlyphRasterKey key, out GlyphLocator locator)
    {
        if (_placementIndex.TryGetValue(key, out int at))
        {
            PlacementRecord rec = _placements[at];
            PageRecord page = _pages[rec.Locator.Page];
            if (!page.Retired)
            {
                page.LastUsedFrame = _frame;      // 触碰 = 页龄归零（淘汰门 2 的时间基）
                locator = rec.Locator;
                return ResidencyState.Resident;
            }
        }
        locator = default;
        return ResidencyState.Missing;
    }

    /// <inheritdoc/>
    public GlyphLocator EnsureResident(in GlyphRasterKey key, ushort texelW, ushort texelH)
    {
        if (texelW == 0 || texelH == 0 || texelW > PageSize || texelH > PageSize)
        {
            UiAssert.Fail($"EnsureResident 非法 texel 尺寸 {texelW}x{texelH}（页边长 {PageSize}）");
            return default;
        }

        if (_placementIndex.TryGetValue(key, out int at))
        {
            PlacementRecord rec = _placements[at];
            PageRecord page = _pages[rec.Locator.Page];
            if (!page.Retired)
            {
                // append-only 承诺的核心分支：同 key 再入店 = 命中既有条目，账本长度不动。
                page.LastUsedFrame = _frame;
                Diag.DedupHits++;
                return rec.Locator;
            }
            // 页已淘汰：旧条目留账（append-only），索引改指新条目——字形「重走 residency」。
        }

        int pi = _openPage[(int)key.Kind];
        if (pi < 0 || !TryPlace(_pages[pi], texelW, texelH, out int x, out int y))
        {
            pi = OpenPage(key.Kind);
            bool placed = TryPlace(_pages[pi], texelW, texelH, out x, out y);
            UiAssert.That(placed, "新开页放不下合法尺寸的字形（shelf 游标初始化损坏）");
            if (!placed) return default;
        }

        PageRecord target = _pages[pi];
        target.LastUsedFrame = _frame;
        var locator = new GlyphLocator(key.Kind, (ushort)pi, (ushort)x, (ushort)y, texelW, texelH, _generation);
        _placementIndex[key] = _placements.Count;
        _placements.Add(new PlacementRecord { Key = key, Locator = locator });
        Diag.PlacementsAppended++;
        return locator;
    }

    /// <summary>shelf 打包：行内右移，行满换行，页满返回 false。游标只前进——页内永不重排。</summary>
    private static bool TryPlace(PageRecord p, ushort w, ushort h, out int x, out int y)
    {
        if (p.ShelfX + w > PageSize)
        {
            p.ShelfY += p.ShelfH;
            p.ShelfX = 0;
            p.ShelfH = 0;
        }
        if (p.ShelfY + h > PageSize) { x = 0; y = 0; return false; }
        x = p.ShelfX;
        y = p.ShelfY;
        p.ShelfX += w;
        if (h > p.ShelfH) p.ShelfH = h;
        return true;
    }

    private int OpenPage(GlyphPageKind kind)
    {
        var page = new PageRecord { Kind = kind, BornGeneration = _generation, LastUsedFrame = _frame };
        _pages.Add(page);
        int pi = _pages.Count - 1;
        _openPage[(int)kind] = pi;
        Diag.PagesOpened++;
        return pi;
    }

    // ── 页账查询面 ──────────────────────────────────────────────────────────

    /// <summary>页型（页的属性）。</summary>
    public GlyphPageKind PageKindOf(int page) => _pages[page].Kind;

    /// <summary>页是否已退休。</summary>
    public bool IsPageRetired(int page) => _pages[page].Retired;

    /// <summary>页的引用计数（= 订阅登记条数）。</summary>
    public int PageRefCount(int page) => _pages[page].Refs.Count;

    /// <summary>页的出生代。</summary>
    public uint PageBornGeneration(int page) => _pages[page].BornGeneration;

    // ── 引用登记（淘汰门 1 + 换代 fan-out 的目标集）─────────────────────────

    /// <summary>
    /// 登记引用：<paramref name="node"/>（文本叶）持有该页上某字形的 locator。
    /// 排版消费者（M1-18）在发射侧登记、文本变更/销毁时释放；同叶多字形同页只需登记一次
    /// ——重复登记按多次计（对称释放归调用方纪律）。
    /// </summary>
    public void AddPageRef(int page, NodeHandle node)
    {
        if ((uint)page >= (uint)_pages.Count) { UiAssert.That(false, "AddPageRef 越界页号 " + page); return; }
        PageRecord rec = _pages[page];
        UiAssert.That(!rec.Retired, "AddPageRef 于已退休页——locator 应已 Missing，引用它是消费方漏查");
        if (rec.Retired) return;
        rec.Refs.Add(node);
    }

    /// <summary>释放引用（移除一条登记）。页已随核按钮退休时引用已被清列，释放成为 no-op。</summary>
    public void ReleasePageRef(int page, NodeHandle node)
    {
        if ((uint)page >= (uint)_pages.Count) { UiAssert.That(false, "ReleasePageRef 越界页号 " + page); return; }
        PageRecord rec = _pages[page];
        if (rec.Retired) return;   // 退休时已清列（fan-out 后引用随页作废）
        bool removed = rec.Refs.Remove(node);
        UiAssert.That(removed, "ReleasePageRef 未找到登记（重复释放或从未登记）");
    }

    // ── 淘汰与换代（申请任何时刻、生效只有 P2）──────────────────────────────

    /// <summary>
    /// 申请淘汰：扫页账，**双门**（引用计数归零 ∧ 页龄 ≥ <paramref name="minIdleFrames"/>）
    /// 都过的页入挂起集，并向失效协议挂起全局失效。**本调用不改变任何可见状态**：
    /// locator 照常有效、Generation 不动——生效点只有下一个 P2。返回挂起页数。
    /// </summary>
    public int RequestEviction(uint minIdleFrames = DefaultEvictMinIdleFrames)
    {
        UiAssert.That(_inv != null, "RequestEviction 前必须 AttachInvalidation（换代生效点在 P2，无失效协议 = 永不生效）");
        if (_inv == null) return 0;
        Diag.EvictionRequests++;

        int pending = 0;
        for (int i = 0; i < _pages.Count; i++)
        {
            if (!PassesEvictionGates(i, minIdleFrames)) continue;
            bool queued = false;
            for (int k = 0; k < _pendingEvict.Count; k++)
                if (_pendingEvict[k].Page == i) { queued = true; break; }
            if (!queued) _pendingEvict.Add((i, minIdleFrames));
            pending++;
        }
        if (pending > 0) _inv.RequestGlobalInvalidate(GlobalSource.GlyphStore);
        return pending;
    }

    /// <summary>
    /// 核按钮（架构三件套③）：整库重建申请——**全部**在册页退休 + 全量定向失效，
    /// 等价一次受控冷启动。不验淘汰双门：引用中的页也退，其订阅叶在换代帧被
    /// 全局失效重排。生效点同样只有 P2。
    /// </summary>
    public void RequestRebuild()
    {
        UiAssert.That(_inv != null, "RequestRebuild 前必须 AttachInvalidation");
        if (_inv == null) return;
        Diag.RebuildRequests++;
        _pendingRebuild = true;
        _inv.RequestGlobalInvalidate(GlobalSource.GlyphStore);
    }

    /// <summary>双门本体（申请与提交同判据；提交侧重查防「申请后又被引用/触碰」）。</summary>
    private bool PassesEvictionGates(int page, uint minIdleFrames)
    {
        PageRecord rec = _pages[page];
        if (rec.Retired) return false;
        if (rec.Refs.Count != 0) return false;                    // 门 1：引用计数归零
        return _frame - rec.LastUsedFrame >= minIdleFrames;       // 门 2：代际距离（帧距）够老
    }

    /// <summary>
    /// P2 全局失效窗口的换代本体（经 <see cref="Invalidation.GlyphStoreFanOut"/> 回调进来，
    /// 相位由 <see cref="Invalidation.ApplyPendingGlobal"/> 把门）。窗口内一次性完成：
    /// 退休 → 订阅叶 fan-out Mark（Ch.Content，理由 GlobalInvalidate）→ Generation++。
    /// 常规淘汰重查双门；核按钮全退不验门。窗口结束即无中间态可观察。
    /// </summary>
    private void ApplyGlobalWindow()
    {
        Invalidation inv = _inv!;
        UiAssert.That(inv.Table.Phase == FramePhase.P2_GlobalInvalidate,
            "GlyphStore 换代只准发生在 P2（fan-out 钩子被相位外调用 = 有第二个换代时点）");

        int retired = 0;
        if (_pendingRebuild)
        {
            for (int i = 0; i < _pages.Count; i++)
            {
                if (_pages[i].Retired) continue;
                RetirePage(i, inv);
                retired++;
            }
        }
        else
        {
            for (int k = 0; k < _pendingEvict.Count; k++)
            {
                (int page, uint minIdle) = _pendingEvict[k];
                // 二次验门（阈值 = 申请时的门）：申请与生效隔半帧，期间被引用/触碰的页不过门、原样留店。
                if (!PassesEvictionGates(page, minIdle))
                {
                    Diag.GateRejectsAtCommit++;
                    continue;
                }
                RetirePage(page, inv);
                retired++;
            }
        }
        _pendingEvict.Clear();
        _pendingRebuild = false;

        if (retired > 0) _generation++;   // 换代 = 页集合真的变了；空窗口不空转版本号
    }

    private void RetirePage(int page, Invalidation inv)
    {
        PageRecord rec = _pages[page];
        rec.Retired = true;
        Diag.PagesRetired++;
        if (_openPage[(int)rec.Kind] == page) _openPage[(int)rec.Kind] = -1;

        // 定向失效：只 Mark 登记过引用的叶（多推允许、漏推是 bug——文本·不变量 6 的方向）。
        // 常规淘汰过了门 1，这里必然空列；核按钮路径它就是「全量定向失效」的目标集。
        for (int i = 0; i < rec.Refs.Count; i++)
        {
            NodeHandle node = rec.Refs[i];
            if (!inv.Table.TryResolve(node, out _)) { Diag.StaleSubscribersDropped++; continue; }
            inv.Mark(node, Ch.Content, InvalidateReason.GlobalInvalidate);
            Diag.FanOutMarks++;
        }
        rec.Refs.Clear();   // 引用随页作废：叶已被通知重排，重排时对新页重新登记
    }
}
