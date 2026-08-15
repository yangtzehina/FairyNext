using FairyNext.Contracts;
using FairyNext.Numerics;

namespace FairyNext.Core.Rendering;

/// <summary>
/// transform 槽表（架构「平面三」机制 5：**动效的物理落点**）。
///
/// 一句话职责：把「滚动 / gear 位移 / tween」三种在旧架构各有旁路的动效，收敛成同一件事——
/// 写一个槽矩阵。槽本身进 GPU uniform 数组（GLES 口径 3 vec4/槽），实例经 <c>route.slot</c>
/// 间接引用；于是「滚动一屏 = 写一个 float4」这条成本承诺才有物理依据，而不是一句宣传。
///
/// 四条本类执法的契约：
///  1. **槽 0 恒为 identity**（<see cref="IdentitySlot"/>）。它不是「第一个可用槽」而是哨兵：
///     不骑槽的实例把 <c>route.slot</c> 写 0，shader 无分支地乘一个单位阵。
///     写槽 0 一律拒绝——允许写它等于允许把整条流的坐标系悄悄挪走。
///  2. **写前位等切断**（不变量 16）：同值写不置脏、不进上传区间。零脏帧短路以此为前提，
///     所以切断必须发生在**这里**，不能指望上游每个调用点自觉。比较走 <see cref="BitEquals"/>：
///     +0/-0 判不等（宁可多脏一次），NaN 判相等（免得含 NaN 的矩阵每帧永脏）。
///  3. **axis-aligned 位每次写重判**（不变量 6）：无旋转/斜切才置位，shader 据此 pixel snap。
///     不是建槽时判一次——槽的用途就是被反复改写，一次性判定必然陈旧。
///  4. **溢出走明文阶梯**（不变量 5）：槽荒时 <see cref="Claim"/> 退回槽 0 并记
///     <see cref="DegradeKind.SlotStarvation"/>，调用方据此退 tier-2 原位重写。
///     静默返回 0 会让「容器不动了」变成一个没有出处的 bug。
///
/// 预算口径：<see cref="Abi.TransformSlotBudget"/> = 32 是**条目表长度**（含 identity），
/// 故可认领 31 个热点槽。与 mock 后端同口径——它按下标 ≥ 32 计 slotOverflow，
/// CPU 侧再自造一套「32 个可认领槽」会造出「CPU 认为没超、后端认为超了」的两本账。
/// </summary>
public sealed class SlotTable
{
    /// <summary>identity 槽（法定哨兵，不可认领、不可写）。</summary>
    public const int IdentitySlot = 0;

    private readonly SlotEntry[] _entries;
    private readonly bool[] _live;
    private readonly DegradeLog _degrade;

    private int _count;        // 表长（最高已用下标 + 1）
    private int _liveCount;    // 活槽数（含槽 0）
    private int _highWater;
    private int _dirtyMin = int.MaxValue;
    private int _dirtyMax = -1;

    /// <summary>建槽表（<paramref name="degrade"/> 收槽荒事件；null 时槽荒只计数不上报）。</summary>
    public SlotTable(DegradeLog? degrade = null)
    {
        _entries = new SlotEntry[Abi.TransformSlotBudget];
        _live = new bool[Abi.TransformSlotBudget];
        _degrade = degrade ?? new DegradeLog();
        Reset();
    }

    /// <summary>条目表长度上限（含 identity 槽；= <see cref="Abi.TransformSlotBudget"/>）。</summary>
    public int Budget => Abi.TransformSlotBudget;

    /// <summary>当前表长（= 最高已用下标 + 1；上传区间的上界）。</summary>
    public int Count => _count;

    /// <summary>活槽数（含槽 0）。</summary>
    public int LiveCount => _liveCount;

    /// <summary>历史最高活槽数（诊断高水位——预算要不要动，看它出实据）。</summary>
    public int HighWater => _highWater;

    /// <summary>槽荒次数（<see cref="Claim"/> 撞预算）。</summary>
    public int Starvation { get; private set; }

    /// <summary>被位等切断的写次数（不变量 16 的正面收据）。</summary>
    public int WritesCut { get; private set; }

