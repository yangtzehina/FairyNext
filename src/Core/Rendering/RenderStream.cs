using FairyNext.Contracts;
using FairyNext.Numerics;

namespace FairyNext.Core.Rendering;

/// <summary>
/// 实例流的 CPU 镜像（架构「平面三 · 渲染平面」核心数据结构）：**与 GPU 1:1 的 SoA 全表**
/// 加上这些表上的原子动作。
///
/// 一句话职责：可见内容只有一种存在方式——流中的 quad 实例；本类持有那份实例流以及让它保持正确
/// 所需的全部旁账（叶区间 / 段 / run / 孤岛 / 裁剪域 / 槽），并把每一次变化压成**最小 GPU 动作**
/// 交给 <see cref="IRenderBackend"/>。它不排版、不命中、不裁决脏。
///
/// ── 表与表的关系 ────────────────────────────────────────────────────────────
/// <code>
///   quads[]      80B 实例，绘制序 = 数组序          ← 唯一主路径
///   baseColor[]  未乘 α 的基色，与 quads 同下标      ← 颜色纯函数的定义域（机制 7）
///   leaves[]     叶 → [start, start+slack) 区间     ← content/color/visible 的作用域
///   segments[]   段键 = ≤4 纹理 × blendClass        ← 一次 draw 的边界
///   runs[]       栅栏括号切出的排序区间（无 AABB）   ← 绘制序的边界
///   islands[]    不可表达内容的安全网                ← 各占一个 run 序
///   clips        ClipBook：继承共享 + inner 剪枝     ← route.clipIndex 的定义域
///   slots        SlotTable：动效的物理落点           ← route.slot 的定义域
/// </code>
/// 一个实例的 <c>route</c> 三分量分别指向后两张表和它所在段的纹理槽——**三者一律由本类盖写**，
/// 发射器（M1-13）只产几何与 UV。段是流切的、槽是流分的、裁剪域是流记的。
///
/// ── 本包（M1-11）的边界 ────────────────────────────────────────────────────
/// 交付的是**数据结构 + 结构上的原子操作 + 对后端的提交路径**。不交付：
///  · 叶发射器与 Extract（M1-13）——<see cref="AppendLeaf"/> 收现成的 quad；
///  · <c>IChannelDrain</c> 五通道排水消费（M1-14）——本类提供的是每条通道**动作本体**
///    （<see cref="RewriteLeafContent"/> / <see cref="WriteSlot"/> / <see cref="RecolorLeaf"/> /
///    <see cref="SetLeafVisible"/> / <see cref="BeginRebuild"/>），谁在哪个相位调它们是失效平面的事；
///  · 真 GPU 后端（M1-15）。
/// 于是本类此刻已经能被完整地行为测试：mock 后端收到的字节 = 本镜像持有的字节，两份快照对拍。
/// </summary>
public sealed class RenderStream
{
    private struct SegDirty
    {
        internal int Min;
        internal int Max;
    }

    private QuadInstance[] _quads = Array.Empty<QuadInstance>();
    private uint[] _baseColor = Array.Empty<uint>();
    private int _quadCount;

    private LeafRange[] _leaves = Array.Empty<LeafRange>();
    private int _leafCount;

    private SegmentDesc[] _segments = Array.Empty<SegmentDesc>();
    private SegDirty[] _segDirty = Array.Empty<SegDirty>();
    private int _segCount;

    private RunRecord[] _runs = Array.Empty<RunRecord>();
    private int _runCount;

    private IslandRecord[] _islands = Array.Empty<IslandRecord>();
    private int _islandCount;

    private readonly DegradeLog _degrade = new DegradeLog();
    private readonly SlotTable _slots;
    private readonly ClipBook _clips;

    private uint _structEpoch;
    private uint _submittedEpoch;
    private bool _epochSubmitted;
    private bool _segmentsDirty;
    private bool _fullUpload;
    private bool _building;
    private int _currentRun = -1;
    private int _currentSegment = -1;
    private StreamHandle _handle;
    private readonly string? _debugName;

    /// <summary>建一条流。</summary>
    /// <param name="debugName">诊断名（HUD/dump 里指认这条流；**不进规范化哈希**）。</param>
    public RenderStream(string? debugName = null)
    {
        _debugName = debugName;
        _slots = new SlotTable(_degrade);
        _clips = new ClipBook(_degrade);
    }

    // ── 只读面 ──────────────────────────────────────────────────────────────

    /// <summary>诊断名。</summary>
    public string? DebugName => _debugName;

    /// <summary>实例流（绘制序 = 数组序）。</summary>
    public ReadOnlySpan<QuadInstance> Quads => new ReadOnlySpan<QuadInstance>(_quads, 0, _quadCount);

    /// <summary>未乘 α 的基色（与 <see cref="Quads"/> 同下标；**永不上传 GPU**）。</summary>
    public ReadOnlySpan<uint> BaseColors => new ReadOnlySpan<uint>(_baseColor, 0, _quadCount);

    /// <summary>叶区间表。</summary>
    public ReadOnlySpan<LeafRange> Leaves => new ReadOnlySpan<LeafRange>(_leaves, 0, _leafCount);

