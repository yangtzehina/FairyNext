// 移植自 fork：~/ECS/FairyGUI-unity Assets/Scripts/Core/Instanced/Fqs.cs 20-338（FqsBlob 段）@ 08a2d56
//
// 直搬的是**血统而非字节**：FQS1 是定形五段 + 变长 texRef 表的封闭容器，FGB1 是段落表式
// 容器（64B 头 + SectionDir + 16B 对齐段区，架构文档「FGB 顶层布局」）。逐行活下来的是
// fork 用血换来的读侧纪律，每条都有 fork 行号：
//  1.（Read 125-129/143-153）敌意/损坏输入先门后分配：计数与总长在读任何段**之前**验完，
//     被改成 20 亿的计数只会换来拒载，不会先 OOM——OOM 不是「拒收 + 诊断」。
//  2.（Read 132-137）magic/formatVersion 精确匹配、不匹配即响亮拒绝；FGB 侧把
//     InvalidDataException 换成 FgbGate 门号——本读取器是 fuzz 常设对象（机制 12），
//     Try 形态 + 报告比异常括号更贴「任意字节输入只有拒载/载入两个出口」的合同。
//  3.（22-24）LE-only：fork 靠「BE 读者过不了 magic」隐式拒绝，FGB 把它显式化成宿主
//     字节序检查 + 头 flags 的 LE 断言位（BE **写者**产出的 blob 在 LE 读者上也要被拒）。
//  4.（181-189 sOne<T>）零依赖的 SizeOf<T>：netstandard2.1 无 Unsafe.SizeOf，沿用 fork 的
//     单元素数组 AsBytes 长度法（每 T 一次静态初始化，之后免费）。
//  5.（146-153）越界判定全程无符号且先界后加：u64 的 offset+length 可回绕，比较写成
//     「offset ≤ len ∧ length ≤ len − offset」，不写「offset+length ≤ len」。
//
// 刻意不随行的：fork 的 fuiHash 单值门（FGB 是四维身份，比对归 M1-22 装载三操作）；
// PeekLeafCount（FGB 无「不全解先窥」需求——段落表本身就是 O(段数) 的窥视）；
// CombineWithReferences（依赖 UIPackage 活表，归 M1-22 的 DEPS 重算）。

using FairyNext.Contracts;
using FairyNext.Numerics;
using System.Runtime.InteropServices;

namespace FairyNext.Core.Fgb;

