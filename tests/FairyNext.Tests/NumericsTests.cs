using FairyNext.Numerics;

namespace FairyNext.Tests;

/// <summary>
/// M1-02 数学 shim 库用例（Program 的 partial 分片，沿用 Check(name, ok) 模式）。
/// </summary>
public static partial class Program
{
    private static void NumericsSuite()
    {
        AffineComposeSemantics();
        AffineTransformPointKnown();
        AffineInvertRoundtrip();
        RectSemantics();
        FnvKnownVectors();
        FnvEntryConsistency();
        BitEqualsSemantics();
        VectorOps();
        Color32Pack();
        Matrix4x4Point3x4();
    }

    private static bool ApproxEq(Vector2 a, Vector2 b, float eps = 1e-5f) =>
        MathF.Abs(a.x - b.x) <= eps && MathF.Abs(a.y - b.y) <= eps;

    private static void AffineComposeSemantics()
    {
        // Compose(a, b) ⇔ 先施 b 再施 a：T⊗S 对 (1,1) = 先缩放 (2,2) 再平移 → (12, 22)
        var t = Affine2D.TRS(new Vector2(10f, 20f), 0f, Vector2.one);
        var s = Affine2D.TRS(Vector2.zero, 0f, new Vector2(2f, 2f));
        var p = new Vector2(1f, 1f);
        Vector2 lhs = Affine2D.Compose(t, s).TransformPoint(p);
        Vector2 rhs = t.TransformPoint(s.TransformPoint(p));
        Check("Affine2D Compose ⊗ 语义（先右后左）", ApproxEq(lhs, rhs) && ApproxEq(lhs, new Vector2(12f, 22f)));
    }

    private static void AffineTransformPointKnown()
    {
        // TRS(t=(10,20), rot=π/2, scale=(2,3))：(1,0) →S (2,0) →R90 (0,2) →T (10,22)
        //                                       (0,1) →S (0,3) →R90 (-3,0) →T (7,20)
        var m = Affine2D.TRS(new Vector2(10f, 20f), MathF.PI / 2f, new Vector2(2f, 3f));
        Check("Affine2D TransformPoint 已知值",
            ApproxEq(m.TransformPoint(new Vector2(1f, 0f)), new Vector2(10f, 22f))
            && ApproxEq(m.TransformPoint(new Vector2(0f, 1f)), new Vector2(7f, 20f)));
    }

    private static void AffineInvertRoundtrip()
    {
        var m = Affine2D.Compose(
            Affine2D.TRS(new Vector2(-7f, 3.5f), 0.7f, new Vector2(2f, 0.5f)),
            Affine2D.TRS(new Vector2(4f, -1f), -1.3f, new Vector2(0.25f, 3f)));
        bool invertible = m.TryInvert(out var inv);
        var p = new Vector2(13.25f, -8.5f);
        Check("Affine2D Compose/Invert 往返", invertible && ApproxEq(inv.TransformPoint(m.TransformPoint(p)), p, 1e-4f));

        var singular = Affine2D.TRS(Vector2.zero, 0.3f, new Vector2(0f, 1f)); // scale.x=0 的隐藏节点
        Check("Affine2D 奇异矩阵 TryInvert=false", !singular.TryInvert(out _));
    }

    private static void RectSemantics()
    {
        var r = new Rect(10f, 20f, 30f, 40f);
        Check("Rect xMin..yMax 派生", r.xMin == 10f && r.yMin == 20f && r.xMax == 40f && r.yMax == 60f);
        // Contains 半开区间 [min, max)：min 边命中，max 边不命中（与 Unity 一致）
        Check("Rect Contains 半开区间",
            r.Contains(new Vector2(10f, 20f)) && r.Contains(new Vector2(39.9f, 59.9f))
            && !r.Contains(new Vector2(40f, 60f)) && !r.Contains(new Vector2(9.9f, 30f)));
        // Intersects = Unity Overlaps：真重叠才算，贴边不算
        Check("Rect Intersects 真重叠/贴边不算",
            r.Intersects(new Rect(35f, 50f, 10f, 10f)) && !r.Intersects(new Rect(40f, 20f, 5f, 5f)));
        Check("Rect Union 包络", Rect.Union(r, new Rect(0f, 0f, 5f, 5f)) == new Rect(0f, 0f, 40f, 60f)
            && Rect.MinMaxRect(1f, 2f, 4f, 8f) == new Rect(1f, 2f, 3f, 6f));
    }

