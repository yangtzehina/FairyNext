using System.Collections.Generic;
using FairyNext.Contracts;
using FairyNext.Numerics;

namespace FairyNext.Core.Rendering;

/// <summary>一次 P7 收尾孤岛同步的收据（全是计数：只有计数说明走了哪条路）。</summary>
public readonly struct IslandSyncReport
{
    /// <summary>下发的孤岛记录数（含 stencil 的 Exit 半边）。</summary>
    public readonly int Synced;
    /// <summary>本帧新挂上的内容数。</summary>
    public readonly int Attached;
    /// <summary>本帧离开流的内容数。</summary>
    public readonly int Detached;
    /// <summary>因挂载参数变化而被通知重画的内容数。</summary>
    public readonly int Remarked;
    /// <summary>自报仍在动的内容数（非零 ⇒ 本帧不许跳 present）。</summary>
    public readonly int Animating;
    /// <summary>本帧轮到重画的孤岛数（<c>renderEveryN</c> 分频后的实际值）。</summary>
    public readonly int Rendered;

    internal IslandSyncReport(int synced, int attached, int detached, int remarked, int animating, int rendered)
    {
        Synced = synced; Attached = attached; Detached = detached;
        Remarked = remarked; Animating = animating; Rendered = rendered;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"islands synced={Synced} attach={Attached} detach={Detached} remark={Remarked} "
        + $"animating={Animating} rendered={Rendered}";
}

/// <summary>
/// 孤岛内容的登记表（架构接缝原文：<c>AddIsland(NodeId, IslandKind, IIslandContent)</c>）。
///
/// 一句话职责：把**节点**与**内容对象**绑起来，并在 P7 收尾把每帧的挂载事实下发给内容与后端。
/// 它不决定孤岛画在哪一层（那是 Extract 切 run 的产物）、不裁决可见性（那是树的 authored 位）、
/// 不持有任何原生对象（那是后端的事）。
///
/// ── 一表一面板 ──────────────────────────────────────────────────────────
/// 离开检测（<see cref="IIslandContent.OnDetach"/>）按「本帧没在流里出现过」判定，
/// 因此**一个表只服务一条流**：两条流共用一张表时，A 流的同步会把只在 B 流里的孤岛判成离开。
/// 多面板 = 多张表（与 <c>RenderPipeline</c> 一一对应），扇出件逐面板调各自的同步。
///
/// ── stencil 括号与内容回调的关系 ────────────────────────────────────────
/// ④ 的一个节点在流里有两条记录（<see cref="IslandBracket.Enter"/> / <see cref="IslandBracket.Exit"/>）。
/// **内容回调只发生在 Enter 半边**——内容是一份东西，不该因为它是括号就收两次 OnSync；
/// Exit 半边照常下发给后端（后端要在那一点擦除模板），但不进内容的账。
/// </summary>
public sealed class IslandTable
{
    /// <summary>
    /// stencil 嵌套深度预算。8 位模板缓冲的天花板是 255，但**嵌套八层的 mask 域在真实 UI 里
    /// 已经是设计事故**——预算取 8 是为了让「递归结构无意中嵌套出上百层」这类错误在第 9 层
    /// 就有声，而不是等到模板值回绕之后以「某些元素随机消失」的形态出现。
    /// 溢出走阶梯：该孤岛**不进流**并计一次 <see cref="DegradeKind.StencilDepthOverflow"/>。
    /// </summary>
    public const int StencilDepthBudget = 8;

    private sealed class Entry
    {
        internal NodeHandle Node;
        internal IslandKind Kind;
        internal IslandNativeKind NativeKind;
        internal int RenderEveryN;
        internal string? DebugName;
        internal IIslandContent Content = null!;
        internal IslandMount Mount = null!;
        internal bool Attached;
        internal ulong SeenFrame;
        internal int SeenSlot;
        internal int SeenClip;
        internal IslandVisual SeenVisual;
        internal Affine2D SeenMatrix;
        internal int SeenOrder;
        internal IslandClipMode SeenClipMode;
    }

    private readonly Dictionary<uint, Entry> _byNode = new Dictionary<uint, Entry>();
    private readonly List<Entry> _entries = new List<Entry>();

    /// <summary>登记的孤岛内容数。</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// 失效协议（内容自报脏的落点）。<c>RenderPipeline</c> 接线时装上；
    /// 未装时 <see cref="IslandMount.MarkDirty"/> 只计数并断言——
    /// 「自报了但没人收」必须响一声，否则外部内容会静默地永远不重画。
    /// </summary>
    public Invalidation? Invalidation { get; set; }

    /// <summary>会话累计：挂上次数。</summary>
    public long Attaches { get; private set; }

    /// <summary>会话累计：离开次数。</summary>
    public long Detaches { get; private set; }

    /// <summary>会话累计：同步下发的记录数。</summary>
    public long Syncs { get; private set; }

    /// <summary>会话累计：内容自报脏次数。</summary>
    public long DirtyMarks { get; private set; }

    /// <summary>最近一次同步的收据。</summary>
    public IslandSyncReport LastSync { get; private set; }

    /// <summary>最近一次同步里有内容自报仍在动（零脏帧短路的第三个前提）。</summary>
    public bool AnyAnimating => LastSync.Animating > 0;

    /// <summary>
    /// 登记一个孤岛内容（架构接缝 <c>AddIsland</c> 的表侧半边）。
    /// 同一节点重复登记 = 换内容（旧内容收一次 <see cref="IIslandContent.OnDetach"/>）。
    /// </summary>
    /// <param name="node">孤岛节点。</param>
    /// <param name="kind">类别（②③④；<see cref="IslandKind.None"/> 是协议违约）。</param>
    /// <param name="content">内容对象。</param>
    /// <param name="nativeKind">③的具名种类（②④ 留 <see cref="IslandNativeKind.None"/>）。</param>
    /// <param name="renderEveryN">RT 分频（1 = 每帧）。</param>
    /// <param name="debugName">诊断名。</param>
    /// <returns>交给内容的回调句柄（内容 → 运行时的唯一通道）。</returns>
    public IslandMount Add(NodeHandle node, IslandKind kind, IIslandContent content,
        IslandNativeKind nativeKind = IslandNativeKind.None, int renderEveryN = 1, string? debugName = null)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        UiAssert.That(kind != IslandKind.None, "孤岛清单是封闭枚举，IslandKind.None 是协议违约");
        UiAssert.That(!node.IsNone, "AddIsland 于空句柄");

        if (_byNode.TryGetValue(node.Index, out Entry? old))
        {
            if (old.Attached) { old.Content.OnDetach(); Detaches++; }
            _entries.Remove(old);
            _byNode.Remove(node.Index);
        }

        var entry = new Entry
        {
            Node = node,
            Kind = kind,
            NativeKind = kind == IslandKind.ExternalNative ? nativeKind : IslandNativeKind.None,
            RenderEveryN = renderEveryN <= 0 ? 1 : renderEveryN,
            DebugName = debugName,
            Content = content,
        };
        entry.Mount = new IslandMount(this, node);
        _byNode[node.Index] = entry;
        _entries.Add(entry);
        return entry.Mount;
    }

    /// <summary>摘掉一个孤岛内容（离开时内容收一次 <see cref="IIslandContent.OnDetach"/>）。</summary>
    public bool Remove(NodeHandle node)
    {
        if (!_byNode.TryGetValue(node.Index, out Entry? entry)) return false;
        if (entry.Attached) { entry.Content.OnDetach(); Detaches++; }
        _byNode.Remove(node.Index);
        _entries.Remove(entry);
        return true;
    }

    /// <summary>本节点登记的内容（未登记返回 null）。</summary>
    public IIslandContent? ContentOf(NodeHandle node) =>
        _byNode.TryGetValue(node.Index, out Entry? e) && e.Node.Equals(node) ? e.Content : null;

    /// <summary>本节点登记的③具名种类（未登记 = <see cref="IslandNativeKind.None"/>）。</summary>
    public IslandNativeKind NativeKindOf(NodeHandle node) =>
        _byNode.TryGetValue(node.Index, out Entry? e) && e.Node.Equals(node)
            ? e.NativeKind : IslandNativeKind.None;

    /// <summary>本节点是否已挂进流（<see cref="IIslandContent.OnAttach"/> 已发生且未离开）。</summary>
    public bool IsAttached(NodeHandle node) =>
        _byNode.TryGetValue(node.Index, out Entry? e) && e.Node.Equals(node) && e.Attached;

    /// <summary>
    /// 一个节点的**内容记录**（登记时给的类别/分频/诊断名）。
    /// 走 <c>ContentTable</c> 的宿主用它把孤岛写进内容池；走 FGB 的宿主不需要
    /// （记录已经在 blob 里，本表只补内容对象）。
    /// </summary>
    public ContentRecord RecordOf(NodeHandle node)
    {
        if (!_byNode.TryGetValue(node.Index, out Entry? e) || !e.Node.Equals(node)) return default;
        return ContentRecord.OfIsland(e.Kind, e.RenderEveryN, e.DebugName);
    }

    /// <summary>内容自报脏（<see cref="IslandMount.MarkDirty"/> 的落点）。</summary>
    internal void OnMountDirty(NodeHandle node)
    {
        DirtyMarks++;
        Invalidation? inval = Invalidation;
        if (inval == null)
        {
            UiAssert.That(false,
                $"孤岛 {node} 自报脏但本表未接失效协议——外部内容会静默地永远不重画");
            return;
        }
        inval.Mark(node, Ch.Content, InvalidateReason.UserWrite);
    }

    /// <summary>
    /// P7 收尾的孤岛同步（架构：<c>P7 收尾做孤岛同步（槽矩阵、visual、clip 下发）</c>）。
    ///
    /// 顺序是契约：**先把本帧的挂载事实算出来**（槽矩阵/clip 矩形/深度区间/分频位），
    /// 变了就先通知内容重画（<see cref="IIslandContent.MarkDirty"/>），再
    /// <see cref="IIslandContent.OnSync"/>，最后问一次 <see cref="IIslandContent.StillAnimating"/>
    /// 并连同 visual/clip 一起下发后端。反过来做（先问在不在动、再改参数）会让「参数刚变、
    /// 内容还没重画」的那一帧被判成静止而跳掉 present。
    /// </summary>
    /// <param name="stream">本面板的流（孤岛记录的属主）。</param>
    /// <param name="backend">后端（null = 只跑内容侧，离线路径合法）。</param>
    /// <param name="table">树域（取 resolved 尺寸）。</param>
    /// <param name="frameId">帧号（分频与离开检测的基准）。</param>
    public IslandSyncReport Sync(RenderStream stream, IRenderBackend? backend, NodeTable table, ulong frameId)
    {
        if (stream == null || table == null)
        {
            UiAssert.That(false, "IslandTable.Sync 收到 null 流或 null 树域");
            LastSync = default;
            return LastSync;
        }

        int synced = 0, attached = 0, detached = 0, remarked = 0, animating = 0, rendered = 0;
        ReadOnlySpan<IslandRecord> records = stream.Islands;

        for (int i = 0; i < records.Length; i++)
        {
            IslandRecord r = records[i];
            bool stillAnimating = false;

            if (r.Bracket != IslandBracket.Exit
                && _byNode.TryGetValue(r.Node.Index, out Entry? entry) && entry.Node.Equals(r.Node))
            {
                entry.SeenFrame = frameId;

                Affine2D matrix = stream.Slots.Matrix(r.Slot);
                Vector4 clipRect = ClipRectOf(stream, r.ClipEntry, r.ClipMode);
                float w = 0f, h = 0f;
                if (table.TryResolve(r.Node, out uint idx)) table.ResolvedAt(idx, out _, out _, out w, out h);
                bool renderThisFrame = entry.RenderEveryN <= 1
                    || frameId % (ulong)entry.RenderEveryN == 0UL;
                if (renderThisFrame) rendered++;

                bool changed = !entry.Attached
                    || entry.SeenSlot != r.Slot
                    || entry.SeenClip != r.ClipEntry
                    || entry.SeenClipMode != r.ClipMode
                    || entry.SeenOrder != r.PaintOrderIndex
                    || !entry.SeenVisual.Equals(r.Visual)
                    || !MatrixEquals(in entry.SeenMatrix, in matrix);

                var ctx = new IslandContext(r.Node, in r, in matrix, in clipRect, w, h,
                    renderThisFrame, changed, entry.Mount);

                if (!entry.Attached)
                {
                    entry.Attached = true;
                    Attaches++;
                    attached++;
                    entry.Content.OnAttach(in ctx);
                }
                else if (changed)
                {
                    remarked++;
                    entry.Content.MarkDirty();
                }

                entry.Content.OnSync(in ctx);
                entry.SeenSlot = r.Slot;
                entry.SeenClip = r.ClipEntry;
                entry.SeenClipMode = r.ClipMode;
                entry.SeenOrder = r.PaintOrderIndex;
                entry.SeenVisual = r.Visual;
                entry.SeenMatrix = matrix;

                stillAnimating = entry.Content.StillAnimating;
                if (stillAnimating) animating++;
            }
            else if (r.Bracket == IslandBracket.Exit
                && _byNode.TryGetValue(r.Node.Index, out Entry? owner) && owner.Node.Equals(r.Node))
            {
                // 括号闭：内容不收回调，但它的「仍在动」结论要跟着走——
                // 否则同一个孤岛的两条记录在后端侧对同一帧给出两种说法。
                owner.SeenFrame = frameId;
                stillAnimating = owner.Attached && owner.Content.StillAnimating;
            }

            stream.SyncIsland(backend, i, in r.Visual, stillAnimating);
            synced++;
            Syncs++;
        }

        // 离开：本帧没在流里出现过的挂上者（整流重编摘掉了它、祖先隐藏了、节点死了）。
        for (int i = 0; i < _entries.Count; i++)
        {
            Entry e = _entries[i];
            if (!e.Attached || e.SeenFrame == frameId) continue;
            e.Attached = false;
            Detaches++;
            detached++;
            e.Content.OnDetach();
        }

        LastSync = new IslandSyncReport(synced, attached, detached, remarked, animating, rendered);
        return LastSync;
    }

    /// <summary>裁剪矩形（在孤岛骑的槽帧里）；不裁剪或解析不到返回全零。</summary>
    private static Vector4 ClipRectOf(RenderStream stream, int entry, IslandClipMode mode)
    {
        if (mode == IslandClipMode.None || entry == ClipBook.NoneEntry) return default;
        int resolved = stream.Clips.Resolve(entry);
        return resolved == ClipBook.NoneEntry ? default : stream.Clips.Entry(resolved).Rect;
    }

    /// <summary>槽矩阵位等比较（与不变量 16 的切断同一把尺：同值不算变化）。</summary>
    private static bool MatrixEquals(in Affine2D a, in Affine2D b) =>
        BitEquals.Eq(a.m00, b.m00) && BitEquals.Eq(a.m01, b.m01) && BitEquals.Eq(a.m10, b.m10)
        && BitEquals.Eq(a.m11, b.m11) && BitEquals.Eq(a.tx, b.tx) && BitEquals.Eq(a.ty, b.ty);

    /// <summary>会话收据（挂/离/同步/自报脏四本账）。</summary>
    public string DescribeReceipt() =>
        $"island-table entries={Count} attach={Attaches} detach={Detaches} syncs={Syncs} dirty={DirtyMarks}";
}
