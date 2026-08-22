using System.Collections.Generic;

namespace FairyNext.Core.Fgb;

/// <summary>
/// 实例池雏形（架构 <c>Instantiator.GetPool(DefId)</c> / <c>Pool.Rent/Return</c> 的 M1 形态）。
/// **池按模板分桶**，而复用发生在 <see cref="NodeTable"/> 的 slab 里而不是这里——
/// 池自己不留对象缓存，它做的是三件事：
///  ① <see cref="Return"/> = 标记即死（段进 P9 回收队列），句柄立刻失效；
///  ② <see cref="Rent"/> = 走同一条 <see cref="FgbInstantiator.Instantiate"/>（**唯一创建路径**），
///     段从模板 bin 的空闲栈里取回，全部列重新 memcpy；
///  ③ 记账：<see cref="SlabReuses"/> 是「这次租到的段首曾经被还回来过」的次数——
///     GList 同模板复用的命中率就看它。
///
/// **为什么不缓存实例对象**：缓存等于让「上一次的状态」有机会活到下一次。
/// 走 Instantiate 意味着每次租用的 21 根列都是从模板 memcpy 出来的，
/// 「复用后有残留」在结构上不可能——那是本设计愿意为之付一次 <c>Array.Copy</c> 的性质。
/// 归还与租用之间必须隔一次 P9（<see cref="NodeTable.EndFrame"/>）：句柄换代与回段都在那里，
/// 这条纪律是「同帧安全」的来源，池不许绕过它去抢一个还没换代的段。
/// </summary>
public sealed class FgbInstancePool
{
    private readonly FgbInstantiator _instantiator;
    private readonly Dictionary<int, HashSet<uint>> _retired = new Dictionary<int, HashSet<uint>>();

    /// <summary>建池。</summary>
    public FgbInstancePool(FgbInstantiator instantiator)
    {
        _instantiator = instantiator ?? throw new ArgumentNullException(nameof(instantiator));
    }

    /// <summary>租用次数。</summary>
    public int Rents { get; private set; }

    /// <summary>归还次数。</summary>
    public int Returns { get; private set; }

    /// <summary>租到了曾经归还过的段首的次数（slab 命中）。</summary>
    public int SlabReuses { get; private set; }

    /// <summary>租一个实例（唯一创建路径：虚拟列表与 GList 的实体化都必须走这里）。</summary>
    public FgbInstance Rent(int compIndex, NodeHandle parent = default)
    {
        FgbInstance inst = _instantiator.Instantiate(compIndex, parent);
        Rents++;
        if (_retired.TryGetValue(compIndex, out HashSet<uint>? bases) && bases.Remove(inst.Segment.Base))
            SlabReuses++;
        return inst;
    }

    /// <summary>归还（标记即死；段在 P9 回 slab，那之后才可能被 <see cref="Rent"/> 租到）。</summary>
    public void Return(FgbInstance inst)
    {
        if (inst == null) throw new ArgumentNullException(nameof(inst));
        uint b = inst.Segment.Base;
        int comp = inst.CompIndex;
        // 嵌套块各自回各自模板的 bin：它们的段首也要记，否则嵌套模板的命中率报不出来。
        RecordRetired(inst);
        _instantiator.Destroy(inst);
        Returns++;
        if (!_retired.TryGetValue(comp, out HashSet<uint>? set))
        {
            set = new HashSet<uint>();
            _retired.Add(comp, set);
        }
        set.Add(b);
    }

    private void RecordRetired(FgbInstance inst)
    {
        for (int i = 0; i < inst.Nested.Count; i++)
        {
            FgbInstance kid = inst.Nested[i];
            RecordRetired(kid);
            if (!_retired.TryGetValue(kid.CompIndex, out HashSet<uint>? set))
            {
                set = new HashSet<uint>();
                _retired.Add(kid.CompIndex, set);
            }
            set.Add(kid.Segment.Base);
        }
    }
}
