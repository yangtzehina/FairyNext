namespace FairyNext.Core.Fgb;

/// <summary>
/// FGB 装载门编号（平面五「装载门」不变量 1-3 的 M1-19 覆盖面；4/5 随 M1-22 的 DEPS/文件名
/// 语义进驻）。编号 append-only：LoadReport 里的门号是跨版本可读的诊断词表，永不重编号。
/// </summary>
public enum FgbGate : byte
{
    /// <summary>未被任何门拒绝（装载成功）。</summary>
    None = 0,
    /// <summary>字节数不足以容纳头/目录（门 1 的长度前置）。</summary>
    Truncated = 1,
    /// <summary>magic != "FGB1"（门 1）。</summary>
    Magic = 2,
    /// <summary>formatVersion 不精确匹配（门 1）——结构性拒载，不降级。</summary>
    FormatVersion = 3,
    /// <summary>宿主不是小端（LE-only 格式在 BE 宿主上 Cast 直读必错，拒载）。</summary>
    Endianness = 4,
    /// <summary>flags：LE 断言位缺失 / 压缩位置位 / 保留位非零（门 1）。</summary>
    HeaderFlags = 5,
    /// <summary>段目录条目数越界（在读目录之前拒——计数是外部输入，先门后分配）。</summary>
    SectionCount = 6,
    /// <summary>段 offset + length 越 blob 界，或段与头/目录重叠（门 2）。</summary>
    SectionBounds = 7,
    /// <summary>段 offset 不是 FgbSectionAlignment 对齐（门 2）。</summary>
    SectionMisaligned = 8,
    /// <summary>selfHash 重算不符（门 3；发布包可信任跳过）。</summary>
    SelfHash = 9,
    /// <summary>段字节长不是记录宽的整倍数（Cast 视图取用时报）。</summary>
    RecordStride = 10,
    /// <summary>NODE 段 payload 与「16B 头 + 逐列对齐布局」的精确尺寸不符（不收半列）。</summary>
    NodeSectionShape = 11,
}

/// <summary>
/// 装载报告（架构文档 <c>AssetServer.lastReport</c> 的 M1-19 子集：哪一门拒的 + 段尺寸清单；
/// patch 数/耗时等字段随 M1-22 的装载三操作进驻）。拒载路径必有 <see cref="RejectedBy"/> ≠ None
/// 与人读 <see cref="Detail"/>；成功路径填段清单供诊断面打印。
/// </summary>
public sealed class FgbLoadReport
{
    /// <summary>拒载的门号；None = 装载成功。</summary>
    public FgbGate RejectedBy;

    /// <summary>人读细节（拒载时：期望 vs 实得；成功时为空串）。</summary>
    public string Detail = "";

    /// <summary>blob 总字节数（进门就填，拒载报告也带）。</summary>
    public long BlobBytes;

    /// <summary>头声明的段数（目录读出后填）。</summary>
    public int SectionCount;

    /// <summary>成功时的段清单（fourcc + 字节长，目录序）；拒载时为 null。</summary>
    public (uint Fourcc, ulong Length)[]? Sections;
}
