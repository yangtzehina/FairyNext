using System.Text;
using FairyNext.Core;
using FairyNext.Core.Rendering;

namespace FairyNext.Backend.Mock;

/// <summary>
/// 门捆绑的零断言载体（架构「平面六 · 验证平面」核心数据结构 <c>GateReport</c>）：
/// **七个计数器，全部为零才算通过**。
///
/// 为什么是计数而不是布尔：降级阶梯的存在让「画对了」不再等于「走对了路径」——
/// 槽荒退 tier-2、clip 超限降父窗口、材质拒绝走 scissor，每一条都能把像素画对，
/// 同时把性能路径悄悄换掉。不绑门，性能路径会在测试全绿的掩护下静默腐烂。
///
/// 七字段与来源：
/// <list type="table">
/// <item><term>degrade</term><description>降级阶梯事件数 ← <see cref="IRenderBackend.ReportDegrade"/>（渲染阶梯上报）</description></item>
/// <item><term>slotOverflow</term><description>transform 槽越预算次数 ← mock 后端 WriteSlots</description></item>
/// <item><term>clipOverflow</term><description>ClipEntry 越预算次数 ← mock 后端 UploadClips</description></item>
/// <item><term>fencePending</term><description>fence 队列超深次数 ← mock 后端 DestroyStream</description></item>
/// <item><term>waveOverflow</term><description>P3 命令波次超限帧数 ← <see cref="FrameDiag.CommandWaveOverflow"/></description></item>
/// <item><term>relabelOverBudget</term><description>序重推超预算次数 ← mock 后端 SetRunOrders</description></item>
/// <item><term>phaseViolation</term><description>相位违例 ← 失效协议的 P7/P8 违约写 + 后端 pass 序违约</description></item>
/// </list>
///
/// **本报告不覆盖协议违约**（use-after-free、axis-aligned 位不一致、帧括号错序）：
/// 那些是断言级失败，走 <see cref="MockBackend.Violations"/>。两者的语义不同——
/// 门计数说「走了降级路径」，违约说「协议破了」。M1-26 的捆绑门两者都查：
/// <c>report.Pass &amp;&amp; backend.Violations.Count == 0</c>。
/// </summary>
public struct GateReport
{
    /// <summary>降级阶梯事件数（degrade）。</summary>
    public uint Degrade;
    /// <summary>transform 槽越预算次数（slotOverflow）。</summary>
    public uint SlotOverflow;
    /// <summary>ClipEntry 越预算次数（clipOverflow）。</summary>
    public uint ClipOverflow;
    /// <summary>fence 队列超深次数（fencePending）。</summary>
    public uint FencePending;
    /// <summary>命令波次超限帧数（waveOverflow）。</summary>
    public uint WaveOverflow;
    /// <summary>序重推超预算次数（relabelOverBudget）。</summary>
    public uint RelabelOverBudget;
    /// <summary>相位违例数（phaseViolation）。</summary>
    public uint PhaseViolation;

    /// <summary>七字段全零。</summary>
    public readonly bool AllZero =>
        (Degrade | SlotOverflow | ClipOverflow | FencePending
         | WaveOverflow | RelabelOverBudget | PhaseViolation) == 0;

    /// <summary>通过 ⇔ 全零。任一非零 = 靠降级画对 = 不算过。</summary>
    public readonly bool Pass => AllZero;

    /// <summary>七字段之和（诊断用的单一水位；不用于判定——判定看 <see cref="AllZero"/>）。</summary>
    public readonly long Total =>
        (long)Degrade + SlotOverflow + ClipOverflow + FencePending
        + WaveOverflow + RelabelOverBudget + PhaseViolation;

    /// <summary>
    /// 内核半边：波次超限与相位违约写。
    /// 相位违例在这里与后端半边**相加**——两个来源是同一类错误的两处观测点，分开报会让门漏判。
    /// </summary>
    public static GateReport FromKernel(FrameDiag diag)
    {
        var r = default(GateReport);
        if (diag == null) return r;
        if (diag.CommandWaveOverflow) r.WaveOverflow++;
        long violations = diag.PhaseViolations;
        r.PhaseViolation = violations > 0 ? (uint)Math.Min(violations, uint.MaxValue) : 0u;
        return r;
    }

    /// <summary>后端半边（直接取 mock 的门计数）。</summary>
    public static GateReport FromBackend(MockBackend backend) =>
        backend == null ? default : backend.Gates;

    /// <summary>逐字段相加（会话内多帧汇总 / 内核半边 + 后端半边）。</summary>
    public static GateReport operator +(GateReport a, GateReport b) => new GateReport
    {
        Degrade = a.Degrade + b.Degrade,
        SlotOverflow = a.SlotOverflow + b.SlotOverflow,
        ClipOverflow = a.ClipOverflow + b.ClipOverflow,
        FencePending = a.FencePending + b.FencePending,
        WaveOverflow = a.WaveOverflow + b.WaveOverflow,
        RelabelOverBudget = a.RelabelOverBudget + b.RelabelOverBudget,
        PhaseViolation = a.PhaseViolation + b.PhaseViolation,
    };

    /// <summary>
    /// 人读报告：通过时一行，失败时逐条列非零字段——门失败要直接给出**哪条路径腐烂了**，
    /// 而不是一个「gate failed」。
    /// </summary>
    public readonly string Describe()
    {
        if (AllZero) return "GATE pass（七字段全零）";
        var sb = new StringBuilder("GATE fail：");
        Append(sb, "degrade", Degrade);
        Append(sb, "slotOverflow", SlotOverflow);
        Append(sb, "clipOverflow", ClipOverflow);
        Append(sb, "fencePending", FencePending);
        Append(sb, "waveOverflow", WaveOverflow);
        Append(sb, "relabelOverBudget", RelabelOverBudget);
        Append(sb, "phaseViolation", PhaseViolation);
        return sb.ToString();
    }

    /// <inheritdoc/>
    public readonly override string ToString() => Describe();

    private static void Append(StringBuilder sb, string name, uint value)
    {
        if (value == 0) return;
        if (sb[sb.Length - 1] != '：') sb.Append(' ');
        sb.Append(name).Append('=').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
