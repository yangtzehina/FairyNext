using System.Globalization;
using System.Text;

namespace FairyNext.Tools.OracleCompare;

/// <summary>一条越界的数值字段差异。</summary>
public sealed class LayoutFieldDiff
{
    public string Path { get; }
    public string Field { get; }
    public double Expected { get; }
    public double Actual { get; }
    public double Eps { get; }
    public double Delta => Math.Abs(Actual - Expected);

    public LayoutFieldDiff(string path, string field, double expected, double actual, double eps)
    {
        Path = path;
        Field = field;
        Expected = expected;
        Actual = actual;
        Eps = eps;
    }

    public override string ToString() => string.Format(CultureInfo.InvariantCulture,
        "{0}.{1}: 期望 {2}，实得 {3}（Δ={4:0.####} > 容差 {5:0.####}）", Path, Field, Expected, Actual, Delta, Eps);
}

/// <summary>布局比对结论。</summary>
public sealed class LayoutDiff
{
    public List<string> MissingPaths { get; } = new List<string>();
    public List<string> ExtraPaths { get; } = new List<string>();
    public List<string> StructuralFailures { get; } = new List<string>();
    public List<LayoutFieldDiff> FieldFailures { get; } = new List<LayoutFieldDiff>();

    public bool Pass => MissingPaths.Count == 0 && ExtraPaths.Count == 0
                        && StructuralFailures.Count == 0 && FieldFailures.Count == 0;

    /// <summary>越界字段里 Δ/容差 最大的一条；全通过时为 null。用于「一行说清最坏情况」。</summary>
    public LayoutFieldDiff? Worst
    {
        get
        {
            LayoutFieldDiff? worst = null;
            foreach (LayoutFieldDiff d in FieldFailures)
                if (worst == null || d.Delta / d.Eps > worst.Delta / worst.Eps) worst = d;
            return worst;
        }
    }

    public string Report()
    {
        if (Pass) return "布局一致";
        var sb = new StringBuilder();
        foreach (string p in MissingPaths) sb.Append("缺节点 ").Append(p).Append('\n');
        foreach (string p in ExtraPaths) sb.Append("多节点 ").Append(p).Append('\n');
        foreach (string s in StructuralFailures) sb.Append(s).Append('\n');
        foreach (LayoutFieldDiff d in FieldFailures) sb.Append(d).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }
}

/// <summary>
/// 布局数值逐字段容差比对。
///
/// 对齐方式是 **path 集合 + 顺序**，不是下标：下标对齐会把「插入了一个节点」报成后面全部节点都错，
/// 定位能力当场归零。path 缺失/多出/顺序变化各自单列，跟数值差异分开报——它们是不同性质的回归。
/// </summary>
public static class LayoutComparer
{
    public static LayoutDiff Compare(LayoutSnapshot expected, LayoutSnapshot actual, OracleTolerance tol)
    {
        var diff = new LayoutDiff();

        var actualByPath = new Dictionary<string, LayoutNode>(StringComparer.Ordinal);
        foreach (LayoutNode n in actual.Nodes)
        {
            if (actualByPath.ContainsKey(n.Path))
                diff.StructuralFailures.Add($"候选里 path 重复：{n.Path}（导出端应保证同层同名唯一）");
            else actualByPath[n.Path] = n;
        }

        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (LayoutNode e in expected.Nodes)
        {
            expectedPaths.Add(e.Path);
            if (!actualByPath.TryGetValue(e.Path, out LayoutNode? a)) { diff.MissingPaths.Add(e.Path); continue; }

            if (!string.Equals(e.Type, a.Type, StringComparison.Ordinal))
                diff.StructuralFailures.Add($"{e.Path}.type: 期望 {e.Type}，实得 {a.Type}");
            if (!string.Equals(e.Name, a.Name, StringComparison.Ordinal))
                diff.StructuralFailures.Add($"{e.Path}.name: 期望 {e.Name}，实得 {a.Name}");
            if (e.Visible != a.Visible)
                diff.StructuralFailures.Add($"{e.Path}.visible: 期望 {e.Visible}，实得 {a.Visible}");

            Num(diff, e.Path, "x", e.X, a.X, tol.LayoutEpsPx);
            Num(diff, e.Path, "y", e.Y, a.Y, tol.LayoutEpsPx);
            Num(diff, e.Path, "width", e.Width, a.Width, tol.LayoutEpsPx);
            Num(diff, e.Path, "height", e.Height, a.Height, tol.LayoutEpsPx);
            Num(diff, e.Path, "scaleX", e.ScaleX, a.ScaleX, tol.LayoutEpsUnitless);
            Num(diff, e.Path, "scaleY", e.ScaleY, a.ScaleY, tol.LayoutEpsUnitless);
            Num(diff, e.Path, "alpha", e.Alpha, a.Alpha, tol.LayoutEpsUnitless);
            Num(diff, e.Path, "rotation", e.Rotation, a.Rotation, tol.LayoutEpsDegrees);
        }

        foreach (LayoutNode a in actual.Nodes)
            if (!expectedPaths.Contains(a.Path)) diff.ExtraPaths.Add(a.Path);

        // 顺序是绘制序（paintOrder）的可见影子：路径集合相同但顺序变了，像素多半也变了。
        if (diff.MissingPaths.Count == 0 && diff.ExtraPaths.Count == 0)
        {
            for (int i = 0; i < expected.Nodes.Count; i++)
            {
                if (!string.Equals(expected.Nodes[i].Path, actual.Nodes[i].Path, StringComparison.Ordinal))
                {
                    diff.StructuralFailures.Add(
                        $"节点顺序在第 {i} 位分歧：期望 {expected.Nodes[i].Path}，实得 {actual.Nodes[i].Path}");
                    break;   // 首分歧即止：其后全是它的回声
                }
            }
        }

        if (expected.StageWidth != actual.StageWidth || expected.StageHeight != actual.StageHeight)
            diff.StructuralFailures.Add($"stage 尺寸: 期望 {expected.StageWidth}x{expected.StageHeight}，" +
                                        $"实得 {actual.StageWidth}x{actual.StageHeight}");

        return diff;
    }

    private static void Num(LayoutDiff diff, string path, string field, double e, double a, double eps)
    {
        if (Math.Abs(a - e) > eps) diff.FieldFailures.Add(new LayoutFieldDiff(path, field, e, a, eps));
    }
}
