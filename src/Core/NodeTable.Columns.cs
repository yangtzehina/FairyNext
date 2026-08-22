using FairyNext.Contracts;
using System.Runtime.InteropServices;

namespace FairyNext.Core;

/// <summary>
/// NODE 段列导出面（M1-19）。「NODE 段列序与 NodeTable 列布局是同一份 ABI」（平面五接缝，
/// 全项目最硬接缝）在树这一侧的落点：**列序不在本文件**——顺序、元素宽、rebase 语义全部
/// 住 <see cref="Abi.NodeColumns"/>（生成物 <see cref="AbiLayout"/> 的 NodeCol* 常量），这里只有
/// 「生成物列号 → 私有列数组」的名义映射。表里有列而这里没接 = 导出即抛（对账测试逐列扫，
/// 红在门里不红在真机）；这里接错数组 = 逐列语义对账测试红（写 authored 值、读列字节比对）。
///
/// 导出的是**原始真值**（拓扑列为绝对下标）：模板镜像的相对化（rebase）是编译器冻结时的
/// 变换（M1-20），实例化 memcpy 后加基址回填是 M1-22——两者都消费本表的 Rebase 位，
/// 树不解释它。
///
/// **导入面（M1-22）**：<see cref="NodeTable.ImportColumn"/> 是机制⑦「每列一次 Array.Copy」的
/// 落点——目标是段分配器给的连续槽区间，源是 <c>FgbNodeView.Column</c> 的零拷贝 span，
/// 中间没有逐节点循环、没有对象、没有 switch 工厂。导入之后有且只有三笔定点修补，
/// 每一笔都对应本表上的一条语义位或一条不变量：
///  ① <see cref="NodeTable.RebaseColumn"/>——Rebase 位为真的拓扑列，<c>abs = base + rel − 1</c>，
///     哨兵 0 保留（"无" 在任何基址上都还是 "无"）。这是冻结期相对化的逆变换，两边写在
///     一处的两个方向上，改一边不改另一边会立刻在往返用例上现形。
///  ② <see cref="NodeTable.SetOwnerInstRange"/>——ownerInst 是**实例身份**不是模板数据：
///     模板里它恒 0（编译世界一个模板只有一个实例、且它就是树根），实例化按块回填成块首下标。
///     不回填的后果不是画面错，是 <see cref="NodeTable.ChildByLocalId"/> 的「跨实例块」断言
///     形同虚设——两个块的 ownerInst 全是 0，互相寻址会静默成功。
///  ③ resolved 槽：模板列记的是**编译世界池下标**，换个表就没有意义。实例化按
///     「非零 = 该节点在编译期布局写集内」重新批量分配（不变量 7），值不搬、集合搬。
/// </summary>
public sealed partial class NodeTable
{
    /// <summary>
    /// 导出一根列的 [start, start+count) 槽区间到 dst（长度必须精确 = count × 列元素宽）。
    /// column 取 <see cref="AbiLayout"/> 的 NodeCol* 常量；未映射列号 = 抛（对账门）。
    /// </summary>
    internal void ExportColumn(int column, uint start, int count, Span<byte> dst)
    {
        UiAssert.That(start + (uint)count <= (uint)_capacity, "ExportColumn 区间越表容量");
        switch (column)
        {
            case AbiLayout.NodeColParent: Copy(_parent, start, count, dst); break;
            case AbiLayout.NodeColFirstChild: Copy(_firstChild, start, count, dst); break;
            case AbiLayout.NodeColNextSib: Copy(_nextSib, start, count, dst); break;
            case AbiLayout.NodeColPrevSib: Copy(_prevSib, start, count, dst); break;
            case AbiLayout.NodeColOwnerInst: Copy(_ownerInst, start, count, dst); break;
            case AbiLayout.NodeColLocalId: Copy(_localId, start, count, dst); break;
            case AbiLayout.NodeColTypeId: Copy(_typeId, start, count, dst); break;
            case AbiLayout.NodeColPosX: Copy(_posX, start, count, dst); break;
            case AbiLayout.NodeColPosY: Copy(_posY, start, count, dst); break;
            case AbiLayout.NodeColWidth: Copy(_width, start, count, dst); break;
            case AbiLayout.NodeColHeight: Copy(_height, start, count, dst); break;
            case AbiLayout.NodeColScaleX: Copy(_scaleX, start, count, dst); break;
            case AbiLayout.NodeColScaleY: Copy(_scaleY, start, count, dst); break;
            case AbiLayout.NodeColRotation: Copy(_rotation, start, count, dst); break;
            case AbiLayout.NodeColSkew: Copy(_skew, start, count, dst); break;
            case AbiLayout.NodeColPivotX: Copy(_pivotX, start, count, dst); break;
            case AbiLayout.NodeColPivotY: Copy(_pivotY, start, count, dst); break;
            case AbiLayout.NodeColLocalVisual: Copy(_localVisual, start, count, dst); break;
            case AbiLayout.NodeColContentRef: Copy(_contentRef, start, count, dst); break;
            case AbiLayout.NodeColStateRef: Copy(_stateRef, start, count, dst); break;
            case AbiLayout.NodeColResolvedRef: Copy(_resolvedRef, start, count, dst); break;
            default:
                throw new InvalidOperationException(
                    $"NODE 列 {column} 在 Abi.NodeColumns 有声明但 NodeTable 未接导出——两边漂移");
        }
    }

