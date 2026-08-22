using FairyNext.Contracts;

namespace FairyNext.Core.Fgb;

/// <summary>
/// STRT 段的 payload 布局（M1-20b）。与 <see cref="FgbNodeSection"/> 同一条纪律：
/// **布局是计数的纯函数**（这里是 count 与 poolBytes 两个），段内没有子目录——
/// 写侧与读侧调同一个函数算偏移，谁也不许手算第二份。
/// 结构：16B 段头（<c>u32 count</c> + <c>u32 poolBytes</c> + 8B 零填充）
/// → <c>StrtEntry[count]</c>（各 8B：池内 offset/length）→ UTF-8 池。
/// 下标 0 恒为空串哨兵（「没有字符串」与「空字符串」在 blob 里同一个值）。
/// </summary>
public static class FgbStrtSection
{
    /// <summary>段头字节数。</summary>
    public const int HeaderBytes = AbiLayout.FgbStrtHeaderSize;

    /// <summary>条目数组起点（= 段头之后，天然 8B 对齐）。</summary>
    public const int EntriesOffset = HeaderBytes;

    /// <summary>UTF-8 池起点。</summary>
    public static int PoolOffset(int count) => EntriesOffset + count * AbiLayout.FgbStrtEntrySize;

    /// <summary>payload 精确字节数。</summary>
    public static int PayloadBytes(int count, int poolBytes) => PoolOffset(count) + poolBytes;

    /// <summary>第 i 条条目记录的起点。</summary>
    public static int EntryOffset(int i) => EntriesOffset + i * AbiLayout.FgbStrtEntrySize;
}

/// <summary>
/// CNST 段的 payload 布局（M1-20b）。同样是**四个计数的纯函数**：
/// 16B 段头（opCount/fanCount/indexCount/maskCount）
/// → <c>ConstraintOp[opCount]</c>（8B）→ <c>FanOut[fanCount]</c>（4B）
/// → <c>ushort fanOpIndices[indexCount]</c> → <c>byte nodeMasks[maskCount]</c>。
/// 每个子数组起点先对齐到 8B（<c>Cast</c> 直读的自然对齐，也让「加一条组件」不会
/// 因为奇数长度把后面的数组挪成非对齐）；末尾补零到 8B。
///
/// **组件内相对**：段是包级的（全部组件的四个数组各自首尾相接），而
/// <c>FanOut.Start</c> 与下标池里的算子下标都是**组件内**下标——读侧加
/// <c>COMP.cnstIdxStart</c> / <c>COMP.cnstOpStart</c> 归位。理由与 NODE 列的 rebase 同：
/// 模板要能在任何基址上被实例化，绝对下标一冻死就绑住了包内位置。
/// </summary>
public static class FgbCnstSection
{
    /// <summary>段头字节数。</summary>
    public const int HeaderBytes = AbiLayout.FgbCnstHeaderSize;

    /// <summary>ConstraintOp 记录宽（运行期 struct：SrcNode/DstNode/Kind/AxisEdges/Next）。</summary>
    public const int OpBytes = 8;

    /// <summary>FanOut 记录宽（Start/Count 两个 u16）。</summary>
    public const int FanBytes = 4;

    /// <summary>算子下标池的元素宽。</summary>
    public const int IndexBytes = 2;

    /// <summary>算子数组起点。</summary>
    public const int OpsOffset = HeaderBytes;

    /// <summary>FanOut 桶数组起点。</summary>
    public static int FansOffset(int opCount) => Align8(OpsOffset + opCount * OpBytes);

    /// <summary>算子下标池起点。</summary>
    public static int IndicesOffset(int opCount, int fanCount) => Align8(FansOffset(opCount) + fanCount * FanBytes);

    /// <summary>归属掩码数组起点。</summary>
    public static int MasksOffset(int opCount, int fanCount, int indexCount) =>
        Align8(IndicesOffset(opCount, fanCount) + indexCount * IndexBytes);

    /// <summary>payload 精确字节数。</summary>
    public static int PayloadBytes(int opCount, int fanCount, int indexCount, int maskCount) =>
        Align8(MasksOffset(opCount, fanCount, indexCount) + maskCount);

    private static int Align8(int v) => (v + 7) & ~7;
}
