using FairyNext.State;

namespace FairyNext.Core;

/// <summary>
/// 责任链的一条边（架构机制⑦ / 不变量 10）：**哪条命令入队了哪条命令**。
/// 波次超限的告警必须携带这条链——「命令生命令」的隐性循环若不可指认，
/// 「同帧最多 4 波」这条上限等于没有：调用方只知道超限了，不知道该改谁。
/// </summary>
public readonly struct CommandLink
{
    /// <summary>生成方所在波次（0 起）。</summary>
    public readonly int Wave;
    /// <summary>生成方命令序号（本 pump 内单调递增）。</summary>
    public readonly int ParentSeq;
    /// <summary>生成方标签（pump 的 describe 委托产出；未提供则为 <c>类型名#序号</c>）。</summary>
    public readonly string ParentLabel;
    /// <summary>被生成命令的序号。</summary>
    public readonly int ChildSeq;
    /// <summary>被生成命令的标签。</summary>
    public readonly string ChildLabel;

    internal CommandLink(int wave, int parentSeq, string parentLabel, int childSeq, string childLabel)
    {
        Wave = wave;
        ParentSeq = parentSeq;
        ParentLabel = parentLabel;
        ChildSeq = childSeq;
        ChildLabel = childLabel;
    }

    /// <inheritdoc/>
    public override string ToString() => $"w{Wave}: {ParentLabel} → {ChildLabel}";
}

/// <summary>
/// P3 命令泵（内核面）。一个泵 = 一种命令类型；内核按波次轮询全部泵。
/// 排空的**时机与上限**归相位机（≤<c>Abi.CommandWaveLimit</c> 波，超限延帧），
/// 泵只负责「一波怎么排」与「谁生成了谁」。
/// </summary>
public interface ICommandPump
{
    /// <summary>诊断名（出现在责任链与超限告警里）。</summary>
    string Name { get; }

    /// <summary>在队条数（含本帧尚未执行的延帧余量）。</summary>
    int PendingCount { get; }

    /// <summary>帧首复位（清责任链；在队命令**不清**——超限的余量按定义要延到下一帧继续跑）。</summary>
    void BeginFrame(ulong frameId);

    /// <summary>
    /// 排空当前在队的**一波快照**，返回执行条数。
    /// 执行期间新入队的命令留在队尾，构成下一波——这是「波次」得以有界的全部机制。
    /// </summary>
    int DrainWave(ref FrameContext ctx);

    /// <summary>责任链现存边数（环形保留最近若干条）。</summary>
    int ChainCount { get; }

    /// <summary>取责任链的第 <paramref name="index"/> 条边（0 = 最早保留的一条）。</summary>
    CommandLink ChainAt(int index);
}

/// <summary>命令执行体（<c>in</c> 传值类型命令，零拷贝；ref ctx 让命令能读相位/帧号）。</summary>
/// <typeparam name="T">命令类型（建议 struct，稳态零 GC）。</typeparam>
/// <param name="command">命令。</param>
/// <param name="ctx">本帧上下文（<see cref="FrameContext.Wave"/> = 当前波次）。</param>
public delegate void CommandExecutor<T>(in T command, ref FrameContext ctx);

/// <summary>
/// 泛型命令泵：包一条 <see cref="CommandQueue{T}"/>（M1-03 自 fork 直搬件，不重写）
/// 加一层「谁生成了谁」的责任链记账。
///
/// 入队一律走本类的 <see cref="Enqueue"/> 而不是内部队列：责任链的记录点在这里——
/// 执行某条命令期间发生的入队，被记成 <c>该命令 → 新命令</c> 的一条边。
/// 执行之外的入队（P0 事件回调、宿主线程）没有父，不记边。
/// </summary>
/// <typeparam name="T">命令类型。</typeparam>
public sealed class CommandPump<T> : ICommandPump
{
    /// <summary>责任链环形缓冲容量（超出部分丢最早的边；诊断只需要最近的生成关系）。</summary>
    public const int LinkRingCapacity = 64;

