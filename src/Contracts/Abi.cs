namespace FairyNext.Contracts;

// ============================================================================
// ABI 常量单一事实源（设计书 v1.2「契约执法机构」①②；架构文档「编译平面」机制 8）。
//
// 本文件是**唯一定义点**：跨界布局（80B QuadInstance、48B ClipEntry、route/flags 位域、
// PropId 分组、预算与格式版本）全部以**纯数据**形式声明——const 标量 + 可枚举的静态只读表。
// 三份生成物由 tools/AbiGen 从这些表产出：
//   src/Contracts/Abi.Layout.g.cs（C# 偏移/位域常量 + 定义点交叉校验）
//   src/Backend.Mock/AbiMock.g.cs（mock 后端同一组常量，独立副本）
//   shaders/abi.g.hlsl（HLSL include：结构体布局 + #define + 位域取值宏）
// 手写第二份偏移量在本仓库是缺陷，不是风格问题——C#↔HLSL 边界上手抄的错位表现为
// 无法归因的花屏而非编译错。
//
// 程序性纪律（append-only 定义点纪律，改动前必读）：
//  1. 改任何值 → `dotnet run --project tools/AbiGen` 重新生成三份产物 → 一并提交；
//  2. 改了二进制布局（大小/偏移/位域）→ bump 对应 FormatVersion；
//  3. id/位域 append-only：永不重编号、永不复用；分组之间留数字 gap 供增长；
//  4. CI 在内存重新生成并与已提交生成物逐字节比较——漂移即红灯
//     （tests/FairyNext.Tests「ABI 字节比对门」，验证平面不变量 10）。
//  生成器必须确定性：无日期、无环境变量、无绝对路径，仅按声明序输出。
// ============================================================================

/// <summary>ABI 字段的机器类型。生成器据此产出 HLSL 成员类型；Pad 为对齐填充，写零。</summary>
public enum AbiFieldKind : byte
{
    Float4 = 0,
    Float2 = 1,
    UInt32 = 2,
    Pad = 3,
    // ---- M1-19 追加（append-only）：FGB 头/NODE 列是 CPU 侧记录，不进 HLSL 生成物 ----
    UInt64 = 4,
    UInt16 = 5,
    Float32 = 6,
    // ---- M1-20b 追加（append-only）：FGB 定长记录里的有符号 32 位与单字节字段 ----
    Int32 = 7,
    UInt8 = 8,
}

/// <summary>生成物投放面：标量按此决定进哪几份产物（位域与字段表三份全进）。</summary>
[System.Flags]
public enum AbiScope : byte
{
    CSharp = 1,
    Hlsl = 2,
    All = CSharp | Hlsl,
}

/// <summary>定长记录的一个字段（偏移/宽度以字节计）。表内顺序 = 生成物输出顺序。</summary>
public readonly struct AbiField
{
    /// <summary>C# 生成物标识符词根（PascalCase）。</summary>
    public readonly string Name;
    /// <summary>HLSL 结构体成员名（与架构文档渲染平面同名）。</summary>
    public readonly string HlslName;
    public readonly int Offset;
    public readonly int Size;
    public readonly AbiFieldKind Kind;
    /// <summary>单行注释，原样进三份生成物。</summary>
    public readonly string Doc;

    public AbiField(string name, string hlslName, int offset, int size, AbiFieldKind kind, string doc)
    {
        Name = name;
        HlslName = hlslName;
        Offset = offset;
        Size = size;
        Kind = kind;
        Doc = doc;
    }
}

/// <summary>u32 内的一个位域（含保留段——表必须覆盖全 32 位，缺口即定义漏洞）。</summary>
public readonly struct AbiBitField
{
    public readonly string Name;
    public readonly int Shift;
    public readonly int Width;
    public readonly string Doc;

    public AbiBitField(string name, int shift, int width, string doc)
    {
        Name = name;
        Shift = shift;
        Width = width;
        Doc = doc;
    }
}

/// <summary>PropId 分组区间（组间 gap 供 append-only 增长）。</summary>
public readonly struct AbiPropGroup
{
    public readonly string Name;
    public readonly byte First;
    public readonly byte Last;
    public readonly string Doc;

    public AbiPropGroup(string name, byte first, byte last, string doc)
    {
        Name = name;
        First = first;
        Last = last;
        Doc = doc;
    }
}

/// <summary>单个属性 id。id 一经入库永不重编号、永不复用（改语义 = 新 id）。</summary>
public readonly struct AbiPropId
{
    public readonly string Name;
    public readonly byte Id;
    /// <summary>所属分组名，必须与 <see cref="Abi.PropGroups"/> 中某项同名且 id 落在其区间内。</summary>
    public readonly string Group;
    public readonly string Doc;

    public AbiPropId(string name, byte id, string group, string doc)
    {
        Name = name;
        Id = id;
        Group = group;
        Doc = doc;
    }
}

/// <summary>
/// FGB NODE 段的一根列（M1-19）。列序 = 表内声明序 = <c>NodeTable</c> 真值列声明序——
/// 「NODE 段列序与 NodeTable 列布局是同一份 ABI」（编译平面接缝，全项目最硬接缝）的数据本体：
/// 实例化 memcpy（每列一次 Array.Copy）的正确性由本表单源保证，运行时/编译器/生成物都
/// 只准从这里读列序，手写第二份列序是缺陷。改列（增/删/改宽/重排）必 bump FgbFormatVersion。
/// </summary>
public readonly struct AbiNodeColumn
{
    public readonly string Name;
    /// <summary>元素字节宽（列内 stride；全列宽之和 = <see cref="Abi.NodeBytesPerNode"/>）。</summary>
    public readonly int ElementSize;
    public readonly AbiFieldKind Kind;
    /// <summary>true = 列存**段内相对下标**，实例化 memcpy 后加基址回填（M1-22 消费；哨兵语义随其落地）。</summary>
    public readonly bool Rebase;
    public readonly string Doc;

    public AbiNodeColumn(string name, int elementSize, AbiFieldKind kind, bool rebase, string doc)
    {
        Name = name;
        ElementSize = elementSize;
        Kind = kind;
        Rebase = rebase;
        Doc = doc;
    }
}

