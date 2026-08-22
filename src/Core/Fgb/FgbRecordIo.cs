using System.Buffers.Binary;

namespace FairyNext.Core.Fgb;

/// <summary>
/// FGB 段内定长记录的标量读写（M1-20b 立于编译器侧，M1-22 迁入 Core）。**全部显式 little-endian**——与 M1-19 的
/// <c>FgbWriter</c> 同一条纪律：写入器在任何宿主上产出同一批字节，编译产物 golden 才不用
/// 按构建机分叉（BE 构建机产 BE blob，靠读侧 magic 拒，是「没人会遇到所以没人会发现」的坑）。
///
/// 例外只有两处，都是**已有运行期结构的整体 blit**：NODE 列（<c>NodeTable.ExportColumn</c>）
/// 与 QUAD/CLIP/CNST 算子（<c>MemoryMarshal.AsBytes</c>）。它们的布局由运行期 struct 定义，
/// 逐字段重写等于抄第二份布局；宿主字节序由头的 LE 断言位 + <c>FgbBlobView.TryOpen</c> 的
/// 宿主端序检查兜住（M1-19 既有形态，本包不改）。
///
/// 偏移一律取生成物 <c>AbiLayout.Fgb*Offset</c>，本文件不出现任何字面偏移。
///
/// **住 Core 的理由（M1-22）**：写侧（编译器 <c>FgbFreezer</c>）与读侧（装载器 <c>FgbPackage</c>、
/// 实例化、测试的读回对账）必须共用同一套标量原语——两份 LE 读写迟早在某个字段上分叉，
/// 而分叉的表现是「某个偏移读出垃圾」，不是编译错。Core 是两侧都能到的唯一位置。
/// </summary>
public static class FgbRecordIo
{
    public static void U8(Span<byte> rec, int offset, byte v) => rec[offset] = v;

    public static void U16(Span<byte> rec, int offset, ushort v) =>
        BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(offset), v);

    public static void U32(Span<byte> rec, int offset, uint v) =>
        BinaryPrimitives.WriteUInt32LittleEndian(rec.Slice(offset), v);

    public static void I32(Span<byte> rec, int offset, int v) =>
        BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(offset), v);

    public static void U64(Span<byte> rec, int offset, ulong v) =>
        BinaryPrimitives.WriteUInt64LittleEndian(rec.Slice(offset), v);

    /// <summary>float 按**原始位**落盘（+0/-0 与 NaN payload 都保真——冻结是记录不是求值）。</summary>
    public static void F32(Span<byte> rec, int offset, float v) =>
        BinaryPrimitives.WriteUInt32LittleEndian(rec.Slice(offset),
            unchecked((uint)BitConverter.SingleToInt32Bits(v)));

    // ── 读回面（装载器与测试的读回 sanity 走同一批偏移常量）────────────────────

    public static byte ReadU8(ReadOnlySpan<byte> rec, int offset) => rec[offset];

    public static ushort ReadU16(ReadOnlySpan<byte> rec, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(offset));

    public static uint ReadU32(ReadOnlySpan<byte> rec, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(offset));

    public static int ReadI32(ReadOnlySpan<byte> rec, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(offset));

    public static ulong ReadU64(ReadOnlySpan<byte> rec, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(rec.Slice(offset));

    public static float ReadF32(ReadOnlySpan<byte> rec, int offset) =>
        BitConverter.Int32BitsToSingle(unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(offset))));
}
