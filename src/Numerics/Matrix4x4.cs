namespace FairyNext.Numerics;

/// <summary>
/// 4×4 矩阵 shim——刻意最小：16 个字段、Identity、MultiplyPoint3x4，不再长大。
///
/// 二选一决策（M1-02 工作包要求注明）：QuadReassembler 的 toLocal 来自引擎侧
/// worldToLocal，含 z 列——网格顶点是 Vector3，烘焙输入可能带非零 z；改走 Affine2D
/// 必须重写其数学并丢弃 z 信息，对拍 oracle 时引入数学改写差异。故保留最小
/// Matrix4x4 使该文件逐行直搬；纯 2D 树内变换一律用 Affine2D。
///
/// 字段命名 m行列（m01 = 第 0 行第 1 列），MultiplyPoint3x4 忽略透视行（视 w 恒为 1），
/// 均与 UnityEngine.Matrix4x4 一致。
/// </summary>
public struct Matrix4x4
{
    public float m00, m01, m02, m03;
    public float m10, m11, m12, m13;
    public float m20, m21, m22, m23;
    public float m30, m31, m32, m33;

    public static Matrix4x4 Identity => new Matrix4x4 { m00 = 1f, m11 = 1f, m22 = 1f, m33 = 1f };

    public readonly Vector3 MultiplyPoint3x4(Vector3 p) => new Vector3(
        m00 * p.x + m01 * p.y + m02 * p.z + m03,
        m10 * p.x + m11 * p.y + m12 * p.z + m13,
        m20 * p.x + m21 * p.y + m22 * p.z + m23);
}