/// <summary>
/// 一条 FGB 定长记录的完整布局（M1-20b）。「FGB 全部记录布局集中为纯数据 C# 文件」
/// （编译平面机制 8）在 M1-19 只覆盖了头/目录/NODE 列；本类型把**段内记录**一并纳入：
/// 写侧（<c>FgbFreezer</c>）与读侧（M1-22 装载、测试读回对账）都只从生成物的
/// <c>Fgb{Name}{Field}Offset</c> 取偏移，手抄第二份偏移量是缺陷。
/// 相邻字段必须首尾相接、末字段收口于 <see cref="Size"/>——生成物内含编译期断言。
/// 记录布局变更（增/删/改宽/重排）必 bump <see cref="Abi.FgbFormatVersion"/>；
/// 新字段只从末尾的保留区切（append-only）。
/// </summary>
public readonly struct AbiRecord
{
    /// <summary>记录名（生成物常量前缀 = <c>Fgb</c> + 本名）。</summary>
    public readonly string Name;
    /// <summary>记录字节数。</summary>
    public readonly int Size;
    /// <summary>字段表（声明序 = 偏移升序）。</summary>
    public readonly AbiField[] Fields;
    public readonly string Doc;

    public AbiRecord(string name, int size, AbiField[] fields, string doc)
    {
        Name = name;
        Size = size;
        Fields = fields;
        Doc = doc;
    }
}

/// <summary>FGB 段 fourcc（M1-19）。id append-only：值一经入库永不复用；未知 fourcc 读取器整段跳过。</summary>
public readonly struct AbiFourcc
{
    public readonly string Name;
    /// <summary>四字符按 little-endian u32（首字符在最低字节）。与 Name 的字节一致性有测试钉住。</summary>
    public readonly uint Value;
    public readonly string Doc;

    public AbiFourcc(string name, uint value, string doc)
    {
        Name = name;
        Value = value;
        Doc = doc;
    }
}

/// <summary>进生成物的标量常量。Value 直接取自本文件的 const，杜绝表与 const 两份值。</summary>
public readonly struct AbiScalar
{
    public readonly string Name;
    public readonly long Value;
    /// <summary>true = 生成物按十六进制输出（magic 之类按位读的值）。</summary>
    public readonly bool Hex;
    public readonly AbiScope Scope;
    public readonly string Doc;

    public AbiScalar(string name, long value, bool hex, AbiScope scope, string doc)
    {
        Name = name;
        Value = value;
        Hex = hex;
        Scope = scope;
        Doc = doc;
    }
}

public static class Abi
{
    // ---- FGB 容器（设计书 §4.8；头/段目录/NODE 列的记录布局见文件下方数据表，M1-19）----
    public const uint FgbMagic = 0x31424746;       // "FGB1" little-endian
    public const int FgbFormatVersion = 1;         // 布局变更必 bump（结构性不符 → 拒载）
    public const int FgbSectionAlignment = 16;     // 段 16B 对齐，定长记录 MemoryMarshal.Cast 直读
    public const int FgbHeaderSize = 64;           // FgbHeader 定长（16B 对齐；布局 = FgbHeaderFields）
    public const int FgbSectionDirEntrySize = 24;  // SectionDir 条目定长（布局 = FgbSectionDirFields）
    public const int NodeBytesPerNode = 80;        // NODE 段列元素宽之和 = NodeTable 真值列 80B/节点

    // ---- Quad 实例流（设计书 §4.2）----
    public const int QuadInstanceSize = 80;        // bytes/实例，16B 对齐；布局变更必 bump ShaderAbiVersion
    public const int QuadInstanceAlignment = 16;   // 段实例缓冲按此对齐（stride == QuadInstanceSize）
    public const int SegmentMaxTextures = 4;       // 段纹理槽（2bit 编码；WR TextureSet[3]+mask 独立收敛）
    public const int ClipEntrySize = 48;           // rect + soft + radii + slot；条目 0 = None 哨兵
    public const int ClipEntryAlignment = 16;      // 条目数组 16B 对齐，Cast 直读
    public const int PaintOrderStride = 16;        // 渲染单元 sortingOrder = paintOrder 下标 × 16（裁决 3）
    public const int ShaderAbiVersion = 1;

    // ---- 顶点流后端预算（fork 实测水位起步，溢出走阶梯降级 + 诊断高水位）----
    public const int TransformSlotBudget = 32;     // 槽 0 = identity
    public const int ClipEntryBudget = 16;         // Inherited/Owned 继承共享后按「裁剪域数」计（v1.3）
    public const int GpuFenceDepth = 4;            // 释放缓冲入 pending 队列，CPU fence 到期才真释放（v1.1）；
                                                   // WebGL2 无 fence，同一深度改按帧环形缓冲轮转

    // ---- 状态层（设计书 §4.4）----
    public const int MaxObservableProps = 64;      // 64bit 脏掩码硬顶：超出 = 编译错误，不做多 word 回退（v1.1）
    public const int CommandWaveLimit = 4;         // P3 波次上限，超限延帧 + 责任链诊断
    public const int LayoutMicroDrainLimit = 3;    // P5 兜底轮次（不变式快路径优先，v1.3）

    // ========================================================================
    // 以下为生成器读取的纯数据表。**表内顺序即生成物输出顺序**——重排表 = 重排三份
    // 生成物 = 字节比对门变红，这是有意的：顺序本身是契约的一部分。
    // ========================================================================

