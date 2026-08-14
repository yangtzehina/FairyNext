namespace FairyNext.Numerics;

/// <summary>
/// 2D 向量 shim。字段名/API 形状对齐 UnityEngine.Vector2，使 fork 移植件（QuadReassembler、
/// PixelHitTest）逐行落地；非移植代码。
/// 与 Unity 的刻意分歧：==/!= 为精确 float 相等（Unity 为 1e-5 近似比较）——移植件若依赖
/// 近似 ==，移植时必须显式改写并在改写处注释。
/// </summary>
public struct Vector2 : IEquatable<Vector2>
{
    public float x, y;

    public Vector2(float x, float y) { this.x = x; this.y = y; }

    public static Vector2 zero => new Vector2(0f, 0f);
    public static Vector2 one => new Vector2(1f, 1f);

    public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
    public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
    public static Vector2 operator -(Vector2 a) => new Vector2(-a.x, -a.y);
    public static Vector2 operator *(Vector2 a, float s) => new Vector2(a.x * s, a.y * s);
    public static Vector2 operator *(float s, Vector2 a) => new Vector2(a.x * s, a.y * s);
    public static Vector2 operator /(Vector2 a, float s) => new Vector2(a.x / s, a.y / s);
    public static bool operator ==(Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;
    public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);

    public static Vector2 Min(Vector2 a, Vector2 b) => new Vector2(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y));
    public static Vector2 Max(Vector2 a, Vector2 b) => new Vector2(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y));

    /// <summary>t 截断到 [0,1]（对齐 Unity Lerp 语义；不截断版本按需另加）。</summary>
    public static Vector2 Lerp(Vector2 a, Vector2 b, float t)
    {
        t = t < 0f ? 0f : t > 1f ? 1f : t;
        return new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
    }

    public readonly bool Equals(Vector2 other) => x == other.x && y == other.y;
    public readonly override bool Equals(object? obj) => obj is Vector2 v && Equals(v);
    public readonly override int GetHashCode() => HashCode.Combine(x, y);
    public readonly override string ToString() => $"({x}, {y})";
}