    /// <summary>段表。</summary>
    public ReadOnlySpan<SegmentDesc> Segments => new ReadOnlySpan<SegmentDesc>(_segments, 0, _segCount);

    /// <summary>run 表（**类型上没有 AABB**——见 <see cref="RunRecord"/>）。</summary>
    public ReadOnlySpan<RunRecord> Runs => new ReadOnlySpan<RunRecord>(_runs, 0, _runCount);

    /// <summary>孤岛表。</summary>
    public ReadOnlySpan<IslandRecord> Islands => new ReadOnlySpan<IslandRecord>(_islands, 0, _islandCount);

    /// <summary>transform 槽表。</summary>
    public SlotTable Slots => _slots;

    /// <summary>裁剪域表。</summary>
    public ClipBook Clips => _clips;

    /// <summary>降级阶梯账本（P8 提交时转交后端）。</summary>
    public DegradeLog Degrades => _degrade;

    /// <summary>实例数。</summary>
    public int QuadCount => _quadCount;

    /// <summary>叶数。</summary>
    public int LeafCount => _leafCount;

    /// <summary>段数（= 段切换次数）。</summary>
    public int SegmentCount => _segCount;

    /// <summary>run 数。</summary>
    public int RunCount => _runCount;

    /// <summary>孤岛数。</summary>
    public int IslandCount => _islandCount;

    /// <summary>结构代（序派生的唯一许可证，不变量 10）。</summary>
    public uint StructEpoch => _structEpoch;

    /// <summary>已绑定的后端流句柄（<see cref="StreamHandle.None"/> = 未接后端）。</summary>
    public StreamHandle Handle => _handle;

    /// <summary>是否有待提交的变化（零脏帧短路的 CPU 侧判据）。</summary>
    public bool HasPendingWork =>
        _fullUpload || _segmentsDirty || AnyQuadDirty() || _clips.HasDirty || _slots.HasDirty
        || (!_epochSubmitted || _submittedEpoch != _structEpoch);

    /// <summary>取叶区间（越界返回 default）。</summary>
    public LeafRange Leaf(int leaf) => (uint)leaf < (uint)_leafCount ? _leaves[leaf] : default;

    /// <summary>诊断面（架构接缝：<c>StreamDiag</c>）。</summary>
    public StreamDiag Diagnostics => new StreamDiag
    {
        QuadCount = _quadCount,
        LeafCount = _leafCount,
        SegmentCount = _segCount,
        RunCount = _runCount,
        IslandCount = _islandCount,
        StructEpoch = _structEpoch,
        SlotLive = _slots.LiveCount,
        SlotHighWater = _slots.HighWater,
        SlotStarvation = _slots.Starvation,
        SlotWritesCut = _slots.WritesCut,
        ClipDomains = _clips.DomainCount,
        ClipHighWater = _clips.HighWater,
        ClipStarvation = _clips.Starvation,
        ClipInherited = _clips.InheritedShares,
        ClipPruned = _clips.Pruned,
        DegradeEvents = _degrade.TotalCount,
    };

    // ── 结构：面板粒度整流重编（Structure 通道消费，机制 10）────────────────

    /// <summary>
    /// 开一次整流重编。清 quads / leaves / segments / runs / islands 与**裁剪域表**，
    /// **不清槽表**——槽的 Claim 归调用方（滚动显式 Claim，跨重编存活），
    /// 而裁剪域是树遍历的派生物，留着旧条目只会让预算虚高。
    /// fork 实测 237 quads 整编 &lt;1ms：中等面板整编便宜到不值得增量，作用域重编要等 FrameStats 出实据才立项。
    ///
    /// 已在后端建出的孤岛必须在此拆掉，所以带 live 句柄的重编**必须**传后端：
    /// 悄悄丢掉句柄等于把 GPU 侧的孤岛泄漏成一个没人再引用、也没人再销毁的东西。
    /// </summary>
    /// <param name="backend">拆孤岛用（无 live 孤岛时可省）。</param>
    public void BeginRebuild(IRenderBackend? backend = null)
    {
        UiAssert.That(!_building, "BeginRebuild 重入（重编括号不可嵌套）");
        for (int i = 0; i < _islandCount; i++)
        {
            if (_islands[i].Handle.IsNone) continue;
            if (backend == null)
            {
                UiAssert.That(false, "重编前有 live 孤岛句柄却没给后端——句柄会泄漏在 GPU 侧");
                continue;
            }
            backend.DestroyIsland(_islands[i].Handle);
            _islands[i].Handle = IslandHandle.None;
        }
        _building = true;
        _quadCount = 0;
        _leafCount = 0;
        _segCount = 0;
        _runCount = 0;
        _islandCount = 0;
        _currentRun = -1;
        _currentSegment = -1;
        _clips.Reset();
    }