    /// <summary>进生成物的标量清单（值取自上方 const，单一来源）。</summary>
    public static readonly AbiScalar[] Scalars =
    {
        new AbiScalar("FgbMagic", FgbMagic, true, AbiScope.CSharp, "\"FGB1\" little-endian；头首 4 字节，不符即拒载"),
        new AbiScalar("FgbFormatVersion", FgbFormatVersion, false, AbiScope.CSharp, "精确匹配，不匹配 = 结构性拒载（不降级）"),
        new AbiScalar("FgbSectionAlignment", FgbSectionAlignment, false, AbiScope.CSharp, "段 16B 对齐，定长记录 Cast 直读"),
        new AbiScalar("QuadInstanceSize", QuadInstanceSize, false, AbiScope.All, "bytes/实例；实例缓冲 stride"),
        new AbiScalar("QuadInstanceAlignment", QuadInstanceAlignment, false, AbiScope.All, "实例数组对齐"),
        new AbiScalar("ClipEntrySize", ClipEntrySize, false, AbiScope.All, "bytes/裁剪条目；条目 0 = None 哨兵"),
        new AbiScalar("ClipEntryAlignment", ClipEntryAlignment, false, AbiScope.All, "条目数组对齐"),
        new AbiScalar("SegmentMaxTextures", SegmentMaxTextures, false, AbiScope.All, "段纹理槽上限（route.texSlot 2bit 编码的定义域）"),
        new AbiScalar("PaintOrderStride", PaintOrderStride, false, AbiScope.CSharp, "sortingOrder = paintOrder 下标 × 本值（派生，不独立维护）"),
        new AbiScalar("ShaderAbiVersion", ShaderAbiVersion, false, AbiScope.All, "实例布局变更必 bump——后端启动时比对"),
        new AbiScalar("TransformSlotBudget", TransformSlotBudget, false, AbiScope.All, "槽 0 = identity；溢出 → 该容器退 tier-2 重写 + 高水位"),
        new AbiScalar("ClipEntryBudget", ClipEntryBudget, false, AbiScope.All, "按裁剪域数计；溢出 → 父窗口降级 + 警告"),
        new AbiScalar("GpuFenceDepth", GpuFenceDepth, false, AbiScope.CSharp, "GPU 缓冲回收的 pending 队列深度上限；超深 = 门不过"),
        new AbiScalar("MaxObservableProps", MaxObservableProps, false, AbiScope.CSharp, "64bit 脏掩码硬顶；超出 = 编译错误"),
        new AbiScalar("CommandWaveLimit", CommandWaveLimit, false, AbiScope.CSharp, "P3 波次上限"),
        new AbiScalar("LayoutMicroDrainLimit", LayoutMicroDrainLimit, false, AbiScope.CSharp, "P5 兜底微排水轮次"),
        // ---- M1-19 追加（append-only：只在表尾续，不插空）----
        new AbiScalar("FgbHeaderSize", FgbHeaderSize, false, AbiScope.CSharp, "FgbHeader 定长（16B 对齐；布局 = FgbHeaderFields）"),
        new AbiScalar("FgbSectionDirEntrySize", FgbSectionDirEntrySize, false, AbiScope.CSharp, "SectionDir 条目定长（布局 = FgbSectionDirFields）"),
        new AbiScalar("NodeBytesPerNode", NodeBytesPerNode, false, AbiScope.CSharp, "NODE 段列元素宽之和 = NodeTable 真值列 80B/节点"),
        // ---- M1-20b 追加（append-only：只在表尾续，不插空）----
        new AbiScalar("FgbInstanceHeaderBytes", FgbInstanceHeaderBytes, false, AbiScope.CSharp, "实例块头定长；控制器状态与编译期 scratch 从其后切"),
    };

    /// <summary>
    /// QuadInstance 字段表（80B，16B 对齐）。相邻字段必须首尾相接（offset + size == 下一 offset），
    /// 末字段收口于 <see cref="QuadInstanceSize"/>——生成物内含编译期断言，测试另有表级自洽门。
    /// </summary>
    public static readonly AbiField[] QuadInstanceFields =
    {
        new AbiField("Rect", "rect", 0, 16, AbiFieldKind.Float4, "xy = 槽本地 min 角，zw = size"),
        new AbiField("UvA", "uvA", 16, 16, AbiFieldKind.Float4, "corner(0,0)+(1,0) UV —— 按角归位，不用 min/max"),
        new AbiField("UvB", "uvB", 32, 16, AbiFieldKind.Float4, "corner(0,1)+(1,1) UV"),
        new AbiField("Color", "color", 48, 4, AbiFieldKind.UInt32, "RGBA8 直通色（已乘 worldVisual α；未乘基色在 CPU 镜像 baseColor）"),
        new AbiField("Route", "route", 52, 4, AbiFieldKind.UInt32, "位域见 RouteBits：slot | clipIndex | texSlot"),
        new AbiField("Flags", "flags", 56, 4, AbiFieldKind.UInt32, "位域见 QuadFlagBits"),
        new AbiField("Aux", "aux", 60, 4, AbiFieldKind.UInt32, "按 kind 复用：glyphIndex / 圆角 pack / 渐变句柄 / 径向填充参数（定点）"),
        new AbiField("Extra", "extra", 64, 16, AbiFieldKind.Float4, "按 kind 复用：SDF 半径 / 曲线 em-bbox / 渐变端点 / 填充中心"),
    };

    /// <summary>
    /// ClipEntry 字段表（48B）。条目 0 = None 哨兵，shader 对其零采样；
    /// 实例经 route.clipIndex 间接引用，条目绑 transform 槽（槽动才由 CPU 重推 rect）。
    /// </summary>
    public static readonly AbiField[] ClipEntryFields =
    {
        new AbiField("Rect", "rect", 0, 16, AbiFieldKind.Float4, "槽本地 xMin,yMin,xMax,yMax（outer）"),
        new AbiField("Soft", "soft", 16, 8, AbiFieldKind.Float2, "软边梯度"),
        new AbiField("Radii", "radii", 24, 16, AbiFieldKind.Float4, "四角半径 —— 圆角矩形 mask 进流，不走 stencil"),
        new AbiField("Slot", "slot", 40, 4, AbiFieldKind.UInt32, "绑定的 transform 槽下标"),
        new AbiField("Pad", "_pad", 44, 4, AbiFieldKind.Pad, "对齐填充，写零（append-only：新字段从此处切）"),
    };

    /// <summary>
    /// QuadInstance.route 位域（u32，表须覆盖全 32 位）。clipIndex 12 位 = 4095 条目寻址上限，
    /// 远大于 <see cref="ClipEntryBudget"/>——预算是运行期水位，位宽是 ABI 天花板，两者不同源。
    /// </summary>
    public static readonly AbiBitField[] RouteBits =
    {
        new AbiBitField("Slot", 0, 8, "transform 槽下标（0 = identity）"),
        new AbiBitField("ClipIndex", 8, 12, "ClipEntry 下标（0 = None 哨兵，shader 零采样）"),
        new AbiBitField("TexSlot", 20, 2, "段内纹理槽（定义域 = SegmentMaxTextures）"),
        new AbiBitField("Reserved", 22, 10, "保留，写零（append-only：新位域从此处切）"),
    };

    /// <summary>
    /// QuadInstance.flags 位域（u32，表须覆盖全 32 位）。b16-17 是 texSlot 备用位：
    /// route.texSlot 若因新增位域被挤走，迁移到此处而不重编号既有位。
    /// </summary>
    public static readonly AbiBitField[] QuadFlagBits =
    {
        new AbiBitField("FontAlpha", 0, 1, "字体图集只有 α 通道，采样后当灰度用"),
        new AbiBitField("Sdf", 1, 2, "b1 = SDF fill，b2 = SDF border"),
        new AbiBitField("CurveGlyph", 3, 1, "曲线字形（band 数据纹理求值；M1 只占位不实现）"),
        new AbiBitField("Grayed", 4, 1, "worldVisual.grayed 落叶结果"),
        new AbiBitField("RadialFill", 5, 1, "径向填充（技能 CD）走 shader 求值，参数在 aux/extra——不落孤岛"),
        new AbiBitField("ReservedLow", 6, 2, "保留，写零"),
        new AbiBitField("BorderW", 8, 8, "描边宽度（u8 定点）"),
        new AbiBitField("TexSlotSpare", 16, 2, "texSlot 备用位（route 位域挤满时的迁移目的地）"),
        new AbiBitField("ReservedHigh", 18, 14, "保留，写零"),
    };

