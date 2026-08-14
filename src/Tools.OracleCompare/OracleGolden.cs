using System.Globalization;

namespace FairyNext.Tools.OracleCompare;

/// <summary>
/// 容差参数。**不在这里定默认值当事实源**：唯一事实源是场景描述符 tools/oracle/scenes/&lt;id&gt;.json 的
/// tolerance 块，截取时抄进 meta.json，比对时从 meta 读回。<see cref="Baseline"/> 只是「新场景从哪个数
/// 起步」的建议值，并由单测钉住它与首个入库 golden 一致——两处一漂移，测试就红。
/// </summary>
public sealed class OracleTolerance
{
    /// <summary>x/y/width/height 的逐字段容差，像素。</summary>
    public double LayoutEpsPx { get; }

    /// <summary>scaleX/scaleY/alpha 这类无量纲字段的容差。</summary>
    public double LayoutEpsUnitless { get; }

    /// <summary>rotation 的容差，度。</summary>
    public double LayoutEpsDegrees { get; }

    /// <summary>单像素单通道 |Δ| 不超过它就不算「差异像素」。</summary>
    public int PixelChannelDelta { get; }

    /// <summary>任一像素单通道 |Δ| 超过它即直接判失败——少量但离谱的差异不该被占比阈值稀释掉。</summary>
    public int PixelMaxChannelDelta { get; }

    /// <summary>差异像素占比上限。</summary>
    public double PixelDiffRatio { get; }

    /// <summary>热点框的网格边长（像素）。</summary>
    public int HotspotCell { get; }

    public OracleTolerance(double layoutEpsPx, double layoutEpsUnitless, double layoutEpsDegrees,
                           int pixelChannelDelta, int pixelMaxChannelDelta, double pixelDiffRatio, int hotspotCell)
    {
        LayoutEpsPx = layoutEpsPx;
        LayoutEpsUnitless = layoutEpsUnitless;
        LayoutEpsDegrees = layoutEpsDegrees;
        PixelChannelDelta = pixelChannelDelta;
        PixelMaxChannelDelta = pixelMaxChannelDelta;
        PixelDiffRatio = pixelDiffRatio;
        HotspotCell = hotspotCell;
    }

    /// <summary>新场景描述符的起步值；不是比对时的回退值（比对永远读 meta）。</summary>
    public static OracleTolerance Baseline { get; } = new OracleTolerance(0.5, 0.001, 0.01, 2, 64, 0.002, 16);

    public static OracleTolerance FromMeta(JsonValue tolerance) => new OracleTolerance(
        tolerance.RequireNumber("layoutEpsPx"),
        tolerance.RequireNumber("layoutEpsUnitless"),
        tolerance.RequireNumber("layoutEpsDegrees"),
        tolerance.RequireInt("pixelChannelDelta"),
        tolerance.RequireInt("pixelMaxChannelDelta"),
        tolerance.RequireNumber("pixelDiffRatio"),
        tolerance.RequireInt("hotspotCell"));

    public bool SameAs(OracleTolerance o) =>
        LayoutEpsPx.Equals(o.LayoutEpsPx) && LayoutEpsUnitless.Equals(o.LayoutEpsUnitless) &&
        LayoutEpsDegrees.Equals(o.LayoutEpsDegrees) && PixelChannelDelta == o.PixelChannelDelta &&
        PixelMaxChannelDelta == o.PixelMaxChannelDelta && PixelDiffRatio.Equals(o.PixelDiffRatio) &&
        HotspotCell == o.HotspotCell;
}

/// <summary>一个布局节点的数值签名。字段集与 tools/oracle/lib/declarations.cs 的 DumpLayout 一一对应。</summary>
public sealed class LayoutNode
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double ScaleX { get; set; }
    public double ScaleY { get; set; }
    public double Rotation { get; set; }
    public double Alpha { get; set; }
    public bool Visible { get; set; }
}

/// <summary>layout.json 的解析结果。</summary>
public sealed class LayoutSnapshot
{
    public string Scene { get; }
    public int StageWidth { get; }
    public int StageHeight { get; }
    public double UnitsPerPixel { get; }
    public List<LayoutNode> Nodes { get; }

    private LayoutSnapshot(string scene, int w, int h, double upp, List<LayoutNode> nodes)
    {
        Scene = scene;
        StageWidth = w;
        StageHeight = h;
        UnitsPerPixel = upp;
        Nodes = nodes;
    }

