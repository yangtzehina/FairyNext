using System.Globalization;
using System.Text;
using FairyNext.Contracts;
using FairyNext.Core.Layout;
using FairyNext.Core.Rendering;

namespace FairyNext.Compiler.Freeze;

/// <summary>
/// 内存计划与编译产物 golden 的**格式化**（账本住 <see cref="FgbFreezer"/>，这里只排版）。
///
/// 内存计划（架构机制 11）：定长分配的世界里内存是**编译期承诺而非运行期观测**——
/// 每次编译把段字节、每组件 instanceBytes、池预算、64 位属性掩码占用率打出来，
/// 「第 65 个属性」「实例块膨胀」才会在 CI 日志里被看见，而不是在真机 OOM 里。
/// 文本是确定性的（同输入同字节），于是它同时是**测试断言锚**：数字变了 diff 就现形。
///
/// 排版纪律：不含时间戳/路径/机器名（那些会让 golden 每次都变），数字一律
/// <c>InvariantCulture</c>（区域设置不进产物——`1,234` 与 `1.234` 在别的 locale 下真会发生）。
/// </summary>
internal static class MemoryPlan
{
    public static string Build(FgbFreezer f, int blobBytes)
    {
        var sb = new StringBuilder();
        sb.Append("memory-plan blob=").Append(N(blobBytes)).Append("B sections=")
          .Append(N(f.Sections.Count)).Append(" components=").Append(N(f.Comps.Count)).Append('\n');

        for (int i = 0; i < f.Sections.Count; i++)
        {
            (string name, uint fourcc, int bytes) = f.Sections[i];
            sb.Append("  section ").Append(name).Append(' ').Append(N(bytes)).Append("B");
            sb.Append(RecordNote(name, f, bytes));
            sb.Append('\n');
        }

        long poolTotal = 0;
        foreach (FgbFreezer.Frozen c in f.Comps)
        {
            long nodeCost = (long)c.NodeCount * Abi.NodeBytesPerNode;
            long pool = nodeCost + c.InstanceBytes;
            poolTotal += pool;
            sb.Append("  component ").Append(c.Sc.Item.Name ?? c.Sc.Item.Id)
              .Append(" nodes=").Append(N(c.NodeCount))
              .Append(" quads=").Append(N(c.QuadCount))
              .Append(" segs=").Append(N(c.SegCount))
              .Append(" leaves=").Append(N(c.LeafCount))
              .Append(" clips=").Append(N(c.ClipCount))
              .Append(" ops=").Append(N(c.OpCount))
              .Append(" slots=").Append(N(c.ResolvedSlots))
              .Append(" instanceBytes=").Append(N(c.InstanceBytes))
              .Append(" pool=").Append(N(pool)).Append('B')
              .Append('\n');
        }

        // 池预算 = 「实例化一份全部组件」的定长开销（NODE memcpy + 实例块）。
        sb.Append("  pool-budget total=").Append(N(poolTotal))
          .Append("B nodes=").Append(N(f.TotalNodes))
          .Append(" nodeBytes=").Append(N((long)f.TotalNodes * Abi.NodeBytesPerNode)).Append('B')
          .Append('\n');

        // canonical 去重率（机制 10）：分子 = 去重后条数，分母 = 登记次数。
        sb.Append("  canonical strings=").Append(N(f.Strings.Count)).Append('/').Append(N(f.Strings.Inserts))
          .Append(" content=").Append(N(f.Content.Count)).Append('/').Append(N(f.Content.Inserts))
          .Append(" texrefs=").Append(N(f.TexRefs.Count)).Append('/').Append(N(f.TexRefs.Inserts))
          .Append('\n');

        // 掩码占用率（机制 11 的第四项）。M1 的边界写明：BIND 未进编译面，故每组件的
        // 可观测属性掩码恒 0/64；能报的真预算是**属性 id 空间**的分组占用——「第 65 个属性」
        // 的告警位置在这里，随 M2 状态层接上真掩码后同一行会开始动。
        sb.Append("  mask observable=0/").Append(N(Abi.MaxObservableProps))
          .Append(" (BIND 未进 M1 编译面)");
        for (int i = 0; i < Abi.PropGroups.Length; i++)
        {
            AbiPropGroup g = Abi.PropGroups[i];
            int used = 0;
            for (int j = 0; j < Abi.PropIds.Length; j++)
                if (Abi.PropIds[j].Id >= g.First && Abi.PropIds[j].Id <= g.Last) used++;
            sb.Append(' ').Append(g.Name).Append('=').Append(N(used)).Append('/')
              .Append(N(g.Last - g.First + 1));
        }
        sb.Append('\n');
        return sb.ToString();
    }