    /// <summary>
    /// <c>QuadInstance.aux</c> 在 <c>flags.radialFill</c> 置位时的位域（u32，表须覆盖全 32 位）。
    ///
    /// 径向填充（技能 CD）**不落孤岛**（v1.3 裁决 / 渲染平面机制 9）：参数进实例、由 shader 求值。
    /// 于是「一屏 40 个技能图标各转各的 CD」是 40 个普通实例，而不是 40 个栅栏。
    ///
    /// 本表是**参数记录**（作者意图的无损回读面：dump / 金样 / 诊断），
    /// <c>extra</c> 存的是同一组参数的**求值形态**（中心 + 起角 + 有符号扫角，见 <c>RadialFill</c>），
    /// 让 shader 不必按 method 分五路。两者由 CPU 侧同一个纯函数一次写出——**只有一个写口**，
    /// 因此不存在「两份参数各自漂移」。
    ///
    /// amount 走 u16 定点而非 f32：填充比是 0..1 的进度量，65535 级远细于任何屏幕上可分辨的角度，
    /// 而定点值在规范化流哈希里是稳定的位（f32 的最低位会随求值路径抖动，金样就此变成本机绿）。
    /// </summary>
    public static readonly AbiBitField[] RadialFillAuxBits =
    {
        new AbiBitField("Method", 0, 3, "FillMethod：1 水平 2 垂直 3 Radial90 4 Radial180 5 Radial360（0 = 无填充）"),
        new AbiBitField("Origin", 3, 3, "起点，按 method 解释（Origin90 / Origin180 / Origin360）"),
        new AbiBitField("Clockwise", 6, 1, "1 = 顺时针（屏幕视觉；全链路 y 向下）"),
        new AbiBitField("Reserved", 7, 9, "保留，写零（append-only：新位域从此处切）"),
        new AbiBitField("Amount", 16, 16, "完成比 u16 定点 = round(amount × 65535)"),
    };

    /// <summary>
    /// PropId 分组区间。分组之间留 gap 供增长；id 0 保留为 None 哨兵。
    /// 注意与 <see cref="MaxObservableProps"/> 的区别：PropId 是**节点属性 id 空间**（u8，分组编号），
    /// 64 位硬顶约束的是 VM 可观测属性的**位下标**，两者不是同一编号空间。
    /// </summary>
    public static readonly AbiPropGroup[] PropGroups =
    {
        new AbiPropGroup("Layout", 1, 63, "width/height/约束输入/autoSize —— Ch.Layout"),
        new AbiPropGroup("Visual", 64, 95, "alpha/tint/visible/grayed/touchable —— Ch.Color | Ch.Visible"),
        new AbiPropGroup("Text", 96, 127, "文本串/字号/描边 —— Ch.Text"),
        new AbiPropGroup("Transform", 128, 159, "x/y/scale/rotation/skew/pixelSnap —— Ch.Transform"),
    };

    /// <summary>
    /// 已启用的 PropId（本期只定义被现有代码/文档实际用到的少量 id，其余按需 append）。
    /// 表内按 id 升序，同组连续；新增 id 一律追加到所属分组末尾，永不插空重排。
    /// </summary>
    public static readonly AbiPropId[] PropIds =
    {
        new AbiPropId("Width", 1, "Layout", "authored 宽（布局输入；resolved 由 P5 独占写）"),
        new AbiPropId("Height", 2, "Layout", "authored 高"),
        new AbiPropId("Alpha", 64, "Visual", "局部 α（worldVisual 按积级联）"),
        new AbiPropId("Visible", 65, "Visual", "局部可见（worldVisual 按 AND 级联）"),
        new AbiPropId("Grayed", 66, "Visual", "局部置灰（worldVisual 按 OR 级联）"),
        new AbiPropId("Color", 67, "Visual", "tint 基色（未乘 α，Color 通道从基色重乘）"),
        new AbiPropId("Touchable", 68, "Visual", "局部可命中（worldVisual 按 AND 级联；事件平面 P0 读）"),
        new AbiPropId("TextId", 96, "Text", "字符串表 id（STRT/LANG 视图叠加后取值）"),
        new AbiPropId("FontSize", 97, "Text", "字号（逻辑单位；度量与栅格密度解耦）"),
        new AbiPropId("X", 128, "Transform", "authored 位置 x（节点原点在父空间）"),
        new AbiPropId("Y", 129, "Transform", "authored 位置 y（y 向下）"),
        new AbiPropId("ScaleX", 130, "Transform", "缩放 x（永远只有 authored 一份）"),
        new AbiPropId("ScaleY", 131, "Transform", "缩放 y"),
        new AbiPropId("Rotation", 132, "Transform", "旋转（弧度，绕 pivot）"),
        new AbiPropId("Skew", 133, "Transform", "剪切角（弧度，水平剪切；fork 的双值 skewX/skewY 编译期归一）"),
        new AbiPropId("PixelSnap", 134, "Transform", "像素对齐位（不级联；存 localVisual 位段，通道归 Transform）"),
    };

    // ========================================================================
    // FGB 容器记录布局（M1-19）。「FGB 全部记录布局集中为纯数据 C# 文件」（编译平面机制 8）
    // 从此处开始兑现：头、段目录、NODE 列序都是数据表，写入器/读取器/生成物同源消费。
    // ========================================================================

