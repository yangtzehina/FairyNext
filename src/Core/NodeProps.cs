namespace FairyNext.Core;

/// <summary>
/// 属性的存储形态——生成器据此选写列代码，**不是**通道语义（通道见 <see cref="Ch"/>）。
/// 新增形态要同时改生成器的 <c>Emitter</c>：没有对应发射分支的形态是编译错，不会静默跳过。
/// </summary>
public enum NodePropStore : byte
{
    /// <summary>独占一条 float 列（<c>Column</c> = 列字段名）。等值切断走 BitEquals（NaN 视为相等）。</summary>
    FloatColumn = 0,
    /// <summary>α：入参 0..1 float，落 <c>Column</c>（u32 列）的 u8 位段。**等值切断在 u8 上做**。</summary>
    AlphaU8 = 1,
    /// <summary><c>Column</c>（u32 列）里的一个 <see cref="Visual"/> 位（<c>Bit</c> = 位常量名）。</summary>
    VisualBit = 2,
    /// <summary>
    /// 已声明通道归属、**列尚未接入**（<c>Pending</c> 必填，写明由哪个包接）。
    /// 生成器为它发射「断言 + 不写不脏」分支：属性存在于 id 空间但写不进去这件事是**响亮**的，
    /// 不是 switch 落到 default 的静默。
    /// </summary>
    Unbacked = 3,
}

/// <summary>
/// 属性 → 通道归属的**单一定义点**（不变量 1「通道归属封闭」的声明面）。
/// 每条声明一个 <see cref="Contracts.Abi.PropIds"/> 里的属性；id 不在这里重述——
/// 生成器按 <c>Name</c> 与 ABI 单源（<c>AbiLayout.PropId*</c> 常量）对账：
/// ABI 有而这里没有 = FNP001 编译错，这里有而 ABI 没有 = FNP002 编译错。
/// </summary>
/// <remarks>
/// <c>Channel</c> 必须是**单个上行通道位**（架构：每个可变属性属于且仅属于一个通道）；
/// 级联用的下行位走 <c>Down</c>（setter 可 Mark 多位，消费者仍是各自唯一的相位）。
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
internal sealed class NodePropAttribute : Attribute
{
    public NodePropAttribute(string name, Ch channel, NodePropStore store)
    {
        Name = name;
        Channel = channel;
        Store = store;
    }

    /// <summary>属性名，必须与 <c>Abi.PropIds</c> 中某项同名（id 由生成器从 ABI 单源取）。</summary>
    public string Name { get; }

    /// <summary>通道归属：**单个**上行通道位。</summary>
    public Ch Channel { get; }

    /// <summary>存储形态。</summary>
    public NodePropStore Store { get; }

    /// <summary>伴随的下行通道位（级联属性用；必须落在 <see cref="ChMask.Down"/> 内）。</summary>
    public Ch Down { get; set; }

    /// <summary>存储列字段名（FloatColumn/AlphaU8/VisualBit 必填）。</summary>
    public string Column { get; set; } = "";

    /// <summary><see cref="Visual"/> 上的位常量名（VisualBit 必填）。</summary>
    public string Bit { get; set; } = "";

    /// <summary>未接列的去处说明（Unbacked 必填）。</summary>
    public string Pending { get; set; } = "";
}

/// <summary>属性表的一行（生成物 <c>NodeProps.All</c> 的元素；诊断面与单测按此遍历全部属性）。</summary>
public readonly struct NodePropInfo
{
    /// <summary>PropId（取自 ABI 单源）。</summary>
    public readonly byte Id;
    /// <summary>属性名。</summary>
    public readonly string Name;
    /// <summary>通道归属（单个上行位）。</summary>
    public readonly Ch Channel;
    /// <summary>setter 实际 Mark 的位集 = <see cref="Channel"/> ∪ 下行伴随位。</summary>
    public readonly Ch Marks;
    /// <summary>存储形态。</summary>
    public readonly NodePropStore Store;

    /// <summary>列已接入（<see cref="Store"/> != <see cref="NodePropStore.Unbacked"/>）。</summary>
    public bool Backed => Store != NodePropStore.Unbacked;

    public NodePropInfo(byte id, string name, Ch channel, Ch marks, NodePropStore store)
    {
        Id = id;
        Name = name;
        Channel = channel;
        Marks = marks;
        Store = store;
    }

    public override string ToString() => Name + "#" + Id + "(" + Channel + ")";
}

