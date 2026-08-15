using FairyNext.Contracts;
using FairyNext.Numerics;

namespace FairyNext.Core.Rendering;

/// <summary>裁剪域条目的共享形态（架构机制 6：<c>share[]</c> 那一列）。</summary>
public enum ClipShareKind : byte
{
    /// <summary>哨兵条目 0（不裁剪）。</summary>
    None = 0,
    /// <summary>别名：本条目的语义 = 它 <see cref="ClipShare.Parent"/> 的语义（挂载/拼接期用）。</summary>
    Inherited = 1,
    /// <summary>自有：裁剪参数与父域异值，独占一个条目。</summary>
    Owned = 2,
}

/// <summary>
/// 一个条目的 CPU 簿记（<see cref="ClipEntry"/> 是它的 GPU 半边）。
/// <see cref="Parent"/> 是**外层裁剪域**：Owned 条目记它折叠自谁（诊断/重推用），
/// Inherited 条目记它别名到谁（<see cref="ClipBook.Resolve"/> 沿此链走到 Owned，不变量 8）。
/// </summary>
public struct ClipShare
{
    /// <summary>共享形态。</summary>
    public ClipShareKind Kind;
    /// <summary>外层域 / 别名目标条目下标。</summary>
    public int Parent;
    /// <summary>rect 所在的帧（= 绑定的 transform 槽）。</summary>
    public int Slot;
    /// <summary>域的持有者（诊断）。</summary>
    public NodeHandle Owner;
}

/// <summary>
/// 裁剪域的参数（<see cref="ClipBook.Push"/> 的输入）。
/// <see cref="Rect"/> 是 <c>xMin,yMin,xMax,yMax</c>（**outer**，与 ABI 的 ClipEntry.rect 同形），
/// 表达在 <see cref="Slot"/> 这个槽的本地帧里。
/// <see cref="Radii"/> 的分量序在本仓库固定为 <c>x=左上, y=右上, z=右下, w=左下</c>（CSS 序）——
/// ABI 只说「四角半径」，序必须有个唯一定义点，否则 CPU 与 shader 会各转各的角。
/// </summary>
public struct ClipParams
{
    /// <summary>槽本地 xMin,yMin,xMax,yMax（outer）。</summary>
    public Vector4 Rect;
    /// <summary>软边梯度（x = 水平，y = 垂直）。</summary>
    public Vector2 Soft;
    /// <summary>四角半径（x=左上 y=右上 z=右下 w=左下）。</summary>
    public Vector4 Radii;
    /// <summary>rect 所在的帧（绑定的 transform 槽）。</summary>
    public int Slot;

    /// <summary>直角、无软边的矩形域。</summary>
    public static ClipParams Rectangle(float xMin, float yMin, float xMax, float yMax, int slot = 0) =>
        new ClipParams
        {
            Rect = new Vector4(xMin, yMin, xMax, yMax),
            Soft = Vector2.zero,
            Radii = Vector4.zero,
            Slot = slot,
        };
}