    /// <summary>
    /// FgbHeader 字段表（64B，16B 对齐）。与架构文档草图的唯一偏离：@12 插 4B 填充使三个
    /// u64 哈希 16B 对齐（草图把 selfHash 排在 flags 后的 @12，非对齐 u64 在 Cast 直读的
    /// 世界里是隐患）；scaleLevel/branchId/sectionCount 顺序照草图，尾部 16B 保留写零。
    /// </summary>
    public static readonly AbiField[] FgbHeaderFields =
    {
        new AbiField("Magic", "magic", 0, 4, AbiFieldKind.UInt32, "「FGB1」little-endian；不符拒载（装载门 1）"),
        new AbiField("FormatVersion", "formatVersion", 4, 4, AbiFieldKind.UInt32, "精确匹配；不符 = 结构性拒载，不降级（装载门 1）"),
        new AbiField("Flags", "flags", 8, 4, AbiFieldKind.UInt32, "位域见 FgbFlagBits；未知位置位 = 拒载（头无段粒度前向兼容）"),
        new AbiField("Reserved0", "_reserved0", 12, 4, AbiFieldKind.Pad, "对齐填充，写零（使 u64 哈希三连 16B 对齐）"),
        new AbiField("SelfHash", "selfHash", 16, 8, AbiFieldKind.UInt64, "全 blob FNV-1a，本字段按零参与散列；发布包可信任跳过（装载门 3）"),
        new AbiField("SourceHash", "sourceHash", 24, 8, AbiFieldKind.UInt64, "源 .fui 描述符字节哈希（四维身份 1/4）"),
        new AbiField("CombinedRefHash", "combinedRefHash", 32, 8, AbiFieldKind.UInt64, "链上全部被引用包 sourceHash，id 去重 ordinal 排序（四维身份 2/4）"),
        new AbiField("ScaleLevel", "scaleLevel", 40, 2, AbiFieldKind.UInt16, "内容缩放档（四维身份 3/4）"),
        new AbiField("BranchId", "branchId", 42, 2, AbiFieldKind.UInt16, "branch 变体（四维身份 4/4）"),
        new AbiField("SectionCount", "sectionCount", 44, 4, AbiFieldKind.UInt32, "段目录条目数（目录紧随头部）"),
        new AbiField("Reserved1", "_reserved1", 48, 16, AbiFieldKind.Pad, "保留，写零（append-only：新字段从此处切）"),
    };

    /// <summary>SectionDir 条目字段表（24B；目录数组紧随 64B 头，条目 0 起）。</summary>
    public static readonly AbiField[] FgbSectionDirFields =
    {
        new AbiField("Fourcc", "fourcc", 0, 4, AbiFieldKind.UInt32, "段 id（FgbSectionIds）；未知 fourcc 整段跳过——前向兼容只保留在段粒度"),
        new AbiField("Reserved", "_reserved", 4, 4, AbiFieldKind.Pad, "保留，写零"),
        new AbiField("Offset", "offset", 8, 8, AbiFieldKind.UInt64, "段起点（blob 内字节偏移；必须 FgbSectionAlignment 对齐，装载门 2）"),
        new AbiField("Length", "length", 16, 8, AbiFieldKind.UInt64, "段字节长（offset + length ≤ blob 长，装载门 2）"),
    };

    /// <summary>FgbHeader.flags 位域（u32，表覆盖全 32 位）。保留位非零 = 拒载。</summary>
    public static readonly AbiBitField[] FgbFlagBits =
    {
        new AbiBitField("LittleEndian", 0, 1, "LE 断言位：写入器恒置 1；读到 0 = 大端产物，拒载"),
        new AbiBitField("Compressed", 1, 1, "压缩位：M1 不支持，置位即拒载（结构性）"),
        new AbiBitField("Reserved", 2, 30, "保留，写零；非零拒载"),
    };

    /// <summary>
    /// FGB 段 fourcc 清单（架构文档「FGB 顶层布局」全段；M1-19 只有 NODE 有消费者，
    /// 其余先占 id——id 空间 append-only，永不复用）。值 = 四字符 little-endian u32。
    /// </summary>
    public static readonly AbiFourcc[] FgbSectionIds =
    {
        new AbiFourcc("Strt", 0x54525453, "不可变字符串表"),
        new AbiFourcc("Lang", 0x474E414C, "每语言一段补丁（视图叠加，不改 STRT；同 fourcc 多段）"),
        new AbiFourcc("Comp", 0x504D4F43, "ComponentDef[]"),
        new AbiFourcc("Node", 0x45444F4E, "NodeRecord SoA 分列子段（列序 = NodeColumns 表）"),
        new AbiFourcc("Plan", 0x4E414C50, "InstStep[] 后序扁平实例化计划"),
        new AbiFourcc("Quad", 0x44415551, "QuadInstance[]（80B shader ABI）"),
        new AbiFourcc("Segs", 0x53474553, "段表（冻结布局）"),
        new AbiFourcc("Leaf", 0x4641454C, "叶表"),
        new AbiFourcc("Clip", 0x50494C43, "ClipEntry 表"),
        new AbiFourcc("Ptch", 0x48435450, "UvPatch[] 跨包 UV 装载期回填"),
        new AbiFourcc("Cnst", 0x54534E43, "增量约束图（ConstraintOp + FanOut CSR）"),
        new AbiFourcc("Bind", 0x444E4942, "StateProgram（归状态平面）"),
        new AbiFourcc("Anim", 0x4D494E41, "Timeline/Track/Key 冻结记录"),
        new AbiFourcc("Sprt", 0x54525053, "sprite rects"),
        new AbiFourcc("Tref", 0x46455254, "纹理/声音符号引用"),
        new AbiFourcc("Deps", 0x53504544, "依赖包 { pkgId, expectedSourceHash }[]"),
        new AbiFourcc("Hitt", 0x54544948, "像素点击测试位图"),
        new AbiFourcc("Brch", 0x48435242, "branch 元数据"),
        // ---- M1-20b 追加（append-only：只在表尾续，值永不复用）----
        new AbiFourcc("Cont", 0x544E4F43, "内容条目表（contentRef 的目标；组件级降回运行时 Extract 的源）"),
        new AbiFourcc("Locl", 0x4C434F4C, "localId ⇄ 编辑器 id 映射"),
    };