/// <summary>
/// FGB blob 的零拷贝只读视图（平面五「零解析三操作」之 ①验证 + ②视图；③绑定归 M1-22）。
/// <see cref="TryOpen"/> 把全部结构校验做完（头/目录/越界/对齐/selfHash），成功后段访问
/// 只是切 span + <see cref="MemoryMarshal.Cast{TFrom,TTo}"/>——**打开即验完，取用不再失败**
/// （唯一例外：记录宽不整除的 <see cref="TryRecords{T}"/>，因为 T 是取用方才知道的）。
/// 任意字节序列喂 <see cref="TryOpen"/> 的可接受结果只有「false + 门号」与「true + 全段
/// 可达」，绝不越界、绝不半载——fuzz 门（M1-22 转常设）盯的就是这句话。
/// </summary>
public readonly struct FgbBlobView
{
    private readonly ReadOnlyMemory<byte> _blob;
    private readonly int _sectionCount;

    private FgbBlobView(ReadOnlyMemory<byte> blob, int sectionCount)
    {
        _blob = blob;
        _sectionCount = sectionCount;
    }

    // ── 头字段读口（TryOpen 成功后有效）────────────────────────────────────

    public uint FormatVersion => ReadU32(AbiLayout.FgbHeaderFormatVersionOffset);
    public uint Flags => ReadU32(AbiLayout.FgbHeaderFlagsOffset);
    public ulong SelfHash => ReadU64(AbiLayout.FgbHeaderSelfHashOffset);
    public ulong SourceHash => ReadU64(AbiLayout.FgbHeaderSourceHashOffset);
    /// <summary>包 id 字符串的 FNV-1a 64（装载门 5 的对照物；v2 起有效，v1 blob 已被门 1 拒）。</summary>
    public ulong PkgId => ReadU64(AbiLayout.FgbHeaderPkgIdOffset);
    public ulong CombinedRefHash => ReadU64(AbiLayout.FgbHeaderCombinedRefHashOffset);
    public ushort ScaleLevel => ReadU16(AbiLayout.FgbHeaderScaleLevelOffset);
    public ushort BranchId => ReadU16(AbiLayout.FgbHeaderBranchIdOffset);
    public int SectionCount => _sectionCount;

    /// <summary>段目录条目数的理智上限。位宽（u32）是天花板，这是「先门后分配」的水位。</summary>
    public const int MaxSectionCount = 1 << 20;

    /// <summary>
    /// 打开并验证一个 FGB blob。false ⇒ <paramref name="report"/> 带门号与细节，
    /// <paramref name="view"/> 为 default 不可用；true ⇒ 全部声明段可达。
    /// </summary>
    /// <param name="verifySelfHash">发布包可信任跳过（装载门 3 的明文豁免口，默认开）。</param>
    public static bool TryOpen(ReadOnlyMemory<byte> blob, out FgbBlobView view, out FgbLoadReport report,
        bool verifySelfHash = true)
    {
        view = default;
        report = new FgbLoadReport { BlobBytes = blob.Length };
        ReadOnlySpan<byte> s = blob.Span;

        // LE-only：BE 宿主上 Cast 直读整套失效，头都不必看（见文件头改动 3）。
        if (!BitConverter.IsLittleEndian)
            return Reject(ref report, FgbGate.Endianness, "宿主不是小端：FGB 是 LE-only 格式");

        if (s.Length < Abi.FgbHeaderSize)
            return Reject(ref report, FgbGate.Truncated,
                $"blob {s.Length}B < 头 {Abi.FgbHeaderSize}B");

        uint magic = MemoryMarshal.Read<uint>(s.Slice(AbiLayout.FgbHeaderMagicOffset));
        if (magic != Abi.FgbMagic)
            return Reject(ref report, FgbGate.Magic,
                $"magic 0x{magic:X8} != 0x{Abi.FgbMagic:X8}（FGB1）");

        uint version = MemoryMarshal.Read<uint>(s.Slice(AbiLayout.FgbHeaderFormatVersionOffset));
        if (version != Abi.FgbFormatVersion)
            return Reject(ref report, FgbGate.FormatVersion,
                $"formatVersion {version} != {Abi.FgbFormatVersion}（精确匹配；重烘，不降级）");

        uint flags = MemoryMarshal.Read<uint>(s.Slice(AbiLayout.FgbHeaderFlagsOffset));
        if (AbiLayout.FgbFlagLittleEndian(flags) == 0)
            return Reject(ref report, FgbGate.HeaderFlags, "LE 断言位缺失（大端写者的产物）");
        if (AbiLayout.FgbFlagCompressed(flags) != 0)
            return Reject(ref report, FgbGate.HeaderFlags, "压缩位置位：M1 不支持压缩 blob");
        if (AbiLayout.FgbFlagReserved(flags) != 0)
            return Reject(ref report, FgbGate.HeaderFlags,
                $"保留位非零（0x{flags:X8}）：头位域无段粒度前向兼容，未知位只能拒");

        uint sectionCount = MemoryMarshal.Read<uint>(s.Slice(AbiLayout.FgbHeaderSectionCountOffset));
        // 先门后分配（文件头改动 1）：计数是外部输入，越水位或目录越 blob 界都在此处拒。
        if (sectionCount > MaxSectionCount)
            return Reject(ref report, FgbGate.SectionCount, $"段数 {sectionCount} 越水位 {MaxSectionCount}");
        long dirEnd = Abi.FgbHeaderSize + (long)sectionCount * Abi.FgbSectionDirEntrySize;
        if (dirEnd > s.Length)
            return Reject(ref report, FgbGate.Truncated,
                $"段目录需 {dirEnd}B > blob {s.Length}B");
        report.SectionCount = (int)sectionCount;

        // 段区起点：目录尾对齐上去。段不得与头/目录重叠——offset 指进头部的 blob 不是
        // 内存不安全，但没有任何合法写入器会产出它，按门 2 一并拒（overlap = 结构性不符）。
        ulong payloadFloor = AlignUp((ulong)dirEnd);
        ulong blobLen = (ulong)s.Length;
        var sections = new (uint Fourcc, ulong Length)[sectionCount];
        for (int i = 0; i < (int)sectionCount; i++)
        {
            int e = Abi.FgbHeaderSize + i * Abi.FgbSectionDirEntrySize;
            uint fourcc = MemoryMarshal.Read<uint>(s.Slice(e + AbiLayout.FgbDirFourccOffset));
            ulong off = MemoryMarshal.Read<ulong>(s.Slice(e + AbiLayout.FgbDirOffsetOffset));
            ulong len = MemoryMarshal.Read<ulong>(s.Slice(e + AbiLayout.FgbDirLengthOffset));
            // 无符号先界后加（文件头改动 5）：off+len 可回绕，不许写「off+len ≤ blobLen」。
            if (off > blobLen || len > blobLen - off)
                return Reject(ref report, FgbGate.SectionBounds,
                    $"段[{i}] offset {off} + length {len} 越 blob {blobLen}B 界");
            if (off < payloadFloor)
                return Reject(ref report, FgbGate.SectionBounds,
                    $"段[{i}] offset {off} 与头/目录（前 {payloadFloor}B）重叠");
            if (off % Abi.FgbSectionAlignment != 0)
                return Reject(ref report, FgbGate.SectionMisaligned,
                    $"段[{i}] offset {off} 非 {Abi.FgbSectionAlignment}B 对齐");
            sections[i] = (fourcc, len);
        }

        // 门 1/2 到此为止全绿：判决表逐门记一行（拒载路径由 Reject 记，成功路径在这里记）。
        report.Note(FgbGate.Magic, FgbGateClass.Structural, false, false,
            $"magic/formatVersion/flags 精确匹配（v{version}）");
        report.Note(FgbGate.SectionBounds, FgbGateClass.Structural, false, false,
            $"{sectionCount} 段全部在界且 {Abi.FgbSectionAlignment}B 对齐");

        if (verifySelfHash)
        {
            ulong stored = MemoryMarshal.Read<ulong>(s.Slice(AbiLayout.FgbHeaderSelfHashOffset));
            ulong computed = ComputeSelfHash(s);
            if (stored != computed)
                return Reject(ref report, FgbGate.SelfHash,
                    $"selfHash 0x{stored:X16} != 重算 0x{computed:X16}（传输损坏或事后改写）");
            report.Note(FgbGate.SelfHash, FgbGateClass.Structural, false, false, "全 blob FNV-1a 相符");
        }
        else
        {
            report.Note(FgbGate.SelfHash, FgbGateClass.Structural, false, true, "调用方声明发布包可信任");
        }

        report.Sections = sections;
        report.Outcome = FgbLoadOutcome.Loaded;
        view = new FgbBlobView(blob, (int)sectionCount);
        return true;
    }

    /// <summary>
    /// selfHash 的唯一算法实现点（编译器侧写入器 <c>FgbWriter</c> 调的就是本入口，不许抄第二份）：
    /// 全 blob FNV-1a，SelfHash 字段 8 字节按零参与。三段续链免整块复制（见 FnvHash M1-19 注）。
    /// </summary>
    public static ulong ComputeSelfHash(ReadOnlySpan<byte> blob)
    {
        ulong h = FnvHash.Hash64Continue(FnvHash.OffsetBasis64, blob.Slice(0, AbiLayout.FgbHeaderSelfHashOffset));
        h = FnvHash.Hash64ContinueZeros(h, AbiLayout.FgbHeaderSelfHashSize);
        return FnvHash.Hash64Continue(h, blob.Slice(AbiLayout.FgbHeaderSelfHashOffset + AbiLayout.FgbHeaderSelfHashSize));
    }

    // ── 段访问（TryOpen 已验完界与对齐，切 span 不会失败）───────────────────

    /// <summary>目录序访问：第 i 段的 fourcc。</summary>
    public uint FourccAt(int i) => DirEntry(i).Fourcc;

    /// <summary>目录序访问：第 i 段的 payload（零拷贝切片）。</summary>
    public ReadOnlySpan<byte> SectionAt(int i)
    {
        (_, ulong off, ulong len) = DirEntry(i);
        return _blob.Span.Slice((int)off, (int)len);
    }

    /// <summary>
    /// 按 fourcc 取段（目录序首个命中——LANG 一类同 fourcc 多段的逐段消费走 <see cref="SectionAt"/>）。
    /// false = 无此段：未知段被跳过、缺段由取用方决定语义，都不是错误。
    /// </summary>
    public bool TryGetSection(uint fourcc, out ReadOnlySpan<byte> payload)
    {
        for (int i = 0; i < _sectionCount; i++)
        {
            if (DirEntry(i).Fourcc != fourcc)
                continue;
            payload = SectionAt(i);
            return true;
        }
        payload = default;
        return false;
    }

    /// <summary>
    /// 按 fourcc 取段的**字节区间**（目录序首个命中）。span 在 <c>ref struct</c> 之外存不住，
    /// 而装载器要把十四个段的位置记在对象上——故给出 (offset,length) 形态。
    /// </summary>
    public bool TryGetSectionRange(uint fourcc, out int offset, out int length)
    {
        for (int i = 0; i < _sectionCount; i++)
        {
            (uint fc, ulong off, ulong len) = DirEntry(i);
            if (fc != fourcc) continue;
            offset = (int)off;
            length = (int)len;
            return true;
        }
        offset = 0;
        length = 0;
        return false;
    }

    /// <summary>
    /// 段的定长记录视图：<see cref="MemoryMarshal.Cast{TFrom,TTo}"/> 直读。
    /// false 的两个原因用 <paramref name="gate"/> 区分：无此段（None）/ 段长不是记录宽整倍数
    /// （RecordStride——半条记录不是记录，Cast 的静默截断在这里被挡成有声拒绝）。
    /// </summary>
    public bool TryRecords<T>(uint fourcc, out ReadOnlySpan<T> records, out FgbGate gate) where T : struct
    {
        records = default;
        if (!TryGetSection(fourcc, out ReadOnlySpan<byte> payload))
        {
            gate = FgbGate.None;
            return false;
        }
        int stride = SizeOf<T>.Bytes;
        if (payload.Length % stride != 0)
        {
            gate = FgbGate.RecordStride;
            return false;
        }
        records = MemoryMarshal.Cast<byte, T>(payload);
        gate = FgbGate.None;
        return true;
    }

    // ── 内部 ────────────────────────────────────────────────────────────────

    private (uint Fourcc, ulong Offset, ulong Length) DirEntry(int i)
    {
        ReadOnlySpan<byte> s = _blob.Span;
        int e = Abi.FgbHeaderSize + i * Abi.FgbSectionDirEntrySize;
        return (MemoryMarshal.Read<uint>(s.Slice(e + AbiLayout.FgbDirFourccOffset)),
                MemoryMarshal.Read<ulong>(s.Slice(e + AbiLayout.FgbDirOffsetOffset)),
                MemoryMarshal.Read<ulong>(s.Slice(e + AbiLayout.FgbDirLengthOffset)));
    }

    private uint ReadU32(int offset) => MemoryMarshal.Read<uint>(_blob.Span.Slice(offset));
    private ulong ReadU64(int offset) => MemoryMarshal.Read<ulong>(_blob.Span.Slice(offset));
    private ushort ReadU16(int offset) => MemoryMarshal.Read<ushort>(_blob.Span.Slice(offset));

    /// <summary>对齐到 <see cref="Abi.FgbSectionAlignment"/>（写入器/NODE 列布局同用一份，不许各抄）。</summary>
    public static ulong AlignUp(ulong v) =>
        (v + (ulong)(Abi.FgbSectionAlignment - 1)) & ~(ulong)(Abi.FgbSectionAlignment - 1);

    private static bool Reject(ref FgbLoadReport report, FgbGate gate, string detail)
    {
        // 容器门一律结构性：版本不匹配/损坏/越界只源于部署错配，必须响亮拒载而不是降级。
        report.Note(gate, FgbGateClass.Structural, true, false, detail);
        return false;
    }

    /// <summary>零依赖 SizeOf（fork Fqs.cs 181-189 的 sOne&lt;T&gt; 手法：单元素数组 AsBytes 长度）。</summary>
    private static class SizeOf<T> where T : struct
    {
        public static readonly int Bytes = MemoryMarshal.AsBytes(new T[1].AsSpan()).Length;
    }
}
