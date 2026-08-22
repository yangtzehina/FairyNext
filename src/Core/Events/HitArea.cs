using FairyNext.Numerics;

namespace FairyNext.Core.Events;

/// <summary>
/// 命中策略（架构平面四 B：节点 SoA 一列 <c>byte hitMode</c> + 冷侧表）。
/// **封闭集、append-only**：编译产物里的 hitMode 是字节级契约。
/// </summary>
public enum HitMode : byte
{
    /// <summary>默认：自身命中区 = resolved 框（<c>[0,w) × [0,h)</c> 的局部矩形）。</summary>
    Rect = 0,

    /// <summary>
    /// 命中黑洞：本节点**及其整棵子树**不参与命中（fork <c>Flags.TouchDisabled</c> 同语义）。
    /// 与 <c>touchable=false</c> 的区别是它不动 <see cref="Visual"/> 位、不走级联下行通道——
    /// 「这块区域永远不吃点击」是布局性事实，不该每次改都推一遍子树重算。
    /// </summary>
    None = 1,

    /// <summary>1bit 位图逐点判定（零拷贝引自 FGB blob；见 <see cref="PixelHitMask"/>）。</summary>
    PixelTest = 2,

    /// <summary>形状域：显式局部矩形（M1 面；圆角/网格随 M2 的矢量形状进驻）。</summary>
    Shape = 3,

    /// <summary>自定义回调（<see cref="IHitArea"/>）。</summary>
    Custom = 4,
}

/// <summary>自定义命中区（<see cref="HitMode.Custom"/> 的实现面）。</summary>
public interface IHitArea
{
    /// <summary>点是否落在命中区内。</summary>
    /// <param name="contentRect">节点 resolved 框（局部空间，原点在左上角）。</param>
    /// <param name="local">局部空间的点。</param>
    bool HitTest(in Rect contentRect, Vector2 local);
}

