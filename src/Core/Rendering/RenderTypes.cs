using System.Runtime.InteropServices;
using FairyNext.Contracts;
using FairyNext.Numerics;

namespace FairyNext.Core.Rendering;

// ============================================================================
// 渲染平面的跨界值类型（架构文档「平面三 · 渲染平面」核心数据结构）。
//
// 本文件是**运行时结构**的定义点，不是 ABI 的定义点：字节布局的唯一事实源仍是
// src/Contracts/Abi.cs 的数据表，这里的 [StructLayout(Size = …)] 与偏移全部引用其生成物
// （AbiLayout）。两者的关系是「声明 vs 执法」——写错任何一个偏移，
// tests 的 ABI 字节比对门与本文件类型加载期的 Size 校验会先后响。
//
// 为什么住在 Core 而不是某个后端工程：QuadInstance/ClipEntry/SegmentDesc 是
// RenderStream（M1-11，CPU 镜像）与全部后端（mock / Unity 段 / WebGL2 顶点流）之间的
// 共同货币。放进任一后端工程会逼另外两个后端引用它——Unity 打包时把 mock 后端一起带走。
// ============================================================================

/// <summary>
/// 纹理身份（资产侧 id，段键的组成部分）。**不是**后端句柄：同一份图集在三个后端各有各的
/// 原生对象，但 TexId 相同——规范化流哈希据此可以跨后端比较。0 = 无纹理（纯色 quad）。
/// </summary>
public readonly struct TexId : IEquatable<TexId>
{
    /// <summary>资产侧编号（0 = 无）。</summary>
    public readonly uint Value;

    /// <summary>按编号建。</summary>
    public TexId(uint value) { Value = value; }

    /// <summary>无纹理哨兵。</summary>
    public static readonly TexId None = default;

    /// <summary>是否为无纹理哨兵。</summary>
    public bool IsNone => Value == 0;

    /// <inheritdoc/>
    public bool Equals(TexId other) => Value == other.Value;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TexId t && Equals(t);
    /// <inheritdoc/>
    public override int GetHashCode() => (int)Value;
    /// <inheritdoc/>
    public override string ToString() => IsNone ? "TexId.None" : "TexId#" + Value;
}

/// <summary>
/// 混合类别（段键的另一半，裁决：**混合模式进段键、不是栅栏**）。
/// append-only：数值进段键的规范化哈希，重编号 = 全部金样失效。
/// M1 只需要 Normal；其余值随 M1-13 叶发射器与 M1-15 Unity 后端的材质表补齐语义，
/// 现在先把编号占住，免得后来插值重排。
/// </summary>
public enum BlendClass : byte
{
    /// <summary>常规 alpha 混合（src·a + dst·(1-a)）。</summary>
    Normal = 0,
    /// <summary>加法混合（粒子/发光；自成一段，遮挡序由相邻性排序保证）。</summary>
    Add = 1,
    /// <summary>正片叠底。</summary>
    Multiply = 2,
    /// <summary>滤色。</summary>
    Screen = 3,
    /// <summary>擦除（写 dst alpha）。</summary>
    Erase = 4,
}

/// <summary>
/// 后端流句柄。<see cref="Index"/> 是后端私有的池下标、<see cref="Gen"/> 是复用代际——
/// **两者都是非语义位**，规范化流哈希（<c>CanonicalStream</c>）一律剔除。
/// </summary>
public readonly struct StreamHandle : IEquatable<StreamHandle>
{
    /// <summary>后端池下标（1 起；0 = None）。</summary>
    public readonly int Index;
    /// <summary>复用代际（同下标复用时递增，抓 use-after-free）。</summary>
    public readonly uint Gen;

    /// <summary>按下标与代际建（后端内部用）。</summary>
    public StreamHandle(int index, uint gen) { Index = index; Gen = gen; }

    /// <summary>空句柄。</summary>
    public static readonly StreamHandle None = default;

    /// <summary>是否为空句柄。</summary>
    public bool IsNone => Index == 0;

    /// <inheritdoc/>
    public bool Equals(StreamHandle other) => Index == other.Index && Gen == other.Gen;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is StreamHandle h && Equals(h);
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Index, Gen);
    /// <inheritdoc/>
    public override string ToString() => IsNone ? "Stream.None" : $"Stream#{Index}@g{Gen}";
}

