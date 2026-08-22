using System.Collections.Generic;
using System.Runtime.InteropServices;
using FairyNext.Contracts;
using FairyNext.Core.Layout;
using FairyNext.Core.Rendering;
using FairyNext.Numerics;

namespace FairyNext.Core.Fgb;

/// <summary>
/// 一个实例的 arm 状态（架构机制 6「arm-not-mount：所有预编译产物的法定首用协议」）。
/// </summary>
public enum FgbArmState : byte
{
    /// <summary>已装填未绑定：节点在树上、几何就位，但内容/纹理一个字节都还没读。</summary>
    Armed = 0,
    /// <summary>首用时绑定成功。</summary>
    Realized = 1,
    /// <summary>首用时对不上（模板身份链不符 / 段被外部改坏）：**静默降级 + 计数，绝不画错**。</summary>
    Inactive = 2,
}

/// <summary>
/// 一个组件实例：一段连续节点槽 + 一块定长实例字节 + 一枚 arm 令牌。
/// 「顶层块 / 嵌套 slab」的区别落在**谁来分配**：顶层块与嵌套块都是各自模板的 slab 段
/// （bin 按模板 id 分桶，同模板复用命中率因此不受嵌套影响），差别只在顶层块挂在调用方给的
/// 父节点下、嵌套块挂在宿主 localId 上。
/// </summary>
public sealed class FgbInstance
{
    /// <summary>所属包。</summary>
    public FgbPackage Package { get; }
    /// <summary>COMP 段内的模板下标。</summary>
    public int CompIndex { get; }
    /// <summary>节点段（地址稳定，不变量 12）。</summary>
    public NodeSegment Segment { get; internal set; }
    /// <summary>实例根（= 段首槽）。</summary>
    public NodeHandle Root { get; internal set; }
    /// <summary>定长实例块（COMP.instanceBytes；M1 只有块头）。</summary>
    public byte[] Block { get; }
    /// <summary>arm 状态。</summary>
    public FgbArmState State { get; internal set; }
    /// <summary>嵌套子实例（后序展开的产物，深度优先序）。</summary>
    public IReadOnlyList<FgbInstance> Nested => _nested;
    /// <summary>布局引擎里的约束实例序号（-1 = 本模板无约束图或未接布局）。</summary>
    public int LayoutInstance { get; internal set; } = -1;

    internal readonly List<FgbInstance> _nested = new List<FgbInstance>();
    internal ulong ArmChain;

    internal FgbInstance(FgbPackage package, int compIndex, uint instanceBytes)
    {
        Package = package;
        CompIndex = compIndex;
        Block = new byte[instanceBytes];
    }

    /// <summary>按 localId O(1) 寻址（机制⑩：<c>nodeBase + localId</c>，零字典零字符串）。</summary>
    public NodeHandle ChildByLocalId(NodeTable table, ushort localId)
    {
        uint idx = Segment.Base + localId;
        return localId < (ushort)Segment.Count ? table.HandleOf(idx) : NodeHandle.None;
    }
}

/// <summary>
/// PLAN 实例化器（架构机制 5）：读 PLAN → 分配 → NODE 段 SoA 列 **memcpy** → 三笔定点修补
/// （基址回填 / 实例身份 / resolved 槽批量分配）→ 约束图布防 → arm。
///
/// **这里没有工厂、没有反射、没有 switch**：一个组件实例化的全部动作是
/// 「一次段分配 + 21 次 <c>Array.Copy</c> + 5 列基址回填 + 一次 <c>Arm</c>」，
/// 与组件里有多少个节点无关的部分是 O(1) 的。旧世界的 `ConstructFromResourceCore`
/// 三遍串行 + Activator 工厂 + before/relations/after 顺序契约，在这一层整体不存在——
/// 顺序在编译期就定死了（后序 PLAN），运行期没有这个契约可违反。
///
/// **arm-not-mount**：实例化只装填。内容记录的解码与纹理像素的装载都推迟到首次被
/// <see cref="FgbContentSource"/> 问到（= 渲染平面 P7 的首次 extract）。中间发生的编辑
/// 因此天然被吸收；而首用时对不上的实例转 <see cref="FgbArmState.Inactive"/> —— 不画，不画错。
/// </summary>
public sealed class FgbInstantiator
{
    private readonly NodeTable _table;
    private readonly FgbPackage _package;
    private readonly LayoutEngine? _layout;
    private readonly Dictionary<uint, FgbInstance> _byBase = new Dictionary<uint, FgbInstance>();

