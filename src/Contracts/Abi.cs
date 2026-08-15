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
    // ---- FGB 容器（设计书 §4.8）----
    public const uint FgbMagic = 0x31424746;       // "FGB1" little-endian
    public const int FgbFormatVersion = 1;         // 布局变更必 bump（结构性不符 → 拒载）
    public const int FgbSectionAlignment = 16;     // 段 16B 对齐，定长记录 MemoryMarshal.Cast 直读

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
        new AbiScalar("MaxObservableProps", MaxObservableProps, false, AbiScope.CSharp, "64bit 脏掩码硬顶；超出 = 编译错误"),
        new AbiScalar("CommandWaveLimit", CommandWaveLimit, false, AbiScope.CSharp, "P3 波次上限"),
        new AbiScalar("LayoutMicroDrainLimit", LayoutMicroDrainLimit, false, AbiScope.CSharp, "P5 兜底微排水轮次"),
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
}