/// <summary>孤岛句柄（同 <see cref="StreamHandle"/>：池下标 + 代际，非语义位）。</summary>
public readonly struct IslandHandle : IEquatable<IslandHandle>
{
    /// <summary>后端池下标（1 起；0 = None）。</summary>
    public readonly int Index;
    /// <summary>复用代际。</summary>
    public readonly uint Gen;

    /// <summary>按下标与代际建（后端内部用）。</summary>
    public IslandHandle(int index, uint gen) { Index = index; Gen = gen; }

    /// <summary>空句柄。</summary>
    public static readonly IslandHandle None = default;

    /// <summary>是否为空句柄。</summary>
    public bool IsNone => Index == 0;

    /// <inheritdoc/>
    public bool Equals(IslandHandle other) => Index == other.Index && Gen == other.Gen;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is IslandHandle h && Equals(h);
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Index, Gen);
    /// <inheritdoc/>
    public override string ToString() => IsNone ? "Island.None" : $"Island#{Index}@g{Gen}";
}

/// <summary>
/// 离屏 pass 句柄。<see cref="PassHandle.None"/> = **主表面**——
/// 于是「画到主表面」与「画到某个 RT」在签名上是同一个参数的两个取值，
/// 调用方不需要两条 API，后端也不需要猜当前绑定的是谁。
/// </summary>
public readonly struct PassHandle : IEquatable<PassHandle>
{
    /// <summary>本帧内的 pass 序号（1 起；0 = 主表面）。</summary>
    public readonly int Index;

    /// <summary>按序号建（后端内部用）。</summary>
    public PassHandle(int index) { Index = index; }

    /// <summary>主表面（不是「没有 pass」——主表面就是最后那个 pass）。</summary>
    public static readonly PassHandle None = default;

    /// <summary>是否指向主表面。</summary>
    public bool IsMainSurface => Index == 0;

    /// <inheritdoc/>
    public bool Equals(PassHandle other) => Index == other.Index;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PassHandle h && Equals(h);
    /// <inheritdoc/>
    public override int GetHashCode() => Index;
    /// <inheritdoc/>
    public override string ToString() => IsMainSurface ? "Pass.Main" : "Pass#" + Index;
}

/// <summary>
/// GPU 常驻实例（**80B，16B 对齐**；字段序与偏移由 <see cref="Abi.QuadInstanceFields"/> 定义）。
/// <c>[StructLayout(Size = …)]</c> 把 ABI 尺寸变成**类型加载期**的校验：字段涨过 80B 即 TypeLoadException，
/// 缩小则被 Size 补齐、由测试的逐字节偏移门抓。
///
/// 字段语义见架构「平面三」核心数据结构；位域取值走 <see cref="AbiLayout"/> 的同名宏，
/// 本结构只提供最常用的三个 route 分量便捷访问器——手抄第二份移位常量是本仓库的缺陷。
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = Abi.QuadInstanceSize)]
public struct QuadInstance : IEquatable<QuadInstance>
{
    /// <summary>xy = 槽本地 min 角，zw = size。</summary>
    public Vector4 Rect;
    /// <summary>corner(0,0)+(1,0) UV —— 按角归位，不用 min/max。</summary>
    public Vector4 UvA;
    /// <summary>corner(0,1)+(1,1) UV。</summary>
    public Vector4 UvB;
    /// <summary>RGBA8 直通色（已乘 worldVisual α；未乘基色在 CPU 镜像 baseColor）。</summary>
    public uint Color;
    /// <summary>位域：slot | clipIndex | texSlot（见 <see cref="AbiLayout"/>）。</summary>
    public uint Route;
    /// <summary>位域：fontAlpha / SDF / grayed / radialFill / borderW …</summary>
    public uint Flags;
    /// <summary>按 kind 复用：glyphIndex / 圆角 pack / 渐变句柄 / 径向填充参数。</summary>
    public uint Aux;
    /// <summary>按 kind 复用：SDF 半径 / 曲线 em-bbox / 渐变端点 / 填充中心。</summary>
    public Vector4 Extra;

