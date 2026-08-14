using System.Text;

namespace FairyNext.Tools.OracleCompare;

/// <summary>一次 golden 对拍的完整结论。</summary>
public sealed class OracleCompareResult
{
    /// <summary>可比性失败：基线与候选的采集条件不同（oracle SHA / Unity / 色彩空间 / 图形 API）。</summary>
    public List<string> Inadmissible { get; }

    public LayoutDiff Layout { get; }
    public PixelDiff Pixels { get; }

    public bool Pass => Inadmissible.Count == 0 && Layout.Pass && Pixels.Pass;

    internal OracleCompareResult(List<string> inadmissible, LayoutDiff layout, PixelDiff pixels)
    {
        Inadmissible = inadmissible;
        Layout = layout;
        Pixels = pixels;
    }

    public string Report()
    {
        var sb = new StringBuilder();
        sb.Append(Pass ? "PASS" : "FAIL").Append(" oracle 对拍\n");
        foreach (string r in Inadmissible) sb.Append("  ✗ 基线不可比 ").Append(r).Append('\n');
        sb.Append("  布局: ").Append(Layout.Report().Replace("\n", "\n         ")).Append('\n');
        sb.Append("  像素: ").Append(Pixels.Report().Replace("\n", "\n        "));
        return sb.ToString();
    }
}

/// <summary>
/// golden 对拍入口。判定 = 可比性 ∧ 布局逐字段容差 ∧ 像素两级阈值——三者全过才算过。
///
/// 容差取自**基线自己的 meta**，不取调用方传参：门的松紧写在入库产物里，改松紧必须改 golden 并进 code review，
/// 而不是在调用点悄悄放宽。
/// </summary>
public static class OracleComparer
{
    public static OracleCompareResult Compare(OracleGolden golden, OracleGolden candidate)
    {
        List<string> inadmissible = golden.AdmissibilityAgainst(candidate);
        LayoutDiff layout = LayoutComparer.Compare(golden.Layout, candidate.Layout, golden.Tolerance);
        PixelDiff pixels = PixelComparer.Compare(golden.Image, candidate.Image, golden.Tolerance);
        return new OracleCompareResult(inadmissible, layout, pixels);
    }

    /// <summary>目录对目录：<paramref name="goldenDir"/> 是入库基线，<paramref name="candidateDir"/> 是刚截的一份。</summary>
    public static OracleCompareResult CompareDirectories(string goldenDir, string candidateDir)
        => Compare(OracleGolden.Load(goldenDir), OracleGolden.Load(candidateDir));
}
