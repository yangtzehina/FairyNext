using FairyNext.Core;
using FairyNext.Core.Rendering;

namespace FairyNext.Backend.Mock;

/// <summary>
/// **流结构不变量门**（M1-14b 审计修复包）：对流内每个叶，沿 NodeTable 父链爬 **authored**
/// 可见位（SetVisible 写的那一位），断言链上无隐藏节点、节点活着且在树上——
/// 被隐藏祖先罩住的叶不允许留在流里。
///
/// 为什么必须是第三方判据、且**不读 worldVisual**：增量正确性门的两条腿共用同一个
/// <see cref="Extract"/> 与同一份派生列。2026-08 审计的 CRITICAL（隐藏容器后后代叶仍被绘制）
/// 正是「两腿共享错误前提」的形状——「隐藏子树免下钻」让隐藏子树后代的 worldVisual 合法陈旧，
/// 两条腿读同一份陈旧列，恒等式恒真，门全绿。这类盲区只有独立于两条腿的判据能抓：
/// authored 位是用户写入的原始事实，不经过任何派生管道；本门与增量门共用零个前提。
///
/// 调用点在**帧尾**（P7/P8 已收敛）：帧中间「脏已标、流未编」的窗口里流本来就落后于树，
/// 那不是违约。
///
/// **孤岛自 M1-23 起同受本门管辖**（M1-14b 登记的那条「孤岛不在本门内」到此结账）：
/// <see cref="IslandRecord.Node"/> 落地之后，「被隐藏祖先罩住的孤岛还在流里」与叶的同名错误
/// 是同一个形状——而孤岛的症状更难查：外部渲染器画的东西不在我们的实例流里，
/// 逐字节的增量门连它的像素都看不见，只有这条独立爬 authored 位的门能抓。
/// ④stencil 的 Enter/Exit 两条记录各查一次（括号的任一端漏检都等于没检）。
/// </summary>
public static class StreamStructureGate
{
    /// <summary>跑一次门。true = 流内叶与孤岛全部活着、在树上、父链无 authored 隐藏者。</summary>
    /// <param name="table">树域。</param>
    /// <param name="stream">被检的流（帧尾状态）。</param>
    /// <param name="error">首个违约单元的人读描述（通过时为空串）。</param>
    public static bool Check(NodeTable table, RenderStream stream, out string error)
    {
        if (table == null || stream == null)
        {
            error = "STRUCT-GATE fail：table 或 stream 为 null";
            return false;
        }

        ReadOnlySpan<LeafRange> leaves = stream.Leaves;
        for (int i = 0; i < leaves.Length; i++)
        {
            if (!CheckUnit(table, leaves[i].Node, "叶", i, out error)) return false;
        }

        ReadOnlySpan<IslandRecord> islands = stream.Islands;
        for (int i = 0; i < islands.Length; i++)
        {
            // 节点句柄为空 = 手工建的流（RenderStreamTests 直调 AddIsland），没有可爬的父链：
            // 本门只对「从树上来的」单元负责，不发明它管不着的事实。
            if (islands[i].Node.IsNone) continue;
            if (!CheckUnit(table, islands[i].Node, "孤岛", i, out error)) return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool CheckUnit(NodeTable table, NodeHandle node, string what, int index, out string error)
    {
        if (!table.IsAlive(node))
        {
            error = $"STRUCT-GATE fail：{what} {index} 引用已死节点（下标 {node.Index}）";
            return false;
        }
        if (table.PaintIndexOf(node) == NodeTable.NotInTree)
        {
            error = $"STRUCT-GATE fail：{what} {index}（节点 {node.Index}）已脱链却仍在流里";
            return false;
        }
        for (NodeHandle cur = node; !cur.IsNone; cur = table.Parent(cur))
        {
            if (!table.IsVisible(cur))
            {
                error = $"STRUCT-GATE fail：{what} {index}（节点 {node.Index}）在流里，"
                      + $"但父链上的节点 {cur.Index} authored 不可见（隐藏祖先罩住的单元被画了）";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }
}
