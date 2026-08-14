namespace FairyNext.Numerics;

/// <summary>
/// 等值切断的比较语义（裁决 13，docs/design/02-所有权仲裁表.md）：float/double 按位相等，
/// 且 NaN 一律视为相等（不分位型/payload）。
/// 明文推论（有意语义）：+0 与 -0 位型不同（0x00000000 vs 0x80000000）⇒ 判不等 ⇒
/// 写 -0 覆盖 +0 会置一次脏——宁可多脏一次，也不许 IEEE 的 NaN != NaN 把含 NaN 的属性
/// 钉成永脏（每帧入队空转）。
/// 所有 Mark 入口（setter codegen、Resolve 出口、timeline 采样）写前必须走这里，禁用 ==。
/// </summary>
public static class BitEquals
{
    public static bool Eq(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b)
        || (float.IsNaN(a) && float.IsNaN(b));

    public static bool Eq(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b)
        || (double.IsNaN(a) && double.IsNaN(b));
}
