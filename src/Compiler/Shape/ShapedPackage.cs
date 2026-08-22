using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using FairyNext.Compiler.Fui;
using FairyNext.Core;
using FairyNext.Core.Layout;
using FairyNext.Core.Rendering;
using FairyNext.Core.Text;

namespace FairyNext.Compiler;

/// <summary>
/// localId 映射（M1-20a）：编辑器 id（"n5_9sfl"）⇄ 模板局部 id（u16，= 显示列表下标 + 1，
/// 局部 0 = 组件根）⇄ 节点句柄。M1-25 树查看器（localId 路径）与 M1-22 的 PTCH/BIND
/// 回填（nodeLocalId 寻址）都吃这张表；表随模板冻结，运行期只读。
/// </summary>
public sealed class LocalIdMap
{
    private readonly NodeHandle[] _byLocal;
    private readonly string?[] _editorIds;
    private readonly Dictionary<string, ushort> _byEditorId;

    internal LocalIdMap(NodeHandle[] byLocal, string?[] editorIds, Dictionary<string, ushort> byEditorId)
    {
        _byLocal = byLocal;
        _editorIds = editorIds;
        _byEditorId = byEditorId;
    }

    /// <summary>局部 id 数（含局部 0 = 组件根）。</summary>
    public int Count => _byLocal.Length;

    /// <summary>局部 id → 句柄（越界返回 <see cref="NodeHandle.None"/>）。</summary>
    public NodeHandle HandleOf(ushort local) => local < _byLocal.Length ? _byLocal[local] : NodeHandle.None;

    /// <summary>局部 id → 编辑器 id（根/越界/源数据无 id 返回 null）。</summary>
    public string? EditorIdOf(ushort local) => local < _editorIds.Length ? _editorIds[local] : null;

    /// <summary>编辑器 id → 局部 id。</summary>
    public bool TryLocalOf(string editorId, out ushort local) => _byEditorId.TryGetValue(editorId, out local);

    /// <summary>编辑器 id → 句柄。</summary>
    public bool TryHandleOf(string editorId, out NodeHandle handle)
    {
        if (_byEditorId.TryGetValue(editorId, out ushort local))
        {
            handle = _byLocal[local];
            return true;
        }
        handle = NodeHandle.None;
        return false;
    }
}

/// <summary>
/// 一个组件模板的「已定形的树」（M1-20a 前半产物）：.fui 显示列表建成的**无头运行时世界**——
/// 真树 + 已封约束图 + 编译期 P5/度量已收敛的 resolved。M1-20b 对它跑 Extract 并冻结各段；
/// 世界本体（内核/布局引擎/文本系统）原样暴露，正是「编译器 = 无头运行时」（承诺 8）：
/// 冻结用的求值器与运行期逐行相同，不存在第二套布局/排序实现。
/// </summary>
public sealed class ShapedComponent
{
    internal ShapedComponent(FuiItem item, FuiComponent source, UiKernel kernel,
        LayoutEngine layout, TextSystem? text, ContentTable content, LocalIdMap locals,
        ConstraintGraph? constraints, int anchorPinsInjected, int shapeTicks)
    {
        Item = item;
        Source = source;
        Kernel = kernel;
        Layout = layout;
        Text = text;
        Content = content;
        Locals = locals;
        Constraints = constraints;
        AnchorPinsInjected = anchorPinsInjected;
        ShapeTicks = shapeTicks;
    }

    /// <summary>包内条目（id/名字/源尺寸）。</summary>
    public FuiItem Item { get; }

    /// <summary>显示列表数据模型（M1-20b 用它就地重放 gear/transition/扩展块的 span——不重新解析）。</summary>
    public FuiComponent Source { get; }

    /// <summary>无头世界的相位机（树域经 <see cref="UiKernel.Table"/> 取）。</summary>
    public UiKernel Kernel { get; }

    /// <summary>树域（resolved 列即编译产物的几何真值）。</summary>
    public NodeTable Table => Kernel.Table;