    /// <summary>transform 槽下标（0 = identity）。</summary>
    public uint SlotIndex
    {
        readonly get => (Route >> AbiLayout.RouteSlotShift) & AbiLayout.RouteSlotMask;
        set => Route = (Route & ~(AbiLayout.RouteSlotMask << AbiLayout.RouteSlotShift))
                     | ((value & AbiLayout.RouteSlotMask) << AbiLayout.RouteSlotShift);
    }

    /// <summary>ClipEntry 下标（0 = None 哨兵，shader 零采样）。</summary>
    public uint ClipIndex
    {
        readonly get => (Route >> AbiLayout.RouteClipIndexShift) & AbiLayout.RouteClipIndexMask;
        set => Route = (Route & ~(AbiLayout.RouteClipIndexMask << AbiLayout.RouteClipIndexShift))
                     | ((value & AbiLayout.RouteClipIndexMask) << AbiLayout.RouteClipIndexShift);
    }

    /// <summary>段内纹理槽（定义域 = <see cref="Abi.SegmentMaxTextures"/>）。</summary>
    public uint TexSlot
    {
        readonly get => (Route >> AbiLayout.RouteTexSlotShift) & AbiLayout.RouteTexSlotMask;
        set => Route = (Route & ~(AbiLayout.RouteTexSlotMask << AbiLayout.RouteTexSlotShift))
                     | ((value & AbiLayout.RouteTexSlotMask) << AbiLayout.RouteTexSlotShift);
    }

    /// <summary>某个 flags 位是否置起（<paramref name="shift"/> 取自 <see cref="AbiLayout"/>）。</summary>
    public readonly bool HasFlag(int shift) => ((Flags >> shift) & 1u) != 0;

    /// <summary>
    /// 逐字段 IEEE 相等（含 NaN != NaN）。**逐字节对拍不走这里**——
    /// 增量 vs 全量的比较函数是 <c>CanonicalStream</c>，它比的是原始位。
    /// </summary>
    public readonly bool Equals(QuadInstance other) =>
        Rect == other.Rect && UvA == other.UvA && UvB == other.UvB &&
        Color == other.Color && Route == other.Route && Flags == other.Flags &&
        Aux == other.Aux && Extra == other.Extra;

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is QuadInstance q && Equals(q);
    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(Rect, UvA, Color, Route, Flags, Aux);
    /// <inheritdoc/>
    public readonly override string ToString() =>
        $"quad rect={Rect} color=0x{Color:X8} slot={SlotIndex} clip={ClipIndex} tex={TexSlot} flags=0x{Flags:X}";
}

/// <summary>
/// 裁剪条目（**48B**；条目 0 = None 哨兵，shader 对其零采样）。
/// 实例经 <see cref="QuadInstance.ClipIndex"/> 间接引用；条目绑 transform 槽，
/// **槽动才由 CPU 重推 rect**（推送式，不是每帧重算）。
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = Abi.ClipEntrySize)]
public struct ClipEntry : IEquatable<ClipEntry>
{
    /// <summary>槽本地 xMin,yMin,xMax,yMax（outer）。</summary>
    public Vector4 Rect;
    /// <summary>软边梯度。</summary>
    public Vector2 Soft;
    /// <summary>四角半径 —— 圆角矩形 mask 进流，不走 stencil。</summary>
    public Vector4 Radii;
    /// <summary>绑定的 transform 槽下标。</summary>
    public uint Slot;
    /// <summary>对齐填充，写零（append-only：新字段从此处切）。</summary>
    public uint Pad;

    /// <summary>逐字段 IEEE 相等（<see cref="Pad"/> 不参与——它按契约恒为零）。</summary>
    public readonly bool Equals(ClipEntry other) =>
        Rect == other.Rect && Soft == other.Soft && Radii == other.Radii && Slot == other.Slot;

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is ClipEntry c && Equals(c);
    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(Rect, Soft, Radii, Slot);
    /// <inheritdoc/>
    public readonly override string ToString() => $"clip rect={Rect} slot={Slot}";
}