/// <summary>
/// 裁剪域表（架构「平面三」机制 6：**继承共享 + 包含剪枝**）。
///
/// 三件事，每件都直接对着一条预算或一条不变量：
///
/// **1 · 继承共享**（预算按裁剪域数计，不按节点数计）。子节点默认 <c>Inherited</c>——
/// 不声明自己的裁剪参数就直接引用父条目的 id，**零分配**（<see cref="Inherit"/>）；
/// 参数与父域异值才 <c>Owned</c> 自有一条（<see cref="Push"/>）。
/// 于是「一个滚动列表里 500 个 item 各带一堆子节点」只吃掉 1 条预算，而不是 500 条。
/// 同参数的兄弟域还会被去重命中同一条目——预算真正数的是**几种裁剪矩形**。
///
/// **2 · 折叠**。Owned 条目的 rect = 自身 rect ∩ 外层 rect（与 <c>UpdateContext.EnterClipping</c> 同语义）。
/// 折叠让一个实例只需引用**一个** clipIndex，shader 里没有裁剪栈；代价是折叠发生在 Push 的那一刻，
/// 外层窗口后来变了要靠 Structure 通道重编——这与「无失效通道的紧盒子必然陈旧」是同一条纪律的正面用法：
/// 折叠结果有推送渠道，紧 AABB 没有。
/// 折叠只在**同帧**（同 slot）之间成立：跨帧折叠要两槽的相对变换，那是 Extract 持有 world 列时才有的信息，
/// 所以跨帧调用是 debug 断言 + <see cref="CrossFrameFolds"/> 计数，rect 原样保留而不是偷偷算错。
///
/// **3 · inner rect 包含剪枝**（不变量 7）。每条目维护一个**内含矩形**：完全落在它里面的 quad
/// 一定完全在裁剪域内，于是 P6 可以直接把 clipIndex 摘成 0、shader 一次采样都不做
/// （WebRender outer/inner 同型）。绝大多数 quad 落在域内，逐像素测裁剪是纯浪费。
/// inner 由 outer 按「每边的最大圆角半径 + 软边宽度」内缩得到——保守但绝对安全：
/// 圆角切掉的区域恒在 <c>[边, 边+r]</c> 的角块内，内缩 r 之后不可能碰到。
///
/// 预算口径：<see cref="Abi.ClipEntryBudget"/> = 16 是**条目表长度**（含哨兵 0），
/// 故可容纳 15 个自有域。与 mock 后端同口径（它按 UploadClips 的条目数执法），
/// CPU 侧另立「16 个可分配域」会造出两本账。
/// 溢出阶梯：新域退回**父窗口**（正确但更粗：子只被外层窗口裁），记
/// <see cref="DegradeKind.ClipStarvation"/> + 高水位。
/// </summary>
public sealed class ClipBook
{
    /// <summary>哨兵条目：不裁剪（shader 对其零采样）。</summary>
    public const int NoneEntry = 0;

    private readonly ClipEntry[] _entries;
    private readonly ClipShare[] _shares;
    private readonly Vector4[] _inner;      // xMin,yMin,xMax,yMax（内含矩形）
    private readonly DegradeLog _degrade;

    private int _count;
    private int _highWater;
    private int _dirtyMin = int.MaxValue;
    private int _dirtyMax = -1;

    /// <summary>建裁剪域表（<paramref name="degrade"/> 收超预算事件）。</summary>
    public ClipBook(DegradeLog? degrade = null)
    {
        _entries = new ClipEntry[Abi.ClipEntryBudget];
        _shares = new ClipShare[Abi.ClipEntryBudget];
        _inner = new Vector4[Abi.ClipEntryBudget];
        _degrade = degrade ?? new DegradeLog();
        Reset();
    }

    /// <summary>条目表长度上限（含哨兵；= <see cref="Abi.ClipEntryBudget"/>）。</summary>
    public int Budget => Abi.ClipEntryBudget;

    /// <summary>可分配的自有域上限（= <see cref="Budget"/> - 1，哨兵占一条）。</summary>
    public int MaxDomains => Abi.ClipEntryBudget - 1;

    /// <summary>条目数（含哨兵 0）。</summary>
    public int Count => _count;

    /// <summary>裁剪域数（预算口径：不含哨兵）。</summary>
    public int DomainCount => _count - 1;

    /// <summary>裁剪域数的历史高水位。</summary>
    public int HighWater => _highWater;

    /// <summary>超预算次数。</summary>
    public int Starvation { get; private set; }

    /// <summary>「子引用父条目」的次数（继承共享省下的条目数）。</summary>
    public int InheritedShares { get; private set; }

    /// <summary>去重命中次数（同参数的域复用同一条目）。</summary>
    public int DedupeHits { get; private set; }

    /// <summary>跨帧折叠请求次数（协议违约的可计数形态；正常为 0）。</summary>
    public int CrossFrameFolds { get; private set; }

    /// <summary>包含剪枝摘除 clipIndex 的次数。</summary>
    public int Pruned { get; private set; }

    /// <summary>脏区间起点。</summary>
    public int DirtyMin => _dirtyMin;

    /// <summary>脏区间终点（含）。</summary>
    public int DirtyMax => _dirtyMax;

    /// <summary>是否有待上传的条目。</summary>
    public bool HasDirty => _dirtyMax >= _dirtyMin;