    /// <summary>
    /// 开一个 run（栅栏括号）。<paramref name="paintOrderIndex"/> 是绘制序下标——
    /// sortingOrder 由它派生（× <see cref="Abi.PaintOrderStride"/>），流不维护第二本序号账。
    /// 传 -1 = 上一个 run 的下标 + 1。跨 run 必须严格递增（后端按此校验全序）。
    /// </summary>
    public int OpenRun(int paintOrderIndex = -1)
    {
        UiAssert.That(_building, "OpenRun 只在 BeginRebuild/EndRebuild 括号内");
        CloseCurrentRun();

        int prev = _runCount > 0 ? _runs[_runCount - 1].PaintOrderIndex : -1;
        int index = paintOrderIndex >= 0 ? paintOrderIndex : prev + 1;
        UiAssert.That(index > prev, $"run 的绘制序下标必须严格递增（{prev} → {index}）");

        EnsureRuns(_runCount + 1);
        _runs[_runCount] = new RunRecord
        {
            FirstSegment = _segCount,
            SegmentCount = 0,
            FirstQuad = _quadCount,
            QuadCount = 0,
            PaintOrderIndex = index,
            ClosedByIsland = -1,
        };
        _currentRun = _runCount;
        _currentSegment = -1;      // run 边界一律切段：跨栅栏合批会把绘制序合掉
        _runCount++;
        return _currentRun;
    }

    /// <summary>
    /// 追加一个叶的实例（Extract 的落点）。返回叶下标。
    ///
    /// 三件本方法独占的事：
    ///  1. **切段**：段键 = 纹理集（≤ <see cref="Abi.SegmentMaxTextures"/>）× blendClass。
    ///     当前段装得下这张纹理且混合类别相同 ⇒ 并入；否则起新段。混合模式**进段键不是栅栏**：
    ///     Add 的粒子图自成一段，遮挡序由相邻性排序保证。
    ///  2. **盖 route**：slot / clipIndex / texSlot 三分量由流写，发射器写了即断言。
    ///  3. **上色**：color 场 = <see cref="ColorTier"/>(baseColor, visual)，grayed 落 flags 位。
    ///     发射器交出的是**基色**，不是最终色——这是不变量 9 在入口处的执法。
    ///
    /// <see cref="LeafDesc.SlackHint"/> &gt; 0 的叶（文本）预留区向上取 2 的幂：
    /// 「11 个字变 13 个字」因此是一次原位重写，而不是一次面板整编。
    /// </summary>
    public int AppendLeaf(in LeafDesc desc, ReadOnlySpan<QuadInstance> quads, ReadOnlySpan<uint> baseColors)
    {
        UiAssert.That(_building, "AppendLeaf 只在 BeginRebuild/EndRebuild 括号内");
        UiAssert.That(baseColors.Length == quads.Length, "baseColor 与 quad 必须一一对应（颜色纯函数的定义域）");
        if (_currentRun < 0) OpenRun();

        int slack = SlackFor(quads.Length, desc.SlackHint);
        int seg = SelectSegment(desc.Texture, desc.Blend, out int texSlot);
        EnsureQuads(_quadCount + slack);

        int start = _quadCount;
        for (int i = 0; i < slack; i++)
        {
            int at = start + i;
            if (i < quads.Length)
            {
                QuadInstance q = quads[i];
                uint bc = i < baseColors.Length ? baseColors[i] : 0u;
                UiAssert.That(q.Route == 0u,
                    "发射器不得写 route——slot/clipIndex/texSlot 三分量一律由流盖写");
                StampQuad(ref q, desc.Slot, desc.ClipEntry, texSlot, desc.EmitFlags, bc, in desc.Visual);
                _quads[at] = q;
                _baseColor[at] = bc;
            }
            else
            {
                _quads[at] = default;      // 预留区是真实存在的空实例（size 0、保留位零），不是一段洞
                _baseColor[at] = 0u;
            }
        }
        _quadCount = start + slack;
        _segments[seg].Count = _quadCount - _segments[seg].Start;

        EnsureLeaves(_leafCount + 1);
        _leaves[_leafCount] = new LeafRange
        {
            Node = desc.Node,
            Segment = seg,
            Run = _currentRun,
            Start = start,
            Count = quads.Length,
            Slack = slack,
            ClipEntry = desc.ClipEntry,
            Slot = desc.Slot,
            TexSlot = texSlot,
            EmitFlags = desc.EmitFlags,
            Visual = desc.Visual,
            Hidden = !desc.Visual.Visible,
        };
        return _leafCount++;
    }

    /// <summary>
    /// 记一个孤岛：它**关闭当前 run**并占一个 run 序（孤岛一律无限盒栅栏，机制 9）。
    /// 后端句柄由 <see cref="AttachIslands"/> 在接后端时补上——孤岛的 CPU 记录跨后端存活。
    /// </summary>
    public int AddIsland(in IslandDesc desc)
    {
        UiAssert.That(_building, "AddIsland 只在 BeginRebuild/EndRebuild 括号内");
        UiAssert.That(desc.Kind != IslandKind.None, "孤岛清单是封闭枚举，IslandKind.None 是协议违约");
        if (_currentRun < 0) OpenRun();

        EnsureIslands(_islandCount + 1);
        _islands[_islandCount] = new IslandRecord
        {
            Kind = desc.Kind,
            RunBefore = _currentRun + 1,
            Slot = desc.Slot,
            ClipEntry = desc.ClipIndex,
            Visual = desc.Visual,
            RenderEveryN = desc.RenderEveryN <= 0 ? 1 : desc.RenderEveryN,
            Handle = IslandHandle.None,
            StillAnimating = false,
            DebugName = desc.DebugName,
        };
        _runs[_currentRun].ClosedByIsland = _islandCount;
        CloseCurrentRun();
        _currentSegment = -1;
        return _islandCount++;
    }