/// <summary>槽位标志（CPU 侧簿记；<see cref="TransformSlotFlags.AxisAligned"/> 下发 shader 供 pixel snap）。</summary>
[Flags]
public enum TransformSlotFlags : ushort
{
    /// <summary>无。</summary>
    None = 0,
    /// <summary>无旋转/斜切（CPU 写槽时判定；shader 据此 pixel snap，防槽动画下文本发糊）。</summary>
    AxisAligned = 1 << 0,
    /// <summary>作者声明的「每帧必变」热点：跳过脏账与升格侦测，直接常驻热路径。</summary>
    Volatile = 1 << 1,
}

/// <summary>
/// transform 槽（架构「平面三」机制 5：动效的物理落点）。
/// 滚动 = 写一个槽矩阵、零重建；gear/tween 由流按写频自动升格入槽。
/// <see cref="Owner"/> 是 Claim 持有者，<see cref="WriteFreq"/> 是升格/降格侦测计数——
/// 两者都是 CPU 簿记，**不进 GPU、不进规范化哈希**（句柄与频次是非语义位）。
/// </summary>
public struct SlotEntry : IEquatable<SlotEntry>
{
    /// <summary>槽矩阵（槽 0 恒为 identity）。</summary>
    public Affine2D M;
    /// <summary>Claim 持有者（CPU 簿记）。</summary>
    public NodeHandle Owner;
    /// <summary>槽位标志。</summary>
    public TransformSlotFlags Flags;
    /// <summary>写频计数（自动升格/降格侦测；诊断量）。</summary>
    public ushort WriteFreq;

    /// <summary>identity 槽（槽 0 的法定内容）。</summary>
    public static SlotEntry Identity => new SlotEntry
    {
        M = Affine2D.Identity,
        Owner = NodeHandle.None,
        Flags = TransformSlotFlags.AxisAligned,
        WriteFreq = 0,
    };

    /// <summary>矩阵是否无旋转/斜切（<see cref="TransformSlotFlags.AxisAligned"/> 置位前的重验，不变量 6）。</summary>
    public readonly bool MatrixIsAxisAligned => M.m01 == 0f && M.m10 == 0f;

    /// <summary>矩阵位等（<see cref="IRenderBackend.WriteSlots"/> 的同值切断判据，不变量 16）。</summary>
    public readonly bool MatrixEquals(in SlotEntry other) =>
        BitEquals.Eq(M.m00, other.M.m00) && BitEquals.Eq(M.m01, other.M.m01) &&
        BitEquals.Eq(M.m10, other.M.m10) && BitEquals.Eq(M.m11, other.M.m11) &&
        BitEquals.Eq(M.tx, other.M.tx) && BitEquals.Eq(M.ty, other.M.ty);

    /// <summary>矩阵与标志都相等（Owner/WriteFreq 不参与——它们不影响像素）。</summary>
    public readonly bool Equals(SlotEntry other) => MatrixEquals(in other) && Flags == other.Flags;

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is SlotEntry s && Equals(s);
    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(M.m00, M.m01, M.m10, M.m11, M.tx, M.ty, Flags);
    /// <inheritdoc/>
    public readonly override string ToString() => $"slot {M} {Flags}";
}

/// <summary>
/// 段描述（**段键 = 纹理集 ≤4 × blendClass**）。
/// 4 纹理槽用 2bit 编码零 ABI 成本；<see cref="Start"/>/<see cref="Count"/> 是本段在实例流中的
/// quad 区间，<see cref="RunIndex"/> 是所属 run。后端原生对象（Mesh/VAO/材质）归后端，
/// **不在本结构里**——那是句柄，属于非语义位。
/// </summary>
public struct SegmentDesc : IEquatable<SegmentDesc>
{
    /// <summary>纹理槽 0。</summary>
    public TexId Tex0;
    /// <summary>纹理槽 1。</summary>
    public TexId Tex1;
    /// <summary>纹理槽 2。</summary>
    public TexId Tex2;
    /// <summary>纹理槽 3。</summary>
    public TexId Tex3;
    /// <summary>实际使用的纹理槽数（≤ <see cref="Abi.SegmentMaxTextures"/>）。</summary>
    public byte TexCount;
    /// <summary>混合类别（进段键，不是栅栏）。</summary>
    public BlendClass Blend;
    /// <summary>本段首个 quad 下标。</summary>
    public int Start;
    /// <summary>本段 quad 数。</summary>
    public int Count;
    /// <summary>所属 run 下标。</summary>
    public int RunIndex;