    /// <summary>
    /// NODE 段列序（M1-19）。表内声明序 = 段内列序 = <c>NodeTable</c> 真值列声明序，
    /// 三者由「本表单源 + 生成物字节比对 + 逐列对账测试」钉在一起；元素宽之和有编译期
    /// 断言收口于 <see cref="NodeBytesPerNode"/>。派生列（world/paintIndex/dirtyWord/gen…）
    /// 与槽簿记**不进本表**——它们不参与实例化 memcpy（NodeTable 的 SlotFlags 文件头有病因）。
    /// </summary>
    public static readonly AbiNodeColumn[] NodeColumns =
    {
        new AbiNodeColumn("Parent", 4, AbiFieldKind.UInt32, true, "父下标（0 = 无）"),
        new AbiNodeColumn("FirstChild", 4, AbiFieldKind.UInt32, true, "首子下标"),
        new AbiNodeColumn("NextSib", 4, AbiFieldKind.UInt32, true, "后兄下标（环形链）"),
        new AbiNodeColumn("PrevSib", 4, AbiFieldKind.UInt32, true, "前兄下标（环形链）"),
        new AbiNodeColumn("OwnerInst", 4, AbiFieldKind.UInt32, true, "所属组件实例节点下标"),
        new AbiNodeColumn("LocalId", 2, AbiFieldKind.UInt16, false, "模板内寻址 id（localId u16）"),
        new AbiNodeColumn("TypeId", 2, AbiFieldKind.UInt16, false, "节点类型 id"),
        new AbiNodeColumn("PosX", 4, AbiFieldKind.Float32, false, "authored 位置 x（节点原点在父空间）"),
        new AbiNodeColumn("PosY", 4, AbiFieldKind.Float32, false, "authored 位置 y（y 向下）"),
        new AbiNodeColumn("Width", 4, AbiFieldKind.Float32, false, "authored 宽"),
        new AbiNodeColumn("Height", 4, AbiFieldKind.Float32, false, "authored 高"),
        new AbiNodeColumn("ScaleX", 4, AbiFieldKind.Float32, false, "缩放 x"),
        new AbiNodeColumn("ScaleY", 4, AbiFieldKind.Float32, false, "缩放 y"),
        new AbiNodeColumn("Rotation", 4, AbiFieldKind.Float32, false, "旋转（弧度，绕 pivot）"),
        new AbiNodeColumn("Skew", 4, AbiFieldKind.Float32, false, "剪切角（弧度）"),
        new AbiNodeColumn("PivotX", 4, AbiFieldKind.Float32, false, "pivot x（比例）"),
        new AbiNodeColumn("PivotY", 4, AbiFieldKind.Float32, false, "pivot y（比例）"),
        new AbiNodeColumn("LocalVisual", 4, AbiFieldKind.UInt32, false, "局部视觉位段（alpha/visible/grayed/touchable/pixelSnap 打包）"),
        new AbiNodeColumn("ContentRef", 4, AbiFieldKind.UInt32, false, "内容侧表引用（不是节点下标，不回填）"),
        new AbiNodeColumn("StateRef", 4, AbiFieldKind.UInt32, false, "状态侧表引用"),
        new AbiNodeColumn("ResolvedRef", 4, AbiFieldKind.UInt32, false, "resolved 几何槽引用（实例化批量分配后不再变，不变量 7）"),
    };

    // ========================================================================
    // FGB 段内定长记录布局（M1-20b）。M1-19 只把头/目录/NODE 列做成数据表；冻结各段时
    // 出现的第二批记录（COMP/CONT/LOCL/LEAF/SEGS/TREF/STRT/CNST 头）在此收口——
    // 「FGB 全部记录布局集中为纯数据 C# 文件」（机制 8）自此对全段成立。
    // 记录字节数一律取 4/8/16 的整倍数使 MemoryMarshal.Cast 直读不越对齐；
    // 段起点的 16B 对齐由容器（FgbWriter）保证，记录内部只需自然对齐。
    // ========================================================================

    /// <summary>实例块头字节数（PLAN 实例化的定长部分；控制器状态与编译期 scratch 从其后切）。</summary>
    public const int FgbInstanceHeaderBytes = 16;

    /// <summary>
    /// COMP 记录：一个组件模板在包内的全部段区间。所有 Start/Count 都是**段内记录下标**
    /// （不是字节偏移）——段的记录宽由本表定，下标 × 宽即偏移，装载期不需要第二张目录。
    /// </summary>
    public static readonly AbiField[] FgbCompFields =
    {
        new AbiField("NameStr", "nameStr", 0, 4, AbiFieldKind.UInt32, "组件名在 STRT 的下标"),
        new AbiField("NameHash", "nameHash", 4, 4, AbiFieldKind.UInt32, "组件名 FNV-1a 32（AssetServer.Resolve 的键）"),
        new AbiField("NodeStart", "nodeStart", 8, 4, AbiFieldKind.UInt32, "NODE 段内节点起点（列内元素下标）"),
        new AbiField("NodeCount", "nodeCount", 12, 4, AbiFieldKind.UInt32, "节点数（含局部 0 = 组件根）"),
        new AbiField("QuadStart", "quadStart", 16, 4, AbiFieldKind.UInt32, "QUAD 段内实例起点"),
        new AbiField("QuadCount", "quadCount", 20, 4, AbiFieldKind.UInt32, "实例数（含 slack 预留）"),
        new AbiField("SegStart", "segStart", 24, 4, AbiFieldKind.UInt32, "SEGS 段内起点"),
        new AbiField("SegCount", "segCount", 28, 4, AbiFieldKind.UInt32, "段数"),
        new AbiField("LeafStart", "leafStart", 32, 4, AbiFieldKind.UInt32, "LEAF 段内起点"),
        new AbiField("LeafCount", "leafCount", 36, 4, AbiFieldKind.UInt32, "叶数"),
        new AbiField("ClipStart", "clipStart", 40, 4, AbiFieldKind.UInt32, "CLIP 段内起点"),
        new AbiField("ClipCount", "clipCount", 44, 4, AbiFieldKind.UInt32, "ClipEntry 数（含条目 0 哨兵）"),
        new AbiField("CnstOpStart", "cnstOpStart", 48, 4, AbiFieldKind.UInt32, "CNST 算子起点（数组索引即拓扑序）"),
        new AbiField("CnstOpCount", "cnstOpCount", 52, 4, AbiFieldKind.UInt32, "算子数（0 = 本组件无关系）"),
        new AbiField("CnstFanStart", "cnstFanStart", 56, 4, AbiFieldKind.UInt32, "FanOut CSR 桶起点（桶数 = nodeCount，掩码同起点）"),
        new AbiField("CnstIdxStart", "cnstIdxStart", 60, 4, AbiFieldKind.UInt32, "FanOut 算子下标池起点"),
        new AbiField("CnstIdxCount", "cnstIdxCount", 64, 4, AbiFieldKind.UInt32, "下标池长度"),
        new AbiField("LocalStart", "localStart", 68, 4, AbiFieldKind.UInt32, "LOCL 段内起点"),
        new AbiField("LocalCount", "localCount", 72, 4, AbiFieldKind.UInt32, "localId 条目数（= nodeCount）"),
        new AbiField("InstanceBytes", "instanceBytes", 76, 4, AbiFieldKind.UInt32, "实例块字节（内存计划是承诺不是估计，运行期断言 15）"),
        new AbiField("SourceWidth", "sourceWidth", 80, 4, AbiFieldKind.Float32, "组件源宽"),
        new AbiField("SourceHeight", "sourceHeight", 84, 4, AbiFieldKind.Float32, "组件源高"),
        new AbiField("CtrlCount", "ctrlCount", 88, 2, AbiFieldKind.UInt16, "控制器数（M1 恒 0——BIND 归 M2 状态层）"),
        new AbiField("Flags", "flags", 90, 2, AbiFieldKind.UInt16, "位域：bit0 有约束图 | bit1 有文本叶"),
        new AbiField("Reserved", "_reserved", 92, 4, AbiFieldKind.Pad, "保留，写零（append-only：新字段从此切）"),
    };

