using System.Collections.Generic;
using FairyNext.Contracts;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Core.Text;

/// <summary>文本系统诊断（会话累计；每条是一条路径的收据，不是布尔）。</summary>
public sealed class TextSystemDiag
{
    /// <summary>P7 侧发射次数（含增量门神谕腿的重发射）。</summary>
    public long Emissions;
    /// <summary>度量调用次数（P5 度量层 + 幂等/差分门的重跑）。</summary>
    public long MeasureCalls;
    /// <summary>Text 排水里产物 stamp 未变而**跳过** Content 标记的次数（三级惰性的正面收据）。</summary>
    public long ContentMarksSkipped;
    /// <summary>入过 pending 队列的字形键数（异步驻留面；M1 同步模式恒 0）。</summary>
    public long PendingQueued;
    /// <summary>已交付（EnsureResident + 订阅叶补 Mark Content）的字形键数。</summary>
    public long Delivered;
    /// <summary>因落在叶绑定页之外而被静音（α=0 + 计数）的字形数——M1 单纹理页帐的明码边界。</summary>
    public long CrossPageMuted;
}

/// <summary>
/// 文本系统（M1-18）：<see cref="TextCore"/> 纯函数核心的运行期接线件，一身四职：
///
///  1. **稀疏文本账**（架构 TextTable 的 M1 形态）：文本节点在树上只占一个节点 id，
///     文本串/样式/引用登记按节点稀疏挂载在这里——多数节点无文本，零成本。
///  2. **Ch.Text 排水消费者**（M1-16 留缝：布局只消费 Layout，Text 归本包）。三级惰性在此落地：
///     文本串/样式变 → Mark Text（setter，等值切断）；P5 Text 排水重排比 stamp，
///     **排版产物变才** Mark Content；autoSize 度量变才 Mark Layout（喂给 P5 度量层接力）。
///  3. **ITextMeasure**（M1-16 钉好的签名）：P5 度量层的实现方，只量不出网格；
///     度量只走 <see cref="IGlyphMetricsSource"/>——纯函数、跨 DPI bit-identical。
///  4. **ITextQuadEmitter**（P7 发射侧）：每字一 quad，走 M1-13 叶接缝
///     （SlackHint 2 的幂档由流管、MaxQuads 上界 = 本类维护的字形容量）；
///     字形未驻留时 α=0 占位（占位也占 slack 区间），到货 <see cref="DeliverPending"/> 补
///     Mark Content——几何从度量半区来、从未变过，**不闪不跳版**。
///
/// ── 引用纪律（M1-17 留缝③）─────────────────────────────────────────────
/// 发射按「本次用到的页集合」与上次**调和**（新增 AddPageRef、消失 ReleasePageRef）：
/// 集合相同零副作用——增量门神谕腿同帧重发射因此天然幂等。<see cref="DetachText"/>/
/// 文本移除对称释放全部登记。节点销毁前不调 DetachText = 引用泄漏，对称责任归调用方
/// （与 M1-17 AddPageRef 文档同款纪律）。
///
/// ── M1 面的两条明码边界 ────────────────────────────────────────────────
///  · **单纹理页帐**：一个文本叶的全部字形必须落在店的同一页（<see cref="AtlasTexture"/>
///    是叶级段键，页间路由要等 M2-09 的 per-page 纹理绑定）；落到异页的字形 α=0 静音 +
///    <see cref="TextSystemDiag.CrossPageMuted"/> 计数——宁可不显示，不画错 UV（机制 4）。
///  · **驻留默认同步**（<see cref="SyncResidency"/>=true）：M1 无异步烘焙产者，发射即入店。
///    关掉即进入 M2-09 的异步形态：发射只查账，缺字形入 pending 队列 + α=0 占位，
///    <see cref="DeliverPending"/> 是烘焙完成回调的 M1 替身（M2-09 的 P8 预算上传在此进驻）。
///
/// DPI 只在本类出现（<see cref="RasterScale"/> 只喂 <see cref="GlyphRasterKey.PxSize"/> 与
/// texel 尺寸）——<see cref="TextCore.Layout"/> 的签名里没有它，文本·不变量 1 是结构保证。
/// </summary>
public sealed class TextSystem : IChannelDrain, Layout.ITextMeasure, ITextQuadEmitter
{
    private sealed class Entry
    {
        internal NodeHandle Node;
        internal string Text = string.Empty;
        internal TextStyle Style;
        internal bool AutoWidth, AutoHeight;
        internal uint Ref;
        internal uint ContentId;
        internal ulong ProductStamp;
        internal Vector2 LastMeasured;
        internal bool HasMeasured;
        internal readonly List<int> RefPages = new List<int>();
    }