    /// <summary>取第 <paramref name="slot"/> 个纹理槽（越界返回 <see cref="TexId.None"/>）。</summary>
    public readonly TexId TexAt(int slot) => slot switch
    {
        0 => Tex0,
        1 => Tex1,
        2 => Tex2,
        3 => Tex3,
        _ => TexId.None,
    };

    /// <summary>段键是否相同（纹理集 × blendClass；接缝处 O(1) 合并的判据）。</summary>
    public readonly bool SameKey(in SegmentDesc other) =>
        Tex0.Equals(other.Tex0) && Tex1.Equals(other.Tex1) &&
        Tex2.Equals(other.Tex2) && Tex3.Equals(other.Tex3) &&
        TexCount == other.TexCount && Blend == other.Blend;

    /// <inheritdoc/>
    public readonly bool Equals(SegmentDesc other) =>
        SameKey(in other) && Start == other.Start && Count == other.Count && RunIndex == other.RunIndex;

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is SegmentDesc s && Equals(s);
    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(Tex0, Tex1, TexCount, Blend, Start, Count, RunIndex);
    /// <inheritdoc/>
    public readonly override string ToString() =>
        $"seg [{Start},{Start + Count}) tex×{TexCount} {Blend} run={RunIndex}";
}

/// <summary>
/// run 的排序区间（栅栏括号切出）。**类型上没有 AABB 字段**——
/// 「紧栅栏」在本仓库不可表达（渲染平面不变量 2）：旧 fork 三次让紧 AABB 变陈旧的教训
/// 直接继承为类型防呆，而不是注释里的一句提醒。
/// <see cref="SortingOrder"/> 是派生量（= paintOrder 下标 × <see cref="Abi.PaintOrderStride"/>），
/// 只在 structEpoch 变时整表重推。
/// </summary>
public struct RunOrder : IEquatable<RunOrder>
{
    /// <summary>run 下标。</summary>
    public int RunIndex;
    /// <summary>派生序号（paintOrder 下标 × 16）。</summary>
    public int SortingOrder;

    /// <summary>按下标与序号建。</summary>
    public RunOrder(int runIndex, int sortingOrder)
    {
        RunIndex = runIndex;
        SortingOrder = sortingOrder;
    }

    /// <inheritdoc/>
    public readonly bool Equals(RunOrder other) => RunIndex == other.RunIndex && SortingOrder == other.SortingOrder;
    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is RunOrder r && Equals(r);
    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(RunIndex, SortingOrder);
    /// <inheritdoc/>
    public readonly override string ToString() => $"run#{RunIndex} order={SortingOrder}";
}

/// <summary>孤岛类别（v1 三类，编号沿用架构文档的②③④；append-only）。</summary>
public enum IslandKind : byte
{
    /// <summary>未指定（协议违约）。</summary>
    None = 0,
    /// <summary>②自定义材质（clip 经 shader include 注入；材质拒绝则 scissor 降级 + 警告一次）。</summary>
    CustomMaterial = 2,
    /// <summary>③外部原生对象（Spine/DragonBones 为具名 kind；Unity 用 SortingGroup 对齐 run 序）。</summary>
    ExternalNative = 3,
    /// <summary>④任意形状 stencil mask 域（写入→内容→擦除全在孤岛括号内）。</summary>
    StencilMask = 4,
}

/// <summary>
/// 级联视觉三元（worldVisual 落叶结果）。孤岛按契约**跟随**它：
/// 祖先 SetAlpha/Grayed/隐藏时孤岛一起变（架构机制 9 的三条随流契约之一）。
/// </summary>
public struct IslandVisual : IEquatable<IslandVisual>
{
    /// <summary>级联 α（积）。</summary>
    public float Alpha;
    /// <summary>级联可见（AND）。</summary>
    public bool Visible;
    /// <summary>级联置灰（OR）。</summary>
    public bool Grayed;

    /// <summary>全可见、不灰、α=1。</summary>
    public static IslandVisual Opaque => new IslandVisual { Alpha = 1f, Visible = true, Grayed = false };

