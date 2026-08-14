namespace FairyNext.Numerics;

/// <summary>
/// 2×3 仿射变换——UI 平面变换的法定形态。树内一切局部/世界 2D 变换用它，
/// 不背 4×4 的 10 个恒零/恒一字段。映射：
///   x' = m00·x + m01·y + tx
///   y' = m10·x + m11·y + ty
/// 3D 顶点回投路径（QuadReassembler 的 toLocal 含 z 列）用旁边的最小 Matrix4x4，
/// 二选一决策见该文件头。
/// </summary>
public struct Affine2D
{
    public float m00, m01, m10, m11, tx, ty;

    public Affine2D(float m00, float m01, float m10, float m11, float tx, float ty)
    {
        this.m00 = m00; this.m01 = m01;
        this.m10 = m10; this.m11 = m11;
        this.tx = tx; this.ty = ty;
    }

    public static Affine2D Identity => new Affine2D(1f, 0f, 0f, 1f, 0f, 0f);

    /// <summary>
    /// 复合 a ⊗ b：先施 b、再施 a（矩阵积 A·B），
    /// 即 Compose(a, b).TransformPoint(p) == a.TransformPoint(b.TransformPoint(p))。
    /// 典型用法：child.world = Compose(parent.world, child.local)。
    /// </summary>
    public static Affine2D Compose(in Affine2D a, in Affine2D b) => new Affine2D(
        a.m00 * b.m00 + a.m01 * b.m10,
        a.m00 * b.m01 + a.m01 * b.m11,
        a.m10 * b.m00 + a.m11 * b.m10,
        a.m10 * b.m01 + a.m11 * b.m11,
        a.m00 * b.tx + a.m01 * b.ty + a.tx,
        a.m10 * b.tx + a.m11 * b.ty + a.ty);

    public readonly Vector2 TransformPoint(Vector2 p) =>
        new Vector2(m00 * p.x + m01 * p.y + tx, m10 * p.x + m11 * p.y + ty);

    /// <summary>
    /// 求逆。行列式为 0（典型：scale 含 0 的隐藏节点）返回 false 且 inverse 全零——
    /// 调用方必须显式处理不可逆分支（命中测试对不可见节点本就该早退）。
    /// </summary>
    public readonly bool TryInvert(out Affine2D inverse)
    {
        float det = m00 * m11 - m01 * m10;
        if (det == 0f)
        {
            inverse = default;
            return false;
        }
        float r = 1f / det;
        float i00 = m11 * r, i01 = -m01 * r;
        float i10 = -m10 * r, i11 = m00 * r;
        inverse = new Affine2D(i00, i01, i10, i11,
            -(i00 * tx + i01 * ty),
            -(i10 * tx + i11 * ty));
        return true;
    }

    /// <summary>
    /// TRS 构造：M = T·R·S（对点的作用序：先缩放、再旋转、后平移）。
    /// 正角方向 = 从 +x 轴转向 +y 轴——UI 坐标系 y 向下时视觉为顺时针。
    /// </summary>
    public static Affine2D TRS(Vector2 translation, float rotationRadians, Vector2 scale)
    {
        float c = MathF.Cos(rotationRadians);
        float s = MathF.Sin(rotationRadians);
        return new Affine2D(
            c * scale.x, -s * scale.y,
            s * scale.x, c * scale.y,
            translation.x, translation.y);
    }

    public readonly override string ToString() => $"[{m00}, {m01}, {tx}; {m10}, {m11}, {ty}]";
}