    private sealed class PendingReq
    {
        internal ushort W, H;
        internal readonly List<NodeHandle> Subscribers = new List<NodeHandle>();
    }

    private readonly UiKernel _kernel;
    private readonly NodeTable _table;
    private readonly Invalidation _inval;
    private readonly GlyphMetricsTable _metrics;
    private readonly GlyphStore _store;
    private readonly ContentTable _content;
    private readonly LayoutArena _arena = new LayoutArena();

    private readonly Dictionary<uint, Entry> _entries = new Dictionary<uint, Entry>();
    private readonly Dictionary<ulong, uint> _byNode = new Dictionary<ulong, uint>();
    private readonly Dictionary<GlyphRasterKey, PendingReq> _pending = new Dictionary<GlyphRasterKey, PendingReq>();
    private readonly List<int> _pageScratch = new List<int>(4);
    private uint _nextRef = 1;
    private Layout.LayoutEngine? _layout;
    private bool _attached;

    /// <summary>接一个内核 + 双半区 + 内容池（Extract/StreamDrain 与本类必须共用同一个内容池）。</summary>
    public TextSystem(UiKernel kernel, GlyphMetricsTable metrics, GlyphStore store, ContentTable content)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _table = kernel.Table;
        _inval = kernel.Invalidation;
    }

    /// <summary>诊断收据。</summary>
    public TextSystemDiag Diag { get; } = new TextSystemDiag();

    /// <summary>
    /// 栅格密度（DPI 缩放）。只影响 <see cref="GlyphRasterKey.PxSize"/> 与 texel 尺寸——
    /// 像素采样密度，**永不影响度量**（改它跨 DPI 用例必须逐位同）。
    /// </summary>
    public float RasterScale { get; set; } = 1f;

    /// <summary>驻留模式（见类型文档「明码边界」）。</summary>
    public bool SyncResidency { get; set; } = true;

    /// <summary>M1 页型路由（per-size 三路由归 M2-09；这里只有一路）。</summary>
    public GlyphPageKind PageKind { get; set; } = GlyphPageKind.Sdf;

    /// <summary>文本叶的段键纹理（M1 单纹理页帐；后端把它绑到店的页 0 图集）。</summary>
    public TexId AtlasTexture { get; set; } = new TexId(0xF0F0u);

    /// <summary>在册文本条目数。</summary>
    public int EntryCount => _entries.Count;

    /// <summary>pending 队列里的字形键数（同步模式恒 0）。</summary>
    public int PendingCount => _pending.Count;

    // ── 接线 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 接上失效协议（Ch.Text 消费者）+ 店钟（<see cref="GlyphStore.BeginFrame"/> 挂
    /// <see cref="UiKernel.BeforeFrame"/>，M1-17 留缝④）+ 可选的 P5 度量层进驻。
    /// <see cref="Layout.LayoutEngine.TextMeasure"/> 是**硬独占**（M1-14b Attach 同款）：
    /// 已被占用即 throw——两本文本账会让度量与排版分家。
    /// </summary>
    public void Attach(Layout.LayoutEngine? layout = null)
    {
        if (_attached) throw new InvalidOperationException("TextSystem 重复 Attach（重接前先 Detach）");
        if (layout != null && layout.TextMeasure != null)
            throw new InvalidOperationException(
                "LayoutEngine.TextMeasure 已被占用：一个布局引擎只能接一个文本度量源——"
                + "静默覆盖会让先接系统的 autoSize 全部停摆。先对旧系统调 Detach。");
        _inval.Register(this);
        _kernel.BeforeFrame += OnBeforeFrame;
        if (layout != null)
        {
            layout.TextMeasure = this;
            _layout = layout;
        }
        _attached = true;
    }

    /// <summary>卸线（测试拆装用；文本账不动）。</summary>
    public void Detach()
    {
        if (!_attached) return;
        _inval.Unregister(this);
        _kernel.BeforeFrame -= OnBeforeFrame;
        if (_layout != null && ReferenceEquals(_layout.TextMeasure, this)) _layout.TextMeasure = null;
        _layout = null;
        _attached = false;
    }

    private void OnBeforeFrame(ulong frameId) => _store.BeginFrame(frameId);

    // ── 文本账的作者面（任何相位可调；Mark 收进各自通道）────────────────────

    /// <summary>
    /// 给节点挂文本。建内容池记录（文本臂 LeafSpec：<see cref="ITextQuadEmitter"/> = 本类、
    /// SlackHint = 字形容量）并写 contentRef。autoSize 位只声明意图——resolved 槽与度量归属
    /// 由调用方经 <see cref="Layout.LayoutEngine.RegisterMeasured"/> 布防（与 Arm/RegisterLinear 同形态）。
    /// </summary>
    /// <returns>文本引用 id（发射器账本键，诊断用）。</returns>
    public uint AttachText(NodeHandle node, string text, in TextStyle style,
        bool autoWidth = false, bool autoHeight = false)
    {
        if (!_table.TryResolve(node, out _)) { UiAssert.That(false, "AttachText 收到失效句柄"); return 0; }
        if (_byNode.ContainsKey(node.Pack()))
        {
            UiAssert.That(false, "AttachText 重复挂文本（改文本走 SetText/SetStyle）");
            return 0;
        }
        var e = new Entry
        {
            Node = node,
            Text = text ?? string.Empty,
            Style = style,
            AutoWidth = autoWidth,
            AutoHeight = autoHeight,
            Ref = _nextRef++,
        };
        _entries.Add(e.Ref, e);
        _byNode.Add(node.Pack(), e.Ref);
        e.ContentId = _content.AddLeaf(SpecOf(e));
        _table.SetContentRef(node, e.ContentId);
        _inval.Mark(node, Ch.Text, InvalidateReason.UserWrite);
        return e.Ref;
    }

    /// <summary>改文本串（等值切断：同串不 Mark——三级惰性的第零级）。</summary>
    public void SetText(NodeHandle node, string text)
    {
        Entry? e = EntryOf(node);
        if (e == null) return;
        text ??= string.Empty;
        if (string.Equals(e.Text, text, StringComparison.Ordinal)) return;
        e.Text = text;
        RefreshSpec(e);
        _inval.Mark(node, Ch.Text, InvalidateReason.UserWrite);
    }

    /// <summary>改样式（逐字段位等切断）。</summary>
    public void SetStyle(NodeHandle node, in TextStyle style)
    {
        Entry? e = EntryOf(node);
        if (e == null) return;
        if (e.Style.Equals(style)) return;
        e.Style = style;
        RefreshSpec(e);
        _inval.Mark(node, Ch.Text, InvalidateReason.UserWrite);
    }

    /// <summary>
    /// 摘文本：对称释放全部页引用（淘汰门 1 的另一半）、内容记录清为「不产渲染单元」、
    /// 节点还活着就 Mark Structure（叶要出流 = 数量变化）。
    /// </summary>
    public void DetachText(NodeHandle node)
    {
        Entry? e = EntryOf(node);
        if (e == null) return;
        for (int i = 0; i < e.RefPages.Count; i++) _store.ReleasePageRef(e.RefPages[i], e.Node);
        e.RefPages.Clear();
        _content.Set(e.ContentId, default);
        _byNode.Remove(node.Pack());
        _entries.Remove(e.Ref);
        if (_table.TryResolve(node, out _)) _inval.Mark(node, Ch.Structure, InvalidateReason.UserWrite);
    }

    /// <summary>读回文本串（诊断/测试）。null = 该节点无文本。</summary>
    public string? TextOf(NodeHandle node) => EntryOf(node)?.Text;

    private Entry? EntryOf(NodeHandle node) =>
        _byNode.TryGetValue(node.Pack(), out uint r) && _entries.TryGetValue(r, out Entry? e) ? e : null;

    // ── Ch.Text 排水（P5 第一条；三级惰性的第一、二级）───────────────────────

    /// <inheritdoc/>
    public Ch Consumes => Ch.Text;

    /// <inheritdoc/>
    public void Drain(ref FrameContext ctx, Ch channel, ReadOnlySpan<NodeHandle> queue)
    {
        UiAssert.That(channel == Ch.Text, "TextSystem 只消费 Text 通道，收到 " + channel);
        for (int i = 0; i < queue.Length; i++)
        {
            NodeHandle h = queue[i];
            Entry? e = EntryOf(h);
            if (e == null || !_table.TryResolve(h, out _)) continue;
            ResolvedGeom g = _table.GetResolved(h);

            // autoSize：度量变才 Mark Layout（喂 P5 度量层接力；排在 Text 之后的 Layout 排水
            // 与 LayoutStep 同帧消化）。度量宽 = autoWidth 时不限、否则当前 resolved 宽。
            if (e.AutoWidth || e.AutoHeight)
            {
                Vector2 sz = MeasureEntry(e, e.AutoWidth ? float.PositiveInfinity : g.W);
                if (!e.HasMeasured || !BitEquals.Eq(sz.x, e.LastMeasured.x) || !BitEquals.Eq(sz.y, e.LastMeasured.y))
                {
                    e.HasMeasured = true;
                    e.LastMeasured = sz;
                    ctx.Invalidation.Mark(h, Ch.Layout, InvalidateReason.LayoutDerived);
                }
            }

            // 产物 stamp：按当前叶框重排一遍取指纹，变了才 Mark Content（三级惰性第二级）。
            // stamp 混入 (w,h)：尺寸随 autoSize 在本帧稍后变化时走 SetResolved 的派生 Mark，
            // 这里只对「同尺寸下产物没变」负责——漏推不可能，多推允许。
            ulong stamp = ProductStamp(e, g.W, g.H);
            if (stamp != e.ProductStamp)
            {
                e.ProductStamp = stamp;
                ctx.Invalidation.Mark(h, Ch.Content, InvalidateReason.LayoutDerived);
            }
            else Diag.ContentMarksSkipped++;
        }
    }

    // ── ITextMeasure（P5 度量层；只量不出网格）───────────────────────────────

    /// <inheritdoc/>
    public Vector2 Measure(NodeHandle node, float availWidth)
    {
        Entry? e = EntryOf(node);
        if (e == null)
        {
            UiAssert.That(false, "Measure 收到无文本账的节点 " + node + "（RegisterMeasured 早于 AttachText？）");
            return Vector2.zero;
        }
        return MeasureEntry(e, availWidth);
    }

    private Vector2 MeasureEntry(Entry e, float availWidth)
    {
        Diag.MeasureCalls++;
        TextCore.Layout(e.Text, in e.Style, availWidth, float.PositiveInfinity,
            _metrics, _arena, out TextLayoutResult res);
        return new Vector2(res.ContentW, res.ContentH);
    }

    // ── ITextQuadEmitter（P7 发射侧）────────────────────────────────────────

    /// <inheritdoc/>
    public int Emit(uint textRef, float width, float height, Span<QuadInstance> quads, Span<uint> baseColors)
    {
        if (!_entries.TryGetValue(textRef, out Entry? e))
        {
            UiAssert.That(false, "Emit 收到未知 textRef " + textRef + "（DetachText 后的陈旧 spec？）");
            return 0;
        }
        Diag.Emissions++;
        TextCore.Layout(e.Text, in e.Style, width, height, _metrics, _arena, out TextLayoutResult res);

        float size = e.Style.SizePx;
        ushort pxSize = QuantizePx(size);
        int n = 0;
        int leafPage = -1;
        _pageScratch.Clear();
        for (int i = 0; i < res.Glyphs.Length; i++)
        {
            PlacedGlyph g = res.Glyphs[i];
            if (!_metrics.TryGetGlyphMetrics(e.Style.FaceId, g.GlyphId, out GlyphMetrics gm) || !gm.HasOutline)
                continue;                             // 空格类：占 advance、不产 quad
            float qx = g.X + gm.XMinEm * size;        // 墨迹矩形（bbox 是 y 向上的 TTF 系，翻到 y 向下）
            float qy = g.Y - gm.YMaxEm * size;
            float qw = (gm.XMaxEm - gm.XMinEm) * size;
            float qh = (gm.YMaxEm - gm.YMinEm) * size;
            if (!(qw > 0f) || !(qh > 0f)) continue;
            if (n >= quads.Length || n >= baseColors.Length)
            {
                UiAssert.That(false, "文本发射超出容量（QuadCapacity 与实际发射数失同步）");
                break;
            }

            var key = new GlyphRasterKey(e.Style.FaceId, g.GlyphId, PageKind, pxSize);
            uint color = e.Style.Color;
            var q = new QuadInstance { Rect = new Vector4(qx, qy, qw, qh) };
            GlyphLocator loc;
            bool resident;
            if (SyncResidency)
            {
                loc = _store.EnsureResident(key,
                    TexelOf(gm.XMaxEm - gm.XMinEm, pxSize), TexelOf(gm.YMaxEm - gm.YMinEm, pxSize));
                resident = true;
            }
            else
            {
                resident = _store.TryGetLocator(key, out loc) == ResidencyState.Resident;
                if (!resident)
                    QueuePending(key, TexelOf(gm.XMaxEm - gm.XMinEm, pxSize),
                        TexelOf(gm.YMaxEm - gm.YMinEm, pxSize), e.Node);
            }
            if (resident)
            {
                if (leafPage < 0) leafPage = loc.Page;
                if (loc.Page != leafPage)
                {
                    color &= 0x00FFFFFFu;             // 异页静音（M1 单纹理页帐的明码边界）
                    Diag.CrossPageMuted++;
                }
                else if (!_pageScratch.Contains(loc.Page)) _pageScratch.Add(loc.Page);
                const float inv = 1f / GlyphStore.PageSize;
                float u0 = loc.X * inv, v0 = loc.Y * inv;
                float u1 = (loc.X + loc.W) * inv, v1 = (loc.Y + loc.H) * inv;
                q.UvA = new Vector4(u0, v0, u1, v0);
                q.UvB = new Vector4(u0, v1, u1, v1);
            }
            else color &= 0x00FFFFFFu;                // Pending：α=0 占位（几何照占、UV 零）

            quads[n] = q;
            baseColors[n] = color;
            n++;
        }
        ReconcilePageRefs(e);
        return n;
    }

    /// <summary>页引用调和（幂等：同集合零副作用——神谕腿同帧重发射的前提）。</summary>
    private void ReconcilePageRefs(Entry e)
    {
        for (int i = e.RefPages.Count - 1; i >= 0; i--)
        {
            if (_pageScratch.Contains(e.RefPages[i])) continue;
            _store.ReleasePageRef(e.RefPages[i], e.Node);
            e.RefPages.RemoveAt(i);
        }
        for (int i = 0; i < _pageScratch.Count; i++)
        {
            if (e.RefPages.Contains(_pageScratch[i])) continue;
            _store.AddPageRef(_pageScratch[i], e.Node);
            e.RefPages.Add(_pageScratch[i]);
        }
    }

    // ── pending 面（异步驻留的 M1 替身；M2-09 的烘焙队列/预算上传在此进驻）──

    private void QueuePending(in GlyphRasterKey key, ushort w, ushort h, NodeHandle subscriber)
    {
        if (!_pending.TryGetValue(key, out PendingReq? req))
        {
            req = new PendingReq { W = w, H = h };
            _pending.Add(key, req);
            Diag.PendingQueued++;
        }
        if (!req.Subscribers.Contains(subscriber)) req.Subscribers.Add(subscriber);
    }

    /// <summary>
    /// 交付全部 pending 字形（「页到货」）：入店 + 订阅叶补 Mark Content——下一帧 P7 原位重写
    /// 把 α=0 占位换成真 UV/真色。度量从未变过，占位与到货两帧几何逐字节同（不闪不跳版，
    /// 由行为用例钉住）。返回交付的键数。
    /// </summary>
    public int DeliverPending()
    {
        if (_pending.Count == 0) return 0;
        int delivered = 0;
        foreach (KeyValuePair<GlyphRasterKey, PendingReq> kv in _pending)
        {
            _store.EnsureResident(kv.Key, kv.Value.W, kv.Value.H);
            delivered++;
            for (int i = 0; i < kv.Value.Subscribers.Count; i++)
            {
                NodeHandle s = kv.Value.Subscribers[i];
                if (!_table.TryResolve(s, out _)) continue;
                _inval.Mark(s, Ch.Content, InvalidateReason.ResidencyDelivered);
            }
        }
        _pending.Clear();
        Diag.Delivered += delivered;
        return delivered;
    }

    // ── spec 维护 ───────────────────────────────────────────────────────────

    private void RefreshSpec(Entry e) => _content.Set(e.ContentId, ContentRecord.OfLeaf(SpecOf(e)));

    private LeafSpec SpecOf(Entry e) => new LeafSpec
    {
        Texture = AtlasTexture,
        Blend = BlendClass.Normal,
        Region = SpriteRegion.Solid,
        BaseColor = e.Style.Color,
        EmitFlags = AbiLayout.FlagsFontAlphaMask << AbiLayout.FlagsFontAlphaShift,
        SlackHint = QuadCapacity(e),
        Fill = RadialFillParams.None,
        Text = this,
        TextRef = e.Ref,
    };

    /// <summary>
    /// 发射容量上界 = 有轮廓字形数 + 1（省略号位）。**不依赖宽高**（断行换行不换字形数；
    /// 截断只减）——MaxQuads/SlackHint 因此在遍历前就能定。流按它向上取 2 的幂：
    /// 「11 个字变 13 个字」是一次原位重写，跨档才整编。
    /// </summary>
    private int QuadCapacity(Entry e)
    {
        int cap = 1;
        string t = e.Text;
        for (int i = 0; i < t.Length;)
        {
            int cp = t[i];
            if (char.IsHighSurrogate(t[i]) && i + 1 < t.Length && char.IsLowSurrogate(t[i + 1]))
            {
                cp = char.ConvertToUtf32(t[i], t[i + 1]);
                i += 2;
            }
            else i += 1;
            if (cp == '\r' || cp == '\n') continue;
            ushort gid = _metrics.MapCodepoint(e.Style.FaceId, cp);
            if (_metrics.TryGetGlyphMetrics(e.Style.FaceId, gid, out GlyphMetrics gm) && gm.HasOutline) cap++;
        }
        return cap;
    }

    /// <summary>产物指纹：FNV-1a 64 over 定位字形流 + 行表 + 尺寸 + 发射可见的样式量。</summary>
    private ulong ProductStamp(Entry e, float w, float h)
    {
        TextCore.Layout(e.Text, in e.Style, w, h, _metrics, _arena, out TextLayoutResult res);
        ulong hash = 14695981039346656037ul;
        Mix(ref hash, (uint)res.Glyphs.Length);
        for (int i = 0; i < res.Glyphs.Length; i++)
        {
            PlacedGlyph g = res.Glyphs[i];
            Mix(ref hash, g.GlyphId);
            Mix(ref hash, g.Line);
            Mix(ref hash, (uint)BitConverter.SingleToInt32Bits(g.X));
            Mix(ref hash, (uint)BitConverter.SingleToInt32Bits(g.Y));
        }
        Mix(ref hash, (uint)res.Lines.Length);
        for (int i = 0; i < res.Lines.Length; i++)
        {
            Mix(ref hash, (uint)BitConverter.SingleToInt32Bits(res.Lines[i].Y));
            Mix(ref hash, (uint)BitConverter.SingleToInt32Bits(res.Lines[i].Width));
        }
        Mix(ref hash, (uint)BitConverter.SingleToInt32Bits(w));
        Mix(ref hash, (uint)BitConverter.SingleToInt32Bits(h));
        Mix(ref hash, res.Truncated ? 1u : 0u);
        Mix(ref hash, e.Style.Color);
        return hash;
    }

    private static void Mix(ref ulong hash, uint v)
    {
        for (int b = 0; b < 4; b++)
        {
            hash ^= (byte)(v >> (b * 8));
            hash *= 1099511628211ul;
        }
    }

    private ushort QuantizePx(float sizePx)
    {
        int q = (int)(sizePx * RasterScale + 0.5f);
        if (q < 1) q = 1;
        if (q > 4096) q = 4096;
        return (ushort)q;
    }

    private static ushort TexelOf(float spanEm, ushort pxSize)
    {
        int t = (int)MathF.Ceiling(spanEm * pxSize);
        if (t < 1) t = 1;
        if (t > GlyphStore.PageSize) t = GlyphStore.PageSize;
        return (ushort)t;
    }
}
