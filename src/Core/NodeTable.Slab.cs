namespace FairyNext.Core;

/// <summary>
/// 节点段 = SoA 全局表内的连续区间 [Base, Base+Count)（机制⑦）。
/// 段在实例存活期内**地址稳定**（不变量 12：无压缩承诺），因而句柄下标可长期持有。
/// </summary>
public readonly struct NodeSegment : IEquatable<NodeSegment>
{
    /// <summary>模板 id（终态为 32 位 DefId = {pkg:u16, comp:u16}；本包只作 slab bin 键）。</summary>
    public readonly uint TemplateId;
    /// <summary>段首下标（= 机制⑩ 的 nodeBase）。</summary>
    public readonly uint Base;
    /// <summary>段内节点数（同模板恒定）。</summary>
    public readonly int Count;

    /// <summary>构造一个段描述。</summary>
    public NodeSegment(uint templateId, uint baseIndex, int count)
    {
        TemplateId = templateId;
        Base = baseIndex;
        Count = count;
    }

    /// <summary>是否为空段。</summary>
    public bool IsEmpty => Count == 0;

    /// <inheritdoc/>
    public bool Equals(NodeSegment other) =>
        TemplateId == other.TemplateId && Base == other.Base && Count == other.Count;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is NodeSegment s && Equals(s);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(TemplateId, Base, Count);

    /// <inheritdoc/>
    public override string ToString() => $"seg(tpl={TemplateId}, [{Base},{Base + (uint)Count}))";
}

public sealed partial class NodeTable
{
    /// <summary>
    /// 分配一个节点段（机制⑦：每模板一个空闲栈 + bump 顶端）。
    /// 段内节点已初始化为**活**节点，localId 预置为段内偏移（M1-22 的 NODE 段 memcpy 会覆写为模板值，
    /// 二者必须一致——不一致即模板 localId 映射有 bug，<see cref="ChildByLocalId"/> 的断言会抓到）。
    /// </summary>
    /// <param name="templateId">模板 id（同一模板的段长必须恒定，第一次分配时定档）。</param>
    /// <param name="count">段长（节点数）。</param>
    public NodeSegment AllocSegment(uint templateId, int count)
    {
        UiAssert.That(count > 0, "AllocSegment 段长必须为正");
        if (count <= 0) return default;
        uint baseIdx = _slab.AllocSegment(templateId, count);
        for (int k = 0; k < count; k++)
            InitSlot(baseIdx + (uint)k, NodeType.Component, (ushort)k, 0);
        return new NodeSegment(templateId, baseIdx, count);
    }

    /// <summary>
    /// 立即归还一个节点段（段内必须全 DEAD）。常规路径是 <see cref="DestroySegment"/> + P9 的
    /// <see cref="EndFrame"/>；本方法给不经帧循环的装载失败回滚用。
    /// </summary>
    public void FreeSegment(in NodeSegment seg) => _slab.FreeSegment(seg);

    /// <summary>
    /// 每模板一个空闲栈 + bump 顶端的槽分配器。
    /// 复用命中率 ≈100%、碎片有界的前提是「同模板段长恒定」——分配器对此有断言。
    /// bin 数是常量级（装载期确定的模板集），线性查找；诊断面出现「bin 扫描超预算」实据前不换字典。
    /// </summary>
    private sealed class Slab
    {
        private struct Bin
        {
            public uint TemplateId;
            public int Count;      // 该模板的段长（0 = 尚未定档）
            public uint[] Free;    // 空闲段首栈
            public int Top;
        }

        private readonly NodeTable _t;
        private Bin[] _bins = Array.Empty<Bin>();
        private int _binCount;
        private uint _bumpTop = 1;   // 槽 0 保留为哨兵，永不分配

        public Slab(NodeTable t) => _t = t;

        public uint AllocSingle() => AllocSegment(SingleSlotTemplate, 1);

        public void FreeSingle(uint index) =>
            FreeSegment(new NodeSegment(SingleSlotTemplate, index, 1));

        public uint AllocSegment(uint templateId, int count)
        {
            int b = BinOf(templateId, count);
            ref Bin bin = ref _bins[b];
            if (bin.Top > 0)
                return bin.Free[--bin.Top];

            uint baseIdx = _bumpTop;
            _bumpTop += (uint)count;
            _t.EnsureCapacity((int)_bumpTop);
            return baseIdx;
        }

        public void FreeSegment(in NodeSegment seg)
        {
            if (seg.Count <= 0) return;
            for (int k = 0; k < seg.Count; k++)
            {
                UiAssert.That((_t._slot[seg.Base + (uint)k] & SlotFlags.Dead) != 0,
                    "FreeSegment 收到仍有活节点的段（回收必须先标记即死）");
            }
            int b = BinOf(seg.TemplateId, seg.Count);
            ref Bin bin = ref _bins[b];
            if (bin.Top == bin.Free.Length)
                Array.Resize(ref bin.Free, bin.Free.Length == 0 ? 8 : bin.Free.Length * 2);
            bin.Free[bin.Top++] = seg.Base;
        }

        private int BinOf(uint templateId, int count)
        {
            for (int i = 0; i < _binCount; i++)
            {
                if (_bins[i].TemplateId != templateId) continue;
                UiAssert.That(_bins[i].Count == count,
                    "同模板段长必须恒定（slab bin 的复用前提），模板 " + templateId);
                return i;
            }
            if (_binCount == _bins.Length)
                Array.Resize(ref _bins, _bins.Length == 0 ? 4 : _bins.Length * 2);
            _bins[_binCount] = new Bin
            {
                TemplateId = templateId,
                Count = count,
                Free = Array.Empty<uint>(),
                Top = 0,
            };
            return _binCount++;
        }
    }
}
