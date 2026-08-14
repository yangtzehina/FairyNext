namespace FairyNext.Numerics;

/// <summary>
/// 4D 向量 shim。主要用途是 QuadInstance 的 rect/uvA/uvB 四元打包（QuadReassembler 调用面），
/// API 形状对齐 UnityEngine.Vector4。==/!= 为精确 float 相等（同 Vector2 的分歧说明）。
/// </summary>
public struct Vector4 : IEquatable<Vector4>
{
    public float x, y, z, w;

    public Vector4(float x, float y, float z, float w)
    {
        this.x = x; this.y = y; this.z = z; this.w = w;
    }

    public static Vector4 zero => new Vector4(0f, 0f, 0f, 0f);

    public static Vector4 operator +(Vector4 a, Vector4 b) => new Vector4(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
    public static Vector4 operator -(Vector4 a, Vector4 b) => new Vector4(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
    public static Vector4 operator -(Vector4 a) => new Vector4(-a.x, -a.y, -a.z, -a.w);
    public static Vector4 operator *(Vector4 a, float s) => new Vector4(a.x * s, a.y * s, a.z * s, a.w * s);
    public static Vector4 operator *(float s, Vector4 a) => new Vector4(a.x * s, a.y * s, a.z * s, a.w * s);
    public static Vector4 operator /(Vector4 a, float s) => new Vector4(a.x / s, a.y / s, a.z / s, a.w / s);
    public static bool operator ==(Vector4 a, Vector4 b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
    public static bool operator !=(Vector4 a, Vector4 b) => !(a == b);

    public static Vector4 Min(Vector4 a, Vector4 b) =>
        new Vector4(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y), MathF.Min(a.z, b.z), MathF.Min(a.w, b.w));
    public static Vector4 Max(Vector4 a, Vector4 b) =>
        new Vector4(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z), MathF.Max(a.w, b.w));

    /// <summary>t 截断到 [0,1]（对齐 Unity Lerp 语义）。</summary>
    public static Vector4 Lerp(Vector4 a, Vector4 b, float t)
    {
        t = t < 0f ? 0f : t > 1f ? 1f : t;
        return new Vector4(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t,
                           a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t);
    }

    public readonly bool Equals(Vector4 other) => x == other.x && y == other.y && z == other.z && w == other.w;
    public readonly override bool Equals(object? obj) => obj is Vector4 v && Equals(v);
    public readonly override int GetHashCode() => HashCode.Combine(x, y, z, w);
    public readonly override string ToString() => $"({x}, {y}, {z}, {w})";
}