    /// <summary>真正落盘的写次数。</summary>
    public int WritesApplied { get; private set; }

    /// <summary>脏区间起点（无脏时 &gt; <see cref="DirtyMax"/>）。</summary>
    public int DirtyMin => _dirtyMin;

    /// <summary>脏区间终点（含）。</summary>
    public int DirtyMax => _dirtyMax;

    /// <summary>是否有待上传的槽。</summary>
    public bool HasDirty => _dirtyMax >= _dirtyMin;

    /// <summary>只读槽表视图（<c>[0, Count)</c>；快照与规范化哈希的输入）。</summary>
    public ReadOnlySpan<SlotEntry> Entries => new ReadOnlySpan<SlotEntry>(_entries, 0, _count);

    /// <summary>某槽是否已认领（槽 0 恒为真）。</summary>
    public bool IsLive(int slot) => (uint)slot < (uint)_live.Length && _live[slot];

    /// <summary>取槽条目（越界返回 <see cref="SlotEntry.Identity"/>——越界引用画的是不动的东西，不是垃圾）。</summary>
    public SlotEntry Entry(int slot) =>
        (uint)slot < (uint)_count ? _entries[slot] : SlotEntry.Identity;

    /// <summary>取槽矩阵（架构接缝 <c>SlotMatrix(int) → Affine2D</c>：命中测试合成有效局部变换用）。</summary>
    public Affine2D Matrix(int slot) => Entry(slot).M;

    /// <summary>取槽的 Claim 持有者。</summary>
    public NodeHandle OwnerOf(int slot) => Entry(slot).Owner;

    /// <summary>取写频计数（自动升格/降格侦测的输入；诊断量）。</summary>
    public int WriteFreqOf(int slot) => Entry(slot).WriteFreq;

    /// <summary>
    /// 认领一个槽（架构接缝 <c>AllocSlot(NodeId)</c>）。
    /// 从低下标起复用空位——分配序确定性是规范化哈希能跨运行比较的前提之一
    /// （哈希本身会重编号，但重编号的输入必须先是确定的）。
    /// 槽荒返回 <see cref="IdentitySlot"/> 并走阶梯（该容器退 tier-2 重写）。
    /// </summary>
    /// <param name="owner">持有者（CPU 簿记，不进 GPU）。</param>
    /// <param name="flags">初始标志（<see cref="TransformSlotFlags.Volatile"/> = 作者声明的每帧必变热点）。</param>
    public int Claim(NodeHandle owner, TransformSlotFlags flags = TransformSlotFlags.None)
    {
        for (int i = 1; i < _entries.Length; i++)
        {
            if (_live[i]) continue;
            _live[i] = true;
            _entries[i] = new SlotEntry
            {
                M = Affine2D.Identity,
                Owner = owner,
                Flags = flags | TransformSlotFlags.AxisAligned,   // identity 天然轴对齐
                WriteFreq = 0,
            };
            if (i + 1 > _count) _count = i + 1;
            _liveCount++;
            if (_liveCount > _highWater) _highWater = _liveCount;
            MarkDirty(i);
            return i;
        }

        Starvation++;
        _degrade.Report(DegradeKind.SlotStarvation,
            $"槽荒：{owner} 认领失败（预算 {Budget}，活槽 {_liveCount}）——该容器退 tier-2 原位重写");
        return IdentitySlot;
    }

    /// <summary>
    /// 归还槽。槽 0 不可归还；归还后矩阵复位为 identity 并进脏区间——
    /// 复用者拿到的必须是一个确定的初值，而不是上一任的位移。
    /// </summary>
    public bool Release(int slot)
    {
        if (slot == IdentitySlot)
        {
            UiAssert.That(false, "槽 0 是 identity 哨兵，不可归还");
            return false;
        }
        if (!IsLive(slot)) return false;
        _live[slot] = false;
        _entries[slot] = SlotEntry.Identity;
        _liveCount--;
        MarkDirty(slot);
        return true;
    }

