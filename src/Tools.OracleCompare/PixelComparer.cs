using System.Globalization;
using System.Text;

namespace FairyNext.Tools.OracleCompare;

/// <summary>差异热点框：一片相邻差异格子的包围盒（像素坐标，原点左上）。</summary>
public sealed class HotspotBox
{
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public int DiffPixels { get; }
    public int MaxChannelDelta { get; }

    public HotspotBox(int x, int y, int width, int height, int diffPixels, int maxChannelDelta)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        DiffPixels = diffPixels;
        MaxChannelDelta = maxChannelDelta;
    }

    public override string ToString() => string.Format(CultureInfo.InvariantCulture,
        "[{0},{1} {2}x{3}] 差异像素 {4}，最大通道 Δ {5}", X, Y, Width, Height, DiffPixels, MaxChannelDelta);
}

/// <summary>像素比对结论。</summary>
public sealed class PixelDiff
{
    public int Width { get; }
    public int Height { get; }
    public int DiffPixels { get; }
    public double DiffRatio { get; }
    public int MaxChannelDelta { get; }
    public int MaxDeltaX { get; }
    public int MaxDeltaY { get; }
    public List<HotspotBox> Hotspots { get; }
    public List<string> Failures { get; }

    public bool Pass => Failures.Count == 0;

    internal PixelDiff(int width, int height, int diffPixels, double diffRatio, int maxChannelDelta,
                       int maxDeltaX, int maxDeltaY, List<HotspotBox> hotspots, List<string> failures)
    {
        Width = width;
        Height = height;
        DiffPixels = diffPixels;
        DiffRatio = diffRatio;
        MaxChannelDelta = maxChannelDelta;
        MaxDeltaX = maxDeltaX;
        MaxDeltaY = maxDeltaY;
        Hotspots = hotspots;
        Failures = failures;
    }

    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "{0}x{1}: 差异像素 {2}（占比 {3:0.#####}），最大通道 Δ {4} @ ({5},{6})",
            Width, Height, DiffPixels, DiffRatio, MaxChannelDelta, MaxDeltaX, MaxDeltaY);
        foreach (string f in Failures) sb.Append('\n').Append("  ✗ ").Append(f);
        foreach (HotspotBox h in Hotspots) sb.Append('\n').Append("  热点 ").Append(h);
        return sb.ToString();
    }
}

/// <summary>
/// 像素比对：两级阈值 + 热点框。
///
/// 一级——单像素任一通道 |Δ| &gt; PixelChannelDelta 才算「差异像素」，吸收 GPU/驱动的最低位抖动；
/// 二级——差异像素占比 &gt; PixelDiffRatio 判失败。另加一条硬线 PixelMaxChannelDelta：
/// 任何单像素超过它立即失败，因为「几十个像素但完全画错」在占比口径下会被整幅图稀释成零。
///
/// 热点框把差异像素按 HotspotCell 网格聚类再连通合并，输出的是**去哪儿看**，不是一串坐标。
/// 这里刻意不写差异图 PNG：编码器是第二份可能出错的实现，而框 + 计数已经够定位；
/// 需要看图时用 golden 与候选两张 PNG 直接比。
/// </summary>
public static class PixelComparer
{
    public const int MaxHotspots = 8;

    public static PixelDiff Compare(PngImage expected, PngImage actual, OracleTolerance tol)
    {
        var failures = new List<string>();
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            failures.Add($"尺寸不同：期望 {expected.Width}x{expected.Height}，实得 {actual.Width}x{actual.Height}");
            return new PixelDiff(expected.Width, expected.Height, -1, 1.0, -1, -1, -1,
                                 new List<HotspotBox>(), failures);
        }

        int w = expected.Width, h = expected.Height;
        int cell = Math.Max(1, tol.HotspotCell);
        int gw = (w + cell - 1) / cell, gh = (h + cell - 1) / cell;
        int[] cellCount = new int[gw * gh];
        int[] cellMax = new int[gw * gh];

