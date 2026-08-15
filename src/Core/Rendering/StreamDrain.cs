using FairyNext.Numerics;

namespace FairyNext.Core.Rendering;

/// <summary>
/// 升级到 Structure 的原因（架构机制 4「降级阶梯」的可计数形态）。
/// 每一条都能把像素画对——**只有计数说明走了哪条路**，所以升级必须分类记，不能只有一个 bool。
/// </summary>
public enum EscalateCause : byte
{
    /// <summary>无。</summary>
    None = 0,
    /// <summary>节点在流里没有叶（容器 / 隐藏 / 已被域外剔除）。</summary>
    NotInStream = 1,
    /// <summary>内容源不再把它描述成一个叶。</summary>
    NoContent = 2,
    /// <summary>纹理或混合变了：段键变化只能由重新切段解决。</summary>
    SegmentKey = 3,
    /// <summary>预留区形状变了（发射数跨过了 slack 的 2 的幂档）。</summary>
    SlackShape = 4,
    /// <summary>发射数超预留（<see cref="RenderStream.RewriteLeafContent"/> 拒收）。</summary>
    SlackOverflow = 5,
    /// <summary>域外剔除的结论翻转：叶要进/出流。</summary>
    ClipCull = 6,
    /// <summary>落位二分翻转（烘 rect ⇄ 骑槽），或槽被多个叶共用而无法单独改写。</summary>
    SlotShape = 7,
    /// <summary>可见性跃迁：叶进/出流是数量变化。</summary>
    VisibleFlip = 8,
    /// <summary>tier-2 增量含旋转/斜切，<c>rect</c> 的字段形态表达不了。</summary>
    Tier2 = 9,
}

/// <summary>
/// 五通道消费端的前四条（架构「平面三」机制 3 的动作表）：
/// <c>Content → 叶内原位重写</c> / <c>Transform → 槽写或重新落位</c> /
/// <c>Color → 从基色重算</c> / <c>Visible → 隐显</c>。
/// 第五条 <see cref="Ch.Structure"/> 归 <see cref="Extract"/>（面板粒度整流重编）——
/// 两个消费者，因为「改一个叶」和「重编一个面板」不是同一量级的动作。
///
/// ── 本类的中心纪律：**原位动作的产物必须与整编的产物逐字节相同** ────────────────
/// 增量正确性门（M1-14 上线）拿「增量消化的流」与「从树全量重建的流」逐字节比。于是每个动作前
/// 都要先问一句「整编会不会做出**别的**形状」：预留区大小、段键、落位二分、域外剔除、叶的进出——
/// 任何一项会变，本类就**不做**这次原位改写，而是升级 Structure 让整编重来一次（
/// 五通道表原文：「数量变化 → 标 Structure」）。升级不是失败，是这条阶梯的第一级；
/// 静默地写出一个「画面对、字节不对」的流才是失败，因为下一次真的漏标时门已经分不清了。
///
/// 升级**不走** <see cref="Invalidation.Mark"/>：P7 的排水水位已冻结，回队的条目本帧不再排
/// （画面停一帧），而且 P7 的 Mark 本身计一次相位违例。改为置一个旗，由 P7 收尾
/// （<see cref="UiKernel.DrainTailStep"/>）整编一次——同帧闭合。
/// </summary>
public sealed class StreamDrain : IChannelDrain
{
    private readonly RenderStream _stream;
    private readonly NodeTable _table;
    private readonly IExtractSource _source;

    private int[] _leafOf = Array.Empty<int>();            // 节点下标 → 叶下标（-1 = 不在流里）
    private Affine2D[] _leafWorld = Array.Empty<Affine2D>();   // 叶落位时用的 world（tier-2 增量的基准）
    private bool _mapValid;
    private uint _mapEpoch;

    private QuadInstance[] _quadScratch = new QuadInstance[LeafEmitter.Scale9MaxQuads];
    private uint[] _colorScratch = new uint[LeafEmitter.Scale9MaxQuads];

    private readonly int[] _causes = new int[16];

    /// <summary>接一条流与一棵树。</summary>
    /// <param name="stream">本面板的实例流。</param>
    /// <param name="table">树域。</param>
    /// <param name="source">内容源（与 <see cref="Extract"/> 用的必须是同一个：两条路径同一份内容）。</param>
    public StreamDrain(RenderStream stream, NodeTable table, IExtractSource source)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <inheritdoc/>
    public Ch Consumes => Ch.Content | Ch.Transform | Ch.Color | Ch.Visible;

