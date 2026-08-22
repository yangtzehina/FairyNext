using System.Collections.Generic;
using FairyNext.Compiler.Fui;
using FairyNext.Compiler.Freeze;
using FairyNext.Compiler.Shape;
using FairyNext.Core.Rendering;
using FairyNext.Core.Text;

namespace FairyNext.Compiler;

/// <summary>
/// .fui → FGB 编译器门面。
/// 宪法约束：编译 = 对着无头运行时跑一遍再冻结产物——不存在第二套布局/排序实现（承诺 8）。
/// .fui 前端（两级块表 ByteBuffer 读取器）只活在本工程；发布版运行时不含 .fui 解析。
///
/// 两半切分（M1-20a / 20b）：
///  - <see cref="Shape"/>（**前半，已可用**）：.fui → 建树（GGroup 真节点化 + pivotAsAnchor
///    编译期消灭 + localId 映射）→ relation → 约束编译（拒环 = FGM101，L0）→ 编译期跑
///    P5 + 度量（复用 TextSystem/LayoutEngine，幂等门与差分神谕全程开启，
///    LateSlotAllocs 恒零升门 FGM902）→ <see cref="ShapedPackage"/>（已定形的树 + 诊断）。
///  - <see cref="Freeze"/>（**后半，已可用**）：吃 <see cref="ShapedPackage"/>——**不重新解析 .fui**——
///    对每个已定形的树跑**运行期同一个** <c>Extract</c>（离线路径自开尾剪枝）→ canonical 去重
///    → 十一段冻结（STRT/TREF/COMP/NODE/CONT/LOCL/CNST/QUAD/SEGS/LEAF/CLIP）→ 内存计划打印。
///  - <see cref="Compile"/>（全量门面）= <see cref="Shape"/> + <see cref="Freeze"/>。
/// </summary>
public static class FgbCompiler
{
    /// <summary>
    /// 前半入口（M1-20a）：.fui 字节 → 逐组件已定形的无头世界 + FGM 诊断。
    /// 永不抛：任何输入的结果都是一个 <see cref="ShapedPackage"/>（失败面 = Error 级诊断 +
    /// <see cref="ShapedPackage.Success"/> false；失败组件不出现在产物里）。
    /// </summary>
    public static ShapedPackage Shape(ReadOnlyMemory<byte> fuiBytes, in CompileOptions options)
    {
        var diag = new CompileDiagnostics();
        var components = new List<ShapedComponent>();

        if (!FuiPackage.TryParse(fuiBytes.ToArray(), out FuiPackage? pkg, out string parseDiag) || pkg == null)
        {
            diag.Add(FgmCodes.PackageRejected, FgmSeverity.Error, "", parseDiag);
            return new ShapedPackage(null, components, diag, null,
                Array.Empty<KeyValuePair<string, TexId>>(), options.ScaleLevel, options.BranchId);
        }

        var ctx = new ShapeContext();

        // ── 字体注册（机制 13 font-map 的 M1 形态；首条 = 回退）────────────────
        IReadOnlyList<CompileFont>? fonts = options.Fonts;
        if (fonts != null && fonts.Count > 0)
        {
            var metrics = new GlyphMetricsTable();
            bool any = false;
            for (int i = 0; i < fonts.Count; i++)
            {
                CompileFont f = fonts[i];
                if (f.Data == null)
                {
                    diag.Add(FgmCodes.FontUnmapped, FgmSeverity.Warning, "",
                        "注册字体 '" + f.Name + "' 拒载：字节为 null");
                    continue;
                }
                if (!TtfFontFace.TryLoad(f.Data, f.Name, out TtfFontFace? face, out string fd) || face == null)
                {
                    diag.Add(FgmCodes.FontUnmapped, FgmSeverity.Warning, "",
                        "注册字体 '" + f.Name + "' 拒载：" + fd);
                    continue;
                }
                ushort id = metrics.RegisterFace(face);
                if (!ctx.FacesByName.ContainsKey(f.Name)) ctx.FacesByName.Add(f.Name, id);
                if (!any) { ctx.FallbackFace = id; any = true; }
            }
            if (any) ctx.Metrics = metrics;
        }

        // ── 图集纹理 id（包内分配序；M1-20b 冻结 TREF/QUAD 时同源）──────────────
        uint nextTex = 1;
        for (int i = 0; i < pkg.Items.Count; i++)
        {
            FuiItem it = pkg.Items[i];
            if (it.Type != FuiItemType.Atlas) continue;
            var tex = new TexId(nextTex++);
            ctx.AtlasTex.Add(it.Id, tex);
            ctx.AtlasOrder.Add(new KeyValuePair<string, TexId>(it.Id, tex));
        }

        // ── 逐组件定形（包条目序；每组件一个独立无头世界）──────────────────────
        ushort treeId = 1;
        for (int i = 0; i < pkg.Items.Count; i++)
        {
            FuiItem it = pkg.Items[i];
            if (it.Type != FuiItemType.Component) continue;
            if (!FuiComponent.TryParse(pkg, it, out FuiComponent? comp, out string compDiag) || comp == null)
            {
                diag.Add(FgmCodes.ComponentRejected, FgmSeverity.Error, it.Name ?? it.Id, compDiag);
                continue;
            }
            ShapedComponent? shaped = TreeBuilder.Build(pkg, it, comp, ctx, treeId++, diag);
            if (shaped != null) components.Add(shaped);
        }

        return new ShapedPackage(pkg, components, diag, ctx.Metrics, ctx.AtlasOrder,
            options.ScaleLevel, options.BranchId);
    }