    private readonly struct Entry
    {
        public readonly T Command;
        public readonly int Seq;
        public Entry(in T command, int seq) { Command = command; Seq = seq; }
    }

    private readonly CommandQueue<Entry> _queue;
    private readonly CommandExecutor<T> _executor;
    private readonly Func<T, string>? _describe;
    private readonly CommandLink[] _links = new CommandLink[LinkRingCapacity];

    private int _linkWrite;
    private int _linkCount;
    private int _nextSeq;

    // 当前正在执行的命令（责任链的父）
    private bool _executing;
    private int _currentSeq = -1;
    private int _currentWave;
    private string? _currentLabel;
    private T _current = default!;

    /// <summary>建泵。</summary>
    /// <param name="executor">命令执行体（P3 内调用）。</param>
    /// <param name="describe">命令 → 诊断标签；null 时用 <c>类型名#序号</c>。只在真的记录责任链时被调用。</param>
    /// <param name="name">泵名（诊断）。</param>
    /// <param name="capacity">环形队列初始容量。</param>
    public CommandPump(CommandExecutor<T> executor, Func<T, string>? describe = null,
        string? name = null, int capacity = 16)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _describe = describe;
        _queue = new CommandQueue<Entry>(capacity);
        Name = name ?? typeof(T).Name;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public int PendingCount => _queue.count;

    /// <summary>已入队命令总数（诊断；也是序号发号器的水位）。</summary>
    public int TotalEnqueued => _nextSeq;

    /// <summary>
    /// 入队。任何相位都可入队（P0 事件回调、P3 命令体、宿主线程外的同步调用）；
    /// 执行**永远**在下一个 P3 的波次里——命令队列不是唯一写入口，法定合成点才是。
    /// </summary>
    public void Enqueue(in T command)
    {
        int seq = _nextSeq++;
        _queue.Enqueue(new Entry(in command, seq));
        if (_executing) RecordLink(in command, seq);
    }

    /// <inheritdoc/>
    public void BeginFrame(ulong frameId)
    {
        _ = frameId;
        _linkCount = 0;
        _linkWrite = 0;
    }

    /// <inheritdoc/>
    public int DrainWave(ref FrameContext ctx)
    {
        int wave = ctx.Wave < 0 ? 0 : ctx.Wave;
        int snapshot = _queue.count;                 // 本波 = 进波瞬间的在队快照
        int executed = 0;
        for (int i = 0; i < snapshot; i++)
        {
            if (!_queue.TryDequeue(out Entry e)) break;
            _executing = true;
            _currentSeq = e.Seq;
            _currentWave = wave;
            _currentLabel = null;                    // 懒求值：没生成下一波就不花这次字符串
            _current = e.Command;
            try
            {
                _executor(in e.Command, ref ctx);
            }
            finally
            {
                _executing = false;
                _currentSeq = -1;
                _currentLabel = null;
                _current = default!;
            }
            executed++;
        }
        return executed;
    }

    /// <inheritdoc/>
    public int ChainCount => _linkCount;

    /// <inheritdoc/>
    public CommandLink ChainAt(int index)
    {
        if ((uint)index >= (uint)_linkCount) return default;
        int start = _linkCount == LinkRingCapacity ? _linkWrite : 0;
        return _links[(start + index) % LinkRingCapacity];
    }

    /// <summary>清空在队命令（树域重置/回放起点）。责任链一并清。</summary>
    public void Clear()
    {
        _queue.Clear();
        _linkCount = 0;
        _linkWrite = 0;
    }

    private void RecordLink(in T child, int childSeq)
    {
        _currentLabel ??= Describe(in _current, _currentSeq);
        var link = new CommandLink(_currentWave, _currentSeq, _currentLabel, childSeq, Describe(in child, childSeq));
        _links[_linkWrite] = link;
        _linkWrite = (_linkWrite + 1) % LinkRingCapacity;
        if (_linkCount < LinkRingCapacity) _linkCount++;
    }

    private string Describe(in T command, int seq)
    {
        if (_describe != null) return _describe(command);
        return typeof(T).Name + "#" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
