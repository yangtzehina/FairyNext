namespace FairyNext.Core;

/// <summary>
/// 时域（架构「平面二」机制⑪）。三域并存、语义**一处声明**：
/// UI 默认 <see cref="Unscaled"/>（不吃 timeScale），跟随游戏内物件的 UI 逐定时器改 <see cref="Scaled"/>，
/// <see cref="Manual"/> 由测试/回放代码显式步进——没有 Manual 域，回放测试永远被宿主时钟污染。
/// </summary>
public enum TimeDomain : byte
{
    /// <summary>缩放时域：按 <see cref="FrameTime.ScaledDt"/> 推进（吃 timeScale）。</summary>
    Scaled = 0,
    /// <summary>真实时域：按 <see cref="FrameTime.UnscaledDt"/> 推进（不吃 timeScale）。UI 默认。</summary>
    Unscaled = 1,
    /// <summary>手动时域：**只**由 <see cref="Clock.StepManual"/> 推进；Tick 不动它（回放门的前提）。</summary>
    Manual = 2,
}

/// <summary>
/// 定时器代际句柄（架构机制⑪ / 不变量 11）：<c>(槽下标, 代际)</c>。
/// 槽被回收后代际号 +1，于是**陈旧句柄的 Cancel 是静默 no-op**——
/// 「跨帧缓存定时器引用」从文档警告变成类型防呆。
/// </summary>
public readonly struct TimerHandle : IEquatable<TimerHandle>
{
    /// <summary>槽下标。</summary>
    public readonly int Index;
    /// <summary>代际号（0 = 空句柄）。</summary>
    public readonly int Gen;

    internal TimerHandle(int index, int gen)
    {
        Index = index;
        Gen = gen;
    }

    /// <summary>空句柄（<see cref="Clock.Cancel"/> 对它静默 no-op）。</summary>
    public static readonly TimerHandle None = default;

    /// <summary>是否为空句柄。</summary>
    public bool IsNone => Gen == 0;

    /// <inheritdoc/>
    public bool Equals(TimerHandle other) => Index == other.Index && Gen == other.Gen;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TimerHandle h && Equals(h);
    /// <inheritdoc/>
    public override int GetHashCode() => (Index * 397) ^ Gen;
    /// <inheritdoc/>
    public override string ToString() => IsNone ? "TimerHandle.None" : $"timer#{Index}@g{Gen}";
}

/// <summary>
/// 三时域回调式定时器（架构「平面二」门面 <c>Clock</c>，机制⑪）。
/// tween 与时间轴**不在这里**——它们归状态层，在 P4 的 <c>TickTimelines</c> 推进并写 anim 通道；
/// 两者读同一份 <see cref="FrameTime"/>，时域语义只此一处声明。
///
/// **触发序是 API 承诺，不是实现细节**：同一次推进内到期的定时器按 <c>(deadline, 插入序)</c>
/// 双键排序触发——先按到期时刻升序，**同一 deadline 按 After/Every 的调用先后**。
/// 单键「创建序」不够用：晚建的短定时器必须先响，而同刻到期时又必须回到创建序才有确定性。
/// 这条顺序由单测钉住（<c>tests/FairyNext.Tests/UiKernelTests.cs</c>），改它等于改公开契约。
///
/// 与架构文档的形态差异：文档写 <c>static class Clock</c>，这里是**每内核一个实例**——
/// 静态全局态会让多 stage / 多测试互相污染，也让 Manual 域无法并行回放（同 M1-07 对 Invalidation 的处理）。
/// </summary>
public sealed class Clock
{
    private struct TimerRec
    {
        public int Gen;                     // 代际（回收时 +1，跳过 0）
        public TimeDomain Domain;
        public double Deadline;             // 所属时域的绝对时刻
        public double Interval;             // >0 = 周期定时器；<=0 = 一次性
        public long Seq;                    // 插入序（双键排序的第二键）
        public Action<TimerHandle>? Callback;
        public bool Active;
    }