    /// <summary>
    /// 全量编译门面。任一半失败抛 <see cref="FgmCompileException"/>（诊断随身）。
    /// </summary>
    public static CompileResult Compile(ReadOnlyMemory<byte> fuiBytes, in CompileOptions options)
    {
        ShapedPackage shaped = Shape(fuiBytes, in options);
        if (!shaped.Success) throw new FgmCompileException(shaped.Diagnostics);
        return Freeze(shaped);
    }

    /// <summary>
    /// 后半（M1-20b）：已定形的树 → 编译期 Extract → canonical 去重 → 段冻结 → 内存计划。
    /// **唯一入口，且不重新解析 .fui**——.fui 前端在本半根本不在场，产物只来自
    /// <paramref name="shaped"/> 里那些真无头世界（承诺 8）。
    /// </summary>
    public static CompileResult Freeze(ShapedPackage shaped) => FgbFreezer.Run(shaped);
}

/// <summary>编译失败（Error 级 FGM 诊断存在）。诊断集随身，消息 = 逐行诊断文本。</summary>
public sealed class FgmCompileException : Exception
{
    /// <summary>全部诊断。</summary>
    public CompileDiagnostics Diagnostics { get; }

    public FgmCompileException(CompileDiagnostics diagnostics)
        : base("FGM 编译失败：\n" + diagnostics)
    {
        Diagnostics = diagnostics;
    }
}

/// <summary>编译选项。scaleLevel/branch 进包身份（M1-19 头字段）；fonts 见 <see cref="CompileFont"/>。</summary>
public readonly struct CompileOptions
{
    public readonly int ScaleLevel;
    public readonly int BranchId;

    /// <summary>字体注册表（null/空 = 无字体：文本节点只有几何，逐节点出 FGM301）。</summary>
    public readonly IReadOnlyList<CompileFont>? Fonts;

    public CompileOptions(int scaleLevel, int branchId) : this(scaleLevel, branchId, null) { }

    public CompileOptions(int scaleLevel, int branchId, IReadOnlyList<CompileFont>? fonts)
    {
        ScaleLevel = scaleLevel;
        BranchId = branchId;
        Fonts = fonts;
    }
}

/// <summary>
/// 一次冻结的全部产物。<see cref="Blob"/> 是发布物；其余三项是**验证物**——
/// 两份确定性文本进 code review 的 diff，逐组件的流快照进等价性金样。
/// </summary>
public sealed class CompileResult
{
    /// <summary>FGB blob（十一段，规范段序；同输入逐字节相同）。</summary>
    public ReadOnlyMemory<byte> Blob { get; }

    /// <summary>内存计划（v1.2 机制 11）：段字节 / 实例块字节 / 池预算 / 掩码占用率，人读且可断言。</summary>
    public string MemoryPlan { get; }

    /// <summary>编译产物 golden 文本（v1.2）：ABI 行 + CNST 拓扑序 + 订阅掩码（BIND 随 M2）。</summary>
    public string ReactiveGraph { get; }

    /// <summary>全部 FGM 诊断（Shape 与 Freeze 两半共用一本）。</summary>
    public CompileDiagnostics Diagnostics { get; }

    /// <summary>逐组件的冻结账（含编译期 Extract 的流快照——等价性金样的路径 A）。</summary>
    public IReadOnlyList<FrozenComponent> Components { get; }

    public CompileResult(ReadOnlyMemory<byte> blob, string memoryPlan, string reactiveGraph,
        CompileDiagnostics diagnostics, IReadOnlyList<FrozenComponent> components)
    {
        Blob = blob;
        MemoryPlan = memoryPlan;
        ReactiveGraph = reactiveGraph;
        Diagnostics = diagnostics;
        Components = components;
    }

    /// <summary>按包内条目 id 取冻结账。</summary>
    public bool TryGetComponent(string itemId, out FrozenComponent component)
    {
        for (int i = 0; i < Components.Count; i++)
        {
            if (Components[i].ItemId != itemId) continue;
            component = Components[i];
            return true;
        }
        component = null!;
        return false;
    }
}

/// <summary>
/// 一个组件的冻结账。<see cref="Stream"/> 是**编译期 Extract 的产物快照**——
/// 等价性金样（不变量 18）拿它的 <c>CanonicalStream</c> 字节与运行时管线跑出的流比对，
/// 那同时是降级阶梯「组件级降回运行时 Extract」的正确性凭据：两条路径是同一段代码。
/// </summary>
public sealed class FrozenComponent
{
    public string ItemId { get; }
    public string Name { get; }
    /// <summary>编译期 Extract 的流快照。</summary>
    public StreamSnapshot Stream { get; }
    /// <summary>编译期 Extract 的收据（叶/实例/段/剪枝计数）。</summary>
    public ExtractReport Extract { get; }
    public int NodeStart { get; }
    public int NodeCount { get; }
    public int QuadStart { get; }
    public int QuadCount { get; }
    public int LeafStart { get; }
    public int LeafCount { get; }
    public int OpStart { get; }
    public int OpCount { get; }
    /// <summary>实例块字节（内存计划是承诺不是估计）。</summary>
    public uint InstanceBytes { get; }

    internal FrozenComponent(string itemId, string name, StreamSnapshot stream, ExtractReport extract,
        int nodeStart, int nodeCount, int quadStart, int quadCount, int leafStart, int leafCount,
        int opStart, int opCount, uint instanceBytes)
    {
        ItemId = itemId; Name = name; Stream = stream; Extract = extract;
        NodeStart = nodeStart; NodeCount = nodeCount;
        QuadStart = quadStart; QuadCount = quadCount;
        LeafStart = leafStart; LeafCount = leafCount;
        OpStart = opStart; OpCount = opCount;
        InstanceBytes = instanceBytes;
    }
}
