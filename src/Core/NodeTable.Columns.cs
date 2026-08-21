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
/// 树不解释它。导入（列 → 表）随 M1-22 的 PLAN 实例化进驻，本文件先只出不进。
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

    private static void Copy<T>(T[] col, uint start, int count, Span<byte> dst) where T : struct
    {
        ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(col.AsSpan((int)start, count));
        UiAssert.That(src.Length == dst.Length, "ExportColumn 目标长度 != count × 列元素宽（列宽账不符）");
        src.CopyTo(dst);
    }
}
