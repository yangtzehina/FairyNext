using FairyNext.Contracts;
using FairyNext.Numerics;

namespace FairyNext.Core.Rendering;

// ============================================================================
// 孤岛协议的**内容侧**（架构「平面三」机制 9；M1-23）。
//
// M1-11 交付的是流侧的账（IslandRecord：谁、插在哪、骑哪个槽），M1-13 让 Extract 会切 run，
// M1-14 只做到 AttachIslands。本文件补上那半边——**谁来画那块不可表达的内容**：
//   ①（保留）3D 透视：ABI 预留，按真实工程语料裁决后再定，不在 v1 清单里；
//   ②自定义材质：clip 经标准 shader include 注入；材质拒绝 include ⇒ scissor 降级 + 有声；
//   ③外部原生对象：Unity 用 SortingGroup 对齐 run 序，非 Unity 拿 [zMin,zMax) 深度区间与槽矩阵；
//   ④任意形状 stencil mask 域：写入 → 内容 → 擦除全在**孤岛括号**内，成对且可嵌套。
//
// ── 三条随流契约在本文件的落点 ─────────────────────────────────────────────
//  1. **visual 并入下行**：<see cref="IslandContext.Visual"/> 的唯一产生式是 worldVisual 的落叶
//     结果（<c>Extract.VisualOf</c>），与叶用的是同一个函数。祖先 SetAlpha/Grayed 时孤岛跟随，
//     祖先隐藏时孤岛**整只离开流**（不是画成透明）——判据走 Extract 的 authored 父链传播，
//     与叶字面同源。**不读 worldVisual 判进出流**：隐藏子树后代的 worldVisual 合法陈旧
//     （「隐藏子树免下钻」），逐点判它会把被隐藏祖先罩住的孤岛照画出来（14b CRITICAL 同型）。
//  2. **renderEveryN 分频**：<see cref="IslandContext.RenderThisFrame"/> 每帧下发，
//     滞后 N-1 帧是**自愿契约**——内容自己声明的分频，不是我们替它省的帧。
//  3. **仍在动自报**：<see cref="IIslandContent.StillAnimating"/> 是零脏帧短路的第三个前提
//     （不变量 13）。没有失效通道的外部内容不自报就没有跳帧资格——自报为真的那一帧
//     <c>FrameStats.Dirty</c> 为真、present 照涨。
//
// ── 两个方向的 MarkDirty（**不是同一件事**，故住在两个类型上）───────────────
//  · <see cref="IIslandContent.MarkDirty"/>：**运行时 → 内容**。「你的挂载参数变了
//    （槽矩阵/clip/visual/深度区间），自己重画」。RT 型内容据此重捕获。
//  · <see cref="IslandMount.MarkDirty"/>：**内容 → 运行时**。外部内容（Spine 换动作、
//    Animator 驱动）没有失效通道，只能显式说「我脏了」；它落成节点的 <see cref="Ch.Content"/>
//    上行标记，由 P7 的 <c>StreamDrain</c> 消化成一次孤岛同步——**不是**一次面板整编
//    （把每一次 Spine 帧推进判成结构变化，等于把最常见的外部动效变成最贵的路径）。
//  相位纪律：内容只能在 P0–P5 调 <see cref="IslandMount.MarkDirty"/>；P7 里调 = 相位违例
//  （失效协议自己会计一次），因为那时排水水位已冻结。
// ============================================================================

/// <summary>
/// ③外部原生对象的**具名种类**（架构机制 9：「Spine/DragonBones 为具名 kind」）。
///
/// 为什么不进 <see cref="IslandKind"/>：那个枚举是「②③④」这张封闭清单本身，编号即架构文档的
/// 条目号；把 Spine 塞进去等于把「一类不可表达内容」与「这类内容的一个具体实现」混成一层。
/// 具名种类是 ③ 内部的分派维度——后端据它决定建什么原生对象（Unity 侧建 SortingGroup 挂点），
/// 核心侧**不引任何 Spine/DragonBones 依赖**：这里只有一个 byte。append-only。
/// </summary>
public enum IslandNativeKind : byte
{
    /// <summary>未具名（通用外部渲染器；后端按最保守路径建挂点）。</summary>
    None = 0,
    /// <summary>Spine 骨骼动画（fork GoWrapper 最常见的宿主）。</summary>
    Spine = 1,
    /// <summary>DragonBones 骨骼动画。</summary>
    DragonBones = 2,
    /// <summary>宿主自定义渲染器（宿主自己认名字）。</summary>
    Custom = 3,
}

