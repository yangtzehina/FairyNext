using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using FairyNext.PropGen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// 属性 setter 生成器的行为门（M1-09）。形态承自 fork 的 tools/FairyGUI.Mvvm.Generator.Tests：
/// 用 <see cref="CSharpGeneratorDriver"/> 跑**真实生成器**打在 stub 类型上，一条 Check 对一条契约，
/// 末行 <c>RESULT pass=N fail=N</c>，fail&gt;0 退出码非零。
///
/// 这套门守的是「生成期执法」本身——归属缺失必须是**编译错**、生成的 setter 必须真的含等值切断与 Mark、
/// 同输入必须逐字节可复现。运行期的等价性由主 runner（tests/FairyNext.Tests）负责，两套互不替代。
///
///   dotnet run --project tools/PropGen.Tests
/// </summary>
internal static class Program
{
    // ── stub：只需名字对得上（生成器按符号名耦合，零程序集引用）────────────────────────
    private const string Prelude = @"
namespace FairyNext.Numerics
{
    public static class BitEquals { public static bool Eq(float a, float b) => a.Equals(b); }
}
namespace FairyNext.Core
{
    [System.Flags]
    public enum Ch : ushort
    {
        None = 0, Content = 1 << 0, Transform = 1 << 1, Color = 1 << 2, Visible = 1 << 3,
        Structure = 1 << 4, Layout = 1 << 5, Text = 1 << 6, BoundsD = 1 << 7,
        DownColor = 1 << 8, DownVisible = 1 << 9, DownLayer = 1 << 10,
    }
    public static class ChMask
    {
        public const Ch Up = Ch.Content | Ch.Transform | Ch.Color | Ch.Visible | Ch.Structure | Ch.Layout | Ch.Text;
        public const Ch Down = Ch.DownColor | Ch.DownVisible | Ch.DownLayer;
    }
    public enum WriteSource : byte { User = 0, Binding = 1, Anim = 2, Layout = 3 }
    public static class UiAssert { public static void That(bool condition, string message) { } }
    public static class Visual
    {
        public const uint AlphaMask = 0xFFu;
        public const uint Visible = 1u << 8;
        public const uint Grayed = 1u << 9;
        public const uint PixelSnap = 1u << 11;
        public static byte Alpha(uint v) => (byte)(v & AlphaMask);
        public static uint WithAlpha(uint v, byte a) => (v & ~AlphaMask) | a;
    }
    public enum NodePropStore : byte { FloatColumn = 0, AlphaU8 = 1, VisualBit = 2, Unbacked = 3 }
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    internal sealed class NodePropAttribute : System.Attribute
    {
        public NodePropAttribute(string name, Ch channel, NodePropStore store) { Name = name; Channel = channel; Store = store; }
        public string Name { get; }
        public Ch Channel { get; }
        public NodePropStore Store { get; }
        public Ch Down { get; set; }
        public string Column { get; set; } = """";
        public string Bit { get; set; } = """";
        public string Pending { get; set; } = """";
    }
    public readonly struct NodePropInfo
    {
        public readonly byte Id; public readonly string Name; public readonly Ch Channel;
        public readonly Ch Marks; public readonly NodePropStore Store;
        public NodePropInfo(byte id, string name, Ch channel, Ch marks, NodePropStore store)
        { Id = id; Name = name; Channel = channel; Marks = marks; Store = store; }
    }
    public sealed partial class NodeTable
    {
        private float[] _width = new float[8];
        private float[] _posX = new float[8];
        private uint[] _localVisual = new uint[8];
        private uint[] _contentRef = new uint[8];
        private static byte ToU8(float a) => (byte)(a <= 0f ? 0 : a >= 1f ? 255 : (int)(a * 255f + 0.5f));
        private void Mark(uint index, Ch channel, WriteSource source) { _ = index; _ = channel; _ = source; }
    }
}";

    /// <summary>ABI 单源 stub 的 id 清单（默认四个：Width/Alpha/Visible/X；个别用例追加）。</summary>
    private const string DefaultAbi = "PropIdWidth = 1, PropIdAlpha = 64, PropIdVisible = 65, PropIdX = 128";

    /// <summary>完整且合法的归属表（各用例在其上做定点破坏）。与运行时同住 FairyNext.Core。</summary>
    private const string FullTable = @"
namespace FairyNext.Core
{
    [NodeProp(""Width"", Ch.Layout, NodePropStore.FloatColumn, Column = ""_width"")]
    [NodeProp(""Alpha"", Ch.Color, NodePropStore.AlphaU8, Column = ""_localVisual"", Down = Ch.DownColor)]
    [NodeProp(""Visible"", Ch.Visible, NodePropStore.VisualBit, Column = ""_localVisual"", Bit = ""Visible"", Down = Ch.DownVisible)]
    [NodeProp(""X"", Ch.Transform, NodePropStore.FloatColumn, Column = ""_posX"")]
    public static partial class NodeProps { }
}";

    private static int _pass, _fail;
    private static readonly StringBuilder Log = new StringBuilder();

    private static void Check(string name, bool ok, string detail = null)
    {
        if (ok) _pass++; else _fail++;
        Log.Append(ok ? "PASS " : "FAIL ").Append(name);
        if (!ok && detail != null) Log.Append("  <- ").Append(Trim(detail));
        Log.AppendLine();
    }

    private static string Trim(string s) => s.Length <= 600 ? s : s.Substring(0, 600) + "…";

    private static CSharpCompilation Compile(string abiProps, params string[] sources)
    {
        string dir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(dir, "System.Runtime.dll")),
        };
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(Prelude, path: "Prelude.cs"),
            CSharpSyntaxTree.ParseText("namespace FairyNext.Contracts { public static class AbiLayout { public const byte PropIdNone = 0; public const byte "
                + abiProps + "; } }", path: "Abi.cs"),
        };
        for (int i = 0; i < sources.Length; i++)
            trees.Add(CSharpSyntaxTree.ParseText(sources[i], path: $"User{i}.cs"));
        return CSharpCompilation.Create("PropGenGate", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private sealed class RunResult
    {
        internal string Writers = "";
        internal string Table = "";
        internal ImmutableArray<Diagnostic> Diags;
        internal Compilation Updated;
        internal int FileCount;

        internal bool Has(string id) => Diags.Any(d => d.Id == id);
        internal string Ids => string.Join("; ", Diags.Select(d => d.Id + ":" + d.GetMessage()));
        internal string Errors => string.Join("; ",
            Updated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
    }

    private static RunResult Run(CSharpCompilation comp)
    {
        var driver = CSharpGeneratorDriver.Create(new NodePropGenerator().AsSourceGenerator());
        driver.RunGeneratorsAndUpdateCompilation(comp, out Compilation updated, out ImmutableArray<Diagnostic> diags);

        var result = new RunResult { Diags = diags, Updated = updated };
        foreach (SyntaxTree t in updated.SyntaxTrees.Skip(comp.SyntaxTrees.Length))
        {
            result.FileCount++;
            if (t.FilePath.EndsWith("NodeTable.Props.g.cs", StringComparison.Ordinal)) result.Writers = t.ToString();
            else if (t.FilePath.EndsWith("NodeProps.g.cs", StringComparison.Ordinal)) result.Table = t.ToString();
        }
        return result;
    }

    private static void Main()
    {
        // ── g1：完整归属表 = 零诊断、两份生成物、生成代码本身编译得过 ─────────────────
        {
            RunResult r = Run(Compile(DefaultAbi, FullTable));
            Check("g1.完整归属表零诊断", r.Diags.IsEmpty, r.Ids);
            Check("g1.发射两份生成物（写口 + 元表）", r.FileCount == 2 && r.Writers.Length > 0 && r.Table.Length > 0);
            Check("g1.生成代码编译无错误",
                !r.Updated.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), r.Errors);

            // 等值切断 → 写列 → Mark：三段缺一不可，逐段钉住（不变量 3 的生成期形态）
            Check("g1.float 写口含 BitEquals 等值切断",
                r.Writers.Contains("if (global::FairyNext.Numerics.BitEquals.Eq(_posX[index], value)) return false;"),
                r.Writers);
            Check("g1.float 写口写的是声明的列", r.Writers.Contains("_posX[index] = value;"), r.Writers);
            Check("g1.写口 Mark 的是归属通道",
                r.Writers.Contains("internal const Ch MarksX = Ch.Transform;")
                && r.Writers.Contains("Mark(index, MarksX, src);"), r.Writers);
            Check("g1.α 的等值切断在 u8 存储值上（不在入参 float 上）",
                r.Writers.Contains("byte a = ToU8(value);") && r.Writers.Contains("if (Visual.Alpha(v) == a) return false;"),
                r.Writers);
            Check("g1.位属性比较位值而非整字",
                r.Writers.Contains("if (((v & Visual.Visible) != 0) == on) return false;"), r.Writers);
            Check("g1.下行伴随位并进 Mark 位集",
                r.Writers.Contains("internal const Ch MarksAlpha = Ch.Color | Ch.DownColor;")
                && r.Writers.Contains("internal const Ch MarksVisible = Ch.Visible | Ch.DownVisible;"), r.Writers);
            Check("g1.元表按 id 升序且带归属/Mark 两列",
                r.Table.Contains("new NodePropInfo(1, \"Width\", Ch.Layout, Ch.Layout, NodePropStore.FloatColumn),")
                && r.Table.Contains("new NodePropInfo(64, \"Alpha\", Ch.Color, Ch.Color | Ch.DownColor, NodePropStore.AlphaU8),"),
                r.Table);
        }

        // ── g2：WriteSource 分流 —— 写口签名带来源并原样传给 Mark，位属性走 value != 0f ──
        {
            RunResult r = Run(Compile(DefaultAbi, FullTable));
            Check("g2.写口签名带 WriteSource",
                r.Writers.Contains("internal bool WriteX(uint index, float value, WriteSource src)")
                && r.Writers.Contains("internal bool WriteVisible(uint index, bool on, WriteSource src)"), r.Writers);
            Check("g2.来源原样传给 Mark（不自造第二套 reason 映射）",
                r.Writers.Contains("Mark(index, MarksAlpha, src);")
                && !r.Writers.Contains("WriteSource.User")
                && !r.Writers.Contains("InvalidateReason"), r.Writers);
            Check("g2.分派把 float 入参折成位值",
                r.Writers.Contains("case 65: return WriteVisible(index, value != 0f, src);"), r.Writers);
            Check("g2.未归属 id 落 default 断言（分派封闭）",
                r.Writers.Contains("default:")
                && r.Writers.Contains("WriteAuthored 收到未归属的 PropId="), r.Writers);
        }

        // ── g3：ABI 有 id、归属表没声明 → FNP001 编译错（通道归属封闭的执法点）────────
        {
            RunResult r = Run(Compile(DefaultAbi + ", PropIdHeight = 2", FullTable));
            Check("g3.缺通道归属报 FNP001", r.Has("FNP001"), r.Ids);
            Check("g3.FNP001 是 Error 而不是 Warning",
                r.Diags.Any(d => d.Id == "FNP001" && d.Severity == DiagnosticSeverity.Error), r.Ids);
            Check("g3.缺归属的属性不会被静默生成写口", !r.Writers.Contains("WriteHeight"), r.Writers);
        }

        // ── g4：归属表声明了 ABI 里没有的属性 → FNP002 ─────────────────────────────
        {
            RunResult r = Run(Compile(DefaultAbi, FullTable.Replace("    public static partial class NodeProps { }",
                "    [NodeProp(\"Ghost\", Ch.Layout, NodePropStore.FloatColumn, Column = \"_width\")]\n    public static partial class NodeProps { }")));
            Check("g4.孤儿归属声明报 FNP002", r.Has("FNP002"), r.Ids);
            Check("g4.孤儿不进生成物", !r.Writers.Contains("WriteGhost"), r.Writers);
        }

        // ── g5：同一属性声明两次 → FNP003 ──────────────────────────────────────────
        {
            RunResult r = Run(Compile(DefaultAbi, FullTable.Replace("    public static partial class NodeProps { }",
                "    [NodeProp(\"X\", Ch.Layout, NodePropStore.FloatColumn, Column = \"_posX\")]\n    public static partial class NodeProps { }")));
            Check("g5.重复归属声明报 FNP003", r.Has("FNP003"), r.Ids);
        }

        // ── g6：归属必须是单个上行通道位（多位 / 下行位 / 派生位一律拒）──────────────
        {
            RunResult multi = Run(Compile(DefaultAbi,
                FullTable.Replace("[NodeProp(\"X\", Ch.Transform,", "[NodeProp(\"X\", Ch.Transform | Ch.Color,")));
            Check("g6.多位归属报 FNP004", multi.Has("FNP004"), multi.Ids);

            RunResult down = Run(Compile(DefaultAbi,
                FullTable.Replace("[NodeProp(\"X\", Ch.Transform,", "[NodeProp(\"X\", Ch.DownColor,")));
            Check("g6.拿下行位当归属报 FNP004", down.Has("FNP004"), down.Ids);

            RunResult derived = Run(Compile(DefaultAbi,
                FullTable.Replace("[NodeProp(\"X\", Ch.Transform,", "[NodeProp(\"X\", Ch.BoundsD,")));
            Check("g6.拿派生位当归属报 FNP004", derived.Has("FNP004"), derived.Ids);
        }

        // ── g7：Down 只收下行位 → FNP005 ───────────────────────────────────────────
        {
            RunResult r = Run(Compile(DefaultAbi,
                FullTable.Replace("Down = Ch.DownColor", "Down = Ch.Structure")));
            Check("g7.Down 收到上行位报 FNP005", r.Has("FNP005"), r.Ids);
        }

        // ── g8：存储声明与真实列对账 → FNP006 ──────────────────────────────────────
        {
            RunResult missing = Run(Compile(DefaultAbi, FullTable.Replace("Column = \"_posX\"", "Column = \"_nope\"")));
            Check("g8.列字段不存在报 FNP006", missing.Has("FNP006"), missing.Ids);

            RunResult wrongType = Run(Compile(DefaultAbi, FullTable.Replace("Column = \"_posX\"", "Column = \"_contentRef\"")));
            Check("g8.列类型不符（uint[] 当 float 列）报 FNP006", wrongType.Has("FNP006"), wrongType.Ids);

            RunResult noBit = Run(Compile(DefaultAbi, FullTable.Replace("Bit = \"Visible\", ", "")));
            Check("g8.VisualBit 缺 Bit 报 FNP006", noBit.Has("FNP006"), noBit.Ids);

            RunResult badBit = Run(Compile(DefaultAbi, FullTable.Replace("Bit = \"Visible\"", "Bit = \"Nope\"")));
            Check("g8.Visual 上没有该位常量报 FNP006", badBit.Has("FNP006"), badBit.Ids);
        }

        // ── g9：未接列属性 —— 必须显式登记 Pending，且生成「断言分支」而不是静默 ─────
        {
            string unbackedOk = FullTable.Replace("[NodeProp(\"X\", Ch.Transform, NodePropStore.FloatColumn, Column = \"_posX\")]",
                "[NodeProp(\"X\", Ch.Transform, NodePropStore.Unbacked, Pending = \"列随 M9-99 接入\")]");
            RunResult ok = Run(Compile(DefaultAbi, unbackedOk));
            Check("g9.未接列属性仍占分派 case 并断言（不静默）",
                ok.Diags.IsEmpty && ok.Writers.Contains("case 128:")
                && ok.Writers.Contains("列尚未接入：列随 M9-99 接入") && !ok.Writers.Contains("WriteX"),
                ok.Ids + " | " + ok.Writers);
            Check("g9.未接列属性照样进元表（id 空间完整）",
                ok.Table.Contains("new NodePropInfo(128, \"X\", Ch.Transform, Ch.Transform, NodePropStore.Unbacked),"),
                ok.Table);

            RunResult noPending = Run(Compile(DefaultAbi, unbackedOk.Replace(", Pending = \"列随 M9-99 接入\"", "")));
            Check("g9.未接列却不写去处报 FNP006", noPending.Has("FNP006"), noPending.Ids);
        }

        // ── g10：确定性 —— 同输入两次生成逐字节相等 ────────────────────────────────
        {
            RunResult a = Run(Compile(DefaultAbi, FullTable));
            RunResult b = Run(Compile(DefaultAbi, FullTable));
            Check("g10.同输入两次生成逐字节相等",
                string.Equals(a.Writers, b.Writers, StringComparison.Ordinal)
                && string.Equals(a.Table, b.Table, StringComparison.Ordinal));
        }

        // ── g11：确定性 —— 声明顺序与空白不参与排序（生成物按 PropId 升序）──────────
        {
            const string shuffled = @"
namespace FairyNext.Core
{

    [NodeProp(""X"", Ch.Transform, NodePropStore.FloatColumn, Column = ""_posX"")]


    [NodeProp(""Visible"", Ch.Visible, NodePropStore.VisualBit, Column = ""_localVisual"", Bit = ""Visible"", Down = Ch.DownVisible)]
    [NodeProp(""Alpha"", Ch.Color, NodePropStore.AlphaU8, Column = ""_localVisual"", Down = Ch.DownColor)]
    [NodeProp(""Width"", Ch.Layout, NodePropStore.FloatColumn, Column = ""_width"")]
    public static partial class NodeProps { }
}";
            RunResult ordered = Run(Compile(DefaultAbi, FullTable));
            RunResult mixed = Run(Compile(DefaultAbi, shuffled));
            Check("g11.打乱声明顺序 + 插空行不改变生成物",
                string.Equals(ordered.Writers, mixed.Writers, StringComparison.Ordinal)
                && string.Equals(ordered.Table, mixed.Table, StringComparison.Ordinal));
        }

        // ── g12：没有归属表的编译（别的工程引到同一分析器）→ 零诊断零生成 ───────────
        {
            RunResult r = Run(Compile(DefaultAbi, "public class Unrelated { }"));
            Check("g12.无归属表的编译不产诊断也不产文件", r.Diags.IsEmpty && r.FileCount == 0, r.Ids);
        }

        // ── g13：宿主符号缺失（ABI 单源没有任何 PropId）→ FNP007，不猜不降级 ────────
        {
            var trees = new List<SyntaxTree>
            {
                CSharpSyntaxTree.ParseText(Prelude, path: "Prelude.cs"),
                CSharpSyntaxTree.ParseText("namespace FairyNext.Contracts { public static class AbiLayout { public const byte PropIdNone = 0; } }", path: "Abi.cs"),
                CSharpSyntaxTree.ParseText(FullTable, path: "Table.cs"),
            };
            string dir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            var comp = CSharpCompilation.Create("PropGenGate", trees, new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(dir, "System.Runtime.dll")),
            }, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            RunResult r = Run(comp);
            Check("g13.ABI 单源没有 PropId 常量报 FNP007", r.Has("FNP007"), r.Ids);
        }

        // ── g14：归属表不是 partial / 是嵌套类型 → FNP008（否则生成的分部会另起新类型）──
        {
            RunResult notPartial = Run(Compile(DefaultAbi, FullTable.Replace("static partial class NodeProps", "static class NodeProps")));
            Check("g14.归属表未声明 partial 报 FNP008", notPartial.Has("FNP008"), notPartial.Ids);

            const string nestedTable = @"
namespace FairyNext.Core
{
    public partial class Outer
    {
        [NodeProp(""Width"", Ch.Layout, NodePropStore.FloatColumn, Column = ""_width"")]
        public static partial class Inner { }
    }
}";
            RunResult nested = Run(Compile(DefaultAbi, nestedTable));
            Check("g14.嵌套归属表报 FNP008", nested.Has("FNP008"), nested.Ids);

            // 归属表挪出 FairyNext.Core = 生成的分部取不到 Ch/NodePropInfo：拒绝生成，而不是发一份编译不过的文件
            const string elsewhere = @"
namespace Somewhere
{
    [FairyNext.Core.NodeProp(""Width"", FairyNext.Core.Ch.Layout, FairyNext.Core.NodePropStore.FloatColumn, Column = ""_width"")]
    public static partial class NodeProps { }
}";
            RunResult far = Run(Compile(DefaultAbi, elsewhere));
            Check("g14.归属表不与 Ch 同命名空间报 FNP008", far.Has("FNP008"), far.Ids);
        }

        // ── e1（M1-21）：EventCtx 逃逸必须是**编译错**，不是文档警告 ────────────────
        //
        // 事件·不变量 1【编译错】的执法点。它落在这里而不是主 runner，理由与本工程存在的理由相同：
        // 判定「某段源码编译不过、且报的是那个错号」需要 Roslyn，而主 runner 是零 NuGet 工程。
        // 编译对象是**真实的 FairyNext.Core 程序集**（不是 stub）：stub 只能证明 C# 编译器会拦
        // ref struct 逃逸，证明不了 EventCtx 真的是 ref struct。
        EventCtxEscapeGate();

        Console.WriteLine($"RESULT pass={_pass} fail={_fail}");
        Console.Write(Log.ToString());
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    /// <summary>
    /// 事件·不变量 1：<c>EventCtx</c> 存字段 = CS8345、被闭包捕获 = CS1628，正例零错误。
    /// 找不到已构建的 FairyNext.Core.dll 时**有声跳过**（同 M1-17 的 Monaco golden 纪律：
    /// 缺资产不假装通过，也不把整套门拖红）。
    /// </summary>
    private static void EventCtxEscapeGate()
    {
        string core = FindCoreAssembly();
        if (core == null)
        {
            Check("e1.SKIP（未找到 FairyNext.Core.dll，先 dotnet build 再跑本门）", true);
            return;
        }

        string dir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        string coreDir = Path.GetDirectoryName(core);
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(dir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(dir, "netstandard.dll")),
            MetadataReference.CreateFromFile(core),
        };
        foreach (string dep in new[] { "FairyNext.Numerics.dll", "FairyNext.Contracts.dll", "FairyNext.State.dll" })
        {
            string p = Path.Combine(coreDir, "..", "..", dep.Replace(".dll", ""), Path.GetFileName(coreDir), dep);
            if (File.Exists(p)) refs.Add(MetadataReference.CreateFromFile(Path.GetFullPath(p)));
        }

        const string stored = @"
using FairyNext.Core.Events;
public class Bad { private EventCtx _kept; public void Keep(ref EventCtx c) { _kept = c; } }";
        const string captured = @"
using FairyNext.Core.Events;
public class Bad2 { public System.Action Cap(ref EventCtx c) { return () => c.StopPropagation(); } }";
        const string good = @"
using FairyNext.Core.Events;
public class Fine { public void Handle(ref EventCtx c, in PointerInput p) { c.StopPropagation(); } }";

        Check("e1.EventCtx 存字段报 CS8345（ref struct 不能作字段类型）",
            ErrorsOf(stored, refs).Contains("CS8345"), ErrorsOf(stored, refs));
        Check("e1.EventCtx 被闭包捕获报 CS1628（ref 参数不进匿名函数）",
            ErrorsOf(captured, refs).Contains("CS1628"), ErrorsOf(captured, refs));
        Check("e1.正常的 handler 形态零错误", ErrorsOf(good, refs).Length == 0, ErrorsOf(good, refs));
    }

    private static string ErrorsOf(string source, List<MetadataReference> refs)
    {
        var comp = CSharpCompilation.Create("EventCtxGate",
            new[] { CSharpSyntaxTree.ParseText(source, path: "Escape.cs") }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return string.Join("; ", comp.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Id + ":" + d.GetMessage()));
    }

    /// <summary>自本程序集所在目录向上找 <c>artifacts/bin/FairyNext.Core/&lt;cfg&gt;/FairyNext.Core.dll</c>。</summary>
    private static string FindCoreAssembly()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        string cfg = here.Name;                                   // debug / release
        for (DirectoryInfo d = here; d != null; d = d.Parent)
        {
            if (!string.Equals(d.Name, "bin", StringComparison.OrdinalIgnoreCase)) continue;
            string p = Path.Combine(d.FullName, "FairyNext.Core", cfg, "FairyNext.Core.dll");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
