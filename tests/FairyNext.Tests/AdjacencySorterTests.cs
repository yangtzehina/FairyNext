using FairyNext.Core.Rendering;

namespace FairyNext.Tests;

/// <summary>
/// AdjacencySorter 排序 golden（M1-03）：同 key 前移（相邻性收敛）不得越过 bounds
/// 重叠者——绘制序只在视觉不可分辨处改变。期望序为手工按 fork 算法逐步推演所得。
/// </summary>
public static partial class Program
{
    private static readonly object KeyA = new();
    private static readonly object KeyB = new();
    private static readonly object KeyC = new();

    private static AdjacencyEntry E(object key, float x0, float y0, float x1, float y1, int payload)
        => new() { key = key, x0 = x0, y0 = y0, x1 = x1, y1 = y1, payload = payload };

    private static string SortPayloads(List<AdjacencyEntry> items)
    {
        AdjacencySorter.Sort(items);
        return string.Join(",", items.ConvertAll(e => e.payload));
    }

    private static void AdjacencySuite()
    {
        // golden 1：全不重叠——尾部 A 前移与首个 A 相邻（A A B）
        Check("Adjacency: 无重叠时同 key 聚拢", SortPayloads(new List<AdjacencyEntry>
        {
            E(KeyA, 0, 0, 10, 10, 0),
            E(KeyB, 20, 20, 30, 30, 1),
            E(KeyA, 40, 40, 50, 50, 2),
        }) == "0,2,1");

        // golden 2：中间 B 与两个 A 都重叠——任何移动都会改变可见结果，序保持原样
        Check("Adjacency: 重叠者挡路则不动", SortPayloads(new List<AdjacencyEntry>
        {
            E(KeyA, 0, 0, 10, 10, 0),
            E(KeyB, 5, 5, 15, 15, 1),
            E(KeyA, 8, 8, 20, 20, 2),
        }) == "0,1,2");

        // golden 3：尾部 A 想并入头部 A 段，但中途 C 与其重叠——只沉到 C 之后
        //（越过不重叠的 B，不越过重叠的 C）：A C A B
        Check("Adjacency: 前移止步于重叠者之后", SortPayloads(new List<AdjacencyEntry>
        {
            E(KeyA, 0, 0, 10, 10, 0),
            E(KeyC, 30, 0, 60, 10, 1),
            E(KeyB, 100, 0, 110, 10, 2),
            E(KeyA, 40, 0, 50, 10, 3),
        }) == "0,1,3,2");
    }
}