    /// <summary>
    /// 写槽矩阵（架构接缝 <c>WriteSlot(int, in Affine2D)</c>）。
    /// 返回 <c>true</c> ⇔ 值真的变了（并因此进了脏区间）。同值（位等）返回 <c>false</c> 且不置脏。
    /// </summary>
    public bool Write(int slot, in Affine2D m)
    {
        if (slot == IdentitySlot)
        {
            UiAssert.That(false, "槽 0 恒为 identity，不可写——写它等于把整条流的坐标系挪走");
            return false;
        }
        if (!IsLive(slot))
        {
            UiAssert.That(false, $"写未认领的槽 {slot}（先 Claim 再 Write）");
            return false;
        }

        ref SlotEntry e = ref _entries[slot];
        if (BitEquals.Eq(e.M.m00, m.m00) && BitEquals.Eq(e.M.m01, m.m01) &&
            BitEquals.Eq(e.M.m10, m.m10) && BitEquals.Eq(e.M.m11, m.m11) &&
            BitEquals.Eq(e.M.tx, m.tx) && BitEquals.Eq(e.M.ty, m.ty))
        {
            WritesCut++;
            return false;
        }

        e.M = m;
        // 不变量 6：轴对齐位在**每次写**时重判——槽的用途就是被反复改写。
        bool axisAligned = m.m01 == 0f && m.m10 == 0f;
        e.Flags = axisAligned
            ? e.Flags | TransformSlotFlags.AxisAligned
            : e.Flags & ~TransformSlotFlags.AxisAligned;
        if (e.WriteFreq < ushort.MaxValue) e.WriteFreq++;
        WritesApplied++;
        MarkDirty(slot);
        return true;
    }

    /// <summary>
    /// 声明/撤销 volatile（作者对「每帧必变」节点的先验）。
    /// 与自动升格是**组合而非替代**：volatile 跳过脏账与升格侦测直接常驻热路径，
    /// 滥用由「volatile 实际未变帧占比」诊断暴露（不变量 19，M3 的趋势门）。
    /// </summary>
    public void DeclareVolatile(int slot, bool value)
    {
        if (!IsLive(slot) || slot == IdentitySlot)
        {
            UiAssert.That(false, $"DeclareVolatile 于非活槽 {slot}");
            return;
        }
        ref SlotEntry e = ref _entries[slot];
        TransformSlotFlags next = value
            ? e.Flags | TransformSlotFlags.Volatile
            : e.Flags & ~TransformSlotFlags.Volatile;
        if (next == e.Flags) return;
        e.Flags = next;
        MarkDirty(slot);
    }

    /// <summary>
    /// 写频是否够格自动升格（fork 已验证的热提升：首动标热、重编入槽）。
    /// 判据留在表上，**决策留给调用方**——升格要重编叶，那是 Structure 通道的事。
    /// </summary>
    public bool IsHot(int slot, int threshold = 2) =>
        (Entry(slot).Flags & TransformSlotFlags.Volatile) != 0 || Entry(slot).WriteFreq >= threshold;

    /// <summary>帧末清写频（升格侦测按帧窗口计；不清则任何槽迟早都「热」）。</summary>
    public void ResetWriteFrequencies()
    {
        for (int i = 1; i < _count; i++) _entries[i].WriteFreq = 0;
    }

    /// <summary>清脏区间（提交后调）。</summary>
    public void ClearDirty()
    {
        _dirtyMin = int.MaxValue;
        _dirtyMax = -1;
    }

    /// <summary>把整表标脏（换后端 / 新建后端流之后：GPU 侧是空的，本地的每一条都得重发）。</summary>
    public void MarkAllDirty()
    {
        if (_count == 0) return;
        _dirtyMin = 0;
        _dirtyMax = _count - 1;
    }

    /// <summary>清表（槽 0 复位为 identity，其余归还）。水位与计数不清——它们是会话量。</summary>
    public void Reset()
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            _entries[i] = SlotEntry.Identity;
            _live[i] = false;
        }
        _live[IdentitySlot] = true;
        _count = 1;
        _liveCount = 1;
        if (_highWater < 1) _highWater = 1;
        _dirtyMin = 0;
        _dirtyMax = 0;
    }

    private void MarkDirty(int slot)
    {
        if (slot < _dirtyMin) _dirtyMin = slot;
        if (slot > _dirtyMax) _dirtyMax = slot;
    }
}