    private static string RecordNote(string name, FgbFreezer f, int bytes)
    {
        switch (name)
        {
            case "NODE":
                return " nodes=" + N(f.TotalNodes) + " cols=" + N(Abi.NodeColumns.Length)
                    + " bytesPerNode=" + N(Abi.NodeBytesPerNode);
            case "QUAD": return " records=" + N(f.TotalQuads) + "×" + N(Abi.QuadInstanceSize) + "B";
            case "CLIP": return " records=" + N(f.TotalClips) + "×" + N(Abi.ClipEntrySize) + "B";
            case "COMP": return " records=" + N(bytes / AbiLayout.FgbCompSize) + "×" + N(AbiLayout.FgbCompSize) + "B";
            case "CONT": return " records=" + N(bytes / AbiLayout.FgbContSize) + "×" + N(AbiLayout.FgbContSize) + "B";
            case "LOCL": return " records=" + N(bytes / AbiLayout.FgbLocalSize) + "×" + N(AbiLayout.FgbLocalSize) + "B";
            case "LEAF": return " records=" + N(bytes / AbiLayout.FgbLeafSize) + "×" + N(AbiLayout.FgbLeafSize) + "B";
            case "SEGS": return " records=" + N(bytes / AbiLayout.FgbSegSize) + "×" + N(AbiLayout.FgbSegSize) + "B";
            case "TREF": return " records=" + N(bytes / AbiLayout.FgbTexRefSize) + "×" + N(AbiLayout.FgbTexRefSize) + "B";
            case "STRT": return " strings=" + N(f.Strings.Count) + " pool=" + N(f.Strings.PoolBytes) + "B";
            case "CNST": return " ops=" + N(f.TotalOps);
            default: return string.Empty;
        }
    }

    /// <summary>
    /// 编译产物 golden 文本（架构平面六「编译产物 golden」的三行形态）：
    /// <c>abi</c>（80B 实例 ABI 的偏移复述——C#↔HLSL 漂移在文本 diff 里现形）、
    /// <c>topo</c>（CNST 拓扑序，按编辑器 id 打印）、<c>bind</c>（订阅掩码，随 M2 BIND 进驻）。
    /// </summary>
    public static string BuildReactiveGraph(FgbFreezer f)
    {
        var sb = new StringBuilder();
        sb.Append("abi   QuadInstance ").Append(N(Abi.QuadInstanceSize)).Append('B');
        for (int i = 0; i < Abi.QuadInstanceFields.Length; i++)
        {
            AbiField fld = Abi.QuadInstanceFields[i];
            sb.Append(' ').Append(fld.HlslName).Append('@').Append(N(fld.Offset));
        }
        sb.Append('\n');
        sb.Append("abi   NodeRecord ").Append(N(Abi.NodeBytesPerNode)).Append("B cols=")
          .Append(N(Abi.NodeColumns.Length)).Append('\n');

        foreach (FgbFreezer.Frozen c in f.Comps)
        {
            string comp = c.Sc.Item.Name ?? c.Sc.Item.Id;
            ConstraintGraph? g = c.Sc.Constraints;
            if (g == null || g.Ops.Length == 0)
            {
                sb.Append("topo  ").Append(comp).Append("  (无关系)\n");
                continue;
            }
            sb.Append("topo  ").Append(comp).Append("  ");
            for (int i = 0; i < g.Ops.Length; i++)
            {
                ConstraintOp op = g.Ops[i];
                if (i > 0) sb.Append(" → ");
                sb.Append('#').Append(N(i)).Append(' ')
                  .Append(NameOf(c, op.DstNode)).Append('.').Append(op.Axis).Append(op.DstEdge)
                  .Append('=').Append(KindOf(op.Kind))
                  .Append('(').Append(NameOf(c, op.SrcNode)).Append('.').Append(op.SrcEdge).Append(')');
                if (op.PivotCorrect) sb.Append("+pivot");
            }
            sb.Append('\n');
            sb.Append("bind  ").Append(comp).Append("  (BIND 未进 M1 编译面：gear/controller 归 M2 状态层)\n");
        }
        return sb.ToString();
    }

    private static string KindOf(byte kind) => ((ConstraintKind)kind).ToString();

    private static string NameOf(FgbFreezer.Frozen c, ushort local) =>
        local == 0 ? "<root>" : c.Sc.Locals.EditorIdOf(local) ?? ("#" + N(local));

    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string N(ushort v) => v.ToString(CultureInfo.InvariantCulture);
    // 池预算是字节账：组件多起来 int 会溢，而溢出的账本比没有账本更糟（数字看着像真的）。
    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);
    private static string N(uint v) => v.ToString(CultureInfo.InvariantCulture);
}