    private static void FnvKnownVectors()
    {
        // 标准 FNV-1a 测试向量（ASCII ⇒ UTF-8 同字节）
        Check("FNV-1a 64 已知向量 ''/'a'",
            FnvHash.Hash64("") == 0xcbf29ce484222325UL && FnvHash.Hash64("a") == 0xaf63dc4c8601ec8cUL);
        Check("FNV-1a 32 已知向量 ''/'a'",
            FnvHash.Hash32("") == 0x811c9dc5u && FnvHash.Hash32("a") == 0xe40c292cu);
    }

    private static void FnvEntryConsistency()
    {
        byte[] abc = { 0x61, 0x62, 0x63 };            // "abc"
        byte[] padded = { 0x00, 0x61, 0x62, 0x63, 0x00 };
        Check("FNV bytes/string/子段 三入口一致",
            FnvHash.Hash64(abc) == FnvHash.Hash64("abc")
            && FnvHash.Hash64(padded, 1, 3) == FnvHash.Hash64("abc")
            && FnvHash.Hash32(abc) == FnvHash.Hash32("abc"));
        // fork Fqs.Hash 原语义：null 返回 offset basis（等价空输入）
        Check("FNV null = offset basis（fork 语义）",
            FnvHash.Hash64((byte[]?)null) == FnvHash.OffsetBasis64
            && FnvHash.Hash64((string?)null) == FnvHash.Hash64(""));
    }

    private static void BitEqualsSemantics()
    {
        // 裁决 13：NaN 视为相等（不分 payload）——否则 IEEE NaN != NaN 把含 NaN 属性钉成永脏
        float nanPayload = BitConverter.Int32BitsToSingle(unchecked((int)0xFFC00001));
        Check("BitEquals NaN==NaN（含异 payload）",
            BitEquals.Eq(float.NaN, float.NaN) && BitEquals.Eq(float.NaN, nanPayload)
            && BitEquals.Eq(double.NaN, double.NaN));
        // 有意语义（裁决 13 明文「位等」的推论）：+0 与 -0 位型不同 ⇒ 判不等 ⇒
        // 写 -0 覆盖 +0 置一次脏。宁多脏一次，不引入数值等值的特判分支。
        Check("BitEquals +0 != -0（有意语义）", !BitEquals.Eq(0f, -0f) && !BitEquals.Eq(0.0, -0.0));
        Check("BitEquals 常规位等/不等", BitEquals.Eq(1.5f, 1.5f) && !BitEquals.Eq(1.5f, 1.5000001f)
            && BitEquals.Eq(3.25, 3.25) && !BitEquals.Eq(3.25, 3.250000001));
    }

    private static void VectorOps()
    {
        var a = new Vector2(1f, 5f);
        var b = new Vector2(3f, 2f);
        Check("Vector2 运算/Min/Max/Lerp",
            a + b == new Vector2(4f, 7f) && (a - b) * 2f == new Vector2(-4f, 6f)
            && Vector2.Min(a, b) == new Vector2(1f, 2f) && Vector2.Max(a, b) == new Vector2(3f, 5f)
            && Vector2.Lerp(a, b, 0.5f) == new Vector2(2f, 3.5f)
            && Vector2.Lerp(a, b, 2f) == b);   // t 截断到 [0,1]
        var v = new Vector4(1f, 2f, 3f, 4f);
        Check("Vector4 运算/Min/Max/Lerp",
            v + new Vector4(4f, 3f, 2f, 1f) == new Vector4(5f, 5f, 5f, 5f)
            && v * 2f == new Vector4(2f, 4f, 6f, 8f)
            && Vector4.Min(v, new Vector4(2f, 1f, 4f, 0f)) == new Vector4(1f, 1f, 3f, 0f)
            && Vector4.Lerp(Vector4.zero, v, 0.5f) == new Vector4(0.5f, 1f, 1.5f, 2f));
    }

    private static void Color32Pack()
    {
        // 小端字节序 r,g,b,a：bit0-7=r … bit24-31=a
        Check("Color32.Pack RGBA8",
            new Color32(0x11, 0x22, 0x33, 0x44).Pack() == 0x44332211u
            && new Color32(255, 0, 0, 255).Pack() == 0xFF0000FFu);
    }

    private static void Matrix4x4Point3x4()
    {
        var m = Matrix4x4.Identity;
        m.m00 = 2f; m.m03 = 5f; m.m13 = 6f; m.m23 = 7f;   // scale.x=2 + 平移 (5,6,7)
        Vector3 q = m.MultiplyPoint3x4(new Vector3(1f, 1f, 1f));
        Vector2 flat = q;                                  // 隐转去 z（QuadReassembler 回投路径）
        Check("Matrix4x4.MultiplyPoint3x4 + Vector3→Vector2 隐转",
            q.Equals(new Vector3(7f, 7f, 8f)) && flat == new Vector2(7f, 7f));
    }
}