    /// <summary>布局引擎（编译期布局写集 = 其槽登记；诊断面见 <see cref="LayoutEngine.Stats"/>）。</summary>
    public LayoutEngine Layout { get; }

    /// <summary>文本系统（无字体注册时为 null，文本节点只有几何）。</summary>
    public TextSystem? Text { get; }

    /// <summary>内容池（叶 spec；M1-20b Extract 的内容源）。</summary>
    public ContentTable Content { get; }

    /// <summary>组件根（= <see cref="NodeTable.Root"/>，尺寸 = 源尺寸）。</summary>
    public NodeHandle Root => Table.Root;

    /// <summary>localId 映射。</summary>
    public LocalIdMap Locals { get; }

    /// <summary>已封约束图（无关系的组件为 null；M1-20b 冻结 CNST 段的来源）。</summary>
    public ConstraintGraph? Constraints { get; }

    /// <summary>pivotAsAnchor 消灭时注入的锚定算子数（诊断/测试）。</summary>
    public int AnchorPinsInjected { get; }

    /// <summary>定形用的帧数（&gt;1 = 有反向依赖走了跨帧续排）。</summary>
    public int ShapeTicks { get; }
}

/// <summary>
/// 一次 <see cref="FgbCompiler.Shape"/> 的全部产物：逐组件的已定形世界 + FGM 诊断。
/// <see cref="Success"/> = 包解析通过且无 Error 级诊断；失败的组件不在 <see cref="Components"/> 里
///（为什么失败看 <see cref="Diagnostics"/>）。
/// </summary>
public sealed class ShapedPackage
{
    private readonly List<ShapedComponent> _components;
    private readonly Dictionary<string, int> _byItemId;

    internal ShapedPackage(FuiPackage? package, List<ShapedComponent> components,
        CompileDiagnostics diagnostics, GlyphMetricsTable? fonts)
    {
        Package = package;
        _components = components;
        Diagnostics = diagnostics;
        Fonts = fonts;
        _byItemId = new Dictionary<string, int>(components.Count);
        for (int i = 0; i < components.Count; i++) _byItemId[components[i].Item.Id] = i;
    }

    /// <summary>包描述符（FGM001 拒收时为 null）。</summary>
    public FuiPackage? Package { get; }

    /// <summary>成功定形的组件（包条目序）。</summary>
    public IReadOnlyList<ShapedComponent> Components => _components;

    /// <summary>FGM 诊断集。</summary>
    public CompileDiagnostics Diagnostics { get; }

    /// <summary>共享度量账（全部组件的编译期度量走同一份 face 注册；无字体为 null）。</summary>
    public GlyphMetricsTable? Fonts { get; }

    /// <summary>包解析通过且无 Error 级诊断。</summary>
    public bool Success => Package != null && !Diagnostics.HasErrors;

    /// <summary>按包内条目 id 找已定形组件。</summary>
    public bool TryGetComponent(string itemId, [MaybeNullWhen(false)] out ShapedComponent component)
    {
        if (_byItemId.TryGetValue(itemId, out int i))
        {
            component = _components[i];
            return true;
        }
        component = null;
        return false;
    }
}

/// <summary>
/// 编译字体注册（机制 13 font-map 的 M1 形态）：.fui 只存字体名，度量需要真 TTF 字节。
/// 首条注册是**回退字体**（.fui 的空字体名 = 编辑器默认字体，落到它；具名未映射 →
/// FGM301 + 回退）。映射语义按名字精确匹配（ordinal）。
/// </summary>
public readonly struct CompileFont
{
    /// <summary>字体名（与 .fui 文本块里的 font 字段比对）。</summary>
    public readonly string Name;

    /// <summary>TTF 字节。</summary>
    public readonly byte[] Data;

    public CompileFont(string name, byte[] data)
    {
        Name = name ?? string.Empty;
        Data = data;
    }
}
