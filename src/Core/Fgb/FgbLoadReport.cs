using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FairyNext.Core.Fgb;

/// <summary>
/// FGB 装载门编号（平面五「装载门」不变量 1-5）。编号 **append-only**：LoadReport 里的门号是
/// 跨版本可读的诊断词表，永不重编号。
///
/// 分档（架构机制 4「降级阶梯分档」的可执行形态，<see cref="FgbGateClass"/> 是判据本体）：
///  · **结构性不符** → 拒载。只源于部署错配（版本不匹配 / 文件损坏 / 段目录越界 / 段内计数自相矛盾），
///    必须响亮：画不出来好过画错，且这类错重启一万次也不会自愈。
///  · **哈希级失配** → 组件级降回运行时 Extract + 计数。分包热更窗口期不开天窗。
/// </summary>
public enum FgbGate : byte
{
    /// <summary>未被任何门拒绝（装载成功）。</summary>
    None = 0,
    /// <summary>字节数不足以容纳头/目录（门 1 的长度前置）。</summary>
    Truncated = 1,
    /// <summary>magic != "FGB1"（门 1）。</summary>
    Magic = 2,
    /// <summary>formatVersion 不精确匹配（门 1）——结构性拒载，不降级。</summary>
    FormatVersion = 3,
    /// <summary>宿主不是小端（LE-only 格式在 BE 宿主上 Cast 直读必错，拒载）。</summary>
    Endianness = 4,
    /// <summary>flags：LE 断言位缺失 / 压缩位置位 / 保留位非零（门 1）。</summary>
    HeaderFlags = 5,
    /// <summary>段目录条目数越界（在读目录之前拒——计数是外部输入，先门后分配）。</summary>
    SectionCount = 6,
    /// <summary>段 offset + length 越 blob 界，或段与头/目录重叠（门 2）。</summary>
    SectionBounds = 7,
    /// <summary>段 offset 不是 FgbSectionAlignment 对齐（门 2）。</summary>
    SectionMisaligned = 8,
    /// <summary>selfHash 重算不符（门 3；发布包可信任跳过）。</summary>
    SelfHash = 9,
    /// <summary>段字节长不是记录宽的整倍数（Cast 视图取用时报）。</summary>
    RecordStride = 10,
    /// <summary>NODE 段 payload 与「16B 头 + 逐列对齐布局」的精确尺寸不符（不收半列）。</summary>
    NodeSectionShape = 11,

    // ── M1-22 续编号（append-only）：段内验证与身份门 ─────────────────────────

    /// <summary>必备段缺席（COMP/NODE/PLAN/CONT/LOCL/CNST 之一）——拒载。</summary>
    SectionMissing = 12,
    /// <summary>段字节长与段内计数自相矛盾（STRT 池越界 / CNST 四数组账不符）——拒载。</summary>
    SectionShape = 13,
    /// <summary>COMP 的某段区间越该段记录数，或 nodeCount 之和 != NODE 段行数——拒载。</summary>
    CompRange = 14,
    /// <summary>PLAN 违反后序性质 / compIndex 越界 / hostLocalId 越宿主 nodeCount——拒载。</summary>
    PlanShape = 15,
    /// <summary>PTCH 的 texRef 越 TREF 或 target 越目标段——拒载。</summary>
    PatchRange = 16,
    /// <summary>门 4：DEPS 逐项 sourceHash 不符 / 依赖缺席 / combinedRefHash 重算不符——**降级**。</summary>
    DepsMismatch = 17,
    /// <summary>门 4：scaleLevel/branchId 与宿主全局设定不符——**降级**。</summary>
    IdentityMismatch = 18,
    /// <summary>门 5：文件名 <c>名字_id[.sN][.bX]</c> 的 id/sN/bX 与头内值不一致——拒载（部署错配）。</summary>
    FileName = 19,
    /// <summary>模板的 NODE 拓扑列不构成一棵以局部 0 为根的树（环/断链/兄弟环不闭合）——拒载。</summary>
    NodeTopology = 20,
}

