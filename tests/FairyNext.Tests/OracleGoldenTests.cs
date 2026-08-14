using System.Globalization;
using System.Text;
using FairyNext.AbiGen;
using FairyNext.Tools.OracleCompare;

namespace FairyNext.Tests;

/// <summary>
/// oracle 对拍基建（M1-04）——验证平面不变量 14 的**管线**部分。
///
/// 这里不跑 Unity。基线由 tools/oracle/capture.sh 在本机驱动钉死的 fork 生成后入库，
/// CI 只消费产物。因此本套用例守的是三件事：
///   ① 入库的 golden 可读、三件套自洽（meta / layout / png 互相对得上）；
///   ② 比对器管线通——golden 对 golden 全等（这条一旦红，说明解码或比对本身坏了，与实现无关）；
///   ③ 容差与结构判定的**边界**行为如设计：刚好压线过、越线炸，且失败带定位。
/// 「与被测实现一致」的那一步要等 M1 有了自家渲染管线才接上，不属于本包。
/// </summary>
public static partial class Program
{
    private const string GoldenRelative = "tests/goldens/oracle/scene-001";

    private static void OracleGoldenSuite()
    {
        string? root = RepoRoot.Find(AppContext.BaseDirectory);
        if (root == null) { Check("oracle golden: 定位仓库根", false); return; }

        string dir = RepoRoot.ToAbsolute(root, GoldenRelative);
        OracleGolden golden;
        try
        {
            golden = OracleGolden.Load(dir);
            Check($"oracle golden: 基线可读（{GoldenRelative}）", true);
            Console.WriteLine($"     {golden}");
        }
        catch (Exception ex)
        {
            Check($"oracle golden: 基线可读（{GoldenRelative}）", false);
            Console.WriteLine($"     {ex.Message}");
            return;
        }

        GoldenSelfCompare(dir);
        GoldenPinnedToOracleLock(root, golden);
        DecoderSanity(golden);
        LayoutToleranceBoundary(golden);
        LayoutStructuralDivergence(golden);
        PixelThresholds(golden);
        CandidateCompare(golden);
    }

    /// <summary>
    /// 重生成基线时的实差比对：先把新截的一份放到别处，再
    /// <c>FAIRYNEXT_ORACLE_CANDIDATE=&lt;dir&gt; dotnet run --project tests/FairyNext.Tests</c>。
    /// 未设环境变量时本条不产生用例——CI 不该因为本机没截图而多一条永远缺席的门。
    /// </summary>
    private static void CandidateCompare(OracleGolden golden)
    {
        string? candidateDir = Environment.GetEnvironmentVariable("FAIRYNEXT_ORACLE_CANDIDATE");
        if (string.IsNullOrEmpty(candidateDir)) return;

        OracleCompareResult r;
        try { r = OracleComparer.Compare(golden, OracleGolden.Load(candidateDir)); }
        catch (Exception ex)
        {
            Check($"oracle 候选比对: 装载 {candidateDir}", false);
            Console.WriteLine($"     {ex.Message}");
            return;
        }
        Check($"oracle 候选比对: {candidateDir} 与入库基线一致", r.Pass);
        Console.WriteLine(Indent(r.Report()));
    }

    // ---- ② 管线自证：golden 对 golden 必须全等 ------------------------------

    private static void GoldenSelfCompare(string dir)
    {
        // 刻意装载两次而不是复用同一实例：这样 PNG 解码、JSON 解析、容差读取各跑两遍，
        // 「比对器把同一个对象和自己比」的假通过被排除。
        OracleCompareResult r = OracleComparer.CompareDirectories(dir, dir);
        Check("oracle golden: 自比对全等（可比性 ∧ 布局 ∧ 像素）", r.Pass);
        if (!r.Pass) Console.WriteLine(Indent(r.Report()));
        Check("oracle golden: 自比对零差异像素", r.Pixels.DiffPixels == 0 && r.Pixels.MaxChannelDelta == 0);
    }

