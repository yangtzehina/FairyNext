using System.Collections.Generic;
using FairyNext.Core;
using FairyNext.Core.Rendering;

namespace FairyNext.Backend.Mock;

/// <summary>后端调用种类（留痕的封闭词表；append-only——金样按数值解释留痕）。</summary>
public enum MockCallKind : byte
{
    /// <summary>未指定。</summary>
    None = 0,
    /// <summary>开帧括号。</summary>
    BeginFrame = 1,
    /// <summary>建流。</summary>
    CreateStream = 2,
    /// <summary>销毁流（入 fence 队列，不立即释放）。</summary>
    DestroyStream = 3,
    /// <summary>上传实例区间。</summary>
    UploadInstances = 4,
    /// <summary>上传裁剪条目区间。</summary>
    UploadClips = 5,
    /// <summary>写 transform 槽区间。</summary>
    WriteSlots = 6,
    /// <summary>整表替换段表。</summary>
    SetSegments = 7,
    /// <summary>下发派生序。</summary>
    SetRunOrders = 8,
    /// <summary>建孤岛。</summary>
    CreateIsland = 9,
    /// <summary>孤岛同步。</summary>
    SyncIsland = 10,
    /// <summary>拆孤岛。</summary>
    DestroyIsland = 11,
    /// <summary>开离屏 pass。</summary>
    BeginOffscreenPass = 12,
    /// <summary>关离屏 pass。</summary>
    EndOffscreenPass = 13,
    /// <summary>绑定主表面。</summary>
    BindMainSurface = 14,
    /// <summary>提交一条流。</summary>
    DrawStream = 15,
    /// <summary>上报降级。</summary>
    ReportDegrade = 16,
    /// <summary>收帧。</summary>
    EndFrame = 17,
}

