namespace FairyNext.Numerics;

/// <summary>
/// RGBA8 颜色 shim。字段序 r,g,b,a 对齐 UnityEngine.Color32（QuadReassembler 顶点色调用面）。
/// 不提供 float Color 类型：实例流侧颜色以打包 uint 走 ABI，浮点色到出现真实调用面再加。
/// </summary>
public struct Color32 : IEquatable<Color32>
{
    public byte r, g, b, a;

    public Color32(byte r, byte g, byte b, byte a)
    {
        this.r = r; this.g = g; this.b = b; this.a = a;
    }

    /// <summary>
    /// 打包为 uint：bit0-7=r、bit8-15=g、bit16-23=b、bit24-31=a。小端机器上内存字节序即
    /// r,g,b,a——与 GPU RGBA8 unorm 顶点色/纹素的字节序一致，可直接落实例流。
    /// </summary>
    public readonly uint Pack() => (uint)(r | (g << 8) | (b << 16) | (a << 24));

    public static bool operator ==(Color32 x, Color32 y) => x.r == y.r && x.g == y.g && x.b == y.b && x.a == y.a;
    public static bool operator !=(Color32 x, Color32 y) => !(x == y);

    public readonly bool Equals(Color32 other) => this == other;
    public readonly override bool Equals(object? obj) => obj is Color32 c && Equals(c);
    public readonly override int GetHashCode() => (int)Pack();
    public readonly override string ToString() => $"RGBA({r}, {g}, {b}, {a})";
}