/// <summary>
/// 1bit 命中位图（**零拷贝**：直接引用 FGB blob 的字节数组 + 偏移，不复制、不解包）。
/// 字段形态与 fork 的 <c>PixelHitTestData</c> 逐字段对应，故 <see cref="PixelHitTest"/>
/// 的判定式可以逐行照搬而不引入第二套坐标换算。
/// </summary>
public readonly struct PixelHitMask
{
    /// <summary>位图所在的字节数组（blob 原数组）。</summary>
    public readonly byte[] Pixels;
    /// <summary>位图在数组中的起点。</summary>
    public readonly int Offset;
    /// <summary>位图字节数。</summary>
    public readonly int Length;
    /// <summary>位图宽（像素）。</summary>
    public readonly int PixelWidth;
    /// <summary>源像素 → 位图像素的缩放（= 1 / 采样步长；fork 的 <c>scale</c>）。</summary>
    public readonly float Scale;
    /// <summary>位图在源图中的 x 偏移（源像素）。</summary>
    public readonly int OffsetX;
    /// <summary>位图在源图中的 y 偏移（源像素）。</summary>
    public readonly int OffsetY;
    /// <summary>源图宽（逻辑框 → 源像素的换算分子）。</summary>
    public readonly float SourceWidth;
    /// <summary>源图高。</summary>
    public readonly float SourceHeight;

    /// <summary>建一张位图引用。</summary>
    public PixelHitMask(byte[] pixels, int offset, int length, int pixelWidth, float scale,
        int offsetX, int offsetY, float sourceWidth, float sourceHeight)
    {
        Pixels = pixels;
        Offset = offset;
        Length = length;
        PixelWidth = pixelWidth;
        Scale = scale;
        OffsetX = offsetX;
        OffsetY = offsetY;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    /// <summary>是否是一张可判定的位图（无字节 = 不可判定，判定式一律不命中）。</summary>
    public bool IsValid => Pixels != null && Length > 0 && PixelWidth > 0;
}

/// <summary>
/// 1bit 位图命中判定。
///
/// 移植自 oracle <c>Assets/Scripts/Core/HitTest/PixelHitTest.cs</c> 行 32-81
/// （<c>PixelHitTest.HitTest</c> 与 <c>PixelHitTestData</c> 的字段面）@ oracle 08a2d56。
/// 改写只有两处，都不动数值：
///  ① <c>UnityEngine.Rect/Vector2/Mathf</c> → <c>FairyNext.Numerics</c> 同名 shim；
///  ② 数据从对象字段改为 <see cref="PixelHitMask"/> 的 <c>in</c> 参数（无状态、可并行）。
///
/// **判定式逐位保留**（像素对照门要与 oracle 同判）：
///  - <c>x/y = floor((local × source / rect - offset) × scale)</c>；
///  - 上界只查 <c>x &gt;= pixelWidth</c>，<c>y</c> 的上界由 <c>pos2 &lt; Length</c> 兜住——
///    这是 fork 的原样形态，不是遗漏：改成显式查 y 会在最后一行的边角上与 oracle 分叉。
/// </summary>
public static class PixelHitTest
{
    /// <summary>点是否落在位图的不透明位上。</summary>
    /// <param name="mask">位图引用。</param>
    /// <param name="contentRect">节点 resolved 框（局部空间）。</param>
    /// <param name="local">局部空间的点。</param>
    public static bool Hit(in PixelHitMask mask, in Rect contentRect, Vector2 local)
    {
        if (!mask.IsValid) return false;
        if (!contentRect.Contains(local)) return false;
        if (contentRect.width == 0f || contentRect.height == 0f) return false;

        int x = (int)MathF.Floor((local.x * mask.SourceWidth / contentRect.width - mask.OffsetX) * mask.Scale);
        int y = (int)MathF.Floor((local.y * mask.SourceHeight / contentRect.height - mask.OffsetY) * mask.Scale);
        if (x < 0 || y < 0 || x >= mask.PixelWidth) return false;

        int pos = y * mask.PixelWidth + x;
        int pos2 = pos / 8;
        int pos3 = pos % 8;

        if (pos2 >= 0 && pos2 < mask.Length)
            return ((mask.Pixels[mask.Offset + pos2] >> pos3) & 0x1) > 0;
        return false;
    }
}

/// <summary>
/// 命中策略侧表（**冷侧表**：多数节点走默认，零成本）。
///
/// 三条默认值都不是随手定的：
///  · <see cref="HitMode.Rect"/>——没人声明特殊命中区时，命中区就是 resolved 框；
///  · <c>touchChildren = true</c>——容器默认让孩子先吃点击（fork 同款）；
///  · <c>opaque</c> 由 <b>typeId 推定</b>：叶（Image/Text/Shape/Loader）恒兜底，
///    容器（Component/Group/List/Island）默认**不**兜底。这正是 fork 的两条路径
///    （<c>DisplayObject.HitTest</c> 恒返回自己 vs <c>Container.HitTest_Container</c> 要
///    <c>opaque</c>）在单树上的合并形态——单树没有 DisplayObject/Container 之分，
///    差别只剩「这个节点是不是内容叶」。
///
/// 为什么这三个位不进 <see cref="Visual"/> 位域（明码边界）：它们是**命中策略**，
/// 唯一消费者是 P0，既不参与 worldVisual 级联、也没有渲染侧读者，进位域就得给它们
/// 编一个上行通道与一次排水——而没有任何排水器会消费。真需要 gear/timeline 驱动
/// （典型：controller 翻页改 touchable 那一类）时再进 PropId 空间，那时它自带通道。
/// </summary>
public sealed class HitPolicyTable
{
    [Flags]
    private enum Bits : byte
    {
        None = 0,
        Present = 1 << 0,          // 本节点有显式声明（否则整条按默认走）
        OpaqueSet = 1 << 1,
        Opaque = 1 << 2,
        TouchChildrenSet = 1 << 3,
        TouchChildren = 1 << 4,
    }

    private readonly NodeTable _table;
    private HitMode[] _mode = Array.Empty<HitMode>();
    private Bits[] _bits = Array.Empty<Bits>();
    private PixelHitMask[] _masks = Array.Empty<PixelHitMask>();
    private Rect[] _shapes = Array.Empty<Rect>();
    private IHitArea?[] _custom = Array.Empty<IHitArea?>();

    /// <summary>接一棵树（只读它的 typeId 推定默认 opaque）。</summary>
    public HitPolicyTable(NodeTable table) =>
        _table = table ?? throw new ArgumentNullException(nameof(table));

    /// <summary>有显式策略声明的节点数（诊断：冷侧表真的冷）。</summary>
    public int DeclaredCount { get; private set; }

    /// <summary>设命中模式。</summary>
    public void SetMode(NodeHandle node, HitMode mode)
    {
        if (!Slot(node, out uint i)) return;
        _mode[i] = mode;
    }

    /// <summary>设兜底位（无孩子命中时自身是否成为目标）。</summary>
    public void SetOpaque(NodeHandle node, bool opaque)
    {
        if (!Slot(node, out uint i)) return;
        _bits[i] |= Bits.OpaqueSet;
        if (opaque) _bits[i] |= Bits.Opaque; else _bits[i] &= ~Bits.Opaque;
    }

    /// <summary>设「孩子是否参与命中」。</summary>
    public void SetTouchChildren(NodeHandle node, bool value)
    {
        if (!Slot(node, out uint i)) return;
        _bits[i] |= Bits.TouchChildrenSet;
        if (value) _bits[i] |= Bits.TouchChildren; else _bits[i] &= ~Bits.TouchChildren;
    }

    /// <summary>挂一张 1bit 位图并把模式切到 <see cref="HitMode.PixelTest"/>。</summary>
    public void SetPixelMask(NodeHandle node, in PixelHitMask mask)
    {
        if (!Slot(node, out uint i)) return;
        _masks[i] = mask;
        _mode[i] = HitMode.PixelTest;
    }

    /// <summary>设形状域（局部矩形）并把模式切到 <see cref="HitMode.Shape"/>。</summary>
    public void SetShape(NodeHandle node, in Rect localRect)
    {
        if (!Slot(node, out uint i)) return;
        _shapes[i] = localRect;
        _mode[i] = HitMode.Shape;
    }

    /// <summary>挂自定义命中区并把模式切到 <see cref="HitMode.Custom"/>。</summary>
    public void SetCustom(NodeHandle node, IHitArea area)
    {
        if (!Slot(node, out uint i)) return;
        _custom[i] = area;
        _mode[i] = HitMode.Custom;
    }

    /// <summary>清掉一个节点的全部策略声明（回到默认）。</summary>
    public void Clear(NodeHandle node)
    {
        if (!_table.TryResolve(node, out uint i) || i >= (uint)_bits.Length) return;
        if ((_bits[i] & Bits.Present) != 0) DeclaredCount--;
        _bits[i] = Bits.None;
        _mode[i] = HitMode.Rect;
        _masks[i] = default;
        _shapes[i] = default;
        _custom[i] = null;
    }

    /// <summary>读命中模式。</summary>
    public HitMode ModeOf(NodeHandle node) =>
        _table.TryResolve(node, out uint i) ? ModeAt(i) : HitMode.Rect;

    /// <summary>读兜底位（含 typeId 推定的默认）。</summary>
    public bool OpaqueOf(NodeHandle node) => _table.TryResolve(node, out uint i) && OpaqueAt(i);

    /// <summary>读「孩子是否参与命中」。</summary>
    public bool TouchChildrenOf(NodeHandle node) =>
        !_table.TryResolve(node, out uint i) || TouchChildrenAt(i);

    // ── 下标面（命中下行每步都问，句柄化太贵）──────────────────────────────

    internal HitMode ModeAt(uint i) => i < (uint)_mode.Length ? _mode[i] : HitMode.Rect;

    internal bool OpaqueAt(uint i)
    {
        if (i < (uint)_bits.Length && (_bits[i] & Bits.OpaqueSet) != 0)
            return (_bits[i] & Bits.Opaque) != 0;
        return IsLeafType(_table.TypeAt(i));
    }

    internal bool TouchChildrenAt(uint i)
    {
        if (i < (uint)_bits.Length && (_bits[i] & Bits.TouchChildrenSet) != 0)
            return (_bits[i] & Bits.TouchChildren) != 0;
        return true;
    }

    internal PixelHitMask MaskAt(uint i) => i < (uint)_masks.Length ? _masks[i] : default;
    internal Rect ShapeAt(uint i) => i < (uint)_shapes.Length ? _shapes[i] : default;
    internal IHitArea? CustomAt(uint i) => i < (uint)_custom.Length ? _custom[i] : null;

    /// <summary>叶类型判据（内容叶恒兜底；容器要显式 opaque）。</summary>
    internal static bool IsLeafType(ushort typeId) =>
        typeId == NodeType.Image || typeId == NodeType.Text ||
        typeId == NodeType.Shape || typeId == NodeType.Loader;

    private bool Slot(NodeHandle node, out uint i)
    {
        if (!_table.TryResolve(node, out i))
        {
            UiAssert.That(false, "HitPolicyTable 写于已失效句柄");
            return false;
        }
        Ensure(i);
        if ((_bits[i] & Bits.Present) == 0)
        {
            _bits[i] |= Bits.Present;
            DeclaredCount++;
        }
        return true;
    }

    private void Ensure(uint i)
    {
        if (i < (uint)_bits.Length) return;
        int cap = _bits.Length == 0 ? 32 : _bits.Length;
        while (cap <= i) cap *= 2;
        Array.Resize(ref _mode, cap);
        Array.Resize(ref _bits, cap);
        Array.Resize(ref _masks, cap);
        Array.Resize(ref _shapes, cap);
        Array.Resize(ref _custom, cap);
    }
}