// ============================================================================
// 归属表本体。**这张表就是「通道归属封闭」的执法输入**：
//   ① ABI 里有 id 而这里没声明 → 生成器报 FNP001（编译错），不是运行期断言；
//   ② 这里声明了 ABI 里没有的名字 → FNP002；
//   ③ 通道不是单个上行位 → FNP004；下行位不在 Down 掩码内 → FNP005；
//   ④ 列名/位名不存在或类型不符 → FNP006/FNP007。
// 于是「漏通道 = 编译期没有该 setter」从纪律变成机器执法（架构不变量 1）。
//
// 表内按 PropId 升序排列，仅为阅读方便：生成器按 id 排序输出，**声明顺序不影响生成物**
// （由生成器测试 g10「打乱声明顺序两次生成逐字节相等」钉住）。
// ============================================================================

[NodeProp("Width", Ch.Layout, NodePropStore.FloatColumn, Column = "_width")]
[NodeProp("Height", Ch.Layout, NodePropStore.FloatColumn, Column = "_height")]
[NodeProp("Alpha", Ch.Color, NodePropStore.AlphaU8, Column = "_localVisual", Down = Ch.DownColor)]
[NodeProp("Visible", Ch.Visible, NodePropStore.VisualBit, Column = "_localVisual", Bit = "Visible",
    Down = Ch.DownVisible)]
[NodeProp("Grayed", Ch.Color, NodePropStore.VisualBit, Column = "_localVisual", Bit = "Grayed",
    Down = Ch.DownColor)]
[NodeProp("Color", Ch.Color, NodePropStore.Unbacked,
    Pending = "tint 基色列随渲染平面 CPU 镜像 baseColor 落位（M1-11）")]
// touchable 归 Ch.Color 而非独立通道：它与 α/grayed 同住 localVisual，worldVisual 级联一并算。
// **必须带 Down**：touchable 在 Visual.Cascade 里是 AND 级联，父置 false 必须让整棵子树的
// worldVisual 重算，否则子节点仍自认为可点——命中测试会命中本该不可点的节点。这与 Alpha/
// Grayed/Visible 同构，是「参与级联 ⇒ 必带下行通道」的必然推论，不是可选优化。
// （M1-06 起既有形态漏此位，M1-09 标记待裁，此处裁定补齐；行为由级联下行门逐属性钉住。）
[NodeProp("Touchable", Ch.Color, NodePropStore.VisualBit, Column = "_localVisual", Bit = "Touchable",
    Down = Ch.DownColor)]
[NodeProp("TextId", Ch.Text, NodePropStore.Unbacked,
    Pending = "文本内容列随 TextCore 无状态排版落位（M1-18）")]
[NodeProp("FontSize", Ch.Text, NodePropStore.Unbacked,
    Pending = "字号列随 TextCore 无状态排版落位（M1-18）")]
[NodeProp("X", Ch.Transform, NodePropStore.FloatColumn, Column = "_posX")]
[NodeProp("Y", Ch.Transform, NodePropStore.FloatColumn, Column = "_posY")]
[NodeProp("ScaleX", Ch.Transform, NodePropStore.FloatColumn, Column = "_scaleX")]
[NodeProp("ScaleY", Ch.Transform, NodePropStore.FloatColumn, Column = "_scaleY")]
[NodeProp("Rotation", Ch.Transform, NodePropStore.FloatColumn, Column = "_rotation")]
[NodeProp("Skew", Ch.Transform, NodePropStore.FloatColumn, Column = "_skew")]
// pixelSnap 存 localVisual 位段但通道归 Transform：渲染平面读它决定是否 round 世界坐标，
// 消费者与 x/y 同一条通道，塞进 Color 会让一次对齐切换走错排水器。
[NodeProp("PixelSnap", Ch.Transform, NodePropStore.VisualBit, Column = "_localVisual", Bit = "PixelSnap")]
public static partial class NodeProps
{
    // All 由 tools/PropGen 生成（NodeProps.g.cs）。生成器不在场 = 本类型编译不过 —— 这是有意的：
    // 属性表缺席时应当**构建失败**，而不是退化成一张空表让 setter 静默失踪。

    /// <summary>按 id 查表（未登记返回 false）。</summary>
    public static bool TryGet(byte id, out NodePropInfo info)
    {
        NodePropInfo[] all = All;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].Id != id) continue;
            info = all[i];
            return true;
        }
        info = default;
        return false;
    }

    /// <summary>按名查表（未登记返回 false）。</summary>
    public static bool TryGet(string name, out NodePropInfo info)
    {
        NodePropInfo[] all = All;
        for (int i = 0; i < all.Length; i++)
        {
            if (!string.Equals(all[i].Name, name, StringComparison.Ordinal)) continue;
            info = all[i];
            return true;
        }
        info = default;
        return false;
    }

    /// <summary>属性的 Mark 位集；未登记的 id 返回 <see cref="Ch.None"/>。</summary>
    public static Ch MarksOf(byte id) => TryGet(id, out NodePropInfo info) ? info.Marks : Ch.None;
}
