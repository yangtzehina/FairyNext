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
/// 那不是违约。孤岛不在本门内——<see cref="IslandDesc"/> 不带节点句柄，其可见性归 M1-23。
/// </summary>
public static class StreamStructureGate
{
    /// <summary>跑一次门。true = 流内叶全部活着、在树上、父链无 authored 隐藏者。</summary>
    /// <param name="table">树域。</param>
    /// <param name="stream">被检的流（帧尾状态）。</param>
    /// <param name="error">首个违约叶的人读描述（通过时为空串）。</param>
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
            NodeHandle node = leaves[i].Node;
            if (!table.IsAlive(node))
            {
                error = $"STRUCT-GATE fail：叶 {i} 引用已死节点（下标 {node.Index}）";
                return false;
            }
            if (table.PaintIndexOf(node) == NodeTable.NotInTree)
            {
                error = $"STRUCT-GATE fail：叶 {i}（节点 {node.Index}）已脱链却仍在流里";
                return false;
            }
            for (NodeHandle cur = node; !cur.IsNone; cur = table.Parent(cur))
            {
                if (!table.IsVisible(cur))
                {
                    error = $"STRUCT-GATE fail：叶 {i}（节点 {node.Index}）在流里，"
                          + $"但父链上的节点 {cur.Index} authored 不可见（隐藏祖先罩住的叶被画了）";
                    return false;
                }
            }
        }
        error = string.Empty;
        return true;
    }
}