    /// <summary>
    /// CONT 记录：一个内容条目（<c>NodeTable.contentRef</c> 的目标）。降级阶梯的
    /// 「组件级降回运行时 Extract」读的就是本段——编译器即运行时，故 Extract 需要的
    /// 全部内容参数必须冻进 blob，否则降级路径无源可跑。
    /// </summary>
    public static readonly AbiField[] FgbContFields =
    {
        new AbiField("Kind", "kind", 0, 2, AbiFieldKind.UInt16, "0 = 无内容（哨兵 0 号记录）| 1 = 叶 | 2 = 孤岛（M1-23）"),
        new AbiField("Blend", "blend", 2, 1, AbiFieldKind.UInt8, "BlendClass（段键四类）"),
        new AbiField("Flags", "flags", 3, 1, AbiFieldKind.UInt8, "bit0 九宫格 | bit1 整图平铺 | bit2 开裁剪域 | bit3 文本叶"),
        new AbiField("TexId", "texId", 4, 4, AbiFieldKind.UInt32, "段键纹理 id（0 = 纯色；与 TREF 同源）"),
        new AbiField("BaseColor", "baseColor", 8, 4, AbiFieldKind.UInt32, "未乘 α 基色 RGBA8（颜色纯函数的定义域）"),
        new AbiField("EmitFlags", "emitFlags", 12, 4, AbiFieldKind.UInt32, "发射器 flags"),
        new AbiField("SlackHint", "slackHint", 16, 4, AbiFieldKind.Int32, "预留实例数下限（文本叶 = 有轮廓字形数 + 1，不依赖宽高）"),
        new AbiField("TileGridIndice", "tileGridIndice", 20, 4, AbiFieldKind.UInt32, "九宫平铺位掩码（M1 发射面拒发，有声计数）"),
        new AbiField("TextStr", "textStr", 24, 4, AbiFieldKind.UInt32, "文本串在 STRT 的下标（0 = 非文本）"),
        new AbiField("FillPack", "fillPack", 28, 4, AbiFieldKind.UInt32, "填充参数打包：method:8 | origin:8 | clockwise:1"),
        new AbiField("UvX0", "uvX0", 32, 4, AbiFieldKind.Float32, "UV xMin"),
        new AbiField("UvY0", "uvY0", 36, 4, AbiFieldKind.Float32, "UV yMin"),
        new AbiField("UvX1", "uvX1", 40, 4, AbiFieldKind.Float32, "UV xMax"),
        new AbiField("UvY1", "uvY1", 44, 4, AbiFieldKind.Float32, "UV yMax"),
        new AbiField("SourceWidth", "srcW", 48, 4, AbiFieldKind.Float32, "图元源宽（像素）"),
        new AbiField("SourceHeight", "srcH", 52, 4, AbiFieldKind.Float32, "图元源高（像素）"),
        new AbiField("GridX", "gridX", 56, 4, AbiFieldKind.Float32, "九宫格 x（源像素）"),
        new AbiField("GridY", "gridY", 60, 4, AbiFieldKind.Float32, "九宫格 y"),
        new AbiField("GridW", "gridW", 64, 4, AbiFieldKind.Float32, "九宫格 w"),
        new AbiField("GridH", "gridH", 68, 4, AbiFieldKind.Float32, "九宫格 h"),
        new AbiField("FillAmount", "fillAmount", 72, 4, AbiFieldKind.Float32, "填充完成比 [0,1]"),
        new AbiField("Reserved", "_reserved", 76, 4, AbiFieldKind.Pad, "保留，写零"),
    };

    /// <summary>
    /// LOCL 记录：localId ⇄ 编辑器 id 的冻结映射（M1-25 树查看器的 localId 路径、
    /// M1-22 的 PTCH/BIND 按 nodeLocalId 寻址都吃它）。
    /// </summary>
    public static readonly AbiField[] FgbLocalFields =
    {
        new AbiField("LocalId", "localId", 0, 2, AbiFieldKind.UInt16, "模板局部 id（0 = 组件根）"),
        new AbiField("Flags", "flags", 2, 2, AbiFieldKind.UInt16, "bit0 = GGroup 真节点化产生的组节点"),
        new AbiField("EditorIdStr", "editorIdStr", 4, 4, AbiFieldKind.UInt32, "编辑器 id 在 STRT 的下标（0 = 无）"),
        new AbiField("NameStr", "nameStr", 8, 4, AbiFieldKind.UInt32, "编辑器 name 在 STRT 的下标（0 = 无）"),
        new AbiField("EditorIdHash", "editorIdHash", 12, 4, AbiFieldKind.UInt32, "编辑器 id 的 FNV-1a 32（陈旧可检测：arm 时比哈希链）"),
    };

    /// <summary>LEAF 记录：一个叶在冻结流里的落位（区间与 route 三分量）。</summary>
    public static readonly AbiField[] FgbLeafFields =
    {
        new AbiField("LocalId", "localId", 0, 2, AbiFieldKind.UInt16, "叶节点的模板局部 id"),
        new AbiField("TexSlot", "texSlot", 2, 2, AbiFieldKind.UInt16, "叶在所属段内的纹理槽"),
        new AbiField("QuadStart", "quadStart", 4, 4, AbiFieldKind.UInt32, "首实例（**组件内相对**下标）"),
        new AbiField("QuadCount", "quadCount", 8, 4, AbiFieldKind.UInt32, "在用实例数"),
        new AbiField("QuadSlack", "quadSlack", 12, 4, AbiFieldKind.UInt32, "预留实例数（写区间上界，≥ quadCount）"),
        new AbiField("Segment", "segment", 16, 4, AbiFieldKind.UInt32, "所属段（组件内相对）"),
        new AbiField("Run", "run", 20, 4, AbiFieldKind.UInt32, "所属 run（组件内相对）"),
        new AbiField("ClipEntry", "clipEntry", 24, 4, AbiFieldKind.UInt32, "裁剪条目（组件内相对；0 = 不裁剪）"),
        new AbiField("Slot", "slot", 28, 4, AbiFieldKind.UInt32, "transform 槽（0 = identity；实例化时 arm 重认领）"),
        new AbiField("ContentRef", "contentRef", 32, 4, AbiFieldKind.UInt32, "CONT 下标（canonical id：同内容必同 id）"),
        new AbiField("EmitFlags", "emitFlags", 36, 4, AbiFieldKind.UInt32, "发射器 flags"),
    };