    /// <inheritdoc/>
    public readonly bool Equals(IslandVisual other) =>
        BitEquals.Eq(Alpha, other.Alpha) && Visible == other.Visible && Grayed == other.Grayed;
    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is IslandVisual v && Equals(v);
    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(Alpha, Visible, Grayed);
    /// <inheritdoc/>
    public readonly override string ToString() => $"visual α={Alpha} vis={Visible} gray={Grayed}";
}

/// <summary>
/// 孤岛描述（创建期）。孤岛一律**无限盒栅栏**、占一个 run 序；
/// <see cref="RenderEveryN"/> &gt; 1 的 RT 型孤岛滞后 N-1 帧是自愿契约（Slate RetainerBox 同型）。
/// 内容侧接口 <c>IIslandContent</c>（OnAttach / MarkDirty / StillAnimating）归 M1-23，
/// 后端只认这份描述 + 每帧的 <see cref="IslandSync"/>。
/// </summary>
public struct IslandDesc
{
    /// <summary>类别。</summary>
    public IslandKind Kind;
    /// <summary>插在哪个 run 之前（孤岛占一个 run 序）。</summary>
    public int RunBefore;
    /// <summary>绑定的 transform 槽。</summary>
    public int Slot;
    /// <summary>绑定的 ClipEntry 下标（0 = 无裁剪）。</summary>
    public int ClipIndex;
    /// <summary>创建时的级联视觉。</summary>
    public IslandVisual Visual;
    /// <summary>RT 分频（1 = 每帧；&gt;1 = 每 N 帧重画一次）。</summary>
    public int RenderEveryN;
    /// <summary>诊断名（SoA 表没有对象身份，可读名字必须侧带）。</summary>
    public string? DebugName;
}

/// <summary>
/// 孤岛每帧同步（P7 收尾下发：槽矩阵、visual、clip）。
/// <see cref="StillAnimating"/> 是「无失效通道的外部内容自报仍在动」——
/// 零脏帧短路的合法性判据之一（不变量 13），外部内容不自报就没有跳帧资格。
/// </summary>
public struct IslandSync
{
    /// <summary>本帧槽矩阵。</summary>
    public Affine2D SlotMatrix;
    /// <summary>本帧级联视觉。</summary>
    public IslandVisual Visual;
    /// <summary>本帧 ClipEntry 下标。</summary>
    public int ClipIndex;
    /// <summary>内容自报「仍在动」。</summary>
    public bool StillAnimating;
}

/// <summary>
/// 流创建描述。容量是**预算而非上限**：后端可以按需增长，但增长要走 fence 回收
/// （旧缓冲不立即销毁），所以调用方给准初值仍然值钱。
/// </summary>
public struct StreamDesc
{
    /// <summary>quad 容量预算。</summary>
    public int QuadCapacity;
    /// <summary>ClipEntry 容量预算（预算水位见 <see cref="Abi.ClipEntryBudget"/>）。</summary>
    public int ClipCapacity;
    /// <summary>transform 槽容量预算（水位见 <see cref="Abi.TransformSlotBudget"/>）。</summary>
    public int SlotCapacity;
    /// <summary>段容量预算。</summary>
    public int SegmentCapacity;
    /// <summary>诊断名（HUD/dump 里指认这条流）。</summary>
    public string? DebugName;

    /// <summary>按 quad 数给一组够用的默认预算（槽/clip 直接取 ABI 水位）。</summary>
    public static StreamDesc ForQuads(int quads, string? debugName = null) => new StreamDesc
    {
        QuadCapacity = quads,
        ClipCapacity = Abi.ClipEntryBudget,
        SlotCapacity = Abi.TransformSlotBudget,
        SegmentCapacity = quads > 0 ? 4 : 0,
        DebugName = debugName,
    };
}

/// <summary>主表面描述（尺寸 + 清屏色）。</summary>
public struct SurfaceDesc
{
    /// <summary>宽（像素）。</summary>
    public int Width;
    /// <summary>高（像素）。</summary>
    public int Height;
    /// <summary>清屏色（RGBA8 打包）。</summary>
    public uint ClearColor;
}