        byte[] e = expected.Rgba, a = actual.Rgba;
        int diffPixels = 0, maxDelta = 0, maxX = -1, maxY = -1;

        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                int d = Math.Abs(e[i] - a[i]);
                int d1 = Math.Abs(e[i + 1] - a[i + 1]); if (d1 > d) d = d1;
                int d2 = Math.Abs(e[i + 2] - a[i + 2]); if (d2 > d) d = d2;
                int d3 = Math.Abs(e[i + 3] - a[i + 3]); if (d3 > d) d = d3;
                if (d == 0) continue;

                if (d > maxDelta) { maxDelta = d; maxX = x; maxY = y; }
                if (d <= tol.PixelChannelDelta) continue;

                diffPixels++;
                int c = (y / cell) * gw + (x / cell);
                cellCount[c]++;
                if (d > cellMax[c]) cellMax[c] = d;
            }
        }

        double ratio = (double)diffPixels / (w * h);
        if (maxDelta > tol.PixelMaxChannelDelta)
            failures.Add($"最大通道 Δ {maxDelta} > 硬线 {tol.PixelMaxChannelDelta} @ ({maxX},{maxY})");
        if (ratio > tol.PixelDiffRatio)
            failures.Add(string.Format(CultureInfo.InvariantCulture,
                "差异像素占比 {0:0.#####} > 阈值 {1:0.#####}（{2} 像素通道 Δ > {3}）",
                ratio, tol.PixelDiffRatio, diffPixels, tol.PixelChannelDelta));

        return new PixelDiff(w, h, diffPixels, ratio, maxDelta, maxX, maxY,
                             Cluster(cellCount, cellMax, gw, gh, cell, w, h), failures);
    }

    /// <summary>占用格子的 4 邻接连通分量 → 包围盒，按差异像素数降序，最多 <see cref="MaxHotspots"/> 个。</summary>
    private static List<HotspotBox> Cluster(int[] count, int[] max, int gw, int gh, int cell, int w, int h)
    {
        var boxes = new List<HotspotBox>();
        bool[] seen = new bool[count.Length];
        var stack = new Stack<int>();

        for (int start = 0; start < count.Length; start++)
        {
            if (seen[start] || count[start] == 0) continue;
            seen[start] = true;
            stack.Push(start);
            int minCx = int.MaxValue, minCy = int.MaxValue, maxCx = -1, maxCy = -1, total = 0, peak = 0;

            while (stack.Count > 0)
            {
                int c = stack.Pop();
                int cx = c % gw, cy = c / gw;
                if (cx < minCx) minCx = cx;
                if (cy < minCy) minCy = cy;
                if (cx > maxCx) maxCx = cx;
                if (cy > maxCy) maxCy = cy;
                total += count[c];
                if (max[c] > peak) peak = max[c];

                if (cx > 0) Push(stack, seen, count, c - 1);
                if (cx + 1 < gw) Push(stack, seen, count, c + 1);
                if (cy > 0) Push(stack, seen, count, c - gw);
                if (cy + 1 < gh) Push(stack, seen, count, c + gw);
            }

            int x0 = minCx * cell, y0 = minCy * cell;
            int x1 = Math.Min(w, (maxCx + 1) * cell), y1 = Math.Min(h, (maxCy + 1) * cell);
            boxes.Add(new HotspotBox(x0, y0, x1 - x0, y1 - y0, total, peak));
        }

        boxes.Sort((p, q) => q.DiffPixels.CompareTo(p.DiffPixels));
        if (boxes.Count > MaxHotspots) boxes.RemoveRange(MaxHotspots, boxes.Count - MaxHotspots);
        return boxes;
    }

    private static void Push(Stack<int> stack, bool[] seen, int[] count, int c)
    {
        if (seen[c] || count[c] == 0) return;
        seen[c] = true;
        stack.Push(c);
    }
}