    /// <summary>收一次整流重编：结构代 +1，段表与全流进上传账。</summary>
    public void EndRebuild()
    {
        UiAssert.That(_building, "EndRebuild 没有对应的 BeginRebuild");
        CloseCurrentRun();
        _building = false;
        _structEpoch++;
        _segmentsDirty = true;
        _fullUpload = true;
    }

    // ── Content 通道：叶内原位重写（slack 内不动邻叶）─────────────────────

    /// <summary>
    /// 重写一个叶的实例内容（Content 通道的最小动作）。
    /// 写区间必须 ⊆ <c>[Start, Start + Slack)</c>（不变量 4）；超 slack **必须**升级 Structure——
    /// 越写邻叶是本仓库最不许发生的一类错误（相邻叶的像素会以「另一个组件闪了一下」的形态出现，
    /// 且没有任何账能指认是谁写的）。返回 false = 调用方须标 Structure 脏。
    /// </summary>
    public bool RewriteLeafContent(int leaf, ReadOnlySpan<QuadInstance> quads, ReadOnlySpan<uint> baseColors)
    {
        if ((uint)leaf >= (uint)_leafCount)
        {
            UiAssert.That(false, $"RewriteLeafContent 收到越界叶下标 {leaf}");
            return false;
        }
        UiAssert.That(baseColors.Length == quads.Length, "baseColor 与 quad 必须一一对应");

        ref LeafRange r = ref _leaves[leaf];
        if (quads.Length > r.Slack)
        {
            _degrade.Report(DegradeKind.SlackOverflowToStructure,
                $"叶 {r.Node} 发射 {quads.Length} 个实例，超预留 {r.Slack}——升级 Structure 整编");
            return false;
        }

        int touch = quads.Length > r.Count ? quads.Length : r.Count;
        UiAssert.That(touch <= r.Slack, "不变量 4：content 写区间越出 [start, start+slack)");
        for (int i = 0; i < touch; i++)
        {
            int at = r.Start + i;
            if (i < quads.Length)
            {
                QuadInstance q = quads[i];
                uint bc = i < baseColors.Length ? baseColors[i] : 0u;
                UiAssert.That(q.Route == 0u, "发射器不得写 route——三分量由流盖写");
                StampQuad(ref q, r.Slot, r.ClipEntry, r.TexSlot, r.EmitFlags, bc, in r.Visual);
                _quads[at] = q;
                _baseColor[at] = bc;
            }
            else
            {
                _quads[at] = default;       // 变短的尾巴清干净：残留实例会以「上一帧的字还在」的形态出现
                _baseColor[at] = 0u;
            }
        }
        r.Count = quads.Length;
        MarkQuadsDirty(r.Segment, r.Start, r.Start + touch - 1);
        return true;
    }

    // ── Transform 通道：槽（tier-1）与原位重 stamp（tier-2）────────────────

    /// <summary>认领一个 transform 槽（架构接缝 <c>AllocSlot(NodeId)</c>）。槽荒退 0 并走阶梯。</summary>
    public int ClaimSlot(NodeHandle owner, TransformSlotFlags flags = TransformSlotFlags.None) =>
        _slots.Claim(owner, flags);

    /// <summary>归还槽。</summary>
    public bool ReleaseSlot(int slot) => _slots.Release(slot);

    /// <summary>
    /// 写槽矩阵（架构接缝 <c>WriteSlot(int, in Affine2D)</c>）：**滚动 = 写一个槽矩阵，零重建**。
    /// 写前位等切断（不变量 16）；真的变了才把绑定该槽的裁剪条目一并推进脏账（机制 3）。
    /// 返回 true ⇔ 值真的变了。
    /// </summary>
    public bool WriteSlot(int slot, in Affine2D m)
    {
        if (!_slots.Write(slot, in m)) return false;
        _clips.MarkSlotMoved(slot);
        return true;
    }

    /// <summary>
    /// tier-2：用一个**轴对齐**的增量变换原位重 stamp 一个叶的 rect（无槽可用时的降级路径）。
    /// 含旋转/斜切的增量在 <c>rect</c>（min 角 + size）这个字段形态里不可表达——
    /// 那时返回 false 并记 <see cref="DegradeKind.Tier2ToStructure"/>，由调用方标 Structure。
    /// **不硬算一个近似值**：近似的 rect 会画出一个「差一点点」的画面，比不画更难查。
    /// </summary>
    public bool RestampLeaf(int leaf, in Affine2D delta)
    {
        if ((uint)leaf >= (uint)_leafCount)
        {
            UiAssert.That(false, $"RestampLeaf 收到越界叶下标 {leaf}");
            return false;
        }
        LeafRange r = _leaves[leaf];
        if (delta.m01 != 0f || delta.m10 != 0f)
        {
            _degrade.Report(DegradeKind.Tier2ToStructure,
                $"叶 {r.Node} 的 tier-2 增量含旋转/斜切（m01={delta.m01} m10={delta.m10}）——升级 Structure");
            return false;
        }
        if (r.Count == 0) return true;

        for (int i = 0; i < r.Count; i++)
        {
            ref QuadInstance q = ref _quads[r.Start + i];
            var min = new Vector2(q.Rect.x, q.Rect.y);
            var max = new Vector2(q.Rect.x + q.Rect.z, q.Rect.y + q.Rect.w);
            Vector2 a = delta.TransformPoint(min);
            Vector2 b = delta.TransformPoint(max);
            float x0 = MathF.Min(a.x, b.x), y0 = MathF.Min(a.y, b.y);
            q.Rect = new Vector4(x0, y0, MathF.Abs(b.x - a.x), MathF.Abs(b.y - a.y));
        }
        MarkQuadsDirty(r.Segment, r.Start, r.Start + r.Count - 1);
        return true;
    }