/// <summary>
/// 孤岛的裁剪下发方式（架构机制 9 的②号条目）。
///
/// **scissor 是降级不是等价物**：它是屏幕轴对齐的矩形，旋转/斜切裁剪下没有静默正确解，
/// 所以走这条路必须有声（<see cref="DegradeKind.ScissorFallback"/>，机制 4「无一级画错」）。
/// </summary>
public enum IslandClipMode : byte
{
    /// <summary>不裁剪（孤岛不在任何裁剪域内）。</summary>
    None = 0,
    /// <summary>clip 经标准 shader include 注入，材质自己采样裁剪域（正路）。</summary>
    ShaderInclude = 1,
    /// <summary>材质拒绝 include ⇒ 退到屏幕轴对齐 scissor（降级，有声）。</summary>
    Scissor = 2,
}

/// <summary>
/// ④stencil mask 域的括号位（写入 → 内容 → 擦除）。
///
/// 一个 stencil 孤岛节点在流里产生**两条**孤岛记录：子树之前一条 <see cref="Enter"/>
/// （写模板、开测试），子树之后一条 <see cref="Exit"/>（擦除、关测试）。
/// 括号成对由 Extract 按 paintOrder 的子树区间闭合，嵌套按 LIFO——
/// 「进了没出」在 fork 的 stencil 路径上是整屏被裁掉，症状与「内容没画」无法区分。
/// </summary>
public enum IslandBracket : byte
{
    /// <summary>不是括号（②③ 孤岛都是单点）。</summary>
    None = 0,
    /// <summary>括号开：写模板并开启模板测试。</summary>
    Enter = 1,
    /// <summary>括号闭：擦除模板并关闭模板测试。</summary>
    Exit = 2,
}