    private static void GoldenPinnedToOracleLock(string root, OracleGolden golden)
    {
        string lockText = File.ReadAllText(RepoRoot.ToAbsolute(root, "oracle.lock"));
        string lockSha = JsonValue.Parse(lockText).RequireString("sha");
        string lockUnity = JsonValue.Parse(lockText).RequireString("unityVersion");

        // 基线上写的采集条件必须就是 oracle.lock 钉的那一个。fork 换 SHA 而 golden 没重生成，
        // 像素门从此比的是历史，且没人会发现——所以这条门比像素本身更早。
        Check($"oracle golden: meta.oracleSha == oracle.lock（{lockSha}）",
            string.Equals(golden.OracleSha, lockSha, StringComparison.Ordinal));
        Check($"oracle golden: meta.unityVersion == oracle.lock（{lockUnity}）",
            string.Equals(golden.UnityVersion, lockUnity, StringComparison.Ordinal));
        Check("oracle golden: meta.tolerance == OracleTolerance.Baseline（容差事实源未漂移）",
            golden.Tolerance.SameAs(OracleTolerance.Baseline));
    }

    // ---- ① 解码自证 --------------------------------------------------------

    private static void DecoderSanity(OracleGolden golden)
    {
        PngImage img = golden.Image;
        // scene-001 的背景 = (0.09,0.09,0.11,1) 在 Gamma 色彩空间下 = (23,23,28,255)；
        // 图内 (100,100) 落在 solid 矩形里 = (0.20,0.55,0.90) = (51,140,230,255)。
        // 写死这两个值是有意的：PNG 解码器坏掉的典型形态是「整体偏移一行 / 通道错位」，
        // 只断言尺寸抓不到，断言两个已知颜色能抓到。
        Check("oracle golden: PNG 尺寸 = 640x360", img.Width == 640 && img.Height == 360);
        Check("oracle golden: PNG 解码 — 左上角 = 背景色 (23,23,28,255)", PixelIs(img, 0, 0, 23, 23, 28, 255));
        Check("oracle golden: PNG 解码 — (100,100) 落在纯色矩形内 (51,140,230,255)",
            PixelIs(img, 100, 100, 51, 140, 230, 255));

        // 布局侧：两个节点、类型与几何就是场景描述符写的那样。
        Check("oracle golden: layout 两个节点（GGraph + GImage 九宫格）",
            golden.Layout.Nodes.Count == 2 &&
            golden.Layout.Nodes[0].Type == "GGraph" && golden.Layout.Nodes[1].Type == "GImage");
        LayoutNode nine = golden.Layout.Nodes[1];
        Check("oracle golden: 九宫格节点几何 = (40,200,420,110)",
            nine.X == 40 && nine.Y == 200 && nine.Width == 420 && nine.Height == 110);
    }

    private static bool PixelIs(PngImage img, int x, int y, int r, int g, int b, int a)
    {
        int i = (y * img.Width + x) * 4;
        return img.Rgba[i] == r && img.Rgba[i + 1] == g && img.Rgba[i + 2] == b && img.Rgba[i + 3] == a;
    }

    // ---- ③ 容差与结构判定的边界 --------------------------------------------

    private static void LayoutToleranceBoundary(OracleGolden golden)
    {
        OracleTolerance tol = golden.Tolerance;   // layoutEpsPx = 0.5

        LayoutSnapshot under = Mutate(golden.Layout, n => { if (n.Path == "/solid") n.X += 0.4; });
        Check("oracle 布局容差: x 偏 0.4px（< 0.5）判过", LayoutComparer.Compare(golden.Layout, under, tol).Pass);

        LayoutSnapshot over = Mutate(golden.Layout, n => { if (n.Path == "/solid") n.X += 0.6; });
        LayoutDiff d = LayoutComparer.Compare(golden.Layout, over, tol);
        Check("oracle 布局容差: x 偏 0.6px（> 0.5）判失败", !d.Pass);
        Check("oracle 布局容差: 失败定位到 /solid.x 且只此一条",
            d.FieldFailures.Count == 1 && d.Worst != null &&
            d.Worst.Path == "/solid" && d.Worst.Field == "x");

        // 无量纲字段走自己的容差（0.001），不该被 0.5px 那把尺子量。
        LayoutSnapshot alpha = Mutate(golden.Layout, n => { if (n.Path == "/nine") n.Alpha -= 0.01; });
        LayoutDiff da = LayoutComparer.Compare(golden.Layout, alpha, tol);
        Check("oracle 布局容差: alpha 偏 0.01 走无量纲容差（0.001）判失败",
            !da.Pass && da.FieldFailures.Count == 1 && da.FieldFailures[0].Field == "alpha");
    }