/// <summary>离屏 pass 的用途（诊断分类；<see cref="OffscreenPassDesc"/> 携带）。</summary>
public enum PassKind : byte
{
    /// <summary>未指定（协议违约）。</summary>
    None = 0,
    /// <summary>滤镜子树捕获（结果在父流中只是一个普通 quad）。</summary>
    FilterCapture = 1,
    /// <summary>fadeGroup 组透明度合成（声明才付费）。</summary>
    FadeGroup = 2,
    /// <summary>孤岛自持 RT。</summary>
    IslandRt = 3,
}

/// <summary>
/// 离屏 pass 描述。<see cref="Consumer"/> 是**依赖序的声明**：
/// 消费本 pass 结果的那个 pass（<see cref="PassHandle.None"/> = 主表面）。
/// 后端据此执法「capture 依赖序内层先」——嵌套滤镜里，内层 pass 必须先于它的消费者提交。
/// </summary>
public struct OffscreenPassDesc
{
    /// <summary>用途分类。</summary>
    public PassKind Kind;
    /// <summary>目标 RT 的资源 id（(句柄, revision) 双键的句柄半边；revision 归 M2-12）。</summary>
    public uint RtId;
    /// <summary>RT 宽。</summary>
    public int Width;
    /// <summary>RT 高。</summary>
    public int Height;
    /// <summary>消费本 pass 结果者（None = 主表面）。</summary>
    public PassHandle Consumer;
    /// <summary>诊断名。</summary>
    public string? DebugName;
}

/// <summary>
/// 降级阶梯的事件类别（架构机制 4「无一级画错」的可计数形态）。
/// 每一级要么正确、要么明确不显示并计数——**任一非零即 <see cref="GateReport"/> 不过**。
/// append-only。
/// </summary>
public enum DegradeKind : byte
{
    /// <summary>未指定（协议违约）。</summary>
    None = 0,
    /// <summary>tier-2 原位重 stamp 不可表达 → 标 Structure。</summary>
    Tier2ToStructure = 1,
    /// <summary>content 发射超 slack → 标 Structure。</summary>
    SlackOverflowToStructure = 2,
    /// <summary>面板整流重编仍不可表达 → 孤岛。</summary>
    RestructureToIsland = 3,
    /// <summary>槽荒（&gt; <see cref="Abi.TransformSlotBudget"/>）→ 容器退 tier-2 重写。</summary>
    SlotStarvation = 4,
    /// <summary>ClipEntry 超限 → 父窗口降级 + 警告。</summary>
    ClipStarvation = 5,
    /// <summary>圆角超限 → stencil 孤岛。</summary>
    RoundedToStencil = 6,
    /// <summary>自定义材质拒绝 clip include → scissor 降级（屏幕轴对齐，旋转裁剪无静默正确解）。</summary>
    ScissorFallback = 7,
    /// <summary>哈希级失配 → 组件级降回运行时 Extract（照常显示 + 计数）。</summary>
    BlobToExtract = 8,
    /// <summary>滤镜域内含孤岛 → 退化为「可见即每帧重捕获」。</summary>
    FilterEveryFrame = 9,
    /// <summary>
    /// 子裁剪域与外层域不在同一帧（子域骑自己的槽）→ 沿用父窗口（M1-13 补）。
    /// 折叠 <c>子域 ∩ 外层</c> 需要两槽的相对变换，而 ClipEntry 的 rect 按 ABI 只表达在
    /// **它自己绑的槽**的本地帧里；跨帧折叠是拿两个坐标系比大小（架构平面三 M1-11 实现期补充②）。
    /// 沿用父窗口 = 裁得更粗但不画错，与「clip 超限 → 父窗口降级」同一条阶梯。
    /// </summary>
    ClipCrossFrameToParent = 10,
    /// <summary>
    /// 非有限尺寸的叶（±∞ 宽高）→ 拒发（M1-14b 审计补）。旧判据 <c>!(w &gt; 0)</c> 只吃
    /// 零/负/NaN，<c>+∞ &gt; 0</c> 为真照发——rect 含 ∞ 的实例把 AABB/域外剔除/相邻性
    /// 排序全部毒化。拒发 + 计数，与槽荒「不画并计数」同一条阶梯。
    /// </summary>
    NonFiniteLeafSize = 11,
    /// <summary>
    /// 九宫格平铺（tileGridIndice/scaleByTile）在 M2 之前无发射表达 → 拒发（M1-14b 审计补，
    /// plan.md M2-14 登记实现）。画成拉伸是「近似」，机制 4 禁止；拒发有声，资产一进来就能数到。
    /// </summary>
    Scale9TileUnimplemented = 12,
}