    // ── Color / Visible 通道：从基色重算（机制 7，不变量 9）────────────────

    /// <summary>
    /// 从**基色**重算一个叶的 color 场并落 grayed 位（Color 通道的最小动作）。
    /// 输入是 worldVisual 三元，输出是纯函数——没有「按比例重标」，也就没有 fork 的
    /// <c>bakedAlpha ≤ 0</c> 退化分支与反复乘除的色值漂移。返回改写的实例数。
    /// </summary>
    public int RecolorLeaf(int leaf, in IslandVisual visual)
    {
        if ((uint)leaf >= (uint)_leafCount)
        {
            UiAssert.That(false, $"RecolorLeaf 收到越界叶下标 {leaf}");
            return 0;
        }
        ref LeafRange r = ref _leaves[leaf];
        r.Visual = visual;
        r.Hidden = !visual.Visible;
        if (r.Count == 0) return 0;

        for (int i = 0; i < r.Count; i++)
        {
            int at = r.Start + i;
            ref QuadInstance q = ref _quads[at];
            q.Color = ColorTier.Apply(_baseColor[at], in visual);
            q.Flags = ColorTier.WithGrayed(q.Flags, visual.Grayed);
        }
        MarkQuadsDirty(r.Segment, r.Start, r.Start + r.Count - 1);
        return r.Count;
    }

    /// <summary>
    /// 隐显一个叶（Visible 通道的最小动作）：α 归零 / 恢复，**几何与槽位原样保留**。
    /// 恢复不需要任何额外的账——基色还在 <see cref="BaseColors"/> 里，重算一次即可。
    /// </summary>
    public int SetLeafVisible(int leaf, bool visible)
    {
        if ((uint)leaf >= (uint)_leafCount)
        {
            UiAssert.That(false, $"SetLeafVisible 收到越界叶下标 {leaf}");
            return 0;
        }
        IslandVisual v = _leaves[leaf].Visual;
        v.Visible = visible;
        return RecolorLeaf(leaf, in v);
    }

    /// <summary>
    /// 全量重算 color 场（增量正确性门的神谕半边）：按每个叶当前的 worldVisual 从基色重算一遍。
    /// 增量路径与它逐字节一致是不变量 9 的验证形态——mock 后端在 Color 排水后就是这么比的。
    /// </summary>
    public int RecolorAll()
    {
        int n = 0;
        for (int i = 0; i < _leafCount; i++) n += RecolorLeaf(i, _leaves[i].Visual);
        return n;
    }

    // ── 裁剪：inner rect 包含剪枝（不变量 7）───────────────────────────────

    /// <summary>
    /// 包含剪枝：AABB 完全落在所属裁剪域**内含矩形**里的实例，摘掉 clipIndex、不进 shader 采样。
    /// 返回被摘的实例数。P6 跑一遍；content 重写之后要重跑（新几何未必还在内含矩形里，
    /// 而重写时流会按叶的 clipEntry 重新盖上 clipIndex——剪枝结论不会被偷偷继承）。
    /// </summary>
    public int PruneContainedClips()
    {
        int pruned = 0;
        for (int li = 0; li < _leafCount; li++)
        {
            LeafRange r = _leaves[li];
            if (r.ClipEntry == ClipBook.NoneEntry || r.Count == 0) continue;

            int first = -1, last = -1;
            for (int i = 0; i < r.Count; i++)
            {
                int at = r.Start + i;
                ref QuadInstance q = ref _quads[at];
                if (q.ClipIndex == 0u) continue;
                if (!_clips.CanPrune((int)q.ClipIndex, in q.Rect, (int)q.SlotIndex)) continue;
                q.ClipIndex = 0u;
                _clips.CountPrune();
                pruned++;
                if (first < 0) first = at;
                last = at;
            }
            if (first >= 0) MarkQuadsDirty(r.Segment, first, last);
        }
        return pruned;
    }

    // ── 孤岛同步 ────────────────────────────────────────────────────────────

