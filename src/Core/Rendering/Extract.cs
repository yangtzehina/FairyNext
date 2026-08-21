using System.Collections.Generic;
using FairyNext.Numerics;

namespace FairyNext.Core.Rendering;

// ============================================================================
// Extract：树 → 实例流的整流重编（架构「平面三」机制 10 的 MVP 形态；M1-13）。
//
// 三步，顺序是契约：
//   ① **发射**——沿 paintOrder 正序走一遍树，逐叶问内容源要 LeafSpec，用 resolved 几何与
//      worldVisual 把它变成「一批 quad + 一条 route 意图」；
//   ② **相邻性排序**——AdjacencySorter（M1-03 fork 直搬的纯函数）在**一个 run 之内**把同段键的叶
//      前移，但不越过与它 AABB 重叠者：绘制序只在视觉不可分辨处被改动；
//   ③ **切段**——按排好的序调 RenderStream.AppendLeaf，段边界由流按「≤4 纹理 × blendClass」自己切。
//
// 作用域重编（括号内原位 diff、段句柄复用、run 局部重编号）**不进 MVP**（v1.3 裁决）：
// fork 实测 237 quads 整编 <1ms，中等面板整编便宜到不值得增量；立项条件是 FrameStats 出实据。
// 于是本文件里没有任何「上次是什么样」的状态——重编就是从空开始再走一遍，
// 这也是「同一棵树两次 Extract 逐字节相等」能成为一条门的原因。
//
// ── 本包与文档的一处偏离（已回写 architecture.md 实现期补充）─────────────────
// 工作包写「可见性与 clip 域从 worldVisual 取」。**两者都不能**，病因各异：
//  · clip 域：域 id 是 ClipBook 的分配序产物，而整流重编第一件事就是 ClipBook.Reset()——
//    P6 写进 worldVisual 的那个 id 描述的是**上一次分配**，重编后立刻陈旧；
//  · 进/出流的可见性判定：Invalidation.DrainDown 的「隐藏子树免下钻」（不变量 13）让隐藏
//    子树**后代**的 worldVisual 合法陈旧（仍报 visible）。逐点判 wv 会把被隐藏祖先罩住的叶
//    照画出来，而增量门的两条腿读的是同一份陈旧列——恒等式恒真，门抓不到（2026-08 审计 CRITICAL）。
// 因此 Extract 沿同一趟 paintOrder 自己传播两者（DFS 前序保证父先于子，各一个按下标索引的数组）：
// 进流条件 = 自己 authored 可见 **且** 父链无隐藏者。worldVisual 只供 α/置灰/visible 载荷取值——
// 进了流的叶按契约没有隐藏祖先，其 wv 的新鲜由重新显示的补戳（DownVisible/RestampOnShow）
// 与派生列神谕（DerivedMatchesFullRecompute）保证。
// ============================================================================

/// <summary>内容记录的种类（封闭集）。</summary>
public enum ExtractKind : byte
{
    /// <summary>不产生任何渲染单元（容器、Group、纯逻辑节点）。</summary>
    None = 0,
    /// <summary>叶：发射 quad。</summary>
    Leaf = 1,
    /// <summary>孤岛：占一个 run 序（内容侧接口 IIslandContent 归 M1-23）。</summary>
    Island = 2,
}

/// <summary>
/// 一个节点开出的裁剪域的形状参数。rect **不在这里**——它恒等于该节点自己的 resolved 框，
/// 由 Extract 换算到槽帧。给一个可以与节点框不同的 rect 等于开一个「窗口和节点不重合」的口子，
/// 那是 fork 的 ComputeExternalWindow 每帧重算外窗口的病根。
/// </summary>
public struct ClipShape
{
    /// <summary>软边梯度（x = 水平，y = 垂直）。</summary>
    public Vector2 Soft;
    /// <summary>四角半径（圆角 mask 进流，不走 stencil）。</summary>
    public Vector4 Radii;

    /// <summary>直角硬边窗口。</summary>
    public static ClipShape Rect => default;
}

/// <summary>
/// 内容源交给 Extract 的一条记录：这个节点在流里是什么。
/// </summary>
public struct ContentRecord
{
    /// <summary>种类。</summary>
    public ExtractKind Kind;
    /// <summary>叶内容（<see cref="Kind"/> == <see cref="ExtractKind.Leaf"/> 时有效）。</summary>
    public LeafSpec Leaf;
    /// <summary>孤岛类别（<see cref="Kind"/> == <see cref="ExtractKind.Island"/> 时有效）。</summary>
    public IslandKind Island;
    /// <summary>孤岛 RT 分频（1 = 每帧）。</summary>
    public int RenderEveryN;
    /// <summary>本节点是否开一个自有裁剪域（子树默认继承之）。</summary>
    public bool OpensClip;
    /// <summary>裁剪域形状（<see cref="OpensClip"/> 时有效）。</summary>
    public ClipShape Clip;
    /// <summary>诊断名（SoA 表没有对象身份，可读名字必须侧带）。</summary>
    public string? DebugName;