    private TimerRec[] _recs = new TimerRec[16];
    private int _count;                     // 已用槽数（含空闲槽）
    private int[] _free = new int[16];
    private int _freeCount;
    private long _seqNext;
    private int _activeCount;

    private double _scaledNow;
    private double _unscaledNow;
    private double _manualNow;

    // 到期集快照（本次推进期间新建的定时器不在集内 ⇒ 同帧不触发，不会自我喂养成死循环）
    private int[] _due = new int[16];
    private int _dueCount;

    /// <summary>某时域的当前时刻（秒）。</summary>
    public double Now(TimeDomain domain) => domain switch
    {
        TimeDomain.Scaled => _scaledNow,
        TimeDomain.Unscaled => _unscaledNow,
        TimeDomain.Manual => _manualNow,
        _ => _unscaledNow,
    };

    /// <summary>在队（未取消、未触发完）的定时器数。</summary>
    public int ActiveCount => _activeCount;

    /// <summary>上一次推进触发的定时器条数（诊断）。</summary>
    public int LastFired { get; private set; }

    /// <summary>
    /// <paramref name="seconds"/> 秒后触发一次（触发后自动回收，句柄随即失效）。
    /// </summary>
    /// <param name="seconds">延时（秒；≤0 = 下一次推进即到期）。</param>
    /// <param name="domain">时域。</param>
    /// <param name="callback">回调（收到自己的句柄——回调内可 Cancel 自己或重排）。</param>
    public TimerHandle After(float seconds, TimeDomain domain, Action<TimerHandle> callback) =>
        Arm(seconds, 0.0, domain, callback);

    /// <summary>
    /// 每 <paramref name="seconds"/> 秒触发一次（首次也在 <paramref name="seconds"/> 秒后）。
    /// 重排在回调**之前**完成：回调内 <see cref="Cancel"/> 自己一定生效。
    /// 一次推进内最多触发一次——即使 dt 大于若干个周期，也不做「补触发」
    /// （补触发会让掉帧变成回调风暴，且破坏「同帧至多一次」的可预期性）。
    /// </summary>
    public TimerHandle Every(float seconds, TimeDomain domain, Action<TimerHandle> callback)
    {
        UiAssert.That(seconds > 0f, "Every 的周期必须 > 0（0 周期 = 每帧回调，请用 P1 钩子）");
        double interval = seconds > 0f ? seconds : 0.0;
        return Arm(seconds, interval, domain, callback);
    }

    /// <summary>
    /// 取消。**gen 不符 = 静默 no-op**（不变量 11）：句柄陈旧、槽已被复用、重复取消——一律安全。
    /// </summary>
    public void Cancel(TimerHandle handle)
    {
        if (handle.IsNone) return;
        int i = handle.Index;
        if ((uint)i >= (uint)_count) return;
        if (_recs[i].Gen != handle.Gen || !_recs[i].Active) return;    // 静默 no-op
        Release(i);
    }

    /// <summary>句柄是否仍指向活定时器（诊断/测试）。</summary>
    public bool IsActive(TimerHandle handle) =>
        !handle.IsNone && (uint)handle.Index < (uint)_count
        && _recs[handle.Index].Gen == handle.Gen && _recs[handle.Index].Active;

    /// <summary>
    /// 推进 Manual 时域（**Manual 的唯一推进方式**）。回放/单测在 <see cref="UiKernel.Tick"/> 前调；
    /// 到期判定仍然发生在 P1——时域是「时间怎么走」，相位是「什么时候看」，两件事不混。
    /// </summary>
    public void StepManual(double seconds)
    {
        UiAssert.That(seconds >= 0.0, "Manual 时域不可倒流（回放靠单调时间线）");
        if (seconds > 0.0) _manualNow += seconds;
    }

    /// <summary>P1 前半：把宿主时刻装进 Scaled/Unscaled 两域（Manual 不受影响）。</summary>
    internal void Advance(in FrameTime time)
    {
        UiAssert.That(time.ScaledNow >= _scaledNow && time.UnscaledNow >= _unscaledNow,
            "FrameTime 时刻回退（宿主必须保证两域单调不减）");
        if (time.ScaledNow > _scaledNow) _scaledNow = time.ScaledNow;
        if (time.UnscaledNow > _unscaledNow) _unscaledNow = time.UnscaledNow;
    }