    /// <summary>在后端建出全部孤岛（接后端之后调；已建的跳过）。</summary>
    public void AttachIslands(IRenderBackend backend)
    {
        if (backend == null || _handle.IsNone) return;
        for (int i = 0; i < _islandCount; i++)
        {
            if (!_islands[i].Handle.IsNone) continue;
            IslandRecord r = _islands[i];
            _islands[i].Handle = backend.CreateIsland(_handle, new IslandDesc
            {
                Kind = r.Kind,
                RunBefore = r.RunBefore,
                Slot = r.Slot,
                ClipIndex = r.ClipEntry,
                Visual = r.Visual,
                RenderEveryN = r.RenderEveryN,
                DebugName = r.DebugName,
            });
        }
    }

    /// <summary>
    /// P7 收尾的孤岛同步：槽矩阵 / visual / clip 下发。
    /// <paramref name="stillAnimating"/> 是外部内容的自报——没有失效通道的内容不自报就没有跳帧资格
    /// （不变量 13）。
    /// </summary>
    public void SyncIsland(IRenderBackend backend, int island, in IslandVisual visual, bool stillAnimating)
    {
        if ((uint)island >= (uint)_islandCount)
        {
            UiAssert.That(false, $"SyncIsland 收到越界孤岛下标 {island}");
            return;
        }
        ref IslandRecord r = ref _islands[island];
        r.Visual = visual;
        r.StillAnimating = stillAnimating;
        if (backend == null || r.Handle.IsNone) return;
        backend.SyncIsland(r.Handle, new IslandSync
        {
            SlotMatrix = _slots.Matrix(r.Slot),
            Visual = visual,
            ClipIndex = r.ClipEntry,
            StillAnimating = stillAnimating,
        });
    }

    /// <summary>是否有孤岛自报仍在动（零脏帧短路的第三个前提）。</summary>
    public bool AnyIslandAnimating()
    {
        for (int i = 0; i < _islandCount; i++) if (_islands[i].StillAnimating) return true;
        return false;
    }

    // ── 后端接线与提交 ──────────────────────────────────────────────────────

    /// <summary>
    /// 接后端：建后端流并把整份镜像标脏（GPU 侧此刻是空的，本地每一条都得重发）。
    /// 建流在帧括号**外**也合法——流的生命周期跨帧。
    /// </summary>
    public StreamHandle Attach(IRenderBackend backend)
    {
        if (backend == null) { UiAssert.That(false, "Attach 收到 null 后端"); return StreamHandle.None; }
        UiAssert.That(_handle.IsNone, "重复 Attach（一条镜像同时只接一个后端流）");

        int cap = _quadCount > 0 ? _quadCount : 16;
        _handle = backend.CreateStream(StreamDesc.ForQuads(cap, _debugName));
        _fullUpload = true;
        _segmentsDirty = true;
        _epochSubmitted = false;
        _slots.MarkAllDirty();
        _clips.MarkAllDirty();
        return _handle;
    }

    /// <summary>拆后端（句柄入 fence 队列；此后对它的任何调用都是 use-after-free）。</summary>
    public void Detach(IRenderBackend backend)
    {
        if (backend == null || _handle.IsNone) return;
        for (int i = 0; i < _islandCount; i++)
        {
            if (_islands[i].Handle.IsNone) continue;
            backend.DestroyIsland(_islands[i].Handle);
            _islands[i].Handle = IslandHandle.None;
        }
        backend.DestroyStream(_handle);
        _handle = StreamHandle.None;
    }

    /// <summary>
    /// P8 提交：把本帧的脏账压成最小的一组后端调用。
    ///
    /// 四条纪律：
    ///  1. **每段一次上传**——段内脏区间合并后一次 <see cref="IRenderBackend.UploadInstances"/>；
    ///  2. **段表只在变了时下发**——「整表替换」说的是内容，不是频率；零脏帧不得有任何上传，
    ///     否则不变量 13 的探针（mock 在 EndFrame 核对「Dirty=false 却有上传」）必响；
    ///  3. **序派生当且仅当 structEpoch 变**（不变量 10）；
    ///  4. 降级阶梯事件在此转交后端——预算超限而无阶梯事件是被禁的那一种（不变量 5）。
    /// </summary>
    public SubmitReport Submit(IRenderBackend backend)
    {
        if (backend == null || _handle.IsNone)
        {
            UiAssert.That(false, "Submit 前必须先 Attach");
            return default;
        }

        int calls = 0, quads = 0, clipCount = 0, slotCount = 0;

        if (_fullUpload)
        {
            for (int i = 0; i < _segCount; i++)
            {
                SegmentDesc s = _segments[i];
                if (s.Count <= 0) continue;
                backend.UploadInstances(_handle, s.Start, new ReadOnlySpan<QuadInstance>(_quads, s.Start, s.Count));
                calls++;
                quads += s.Count;
                _segDirty[i] = new SegDirty { Min = int.MaxValue, Max = -1 };
            }
        }
        else
        {
            for (int i = 0; i < _segCount; i++)
            {
                SegDirty d = _segDirty[i];
                if (d.Max < d.Min) continue;
                int len = d.Max - d.Min + 1;
                backend.UploadInstances(_handle, d.Min, new ReadOnlySpan<QuadInstance>(_quads, d.Min, len));
                calls++;
                quads += len;
                _segDirty[i] = new SegDirty { Min = int.MaxValue, Max = -1 };
            }
        }

        if (_clips.HasDirty)
        {
            int first = _clips.DirtyMin;
            int len = _clips.DirtyMax - first + 1;
            backend.UploadClips(_handle, first, _clips.Entries.Slice(first, len));
            _clips.ClearDirty();
            calls++;
            clipCount = len;
        }

        if (_slots.HasDirty)
        {
            int first = _slots.DirtyMin;
            int len = _slots.DirtyMax - first + 1;
            backend.WriteSlots(_handle, first, _slots.Entries.Slice(first, len));
            _slots.ClearDirty();
            calls++;
            slotCount = len;
        }

        bool segments = false;
        if (_segmentsDirty)
        {
            backend.SetSegments(_handle, Segments);
            _segmentsDirty = false;
            segments = true;
            calls++;
        }

        bool orders = false;
        if (!_epochSubmitted || _submittedEpoch != _structEpoch)
        {
            backend.SetRunOrders(_handle, _structEpoch, BuildRunOrders());
            _submittedEpoch = _structEpoch;
            _epochSubmitted = true;
            orders = true;
            calls++;
        }

        int degrades = _degrade.Flush(backend);
        _fullUpload = false;
        return new SubmitReport(calls, quads, clipCount, slotCount, segments, orders, degrades);
    }