/// <summary>门的分档：拒载 / 降级。</summary>
public enum FgbGateClass : byte
{
    /// <summary>结构性不符：拒载 + 响亮。</summary>
    Structural = 0,
    /// <summary>哈希级失配：组件级降回运行时 Extract + 计数。</summary>
    Degrade = 1,
}

/// <summary>一次装载的三种出口（**二值失败面的可执行形态**：绝无「半载」这一档）。</summary>
public enum FgbLoadOutcome : byte
{
    /// <summary>拒载：<c>package == null</c>，一个字节也没被信任。</summary>
    Rejected = 0,
    /// <summary>装载成功且身份四维全绿。</summary>
    Loaded = 1,
    /// <summary>装载成功但身份哈希失配：组件级降回运行时 Extract，有声计数。</summary>
    Degraded = 2,
}

/// <summary>装载门的判决（逐门逐项可读的一行）。</summary>
public readonly struct FgbGateVerdict
{
    /// <summary>门号。</summary>
    public readonly FgbGate Gate;
    /// <summary>分档。</summary>
    public readonly FgbGateClass Class;
    /// <summary>是否被本门拦下（false = 通过或跳过）。</summary>
    public readonly bool Tripped;
    /// <summary>本门是否因缺语料被跳过（例：未给文件名 ⇒ 门 5 无对照物）。</summary>
    public readonly bool Skipped;
    /// <summary>人读细节（期望 vs 实得；通过时为空串）。</summary>
    public readonly string Detail;

    internal FgbGateVerdict(FgbGate gate, FgbGateClass cls, bool tripped, bool skipped, string detail)
    {
        Gate = gate; Class = cls; Tripped = tripped; Skipped = skipped; Detail = detail;
    }

    /// <summary>状态词（pass/skip/reject/degrade）——进 <see cref="FgbLoadReport.Describe"/> 的那一列。</summary>
    public string Status => Skipped ? "skip"
        : !Tripped ? "pass"
        : Class == FgbGateClass.Structural ? "reject" : "degrade";
}

/// <summary>
/// 装载报告（架构文档 <c>AssetServer.lastReport</c> 的落地形态）：**哪一门拒的 / 段尺寸清单 /
/// patch 数 / 耗时**。M1-19 只有前三门与段清单；M1-22 补齐门 4/5、逐门判决表、
/// 降级二分与 <see cref="Describe"/> 的确定性人读面（无时间戳、无路径——它进 CI 日志也进用例断言）。
///
/// 报告在**拒载路径上也必须完整**：一次拒载要能回答「哪一门、期望什么、实得什么」，
/// 否则线上只剩「装载失败」四个字，等于没有诊断。
/// </summary>
public sealed class FgbLoadReport
{
    /// <summary>拒载的门号；None = 未被拒（可能仍是 <see cref="FgbLoadOutcome.Degraded"/>）。</summary>
    public FgbGate RejectedBy;

    /// <summary>人读细节（拒载时：期望 vs 实得；成功时为空串）。</summary>
    public string Detail = "";

    /// <summary>blob 总字节数（进门就填，拒载报告也带）。</summary>
    public long BlobBytes;

    /// <summary>头声明的段数（目录读出后填）。</summary>
    public int SectionCount;

    /// <summary>成功时的段清单（fourcc + 字节长，目录序）；容器门拒载时为 null。</summary>
    public (uint Fourcc, ulong Length)[]? Sections;

    /// <summary>本次装载的出口。</summary>
    public FgbLoadOutcome Outcome = FgbLoadOutcome.Rejected;

    /// <summary>逐门判决（跑过的门，按门号序）。</summary>
    public readonly List<FgbGateVerdict> Gates = new List<FgbGateVerdict>();

    /// <summary>装载期回填的 PTCH 条数（机制 3 的 O(patch 数) 那个数）。</summary>
    public int PatchCount;