/// <summary>
/// 挂载上下文（架构接缝原文：<c>OnAttach(ctx: 槽/clip 矩形/深度区间)</c>）。
///
/// 它是**本帧的挂载事实**，不是一份可缓存的状态：槽矩阵会随滚动变、visual 会随祖先淡入变、
/// clip 会随重编换条目。内容要么每帧从 <see cref="IIslandContent.OnSync"/> 收一份新的，
/// 要么在 <see cref="IIslandContent.MarkDirty"/> 之后重取——不存在「挂上就不变」的字段。
/// </summary>
public readonly struct IslandContext
{
    /// <summary>孤岛节点。</summary>
    public readonly NodeHandle Node;
    /// <summary>类别（②③④）。</summary>
    public readonly IslandKind Kind;
    /// <summary>③的具名种类（非 ③ 恒 <see cref="IslandNativeKind.None"/>）。</summary>
    public readonly IslandNativeKind NativeKind;
    /// <summary>④的括号位（②③ 恒 <see cref="IslandBracket.None"/>）。</summary>
    public readonly IslandBracket Bracket;
    /// <summary>④的嵌套深度（1 起；即 stencil ref 值）。</summary>
    public readonly int StencilDepth;
    /// <summary>裁剪下发方式。</summary>
    public readonly IslandClipMode ClipMode;
    /// <summary>骑的 transform 槽（0 = identity）。</summary>
    public readonly int Slot;
    /// <summary>本帧槽矩阵。</summary>
    public readonly Affine2D SlotMatrix;
    /// <summary>裁剪域条目下标（0 = 不裁剪）。</summary>
    public readonly int ClipEntry;
    /// <summary>裁剪矩形（**在 <see cref="Slot"/> 的槽帧里**；<see cref="ClipMode"/> 为 None 时全零）。</summary>
    public readonly Vector4 ClipRect;
    /// <summary>级联视觉三元（α 积 / visible AND / grayed OR）。</summary>
    public readonly IslandVisual Visual;
    /// <summary>孤岛节点的 resolved 宽。</summary>
    public readonly float Width;
    /// <summary>孤岛节点的 resolved 高。</summary>
    public readonly float Height;
    /// <summary>本孤岛独占的绘制序号（= 自有 run 序 × <see cref="Abi.PaintOrderStride"/>）。</summary>
    public readonly int SortingOrder;
    /// <summary>深度区间下界（非 Unity 后端按 [ZMin, ZMax) 分配自己的深度）。</summary>
    public readonly float ZMin;
    /// <summary>深度区间上界（开区间）。</summary>
    public readonly float ZMax;
    /// <summary>本帧是否轮到重画（<c>renderEveryN</c> 分频；滞后 N-1 帧是自愿契约）。</summary>
    public readonly bool RenderThisFrame;
    /// <summary>本帧挂载参数是否变过（变了 ⇒ 运行时已调过 <see cref="IIslandContent.MarkDirty"/>）。</summary>
    public readonly bool MountChanged;
    /// <summary>内容 → 运行时的回调句柄（外部内容自报脏走它）。</summary>
    public readonly IslandMount Mount;

    /// <summary>建一份上下文（渲染平面内部用；内容侧只读）。</summary>
    internal IslandContext(NodeHandle node, in IslandRecord record, in Affine2D slotMatrix,
        in Vector4 clipRect, float width, float height, bool renderThisFrame, bool mountChanged,
        IslandMount mount)
    {
        Node = node;
        Kind = record.Kind;
        NativeKind = record.NativeKind;
        Bracket = record.Bracket;
        StencilDepth = record.StencilDepth;
        ClipMode = record.ClipMode;
        Slot = record.Slot;
        SlotMatrix = slotMatrix;
        ClipEntry = record.ClipEntry;
        ClipRect = clipRect;
        Visual = record.Visual;
        Width = width;
        Height = height;
        SortingOrder = record.PaintOrderIndex * Abi.PaintOrderStride;
        ZMin = record.PaintOrderIndex * (float)Abi.PaintOrderStride;
        ZMax = (record.PaintOrderIndex + 1) * (float)Abi.PaintOrderStride;
        RenderThisFrame = renderThisFrame;
        MountChanged = mountChanged;
        Mount = mount;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"island {Kind}{(NativeKind == IslandNativeKind.None ? "" : "/" + NativeKind)}"
        + $"{(Bracket == IslandBracket.None ? "" : " " + Bracket + "@" + StencilDepth)}"
        + $" slot={Slot} clip={ClipEntry}/{ClipMode} order={SortingOrder} z=[{ZMin},{ZMax})"
        + $" {Visual} render={RenderThisFrame}";
}

/// <summary>
/// 孤岛内容（架构接缝原文：<c>IIslandContent { OnAttach(ctx), MarkDirty(), StillAnimating }</c>；
/// M1-23 另补 <see cref="OnSync"/> 与 <see cref="OnDetach"/>——前者是「每帧下发」这条契约的
/// 调用点，后者是句柄纪律：孤岛随整流重编在后端拆除，内容必须知道自己已经不在流里了）。
///
/// 实现者是宿主代码（Spine 组件、自定义材质的 quad、stencil 形状画笔）。核心不认识它们中的任何一个，
/// 只认这五个方法——**孤岛是安全网不是垃圾桶**：清单封闭、每类闭环，接口面因此可以很小。
/// </summary>
public interface IIslandContent
{
    /// <summary>
    /// 材质是否接受 clip 的 shader include（②号条目的唯一分岔）。
    /// 返回 false ⇒ 运行时退 scissor 并计一次 <see cref="DegradeKind.ScissorFallback"/>。
    /// ③④ 不问这一项（外部渲染器与 stencil 域各有自己的裁剪路径）。
    /// </summary>
    bool AcceptsClipInclude { get; }

    /// <summary>
    /// 内容是否**仍在动**（零脏帧短路的第三个前提，不变量 13）。
    /// 每帧在 P7 收尾被问一次；为真的那一帧不许跳 present。
    /// 没有失效通道的外部内容必须诚实自报——报假静止的代价是画面停在上一帧且没有任何报错。
    /// </summary>
    bool StillAnimating { get; }

    /// <summary>挂进流（首帧，或整流重编之后重新挂上）。</summary>
    void OnAttach(in IslandContext ctx);

    /// <summary>每帧下发挂载参数（P7 收尾；槽矩阵/visual/clip/深度区间/分频位）。</summary>
    void OnSync(in IslandContext ctx);

    /// <summary>
    /// **运行时 → 内容**：挂载参数变了，自己重画（RT 型内容据此重捕获）。
    /// 调用点在 <see cref="OnSync"/> 之前；同帧只调一次。
    /// </summary>
    void MarkDirty();

    /// <summary>离开流（整流重编拆句柄、祖先隐藏、节点销毁）。</summary>
    void OnDetach();
}

/// <summary>
/// **内容 → 运行时**的回调句柄（<see cref="IIslandContent.OnAttach"/> 经
/// <see cref="IslandContext.Mount"/> 交给内容）。
///
/// 它存在的唯一理由：外部内容没有失效通道。Spine 换了动作、Animator 推进了一帧——
/// 树里一个字节都没变，五通道全空，零脏帧短路会把这帧跳掉。内容调一次
/// <see cref="MarkDirty"/> 就把这件事变成节点的 <see cref="Ch.Content"/> 上行标记，
/// 走的是与「改了一张图的 frame」完全相同的那条通道。
/// </summary>
public sealed class IslandMount
{
    private readonly IslandTable _owner;

    internal IslandMount(IslandTable owner, NodeHandle node)
    {
        _owner = owner;
        Node = node;
    }

    /// <summary>孤岛节点。</summary>
    public NodeHandle Node { get; }

    /// <summary>本挂点累计自报脏次数（诊断：外部内容的真实脏频次）。</summary>
    public long DirtyMarks { get; private set; }

    /// <summary>
    /// 自报脏（**内容 → 运行时**）。落成节点的 <see cref="Ch.Content"/> 上行标记：
    /// P7 的 <c>StreamDrain</c> 把它消化成一次孤岛同步，**不升级 Structure**。
    /// 相位纪律：只能在 P0–P5 调；P7 里调会被失效协议记一次相位违例。
    /// </summary>
    public void MarkDirty()
    {
        DirtyMarks++;
        _owner.OnMountDirty(Node);
    }

    /// <inheritdoc/>
    public override string ToString() => $"mount {Node} dirty={DirtyMarks}";
}