    /// <summary>
    /// 本帧的渲染账（架构接缝 <c>FrameStats</c>）。<c>Dirty</c> 的判据归本平面：
    /// 本帧真的提交了东西，或者有孤岛自报仍在动。后端不自己猜「这帧没变」。
    /// <c>UploadBytes</c> 只算有 ABI 尺寸常量的两项（实例 + 裁剪条目）——
    /// 槽数组与段属性块的字节口径各后端不同，那部分的账归后端自己记。
    /// </summary>
    public FrameStats BuildStats(ulong frameId, in SubmitReport report) => new FrameStats
    {
        FrameId = frameId,
        QuadCount = _quadCount,
        RunCount = _runCount,
        SegSwitches = _segCount,
        UploadBytes = report.Quads * Abi.QuadInstanceSize + report.Clips * Abi.ClipEntrySize,
        Recaptures = 0,
        IslandCount = _islandCount,
        Dirty = report.Calls > 0 || AnyIslandAnimating(),
    };

    /// <summary>
    /// 定格一份只读快照（六个数组全部拷贝）。与 <c>MockBackend.Snapshot</c> 的对拍是一条门：
    /// **后端收到的字节必须等于镜像持有的字节**。
    /// </summary>
    public StreamSnapshot Snapshot() => new StreamSnapshot(
        Quads, BaseColors, _clips.Entries, _slots.Entries, Segments, BuildRunOrders(), _structEpoch, _debugName);

    /// <summary>
    /// 派生序（= paintOrder 下标 × <see cref="Abi.PaintOrderStride"/>）。
    /// 每次现算：序号不是被维护的状态，是被派生的值——留一份缓存就是留一本会陈旧的第二账。
    /// </summary>
    public RunOrder[] BuildRunOrders()
    {
        if (_runCount == 0) return Array.Empty<RunOrder>();
        var orders = new RunOrder[_runCount];
        for (int i = 0; i < _runCount; i++) orders[i] = new RunOrder(i, _runs[i].SortingOrder);
        return orders;
    }

    /// <summary>
    /// 诊断拾取（架构接缝 <c>DebugPick(x,y) → LeafId/NodeId</c>）：CPU 扫实例流，
    /// 返回最上层命中的叶下标（-1 = 没命中）。SoA 表没有 GameObject 可以在 Hierarchy 里点，
    /// 这个函数就是那个替代品。
    /// </summary>
    /// <param name="x">流根空间 x。</param>
    /// <param name="y">流根空间 y。</param>
    /// <param name="quadIndex">命中的实例下标（-1 = 没命中）。</param>
    public int DebugPick(float x, float y, out int quadIndex)
    {
        quadIndex = -1;
        for (int i = _quadCount - 1; i >= 0; i--)
        {
            QuadInstance q = _quads[i];
            if (q.Rect.z <= 0f || q.Rect.w <= 0f) continue;
            if ((q.Color >> 24) == 0u) continue;              // 全透明的实例不该被点到

            var local = new Vector2(x, y);
            if (q.SlotIndex != 0u)
            {
                if (!_slots.Matrix((int)q.SlotIndex).TryInvert(out Affine2D inv)) continue;
                local = inv.TransformPoint(local);
            }
            if (local.x < q.Rect.x || local.y < q.Rect.y) continue;
            if (local.x >= q.Rect.x + q.Rect.z || local.y >= q.Rect.y + q.Rect.w) continue;

            quadIndex = i;
            for (int li = 0; li < _leafCount; li++)
            {
                LeafRange r = _leaves[li];
                if (i >= r.Start && i < r.Start + r.Count) return li;
            }
            return -1;
        }
        return -1;
    }