    private static void LayoutStructuralDivergence(OracleGolden golden)
    {
        OracleTolerance tol = golden.Tolerance;

        LayoutSnapshot dropped = Rebuild(golden.Layout, nodes => nodes.RemoveAt(1));
        LayoutDiff d1 = LayoutComparer.Compare(golden.Layout, dropped, tol);
        Check("oracle 布局结构: 少一个节点被抓且报出 path",
            !d1.Pass && d1.MissingPaths.Count == 1 && d1.MissingPaths[0] == "/nine");

        LayoutSnapshot retyped = Mutate(golden.Layout, n => { if (n.Path == "/nine") n.Type = "GLoader"; });
        LayoutDiff d2 = LayoutComparer.Compare(golden.Layout, retyped, tol);
        Check("oracle 布局结构: 节点类型变化被抓（数值全同也算回归）",
            !d2.Pass && d2.FieldFailures.Count == 0 && d2.StructuralFailures.Count == 1);

        LayoutSnapshot reordered = Rebuild(golden.Layout, nodes => nodes.Reverse());
        LayoutDiff d3 = LayoutComparer.Compare(golden.Layout, reordered, tol);
        Check("oracle 布局结构: 顺序颠倒被抓（绘制序也是被比对物）",
            !d3.Pass && d3.MissingPaths.Count == 0 && d3.ExtraPaths.Count == 0 &&
            d3.StructuralFailures.Exists(s => s.Contains("节点顺序")));
    }

    private static void PixelThresholds(OracleGolden golden)
    {
        OracleTolerance tol = golden.Tolerance;   // channelDelta=2, maxChannelDelta=64, ratio=0.002
        PngImage baseline = golden.Image;

        // 全图每通道 +2：在单像素阈值内 ⇒ 零差异像素 ⇒ 判过。GPU 最低位抖动就是这个形状。
        PngImage jitter = Shift(baseline, 2);
        PixelDiff p1 = PixelComparer.Compare(baseline, jitter, tol);
        Check("oracle 像素: 全图通道 Δ=2（= 阈值）判过", p1.Pass && p1.DiffPixels == 0);

        // 全图每通道 +3：越单像素阈值，占比 100% ⇒ 判失败。
        PngImage broad = Shift(baseline, 3);
        PixelDiff p2 = PixelComparer.Compare(baseline, broad, tol);
        Check("oracle 像素: 全图通道 Δ=3 判失败（占比越线）",
            !p2.Pass && p2.DiffRatio > 0.99);

        // 一个像素画歪 100：占比只有 1/230400，靠占比阈值永远抓不到——硬线就是为这条存在的。
        byte[] buf = (byte[])baseline.Rgba.Clone();
        int at = (77 * baseline.Width + 300) * 4;
        buf[at] = (byte)Math.Min(255, buf[at] + 100);
        PngImage spike = PngImage.FromRgba(baseline.Width, baseline.Height, buf);
        PixelDiff p3 = PixelComparer.Compare(baseline, spike, tol);
        Check("oracle 像素: 单像素通道 Δ=100 被硬线抓住（占比阈值抓不到）",
            !p3.Pass && p3.DiffPixels == 1 && p3.MaxChannelDelta == 100 &&
            p3.MaxDeltaX == 300 && p3.MaxDeltaY == 77 &&
            p3.DiffRatio <= tol.PixelDiffRatio);
        Check("oracle 像素: 热点框框住差异点（16px 网格 → [288,64 16x16]）",
            p3.Hotspots.Count == 1 && p3.Hotspots[0].X == 288 && p3.Hotspots[0].Y == 64 &&
            p3.Hotspots[0].Width == 16 && p3.Hotspots[0].Height == 16 &&
            p3.Hotspots[0].DiffPixels == 1);

        // 两块相隔很远的差异 → 两个热点框，不合并成一个覆盖半张图的巨框。
        byte[] two = (byte[])baseline.Rgba.Clone();
        Spot(two, baseline.Width, 20, 20);
        Spot(two, baseline.Width, 500, 300);
        PixelDiff p4 = PixelComparer.Compare(baseline, PngImage.FromRgba(baseline.Width, baseline.Height, two), tol);
        Check("oracle 像素: 两处远隔差异 → 两个热点框（不合并）", p4.Hotspots.Count == 2);

        // 尺寸不同不是「差异很大」，是「不可比」——必须立刻响亮失败，而不是逐像素比到崩。
        PixelDiff p5 = PixelComparer.Compare(baseline, PngImage.FromRgba(4, 4, new byte[4 * 4 * 4]), tol);
        Check("oracle 像素: 尺寸不同直接判失败并说明", !p5.Pass && p5.Failures.Count == 1);
    }

