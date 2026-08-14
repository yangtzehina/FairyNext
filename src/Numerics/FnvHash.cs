namespace FairyNext.Numerics;

/// <summary>
/// FNV-1a 散列（64/32）。
/// 语义源：~/ECS/FairyGUI-unity Assets/Scripts/Core/Instanced/Fqs.cs 的 Hash() @ d1a9d7d——
/// 64 位 offset basis 0xcbf29ce484222325、prime 0x100000001b3，逐字节 (h ^ b) * prime，
/// null 输入返回 offset basis（fork 原语义，保持以便同一资产两边算出同一门值）。
/// 32 位用标准 FNV-1a 参数（basis 0x811c9dc5 / prime 0x01000193）。
/// string 入口按 UTF-8 字节流散列（零分配内联编码；孤立代理折为 U+FFFD，与 Encoding.UTF8
/// 的替换语义一致）——ASCII 串与其字节数组同值，标准测试向量（"" / "a"）直接适用。
/// </summary>
public static class FnvHash
{
    public const ulong OffsetBasis64 = 0xcbf29ce484222325UL;
    public const ulong Prime64 = 0x100000001b3UL;
    public const uint OffsetBasis32 = 0x811c9dc5u;
    public const uint Prime32 = 0x01000193u;

    public static ulong Hash64(byte[]? bytes) => Hash64(bytes, 0, bytes?.Length ?? 0);

    public static ulong Hash64(byte[]? bytes, int offset, int count)
    {
        ulong h = OffsetBasis64;
        if (bytes == null)
            return h;
        int end = offset + count;
        for (int i = offset; i < end; i++)
            h = (h ^ bytes[i]) * Prime64;
        return h;
    }

    public static uint Hash32(byte[]? bytes) => Hash32(bytes, 0, bytes?.Length ?? 0);

    public static uint Hash32(byte[]? bytes, int offset, int count)
    {
        uint h = OffsetBasis32;
        if (bytes == null)
            return h;
        int end = offset + count;
        for (int i = offset; i < end; i++)
            h = (h ^ bytes[i]) * Prime32;
        return h;
    }

    public static ulong Hash64(string? s)
    {
        ulong h = OffsetBasis64;
        if (s == null)
            return h;
        Span<byte> buf = stackalloc byte[4];
        for (int i = 0; i < s.Length;)
        {
            int n = EncodeUtf8(NextScalar(s, ref i), buf);
            for (int k = 0; k < n; k++)
                h = (h ^ buf[k]) * Prime64;
        }
        return h;
    }

    public static uint Hash32(string? s)
    {
        uint h = OffsetBasis32;
        if (s == null)
            return h;
        Span<byte> buf = stackalloc byte[4];
        for (int i = 0; i < s.Length;)
        {
            int n = EncodeUtf8(NextScalar(s, ref i), buf);
            for (int k = 0; k < n; k++)
                h = (h ^ buf[k]) * Prime32;
        }
        return h;
    }

    /// <summary>取下一个 Unicode 标量（代理对合并；孤立代理 → U+FFFD）。</summary>
    private static int NextScalar(string s, ref int i)
    {
        char c = s[i++];
        if (char.IsHighSurrogate(c) && i < s.Length && char.IsLowSurrogate(s[i]))
            return char.ConvertToUtf32(c, s[i++]);
        return char.IsSurrogate(c) ? 0xFFFD : c;
    }

    private static int EncodeUtf8(int scalar, Span<byte> dst)
    {
        if (scalar < 0x80)
        {
            dst[0] = (byte)scalar;
            return 1;
        }
        if (scalar < 0x800)
        {
            dst[0] = (byte)(0xC0 | (scalar >> 6));
            dst[1] = (byte)(0x80 | (scalar & 0x3F));
            return 2;
        }
        if (scalar < 0x10000)
        {
            dst[0] = (byte)(0xE0 | (scalar >> 12));
            dst[1] = (byte)(0x80 | ((scalar >> 6) & 0x3F));
            dst[2] = (byte)(0x80 | (scalar & 0x3F));
            return 3;
        }
        dst[0] = (byte)(0xF0 | (scalar >> 18));
        dst[1] = (byte)(0x80 | ((scalar >> 12) & 0x3F));
        dst[2] = (byte)(0x80 | ((scalar >> 6) & 0x3F));
        dst[3] = (byte)(0x80 | (scalar & 0x3F));
        return 4;
    }
}
