namespace FairyNext.Compiler.Shape;

/// <summary>
/// GGroup 真节点化的**归属规划**（M1-20a）：显示列表的 groupId 引用 → 每个孩子的有效父
/// （组下标或 -1 = 组件根）。纯函数、可单测——树的实际重挂在 TreeBuilder。
///
/// fork 里 GGroup 是运行时代理对象（组员是组件的直接孩子，组移动 = 逐员补写坐标 +
/// `_updating` 抑制回环）；编译成真节点子树后移动/alpha/visible 的树级联免费（平面五烘焙表）。
/// 显示序前提：**组员在显示列表上连续**（编辑器保证；组条目紧随其员之后）——真节点化后
/// 组块整体占组条目的绘制位，连续时与 fork 逐员绘制的序逐位同；不连续出 FGM201（诊断，
/// 仍按归属重挂，绘制序差异明码）。
/// </summary>
internal static class GroupPlan
{
    /// <summary>
    /// 规划归属。<paramref name="groupOf"/>[i] = 孩子 i 声明的组孩子下标（-1 = 无组）；
    /// <paramref name="isGroup"/>[i] = 孩子 i 是 Group 类型。
    /// 返回 parentOf：每孩子的有效父孩子下标（-1 = 组件根）。非法归属（越界 / 指向非组 /
    /// 指向自己 / 组链成环）按无组处理并出 FGM203。
    /// </summary>
    public static int[] Plan(int[] groupOf, bool[] isGroup, string site, CompileDiagnostics diag)
    {
        int n = groupOf.Length;
        var parent = new int[n];

        // ── 归属清洗 ────────────────────────────────────────────────────────
        for (int i = 0; i < n; i++)
        {
            int g = groupOf[i];
            if (g < 0) { parent[i] = -1; continue; }
            if (g >= n || g == i || !isGroup[g])
            {
                diag.Add(FgmCodes.GroupMembershipInvalid, FgmSeverity.Warning, site,
                    "孩子 #" + i + " 的组归属非法（groupId=" + g + (g >= n ? " 越界" : g == i ? " 指向自己" : " 不是组")
                    + "），按无组处理");
                parent[i] = -1;
                continue;
            }
            parent[i] = g;
        }

        // ── 组链断环（a∈b ∧ b∈a 类损坏数据）。只对组节点断：环只可能由组构成
        //（parent 只指向组），先把组间环拆掉，普通组员的链随后必然有界——
        // 反过来先走组员会把无辜组员误断出组。──────────────────────────────
        for (int i = 0; i < n; i++)
        {
            if (!isGroup[i]) continue;
            int steps = 0;
            for (int p = parent[i]; p >= 0; p = parent[p])
            {
                if (++steps <= n) continue;
                diag.Add(FgmCodes.GroupMembershipInvalid, FgmSeverity.Warning, site,
                    "组孩子 #" + i + " 的组链成环，就地断开（按无组处理）");
                parent[i] = -1;
                break;
            }
        }

        // ── 连续性检查（诊断，不改归属）────────────────────────────────────
        for (int g = 0; g < n; g++)
        {
            if (!isGroup[g]) continue;
            int min = int.MaxValue, max = int.MinValue, members = 0;
            for (int i = 0; i < n; i++)
            {
                if (parent[i] != g) continue;
                members++;
                if (i < min) min = i;
                if (i > max) max = i;
            }
            if (members == 0) continue;
            for (int k = min; k <= max; k++)
            {
                if (InChain(parent, k, g)) continue;
                diag.Add(FgmCodes.GroupNotContiguous, FgmSeverity.Warning, site,
                    "组孩子 #" + g + " 的组员不连续（孩子 #" + k + " 夹在 [" + min + "," + max
                    + "] 中却不属于该组）——真节点化后组块整体占组的绘制位，序与 fork 有差异");
                break;
            }
        }
        return parent;
    }

    /// <summary>孩子 k 的组链是否含 g（含自身；组链已断环，walk 有界）。</summary>
    private static bool InChain(int[] parent, int k, int g)
    {
        for (int p = k; p >= 0; p = parent[p])
            if (p == g) return true;
        return false;
    }
}