    /// <summary>只读条目表（<c>[0, Count)</c>）。</summary>
    public ReadOnlySpan<ClipEntry> Entries => new ReadOnlySpan<ClipEntry>(_entries, 0, _count);

    /// <summary>取条目（越界返回哨兵内容）。</summary>
    public ClipEntry Entry(int id) => (uint)id < (uint)_count ? _entries[id] : default;

    /// <summary>取条目的 CPU 簿记。</summary>
    public ClipShare Share(int id) => (uint)id < (uint)_count ? _shares[id] : default;

    /// <summary>取条目的内含矩形（xMin,yMin,xMax,yMax）。</summary>
    public Vector4 Inner(int id) => (uint)id < (uint)_count ? _inner[id] : default;

    /// <summary>
    /// 继承：子节点没有自己的裁剪参数 ⇒ 直接用父域的条目 id，**不分配**。
    /// 这是深嵌套列表里最常走的一条路——把它写成一次函数调用而不是「什么都不做」，
    /// 是为了让 <see cref="InheritedShares"/> 能出示「继承共享省了多少条预算」的实据。
    /// </summary>
    public int Inherit(int parent)
    {
        if ((uint)parent >= (uint)_count)
        {
            UiAssert.That(false, $"Inherit 收到越界的父条目 {parent}");
            return NoneEntry;
        }
        InheritedShares++;
        return parent;
    }

    /// <summary>
    /// 别名：分配一个 <see cref="ClipShareKind.Inherited"/> 条目指向 <paramref name="parent"/>，
    /// 内容为父条目的当前解析结果。给 blob 拼接（M1-22）用——预编译的 quad 引用的是 blob 内的
    /// 条目编号，挂载时需要一个真实条目承接，而它的语义必须跟着挂载点的父域走。
    /// <see cref="Resolve"/> 沿链走到 Owned 并带环检测（不变量 8）。
    /// </summary>
    public int Alias(int parent, NodeHandle owner = default)
    {
        if ((uint)parent >= (uint)_count)
        {
            UiAssert.That(false, $"Alias 收到越界的父条目 {parent}");
            return NoneEntry;
        }
        int id = Allocate();
        if (id < 0)
        {
            Starve(parent, "别名条目");
            return parent;
        }
        int resolved = Resolve(parent);
        _entries[id] = _entries[resolved];
        _inner[id] = _inner[resolved];
        _shares[id] = new ClipShare
        {
            Kind = ClipShareKind.Inherited,
            Parent = parent,
            Slot = _shares[resolved].Slot,
            Owner = owner,
        };
        MarkDirty(id);
        return id;
    }

    /// <summary>
    /// 沿 Inherited 链解析到 Owned 条目（不变量 8：链无环且终于 Owned）。
    /// 环在 debug 下断言、在 release 下返回哨兵——**画不出东西**好过无限循环。
    /// </summary>
    public int Resolve(int id)
    {
        int cur = id;
        for (int guard = 0; guard <= _count; guard++)
        {
            if ((uint)cur >= (uint)_count) return NoneEntry;
            if (_shares[cur].Kind != ClipShareKind.Inherited) return cur;
            cur = _shares[cur].Parent;
        }
        UiAssert.That(false, $"ClipEntry 继承链成环（自条目 {id} 起）——不变量 8");
        return NoneEntry;
    }