    /// <summary>组件数（COMP 记录数）。</summary>
    public int ComponentCount;

    /// <summary>PLAN 步数。</summary>
    public int PlanSteps;

    /// <summary>DEPS 条数。</summary>
    public int DepCount;

    /// <summary>DEPS 中 <c>expectedSourceHash == 0</c>（单包编译面给不出）的条数——**未知 ≠ 不符**。</summary>
    public int DepsUnverified;

    /// <summary>降级涉及的组件数（降级是组件级：全包降级时 = 组件总数）。</summary>
    public int DegradedComponents;

    /// <summary>装载耗时（<c>Stopwatch</c> tick；**不进 <see cref="Describe"/>**——它必须确定性）。</summary>
    public long ElapsedTicks;

    internal void Note(FgbGate gate, FgbGateClass cls, bool tripped, bool skipped, string detail)
    {
        Gates.Add(new FgbGateVerdict(gate, cls, tripped, skipped, detail));
        if (!tripped) return;
        if (cls == FgbGateClass.Structural)
        {
            RejectedBy = gate;
            Detail = detail;
            Outcome = FgbLoadOutcome.Rejected;
        }
        else if (Outcome != FgbLoadOutcome.Rejected)
        {
            Outcome = FgbLoadOutcome.Degraded;
        }
    }

    /// <summary>被降级门拦下的门号（按发生序；无降级为空）。</summary>
    public IEnumerable<FgbGate> DegradedBy
    {
        get
        {
            for (int i = 0; i < Gates.Count; i++)
                if (Gates[i].Tripped && Gates[i].Class == FgbGateClass.Degrade) yield return Gates[i].Gate;
        }
    }

    /// <summary>是否被某一门拦下（拒载或降级）。</summary>
    public bool Tripped(FgbGate gate)
    {
        for (int i = 0; i < Gates.Count; i++)
            if (Gates[i].Gate == gate && Gates[i].Tripped) return true;
        return false;
    }

    /// <summary>
    /// 确定性人读面（**测试断言锚 + CI 日志**）：出口行 → 逐门一行 → 段清单一行。
    /// 不含耗时/路径/机器名——那些会让同一次装载每次打印不同的字。
    /// </summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("load ").Append(Outcome.ToString().ToLowerInvariant())
          .Append(" blob=").Append(N(BlobBytes)).Append('B')
          .Append(" sections=").Append(N(SectionCount))
          .Append(" components=").Append(N(ComponentCount))
          .Append(" plan=").Append(N(PlanSteps))
          .Append(" patches=").Append(N(PatchCount))
          .Append(" deps=").Append(N(DepCount)).Append('/').Append(N(DepsUnverified)).Append("unverified")
          .Append(" degraded=").Append(N(DegradedComponents))
          .Append('\n');
        for (int i = 0; i < Gates.Count; i++)
        {
            FgbGateVerdict g = Gates[i];
            sb.Append("  gate ").Append(N((int)g.Gate)).Append(' ').Append(g.Gate).Append(' ')
              .Append(g.Status);
            if (g.Detail.Length > 0) sb.Append("  ").Append(g.Detail);
            sb.Append('\n');
        }
        if (Sections != null)
        {
            sb.Append("  sections");
            for (int i = 0; i < Sections.Length; i++)
                sb.Append(' ').Append(Fourcc(Sections[i].Fourcc)).Append('=').Append(N((long)Sections[i].Length)).Append('B');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        RejectedBy == FgbGate.None && Outcome != FgbLoadOutcome.Rejected
            ? Outcome.ToString().ToLowerInvariant() + " blob=" + N(BlobBytes) + "B sections=" + N(SectionCount)
            : "rejected by " + RejectedBy + ": " + Detail;

    private static string Fourcc(uint v) => new string(new[]
    {
        (char)(v & 0xFF), (char)((v >> 8) & 0xFF), (char)((v >> 16) & 0xFF), (char)((v >> 24) & 0xFF),
    });

    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);
}