    // ── 内部 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 段选择：当前段同 blendClass 且装得下这张纹理就并入，否则起新段。
    /// <see cref="TexId.None"/>（纯色）也**占一个纹理槽**——<c>route.texSlot</c> 没有「不采样」的编码，
    /// 后端为它绑 1×1 白；给它一个假的共享槽会让纯色 quad 采到邻居的图集。
    /// </summary>
    private int SelectSegment(TexId tex, BlendClass blend, out int texSlot)
    {
        if (_currentSegment >= 0)
        {
            ref SegmentDesc cur = ref _segments[_currentSegment];
            if (cur.Blend == blend)
            {
                int slot = IndexOfOrAddTexture(ref cur, tex);
                if (slot >= 0)
                {
                    texSlot = slot;
                    return _currentSegment;
                }
            }
        }

        EnsureSegments(_segCount + 1);
        _segments[_segCount] = new SegmentDesc
        {
            Tex0 = tex,
            Tex1 = TexId.None,
            Tex2 = TexId.None,
            Tex3 = TexId.None,
            TexCount = 1,
            Blend = blend,
            Start = _quadCount,
            Count = 0,
            RunIndex = _currentRun < 0 ? 0 : _currentRun,
        };
        _segDirty[_segCount] = new SegDirty { Min = int.MaxValue, Max = -1 };
        _currentSegment = _segCount;
        _segCount++;
        if (_currentRun >= 0) _runs[_currentRun].SegmentCount = _segCount - _runs[_currentRun].FirstSegment;
        texSlot = 0;
        return _currentSegment;
    }

    /// <summary>纹理在段内的槽下标；不在且没空位返回 -1（= 该切段了）。</summary>
    private static int IndexOfOrAddTexture(ref SegmentDesc seg, TexId tex)
    {
        for (int i = 0; i < seg.TexCount; i++) if (seg.TexAt(i).Equals(tex)) return i;
        if (seg.TexCount >= Abi.SegmentMaxTextures) return -1;

        int at = seg.TexCount;
        switch (at)
        {
            case 0: seg.Tex0 = tex; break;
            case 1: seg.Tex1 = tex; break;
            case 2: seg.Tex2 = tex; break;
            default: seg.Tex3 = tex; break;
        }
        seg.TexCount = (byte)(at + 1);
        return at;
    }

    /// <summary>盖 route 三分量 + flags + 从基色算 color（三件事只有这一个写口）。</summary>
    private static void StampQuad(ref QuadInstance q, int slot, int clip, int texSlot,
        uint emitFlags, uint baseColor, in IslandVisual visual)
    {
        q.SlotIndex = (uint)slot;
        q.ClipIndex = (uint)clip;
        q.TexSlot = (uint)texSlot;
        q.Flags = ColorTier.WithGrayed(q.Flags | emitFlags, visual.Grayed);
        q.Color = ColorTier.Apply(baseColor, in visual);
    }

    /// <summary>预留实例数：请求了 slack 才向上取 2 的幂，否则严丝合缝等于实际长度。</summary>
    private static int SlackFor(int count, int hint)
    {
        if (hint <= 0) return count;
        int want = hint > count ? hint : count;
        int pow = 1;
        while (pow < want) pow <<= 1;
        return pow;
    }

    private void CloseCurrentRun()
    {
        if (_currentRun < 0) return;
        ref RunRecord r = ref _runs[_currentRun];
        r.SegmentCount = _segCount - r.FirstSegment;
        r.QuadCount = _quadCount - r.FirstQuad;
        _currentRun = -1;
        _currentSegment = -1;
    }

    private void MarkQuadsDirty(int segment, int from, int toInclusive)
    {
        if ((uint)segment >= (uint)_segCount || toInclusive < from) return;
        ref SegDirty d = ref _segDirty[segment];
        if (from < d.Min) d.Min = from;
        if (toInclusive > d.Max) d.Max = toInclusive;
    }

    private bool AnyQuadDirty()
    {
        for (int i = 0; i < _segCount; i++) if (_segDirty[i].Max >= _segDirty[i].Min) return true;
        return false;
    }

    private void EnsureQuads(int n)
    {
        if (_quads.Length >= n) return;
        int cap = _quads.Length == 0 ? 16 : _quads.Length;
        while (cap < n) cap *= 2;
        Array.Resize(ref _quads, cap);
        Array.Resize(ref _baseColor, cap);
    }

    private void EnsureLeaves(int n)
    {
        if (_leaves.Length >= n) return;
        int cap = _leaves.Length == 0 ? 8 : _leaves.Length;
        while (cap < n) cap *= 2;
        Array.Resize(ref _leaves, cap);
    }

    private void EnsureSegments(int n)
    {
        if (_segments.Length >= n) return;
        int cap = _segments.Length == 0 ? 4 : _segments.Length;
        while (cap < n) cap *= 2;
        Array.Resize(ref _segments, cap);
        Array.Resize(ref _segDirty, cap);
    }

    private void EnsureRuns(int n)
    {
        if (_runs.Length >= n) return;
        int cap = _runs.Length == 0 ? 4 : _runs.Length;
        while (cap < n) cap *= 2;
        Array.Resize(ref _runs, cap);
    }

    private void EnsureIslands(int n)
    {
        if (_islands.Length >= n) return;
        int cap = _islands.Length == 0 ? 2 : _islands.Length;
        while (cap < n) cap *= 2;
        Array.Resize(ref _islands, cap);
    }
}