    /// <summary>
    /// 注册一个自有裁剪域：与外层 rect 折叠、与既有条目去重，都不中才分配新条目。
    /// 返回可直接写进 <c>route.clipIndex</c> 的条目下标。
    /// </summary>
    /// <param name="parent">外层域条目（0 = 无外层）。</param>
    /// <param name="p">本域参数（rect 表达在 <see cref="ClipParams.Slot"/> 的帧里）。</param>
    /// <param name="owner">持有者（诊断）。</param>
    /// <param name="degraded">true = 撞了预算，返回值是**父窗口**而不是本域。</param>
    public int Push(int parent, in ClipParams p, NodeHandle owner, out bool degraded)
    {
        degraded = false;
        if ((uint)parent >= (uint)_count)
        {
            UiAssert.That(false, $"Push 收到越界的父条目 {parent}");
            parent = NoneEntry;
        }

        int parentId = Resolve(parent);
        Vector4 rect = p.Rect;
        if (parentId != NoneEntry)
        {
            if (_shares[parentId].Slot != p.Slot)
            {
                // 跨帧折叠需要两槽的相对变换；本表拿不到那个信息，于是不假装能算。
                CrossFrameFolds++;
                UiAssert.That(false,
                    $"跨帧折叠：子域在槽 {p.Slot}，外层域在槽 {_shares[parentId].Slot}——" +
                    "调用方须先把子 rect 换算到外层帧（Extract 持有 world 列时才有这个信息）");
            }
            else
            {
                Vector4 outer = _entries[parentId].Rect;
                rect = new Vector4(
                    MathF.Max(rect.x, outer.x), MathF.Max(rect.y, outer.y),
                    MathF.Min(rect.z, outer.z), MathF.Min(rect.w, outer.w));
            }
        }

        // 去重：同 rect / soft / radii / slot 的域就是同一个域，无论它们挂在树的哪里。
        for (int i = 1; i < _count; i++)
        {
            if (_shares[i].Kind != ClipShareKind.Owned) continue;
            ClipEntry e = _entries[i];
            if (e.Rect == rect && e.Soft == p.Soft && e.Radii == p.Radii && (int)e.Slot == p.Slot)
            {
                DedupeHits++;
                return i;
            }
        }

        int id = Allocate();
        if (id < 0)
        {
            degraded = true;
            Starve(parent, owner.IsNone ? "裁剪域" : owner.ToString());
            return parent;   // 父窗口降级：正确但更粗
        }

        _entries[id] = new ClipEntry
        {
            Rect = rect,
            Soft = p.Soft,
            Radii = p.Radii,
            Slot = (uint)p.Slot,
            Pad = 0,
        };
        _shares[id] = new ClipShare
        {
            Kind = ClipShareKind.Owned,
            Parent = parent,
            Slot = p.Slot,
            Owner = owner,
        };
        _inner[id] = ComputeInner(in _entries[id]);
        MarkDirty(id);
        if (DomainCount > _highWater) _highWater = DomainCount;
        return id;
    }

    /// <summary>
    /// 推一条已存在的 Owned 条目的 rect（外层窗口在不重编的前提下移动时的推送式重推）。
    /// **不重推它的子域**——子域的 rect 是 Push 时刻的折叠快照，外层真的变形要走 Structure 通道整编。
    /// 同值不置脏（比较走位等，同 <see cref="SlotTable.Write"/> 的纪律）。
    /// </summary>
    public bool SetRect(int id, in Vector4 rect)
    {
        if ((uint)id >= (uint)_count || _shares[id].Kind != ClipShareKind.Owned)
        {
            UiAssert.That(false, $"SetRect 于非自有条目 {id}");
            return false;
        }
        ref ClipEntry e = ref _entries[id];
        if (BitEquals.Eq(e.Rect.x, rect.x) && BitEquals.Eq(e.Rect.y, rect.y) &&
            BitEquals.Eq(e.Rect.z, rect.z) && BitEquals.Eq(e.Rect.w, rect.w))
            return false;
        e.Rect = rect;
        _inner[id] = ComputeInner(in e);
        MarkDirty(id);
        return true;
    }

    /// <summary>
    /// 槽动了：绑定该槽的条目进脏账（架构机制 3「写 slots[i] 一项并重推绑定该槽的 ClipEntry」）。
    ///
    /// 在「rect 表达在绑定槽的本地帧」这条 ABI 下，重推的数值通常是恒等的——条目跟着它的槽一起动。
    /// 保留这条边的意义在**触发点唯一**：真实后端（M1-15）可能把 rect 预乘进 uniform 块，
    /// 那时必须有人告诉它「这条要重发」。让触发点缺席、日后再补，就是 fork 那种「窗口对不上」的坑。
    /// 返回被标脏的条目数。
    /// </summary>
    public int MarkSlotMoved(int slot)
    {
        int n = 0;
        for (int i = 1; i < _count; i++)
        {
            if ((int)_entries[i].Slot != slot) continue;
            MarkDirty(i);
            n++;
        }
        return n;
    }