    /// <summary>SEGS 记录：一个段（≤4 纹理 × blendClass 的段键 + 实例区间）。</summary>
    public static readonly AbiField[] FgbSegFields =
    {
        new AbiField("Tex0", "tex0", 0, 4, AbiFieldKind.UInt32, "纹理槽 0"),
        new AbiField("Tex1", "tex1", 4, 4, AbiFieldKind.UInt32, "纹理槽 1"),
        new AbiField("Tex2", "tex2", 8, 4, AbiFieldKind.UInt32, "纹理槽 2"),
        new AbiField("Tex3", "tex3", 12, 4, AbiFieldKind.UInt32, "纹理槽 3"),
        new AbiField("TexCount", "texCount", 16, 1, AbiFieldKind.UInt8, "在用纹理槽数（≤ SegmentMaxTextures）"),
        new AbiField("Blend", "blend", 17, 1, AbiFieldKind.UInt8, "BlendClass（进段键，不是栅栏）"),
        new AbiField("Reserved", "_reserved", 18, 2, AbiFieldKind.Pad, "保留，写零"),
        new AbiField("QuadStart", "quadStart", 20, 4, AbiFieldKind.UInt32, "首实例（组件内相对）"),
        new AbiField("QuadCount", "quadCount", 24, 4, AbiFieldKind.UInt32, "实例数"),
        new AbiField("Run", "run", 28, 4, AbiFieldKind.UInt32, "所属 run（组件内相对）"),
    };

    /// <summary>TREF 记录：纹理/声音符号引用（装载期绑定为句柄，机制 2 的「绑定」那一步）。</summary>
    public static readonly AbiField[] FgbTexRefFields =
    {
        new AbiField("PkgId", "pkgId", 0, 8, AbiFieldKind.UInt64, "包 id 字符串的 FNV-1a 64（本包引用 = 自身 id）"),
        new AbiField("ItemStr", "itemStr", 8, 4, AbiFieldKind.UInt32, "图集条目 id 在 STRT 的下标"),
        new AbiField("Kind", "kind", 12, 2, AbiFieldKind.UInt16, "0 = 纹理（声音/骨骼按 append-only 续编号）"),
        new AbiField("TexId", "texId", 14, 2, AbiFieldKind.UInt16, "段键纹理 id（包内分配序；QUAD 段键与本表同源）"),
    };

    /// <summary>STRT 段头（不可变字符串表；LANG 补丁段视图叠加不改本段）。</summary>
    public static readonly AbiField[] FgbStrtHeaderFields =
    {
        new AbiField("Count", "count", 0, 4, AbiFieldKind.UInt32, "条目数（下标 0 = 空串哨兵）"),
        new AbiField("PoolBytes", "poolBytes", 4, 4, AbiFieldKind.UInt32, "UTF-8 池字节数"),
        new AbiField("Reserved", "_reserved", 8, 8, AbiFieldKind.Pad, "保留，写零（凑 16B 使条目数组自然对齐）"),
    };

    /// <summary>STRT 条目：UTF-8 池内的窗口。</summary>
    public static readonly AbiField[] FgbStrtEntryFields =
    {
        new AbiField("Offset", "offset", 0, 4, AbiFieldKind.UInt32, "池内字节偏移"),
        new AbiField("Length", "length", 4, 4, AbiFieldKind.UInt32, "字节长（UTF-8）"),
    };

    /// <summary>
    /// CNST 段头：四段数组的总长（算子 / FanOut 桶 / 下标池 / 归属掩码）。
    /// 组件的各自区间在 COMP 上；本头只回答「这段一共有多少条」，读侧据此切片。
    /// </summary>
    public static readonly AbiField[] FgbCnstHeaderFields =
    {
        new AbiField("OpCount", "opCount", 0, 4, AbiFieldKind.UInt32, "ConstraintOp 总条数（8B/条）"),
        new AbiField("FanCount", "fanCount", 4, 4, AbiFieldKind.UInt32, "FanOut 桶总数（4B/条；= 各组件 nodeCount 之和）"),
        new AbiField("IndexCount", "indexCount", 8, 4, AbiFieldKind.UInt32, "FanOut 算子下标池长度（2B/条）"),
        new AbiField("MaskCount", "maskCount", 12, 4, AbiFieldKind.UInt32, "布局归属掩码字节数（1B/节点；= fanCount）"),
    };

    /// <summary>
    /// FGB 定长记录清单（M1-20b）。生成物按本表出 <c>Fgb{Name}Size</c> 与
    /// <c>Fgb{Name}{Field}Offset/Size</c> 常量 + 编译期首尾相接断言 + Verify 交叉校验。
    /// **append-only**：记录只在表尾追加，字段只从各自保留区切。
    /// </summary>
    public static readonly AbiRecord[] FgbRecords =
    {
        new AbiRecord("Comp", 96, FgbCompFields, "COMP 记录（组件模板的全段区间）"),
        new AbiRecord("Cont", 80, FgbContFields, "CONT 记录（内容条目 = contentRef 的目标）"),
        new AbiRecord("Local", 16, FgbLocalFields, "LOCL 记录（localId ⇄ 编辑器 id）"),
        new AbiRecord("Leaf", 40, FgbLeafFields, "LEAF 记录（叶在冻结流里的落位）"),
        new AbiRecord("Seg", 32, FgbSegFields, "SEGS 记录（段键 + 实例区间）"),
        new AbiRecord("TexRef", 16, FgbTexRefFields, "TREF 记录（纹理符号引用）"),
        new AbiRecord("StrtHeader", 16, FgbStrtHeaderFields, "STRT 段头"),
        new AbiRecord("StrtEntry", 8, FgbStrtEntryFields, "STRT 条目"),
        new AbiRecord("CnstHeader", 16, FgbCnstHeaderFields, "CNST 段头（四段数组总长）"),
    };
}
