using System.Collections.Generic;
using FairyNext.Compiler.Fui;
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
///  - <see cref="Compile"/>（全量门面）：前半真跑；后半（Extract → canonical 去重 → 段冻结 →
///    内存计划打印）随 M1-20b 进驻——**吃的是 ShapedPackage，不重新解析**。
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
            return new ShapedPackage(null, components, diag, null);
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
            if (it.Type == FuiItemType.Atlas) ctx.AtlasTex.Add(it.Id, new TexId(nextTex++));
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

        return new ShapedPackage(pkg, components, diag, ctx.Metrics);
    }

    /// <summary>
    /// 全量编译门面。前半（<see cref="Shape"/>）真跑；前半失败抛 <see cref="FgmCompileException"/>
    /// （诊断随身）；冻结后半随 M1-20b 进驻。
    /// </summary>
    public static CompileResult Compile(ReadOnlyMemory<byte> fuiBytes, in CompileOptions options)
    {
        ShapedPackage shaped = Shape(fuiBytes, in options);
        if (!shaped.Success) throw new FgmCompileException(shaped.Diagnostics);
        return Freeze(shaped);
    }

    /// <summary>后半（M1-20b 进驻点）：已定形的树 → Extract → canonical 去重 → 段冻结 → 内存计划。</summary>
    public static CompileResult Freeze(ShapedPackage shaped)
        => throw new NotImplementedException(
            "M1-20b：Extract → canonical 去重 → FgbWriter 段冻结 → 内存计划打印（前半产物见 ShapedPackage，勿重新解析）");
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

public sealed class CompileResult
{
    public ReadOnlyMemory<byte> Blob { get; }
    public string MemoryPlan { get; }     // 人读账单，同时是测试断言锚（v1.2）
    public string ReactiveGraph { get; }  // 绑定表/拓扑序/掩码的规范文本 golden（v1.2）

    public CompileResult(ReadOnlyMemory<byte> blob, string memoryPlan, string reactiveGraph)
    {
        Blob = blob; MemoryPlan = memoryPlan; ReactiveGraph = reactiveGraph;
    }
}