    /// <summary>叶记录。</summary>
    public static ContentRecord OfLeaf(in LeafSpec leaf) =>
        new ContentRecord { Kind = ExtractKind.Leaf, Leaf = leaf, RenderEveryN = 1 };

    /// <summary>孤岛记录。</summary>
    public static ContentRecord OfIsland(IslandKind kind, int renderEveryN = 1, string? debugName = null) =>
        new ContentRecord { Kind = ExtractKind.Island, Island = kind, RenderEveryN = renderEveryN, DebugName = debugName };
}

/// <summary>
/// Extract 的内容源（接缝）：给一个节点，说它在流里是什么。
/// 运行期实现是资产平面的内容池（<see cref="ContentTable"/> 走 <c>NodeTable.ContentRef</c>），
/// 编译期实现是 FgbCompiler（M1-20）——两条路径共用本接口正是「编译器 = 无头运行时」的落点。
/// </summary>
public interface IExtractSource
{
    /// <summary>
    /// 描述一个节点。返回 false 或 <see cref="ExtractKind.None"/> 都表示「不产渲染单元」——
    /// 容器节点走的就是这条路，它不是错误。
    /// </summary>
    bool TryDescribe(NodeTable table, NodeHandle node, out ContentRecord record);
}

/// <summary>
/// 按 <c>NodeTable.ContentRef</c> 索引的内容池（数据宪法给 contentRef 的定义就是
/// 「类型化内容池索引」）。append-only：id 一经分配不再变，实例化时整段 memcpy 的 contentRef
/// 列因此可以直接照抄模板。索引 0 是「无内容」哨兵。
/// </summary>
public sealed class ContentTable : IExtractSource
{
    private ContentRecord[] _records = new ContentRecord[1];
    private int _count = 1;

    /// <summary>已分配的记录数（含哨兵 0）。</summary>
    public int Count => _count;

    /// <summary>登记一条记录，返回可写进 <c>contentRef</c> 的 id（≥1）。</summary>
    public uint Add(in ContentRecord record)
    {
        if (_count == _records.Length) Array.Resize(ref _records, _records.Length * 2);
        _records[_count] = record;
        return (uint)_count++;
    }

    /// <summary>登记一个叶。</summary>
    public uint AddLeaf(in LeafSpec leaf) => Add(ContentRecord.OfLeaf(in leaf));

    /// <summary>读一条记录（越界返回 <see cref="ExtractKind.None"/>）。</summary>
    public ContentRecord At(uint id) => id < (uint)_count ? _records[id] : default;

    /// <summary>就地改一条记录（内容通道的作者面；id 不变，故不动结构）。</summary>
    public void Set(uint id, in ContentRecord record)
    {
        if (id == 0 || id >= (uint)_count)
        {
            UiAssert.That(false, $"ContentTable.Set 于非法 id {id}");
            return;
        }
        _records[id] = record;
    }

    /// <inheritdoc/>
    public bool TryDescribe(NodeTable table, NodeHandle node, out ContentRecord record)
    {
        record = default;
        if (table == null) return false;
        uint id = table.ContentRef(node);
        if (id == 0 || id >= (uint)_count) return false;
        record = _records[id];
        return record.Kind != ExtractKind.None || record.OpensClip;
    }
}

