namespace FairyNext.Core.Rendering;

/// <summary>
/// 实例流的**只读快照**（架构接缝：渲染平面向验证平面暴露的那份视图）。
///
/// 六个数组一次拷贝定格：quads / baseColor / segments / runs / clips / slots。
/// 拷贝而非引用是本类型的全部意义——三条门都要「同一时刻的两份流」互相比：
///  - 增量正确性门（不变量 1）：增量消化 vs 从树全量重建，逐字节比；
///  - Trace 逐帧哈希（M1-24）：每帧一个规范化哈希，比首分歧帧；
///  - 参考光栅（M1-10）：快照 → RGBA8 位图 → 像素门。
/// 引用视图在第一次「排水完再看」的用例里就会读到已被覆盖的缓冲。
///
/// 两个产出方：<c>MockBackend.Snapshot</c>（后端收到了什么）与 M1-11 的 <c>RenderStream</c>
/// （CPU 镜像是什么）。两份快照的比对本身就是一条门——后端收到的与镜像持有的必须一致。
///
/// <c>baseColor</c> 在后端侧通常为空：它是 CPU 镜像专有的**未乘 α 基色**，永不上传 GPU。
/// 带上它是为了不变量 9（颜色纯函数：GPU color 场 ≡ f(baseColor, worldVisual)）在快照层面可验。
/// </summary>
public sealed class StreamSnapshot
{
    private readonly QuadInstance[] _quads;
    private readonly uint[] _baseColor;
    private readonly ClipEntry[] _clips;
    private readonly SlotEntry[] _slots;
    private readonly SegmentDesc[] _segments;
    private readonly RunOrder[] _runs;

    /// <summary>定格一份快照（六个数组全部拷贝）。</summary>
    /// <param name="quads">实例流。</param>
    /// <param name="baseColor">未乘 α 基色（可空——后端侧没有这份账）。</param>
    /// <param name="clips">裁剪条目表（条目 0 = None 哨兵）。</param>
    /// <param name="slots">transform 槽表（槽 0 = identity）。</param>
    /// <param name="segments">段表。</param>
    /// <param name="runs">run 派生序。</param>
    /// <param name="structEpoch">结构代（序派生的唯一许可证）。</param>
    /// <param name="debugName">诊断名。</param>
    public StreamSnapshot(
        ReadOnlySpan<QuadInstance> quads,
        ReadOnlySpan<uint> baseColor,
        ReadOnlySpan<ClipEntry> clips,
        ReadOnlySpan<SlotEntry> slots,
        ReadOnlySpan<SegmentDesc> segments,
        ReadOnlySpan<RunOrder> runs,
        uint structEpoch = 0,
        string? debugName = null)
    {
        _quads = quads.ToArray();
        _baseColor = baseColor.ToArray();
        _clips = clips.ToArray();
        _slots = slots.ToArray();
        _segments = segments.ToArray();
        _runs = runs.ToArray();
        StructEpoch = structEpoch;
        DebugName = debugName;
    }

    /// <summary>空快照（零 quad 的流也是合法状态——零脏帧的第一帧就长这样）。</summary>
    public static readonly StreamSnapshot Empty = new StreamSnapshot(
        ReadOnlySpan<QuadInstance>.Empty, ReadOnlySpan<uint>.Empty, ReadOnlySpan<ClipEntry>.Empty,
        ReadOnlySpan<SlotEntry>.Empty, ReadOnlySpan<SegmentDesc>.Empty, ReadOnlySpan<RunOrder>.Empty);

    /// <summary>实例流（绘制序 = 数组序）。</summary>
    public ReadOnlySpan<QuadInstance> Quads => _quads;

    /// <summary>未乘 α 基色（长度 0 = 本快照来自后端侧，没有这份账）。</summary>
    public ReadOnlySpan<uint> BaseColor => _baseColor;

    /// <summary>裁剪条目表。</summary>
    public ReadOnlySpan<ClipEntry> Clips => _clips;

    /// <summary>transform 槽表。</summary>
    public ReadOnlySpan<SlotEntry> Slots => _slots;

    /// <summary>段表。</summary>
    public ReadOnlySpan<SegmentDesc> Segments => _segments;

    /// <summary>run 派生序。</summary>
    public ReadOnlySpan<RunOrder> Runs => _runs;

    /// <summary>结构代（<see cref="IRenderBackend.SetRunOrders"/> 的许可证）。</summary>
    public uint StructEpoch { get; }

    /// <summary>诊断名（**不进规范化哈希**：名字不是像素）。</summary>
    public string? DebugName { get; }

    /// <summary>实例数。</summary>
    public int QuadCount => _quads.Length;

    /// <summary>是否带 baseColor 账（决定不变量 9 能否在本快照上验）。</summary>
    public bool HasBaseColor => _baseColor.Length != 0;

    /// <inheritdoc/>
    public override string ToString() =>
        $"snapshot {DebugName ?? "(anon)"} quads={_quads.Length} segs={_segments.Length} " +
        $"runs={_runs.Length} clips={_clips.Length} slots={_slots.Length} epoch={StructEpoch}";
}