/// <summary>
/// 一帧的渲染账（架构接缝：诊断平面公开 API）。
/// 由渲染平面在 P8 末填好交给 <see cref="IRenderBackend.EndFrame"/>——
/// 后端不自己发明这些数，它只负责**收下并可查**，于是三后端的同一份账可以直接对拍。
/// </summary>
public struct FrameStats
{
    /// <summary>帧号。</summary>
    public ulong FrameId;
    /// <summary>本帧实例总数。</summary>
    public int QuadCount;
    /// <summary>run 数。</summary>
    public int RunCount;
    /// <summary>段切换次数（= 段数，段键相邻合并后的实际值）。</summary>
    public int SegSwitches;
    /// <summary>本帧上传字节数（Buffer 后端立项的证据字段）。</summary>
    public int UploadBytes;
    /// <summary>滤镜重捕获次数（静止滤镜帧必须为 0，不变量 15）。</summary>
    public int Recaptures;
    /// <summary>孤岛数。</summary>
    public int IslandCount;
    /// <summary>
    /// 本帧是否有任何脏（五通道 ∪ structEpoch 变 ∪ 孤岛自报仍在动）。
    /// 判据归渲染平面；后端据此决定要不要 present（零脏帧短路），并把结论记进收据。
    /// </summary>
    public bool Dirty;
}

/// <summary>
/// 帧收据（架构机制 15「零脏帧短路 + 空转收据」）：
/// <see cref="Ticks"/> vs <see cref="Presents"/> 使「这帧为什么重画」从猜测变审计。
/// 会话结束打印 <see cref="Ticks"/>/<see cref="Presents"/> 比即可判断等值切断是否真的生效。
/// </summary>
public readonly struct FrameReceipt
{
    /// <summary>本帧帧号。</summary>
    public readonly ulong FrameId;
    /// <summary>本帧是否真的提交了画面。</summary>
    public readonly bool Presented;
    /// <summary>累计 tick 数（EndFrame 次数）。</summary>
    public readonly ulong Ticks;
    /// <summary>累计 present 数。</summary>
    public readonly ulong Presents;
    /// <summary>本帧上传字节数（后端自己数的，与 <see cref="FrameStats.UploadBytes"/> 对拍）。</summary>
    public readonly int UploadBytes;

    /// <summary>建收据（后端内部用）。</summary>
    public FrameReceipt(ulong frameId, bool presented, ulong ticks, ulong presents, int uploadBytes)
    {
        FrameId = frameId;
        Presented = presented;
        Ticks = ticks;
        Presents = presents;
        UploadBytes = uploadBytes;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"f{FrameId} presented={Presented} ticks={Ticks} presents={Presents} upload={UploadBytes}B";
}

/// <summary>
/// 后端能力位（启动期一次性查询）。渲染平面据此选路径，**不据此改语义**：
/// 没有 fence 的后端（WebGL2）走环形缓冲，但「缓冲在 GPU 可能仍在读时不得复用」这条契约不变。
/// </summary>
public struct BackendCaps
{
    /// <summary>是否支持离屏 pass（滤镜/fadeGroup 的前提）。</summary>
    public bool SupportsOffscreen;
    /// <summary>是否有真 fence（false ⇒ 明文降级为按帧环形缓冲）。</summary>
    public bool SupportsFence;
    /// <summary>fence/环形队列深度（≤ <see cref="Abi.GpuFenceDepth"/>）。</summary>
    public int FenceQueueDepth;
    /// <summary>是否自绘后端（零脏帧短路 + present 收据只对自绘后端适用；Unity 后端为 false）。</summary>
    public bool SelfDrawn;
    /// <summary>段内纹理槽上限（应等于 <see cref="Abi.SegmentMaxTextures"/>）。</summary>
    public int MaxTextureSlots;
}