    /// <summary>建实例化器。<paramref name="layout"/> 为 null 时约束图不布防（几何 = authored）。</summary>
    public FgbInstantiator(NodeTable table, FgbPackage package, LayoutEngine? layout = null)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _layout = layout;
        ContentSource = new FgbContentSource(this);
    }

    /// <summary>喂给 <c>Extract</c> 的内容源（首用触发 Realize 的那一层）。</summary>
    public FgbContentSource ContentSource { get; }

    /// <summary>所属包。</summary>
    public FgbPackage Package => _package;

    /// <summary>已 Realize 的实例数。</summary>
    public int RealizedCount { get; private set; }

    /// <summary>首用时被判 inactive 的实例数（**非零即有内容没上屏**，诊断面读它）。</summary>
    public int InactiveCount { get; private set; }

    /// <summary>活着的实例数（含嵌套）。</summary>
    public int LiveCount => _byBase.Count;

    /// <summary>
    /// 同步实例化一个模板。<paramref name="parent"/> 为 <see cref="NodeHandle.None"/> 时挂到树根。
    /// 返回顶层实例；嵌套子实例在 <see cref="FgbInstance.Nested"/> 里（后序展开序）。
    /// </summary>
    public FgbInstance Instantiate(int compIndex, NodeHandle parent = default)
    {
        if ((uint)compIndex >= (uint)_package.ComponentCount)
            throw new ArgumentOutOfRangeException(nameof(compIndex));
        FgbComponentDef top = _package.Component(compIndex);
        int lo = top.PlanStart, count = top.PlanCount;

        // 后序 ⇒ 逐步执行时子步必然已经产出；宿主步按预建的子链把它们收进来（O(步数)）。
        var produced = new FgbInstance?[count];
        var childHead = new int[count];
        var childNext = new int[count];
        for (int i = 0; i < count; i++) { childHead[i] = -1; childNext[i] = -1; }
        for (int i = 0; i < count; i++)
        {
            FgbPlanStep s = _package.PlanStep(lo + i);
            if (s.IsRoot) continue;
            int p = (int)s.ParentStep - lo;
            childNext[i] = childHead[p];
            childHead[p] = i;
        }

        FgbInstance? result = null;
        for (int i = 0; i < count; i++)
        {
            FgbPlanStep s = _package.PlanStep(lo + i);
            FgbInstance inst = AllocateBlock(s.CompIndex);
            produced[i] = inst;

            // 收孩子：链表是**逆序**串起来的，故这里的挂载序 = 步序（后序里子步的自然序）。
            var kids = new List<int>();
            for (int k = childHead[i]; k >= 0; k = childNext[k]) kids.Add(k);
            for (int k = kids.Count - 1; k >= 0; k--)
            {
                FgbInstance kid = produced[kids[k]]!;
                FgbPlanStep ks = _package.PlanStep(lo + kids[k]);
                NodeHandle host = _table.HandleOf(inst.Segment.Base + ks.HostLocalId);
                AttachNested(host, kid);
                inst._nested.Add(kid);
            }

            // 布防必须在孩子挂完、宿主尺寸下传之后：offset/ratio 的捕获读的就是这一刻的几何。
            ArmLayout(inst);
            if (i == count - 1) result = inst;
        }

        FgbInstance topInst = result!;
        NodeHandle host2 = parent.IsNone ? _table.Root : parent;
        _table.AddChild(host2, topInst.Root);
        return topInst;
    }

    /// <summary>
    /// 一步 = 一个块：段分配 → 逐列 memcpy → 基址回填 → 实例身份回填 → resolved 槽批量分配 → 算 arm 链。
    /// </summary>
    private FgbInstance AllocateBlock(int compIndex)
    {
        FgbComponentDef d = _package.Component(compIndex);
        var inst = new FgbInstance(_package, compIndex, d.InstanceBytes);
        NodeSegment seg = _table.AllocSegment(_package.TemplateIdOf(compIndex), d.NodeCount);
        inst.Segment = seg;
        inst.Root = _table.HandleOf(seg.Base);

        ReadOnlySpan<byte> nodePayload = _package.NodeSection;
        if (!FgbNodeSection.TryView(nodePayload, out FgbNodeView view))
            throw new InvalidOperationException("NODE 段在装载门已验过形状，这里不可能失败");

        // ① memcpy：每列一次（列序/元素宽全走 ABI 表，两侧同一份）。
        for (int col = 0; col < Abi.NodeColumns.Length; col++)
        {
            int w = Abi.NodeColumns[col].ElementSize;
            _table.ImportColumn(col, seg.Base, d.NodeCount,
                view.Column(col).Slice(d.NodeStart * w, d.NodeCount * w));
        }

        // ② 基址回填（Rebase 位）：abs = base + rel − 1，哨兵 0 保留。
        for (int col = 0; col < Abi.NodeColumns.Length; col++)
        {
            if (!Abi.NodeColumns[col].Rebase) continue;
            _table.RebaseColumn(col, seg.Base, d.NodeCount, seg.Base);
        }

        // ③ 实例身份：ownerInst 整块写块首（模板里恒 0，见 NodeTable.Columns 文件头）。
        _table.SetOwnerInstRange(seg.Base, d.NodeCount, seg.Base);

        // ④ resolved 槽：模板列记的是编译世界池下标，值不搬、**集合**搬（不变量 7）。
        for (int k = 0; k < d.NodeCount; k++)
        {
            uint idx = seg.Base + (uint)k;
            uint frozen = _table.ResolvedRefAt(idx);
            _table.SetResolvedRefRaw(idx, 0u);
            if (frozen != 0u) _table.AllocResolvedSlot(_table.HandleOf(idx));
        }

        inst.ArmChain = ExpectedArmChain(in d, in view);
        _byBase.Add(seg.Base, inst);
        return inst;
    }

    /// <summary>
    /// 嵌套块挂到宿主节点上：宿主是父模板里那个「子组件占位」节点，它只当变换载体；
    /// 子组件实例的**自身尺寸**由宿主的 authored 框给（编辑器里量的就是那一个框），
    /// 位置归零（宿主已经承担了位置）。
    /// **边界写明**：宿主尺寸在实例化后再变不会下传到嵌套根——那需要一条「宿主 → 子根」的
    /// 尺寸随动约束，属于 parentUsesSize 的地盘（M2）。M1 只保证装填那一刻的几何正确。
    /// </summary>
    private void AttachNested(NodeHandle host, FgbInstance nested)
    {
        if (!_table.IsAlive(host)) return;
        ResolvedGeom g = _table.GetResolved(host);
        _table.SetPosition(nested.Root, 0f, 0f);
        _table.SetSize(nested.Root, g.W, g.H);
        _table.AddChild(host, nested.Root);
    }

    private void ArmLayout(FgbInstance inst)
    {
        if (_layout == null) return;
        ConstraintGraph? g = _package.ConstraintGraphOf(inst.CompIndex);
        if (g == null) return;
        var byLocal = new NodeHandle[g.NodeCount];
        for (int k = 0; k < g.NodeCount; k++) byLocal[k] = _table.HandleOf(inst.Segment.Base + (uint)k);
        inst.LayoutInstance = _layout.Arm(g, byLocal);
    }

    /// <summary>
    /// 模板身份链：逐 local 的 <c>(localId, typeId, LOCL.editorIdHash)</c> 续 FNV。
    /// 它**不含任何可编辑状态**（位置/颜色/内容都不进链）——arm-not-mount 的承诺是
    /// 「装填到首用之间的编辑被吸收」，拿会被编辑的东西做校验和等于把承诺反过来做。
    /// 链能抓的是另一类事：块被回收后又被当活的用、池发回了别的模板的段、
    /// 段内节点被 Destroy 之后仍按 localId 寻址——那些一律是「不该画」。
    /// </summary>
    private ulong ExpectedArmChain(in FgbComponentDef d, in FgbNodeView view)
    {
        ReadOnlySpan<ushort> lid = MemoryMarshal.Cast<byte, ushort>(
            view.Column(AbiLayout.NodeColLocalId).Slice(d.NodeStart * 2, d.NodeCount * 2));
        ReadOnlySpan<ushort> tid = MemoryMarshal.Cast<byte, ushort>(
            view.Column(AbiLayout.NodeColTypeId).Slice(d.NodeStart * 2, d.NodeCount * 2));
        ReadOnlySpan<byte> locl = _package.LoclSection;
        ulong h = FnvHash.OffsetBasis64;
        for (int k = 0; k < d.NodeCount; k++)
        {
            uint eid = FgbRecordIo.ReadU32(
                locl.Slice((d.LocalStart + k) * AbiLayout.FgbLocalSize, AbiLayout.FgbLocalSize),
                AbiLayout.FgbLocalEditorIdHashOffset);
            h = Fold(h, lid[k], tid[k], eid);
        }
        return h;
    }

    private ulong LiveArmChain(FgbInstance inst)
    {
        FgbComponentDef d = _package.Component(inst.CompIndex);
        ReadOnlySpan<byte> locl = _package.LoclSection;
        ulong h = FnvHash.OffsetBasis64;
        for (int k = 0; k < d.NodeCount; k++)
        {
            uint idx = inst.Segment.Base + (uint)k;
            NodeHandle h2 = _table.HandleOf(idx);
            if (h2.IsNone) return 0ul;                    // 段内有死槽 = 这个块已经不是完整实例
            uint eid = FgbRecordIo.ReadU32(
                locl.Slice((d.LocalStart + k) * AbiLayout.FgbLocalSize, AbiLayout.FgbLocalSize),
                AbiLayout.FgbLocalEditorIdHashOffset);
            h = Fold(h, _table.LocalIdAt(idx), _table.TypeAt(idx), eid);
        }
        return h;
    }

    private static ulong Fold(ulong h, ushort localId, ushort typeId, uint editorIdHash)
    {
        Span<byte> b = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b, localId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(2), typeId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(4), editorIdHash);
        return FnvHash.Hash64Continue(h, b);
    }

    /// <summary>
    /// 首用绑定（<see cref="FgbContentSource"/> 在首次 <c>TryDescribe</c> 时调；也可由宿主显式调）。
    /// 校验模板身份链 → 解码 CONT（一包一次）→ 装载本实例用到的纹理像素。
    /// 链不符 = <see cref="FgbArmState.Inactive"/> + 计数（不变量 14：**不产生错误画面**）。
    /// </summary>
    public bool Realize(FgbInstance inst)
    {
        if (inst.State == FgbArmState.Realized) return true;
        if (inst.State == FgbArmState.Inactive) return false;

        if (LiveArmChain(inst) != inst.ArmChain)
        {
            inst.State = FgbArmState.Inactive;
            InactiveCount++;
            return false;
        }

        _package.EnsureContentDecoded();
        FgbComponentDef d = _package.Component(inst.CompIndex);
        for (int k = 0; k < d.NodeCount; k++)
        {
            NodeHandle h = _table.HandleOf(inst.Segment.Base + (uint)k);
            if (h.IsNone) continue;
            uint cref = _table.ContentRef(h);
            if (cref == 0u) continue;
            ContentRecord rec = _package.ContentAt(cref);
            if (rec.Kind != ExtractKind.Leaf) continue;
            _package.AcquireTextureByRuntimeId(rec.Leaf.Texture.Value);
        }
        inst.State = FgbArmState.Realized;
        RealizedCount++;
        return true;
    }

    /// <summary>下标 → 实例（内容源与诊断用；不是实例的话出 null）。</summary>
    internal FgbInstance? InstanceOfBase(uint owner) =>
        _byBase.TryGetValue(owner, out FgbInstance? inst) ? inst : null;

    /// <summary>
    /// 销毁一个实例（含嵌套）：标记即死 + 段进 P9 回收队列。
    /// 段是分配单位，故回收也以段为单位——单节点 <c>Destroy</c> 不会把段还回去。
    /// </summary>
    public void Destroy(FgbInstance inst)
    {
        for (int i = inst._nested.Count - 1; i >= 0; i--) Destroy(inst._nested[i]);
        inst._nested.Clear();
        if (_byBase.Remove(inst.Segment.Base) && inst.Segment.Count > 0)
            _table.DestroySegment(inst.Segment);
        inst.State = FgbArmState.Armed;
    }
}

