namespace FairyNext.Numerics;

/// <summary>
/// 3D 向量 shim（最小面）：只为 QuadReassembler 的顶点回投路径存在——顶点表是 Vector3、
/// 经 Matrix4x4.MultiplyPoint3x4 后隐式落回 Vector2。与 Vector2 的隐式互转对齐 Unity
/// （去 z / 补 z=0）。不按通用数学库扩 API。
/// </summary>
public struct Vector3 : IEquatable<Vector3>
{
    public float x, y, z;

    public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    public Vector3(float x, float y) { this.x = x; this.y = y; z = 0f; }

    public static Vector3 zero => new Vector3(0f, 0f, 0f);

    public static implicit operator Vector2(Vector3 v) => new Vector2(v.x, v.y);
    public static implicit operator Vector3(Vector2 v) => new Vector3(v.x, v.y, 0f);

    public readonly bool Equals(Vector3 other) => x == other.x && y == other.y && z == other.z;
    public readonly override bool Equals(object? obj) => obj is Vector3 v && Equals(v);
    public readonly override int GetHashCode() => HashCode.Combine(x, y, z);
    public readonly override string ToString() => $"({x}, {y}, {z})";
}
