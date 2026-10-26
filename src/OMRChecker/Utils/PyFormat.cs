using System.Collections;
using System.Globalization;
using System.Text;

namespace OMRChecker.Utils;

/// <summary>
/// 复刻 Python 的 str()/repr()/round()/str.title() 输出格式。
/// 结果 CSV（如分数 "5.0"）与评分解释表需要与 Python 版逐字一致，因此集中在此实现。
/// </summary>
public static class PyFormat
{
    /// <summary>
    /// 等价于 Python 的 repr(float)：整数值输出 "5.0"，其余输出最短往返表示，
    /// 科学计数法使用小写 e（如 1e-05）。NaN/无穷分别输出 nan/inf/-inf。
    /// </summary>
    public static string Float(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        if (value == Math.Floor(value) && Math.Abs(value) < 1e16)
        {
            var integral = ((long)value).ToString(CultureInfo.InvariantCulture);
            if (value == 0 && double.IsNegative(value))
            {
                integral = "-0";
            }

            return integral + ".0";
        }

        return value.ToString("R", CultureInfo.InvariantCulture).Replace("E", "e");
    }

    /// <summary>
    /// 等价于 Python 的 round(x, digits)（银行家舍入）。
    /// 边界：极少数二进制表示恰在中点附近的值可能与 Python 相差 1 个末位。
    /// </summary>
    public static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.ToEven);

    /// <summary>
    /// 等价于 Python 的 str(obj)：字符串原样返回，其余对象使用 <see cref="Repr"/>。
    /// </summary>
    public static string Str(object? value) => value is string s ? s : Repr(value);

    /// <summary>
    /// 等价于 Python 的 repr(obj)。支持 string / long / int / double / bool / null /
    /// 列表（IList）/ 字典（IDictionary，按插入顺序）。
    /// </summary>
    public static string Repr(object? value)
    {
        switch (value)
        {
            case null:
                return "None";
            case string s:
                return QuoteString(s);
            case bool b:
                return b ? "True" : "False";
            case double d:
                return Float(d);
            case float f:
                return Float(f);
            case int or long or short or byte:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case IDictionary dict:
            {
                var parts = new List<string>();
                foreach (DictionaryEntry entry in dict)
                {
                    parts.Add($"{Repr(entry.Key)}: {Repr(entry.Value)}");
                }

                return "{" + string.Join(", ", parts) + "}";
            }

            case IEnumerable list:
                return "[" + string.Join(", ", list.Cast<object?>().Select(Repr)) + "]";
            default:
                return value.ToString() ?? "None";
        }
    }

    /// <summary>
    /// 等价于 Python 的 str.title()：每个字母段首字母大写，其余字母小写。
    /// 例如 "correct-AB" → "Correct-Ab"。
    /// </summary>
    public static string Title(string text)
    {
        var sb = new StringBuilder(text.Length);
        var previousIsLetter = false;
        foreach (var ch in text)
        {
            if (char.IsLetter(ch))
            {
                sb.Append(previousIsLetter ? char.ToLowerInvariant(ch) : char.ToUpperInvariant(ch));
                previousIsLetter = true;
            }
            else
            {
                sb.Append(ch);
                previousIsLetter = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 按 Python repr 规则给字符串加引号：默认单引号；仅当包含单引号且不含双引号时用双引号。
    /// </summary>
    private static string QuoteString(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder();
        sb.Append(quote);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (ch == quote)
                    {
                        sb.Append('\\');
                    }

                    sb.Append(ch);
                    break;
            }
        }

        sb.Append(quote);
        return sb.ToString();
    }
}
