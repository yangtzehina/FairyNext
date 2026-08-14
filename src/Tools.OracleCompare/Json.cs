using System.Globalization;
using System.Text;

namespace FairyNext.Tools.OracleCompare;

/// <summary>
/// 最小 JSON 读取器：golden 的 layout.json / meta.json 是本仓库自己产出的确定性文本，
/// 不需要通用序列化框架，需要的是**零 NuGet** 与**缺字段即拒收**。
///
/// 不支持的：注释、NaN/Infinity 字面量、代理对之外的转义细节。遇到即 <see cref="FormatException"/> 带偏移。
/// 写入端不在这里——meta/layout 由 Unity 侧驱动写出（tools/oracle/lib/declarations.cs）。
/// </summary>
public sealed class JsonValue
{
    public enum JKind { Null, Bool, Number, String, Array, Object }

    public JKind Kind { get; private set; }
    public bool Bool { get; private set; }
    public double Number { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public List<JsonValue> Items { get; } = new List<JsonValue>();
    public Dictionary<string, JsonValue> Members { get; } = new Dictionary<string, JsonValue>(StringComparer.Ordinal);

    public static JsonValue Parse(string text)
    {
        int i = 0;
        JsonValue v = ParseValue(text, ref i);
        SkipWs(text, ref i);
        if (i != text.Length) throw Err(i, "根值之后还有内容");
        return v;
    }

    // ---- 取值：Require* 缺字段就抛，是「meta 缺字段 CI 拒收」的执行形 ----------

    public JsonValue? Member(string name) => Members.TryGetValue(name, out JsonValue? v) ? v : null;

    public JsonValue RequireMember(string name)
        => Member(name) ?? throw new FormatException($"缺字段 \"{name}\"");

    public string RequireString(string name)
    {
        JsonValue v = RequireMember(name);
        if (v.Kind != JKind.String) throw new FormatException($"字段 \"{name}\" 应为 string，实为 {v.Kind}");
        return v.Text;
    }

    public double RequireNumber(string name)
    {
        JsonValue v = RequireMember(name);
        if (v.Kind != JKind.Number) throw new FormatException($"字段 \"{name}\" 应为 number，实为 {v.Kind}");
        return v.Number;
    }

    public int RequireInt(string name)
    {
        double d = RequireNumber(name);
        int i = (int)Math.Round(d);
        if (Math.Abs(d - i) > 1e-9) throw new FormatException($"字段 \"{name}\" 应为整数，实为 {d}");
        return i;
    }

    public bool RequireBool(string name)
    {
        JsonValue v = RequireMember(name);
        if (v.Kind != JKind.Bool) throw new FormatException($"字段 \"{name}\" 应为 bool，实为 {v.Kind}");
        return v.Bool;
    }

    public JsonValue RequireObject(string name)
    {
        JsonValue v = RequireMember(name);
        if (v.Kind != JKind.Object) throw new FormatException($"字段 \"{name}\" 应为 object，实为 {v.Kind}");
        return v;
    }

    public List<JsonValue> RequireArray(string name)
    {
        JsonValue v = RequireMember(name);
        if (v.Kind != JKind.Array) throw new FormatException($"字段 \"{name}\" 应为 array，实为 {v.Kind}");
        return v.Items;
    }

    // ---- 解析 --------------------------------------------------------------

    private static FormatException Err(int at, string what) => new FormatException($"JSON @{at}: {what}");

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
    }

    private static JsonValue ParseValue(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) throw Err(i, "内容提前结束");
        char c = s[i];
        switch (c)
        {
            case '{': return ParseObject(s, ref i);
            case '[': return ParseArray(s, ref i);
            case '"': return new JsonValue { Kind = JKind.String, Text = ParseString(s, ref i) };
        }
        if (Lit(s, i, "true")) { i += 4; return new JsonValue { Kind = JKind.Bool, Bool = true }; }
        if (Lit(s, i, "false")) { i += 5; return new JsonValue { Kind = JKind.Bool, Bool = false }; }
        if (Lit(s, i, "null")) { i += 4; return new JsonValue { Kind = JKind.Null }; }

        int start = i;
        while (i < s.Length && "+-.eE0123456789".IndexOf(s[i]) >= 0) i++;
        if (i == start) throw Err(i, $"无法识别的字符 '{c}'");
        string num = s.Substring(start, i - start);
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            throw Err(start, $"不是合法数字：{num}");
        return new JsonValue { Kind = JKind.Number, Number = d };
    }

    private static bool Lit(string s, int i, string lit)
        => i + lit.Length <= s.Length && string.CompareOrdinal(s, i, lit, 0, lit.Length) == 0;

    private static JsonValue ParseObject(string s, ref int i)
    {
        var o = new JsonValue { Kind = JKind.Object };
        i++; // '{'
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return o; }
        while (true)
        {
            SkipWs(s, ref i);
            string key = ParseString(s, ref i);
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != ':') throw Err(i, "对象里缺 ':'");
            i++;
            o.Members[key] = ParseValue(s, ref i);
            SkipWs(s, ref i);
            if (i >= s.Length) throw Err(i, "对象未闭合");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') { i++; return o; }
            throw Err(i, "对象里缺 ',' 或 '}'");
        }
    }

    private static JsonValue ParseArray(string s, ref int i)
    {
        var a = new JsonValue { Kind = JKind.Array };
        i++; // '['
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return a; }
        while (true)
        {
            a.Items.Add(ParseValue(s, ref i));
            SkipWs(s, ref i);
            if (i >= s.Length) throw Err(i, "数组未闭合");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == ']') { i++; return a; }
            throw Err(i, "数组里缺 ',' 或 ']'");
        }
    }

    private static string ParseString(string s, ref int i)
    {
        if (i >= s.Length || s[i] != '"') throw Err(i, "此处应是字符串");
        i++;
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length) throw Err(i, "字符串未闭合");
            char c = s[i];
            if (c == '"') { i++; return sb.ToString(); }
            if (c != '\\') { sb.Append(c); i++; continue; }
            i++;
            if (i >= s.Length) throw Err(i, "转义未完成");
            char e = s[i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case '/': sb.Append('/'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case 'u':
                    if (i + 4 > s.Length) throw Err(i, "\\u 转义不完整");
                    sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: throw Err(i - 1, $"未知转义 \\{e}");
            }
        }
    }
}
