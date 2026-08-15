using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FairyNext.PropGen
{
    /// <summary>一条经过校验的属性行（归属表声明 ⋈ ABI 单源 id）。生成物按 <see cref="Id"/> 升序发射。</summary>
    internal sealed class PropRow
    {
        internal byte Id;
        internal string Name;
        internal ushort Channel;      // 单个上行通道位
        internal ushort Down;         // 下行伴随位（可为 0）
        internal string Store;        // NodePropStore 成员名
        internal string Column;
        internal string Bit;
        internal string Pending;

        internal ushort Marks => (ushort)(Channel | Down);
    }

    /// <summary>归属表里的一条**未校验**声明（读自 [NodeProp]）。</summary>
    internal sealed class PropDecl
    {
        internal string Name;
        internal ushort Channel;
        internal ushort Down;
        internal string Store;
        internal string Column;
        internal string Bit;
        internal string Pending;
        internal Location Location;
    }

    /// <summary>
    /// 生成器与运行时的**全部耦合面**：一组按名字解析的符号 + 两张从运行时常量读来的表。
    /// 零程序集引用（承自 fork 的形态）——耦合只有符号名，解析不到就报 FNP007，不猜、不静默降级。
    /// </summary>
    internal sealed class SymbolContext
    {
        internal const string AttributeName = "FairyNext.Core.NodePropAttribute";
        private const string AbiLayoutName = "FairyNext.Contracts.AbiLayout";
        private const string ChName = "FairyNext.Core.Ch";
        private const string ChMaskName = "FairyNext.Core.ChMask";
        private const string NodeTableName = "FairyNext.Core.NodeTable";
        private const string VisualName = "FairyNext.Core.Visual";
        private const string PropIdPrefix = "PropId";
        private const string PropIdNone = "PropIdNone";
        private const string AlphaCodecName = "ToU8";

        /// <summary>ABI 单源里已启用的 PropId（名 → id），按 id 升序。</summary>
        internal List<KeyValuePair<string, byte>> AbiPropIds = new List<KeyValuePair<string, byte>>();

        /// <summary>Ch 位 → 成员名（发射 <c>Ch.Transform | Ch.DownColor</c> 这类表达式用）。</summary>
        internal Dictionary<ushort, string> ChBitNames = new Dictionary<ushort, string>();

        internal ushort UpMask;
        internal ushort DownMask;

        /// <summary>Ch 所在命名空间——生成的分部不带 using，归属表必须与它同住（否则取不到 Ch/NodePropInfo）。</summary>
        internal string CoreNamespace = "";

        internal INamedTypeSymbol NodeTable;
        internal INamedTypeSymbol Visual;

        /// <summary>解析全部宿主符号；任何一处缺失都报 FNP007 并返回 null。</summary>
        internal static SymbolContext Create(SourceProductionContext spc, Compilation compilation, Location loc)
        {
            var ctx = new SymbolContext();
            bool ok = true;

            INamedTypeSymbol abi = compilation.GetTypeByMetadataName(AbiLayoutName);
            if (abi == null) ok = Missing(spc, loc, AbiLayoutName);
            else
            {
                foreach (IFieldSymbol f in abi.GetMembers().OfType<IFieldSymbol>())
                {
                    if (!f.HasConstantValue || f.Name == PropIdNone) continue;
                    if (!f.Name.StartsWith(PropIdPrefix, StringComparison.Ordinal)) continue;
                    ctx.AbiPropIds.Add(new KeyValuePair<string, byte>(
                        f.Name.Substring(PropIdPrefix.Length), Convert.ToByte(f.ConstantValue)));
                }
                // 元数据成员序不保证稳定；生成物按 id 升序，是确定性的一半（另一半是声明序不参与排序）。
                ctx.AbiPropIds.Sort((a, b) => a.Value.CompareTo(b.Value));
                if (ctx.AbiPropIds.Count == 0) ok = Missing(spc, loc, AbiLayoutName + " 的 PropId 常量");
            }

            INamedTypeSymbol ch = compilation.GetTypeByMetadataName(ChName);
            if (ch == null) ok = Missing(spc, loc, ChName);
            else
            {
                ctx.CoreNamespace = ch.ContainingNamespace.ToDisplayString();
                foreach (IFieldSymbol f in ch.GetMembers().OfType<IFieldSymbol>())
                {
                    if (!f.HasConstantValue) continue;
                    ushort v = Convert.ToUInt16(f.ConstantValue);
                    if (v != 0 && (v & (v - 1)) == 0 && !ctx.ChBitNames.ContainsKey(v))
                        ctx.ChBitNames.Add(v, f.Name);
                }
            }

            INamedTypeSymbol mask = compilation.GetTypeByMetadataName(ChMaskName);
            IFieldSymbol up = mask?.GetMembers("Up").OfType<IFieldSymbol>().FirstOrDefault();
            IFieldSymbol down = mask?.GetMembers("Down").OfType<IFieldSymbol>().FirstOrDefault();
            if (up == null || !up.HasConstantValue || down == null || !down.HasConstantValue)
                ok = Missing(spc, loc, ChMaskName + ".Up / .Down");
            else
            {
                ctx.UpMask = Convert.ToUInt16(up.ConstantValue);
                ctx.DownMask = Convert.ToUInt16(down.ConstantValue);
            }

            ctx.NodeTable = compilation.GetTypeByMetadataName(NodeTableName);
            if (ctx.NodeTable == null) ok = Missing(spc, loc, NodeTableName);
            else if (!IsPartial(ctx.NodeTable))
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diag.UnsupportedTable, loc,
                    ctx.NodeTable.Name, "没有声明 partial"));
                ok = false;
            }
            else if (!ctx.NodeTable.GetMembers(AlphaCodecName).Any())
                ok = Missing(spc, loc, NodeTableName + "." + AlphaCodecName + "（α 的 u8 编码唯一点）");

            ctx.Visual = compilation.GetTypeByMetadataName(VisualName);
            if (ctx.Visual == null) ok = Missing(spc, loc, VisualName);

            return ok ? ctx : null;
        }

        private static bool Missing(SourceProductionContext spc, Location loc, string what)
        {
            spc.ReportDiagnostic(Diagnostic.Create(Diag.MissingHostSymbol, loc, what));
            return false;
        }

        internal static bool IsPartial(INamedTypeSymbol type) =>
            type.DeclaringSyntaxReferences.Length > 0
            && type.DeclaringSyntaxReferences.All(r =>
                r.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax c
                && c.Modifiers.Any(SyntaxKind.PartialKeyword));

        /// <summary>把 Ch 位集渲染成源码表达式（按位序升序，确定性）。空集 = <c>Ch.None</c>。</summary>
        internal string ChExpr(ushort bits)
        {
            var parts = new List<string>();
            for (int b = 0; b < 16; b++)
            {
                ushort bit = (ushort)(1 << b);
                if ((bits & bit) == 0) continue;
                parts.Add(ChBitNames.TryGetValue(bit, out string name) ? "Ch." + name : "(Ch)0x" + bit.ToString("X"));
            }
            return parts.Count == 0 ? "Ch.None" : string.Join(" | ", parts);
        }

        /// <summary>列字段校验：存在 ∧ 类型是期望的数组元素类型。返回错因，null = 通过。</summary>
        internal string CheckColumn(string column, SpecialType element)
        {
            if (string.IsNullOrEmpty(column)) return "Column 未填";
            IFieldSymbol f = NodeTable.GetMembers(column).OfType<IFieldSymbol>().FirstOrDefault();
            if (f == null) return "NodeTable 上没有列字段 " + column;
            if (!(f.Type is IArrayTypeSymbol arr) || arr.ElementType.SpecialType != element)
                return "列 " + column + " 的元素类型不是 " + element.ToString().Replace("System_", "").ToLowerInvariant();
            return null;
        }

        /// <summary>位常量校验：Visual 上存在同名 uint 常量。返回错因，null = 通过。</summary>
        internal string CheckBit(string bit)
        {
            if (string.IsNullOrEmpty(bit)) return "Bit 未填";
            IFieldSymbol f = Visual.GetMembers(bit).OfType<IFieldSymbol>().FirstOrDefault();
            if (f == null || !f.HasConstantValue || f.Type.SpecialType != SpecialType.System_UInt32)
                return "Visual 上没有 uint 位常量 " + bit;
            return null;
        }
    }
}
