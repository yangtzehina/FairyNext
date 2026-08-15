namespace FairyNext.Core;

/// <summary>
/// typeId 词表（数据宪法：Component/Image/Text/Group/Island/Shape/…）。
/// **append-only**：id 一经入库永不重编号、永不复用——编译产物里的 typeId 是字节级契约。
/// </summary>
public static class NodeType
{
    /// <summary>未定型（slab 新槽的初值；活节点不应停在此值）。</summary>
    public const ushort None = 0;
    /// <summary>组件（模板实例根；树根也用它）。</summary>
    public const ushort Component = 1;
    /// <summary>位图/九宫/径向填充叶。</summary>
    public const ushort Image = 2;
    /// <summary>文本叶。</summary>
    public const ushort Text = 3;
    /// <summary>GGroup = 真节点（机制⑫：无内容、恒 identity 缩放、宽高为派生包围）。</summary>
    public const ushort Group = 4;
    /// <summary>孤岛挂载点（自定义材质/外部原生/stencil 括号）。</summary>
    public const ushort Island = 5;
    /// <summary>矢量形状叶。</summary>
    public const ushort Shape = 6;
    /// <summary>Loader（内容按 url 晚绑）。</summary>
    public const ushort Loader = 7;
    /// <summary>列表容器（虚拟列表的物理节点集宿主）。</summary>
    public const ushort List = 8;
}

/// <summary>
/// localVisual / worldVisual 的 u32 位域（数据宪法：<c>alpha:u8 | flags:u8 | pad:u16</c>）。
/// localVisual 是 authored 真值列（用户/gear/timeline 写），worldVisual 是 P6 派生列，
/// **两者共用本位域**——级联只改值不改布局，渲染平面读任一列都用同一组取值宏。
///
/// pad:u16 段在 worldVisual 里是 clip 域 id；在 localVisual 里保留写零。
/// 节点生命周期位**不在这里**（见 <c>SlotFlags</c> 的文件头注释：实例化 memcpy 会覆写本列）。
/// </summary>
public static class Visual
{
    /// <summary>alpha 位段起点（u8，255 = 不透明）。</summary>
    public const int AlphaShift = 0;
    /// <summary>alpha 位段掩码。</summary>
    public const uint AlphaMask = 0xFFu;

    /// <summary>可见（worldVisual 按 AND 级联）。</summary>
    public const uint Visible = 1u << 8;
    /// <summary>置灰（worldVisual 按 OR 级联）。</summary>
    public const uint Grayed = 1u << 9;
    /// <summary>可命中（worldVisual 按 AND 级联；事件平面 P0 读）。</summary>
    public const uint Touchable = 1u << 10;
    /// <summary>像素对齐（**不级联**：渲染平面读局部位，父节点的对齐意愿不传染子节点）。</summary>
    public const uint PixelSnap = 1u << 11;

    /// <summary>clip 域 id 位段起点（仅 worldVisual 使用；渲染平面 M1-11 接管其分配）。</summary>
    public const int ClipDomainShift = 16;
    /// <summary>clip 域 id 掩码。</summary>
    public const uint ClipDomainMask = 0xFFFFu << ClipDomainShift;

    /// <summary>新建节点的 localVisual 初值：不透明 + 可见 + 可命中。</summary>
    public const uint DefaultLocal = AlphaMask | Visible | Touchable;

    /// <summary>取 alpha（u8）。</summary>
    public static byte Alpha(uint v) => (byte)(v & AlphaMask);

    /// <summary>换 alpha（u8），其余位不动。</summary>
    public static uint WithAlpha(uint v, byte a) => (v & ~AlphaMask) | a;

    /// <summary>取 clip 域 id（仅 worldVisual 有意义）。</summary>
    public static ushort ClipDomain(uint v) => (ushort)((v & ClipDomainMask) >> ClipDomainShift);

    /// <summary>
    /// 级联一层（机制⑥ 的 worldVisual 算法，唯一实现点）：
    /// α = u8 积（<c>(pa·ca + 127)/255</c>，定点四舍五入，跨平台位确定）、
    /// visible/touchable = AND、grayed = OR、clip 域 = 继承父（域切换由渲染平面写）。
    /// </summary>
    public static uint Cascade(uint parentWorld, uint local)
    {
        uint a = (uint)((Alpha(parentWorld) * Alpha(local) + 127) / 255);
        uint flags = parentWorld & ClipDomainMask;
        if ((parentWorld & Visible) != 0 && (local & Visible) != 0) flags |= Visible;
        if ((parentWorld & Touchable) != 0 && (local & Touchable) != 0) flags |= Touchable;
        if (((parentWorld | local) & Grayed) != 0) flags |= Grayed;
        return a | flags;
    }
}