/// <summary>一次整流重编的收据（全部是计数——降级能把像素画对，只有计数说明走了哪条路）。</summary>
public readonly struct ExtractReport
{
    /// <summary>发射的叶数。</summary>
    public readonly int Leaves;
    /// <summary>发射的实例数（含 slack 预留）。</summary>
    public readonly int Quads;
    /// <summary>孤岛数。</summary>
    public readonly int Islands;
    /// <summary>run 数。</summary>
    public readonly int Runs;
    /// <summary>段数。</summary>
    public readonly int Segments;
    /// <summary>因不可见被跳过的节点数（自己 authored 隐藏，或父链上有隐藏者）。</summary>
    public readonly int HiddenSkipped;
    /// <summary>因整只落在裁剪域外被跳过的叶数。</summary>
    public readonly int ClipCulled;
    /// <summary>相邻性排序真的挪动了位置的叶数。</summary>
    public readonly int Reordered;
    /// <summary>为含旋转/斜切的节点认领的 transform 槽数。</summary>
    public readonly int SlotsClaimed;
    /// <summary>槽荒导致无法落位、只能不画的叶数（**非零即有内容没上屏**）。</summary>
    public readonly int Unplaceable;
    /// <summary>被 inner rect 包含剪枝摘掉 clipIndex 的实例数。</summary>
    public readonly int Pruned;

    internal ExtractReport(int leaves, int quads, int islands, int runs, int segments,
        int hiddenSkipped, int clipCulled, int reordered, int slotsClaimed, int unplaceable, int pruned)
    {
        Leaves = leaves; Quads = quads; Islands = islands; Runs = runs; Segments = segments;
        HiddenSkipped = hiddenSkipped; ClipCulled = clipCulled; Reordered = reordered;
        SlotsClaimed = slotsClaimed; Unplaceable = unplaceable; Pruned = pruned;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"extract leaves={Leaves} quads={Quads} segs={Segments} runs={Runs} islands={Islands} " +
        $"hidden={HiddenSkipped} culled={ClipCulled} reordered={Reordered} slots={SlotsClaimed} " +
        $"unplaceable={Unplaceable} pruned={Pruned}";
}

/// <summary>
/// 面板粒度的整流重编器：既是 Extract 算法的宿主，也是 <see cref="Ch.Structure"/> 通道的消费者
/// （架构五通道动作表最后一行：<c>Structure → 面板粒度整流重编</c>）。
///
/// 一个实例服务**一条流 = 一个面板**。Structure 通道一帧里可能收到几十条句柄，
/// 它们全部折叠成**一次**重编——面板是重编的粒度，句柄只是「这个面板脏了」的证据。
/// </summary>
public sealed class Extract : IChannelDrain
{
    private readonly RenderStream _stream;
    private readonly NodeTable _table;
    private readonly IExtractSource _source;

    // 遍历中攒下的叶（排序与发射分两趟：相邻性排序要先看到整个 run 的 AABB）。
    private PendingLeaf[] _pending = new PendingLeaf[32];
    private PendingLeaf[] _reorderScratch = new PendingLeaf[32];
    private int _pendingCount;
    private int _runStart;                      // 当前 run 在 _pending 里的起点（孤岛切 run）

    private readonly List<AdjacencyEntry> _sortBuffer = new List<AdjacencyEntry>();
    private readonly Dictionary<long, object> _batchKeys = new Dictionary<long, object>();

    private QuadInstance[] _quadScratch = new QuadInstance[LeafEmitter.Scale9MaxQuads];
    private uint[] _colorScratch = new uint[LeafEmitter.Scale9MaxQuads];

    private int[] _clipOf = Array.Empty<int>();   // 下标 = 节点下标；值 = 该节点所在裁剪域条目
    private bool[] _hiddenOf = Array.Empty<bool>(); // 下标 = 节点下标；值 = 自己或某个祖先 authored 隐藏
    private int[] _ownedSlots = Array.Empty<int>();
    private int _ownedSlotCount;

    private int _hiddenSkipped, _clipCulled, _reordered, _slotsClaimed, _unplaceable;

    /// <summary>接一条流与一棵树。</summary>
    /// <param name="stream">本面板的实例流。</param>
    /// <param name="table">树域。</param>
    /// <param name="source">内容源。</param>
    /// <param name="backend">后端（重编要拆孤岛句柄；无后端时可省）。</param>
    public Extract(RenderStream stream, NodeTable table, IExtractSource source, IRenderBackend? backend = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Backend = backend;
        PanelRoot = table.Root;
    }

    /// <summary>本面板的流。</summary>
    public RenderStream Stream => _stream;

    /// <summary>树域。</summary>
    public NodeTable Table => _table;

    /// <summary>后端（可随时换；重编时用来拆孤岛句柄）。</summary>
    public IRenderBackend? Backend { get; set; }

    /// <summary>
    /// 面板根（默认树根）。Structure 排水只对**落在本面板子树内**的脏句柄负责——
    /// 「流按面板拆分防大流抖动」（机制 10）在消费端的另一半：别的面板脏了不该让本面板整编。
    /// </summary>
    public NodeHandle PanelRoot { get; set; }

    /// <summary>
    /// 重编后顺手跑一遍 inner rect 包含剪枝（不变量 7）。**默认关**（2026-08 审计裁决：
    /// 剪枝的落点统一在 P7 收尾——quad 在 P7 才写完，这里剪是拿上一帧几何下结论，
    /// 且结构帧会跑两遍、第二遍恒剪 0 条纯空扫）。只留给**不过管线**的离线路径
    /// （FgbCompiler = 无头运行时）显式打开；管线路径的剪枝见 <c>RenderPipeline.DrainTail</c>，
    /// 增量门的神谕腿镜像的也是那道尾剪枝而不是本开关。
    /// </summary>
    public bool PruneAfterRebuild { get; set; }

    /// <summary>累计重编次数（诊断：结构脏的真实频次，作用域重编立项的证据之一）。</summary>
    public int Rebuilds { get; private set; }

    /// <summary>被 <see cref="PanelRoot"/> 过滤掉的结构脏句柄数（不属于本面板）。</summary>
    public int ForeignMarks { get; private set; }

    /// <summary>最近一次重编的收据。</summary>
    public ExtractReport LastReport { get; private set; }

    /// <inheritdoc/>
    public Ch Consumes => Ch.Structure;

    /// <summary>
    /// Structure 通道的消费动作：本批句柄里只要有一条落在本面板内，就整编一次。
    /// **一批一次**，不是一条一次——面板才是重编的粒度。
    /// </summary>
    public void Drain(ref FrameContext ctx, Ch channel, ReadOnlySpan<NodeHandle> queue)
    {
        UiAssert.That(channel == Ch.Structure, $"Extract 只消费 Structure 通道，收到 {channel}");

        // 子树区间只算一次：它要走一遍子树，逐句柄重算会把一批脏变成 O(条数 × 子树)。
        bool hasRange = _table.TryGetSubtreeRange(PanelRoot, out int start, out int count);
        bool mine = false;
        for (int i = 0; i < queue.Length; i++)
        {
            if (hasRange && InRange(queue[i], start, count)) mine = true;
            else ForeignMarks++;
        }
        if (mine) Rebuild();
    }

    /// <summary>句柄是否在本面板子树内（paintOrder 的子树区间是连续的，判断是两次比较）。</summary>
    public bool InPanel(NodeHandle node) =>
        _table.TryGetSubtreeRange(PanelRoot, out int start, out int count) && InRange(node, start, count);

    private bool InRange(NodeHandle node, int start, int count)
    {
        uint pi = _table.PaintIndexOf(node);
        return pi != NodeTable.NotInTree && pi >= (uint)start && pi < (uint)(start + count);
    }

    /// <summary>
    /// 整流重编一次：清流 → 沿 paintOrder 的 **PanelRoot 子树区间**发射 → 相邻性排序 → 切段 → 收口。
    ///
    /// **前置条件：paintOrder 与派生列已定形**（P6 的 ApplyStructure + world/worldVisual 一遍算）。
    /// 相位机里这是自然满足的（P7 在 P6 之后）；离线路径（FgbCompiler = 无头运行时、测试夹具）
    /// 必须自己先走一遍 P6，否则读到的是上一次收敛的序与矩阵——结果**静默错**而不是崩。
    /// </summary>
    public ExtractReport Rebuild()
    {
        Rebuilds++;
        _hiddenSkipped = _clipCulled = _reordered = _slotsClaimed = _unplaceable = 0;
        _pendingCount = 0;
        _runStart = 0;

        // 上一次重编认领的槽先还回去：槽表**不**随 BeginRebuild 清空（Claim 归调用方，
        // 滚动的槽要跨重编存活），所以 Extract 自己认的必须自己还，否则每编一次漏一批槽。
        ReleaseOwnedSlots();
        _stream.BeginRebuild(Backend);

        ReadOnlySpan<int> order = _table.GetPaintOrder();
        EnsureClipMap();

        // 只发射 PanelRoot 子树：paintOrder 是 DFS 前序 ⇒ 子树是连续区间（切片表 O(1)）。
        // Drain/InPanel 按面板过滤脏句柄、Rebuild 却整树发射，等于每条「面板流」装整棵树——
        // 单面板时增量门的 oracle Extract 同样整树发射所以看不出来，M1-15 多面板一来就是重复绘制。
        // 面板根解析不到（已死/脱链）⇒ 空面板：清完流就收口，不发射任何东西。
        int panelStart = 0, panelEnd = 0;
        if (_table.TryGetSubtreeRange(PanelRoot, out int rangeStart, out int rangeCount))
        {
            panelStart = rangeStart;
            panelEnd = rangeStart + rangeCount;
        }

        // 面板根之上的祖先链照样罩可见性（面板不是可见性边界；M1-15 若裁决面板自带可见性语义，
        // 改这个种子即可）。clip 不从面板外进来：域 id 是本流分配序的产物，面板外的域在本流不存在。
        if (panelEnd > panelStart)
        {
            uint above = _table.ParentIndex((uint)order[panelStart]);
            if (above != NodeTable.NoIndex)
            {
                _clipOf[above] = ClipBook.NoneEntry;
                _hiddenOf[above] = AncestorChainHidden(above);
            }
        }

        for (int k = panelStart; k < panelEnd; k++)
        {
            uint idx = (uint)order[k];
            if (!_table.IsIndexAlive(idx)) continue;

            uint wv = _table.WorldVisualAt(idx);
            int parentClip = ParentClip(idx);

            // 不可见 = 整支不进流。判据是 **authored 可见位沿 paintOrder 父先于子传播**
            // （与 _clipOf 完全同一条路子），不是 worldVisual：「隐藏子树免下钻」让隐藏子树
            // 后代的 wv 合法陈旧（仍报 visible），逐点判 wv 会把罩在隐藏祖先下的叶照画出来。
            // 「隐显」这条通道管的是**已在流里**的叶原位清零，数量变化按五通道表升级
            // Structure —— 也就是回到这里再编一次。
            bool hidden = ParentHidden(idx) || !_table.IsIndexVisible(idx);
            _hiddenOf[idx] = hidden;
            if (hidden)
            {
                _clipOf[idx] = parentClip;
                _hiddenSkipped++;
                continue;
            }

            NodeHandle node = _table.HandleOf(idx);
            if (!_source.TryDescribe(_table, node, out ContentRecord rec))
            {
                _clipOf[idx] = parentClip;
                continue;
            }

            _table.ResolvedAt(idx, out _, out _, out float w, out float h);
            Affine2D world = _table.WorldAt(idx);
            bool axisAligned = IsAxisAligned(in world);

            int clip = parentClip;
            if (rec.OpensClip) clip = PushClip(node, in rec.Clip, parentClip, in world, axisAligned, w, h);
            _clipOf[idx] = clip;

            if (rec.Kind == ExtractKind.Island)
            {
                EmitIsland(node, in rec, clip, in world, axisAligned, wv);
                CountInherit(clip, rec.OpensClip);
                continue;
            }
            if (rec.Kind != ExtractKind.Leaf) continue;

            if (AppendPending(node, in rec.Leaf, clip, in world, axisAligned, w, h, wv))
                CountInherit(clip, rec.OpensClip);
        }

        FlushRun();
        _stream.EndRebuild();

        int pruned = PruneAfterRebuild ? _stream.PruneContainedClips() : 0;
        LastReport = new ExtractReport(
            _stream.LeafCount, _stream.QuadCount, _stream.IslandCount, _stream.RunCount, _stream.SegmentCount,
            _hiddenSkipped, _clipCulled, _reordered, _slotsClaimed, _unplaceable, pruned);
        return LastReport;
    }

    // ── 遍历期：攒叶 ────────────────────────────────────────────────────────

    private struct PendingLeaf
    {
        internal NodeHandle Node;
        internal LeafSpec Spec;
        internal float Width;
        internal float Height;
        internal Affine2D ToSlot;      // 叶本地 → 槽帧（骑槽时是 identity）
        internal int Slot;
        internal int Clip;
        internal IslandVisual Visual;
        internal float X0, Y0, X1, Y1; // 根空间 AABB（相邻性排序的重叠判据）
    }

    /// <summary>
    /// 「本单元用的是外层的裁剪域」记一笔——<see cref="ClipBook.InheritedShares"/> 是
    /// 「继承共享省下多少条预算」的实据（深嵌套列表里最常走的一条路，不记就说不清它省了什么）。
    /// </summary>
    private void CountInherit(int clip, bool ownsClip)
    {
        if (!ownsClip && clip != ClipBook.NoneEntry) _stream.Clips.Inherit(clip);
    }

    /// <returns>true = 这个叶真的进了流（false = 落位失败、整只在域外或拒发）。</returns>
    private bool AppendPending(NodeHandle node, in LeafSpec spec, int clip,
        in Affine2D world, bool axisAligned, float w, float h, uint worldVisual)
    {
        // 两条拒发都在落位之前判（拒发的叶不该占槽），判据与 LeafEmitter 字面同源。
        // ① 非有限尺寸：发射器会发 0 实例，但 RootAabb/ClipCulls 在它前面跑，∞ 会先把
        //    排序判据毒成 NaN——拒发必须发生在任何几何运算之前，并且**有声**（机制 4）。
        if (!float.IsFinite(w) || !float.IsFinite(h))
        {
            _stream.Degrades.Report(DegradeKind.NonFiniteLeafSize, $"叶 {node} 尺寸非有限（{w}×{h}）——拒发");
            return false;
        }
        // ② 平铺声明：M2 前无表达，拒发 + 计数（去向见 DegradeKind.Scale9TileUnimplemented）。
        if (LeafEmitter.IsUnsupportedTiling(in spec))
        {
            _stream.Degrades.Report(DegradeKind.Scale9TileUnimplemented,
                $"叶 {node} 声明九宫格平铺（tile={spec.Region.TileGridIndice} byTile={spec.Region.ScaleByTile}）——M2 前拒发");
            return false;
        }

        if (!Place(node, in world, axisAligned, out int slot, out Affine2D toSlot))
        {
            _unplaceable++;
            return false;
        }

        // 裁剪域外整只跳过。**只在同帧时判**（同 CanPrune 的纪律）：跨帧比两个矩形是拿两个
        // 坐标系比大小，而且槽一动结论就废。
        if (ClipCulls(_stream, clip, slot, in toSlot, w, h))
        {
            _clipCulled++;
            return false;
        }

        if (_pendingCount == _pending.Length) Array.Resize(ref _pending, _pending.Length * 2);
        RootAabb(in world, w, h, out float x0, out float y0, out float x1, out float y1);
        _pending[_pendingCount++] = new PendingLeaf
        {
            Node = node,
            Spec = spec,
            Width = w,
            Height = h,
            ToSlot = toSlot,
            Slot = slot,
            Clip = clip,
            Visual = VisualOf(worldVisual),
            X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
        };
        return true;
    }

    private void EmitIsland(NodeHandle node, in ContentRecord rec, int clip,
        in Affine2D world, bool axisAligned, uint worldVisual)
    {
        // 落位先于切 run：落不下就不该切——否则栅栏切了而孤岛没加，
        // 排序分组与 run 分组就此错位（两个分组的边界必须字面同源）。
        if (!Place(node, in world, axisAligned, out int slot, out _))
        {
            _unplaceable++;
            return;
        }
        // 孤岛是栅栏：它之前的叶必须先按本 run 的序落进流，之后的叶属于下一个 run。
        FlushRun();
        _stream.AddIsland(new IslandDesc
        {
            Kind = rec.Island,
            Slot = slot,
            ClipIndex = clip,
            Visual = VisualOf(worldVisual),
            RenderEveryN = rec.RenderEveryN <= 0 ? 1 : rec.RenderEveryN,
            DebugName = rec.DebugName,
        });
        _runStart = _pendingCount;
    }

    // ── 收口期：排序 + 切段 ─────────────────────────────────────────────────

    /// <summary>把当前 run 攒下的叶排序后发射进流。</summary>
    private void FlushRun()
    {
        int n = _pendingCount - _runStart;
        if (n <= 0) return;

        if (n > 1) SortRun(_runStart, n);
        for (int i = _runStart; i < _pendingCount; i++) EmitLeaf(in _pending[i]);
        _runStart = _pendingCount;
    }

    /// <summary>
    /// 相邻性排序（fork FairyBatching 的 <c>DoFairyBatching</c>，M1-03 已入库为纯函数）：
    /// 每个元素向前沉到最近的同键 run，但**不越过与它 AABB 重叠者**——
    /// 于是绘制序只在「换了也看不出来」的地方被改动。
    /// 键必须是**引用相等**可比的（排序器用 <c>==</c> 比 object），所以按 (纹理, 混合) 驻留。
    /// </summary>
    private void SortRun(int start, int count)
    {
        _sortBuffer.Clear();
        for (int i = 0; i < count; i++)
        {
            ref PendingLeaf p = ref _pending[start + i];
            _sortBuffer.Add(new AdjacencyEntry
            {
                key = BatchKey(p.Spec.Texture, p.Spec.Blend),
                x0 = p.X0, y0 = p.Y0, x1 = p.X1, y1 = p.Y1,
                payload = i,
            });
        }

        AdjacencySorter.Sort(_sortBuffer);

        // 按排序结果重排 pending 段（先拷一份再回填——原地重排要用同一段做输入和输出）。
        if (_reorderScratch.Length < count) Array.Resize(ref _reorderScratch, count);
        for (int i = 0; i < count; i++) _reorderScratch[i] = _pending[start + i];
        for (int i = 0; i < count; i++)
        {
            int from = _sortBuffer[i].payload;
            if (from != i) _reordered++;
            _pending[start + i] = _reorderScratch[from];
        }
    }

    private object BatchKey(TexId texture, BlendClass blend)
    {
        long key = ((long)texture.Value << 8) | (long)blend;
        if (!_batchKeys.TryGetValue(key, out object? obj))
        {
            obj = new object();
            _batchKeys[key] = obj;
        }
        return obj;
    }

    private void EmitLeaf(in PendingLeaf p)
    {
        int max = LeafEmitter.MaxQuads(in p.Spec);
        if (_quadScratch.Length < max)
        {
            Array.Resize(ref _quadScratch, max);
            Array.Resize(ref _colorScratch, max);
        }

        int n = LeafEmitter.Emit(in p.Spec, p.Width, p.Height, _quadScratch, _colorScratch);
        for (int i = 0; i < n; i++) BakeToSlot(ref _quadScratch[i], in p.ToSlot);

        var desc = new LeafDesc
        {
            Node = p.Node,
            Texture = p.Spec.Texture,
            Blend = p.Spec.Blend,
            ClipEntry = p.Clip,
            Slot = p.Slot,
            SlackHint = p.Spec.SlackHint,
            Visual = p.Visual,
            EmitFlags = p.Spec.EmitFlags,
        };
        _stream.AppendLeaf(in desc, new ReadOnlySpan<QuadInstance>(_quadScratch, 0, n),
            new ReadOnlySpan<uint>(_colorScratch, 0, n));
    }

    // ── 落位：烘进 rect 还是骑一个槽 ────────────────────────────────────────

    /// <summary>
    /// 决定一个节点的几何怎么落进实例。
    ///  · **轴对齐**（无旋转/斜切）⇒ 直接把 world 烘进 rect，骑 identity 槽——绝大多数 UI 走这条；
    ///  · 否则 ⇒ 认领一个 transform 槽、把 world 写进槽矩阵，rect 留在叶本地帧
    ///    （<c>rect</c> 只有 min 角 + size，旋转在这个字段形态里不可表达，机制 5 的槽就是为此存在的）。
    /// 槽荒时返回 false：**不画并计数**。硬烘一个包围盒会画出一个「差一点点」的画面，
    /// 比不画更难查（机制 4「无一级画错」）。
    /// </summary>
    private bool Place(NodeHandle node, in Affine2D world, bool axisAligned, out int slot, out Affine2D toSlot)
    {
        if (axisAligned)
        {
            slot = SlotTable.IdentitySlot;
            toSlot = world;
            return true;
        }

        slot = ReuseSlot(in world);
        if (slot == SlotTable.IdentitySlot)
        {
            slot = _stream.ClaimSlot(node);          // 槽荒时它自己记 SlotStarvation 阶梯
            if (slot == SlotTable.IdentitySlot)
            {
                toSlot = Affine2D.Identity;
                return false;
            }
            _stream.WriteSlot(slot, in world);
            TrackOwnedSlot(slot);
            _slotsClaimed++;
        }
        toSlot = Affine2D.Identity;
        return true;
    }

    /// <summary>本次重编内已认领过同一矩阵的槽就复用（一个旋转容器下的一批叶共用一个槽）。</summary>
    private int ReuseSlot(in Affine2D world)
    {
        for (int i = 0; i < _ownedSlotCount; i++)
        {
            Affine2D m = _stream.Slots.Matrix(_ownedSlots[i]);
            if (BitEquals.Eq(m.m00, world.m00) && BitEquals.Eq(m.m01, world.m01) &&
                BitEquals.Eq(m.m10, world.m10) && BitEquals.Eq(m.m11, world.m11) &&
                BitEquals.Eq(m.tx, world.tx) && BitEquals.Eq(m.ty, world.ty))
                return _ownedSlots[i];
        }
        return SlotTable.IdentitySlot;
    }

    private void TrackOwnedSlot(int slot)
    {
        if (_ownedSlotCount == _ownedSlots.Length)
            Array.Resize(ref _ownedSlots, _ownedSlots.Length == 0 ? 4 : _ownedSlots.Length * 2);
        _ownedSlots[_ownedSlotCount++] = slot;
    }

    private void ReleaseOwnedSlots()
    {
        for (int i = 0; i < _ownedSlotCount; i++) _stream.ReleaseSlot(_ownedSlots[i]);
        _ownedSlotCount = 0;
    }

    // ── 裁剪域 ──────────────────────────────────────────────────────────────

    private void EnsureClipMap()
    {
        if (_clipOf.Length < _table.Capacity) _clipOf = new int[_table.Capacity];
        else Array.Clear(_clipOf, 0, _clipOf.Length);
        if (_hiddenOf.Length < _table.Capacity) _hiddenOf = new bool[_table.Capacity];
        else Array.Clear(_hiddenOf, 0, _hiddenOf.Length);
    }

    private int ParentClip(uint idx)
    {
        uint p = _table.ParentIndex(idx);
        return p == NodeTable.NoIndex ? ClipBook.NoneEntry : _clipOf[p];
    }

    /// <summary>父链传播的隐藏位（面板根的父由 Rebuild 用 <see cref="AncestorChainHidden"/> 预种）。</summary>
    private bool ParentHidden(uint idx)
    {
        uint p = _table.ParentIndex(idx);
        return p != NodeTable.NoIndex && _hiddenOf[p];
    }

    /// <summary>从 <paramref name="idx"/> 沿祖先链（含自己）爬 authored 可见位——只在面板根的父上跑一次。</summary>
    private bool AncestorChainHidden(uint idx)
    {
        for (uint a = idx; a != NodeTable.NoIndex; a = _table.ParentIndex(a))
            if (!_table.IsIndexVisible(a)) return true;
        return false;
    }

    private int PushClip(NodeHandle node, in ClipShape shape, int parent,
        in Affine2D world, bool axisAligned, float w, float h)
    {
        int slot;
        Vector4 rect;
        if (axisAligned)
        {
            slot = SlotTable.IdentitySlot;
            RootAabb(in world, w, h, out float x0, out float y0, out float x1, out float y1);
            rect = new Vector4(x0, y0, x1, y1);
        }
        else
        {
            if (!Place(node, in world, axisAligned: false, out slot, out _)) return parent;
            rect = new Vector4(0f, 0f, w, h);
        }

        // 折叠（子域 ∩ 外层）要求两域同帧——ClipEntry 的 rect 按 ABI 只表达在它自己绑的槽里。
        // 不同帧时沿用父窗口：裁得更粗但不画错，与「clip 超限 → 父窗口降级」同一条阶梯。
        if (parent != ClipBook.NoneEntry)
        {
            int resolvedParent = _stream.Clips.Resolve(parent);
            if (resolvedParent != ClipBook.NoneEntry && _stream.Clips.Share(resolvedParent).Slot != slot)
            {
                _stream.Degrades.Report(DegradeKind.ClipCrossFrameToParent,
                    $"裁剪域 {node} 在槽 {slot}，外层域在槽 {_stream.Clips.Share(resolvedParent).Slot}——沿用父窗口");
                return parent;
            }
        }

        var p = new ClipParams { Rect = rect, Soft = shape.Soft, Radii = shape.Radii, Slot = slot };
        return _stream.Clips.Push(parent, in p, node, out _);
    }

    // ── 与增量路径共用的判据（M1-14）────────────────────────────────────────
    //
    // 下面四个 internal static 是**整编与增量的同一份规则**。M1-14 的 StreamDrain 走增量路径时
    // 必须做出与整编完全相同的判断（落位二分、烘 rect、域外剔除、级联视觉），否则增量流与
    // 「从树全量重建流」逐字节不等——那正是增量正确性门要红的东西。
    // 抄一份到排水器里是本包最容易犯、也最难查的错：两份规则会在**边角**上分叉。

    /// <summary>轴对齐判据（落位二分的分界：无旋转/斜切才谈得上把 world 烘进 rect）。</summary>
    internal static bool IsAxisAligned(in Affine2D m) => m.m01 == 0f && m.m10 == 0f;

    /// <summary>
    /// 叶是否整只落在其裁剪域之外（同帧才判：跨帧比两个坐标系的矩形没有意义）。
    /// 结论翻转 = 叶要进/出流 = 数量变化 = 一次整编，不是一次原位重写。
    /// </summary>
    internal static bool ClipCulls(RenderStream stream, int clip, int slot, in Affine2D toSlot, float w, float h)
    {
        if (clip == ClipBook.NoneEntry) return false;
        int resolved = stream.Clips.Resolve(clip);
        if (resolved == ClipBook.NoneEntry || stream.Clips.Share(resolved).Slot != slot) return false;
        Vector4 win = stream.Clips.Entry(resolved).Rect;
        LocalAabb(in toSlot, w, h, out float lx0, out float ly0, out float lx1, out float ly1);
        return lx1 <= win.x || lx0 >= win.z || ly1 <= win.y || ly0 >= win.w;
    }

    /// <summary>worldVisual → 落叶的级联三元（Color/Visible 通道的输入）。</summary>
    internal static IslandVisual VisualOf(uint worldVisual) => new IslandVisual
    {
        Alpha = Visual.Alpha(worldVisual) / 255f,
        Visible = (worldVisual & Visual.Visible) != 0,
        Grayed = (worldVisual & Visual.Grayed) != 0,
    };

    /// <summary>把一个**轴对齐**变换烘进 quad 的 rect；负缩放时四角 UV 跟着镜像。</summary>
    internal static void BakeToSlot(ref QuadInstance q, in Affine2D m)
    {
        // identity 快路径（骑槽的叶走的就是这条：几何留本地，变换全在槽矩阵里）
        if (m.m00 == 1f && m.m11 == 1f && m.m01 == 0f && m.m10 == 0f && m.tx == 0f && m.ty == 0f) return;
        UiAssert.That(m.m01 == 0f && m.m10 == 0f,
            "BakeToSlot 收到含旋转/斜切的变换——那种叶必须骑槽（rect 表达不了旋转）");

        float x0 = q.Rect.x, y0 = q.Rect.y;
        float x1 = x0 + q.Rect.z, y1 = y0 + q.Rect.w;
        float ax = m.m00 * x0 + m.tx, bx = m.m00 * x1 + m.tx;
        float ay = m.m11 * y0 + m.ty, by = m.m11 * y1 + m.ty;

        q.Rect = new Vector4(
            ax < bx ? ax : bx,
            ay < by ? ay : by,
            ax < bx ? bx - ax : ax - bx,
            ay < by ? by - ay : ay - by);

        // 负缩放 = 镜像：rect 只记 min 角与 size，翻转必须由**四角 UV 换位**承担，
        // 否则一张 scaleX=-1 的图会原样画出来（fork 用顶点序表达翻转，实例流没有顶点序）。
        if (m.m00 < 0f)
        {
            q.UvA = new Vector4(q.UvA.z, q.UvA.w, q.UvA.x, q.UvA.y);
            q.UvB = new Vector4(q.UvB.z, q.UvB.w, q.UvB.x, q.UvB.y);
        }
        if (m.m11 < 0f)
        {
            Vector4 a = q.UvA;
            q.UvA = q.UvB;
            q.UvB = a;
        }
    }

    /// <summary>叶框在根空间的 AABB（相邻性排序的重叠判据必须同帧，故一律换算到根）。</summary>
    private static void RootAabb(in Affine2D m, float w, float h,
        out float x0, out float y0, out float x1, out float y1)
    {
        Vector2 p0 = m.TransformPoint(new Vector2(0f, 0f));
        Vector2 p1 = m.TransformPoint(new Vector2(w, 0f));
        Vector2 p2 = m.TransformPoint(new Vector2(0f, h));
        Vector2 p3 = m.TransformPoint(new Vector2(w, h));
        x0 = MathF.Min(MathF.Min(p0.x, p1.x), MathF.Min(p2.x, p3.x));
        y0 = MathF.Min(MathF.Min(p0.y, p1.y), MathF.Min(p2.y, p3.y));
        x1 = MathF.Max(MathF.Max(p0.x, p1.x), MathF.Max(p2.x, p3.x));
        y1 = MathF.Max(MathF.Max(p0.y, p1.y), MathF.Max(p2.y, p3.y));
    }

    /// <summary>叶框在**槽帧**的 AABB（裁剪域外剔除的判据；骑槽时 <paramref name="m"/> 是 identity）。</summary>
    private static void LocalAabb(in Affine2D m, float w, float h,
        out float x0, out float y0, out float x1, out float y1) =>
        RootAabb(in m, w, h, out x0, out y0, out x1, out y1);
}
