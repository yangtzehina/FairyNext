using FairyNext.Numerics;

namespace FairyNext.Core.Events;

/// <summary>
/// 一个触点的状态（架构平面四 B「触摸状态机：固定 10 槽，零分配」）。
///
/// 两条快照是这台状态机的全部聪明处：
///  · <b>downChain</b>——TouchBegin 时把 target 整条父链的**句柄**写下来。松开时
///    <see cref="ClickTest"/> 优先取 <c>downChain[0]</c>，代际验证即「仍在台上」，
///    O(1) 替代旧架构的 stage 归属扫描；失效才沿当前 target 父链找与 downChain 的交集。
///    按钮缩放动画（按下时目标会缩小，抬起时指针已不在它上面）与列表滚动误触两个经典问题
///    的答案原样保留。
///  · <b>monitors</b>——<c>ctx.CaptureTouch()</c> 的注册者。此后 move/end 逐个作为**附加目标**
///    收到（离开宿主仍收——手势能成立的关键），且**只收自身相、不冒泡**。
///
/// 全部存句柄不存引用：帧中被 Destroy 的节点解引用必失败（gen + DEAD 双验），
/// 不需要任何「派发前清理悬挂引用」的防御代码。
/// </summary>
public struct TouchSlot
{
    /// <summary>触点 id（<see cref="InputPacket.Id"/>）。</summary>
    public int TouchId;
    /// <summary>本槽是否在用。</summary>
    public bool Active;
    /// <summary>是否处于按下期（Begin 与 End 之间）。</summary>
    public bool Began;
    /// <summary>键号（0 左 / 1 右 / 2 中）。</summary>
    public byte Button;

    /// <summary>当前 stage 坐标。</summary>
    public Vector2 Pos;
    /// <summary>按下时的 stage 坐标。</summary>
    public Vector2 DownPos;
    /// <summary>按下时刻（宿主时间戳，秒）。</summary>
    public double DownTime;
    /// <summary>最近一次事件时刻。</summary>
    public double Time;

    /// <summary>当前命中目标。</summary>
    public NodeHandle Target;
    /// <summary>上一次派发过 RollOver 的目标（链 diff 的旧半边）。</summary>
    public NodeHandle LastRollOver;

    /// <summary>上一次成功点击的时刻（双击四元组之一）。</summary>
    public double LastClickTime;
    /// <summary>上一次成功点击的位置。</summary>
    public Vector2 LastClickPos;
    /// <summary>上一次成功点击的键号。</summary>
    public byte LastClickButton;
    /// <summary>连击计数（1 = 单击，2 = 双击；第三下回 1，与 fork 同）。</summary>
    public byte ClickCount;
    /// <summary>本次按下是否已判定为「不是点击」。</summary>
    public bool ClickCancelled;

    internal NodeHandle[] DownChain;
    internal int DownChainLen;
    internal NodeHandle[] Monitors;
    internal int MonitorLen;

    /// <summary>按下期的父链快照长度（诊断）。</summary>
    public int DownChainLength => DownChainLen;

    /// <summary>已注册的 monitor 数。</summary>
    public int MonitorCount => MonitorLen;

    /// <summary>读第 i 个 monitor（越界返回 None）。</summary>
    public NodeHandle MonitorAt(int i) =>
        (uint)i < (uint)MonitorLen ? Monitors[i] : NodeHandle.None;

    /// <summary>读 downChain 的第 i 条（0 = 按下时的 target）。</summary>
    public NodeHandle DownChainAt(int i) =>
        (uint)i < (uint)DownChainLen ? DownChain[i] : NodeHandle.None;

    internal void Reset(int touchId)
    {
        TouchId = touchId;
        Active = true;
        Began = false;
        Button = 0;
        Pos = default;
        DownPos = default;
        DownTime = 0.0;
        Time = 0.0;
        Target = NodeHandle.None;
        LastRollOver = NodeHandle.None;
        LastClickTime = 0.0;
        LastClickPos = default;
        LastClickButton = 0;
        ClickCount = 0;
        ClickCancelled = false;
        DownChainLen = 0;
        MonitorLen = 0;
    }

    /// <summary>按下：写下 target 整条父链的句柄快照。</summary>
    internal void CaptureDownChain(NodeTable table, NodeHandle target)
    {
        DownChainLen = 0;
        if (DownChain == null) DownChain = new NodeHandle[16];
        for (NodeHandle h = target; !h.IsNone; h = table.Parent(h))
        {
            if (DownChainLen == DownChain.Length) Array.Resize(ref DownChain, DownChain.Length * 2);
            DownChain[DownChainLen++] = h;
        }
    }

    /// <summary>注册一个 monitor（重复注册幂等）。</summary>
    internal void AddMonitor(NodeHandle node)
    {
        if (node.IsNone) return;
        if (Monitors == null) Monitors = new NodeHandle[4];
        for (int i = 0; i < MonitorLen; i++) if (Monitors[i].Equals(node)) return;
        if (MonitorLen == Monitors.Length) Array.Resize(ref Monitors, Monitors.Length * 2);
        Monitors[MonitorLen++] = node;
    }

    internal void ClearMonitors() => MonitorLen = 0;

    /// <summary>
    /// 点击目标判定（fork <c>TouchInfo.ClickTest</c> 同语义，@ oracle 08a2d56 Stage.cs 1802-1827）：
    /// ① 取消即无点击；② <c>downChain[0]</c> 仍解引用得到就用它（即便指针已偏离——
    /// 这是按钮缩放动画能正常点中的原因）；③ 否则沿当前 target 父链找与 downChain 的第一个交集。
    /// </summary>
    internal NodeHandle ClickTest(NodeTable table)
    {
        if (ClickCancelled || DownChainLen == 0) return NodeHandle.None;

        NodeHandle first = DownChain[0];
        if (table.IsAlive(first)) return first;

        for (NodeHandle h = Target; !h.IsNone; h = table.Parent(h))
        {
            for (int i = 0; i < DownChainLen; i++)
            {
                if (!DownChain[i].Equals(h)) continue;
                return table.IsAlive(h) ? h : NodeHandle.None;
            }
        }
        return NodeHandle.None;
    }
}