    /// <summary>
    /// P1 后半：触发本次推进的到期集，返回触发条数。
    /// 三步：① 快照到期集（此后新建的定时器不进本批）；② 按 (deadline, seq) 双键排序；
    /// ③ 顺序触发，每次触发前重验「仍然活着且代际未变」——前一个回调可能已经取消了后一个。
    /// </summary>
    internal int Fire()
    {
        _dueCount = 0;
        for (int i = 0; i < _count; i++)
        {
            ref TimerRec r = ref _recs[i];
            if (!r.Active) continue;
            if (r.Deadline > Now(r.Domain)) continue;
            if (_dueCount == _due.Length) Array.Resize(ref _due, _due.Length * 2);
            _due[_dueCount++] = i;
        }

        SortDue();

        int fired = 0;
        for (int k = 0; k < _dueCount; k++)
        {
            int i = _due[k];
            if (!_recs[i].Active) continue;                       // 被前一个回调取消
            int gen = _recs[i].Gen;
            var handle = new TimerHandle(i, gen);
            Action<TimerHandle>? cb = _recs[i].Callback;

            if (_recs[i].Interval > 0.0)
            {
                // 重排先于回调：回调内 Cancel 自己 = 真取消（否则会被这里的重排复活）
                double next = _recs[i].Deadline + _recs[i].Interval;
                double now = Now(_recs[i].Domain);
                if (next <= now) next = now + _recs[i].Interval;   // 掉帧不补触发，只对齐到下一周期
                _recs[i].Deadline = next;
                cb?.Invoke(handle);
            }
            else
            {
                cb?.Invoke(handle);
                // 一次性：回调后回收。回调内若已 Cancel 自己（代际已变），这里不再动它。
                if (_recs[i].Gen == gen && _recs[i].Active) Release(i);
            }
            fired++;
        }

        LastFired = fired;
        return fired;
    }

    private TimerHandle Arm(float seconds, double interval, TimeDomain domain, Action<TimerHandle> callback)
    {
        if (callback == null)
        {
            UiAssert.That(false, "定时器回调不可为 null");
            return TimerHandle.None;
        }

        int i;
        if (_freeCount > 0)
        {
            i = _free[--_freeCount];
        }
        else
        {
            if (_count == _recs.Length) Array.Resize(ref _recs, _recs.Length * 2);
            i = _count++;
            _recs[i].Gen = 1;
        }

        double delay = seconds > 0f ? seconds : 0.0;
        _recs[i].Domain = domain;
        _recs[i].Deadline = Now(domain) + delay;
        _recs[i].Interval = interval;
        _recs[i].Seq = _seqNext++;
        _recs[i].Callback = callback;
        _recs[i].Active = true;
        _activeCount++;
        return new TimerHandle(i, _recs[i].Gen);
    }

    private void Release(int i)
    {
        _recs[i].Active = false;
        _recs[i].Callback = null;
        _recs[i].Interval = 0.0;
        int g = _recs[i].Gen + 1;
        if (g == 0) g = 1;                                        // 代际跳过 0（0 = 空句柄）
        _recs[i].Gen = g;
        _activeCount--;
        if (_freeCount == _free.Length) Array.Resize(ref _free, _free.Length * 2);
        _free[_freeCount++] = i;
    }

    /// <summary>到期集按 (deadline, 插入序) 双键升序——插入排序，到期集通常个位数。</summary>
    private void SortDue()
    {
        for (int k = 1; k < _dueCount; k++)
        {
            int v = _due[k];
            int j = k - 1;
            while (j >= 0 && Greater(_due[j], v))
            {
                _due[j + 1] = _due[j];
                j--;
            }
            _due[j + 1] = v;
        }
    }

    private bool Greater(int a, int b)
    {
        double da = _recs[a].Deadline, db = _recs[b].Deadline;
        if (da != db) return da > db;
        return _recs[a].Seq > _recs[b].Seq;
    }
}