/// <summary>
/// 一条后端调用的留痕。定长、无堆引用（<see cref="Detail"/> 除外）——
/// 留痕本身要能进金样与逐帧哈希，变长载荷会让「同输入同字节」失去意义。
/// </summary>
public readonly struct MockCall
{
    /// <summary>调用种类。</summary>
    public readonly MockCallKind Kind;
    /// <summary>发生时的帧号（帧括号外为 0）。</summary>
    public readonly ulong FrameId;
    /// <summary>相关流的池下标（无关调用为 0）。</summary>
    public readonly int Stream;
    /// <summary>区间首下标 / pass 下标 / 孤岛下标，按 <see cref="Kind"/> 复用。</summary>
    public readonly int First;
    /// <summary>元素个数，按 <see cref="Kind"/> 复用。</summary>
    public readonly int Count;
    /// <summary>人读细节（降级原因等；可空）。</summary>
    public readonly string? Detail;

    internal MockCall(MockCallKind kind, ulong frameId, int stream, int first, int count, string? detail)
    {
        Kind = kind; FrameId = frameId; Stream = stream; First = first; Count = count; Detail = detail;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"f{FrameId} {Kind} s#{Stream} [{First},{First + Count}){(Detail == null ? "" : " " + Detail)}";
}

/// <summary>一个离屏 pass 的留痕（提交序 = 数组序）。</summary>
public readonly struct MockPass
{
    /// <summary>pass 下标（1 起）。</summary>
    public readonly int Index;
    /// <summary>用途。</summary>
    public readonly PassKind Kind;
    /// <summary>目标 RT id。</summary>
    public readonly uint RtId;
    /// <summary>消费者 pass（0 = 主表面）。</summary>
    public readonly int Consumer;
    /// <summary>提交序（0 起，本帧内单调）。</summary>
    public readonly int SubmitOrder;
    /// <summary>是否已关闭。</summary>
    public readonly bool Ended;

    internal MockPass(int index, PassKind kind, uint rtId, int consumer, int submitOrder, bool ended)
    {
        Index = index; Kind = kind; RtId = rtId; Consumer = consumer; SubmitOrder = submitOrder; Ended = ended;
    }

    /// <inheritdoc/>
    public override string ToString() => $"pass#{Index} {Kind} rt={RtId} → {Consumer} @{SubmitOrder}";
}

/// <summary>
/// mock 后端（架构承诺 10 / 渲染平面机制 12）：**无 GPU 行为门的执法者**。
///
/// 三件事：
///  1. **全部调用留痕**——<see cref="Calls"/> 是有序的调用日志，行为测试断言「谁在什么时候被调了几次」；
///  2. **只读流快照**——<see cref="Snapshot"/> 定格 quads/baseColor/segments/runs/clips/slots，
///     供规范化哈希（<see cref="CanonicalStream"/>）与「增量 vs 全量」逐字节对拍；
///  3. **契约执法**——渲染平面不变量里凡写「mock 后端断言」的条款都落在本类。
///
/// 两级失败，语义不同、不要混：
///  - <see cref="Violations"/>：**协议破了**（use-after-free、pass 序、帧括号、axis-aligned 位不一致）。
///    这类在 debug 下同时走 <see cref="UiAssert"/>；但断言是 <c>[Conditional("DEBUG")]</c>，
///    所以本列表在 release 里照记——否则 release 跑的门等于没跑。
///  - <see cref="Gates"/>（<see cref="GateReport"/> 的后端半边）：**走了降级路径**。
///    画对了不等于走对了；任一计数非零 = 门不过。
///
/// 本类不是线程安全的，也不打算是：帧协议是单线程的。
/// </summary>
public sealed class MockBackend : IRenderBackend
{
    // ── 流 ──────────────────────────────────────────────────────────────────

    private sealed class MockStream
    {
        internal uint Gen;
        internal bool Alive;
        internal string? DebugName;
        internal ulong DestroyedFrame;
        internal uint StructEpoch;
        internal bool EpochSeen;

        internal QuadInstance[] Quads = Array.Empty<QuadInstance>();
        internal uint[] BaseColor = Array.Empty<uint>();
        internal ClipEntry[] Clips = Array.Empty<ClipEntry>();
        internal SlotEntry[] Slots = Array.Empty<SlotEntry>();
        internal SegmentDesc[] Segments = Array.Empty<SegmentDesc>();
        internal RunOrder[] Runs = Array.Empty<RunOrder>();

        internal int QuadCount;
        internal int ClipCount;
        internal int SlotCount;
        internal bool BaseColorWritten;
    }

    private sealed class MockIsland
    {
        internal uint Gen;
        internal bool Alive;
        internal int Stream;
        internal IslandDesc Desc;
        internal IslandSync Sync;
    }

    private readonly List<MockStream> _streams = new List<MockStream>();
    private readonly List<MockIsland> _islands = new List<MockIsland>();
    private readonly List<MockCall> _calls = new List<MockCall>();
    private readonly List<string> _violations = new List<string>();
    private readonly List<MockPass> _passes = new List<MockPass>();
    private readonly List<int> _openPasses = new List<int>();
    private readonly List<(int Stream, ulong Frame)> _fencePending = new List<(int, ulong)>();

    private bool _inFrame;
    private ulong _frameId;
    private bool _mainBound;
    private int _drawsThisFrame;
    private int _uploadsThisFrame;
    private int _uploadBytesThisFrame;
    private int _degradesThisFrame;
    private bool _budgetOverflowThisFrame;

    /// <summary>
    /// 「序重推超预算」的诊断阈值（架构机制 2：单元典型 &lt;500，重推只是几百次写）。
    /// 它是**诊断阈值不是 ABI 预算**——order-maintenance 结构要不要引入，等的就是这条计数出实据。
    /// </summary>
    public int RelabelBudget { get; set; } = 512;

    /// <summary>
    /// 相位探针（可选）。装上后，帧括号内的每次后端调用都会核对相位属于 P7/P8；
    /// 不属于即计一次 <see cref="GateReport.PhaseViolation"/>。M1-14 接线后由内核提供。
    /// </summary>
    public Func<FramePhase>? PhaseProbe { get; set; }

    // ── IRenderBackend：身份与能力 ──────────────────────────────────────────

    /// <inheritdoc/>
    public string Name => "mock";

    /// <inheritdoc/>
    public int ShaderAbiVersion => AbiMock.ShaderAbiVersion;

    /// <inheritdoc/>
    public BackendCaps Caps => new BackendCaps
    {
        SupportsOffscreen = true,
        SupportsFence = true,
        FenceQueueDepth = AbiMock.GpuFenceDepth,
        SelfDrawn = true,           // 自绘：零脏帧短路与 present 收据对 mock 适用
        MaxTextureSlots = AbiMock.SegmentMaxTextures,
    };

    // ── 只读面：留痕、快照、计数 ────────────────────────────────────────────

    /// <summary>调用留痕（有序）。</summary>
    public IReadOnlyList<MockCall> Calls => _calls;

    /// <summary>协议违约记录（release 下照记；空 = 没破协议）。</summary>
    public IReadOnlyList<string> Violations => _violations;

    /// <summary>本帧（或最近一帧）的离屏 pass 留痕，按提交序。</summary>
    public IReadOnlyList<MockPass> Passes => _passes;

    /// <summary>累计 tick 数（EndFrame 次数）。</summary>
    public ulong Ticks { get; private set; }

    /// <summary>累计 present 数（零脏帧短路生效时 &lt; <see cref="Ticks"/>）。</summary>
    public ulong Presents { get; private set; }

    /// <summary>累计上传字节数（Buffer 后端立项的证据字段之一）。</summary>
    public long UploadBytes { get; private set; }

    /// <summary>
    /// 同值槽写次数（不变量 16 的收据）。调用方本应在写前做位等比较，
    /// 这条计数非零 = 上游少做了一次切断，零脏帧短路的前提被削弱。
    /// </summary>
    public int RedundantSlotWrites { get; private set; }

    /// <summary>当前 fence pending 队列深度。</summary>
    public int FencePendingDepth => _fencePending.Count;

    /// <summary>门计数（后端半边；完整的七字段门见 <see cref="GateReport"/>）。</summary>
    public GateReport Gates => _gates;

    private GateReport _gates;

    /// <summary>某类调用的次数。</summary>
    public int CallCount(MockCallKind kind)
    {
        int n = 0;
        for (int i = 0; i < _calls.Count; i++) if (_calls[i].Kind == kind) n++;
        return n;
    }

    /// <summary>某类调用的最后一条（没有则返回 default）。</summary>
    public MockCall LastCall(MockCallKind kind)
    {
        for (int i = _calls.Count - 1; i >= 0; i--) if (_calls[i].Kind == kind) return _calls[i];
        return default;
    }

    /// <summary>清空留痕与违约记录（门计数与收据不清——它们是会话量）。</summary>
    public void ClearLog()
    {
        _calls.Clear();
        _violations.Clear();
    }

    /// <summary>
    /// 定格一条流的只读快照（六个数组全部拷贝）。
    /// 无效句柄返回 <see cref="StreamSnapshot.Empty"/> 并记一次违约——
    /// 「快照悄悄给了空流」是最难查的一类测试假绿。
    /// </summary>
    public StreamSnapshot Snapshot(StreamHandle handle)
    {
        MockStream? s = Resolve(handle, "Snapshot");
        if (s == null) return StreamSnapshot.Empty;
        // 没写过 baseColor 就报「没有这份账」，而不是报一串零：HasBaseColor 要能被信。
        ReadOnlySpan<uint> baseColor = s.BaseColorWritten
            ? new ReadOnlySpan<uint>(s.BaseColor, 0, Math.Min(s.BaseColor.Length, s.QuadCount))
            : ReadOnlySpan<uint>.Empty;
        return new StreamSnapshot(
            new ReadOnlySpan<QuadInstance>(s.Quads, 0, s.QuadCount),
            baseColor,
            new ReadOnlySpan<ClipEntry>(s.Clips, 0, s.ClipCount),
            new ReadOnlySpan<SlotEntry>(s.Slots, 0, s.SlotCount),
            s.Segments,
            s.Runs,
            s.StructEpoch,
            s.DebugName);
    }

    /// <summary>
    /// 写 CPU 镜像基色（**mock 专有的侧信道，不在 <see cref="IRenderBackend"/> 上**）。
    /// baseColor 是未乘 α 的基色，永不上传 GPU；但流快照要带它，不变量 9
    /// （GPU color 场 ≡ f(baseColor, worldVisual)）才能在快照层面验。
    /// M1-11 的 RenderStream 自己持有这份账时，这个侧信道就只剩下「让 mock 侧快照完整」这一个用途。
    /// </summary>
    public void SetBaseColors(StreamHandle handle, int firstQuad, ReadOnlySpan<uint> baseColor)
    {
        MockStream? s = Resolve(handle, nameof(SetBaseColors));
        if (s == null) return;
        EnsureUInt(ref s.BaseColor, firstQuad + baseColor.Length);
        baseColor.CopyTo(new Span<uint>(s.BaseColor, firstQuad, baseColor.Length));
        s.BaseColorWritten = true;
    }

    // ── 帧括号 ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void BeginFrame(ulong frameId)
    {
        if (_inFrame) Violate($"BeginFrame 重入（f{_frameId} 未收帧就开 f{frameId}）");
        _inFrame = true;
        _frameId = frameId;
        _mainBound = false;
        _drawsThisFrame = 0;
        _uploadsThisFrame = 0;
        _uploadBytesThisFrame = 0;
        _degradesThisFrame = 0;
        _budgetOverflowThisFrame = false;
        _passes.Clear();
        _openPasses.Clear();
        ReleaseFencedBuffers(frameId);
        Log(MockCallKind.BeginFrame, 0, 0, 0, null);
    }

    /// <inheritdoc/>
    public FrameReceipt EndFrame(in FrameStats stats)
    {
        CheckPhase(nameof(EndFrame));
        if (!_inFrame) Violate("EndFrame 于帧括号外");
        if (_openPasses.Count != 0) Violate($"EndFrame 时仍有 {_openPasses.Count} 个离屏 pass 未关");

        // 不变量 5：预算超限而无阶梯事件 = 失败。降级可以有，静默不可以。
        if (_budgetOverflowThisFrame && _degradesThisFrame == 0)
            Violate("预算超限（槽/clip）却没有任何降级阶梯事件——静默降级是被禁的那一种");

        // 不变量 13 的探针半边：零脏帧不该有任何上传。
        if (!stats.Dirty && _uploadsThisFrame > 0)
            Violate($"stats.Dirty=false 却发生了 {_uploadsThisFrame} 次上传——零脏帧判据与实际排水不一致");

        Ticks++;
        bool presented = stats.Dirty && _drawsThisFrame > 0;
        if (presented) Presents++;

        Log(MockCallKind.EndFrame, 0, _drawsThisFrame, _uploadBytesThisFrame,
            presented ? "present" : "skip");
        var receipt = new FrameReceipt(stats.FrameId != 0 ? stats.FrameId : _frameId,
            presented, Ticks, Presents, _uploadBytesThisFrame);
        _inFrame = false;
        return receipt;
    }

    // ── 流生命周期 + fence 回收 ─────────────────────────────────────────────

    /// <inheritdoc/>
    public StreamHandle CreateStream(in StreamDesc desc)
    {
        CheckPhase(nameof(CreateStream));
        int slot = -1;
        for (int i = 0; i < _streams.Count; i++)
        {
            if (!_streams[i].Alive && !IsFenced(i + 1)) { slot = i; break; }
        }
        if (slot < 0)
        {
            _streams.Add(new MockStream());
            slot = _streams.Count - 1;
        }

        MockStream s = _streams[slot];
        s.Gen++;
        s.Alive = true;
        s.DebugName = desc.DebugName;
        s.StructEpoch = 0;
        s.EpochSeen = false;
        s.QuadCount = 0;
        s.ClipCount = 0;
        s.SlotCount = 0;
        s.Quads = new QuadInstance[Math.Max(0, desc.QuadCapacity)];
        s.BaseColor = new uint[Math.Max(0, desc.QuadCapacity)];
        s.Clips = new ClipEntry[Math.Max(0, desc.ClipCapacity)];
        s.Slots = new SlotEntry[Math.Max(0, desc.SlotCapacity)];
        s.Segments = Array.Empty<SegmentDesc>();
        s.Runs = Array.Empty<RunOrder>();

        var handle = new StreamHandle(slot + 1, s.Gen);
        Log(MockCallKind.CreateStream, handle.Index, desc.QuadCapacity, desc.SlotCapacity, desc.DebugName);
        return handle;
    }

    /// <inheritdoc/>
    public void DestroyStream(StreamHandle stream)
    {
        CheckPhase(nameof(DestroyStream));
        MockStream? s = Resolve(stream, nameof(DestroyStream));
        if (s == null) return;
        s.Alive = false;
        s.DestroyedFrame = _frameId;
        _fencePending.Add((stream.Index, _frameId));
        if (_fencePending.Count > AbiMock.GpuFenceDepth)
        {
            _gates.FencePending++;   // 队列超深：GPU 可能还在读，回收纪律已经绷不住
        }
        Log(MockCallKind.DestroyStream, stream.Index, _fencePending.Count, 0, null);
    }

    /// <summary>
    /// fence 到期释放（帧首）：<see cref="AbiMock.GpuFenceDepth"/> 帧前入队的缓冲此刻才真释放。
    /// 「真释放」在 mock 里 = 允许该池下标被 <see cref="CreateStream"/> 复用。
    /// </summary>
    private void ReleaseFencedBuffers(ulong frameId)
    {
        for (int i = _fencePending.Count - 1; i >= 0; i--)
        {
            if (frameId >= _fencePending[i].Frame + (ulong)AbiMock.GpuFenceDepth)
                _fencePending.RemoveAt(i);
        }
    }

    private bool IsFenced(int streamIndex)
    {
        for (int i = 0; i < _fencePending.Count; i++) if (_fencePending[i].Stream == streamIndex) return true;
        return false;
    }

    // ── 上传 ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void UploadInstances(StreamHandle stream, int firstQuad, ReadOnlySpan<QuadInstance> quads)
    {
        CheckPhase(nameof(UploadInstances));
        RequireFrame(nameof(UploadInstances));
        MockStream? s = Resolve(stream, nameof(UploadInstances));
        if (s == null) return;
        if (firstQuad < 0) { Violate($"UploadInstances 起点为负（{firstQuad}）"); return; }

        // 保留位必须写零（ABI append-only 纪律的运行期半边）：今天的垃圾位就是明天新位域的
        // 「已经有人在用」——留到位域扩容那天再发现，代价是全部金样重录。
        for (int i = 0; i < quads.Length; i++)
        {
            if (AbiMock.RouteReserved(quads[i].Route) != 0)
                Violate($"quad {firstQuad + i} 的 route 保留位非零（0x{quads[i].Route:X8}）");
            if (AbiMock.FlagsReservedLow(quads[i].Flags) != 0 || AbiMock.FlagsReservedHigh(quads[i].Flags) != 0)
                Violate($"quad {firstQuad + i} 的 flags 保留位非零（0x{quads[i].Flags:X8}）");
        }

        EnsureQuads(s, firstQuad + quads.Length);
        quads.CopyTo(new Span<QuadInstance>(s.Quads, firstQuad, quads.Length));
        if (firstQuad + quads.Length > s.QuadCount) s.QuadCount = firstQuad + quads.Length;

        CountUpload(quads.Length * AbiMock.QuadInstanceSize);
        Log(MockCallKind.UploadInstances, stream.Index, firstQuad, quads.Length, null);
    }

    /// <inheritdoc/>
    public void UploadClips(StreamHandle stream, int firstEntry, ReadOnlySpan<ClipEntry> entries)
    {
        CheckPhase(nameof(UploadClips));
        RequireFrame(nameof(UploadClips));
        MockStream? s = Resolve(stream, nameof(UploadClips));
        if (s == null) return;
        if (firstEntry < 0) { Violate($"UploadClips 起点为负（{firstEntry}）"); return; }

        EnsureClips(s, firstEntry + entries.Length);
        entries.CopyTo(new Span<ClipEntry>(s.Clips, firstEntry, entries.Length));
        if (firstEntry + entries.Length > s.ClipCount) s.ClipCount = firstEntry + entries.Length;

        // 不变量 5：条目数按「裁剪域数」计，超预算必须伴随明文阶梯事件（EndFrame 处核对）。
        if (s.ClipCount > AbiMock.ClipEntryBudget)
        {
            _gates.ClipOverflow++;
            _budgetOverflowThisFrame = true;
        }

        CountUpload(entries.Length * AbiMock.ClipEntrySize);
        Log(MockCallKind.UploadClips, stream.Index, firstEntry, entries.Length, null);
    }

    /// <inheritdoc/>
    public void WriteSlots(StreamHandle stream, int firstSlot, ReadOnlySpan<SlotEntry> slots)
    {
        CheckPhase(nameof(WriteSlots));
        RequireFrame(nameof(WriteSlots));
        MockStream? s = Resolve(stream, nameof(WriteSlots));
        if (s == null) return;
        if (firstSlot < 0) { Violate($"WriteSlots 起点为负（{firstSlot}）"); return; }

        EnsureSlots(s, firstSlot + slots.Length);
        for (int i = 0; i < slots.Length; i++)
        {
            int index = firstSlot + i;
            SlotEntry entry = slots[i];

            // 不变量 6：axis-aligned 位在**每次写**时重验，不是建槽时验一次。
            if ((entry.Flags & TransformSlotFlags.AxisAligned) != 0 && !entry.MatrixIsAxisAligned)
                Violate($"槽 {index} 声明 axisAligned 但矩阵含旋转/斜切（m01={entry.M.m01} m10={entry.M.m10}）");

            // 不变量 16：同值（位等）写不该发生——上游少做一次切断，零脏帧短路就少一分前提。
            if (index < s.SlotCount && s.Slots[index].MatrixEquals(in entry)
                && s.Slots[index].Flags == entry.Flags)
            {
                RedundantSlotWrites++;
            }

            // 预算：槽荒必须走阶梯（DegradeKind.SlotStarvation），不许静默。
            if (index >= AbiMock.TransformSlotBudget)
            {
                _gates.SlotOverflow++;
                _budgetOverflowThisFrame = true;
            }

            s.Slots[index] = entry;
            if (index + 1 > s.SlotCount) s.SlotCount = index + 1;
        }

        // 槽矩阵按 GLES uniform 数组的 3 vec4/槽计费（架构预算口径），不按 C# 结构体大小。
        CountUpload(slots.Length * 48);
        Log(MockCallKind.WriteSlots, stream.Index, firstSlot, slots.Length, null);
    }

    /// <inheritdoc/>
    public void SetSegments(StreamHandle stream, ReadOnlySpan<SegmentDesc> segments)
    {
        CheckPhase(nameof(SetSegments));
        RequireFrame(nameof(SetSegments));
        MockStream? s = Resolve(stream, nameof(SetSegments));
        if (s == null) return;

        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].TexCount > AbiMock.SegmentMaxTextures)
                Violate($"段 {i} 声明 {segments[i].TexCount} 张纹理，超过段键上限 {AbiMock.SegmentMaxTextures}");
        }

        s.Segments = segments.ToArray();
        CountUpload(segments.Length * 32);   // 段属性块的名义大小（真实后端各异，账口径统一在此）
        Log(MockCallKind.SetSegments, stream.Index, 0, segments.Length, null);
    }

    /// <inheritdoc/>
    public void SetRunOrders(StreamHandle stream, uint structEpoch, ReadOnlySpan<RunOrder> runs)
    {
        CheckPhase(nameof(SetRunOrders));
        RequireFrame(nameof(SetRunOrders));
        MockStream? s = Resolve(stream, nameof(SetRunOrders));
        if (s == null) return;

        // 不变量 10：序派生重推**当且仅当** structEpoch 变。epoch 没变还重推 = 两本账的前兆。
        if (s.EpochSeen && structEpoch == s.StructEpoch && runs.Length > 0)
            Violate($"structEpoch 未变（{structEpoch}）却重推了 {runs.Length} 条序——序是派生量，不独立维护");

        // 不变量 10 的第二半：重推后单元序必须与 run 下标严格同序。
        for (int i = 1; i < runs.Length; i++)
        {
            if (runs[i].RunIndex <= runs[i - 1].RunIndex)
                Violate($"run 序不单调：#{runs[i - 1].RunIndex} 之后是 #{runs[i].RunIndex}");
            else if (runs[i].SortingOrder <= runs[i - 1].SortingOrder)
                Violate($"sortingOrder 不严格递增：{runs[i - 1].SortingOrder} → {runs[i].SortingOrder}");
        }

        if (runs.Length > RelabelBudget) _gates.RelabelOverBudget++;

        s.Runs = runs.ToArray();
        s.StructEpoch = structEpoch;
        s.EpochSeen = true;
        Log(MockCallKind.SetRunOrders, stream.Index, (int)structEpoch, runs.Length, null);
    }

    // ── 孤岛 ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public IslandHandle CreateIsland(StreamHandle stream, in IslandDesc desc)
    {
        CheckPhase(nameof(CreateIsland));
        MockStream? s = Resolve(stream, nameof(CreateIsland));
        if (s == null) return IslandHandle.None;
        if (desc.Kind == IslandKind.None) Violate("CreateIsland 收到 IslandKind.None（孤岛清单是封闭枚举）");

        int slot = -1;
        for (int i = 0; i < _islands.Count; i++) if (!_islands[i].Alive) { slot = i; break; }
        if (slot < 0) { _islands.Add(new MockIsland()); slot = _islands.Count - 1; }

        MockIsland island = _islands[slot];
        island.Gen++;
        island.Alive = true;
        island.Stream = stream.Index;
        island.Desc = desc;
        island.Sync = default;

        var handle = new IslandHandle(slot + 1, island.Gen);
        Log(MockCallKind.CreateIsland, stream.Index, handle.Index, (int)desc.Kind, desc.DebugName);
        return handle;
    }

    /// <inheritdoc/>
    public void SyncIsland(IslandHandle island, in IslandSync sync)
    {
        CheckPhase(nameof(SyncIsland));
        RequireFrame(nameof(SyncIsland));
        MockIsland? i = ResolveIsland(island, nameof(SyncIsland));
        if (i == null) return;
        i.Sync = sync;
        Log(MockCallKind.SyncIsland, i.Stream, island.Index, sync.StillAnimating ? 1 : 0, null);
    }

    /// <inheritdoc/>
    public void DestroyIsland(IslandHandle island)
    {
        CheckPhase(nameof(DestroyIsland));
        MockIsland? i = ResolveIsland(island, nameof(DestroyIsland));
        if (i == null) return;
        i.Alive = false;
        Log(MockCallKind.DestroyIsland, i.Stream, island.Index, 0, null);
    }

    /// <summary>某孤岛最近一次同步的内容（诊断/测试）。</summary>
    public IslandSync IslandSyncOf(IslandHandle island)
    {
        MockIsland? i = ResolveIsland(island, nameof(IslandSyncOf));
        return i == null ? default : i.Sync;
    }

    /// <summary>是否所有活孤岛都自报静止（零脏帧短路的第三个前提，不变量 13）。</summary>
    public bool AllIslandsStill()
    {
        for (int i = 0; i < _islands.Count; i++)
            if (_islands[i].Alive && _islands[i].Sync.StillAnimating) return false;
        return true;
    }

    // ── pass 序（v1.3 时序契约，不变量 14）─────────────────────────────────

    /// <inheritdoc/>
    public PassHandle BeginOffscreenPass(in OffscreenPassDesc desc)
    {
        CheckPhase(nameof(BeginOffscreenPass));
        RequireFrame(nameof(BeginOffscreenPass));

        // v1.3 条款：全部离屏 pass 必须先于主表面绑定。
        // 违约计进 phaseViolation——它与 P7/P8 违约写是同一类错误：在错的时刻做对的事。
        if (_mainBound)
        {
            _gates.PhaseViolation++;
            Violate($"离屏 pass（{desc.Kind} rt={desc.RtId}）晚于主表面首次绑定"
                  + "——tiled GPU 上这是一次整块 tile 的 store/load flush");
        }

        // 依赖序：被消费者必须先于消费者提交。消费者已经提交过 = 内层晚于外层。
        if (!desc.Consumer.IsMainSurface)
        {
            bool consumerSubmitted = false;
            for (int i = 0; i < _passes.Count; i++)
                if (_passes[i].Index == desc.Consumer.Index) { consumerSubmitted = true; break; }
            if (consumerSubmitted)
                Violate($"capture 依赖序反了：pass#{desc.Consumer.Index} 已提交，其被消费者才刚开");
        }

        int index = _passes.Count + 1;
        _passes.Add(new MockPass(index, desc.Kind, desc.RtId, desc.Consumer.Index, _passes.Count, false));
        _openPasses.Add(index);
        Log(MockCallKind.BeginOffscreenPass, 0, index, desc.Consumer.Index, desc.DebugName);
        return new PassHandle(index);
    }

    /// <inheritdoc/>
    public void EndOffscreenPass(PassHandle pass)
    {
        CheckPhase(nameof(EndOffscreenPass));
        if (_openPasses.Count == 0) { Violate($"EndOffscreenPass(pass#{pass.Index}) 但没有打开的 pass"); return; }
        int top = _openPasses[_openPasses.Count - 1];
        if (top != pass.Index)
        {
            Violate($"离屏 pass 未按后进先出关闭：栈顶 pass#{top}，收到 pass#{pass.Index}");
            return;
        }
        _openPasses.RemoveAt(_openPasses.Count - 1);
        MockPass p = _passes[pass.Index - 1];
        _passes[pass.Index - 1] = new MockPass(p.Index, p.Kind, p.RtId, p.Consumer, p.SubmitOrder, true);
        Log(MockCallKind.EndOffscreenPass, 0, pass.Index, 0, null);
    }

    /// <inheritdoc/>
    public void BindMainSurface(in SurfaceDesc surface)
    {
        CheckPhase(nameof(BindMainSurface));
        RequireFrame(nameof(BindMainSurface));
        if (_mainBound) Violate("主表面重复绑定（一帧只绑一次）");
        if (_openPasses.Count != 0) Violate($"绑定主表面时仍有 {_openPasses.Count} 个离屏 pass 未关");
        _mainBound = true;
        Log(MockCallKind.BindMainSurface, 0, surface.Width, surface.Height, null);
    }

    /// <inheritdoc/>
    public void DrawStream(StreamHandle stream, PassHandle target)
    {
        CheckPhase(nameof(DrawStream));
        RequireFrame(nameof(DrawStream));
        MockStream? s = Resolve(stream, nameof(DrawStream));
        if (s == null) return;

        if (target.IsMainSurface)
        {
            if (!_mainBound) Violate("向主表面提交但主表面尚未绑定");
        }
        else
        {
            bool open = false;
            for (int i = 0; i < _openPasses.Count; i++) if (_openPasses[i] == target.Index) { open = true; break; }
            if (!open) Violate($"向 pass#{target.Index} 提交，但它不在打开状态");
        }

        _drawsThisFrame++;
        Log(MockCallKind.DrawStream, stream.Index, target.Index, s.QuadCount, null);
    }

    // ── 降级阶梯 ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void ReportDegrade(DegradeKind kind, string detail)
    {
        if (kind == DegradeKind.None) { Violate("ReportDegrade 收到 DegradeKind.None"); return; }
        _gates.Degrade++;
        _degradesThisFrame++;
        Log(MockCallKind.ReportDegrade, 0, (int)kind, 1, detail);
    }

    // ── 内部 ────────────────────────────────────────────────────────────────

    private void Log(MockCallKind kind, int stream, int first, int count, string? detail) =>
        _calls.Add(new MockCall(kind, _frameId, stream, first, count, detail));

    private void CountUpload(int bytes)
    {
        _uploadsThisFrame++;
        _uploadBytesThisFrame += bytes;
        UploadBytes += bytes;
    }

    private void RequireFrame(string op)
    {
        if (!_inFrame) Violate($"{op} 于帧括号外（BeginFrame/EndFrame 之间才准提交）");
    }

    /// <summary>相位探针核对：帧括号内的后端调用只该发生在 P7/P8。</summary>
    private void CheckPhase(string op)
    {
        Func<FramePhase>? probe = PhaseProbe;
        if (probe == null || !_inFrame) return;
        FramePhase phase = probe();
        if (phase == FramePhase.P7_RenderDrain || phase == FramePhase.P8_Submit) return;
        _gates.PhaseViolation++;
        Violate($"{op} 发生在 {phase}——后端提交只在 P7/P8");
    }

    private MockStream? Resolve(StreamHandle handle, string op)
    {
        if (handle.IsNone || handle.Index > _streams.Count)
        {
            Violate($"{op} 收到无效流句柄 {handle}");
            return null;
        }
        MockStream s = _streams[handle.Index - 1];
        if (s.Gen != handle.Gen)
        {
            Violate($"{op} 收到陈旧流句柄 {handle}（当前代 {s.Gen}）");
            return null;
        }
        if (!s.Alive)
        {
            // 不变量 11：pending 中的缓冲收到 upload/draw = use-after-free，
            // 在无 GPU 的测试里就该暴露，而不是留给真机上的随机花屏。
            Violate($"{op} 于已销毁的流 {handle}（f{s.DestroyedFrame} 入 fence 队列，GPU 可能仍在读）");
            return null;
        }
        return s;
    }

    private MockIsland? ResolveIsland(IslandHandle handle, string op)
    {
        if (handle.IsNone || handle.Index > _islands.Count)
        {
            Violate($"{op} 收到无效孤岛句柄 {handle}");
            return null;
        }
        MockIsland i = _islands[handle.Index - 1];
        if (i.Gen != handle.Gen || !i.Alive)
        {
            Violate($"{op} 收到陈旧孤岛句柄 {handle}");
            return null;
        }
        return i;
    }

    /// <summary>
    /// 记一次协议违约。debug 下同时走 <see cref="UiAssert"/>（响亮失败），
    /// release 下只进列表——<see cref="UiAssert.That"/> 是 <c>[Conditional("DEBUG")]</c>，
    /// 不留一份无条件记录的话，release 配置下跑的门就是空跑。
    /// </summary>
    private void Violate(string message)
    {
        _violations.Add(message);
        UiAssert.That(false, "mock 后端：" + message);
    }

    private static void EnsureQuads(MockStream s, int n)
    {
        if (s.Quads.Length >= n) return;
        int cap = s.Quads.Length == 0 ? Math.Max(8, n) : s.Quads.Length;
        while (cap < n) cap *= 2;
        Array.Resize(ref s.Quads, cap);
        EnsureUInt(ref s.BaseColor, cap);
    }

    private static void EnsureClips(MockStream s, int n)
    {
        if (s.Clips.Length >= n) return;
        int cap = s.Clips.Length == 0 ? Math.Max(4, n) : s.Clips.Length;
        while (cap < n) cap *= 2;
        Array.Resize(ref s.Clips, cap);
    }

    private static void EnsureSlots(MockStream s, int n)
    {
        if (s.Slots.Length >= n) return;
        int cap = s.Slots.Length == 0 ? Math.Max(4, n) : s.Slots.Length;
        while (cap < n) cap *= 2;
        Array.Resize(ref s.Slots, cap);
    }

    private static void EnsureUInt(ref uint[] array, int n)
    {
        if (array.Length >= n) return;
        int cap = array.Length == 0 ? Math.Max(8, n) : array.Length;
        while (cap < n) cap *= 2;
        Array.Resize(ref array, cap);
    }
}