    /// <summary>本帧是否有升级到 Structure 的请求（P7 收尾据此整编一次）。</summary>
    public bool StructurePending { get; private set; }

    /// <summary>最近一次升级的原因。</summary>
    public EscalateCause LastCause { get; private set; }

    /// <summary>会话累计升级次数。</summary>
    public int Escalations { get; private set; }

    /// <summary>会话累计的叶内原位重写次数。</summary>
    public int ContentRewrites { get; private set; }

    /// <summary>会话累计的槽写次数（真的改了值的那些）。</summary>
    public int SlotWrites { get; private set; }

    /// <summary>会话累计的 tier-2 原位重 stamp 次数。</summary>
    public int Restamps { get; private set; }

    /// <summary>会话累计的重上色次数。</summary>
    public int Recolors { get; private set; }

    /// <summary>会话累计的隐显落点次数。</summary>
    public int VisibleTouches { get; private set; }

    /// <summary>叶反查表重建次数（= 流的结构代变化次数）。</summary>
    public int MapRebuilds { get; private set; }

    /// <summary>
    /// 无槽叶的 Transform 动作是否走 tier-2 原位重 stamp（默认 **false** = 按新 world 重新发射）。
    ///
    /// 为什么默认不是文档字面的 tier-2：重 stamp 算的是 <c>rect' = Δ·rect</c>，整编算的是
    /// <c>rect' = world'·rect_local</c>，两者数学相等但**浮点不必逐位相等**（float32 上
    /// <c>a + (b − a) == b</c> 不恒成立）。逐字节的增量门一上线，这类 1ulp 差就是红。
    /// 内容源在手时（Image/纯色/九宫）重新发射与整编是**同一条代码路径**，逐位相等由构造保证，
    /// 成本也只是一次发射；tier-2 留给内容源不可复跑的场合（M1-18 之前的文本叶、M2 的 tween 直写小道），
    /// 由本开关显式选择，并由测试单独钉住它的行为。
    /// </summary>
    public bool PreferTier2Restamp { get; set; }

    /// <summary>某类升级原因的累计次数。</summary>
    public int CauseCount(EscalateCause cause)
    {
        int i = (int)cause;
        return (uint)i < (uint)_causes.Length ? _causes[i] : 0;
    }

    /// <summary>
    /// 节点 → 叶下标（不在流里返回 -1）。反查表随流的结构代重建；
    /// 命中后还要核一次句柄——槽复用之后旧下标可能落在别人的叶上。
    /// </summary>
    public int LeafOf(NodeHandle node)
    {
        EnsureMap();
        if (!_table.TryResolve(node, out uint idx)) return -1;
        if (idx >= (uint)_leafOf.Length) return -1;
        int leaf = _leafOf[idx];
        if (leaf < 0 || leaf >= _stream.LeafCount) return -1;
        return _stream.Leaf(leaf).Node.Equals(node) ? leaf : -1;
    }

    /// <summary>清掉升级旗（P7 收尾整编之后调）。</summary>
    public void ClearPending() => StructurePending = false;

    /// <summary>
    /// 在 P7 收尾把叶反查表与落位基准定格（**时机是硬要求，不是优化**）。
    /// <c>_leafWorld</c> 记的是叶落位时用的那份 world，而它只在「P6 已定形、P7 内无人写树」这个窗口里
    /// 与树里的值相等。惰性拖到下一帧的排水再建表，读到的就是**新** world——tier-2 的增量当场退化成
    /// identity，症状是「rect 一动不动」且没有任何报错。
    /// 流的结构代没变时是一次比较就返回（零脏帧不为它付账）。
    /// </summary>
    public void SyncMap() => EnsureMap();

    /// <inheritdoc/>
    public void Drain(ref FrameContext ctx, Ch channel, ReadOnlySpan<NodeHandle> queue)
    {
        EnsureMap();
        for (int i = 0; i < queue.Length; i++)
        {
            switch (channel)
            {
                case Ch.Content: DrainContent(queue[i]); break;
                case Ch.Transform: DrainTransform(queue[i]); break;
                case Ch.Color: DrainColor(queue[i]); break;
                case Ch.Visible: DrainVisible(queue[i]); break;
                default:
                    UiAssert.That(false, $"StreamDrain 收到不消费的通道 {channel}");
                    return;
            }
        }
    }

    // ── Content：叶内原位重写（slack 内不动邻叶）────────────────────────────