    /// <summary>
    /// 包含剪枝（不变量 7）：quad 的 AABB 完全落在条目的**内含矩形**里 ⇒ 可以摘掉 clipIndex。
    /// 两个安全边：
    ///  · 条目 0（不裁剪）无可剪；
    ///  · quad 与条目**必须同帧**（同 slot）。跨帧比较两个矩形是在拿苹果比橘子，
    ///    而且槽一动结论就废——同帧限制让剪枝结论对槽的移动天然免疫。
    /// </summary>
    /// <param name="id">条目下标。</param>
    /// <param name="quadRect">quad 的 <c>rect</c>（xy = min 角，zw = size）。</param>
    /// <param name="quadSlot">quad 骑的槽。</param>
    public bool CanPrune(int id, in Vector4 quadRect, int quadSlot)
    {
        if (id == NoneEntry || (uint)id >= (uint)_count) return false;
        int resolved = Resolve(id);
        if (resolved == NoneEntry) return false;
        if ((int)_entries[resolved].Slot != quadSlot) return false;

        Vector4 inner = _inner[resolved];
        if (!(inner.z > inner.x) || !(inner.w > inner.y)) return false;   // 空内含矩形（也吃掉 NaN）

        float x0 = quadRect.x, y0 = quadRect.y;
        float x1 = x0 + quadRect.z, y1 = y0 + quadRect.w;
        return x0 >= inner.x && y0 >= inner.y && x1 <= inner.z && y1 <= inner.w;
    }

    /// <summary>记一次剪枝命中（计数归本表，动作归流——流才知道要改哪个实例的 route）。</summary>
    public void CountPrune() => Pruned++;

    /// <summary>清脏区间（提交后调）。</summary>
    public void ClearDirty()
    {
        _dirtyMin = int.MaxValue;
        _dirtyMax = -1;
    }

    /// <summary>把整表标脏（新建后端流之后）。</summary>
    public void MarkAllDirty()
    {
        _dirtyMin = 0;
        _dirtyMax = _count - 1;
    }

    /// <summary>
    /// 清表，只留哨兵条目 0。裁剪域是树遍历的派生物——面板整流重编会重新 Push 出来，
    /// 留着旧条目只会让预算虚高。水位与计数不清（会话量）。
    /// </summary>
    public void Reset()
    {
        _entries[NoneEntry] = default;
        _shares[NoneEntry] = new ClipShare { Kind = ClipShareKind.None, Parent = NoneEntry, Slot = 0 };
        _inner[NoneEntry] = default;
        _count = 1;
        _dirtyMin = 0;
        _dirtyMax = 0;
    }

    private int Allocate()
    {
        if (_count >= _entries.Length) return -1;
        return _count++;
    }

    private void Starve(int parent, string who)
    {
        Starvation++;
        _degrade.Report(DegradeKind.ClipStarvation,
            $"裁剪域超预算（{MaxDomains} 域已满）：{who} 退回父窗口 #{parent} 裁剪");
    }

    private void MarkDirty(int id)
    {
        if (id < _dirtyMin) _dirtyMin = id;
        if (id > _dirtyMax) _dirtyMax = id;
    }

    /// <summary>
    /// 内含矩形 = outer 按「每边最大圆角半径 + 该轴软边宽度」内缩。
    /// 保守（圆角只吃掉角块，整边内缩多剪了一点）但绝对安全：宁可少剪一批 quad，
    /// 也不能剪掉一个其实压在圆角或软边上的 quad——那是画错，而画错这一级本设计不留。
    /// </summary>
    private static Vector4 ComputeInner(in ClipEntry e)
    {
        float rTL = e.Radii.x, rTR = e.Radii.y, rBR = e.Radii.z, rBL = e.Radii.w;
        float left = MathF.Max(rTL, rBL) + MathF.Max(e.Soft.x, 0f);
        float right = MathF.Max(rTR, rBR) + MathF.Max(e.Soft.x, 0f);
        float top = MathF.Max(rTL, rTR) + MathF.Max(e.Soft.y, 0f);
        float bottom = MathF.Max(rBL, rBR) + MathF.Max(e.Soft.y, 0f);
        return new Vector4(e.Rect.x + left, e.Rect.y + top, e.Rect.z - right, e.Rect.w - bottom);
    }
}
