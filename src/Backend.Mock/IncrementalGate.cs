using FairyNext.Core;
using FairyNext.Core.Rendering;

namespace FairyNext.Backend.Mock;

/// <summary>一次增量正确性门比对的结果。</summary>
public readonly struct IncrementalGateResult
{
    /// <summary>两条流的规范形逐字节相等。</summary>
    public readonly bool Equal;
    /// <summary>首差异字节偏移（相等为 -1）。</summary>
    public readonly int FirstDifference;
    /// <summary>差异位置（分区/字段/**该由哪条通道负责**）。</summary>
    public readonly CanonicalSite Site;
    /// <summary>增量流的实例数。</summary>
    public readonly int LiveQuads;
    /// <summary>全量重建流的实例数。</summary>
    public readonly int FullQuads;

    internal IncrementalGateResult(bool equal, int firstDifference, CanonicalSite site, int live, int full)
    {
        Equal = equal; FirstDifference = firstDifference; Site = site; LiveQuads = live; FullQuads = full;
    }

    /// <summary>通过 ⇔ 逐字节相等。</summary>
    public bool Pass => Equal;

    /// <summary>差异被归到哪条通道（相等时为 <see cref="Ch.None"/>）。</summary>
    public Ch Channel => Equal ? Ch.None : Site.Channel;

    /// <summary>人读报告：过了一行，没过就直接给出**哪条通道漏标了**。</summary>
    public string Describe() =>
        Equal
            ? $"INCREMENTAL pass（quads={LiveQuads}，增量流 ≡ 全量重建流）"
            : $"INCREMENTAL fail：首差异字节 {FirstDifference} @ {Site}"
              + $"（增量 {LiveQuads} 实例 vs 全量 {FullQuads} 实例）";

    /// <inheritdoc/>
    public override string ToString() => Describe();
}

/// <summary>
/// **增量正确性门**（架构「平面六」不变量 1，L2 层的主力）：
/// 诊断构建同帧双跑「增量流消化」与「从树全量重建流」，用 <see cref="CanonicalStream"/> 的规范形
/// **逐字节**比对，差异按 <see cref="Ch"/> 通道定位。
///
/// 为什么这条门是整套架构的安全网：全系统押注「推送式失效不漏标」。漏标的症状是画面停在上一帧的
/// 某个属性上——不崩、不报错、只在特定操作序下出现，定向测试对它结构性无力。双跑对拍把它变成
/// 每帧一次的机器判定：任何差异即失效协议漏标，而且首差异字节直接指认是哪条通道的活没干。
/// 它同时是等值切断（切错 = 该脏未脏）与静态超集近似（漏订阅）的运行时兜底。
///
/// ── 两条腿的对称性 ─────────────────────────────────────────────────────────
/// 神谕不是另一份实现：全量腿走的是**同一个** <see cref="Extract"/> 算法，只是从树重头编一遍到
/// 一条 scratch 流上。于是「两腿不等」只能是增量路径少做了事，不可能是两份代码各自的 bug。
/// 派生列（world/worldVisual）那一半的神谕在树那侧（<see cref="NodeTable.DerivedMatchesFullRecompute"/>）——
/// 两个半边合起来覆盖 P6 与 P7。
///
/// ── 比对前的规范化 ─────────────────────────────────────────────────────────
/// <see cref="CanonicalStream"/> 已经剔了池下标与未被引用的条目；本类另把 <c>structEpoch</c> 归零：
/// 增量流的代数是它这辈子编过多少次，scratch 流永远是 1，那个数不是画面。
/// </summary>
public sealed class IncrementalGate
{
    private readonly RenderStream _live;
    private readonly Extract _liveExtract;
    private readonly RenderStream _oracle;
    private readonly Extract _oracleExtract;

    /// <summary>接一条在跑的流与它的整流重编器。</summary>
    /// <param name="live">增量消化的那条流。</param>
    /// <param name="liveExtract">它的整流重编器（面板根与剪枝开关由它同步过来）。</param>
    /// <param name="table">树域。</param>
    /// <param name="source">内容源（两腿共用同一份内容，否则比的就不是同一棵树）。</param>
    public IncrementalGate(RenderStream live, Extract liveExtract, NodeTable table, IExtractSource source)
    {
        _live = live ?? throw new ArgumentNullException(nameof(live));
        _liveExtract = liveExtract ?? throw new ArgumentNullException(nameof(liveExtract));
        _oracle = new RenderStream((live.DebugName ?? "stream") + ":oracle");
        _oracleExtract = new Extract(_oracle, table, source);
    }

    /// <summary>接一条管线（常规走法）。</summary>
    public IncrementalGate(RenderPipeline pipeline)
        : this(Require(pipeline).Stream, pipeline.Extract, pipeline.Table, pipeline.Source)
    {
    }

    private static RenderPipeline Require(RenderPipeline pipeline) =>
        pipeline ?? throw new ArgumentNullException(nameof(pipeline));

    /// <summary>全量重建腿用的 scratch 流（诊断可读；它从不接后端）。</summary>
    public RenderStream OracleStream => _oracle;

    /// <summary>比对次数。</summary>
    public long Checks { get; private set; }

    /// <summary>失败次数（非零 = 有通道漏标）。</summary>
    public long Failures { get; private set; }

    /// <summary>最近一次结果。</summary>
    public IncrementalGateResult Last { get; private set; } = new IncrementalGateResult(true, -1, default, 0, 0);

    /// <summary>
    /// 跑一次门。调用点在**帧尾**（P8 之后）：那时增量流已经收敛，全量腿读的 paintOrder 与
    /// world 也已定形——两腿看的是同一棵树的同一个时刻。
    /// </summary>
    public IncrementalGateResult Check()
    {
        _oracleExtract.PanelRoot = _liveExtract.PanelRoot;
        _oracleExtract.PruneAfterRebuild = _liveExtract.PruneAfterRebuild;
        _oracleExtract.Rebuild();

        StreamSnapshot live = Normalize(_live);
        StreamSnapshot full = Normalize(_oracle);
        byte[] a = CanonicalStream.Canonicalize(live);
        byte[] b = CanonicalStream.Canonicalize(full);
        int diff = CanonicalStream.FirstDifference(a, b);

        var result = new IncrementalGateResult(
            diff < 0, diff,
            diff < 0 ? default : CanonicalStream.Locate(live, diff),
            live.Quads.Length, full.Quads.Length);
        Checks++;
        if (!result.Equal) Failures++;
        Last = result;
        return result;
    }

    /// <summary>会话收据（门跑了多少次、红了几次）。</summary>
    public string DescribeReceipt() => $"incremental-gate checks={Checks} failures={Failures}";

    /// <summary>structEpoch 归零后的快照：代数是「编过几次」，不是画面。</summary>
    private static StreamSnapshot Normalize(RenderStream s) => new StreamSnapshot(
        s.Quads, s.BaseColors, s.Clips.Entries, s.Slots.Entries, s.Segments, s.BuildRunOrders(), 0u, null);
}
