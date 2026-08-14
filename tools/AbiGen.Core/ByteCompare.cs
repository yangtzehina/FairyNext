using System.Globalization;
using System.Text;

namespace FairyNext.AbiGen;

/// <summary>
/// 字节比对门的比较与报错渲染。门失败时必须给出**首差异偏移**——「生成物漂移了」不可行动，
/// 「第 1734 字节，行 62 列 18，期望 '8' 实得 '4'」可以（验证平面：首差异字节定位）。
/// </summary>
public static class ByteCompare
{
    /// <summary>首个不同字节的下标；完全相同返回 -1。长度不同但前缀相同则返回较短者长度。</summary>
    public static int FirstDifference(byte[] expected, byte[] actual)
    {
        int n = expected.Length < actual.Length ? expected.Length : actual.Length;
        for (int i = 0; i < n; i++)
        {
            if (expected[i] != actual[i]) return i;
        }
        return expected.Length == actual.Length ? -1 : n;
    }

    /// <summary>
    /// 渲染首差异说明（单行）。上下文按 UTF-8 解码，切口可能落在多字节字符中间——
    /// 此时出现替换字符是诊断噪声，不是数据损坏。
    /// </summary>
    public static string Describe(byte[] expected, byte[] actual, int offset)
    {
        int line = 1, col = 1;
        for (int i = 0; i < offset && i < expected.Length; i++)
        {
            if (expected[i] == (byte)'\n') { line++; col = 1; }
            else col++;
        }

        var sb = new StringBuilder();
        sb.Append("首差异 @").Append(offset.ToString(CultureInfo.InvariantCulture))
          .Append("（行 ").Append(line.ToString(CultureInfo.InvariantCulture))
          .Append(" 列 ").Append(col.ToString(CultureInfo.InvariantCulture)).Append("）")
          .Append("：期望 ").Append(ByteAt(expected, offset))
          .Append("，实得 ").Append(ByteAt(actual, offset))
          .Append("；长度 期望=").Append(expected.Length.ToString(CultureInfo.InvariantCulture))
          .Append(" 实得=").Append(actual.Length.ToString(CultureInfo.InvariantCulture))
          .Append('\n')
          .Append("        期望上下文 |").Append(Excerpt(expected, offset)).Append('|')
          .Append('\n')
          .Append("        实得上下文 |").Append(Excerpt(actual, offset)).Append('|');
        return sb.ToString();
    }

    private static string ByteAt(byte[] data, int offset)
    {
        if (offset >= data.Length) return "<文件结束>";
        byte b = data[offset];
        string printable = b >= 0x20 && b < 0x7F ? " '" + (char)b + "'" : b == (byte)'\n' ? " '\\n'" : string.Empty;
        return "0x" + b.ToString("X2", CultureInfo.InvariantCulture) + printable;
    }

    private static string Excerpt(byte[] data, int offset)
    {
        const int Radius = 40;
        int start = offset - Radius < 0 ? 0 : offset - Radius;
        int end = offset + Radius > data.Length ? data.Length : offset + Radius;
        if (start >= end) return string.Empty;
        return new UTF8Encoding(false).GetString(data, start, end - start).Replace("\n", "\\n");
    }
}
