using System.Text;

namespace OMRChecker.Utils;

/// <summary>
/// CSV 读写。写出格式等价于 pandas to_csv(quoting=QUOTE_NONNUMERIC) 写字符串列：
/// 每个字段都加双引号，内部双引号转义为两个；换行符使用当前系统换行符；UTF-8 无 BOM。
/// </summary>
public static class CsvUtils
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>以追加方式写入一行（文件不存在时自动创建）。</summary>
    public static void AppendRow(string path, IEnumerable<string> values) =>
        File.AppendAllText(path, FormatRow(values) + Environment.NewLine, Utf8NoBom);

    /// <summary>以追加方式写入多行。</summary>
    public static void AppendRows(string path, IEnumerable<IEnumerable<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(FormatRow(row)).Append(Environment.NewLine);
        }

        File.AppendAllText(path, sb.ToString(), Utf8NoBom);
    }

    /// <summary>将一行字段格式化为全引号 CSV 文本。</summary>
    public static string FormatRow(IEnumerable<string> values) =>
        string.Join(",", values.Select(v => "\"" + v.Replace("\"", "\"\"") + "\""));

    /// <summary>
    /// 解析 CSV 文本（RFC 4180：支持引号字段、"" 转义、引号内换行）。
    /// 与 pandas 默认行为一致，跳过空行。
    /// </summary>
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void EndRow()
        {
            EndField();
            if (!(row.Count == 1 && row[0].Length == 0))
            {
                rows.Add(row);
            }

            row = new List<string>();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"' when !fieldStarted || field.Length == 0:
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                case ',':
                    EndField();
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    field.Append(ch);
                    fieldStarted = true;
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            EndRow();
        }

        return rows;
    }
}