    public static LayoutSnapshot Parse(string json)
    {
        JsonValue root = JsonValue.Parse(json);
        JsonValue stage = root.RequireObject("stage");
        var nodes = new List<LayoutNode>();
        foreach (JsonValue n in root.RequireArray("nodes"))
        {
            nodes.Add(new LayoutNode
            {
                Path = n.RequireString("path"),
                Name = n.RequireString("name"),
                Type = n.RequireString("type"),
                X = n.RequireNumber("x"),
                Y = n.RequireNumber("y"),
                Width = n.RequireNumber("width"),
                Height = n.RequireNumber("height"),
                ScaleX = n.RequireNumber("scaleX"),
                ScaleY = n.RequireNumber("scaleY"),
                Rotation = n.RequireNumber("rotation"),
                Alpha = n.RequireNumber("alpha"),
                Visible = n.RequireBool("visible"),
            });
        }
        return new LayoutSnapshot(root.RequireString("scene"), stage.RequireInt("width"),
                                  stage.RequireInt("height"), stage.RequireNumber("unitsPerPixel"), nodes);
    }

    public static LayoutSnapshot Load(string path) => Parse(File.ReadAllText(path));
}

/// <summary>
/// 入库的一份 golden：frame.png + layout.json + meta.json。
///
/// 装载即体检：meta 字段缺一即抛（不许「按默认值继续比」），且 meta.image 的尺寸必须与 PNG 实际尺寸一致
/// ——三件套不同步是重生成流程被打断的典型痕迹，越早炸越好定位。
/// </summary>
public sealed class OracleGolden
{
    public const string PngFileName = "frame.png";
    public const string LayoutFileName = "layout.json";
    public const string MetaFileName = "meta.json";

    public string Directory { get; }
    public string Scene { get; }
    public string OracleSha { get; }
    public string UnityVersion { get; }
    public string ColorSpace { get; }
    public string GraphicsDevice { get; }
    public string Driver { get; }
    public string CapturedUtc { get; }
    public OracleTolerance Tolerance { get; }
    public LayoutSnapshot Layout { get; }
    public PngImage Image { get; }

    private OracleGolden(string dir, JsonValue meta, LayoutSnapshot layout, PngImage image)
    {
        Directory = dir;
        Scene = meta.RequireString("scene");
        OracleSha = meta.RequireString("oracleSha");
        UnityVersion = meta.RequireString("unityVersion");
        ColorSpace = meta.RequireString("colorSpace");
        GraphicsDevice = meta.RequireString("graphicsDevice");
        Driver = meta.RequireString("driver");
        CapturedUtc = meta.RequireString("capturedUtc");
        Tolerance = OracleTolerance.FromMeta(meta.RequireObject("tolerance"));
        Layout = layout;
        Image = image;

        JsonValue img = meta.RequireObject("image");
        int w = img.RequireInt("width"), h = img.RequireInt("height");
        if (w != image.Width || h != image.Height)
            throw new FormatException($"golden 不同步：meta.image={w}x{h}，frame.png={image.Width}x{image.Height}");
        if (Scene != layout.Scene)
            throw new FormatException($"golden 不同步：meta.scene={Scene}，layout.scene={layout.Scene}");
        if (layout.StageWidth != image.Width || layout.StageHeight != image.Height)
            throw new FormatException($"golden 不同步：layout.stage={layout.StageWidth}x{layout.StageHeight}，" +
                                      $"frame.png={image.Width}x{image.Height}");
    }

    public static OracleGolden Load(string directory)
    {
        string metaPath = Path.Combine(directory, MetaFileName);
        string layoutPath = Path.Combine(directory, LayoutFileName);
        string pngPath = Path.Combine(directory, PngFileName);
        foreach (string f in new[] { metaPath, layoutPath, pngPath })
            if (!File.Exists(f)) throw new FileNotFoundException($"golden 缺文件：{f}", f);

        return new OracleGolden(directory, JsonValue.Parse(File.ReadAllText(metaPath)),
                                LayoutSnapshot.Load(layoutPath), PngImage.Load(pngPath));
    }

    /// <summary>
    /// 两份 golden 是否**可比**。oracle SHA / Unity 版本 / 色彩空间 / 图形 API 任一不同，
    /// 像素差异就不再归因于被测实现——这时该报「基线过期」，不该报「实现回归」。
    /// </summary>
    public List<string> AdmissibilityAgainst(OracleGolden other)
    {
        var reasons = new List<string>();
        void Cmp(string field, string a, string b)
        {
            if (!string.Equals(a, b, StringComparison.Ordinal)) reasons.Add($"{field}: golden={a} candidate={b}");
        }
        Cmp("scene", Scene, other.Scene);
        Cmp("oracleSha", OracleSha, other.OracleSha);
        Cmp("unityVersion", UnityVersion, other.UnityVersion);
        Cmp("colorSpace", ColorSpace, other.ColorSpace);
        Cmp("graphicsDevice", GraphicsDevice, other.GraphicsDevice);
        if (!Tolerance.SameAs(other.Tolerance))
            reasons.Add("tolerance: 两侧 meta 的容差块不一致（场景描述符被改过？）");
        return reasons;
    }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0} @ oracle {1} · Unity {2} · {3} · {4}x{5} · {6} 节点",
            Scene, OracleSha, UnityVersion, GraphicsDevice, Image.Width, Image.Height, Layout.Nodes.Count);
}
