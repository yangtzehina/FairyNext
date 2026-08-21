using FairyNext.Contracts;
using System.Runtime.InteropServices;

namespace FairyNext.Core.Fgb;

/// <summary>
/// NODE 段的 payload 布局（M1-19）：<c>u32 nodeCount</c> + 12B 零填充（凑 16B 头），
/// 之后按 <see cref="Abi.NodeColumns"/> 声明序逐列排布，每列起点 16B 对齐（列间零填充）、
/// 列长 = nodeCount × 元素宽。**布局是 count 的纯函数**——没有列目录：列序是封闭世界 ABI
/// （生成物 + 字节比对门钉死），字段级自描述在 blob 里不存在（平面五「FGB 顶层布局」）。
///
/// 写侧出**原始真值列**（拓扑列为绝对下标）：模板镜像的相对化归 M1-20 编译器冻结，
/// 实例化 memcpy + 基址回填归 M1-22——本类型对 Rebase 位只承载不解释。
/// 读侧 <see cref="TryView"/> 只收**精确尺寸**的 payload：长一字节短一字节都拒
/// （<see cref="FgbGate.NodeSectionShape"/>）——半列不是列，多余字节没有合法producer。
/// </summary>
public static class FgbNodeSection
{
    /// <summary>段头字节数（u32 count + 12B 零填充；首列从此偏移起，天然 16B 对齐）。</summary>
    public const int HeaderBytes = 16;

    /// <summary>给定节点数的 payload 精确字节数（写读两侧同一函数，形状账不许有第二份）。</summary>
    public static int PayloadBytes(int nodeCount)
    {
        int end = HeaderBytes;
        for (int c = 0; c < Abi.NodeColumns.Length; c++)
            end = ColumnEnd(c, nodeCount);
        return end;
    }

    /// <summary>第 column 列在 payload 内的起点偏移（列序 = <see cref="Abi.NodeColumns"/> 声明序）。</summary>
    public static int ColumnOffset(int column, int nodeCount)
    {
        int off = HeaderBytes;
        for (int c = 0; c < column; c++)
            off = (int)FgbBlobView.AlignUp((ulong)(off + Abi.NodeColumns[c].ElementSize * nodeCount));
        return off;
    }

    private static int ColumnEnd(int column, int nodeCount) =>
        ColumnOffset(column, nodeCount) + Abi.NodeColumns[column].ElementSize * nodeCount;

    /// <summary>
    /// 从树导出 [start, start+count) 槽区间的 NODE payload（列序/元素宽全走 ABI 表，
    /// 逐列 <c>NodeTable.ExportColumn</c>——树侧只有名义映射，没有第二份列序）。
    /// M1-20 编译器冻结模板时调本入口（相对化变换在其上游做进树或在下游做进副本，届时定）。
    /// </summary>
    public static byte[] Write(NodeTable table, uint start, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        byte[] payload = new byte[PayloadBytes(count)];
        var span = payload.AsSpan();
        uint n = (uint)count;
        MemoryMarshal.Write(span, ref n);
        for (int c = 0; c < Abi.NodeColumns.Length; c++)
        {
            int off = ColumnOffset(c, count);
            table.ExportColumn(c, start, count, span.Slice(off, Abi.NodeColumns[c].ElementSize * count));
        }
        return payload;
    }

    /// <summary>
    /// 打开 NODE payload。false ⇒ 形状不符（<see cref="FgbGate.NodeSectionShape"/> 级拒绝，
    /// 取用方汇入 LoadReport）；true ⇒ 全列可达，<see cref="FgbNodeView.Column"/> 不再失败。
    /// </summary>
    public static bool TryView(ReadOnlySpan<byte> payload, out FgbNodeView view)
    {
        view = default;
        if (payload.Length < HeaderBytes)
            return false;
        uint count = MemoryMarshal.Read<uint>(payload);
        // 先门后算（读侧纪律）：count 是外部输入，上限先卡住，PayloadBytes 的乘法才不会溢 int。
        if (count > (uint)MaxNodeCount)
            return false;
        if (payload.Length != PayloadBytes((int)count))
            return false;
        view = new FgbNodeView(payload, (int)count);
        return true;
    }

    /// <summary>节点数理智上限（int 溢出前置门；表位宽 u32 是天花板，这是水位）。</summary>
    public const int MaxNodeCount = 1 << 24;
}

/// <summary>NODE payload 的零拷贝列视图（<see cref="FgbNodeSection.TryView"/> 成功后有效）。</summary>
public readonly ref struct FgbNodeView
{
    private readonly ReadOnlySpan<byte> _payload;

    /// <summary>节点数（= 每列元素数）。</summary>
    public readonly int NodeCount;

    internal FgbNodeView(ReadOnlySpan<byte> payload, int nodeCount)
    {
        _payload = payload;
        NodeCount = nodeCount;
    }

    /// <summary>第 column 列的原始字节（长度 = NodeCount × 元素宽；列号取 AbiLayout.NodeCol*）。</summary>
    public ReadOnlySpan<byte> Column(int column) =>
        _payload.Slice(FgbNodeSection.ColumnOffset(column, NodeCount),
            Abi.NodeColumns[column].ElementSize * NodeCount);
}
