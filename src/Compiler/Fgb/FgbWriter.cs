// 移植自 fork：~/ECS/FairyGUI-unity Assets/Scripts/Core/Instanced/Fqs.cs 92-225（FqsBlob.Write 段）@ 08a2d56
//
// 随行的 fork 纪律（行号见括号）：
//  1.（193-221 WriteSection/Pad16）段起点 16B 对齐、填充写零、定长记录整段 AsBytes 落盘——
//     对齐使读侧 MemoryMarshal.Cast 直读，零填充使「同输入同字节」成立（垃圾填充 = 每次
//     写出不同 blob，canonical 去重与编译产物 golden 全废）。
//  2.（92-121 Write）单遍顺序拼装、无回写游标——除 selfHash 一处显式补钉外，写出即定稿。
//  3.（22-24）LE-only。FGB 加严：fork 用 MemoryMarshal 依赖宿主字节序（BE 编辑器机上会
//     产出 BE blob，靠读侧 magic 拒），这里全部标量走 BinaryPrimitives.*LittleEndian——
//     **写入器在任何宿主上产出同一批字节**，编译产物 golden 才不用按构建机分叉。
//
// 不随行的：FQS1 的定形五段（FGB 是段落表，段集开放）；texRef 变长表（TREF 是定长记录段，
// 归 M1-20/22）；fuiHash 门语义（四维身份在头部四字段，比对归 M1-22）。
// 计划提到的 1 处 Mathf.Clamp（fork 56-59 EncodeScaleLevel）没有随行对象：FGB 头的
// scaleLevel 是 u16 字段不是 flags 位段，无需夹断编码——u16 定义域就是合法域。

using FairyNext.Contracts;
using FairyNext.Core.Fgb;
using System.Buffers.Binary;

namespace FairyNext.Compiler.Fgb;

/// <summary>
/// FGB 容器写入器（M1-19）：收段 → <see cref="Finish"/> 出整 blob。确定性是合同：同一段
/// 序列 + 同一身份 ⇒ 逐字节相同（测试钉住）；段**顺序 = AddSection 调用序**，规范段序与
/// canonical 去重归 M1-20 编译器——容器不替编译器做排序决定。
/// 同 fourcc 多段合法（LANG 每语言一段），读侧 TryGetSection 取目录序首个。
/// </summary>
public sealed class FgbWriter
{
    private readonly List<(uint Fourcc, byte[] Payload)> _sections = new List<(uint, byte[])>();

    /// <summary>登记一段（payload 复制留存；空段合法——空表也是表）。</summary>
    public void AddSection(uint fourcc, ReadOnlySpan<byte> payload)
    {
        _sections.Add((fourcc, payload.ToArray()));
    }

    /// <summary>已登记段数（诊断/测试）。</summary>
    public int SectionCount => _sections.Count;

    /// <summary>
    /// 拼装整 blob：64B 头 + 目录 + 16B 对齐段区，最后补钉 selfHash（字段按零参与散列，
    /// 算法唯一实现点在 <see cref="FgbBlobView.ComputeSelfHash"/>——写读两侧不许各抄一份）。
    /// </summary>
    public byte[] Finish(ulong pkgId, ulong sourceHash, ulong combinedRefHash, ushort scaleLevel, ushort branchId)
    {
        // 布局账先行：头 | 目录 | 逐段（各自对齐上去）。
        long dirEnd = Abi.FgbHeaderSize + (long)_sections.Count * Abi.FgbSectionDirEntrySize;
        var offsets = new long[_sections.Count];
        long cursor = (long)FgbBlobView.AlignUp((ulong)dirEnd);
        for (int i = 0; i < _sections.Count; i++)
        {
            offsets[i] = cursor;
            cursor = (long)FgbBlobView.AlignUp((ulong)(cursor + _sections[i].Payload.Length));
        }
        // 末段尾不再补对齐填充：blob 总长 = 末段自然终点（对齐只服务「下一段的起点」）。
        long total = _sections.Count == 0
            ? (long)FgbBlobView.AlignUp((ulong)dirEnd)
            : offsets[_sections.Count - 1] + _sections[_sections.Count - 1].Payload.Length;

        byte[] blob = new byte[total];
        Span<byte> s = blob.AsSpan();

        // 头（全部标量显式 LE，见文件头改动 3；保留段是 new byte[] 的零，不必再写）。
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(AbiLayout.FgbHeaderMagicOffset), Abi.FgbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(AbiLayout.FgbHeaderFormatVersionOffset), (uint)Abi.FgbFormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(AbiLayout.FgbHeaderFlagsOffset),
            1u << AbiLayout.FgbFlagLittleEndianShift);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(AbiLayout.FgbHeaderPkgIdOffset), pkgId);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(AbiLayout.FgbHeaderSourceHashOffset), sourceHash);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(AbiLayout.FgbHeaderCombinedRefHashOffset), combinedRefHash);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(AbiLayout.FgbHeaderScaleLevelOffset), scaleLevel);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(AbiLayout.FgbHeaderBranchIdOffset), branchId);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(AbiLayout.FgbHeaderSectionCountOffset), (uint)_sections.Count);

        // 目录 + 段区。
        for (int i = 0; i < _sections.Count; i++)
        {
            int e = Abi.FgbHeaderSize + i * Abi.FgbSectionDirEntrySize;
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(e + AbiLayout.FgbDirFourccOffset), _sections[i].Fourcc);
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(e + AbiLayout.FgbDirOffsetOffset), (ulong)offsets[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(e + AbiLayout.FgbDirLengthOffset), (ulong)_sections[i].Payload.Length);
            _sections[i].Payload.AsSpan().CopyTo(s.Slice((int)offsets[i]));
        }

        // selfHash 补钉（fork 改动 2 的唯一回写点）。
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(AbiLayout.FgbHeaderSelfHashOffset),
            FgbBlobView.ComputeSelfHash(s));
        return blob;
    }
}