    private void DrainContent(NodeHandle node)
    {
        int leaf = LeafOf(node);
        if (leaf < 0 || !_table.TryResolve(node, out uint idx)) { Escalate(EscalateCause.NotInStream); return; }
        LeafRange r = _stream.Leaf(leaf);
        if (!TryLeafSpec(node, out LeafSpec spec)) { Escalate(EscalateCause.NoContent); return; }

        // 段键不可原位改：纹理集与 blendClass 是切段的判据，变了就得重新切段。
        SegmentDesc seg = _stream.Segments[r.Segment];
        if (!seg.TexAt(r.TexSlot).Equals(spec.Texture) || seg.Blend != spec.Blend)
        {
            Escalate(EscalateCause.SegmentKey);
            return;
        }
        if (RewriteLeaf(leaf, in r, in spec, idx)) ContentRewrites++;
    }

    // ── Transform：有槽写槽，无槽重新落位（或 tier-2 重 stamp）──────────────

    private void DrainTransform(NodeHandle node)
    {
        int leaf = LeafOf(node);
        if (leaf < 0 || !_table.TryResolve(node, out uint idx)) { Escalate(EscalateCause.NotInStream); return; }
        LeafRange r = _stream.Leaf(leaf);
        Affine2D world = _table.WorldAt(idx);
        bool axis = Extract.IsAxisAligned(in world);

        if (r.Slot != SlotTable.IdentitySlot)
        {
            // 不再含旋转 ⇒ 整编会把它烘进 rect 并退回 identity 槽：落位形状变了，原位改不出来。
            if (axis) { Escalate(EscalateCause.SlotShape); return; }
            // 一个槽可能被同矩阵的一批兄弟叶共用（整编的 ReuseSlot）。改共用槽 = 连带搬走别人，
            // 而整编会给它们各分各的——只有独占者才谈得上「滚动 = 写一个槽矩阵」。
            if (!_stream.Slots.OwnerOf(r.Slot).Equals(node) || !SoleUserOfSlot(leaf, r.Slot))
            {
                Escalate(EscalateCause.SlotShape);
                return;
            }
            if (_stream.WriteSlot(r.Slot, in world)) SlotWrites++;
            _leafWorld[leaf] = world;
            return;
        }

        if (!axis) { Escalate(EscalateCause.SlotShape); return; }   // 现在需要槽了：认领归整编

        if (PreferTier2Restamp)
        {
            Affine2D previous = _leafWorld[leaf];
            if (!previous.TryInvert(out Affine2D inverse)) { Escalate(EscalateCause.Tier2); return; }
            Affine2D delta = Affine2D.Compose(in world, in inverse);
            if (!_stream.RestampLeaf(leaf, in delta)) { Escalate(EscalateCause.Tier2); return; }
            _leafWorld[leaf] = world;
            Restamps++;
            return;
        }

        if (!TryLeafSpec(node, out LeafSpec spec)) { Escalate(EscalateCause.NoContent); return; }
        if (RewriteLeaf(leaf, in r, in spec, idx)) ContentRewrites++;
    }

    // ── Color / Visible：从基色重算、隐显 ───────────────────────────────────

    private void DrainColor(NodeHandle node)
    {
        int leaf = LeafOf(node);
        // 容器没有叶：它的 α/置灰经下行通道落到子树的叶上，各叶自己会进本通道。
        // 这里升级 Structure 会让每一次容器淡入都整编一次面板——那是把最常见的动效判成结构变化。
        if (leaf < 0 || !_table.TryResolve(node, out uint idx)) return;
        LeafRange r = _stream.Leaf(leaf);
        IslandVisual visual = Extract.VisualOf(_table.WorldVisualAt(idx));
        if (visual.Visible != r.Visual.Visible) { Escalate(EscalateCause.VisibleFlip); return; }
        _stream.RecolorLeaf(leaf, in visual);
        Recolors++;
    }

    private void DrainVisible(NodeHandle node)
    {
        int leaf = LeafOf(node);
        // 不在流里的节点（容器、隐藏叶、被剔除的叶）翻可见性 ⇒ 一批叶要进流：整编。
        if (leaf < 0 || !_table.TryResolve(node, out uint idx)) { Escalate(EscalateCause.VisibleFlip); return; }
        if ((_table.WorldVisualAt(idx) & Visual.Visible) == 0)
        {
            // 叶要出流。原位把 α 归零画面也对，但整编产物里**没有这个实例**——
            // 逐字节门下两条路径必须同形，所以隐一个已在流里的叶同样是一次整编。
            Escalate(EscalateCause.VisibleFlip);
            return;
        }
        _stream.SetLeafVisible(leaf, true);      // 仍可见：幂等落点（α 由 Color 通道负责）
        VisibleTouches++;
    }