    /// <summary>
    /// 导入一根列到 [start, start+count) 槽区间（src 长度必须精确 = count × 列元素宽）。
    /// 与 <see cref="ExportColumn"/> 严格对称：列号取生成物常量，未映射列号 = 抛（同一道对账门）。
    /// **只搬字节**——rebase/ownerInst/resolved 三笔修补由调用方（装载平面）按语义位单独做。
    /// </summary>
    internal void ImportColumn(int column, uint start, int count, ReadOnlySpan<byte> src)
    {
        UiAssert.That(start + (uint)count <= (uint)_capacity, "ImportColumn 区间越表容量");
        switch (column)
        {
            case AbiLayout.NodeColParent: Paste(_parent, start, count, src); break;
            case AbiLayout.NodeColFirstChild: Paste(_firstChild, start, count, src); break;
            case AbiLayout.NodeColNextSib: Paste(_nextSib, start, count, src); break;
            case AbiLayout.NodeColPrevSib: Paste(_prevSib, start, count, src); break;
            case AbiLayout.NodeColOwnerInst: Paste(_ownerInst, start, count, src); break;
            case AbiLayout.NodeColLocalId: Paste(_localId, start, count, src); break;
            case AbiLayout.NodeColTypeId: Paste(_typeId, start, count, src); break;
            case AbiLayout.NodeColPosX: Paste(_posX, start, count, src); break;
            case AbiLayout.NodeColPosY: Paste(_posY, start, count, src); break;
            case AbiLayout.NodeColWidth: Paste(_width, start, count, src); break;
            case AbiLayout.NodeColHeight: Paste(_height, start, count, src); break;
            case AbiLayout.NodeColScaleX: Paste(_scaleX, start, count, src); break;
            case AbiLayout.NodeColScaleY: Paste(_scaleY, start, count, src); break;
            case AbiLayout.NodeColRotation: Paste(_rotation, start, count, src); break;
            case AbiLayout.NodeColSkew: Paste(_skew, start, count, src); break;
            case AbiLayout.NodeColPivotX: Paste(_pivotX, start, count, src); break;
            case AbiLayout.NodeColPivotY: Paste(_pivotY, start, count, src); break;
            case AbiLayout.NodeColLocalVisual: Paste(_localVisual, start, count, src); break;
            case AbiLayout.NodeColContentRef: Paste(_contentRef, start, count, src); break;
            case AbiLayout.NodeColStateRef: Paste(_stateRef, start, count, src); break;
            case AbiLayout.NodeColResolvedRef: Paste(_resolvedRef, start, count, src); break;
            default:
                throw new InvalidOperationException(
                    $"NODE 列 {column} 在 Abi.NodeColumns 有声明但 NodeTable 未接导入——两边漂移");
        }
    }

    /// <summary>
    /// 基址回填：把一根**组件内 1 基**拓扑列换算成本表的绝对槽下标（<c>abs = base + rel − 1</c>）。
    /// 哨兵 0 原样保留。只对 <c>Abi.NodeColumns[column].Rebase</c> 为真的列合法（内有断言）。
    /// </summary>
    internal void RebaseColumn(int column, uint start, int count, uint instanceBase)
    {
        UiAssert.That(Abi.NodeColumns[column].Rebase, "RebaseColumn 用在非 Rebase 列上");
        UiAssert.That(start + (uint)count <= (uint)_capacity, "RebaseColumn 区间越表容量");
        uint[] col = column switch
        {
            AbiLayout.NodeColParent => _parent,
            AbiLayout.NodeColFirstChild => _firstChild,
            AbiLayout.NodeColNextSib => _nextSib,
            AbiLayout.NodeColPrevSib => _prevSib,
            AbiLayout.NodeColOwnerInst => _ownerInst,
            _ => throw new InvalidOperationException($"NODE 列 {column} 声明了 Rebase 但不是 u32 拓扑列"),
        };
        for (int k = 0; k < count; k++)
        {
            uint rel = col[start + (uint)k];
            col[start + (uint)k] = rel == 0u ? 0u : instanceBase + rel - 1u;
        }
    }

    /// <summary>实例身份回填：整块 ownerInst 写成块首下标（见文件头 ②）。</summary>
    internal void SetOwnerInstRange(uint start, int count, uint owner)
    {
        UiAssert.That(start + (uint)count <= (uint)_capacity, "SetOwnerInstRange 区间越表容量");
        for (int k = 0; k < count; k++) _ownerInst[start + (uint)k] = owner;
    }

    /// <summary>resolved 槽引用的下标读（装载平面按「非零 = 在布局写集内」重新批量分配）。</summary>
    internal uint ResolvedRefAt(uint index) => _resolvedRef[index];

    /// <summary>resolved 槽引用的下标写（**仅装载平面**：把模板里的编译世界池下标清成未分配）。</summary>
    internal void SetResolvedRefRaw(uint index, uint value) => _resolvedRef[index] = value;

    /// <summary>localId 的下标读（arm 期身份链校验按下标走，句柄化每步太贵）。</summary>
    internal ushort LocalIdAt(uint index) => _localId[index];

    private static void Copy<T>(T[] col, uint start, int count, Span<byte> dst) where T : struct
    {
        ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(col.AsSpan((int)start, count));
        UiAssert.That(src.Length == dst.Length, "ExportColumn 目标长度 != count × 列元素宽（列宽账不符）");
        src.CopyTo(dst);
    }

    private static void Paste<T>(T[] col, uint start, int count, ReadOnlySpan<byte> src) where T : struct
    {
        Span<byte> dst = MemoryMarshal.AsBytes(col.AsSpan((int)start, count));
        UiAssert.That(src.Length == dst.Length, "ImportColumn 源长度 != count × 列元素宽（列宽账不符）");
        src.CopyTo(dst);
    }
}
