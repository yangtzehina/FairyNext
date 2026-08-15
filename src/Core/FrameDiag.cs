using System.Diagnostics;

namespace FairyNext.Core;

/// <summary>
/// 一帧的诊断锁存（架构门面 <c>UiKernel.Diagnostics</c>，机制⑫）：
/// 聚合 <see cref="InvalidationDiag"/>（按通道/理由聚合的失效账）+ 逐相位计时 +
/// P3 波次与延帧余量 + P7/P8 写门违例数 + 责任链。
///
/// **锁存时点是 P9**：帧内累积在「活账」上，P9 把活账整体拷进锁存账，
/// 于是 <see cref="UiKernel.Diagnostics"/> 永远是一份自洽的完整帧，不会读到半帧状态。
/// 逐相位计时用 <see cref="Stopwatch"/> 原生 tick 存储，换算成毫秒只在读出时做。
/// </summary>
public sealed class FrameDiag
{
    /// <summary>相位计时槽数（P0–P9）。</summary>
    public const int PhaseSlots = 10;

    /// <summary>责任链拷贝的容量上限（超出计入 <see cref="ChainTruncated"/>）。</summary>
    public const int ChainCapacity = 64;

    private readonly long[] _phaseTicks = new long[PhaseSlots];
    private readonly CommandLink[] _chain = new CommandLink[ChainCapacity];
    private int _chainCount;

    /// <summary>本帧帧号。</summary>
    public ulong FrameId;

    /// <summary>本帧总耗时（Stopwatch tick）。</summary>
    public long TotalTicks;

    /// <summary>P0 排空的输入包数。</summary>
    public int InputsDrained;

    /// <summary>P0 未排空的输入包数（没装 <see cref="UiKernel.InputHandler"/> 时留到下一帧）。</summary>
    public int InputsHeld;

    /// <summary>P1 触发的定时器条数。</summary>
    public int TimersFired;

    /// <summary>P2 生效的全局失效源数（0 或 1 或 2）。</summary>
    public int GlobalsApplied;

    /// <summary>P3 实际跑了几波。</summary>
    public int CommandWaves;

    /// <summary>P3 执行的命令条数。</summary>
    public int CommandsExecuted;

    /// <summary>P3 超限后延到下一帧的命令条数。</summary>
    public int CommandsDeferred;

    /// <summary>P3 是否撞上波次上限（撞上时 <see cref="ChainCount"/> 必须 &gt; 0，否则诊断实现本身是 bug）。</summary>
    public bool CommandWaveOverflow;

    /// <summary>P6 下行下钻访问到的节点数。</summary>
    public int DownVisited;

    /// <summary>本帧交付给排水器的句柄总数（各相位累计）。</summary>
    public int DrainedHandles;

    /// <summary>失效账（P9 从 <see cref="Invalidation.LastFrame"/> 拷入）。</summary>
    public InvalidationDiag Invalidation { get; } = new InvalidationDiag();

    /// <summary>P7/P8（及 P9 钩子）内的违约写次数——不变量 9 的可观测量。</summary>
    public long PhaseViolations => Invalidation.PhaseViolations;

    /// <summary>被写门顺延到下一帧排水的失效条数（不丢、只延迟）。</summary>
    public long DeferredMarks => Invalidation.DeferredEntries;

    /// <summary>责任链边数。</summary>
    public int ChainCount => _chainCount;

    /// <summary>责任链是否被截断（边数超过 <see cref="ChainCapacity"/>）。</summary>
    public bool ChainTruncated;

    /// <summary>取责任链第 <paramref name="index"/> 条边。</summary>
    public CommandLink ChainAt(int index) =>
        (uint)index < (uint)_chainCount ? _chain[index] : default;

    /// <summary>某相位耗时（Stopwatch tick）。</summary>
    public long TicksOf(FramePhase phase)
    {
        int i = (int)phase;
        return (uint)i < PhaseSlots ? _phaseTicks[i] : 0;
    }

    /// <summary>某相位耗时（毫秒）。</summary>
    public double MillisOf(FramePhase phase) => TicksOf(phase) * 1000.0 / Stopwatch.Frequency;

    /// <summary>本帧总耗时（毫秒）。</summary>
    public double TotalMillis => TotalTicks * 1000.0 / Stopwatch.Frequency;

    /// <summary>清零（帧首对活账调用）。</summary>
    public void Reset()
    {
        Array.Clear(_phaseTicks, 0, _phaseTicks.Length);
        _chainCount = 0;
        ChainTruncated = false;
        FrameId = 0;
        TotalTicks = 0;
        InputsDrained = InputsHeld = TimersFired = GlobalsApplied = 0;
        CommandWaves = CommandsExecuted = CommandsDeferred = 0;
        CommandWaveOverflow = false;
        DownVisited = 0;
        DrainedHandles = 0;
        Invalidation.Reset();
    }

    /// <summary>整体拷贝（P9 锁存：活账 → 锁存账，不共享数组）。</summary>
    public void CopyFrom(FrameDiag src)
    {
        Array.Copy(src._phaseTicks, _phaseTicks, PhaseSlots);
        Array.Copy(src._chain, _chain, ChainCapacity);
        _chainCount = src._chainCount;
        ChainTruncated = src.ChainTruncated;
        FrameId = src.FrameId;
        TotalTicks = src.TotalTicks;
        InputsDrained = src.InputsDrained;
        InputsHeld = src.InputsHeld;
        TimersFired = src.TimersFired;
        GlobalsApplied = src.GlobalsApplied;
        CommandWaves = src.CommandWaves;
        CommandsExecuted = src.CommandsExecuted;
        CommandsDeferred = src.CommandsDeferred;
        CommandWaveOverflow = src.CommandWaveOverflow;
        DownVisited = src.DownVisited;
        DrainedHandles = src.DrainedHandles;
        Invalidation.CopyFrom(src.Invalidation);
    }

    internal void AddPhaseTicks(FramePhase phase, long ticks)
    {
        int i = (int)phase;
        if ((uint)i < PhaseSlots) _phaseTicks[i] += ticks;
        TotalTicks += ticks;
    }

    internal void AddLink(in CommandLink link)
    {
        if (_chainCount == ChainCapacity) { ChainTruncated = true; return; }
        _chain[_chainCount++] = link;
    }
}