/// <summary>
/// FGB 实例的内容源（<see cref="IExtractSource"/> 的装载平面实现）。
/// 它同时是 **arm-not-mount 的触发点**：一个实例第一次被 Extract 问到「你画什么」的那一刻，
/// 才去解 CONT、才去装纹理。之前它在树上、有几何、参与命中，但一个字节的资源都没占。
///
/// 寻址走 <c>ownerInst</c>（实例身份列，实例化时按块回填）：节点 → 块首 → 实例，O(1) 一次字典。
/// 不属于任何 FGB 块的节点（宿主自己的树根等）直接出 false ——「没有内容」不是错误。
/// </summary>
public sealed class FgbContentSource : IExtractSource
{
    private readonly FgbInstantiator _owner;

    internal FgbContentSource(FgbInstantiator owner) { _owner = owner; }

    /// <inheritdoc/>
    public bool TryDescribe(NodeTable table, NodeHandle node, out ContentRecord record)
    {
        record = default;
        if (table == null) return false;
        uint owner = table.OwnerInst(node);
        if (owner == NodeTable.NoIndex) return false;
        FgbInstance? inst = _owner.InstanceOfBase(owner);
        if (inst == null) return false;
        if (inst.State != FgbArmState.Realized && !_owner.Realize(inst)) return false;

        uint cref = table.ContentRef(node);
        if (cref == 0u) return false;
        record = inst.Package.ContentAt(cref);
        return record.Kind != ExtractKind.None || record.OpensClip;
    }
}