    // ── 共用动作 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 按当前几何重新发射一个叶并写回原区间。**每一步都先问「整编会不会做出别的形状」**：
    /// 落位二分、域外剔除、预留区大小三项任一会变，就升级 Structure 而不是硬写。
    /// </summary>
    private bool RewriteLeaf(int leaf, in LeafRange r, in LeafSpec spec, uint idx)
    {
        Affine2D world = _table.WorldAt(idx);
        bool axis = Extract.IsAxisAligned(in world);
        if (axis != (r.Slot == SlotTable.IdentitySlot)) { Escalate(EscalateCause.SlotShape); return false; }

        Affine2D toSlot = r.Slot == SlotTable.IdentitySlot ? world : Affine2D.Identity;
        _table.ResolvedAt(idx, out _, out _, out float w, out float h);
        if (Extract.ClipCulls(_stream, r.ClipEntry, r.Slot, in toSlot, w, h))
        {
            Escalate(EscalateCause.ClipCull);
            return false;
        }

        int n = Emit(in spec, w, h, in toSlot);
        if (RenderStream.SlackFor(n, spec.SlackHint) != r.Slack) { Escalate(EscalateCause.SlackShape); return false; }
        if (!_stream.RewriteLeafContent(leaf,
                new ReadOnlySpan<QuadInstance>(_quadScratch, 0, n),
                new ReadOnlySpan<uint>(_colorScratch, 0, n)))
        {
            Escalate(EscalateCause.SlackOverflow);      // 流自己也记了一条 SlackOverflowToStructure 阶梯
            return false;
        }
        _leafWorld[leaf] = world;
        return true;
    }

    /// <summary>发射 + 烘 rect（骑槽的叶几何留在本地帧，<paramref name="toSlot"/> 是 identity）。</summary>
    private int Emit(in LeafSpec spec, float w, float h, in Affine2D toSlot)
    {
        int max = LeafEmitter.MaxQuads(in spec);
        if (_quadScratch.Length < max)
        {
            Array.Resize(ref _quadScratch, max);
            Array.Resize(ref _colorScratch, max);
        }
        int n = LeafEmitter.Emit(in spec, w, h, _quadScratch, _colorScratch);
        for (int i = 0; i < n; i++) Extract.BakeToSlot(ref _quadScratch[i], in toSlot);
        return n;
    }

    private bool TryLeafSpec(NodeHandle node, out LeafSpec spec)
    {
        spec = default;
        if (!_source.TryDescribe(_table, node, out ContentRecord rec)) return false;
        if (rec.Kind != ExtractKind.Leaf || rec.OpensClip) return false;   // 开域的节点归结构
        spec = rec.Leaf;
        return true;
    }

    private bool SoleUserOfSlot(int leaf, int slot)
    {
        ReadOnlySpan<LeafRange> leaves = _stream.Leaves;
        for (int i = 0; i < leaves.Length; i++)
            if (i != leaf && leaves[i].Slot == slot) return false;
        return true;
    }

    private void Escalate(EscalateCause cause)
    {
        StructurePending = true;
        LastCause = cause;
        Escalations++;
        int i = (int)cause;
        if ((uint)i < (uint)_causes.Length) _causes[i]++;
    }

    private void EnsureMap()
    {
        if (_mapValid && _mapEpoch == _stream.StructEpoch && _leafOf.Length >= _table.Capacity) return;
        if (_leafOf.Length < _table.Capacity) _leafOf = new int[_table.Capacity];
        _leafOf.AsSpan().Fill(-1);

        ReadOnlySpan<LeafRange> leaves = _stream.Leaves;
        if (_leafWorld.Length < leaves.Length) Array.Resize(ref _leafWorld, Math.Max(8, leaves.Length));
        for (int i = 0; i < leaves.Length; i++)
        {
            if (!_table.TryResolve(leaves[i].Node, out uint idx)) continue;
            _leafOf[idx] = i;
            // 整编刚跑完时 world 就是它落位用的那份（P6 已定形、P7 内无人写树），
            // 于是 tier-2 的增量基准与整编的输入同源。
            _leafWorld[i] = _table.WorldAt(idx);
        }
        _mapValid = true;
        _mapEpoch = _stream.StructEpoch;
        MapRebuilds++;
    }
}