    private static void Spot(byte[] rgba, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        rgba[i + 1] = (byte)Math.Min(255, rgba[i + 1] + 40);
    }

    private static PngImage Shift(PngImage src, int delta)
    {
        byte[] buf = new byte[src.Rgba.Length];
        for (int i = 0; i < buf.Length; i++) buf[i] = (byte)Math.Min(255, src.Rgba[i] + delta);
        return PngImage.FromRgba(src.Width, src.Height, buf);
    }

    // ---- 扰动工具：改对象 → 写回 JSON → 重新解析 ---------------------------
    // 直接 new 一个 LayoutSnapshot 会绕开解析器；这里走一圈 JSON，让每条用例顺带压一次解析路径。

    private static LayoutSnapshot Mutate(LayoutSnapshot src, Action<LayoutNode> edit)
        => Rebuild(src, nodes => { foreach (LayoutNode n in nodes) edit(n); });

    private static LayoutSnapshot Rebuild(LayoutSnapshot src, Action<List<LayoutNode>> edit)
    {
        var nodes = new List<LayoutNode>();
        foreach (LayoutNode n in src.Nodes)
            nodes.Add(new LayoutNode
            {
                Path = n.Path, Name = n.Name, Type = n.Type,
                X = n.X, Y = n.Y, Width = n.Width, Height = n.Height,
                ScaleX = n.ScaleX, ScaleY = n.ScaleY, Rotation = n.Rotation,
                Alpha = n.Alpha, Visible = n.Visible,
            });
        edit(nodes);
        return LayoutSnapshot.Parse(ToJson(src, nodes));
    }

    private static string ToJson(LayoutSnapshot src, List<LayoutNode> nodes)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"scene\": \"").Append(src.Scene).Append("\",\n");
        sb.Append("  \"stage\": { \"width\": ").Append(src.StageWidth)
          .Append(", \"height\": ").Append(src.StageHeight)
          .Append(", \"unitsPerPixel\": ").Append(N(src.UnitsPerPixel)).Append(" },\n");
        sb.Append("  \"nodes\": [\n");
        for (int i = 0; i < nodes.Count; i++)
        {
            LayoutNode n = nodes[i];
            sb.Append("    { \"path\": \"").Append(n.Path).Append("\", \"name\": \"").Append(n.Name)
              .Append("\", \"type\": \"").Append(n.Type)
              .Append("\", \"x\": ").Append(N(n.X)).Append(", \"y\": ").Append(N(n.Y))
              .Append(", \"width\": ").Append(N(n.Width)).Append(", \"height\": ").Append(N(n.Height))
              .Append(", \"scaleX\": ").Append(N(n.ScaleX)).Append(", \"scaleY\": ").Append(N(n.ScaleY))
              .Append(", \"rotation\": ").Append(N(n.Rotation)).Append(", \"alpha\": ").Append(N(n.Alpha))
              .Append(", \"visible\": ").Append(n.Visible ? "true" : "false").Append(" }")
              .Append(i == nodes.Count - 1 ? "\n" : ",\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static string N(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);

    private static string Indent(string text) => "     " + text.Replace("\n", "\n     ");
}
