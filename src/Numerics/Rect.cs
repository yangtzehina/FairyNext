namespace FairyNext.Numerics;

/// <summary>
/// 轴对齐矩形 shim（x/y/width/height）。API 形状对齐 UnityEngine.Rect 的被调面
/// （PixelHitTest.HitTest 等）。语义明文：
///  - Contains 为半开区间 [min, max)——与 Unity 一致，贴 max 边的点不算命中；
///  - Intersects 语义 = Unity Rect.Overlaps：需真重叠，贴边（xMax == other.xMin）不算；
///  - xMin/yMin 的 setter 固定对边（挪 min 边改 width/height），与 Unity 一致。
/// </summary>
public struct Rect : IEquatable<Rect>
{
    public float x, y, width, height;

    public Rect(float x, float y, float width, float height)
    {
        this.x = x; this.y = y; this.width = width; this.height = height;
    }

    public float xMin { readonly get => x; set { float m = x + width; x = value; width = m - value; } }
    public float yMin { readonly get => y; set { float m = y + height; y = value; height = m - value; } }
    public float xMax { readonly get => x + width; set => width = value - x; }
    public float yMax { readonly get => y + height; set => height = value - y; }

    public readonly bool Contains(Vector2 point) =>
        point.x >= x && point.x < x + width && point.y >= y && point.y < y + height;

    public readonly bool Intersects(Rect other) =>
        other.xMin < xMax && other.xMax > xMin && other.yMin < yMax && other.yMax > yMin;

    /// <summary>包络并集（Unity Rect 无此方法；供包围盒累积用）。</summary>
    public static Rect Union(Rect a, Rect b)
    {
        float minX = MathF.Min(a.xMin, b.xMin);
        float minY = MathF.Min(a.yMin, b.yMin);
        return new Rect(minX, minY,
            MathF.Max(a.xMax, b.xMax) - minX,
            MathF.Max(a.yMax, b.yMax) - minY);
    }

    public static Rect MinMaxRect(float xmin, float ymin, float xmax, float ymax) =>
        new Rect(xmin, ymin, xmax - xmin, ymax - ymin);

    public static bool operator ==(Rect a, Rect b) =>
        a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height;
    public static bool operator !=(Rect a, Rect b) => !(a == b);

    public readonly bool Equals(Rect other) => this == other;
    public readonly override bool Equals(object? obj) => obj is Rect r && Equals(r);
    public readonly override int GetHashCode() => HashCode.Combine(x, y, width, height);
    public readonly override string ToString() => $"(x:{x}, y:{y}, w:{width}, h:{height})";
}
