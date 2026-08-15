using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FairyNext.PropGen
{
    /// <summary>
    /// 节点属性 setter 生成器（M1-09）。输入是**两张已有的表**，不新增第三本账：
    ///  ① <c>src/Contracts/Abi.cs</c> 的 PropId 单源（经生成物 <c>AbiLayout.PropId*</c> 常量读入）；
    ///  ② <c>src/Core/NodeProps.cs</c> 的 <c>[NodeProp]</c> 通道归属声明。
    /// 输出是两份生成物：<c>NodeTable</c> 的每属性写口 + PropId 分派 switch，以及 <c>NodeProps.All</c> 元表。
    ///
    /// 执法点（这才是本生成器存在的理由——不是省打字）：
    ///  - **通道归属封闭**（架构不变量 1）：ABI 里有 id 而归属表没声明 = FNP001 编译错；
    ///  - **等值切断**（不变量 3）：每个写口的形态由生成器固定为「比较旧值 → 不同才写列 → Mark」，
    ///    人写不出「忘了比较」的 setter；
    ///  - **写来源分流**（裁决 1/4）：写口签名必带 <c>WriteSource</c> 并原样传给 Mark，
    ///    失效理由（<c>InvalidateReason</c>）由失效平面按来源派生，生成代码不自造第二套映射。
    ///
    /// 与 fork（FairyGUI.Mvvm.Generator）的关系：骨架、诊断体系、确定性纪律照搬，
    /// 字符串符号名与类型规则表全部换成 FairyNext 的（见 <see cref="SymbolContext"/>）。
    /// </summary>
    [Generator]
    public sealed class NodePropGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var tables = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    SymbolContext.AttributeName,
                    static (node, _) => node is ClassDeclarationSyntax,
                    static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol)
                .Collect();

            // 与 Compilation 合并：id 单源、Ch 位名、通道方向掩码、列类型全部按符号读，
            // 生成器里不留任何一份手抄的副本（副本会与运行时静默漂移，正是本包要消灭的东西）。
            context.RegisterSourceOutput(tables.Combine(context.CompilationProvider),
                static (spc, pair) => Emit(spc, pair.Right, pair.Left));
        }

        private static void Emit(SourceProductionContext spc, Compilation compilation,
            ImmutableArray<INamedTypeSymbol> tables)
        {
            // 没有归属表 = 本次编译与本生成器无关（其他工程也可能引用同一个分析器），静默退出。
            if (tables.IsDefaultOrEmpty) return;

            List<INamedTypeSymbol> distinct = Distinct(tables);
            INamedTypeSymbol table = distinct[0];
            for (int i = 1; i < distinct.Count; i++)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diag.UnsupportedTable,
                    Loc(distinct[i]), distinct[i].Name, "是第二张归属表；归属表只能有一个定义点"));
            }

            Location tableLoc = Loc(table);
            if (table.ContainingType != null || table.IsGenericType)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diag.UnsupportedTable, tableLoc, table.Name, "是嵌套或泛型类型"));
                return;
            }
            if (!SymbolContext.IsPartial(table))
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diag.UnsupportedTable, tableLoc, table.Name, "没有声明 partial"));
                return;
            }

            SymbolContext ctx = SymbolContext.Create(spc, compilation, tableLoc);
            if (ctx == null) return;

            // 生成的分部不带 using（生成物要能被人直接读，不靠隐式 using 猜类型来源）：
            // 归属表必须与 Ch/NodePropInfo 同命名空间，否则生成的 NodeProps 分部取不到它们。
            string tableNs = table.ContainingNamespace.ToDisplayString();
            if (tableNs != ctx.CoreNamespace)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diag.UnsupportedTable, tableLoc, table.Name,
                    "不在 " + ctx.CoreNamespace + "（生成的分部不带 using，取不到 Ch/NodePropInfo）"));
                return;
            }

            List<PropDecl> decls = ReadDeclarations(table, tableLoc);
            List<PropRow> rows = Join(spc, ctx, decls, tableLoc);

            spc.AddSource("NodeTable.Props.g.cs", Emitter.EmitWriters(ctx, rows));
            spc.AddSource("NodeProps.g.cs",
                Emitter.EmitTable(table.ContainingNamespace.ToDisplayString(), table.Name, ctx, rows));
        }

        /// <summary>读 <c>[NodeProp]</c> 声明（保持源码顺序：诊断顺序稳定；生成物另按 id 排序）。</summary>
        private static List<PropDecl> ReadDeclarations(INamedTypeSymbol table, Location tableLoc)
        {
            var list = new List<PropDecl>();
            foreach (AttributeData a in table.GetAttributes())
            {
                if (a.AttributeClass?.ToDisplayString() != SymbolContext.AttributeName) continue;
                if (a.ConstructorArguments.Length != 3) continue;

                var d = new PropDecl
                {
                    Name = a.ConstructorArguments[0].Value as string ?? "",
                    Channel = ToU16(a.ConstructorArguments[1].Value),
                    Store = EnumMemberName(a.ConstructorArguments[2]),
                    Column = "",
                    Bit = "",
                    Pending = "",
                    Location = a.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? tableLoc,
                };

                foreach (KeyValuePair<string, TypedConstant> named in a.NamedArguments)
                {
                    switch (named.Key)
                    {
                        case "Down": d.Down = ToU16(named.Value.Value); break;
                        case "Column": d.Column = named.Value.Value as string ?? ""; break;
                        case "Bit": d.Bit = named.Value.Value as string ?? ""; break;
                        case "Pending": d.Pending = named.Value.Value as string ?? ""; break;
                    }
                }
                list.Add(d);
            }
            return list;
        }

        /// <summary>归属表 ⋈ ABI 单源。两侧的缺口分别报 FNP001（缺归属）与 FNP002（孤儿声明）。</summary>
        private static List<PropRow> Join(SourceProductionContext spc, SymbolContext ctx,
            List<PropDecl> decls, Location tableLoc)
        {
            var byName = new Dictionary<string, PropDecl>(StringComparer.Ordinal);
            foreach (PropDecl d in decls)
            {
                if (byName.ContainsKey(d.Name))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(Diag.DuplicateDeclaration, d.Location, d.Name));
                    continue;
                }
                byName.Add(d.Name, d);
            }

            var rows = new List<PropRow>();
            var matched = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, byte> abi in ctx.AbiPropIds)
            {
                if (!byName.TryGetValue(abi.Key, out PropDecl d))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(Diag.MissingOwnership, tableLoc, abi.Key, abi.Value));
                    continue;
                }
                matched.Add(abi.Key);
                PropRow row = Validate(spc, ctx, d, abi.Value);
                if (row != null) rows.Add(row);
            }

            foreach (PropDecl d in decls)
            {
                if (!matched.Contains(d.Name) && !string.IsNullOrEmpty(d.Name))
                    spc.ReportDiagnostic(Diagnostic.Create(Diag.OrphanDeclaration, d.Location, d.Name));
            }

            rows.Sort((a, b) => a.Id.CompareTo(b.Id));
            return rows;
        }

        /// <summary>逐条校验：非法声明只丢自己那一行（其余属性照常生成，诊断已是编译错，不会漏网）。</summary>
        private static PropRow Validate(SourceProductionContext spc, SymbolContext ctx, PropDecl d, byte id)
        {
            if (!SyntaxFacts.IsValidIdentifier(d.Name))
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diag.BadPropName, d.Location, d.Name));
                return null;
            }

            bool single = d.Channel != 0 && (d.Channel & (d.Channel - 1)) == 0;
            if (!single || (d.Channel & ctx.UpMask) != d.Channel)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diag.BadChannel, d.Location, d.Name, d.Channel.ToString("X")));
                return null;
            }

            if ((d.Down & ctx.DownMask) != d.Down)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diag.BadDownChannel, d.Location, d.Name, d.Down.ToString("X")));
                return null;
            }

            string bad = CheckStorage(ctx, d);
            if (bad != null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diag.BadStorage, d.Location, d.Name, d.Store, bad));
                return null;
            }

            return new PropRow
            {
                Id = id,
                Name = d.Name,
                Channel = d.Channel,
                Down = d.Down,
                Store = d.Store,
                Column = d.Column,
                Bit = d.Bit,
                Pending = d.Pending,
            };
        }

        private static string CheckStorage(SymbolContext ctx, PropDecl d)
        {
            switch (d.Store)
            {
                case "FloatColumn":
                    return ctx.CheckColumn(d.Column, SpecialType.System_Single);
                case "AlphaU8":
                    return ctx.CheckColumn(d.Column, SpecialType.System_UInt32);
                case "VisualBit":
                    return ctx.CheckColumn(d.Column, SpecialType.System_UInt32) ?? ctx.CheckBit(d.Bit);
                case "Unbacked":
                    if (d.Column.Length != 0 || d.Bit.Length != 0)
                        return "未接列的属性不该带 Column/Bit";
                    return d.Pending.Length == 0 ? "Pending 未填——未接列必须写明由哪个包接" : null;
                default:
                    return "未知的存储形态（生成器没有对应的发射分支）";
            }
        }

        private static List<INamedTypeSymbol> Distinct(ImmutableArray<INamedTypeSymbol> types)
        {
            var seen = new List<INamedTypeSymbol>();
            foreach (INamedTypeSymbol t in types)
            {
                if (!seen.Any(s => SymbolEqualityComparer.Default.Equals(s, t))) seen.Add(t);
            }
            seen.Sort((a, b) => string.CompareOrdinal(a.ToDisplayString(), b.ToDisplayString()));
            return seen;
        }

        private static Location Loc(ISymbol s) => s.Locations.FirstOrDefault() ?? Location.None;

        private static ushort ToU16(object value) => value == null ? (ushort)0 : Convert.ToUInt16(value);

        /// <summary>枚举实参 → 成员名（按值查名，不按序号：枚举重编号不会静默改语义）。</summary>
        private static string EnumMemberName(TypedConstant tc)
        {
            if (!(tc.Type is INamedTypeSymbol e) || tc.Value == null) return "";
            long v = Convert.ToInt64(tc.Value);
            foreach (IFieldSymbol f in e.GetMembers().OfType<IFieldSymbol>())
            {
                if (f.HasConstantValue && Convert.ToInt64(f.ConstantValue) == v) return f.Name;
            }
            return "";
        }
    }
}
