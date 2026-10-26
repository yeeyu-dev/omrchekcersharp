using System.Text;

namespace OMRChecker.Logging;

/// <summary>
/// 简易控制台表格，替代 Python 版使用的 rich.table.Table。
/// 同时保存列名与单元格数据，供评分解释表导出 CSV 使用。
/// </summary>
public sealed class ConsoleTable
{
    private readonly List<string> _columns = new();
    private readonly List<string[]> _rows = new();

    /// <summary>创建表格。</summary>
    /// <param name="title">表格标题，可为空。</param>
    /// <param name="showHeader">是否显示表头。</param>
    /// <param name="showLines">是否在每行之间绘制分隔线。</param>
    public ConsoleTable(string? title = null, bool showHeader = true, bool showLines = false)
    {
        Title = title;
        ShowHeader = showHeader;
        ShowLines = showLines;
    }

    public string? Title { get; }

    public bool ShowHeader { get; }

    public bool ShowLines { get; }

    /// <summary>列名（只读视图）。</summary>
    public IReadOnlyList<string> Columns => _columns;

    /// <summary>行数据（只读视图）。</summary>
    public IReadOnlyList<string[]> Rows => _rows;

    /// <summary>追加一列。</summary>
    public void AddColumn(string name) => _columns.Add(name);

    /// <summary>
    /// 追加一行；单元格少于列数时以空串补齐，多于列数时抛出异常。
    /// </summary>
    public void AddRow(params string[] cells)
    {
        if (cells.Length > _columns.Count)
        {
            throw new ArgumentException($"行单元格数 {cells.Length} 超过列数 {_columns.Count}");
        }

        var row = new string[_columns.Count];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = i < cells.Length ? cells[i] : string.Empty;
        }

        _rows.Add(row);
    }

    /// <summary>取出某一列的全部单元格（用于导出 CSV）。</summary>
    public IReadOnlyList<string> GetColumnCells(int columnIndex) => _rows.Select(r => r[columnIndex]).ToList();

    /// <summary>将表格渲染为字符串（框线字符绘制，按显示宽度对齐中日韩字符）。</summary>
    public string Render()
    {
        var widths = new int[_columns.Count];
        for (var c = 0; c < _columns.Count; c++)
        {
            widths[c] = ShowHeader ? DisplayWidth(_columns[c]) : 0;
            foreach (var row in _rows)
            {
                widths[c] = Math.Max(widths[c], DisplayWidth(row[c]));
            }
        }

        var sb = new StringBuilder();
        var totalWidth = widths.Sum() + (3 * widths.Length) + 1;
        if (Title is { Length: > 0 })
        {
            var pad = Math.Max(0, (totalWidth - DisplayWidth(Title)) / 2);
            sb.Append(' ', pad).AppendLine(Title);
        }

        sb.AppendLine(Border('┌', '┬', '┐', widths));
        if (ShowHeader)
        {
            sb.AppendLine(Line(_columns, widths));
            sb.AppendLine(Border('├', '┼', '┤', widths));
        }

        for (var r = 0; r < _rows.Count; r++)
        {
            sb.AppendLine(Line(_rows[r], widths));
            if (ShowLines && r < _rows.Count - 1)
            {
                sb.AppendLine(Border('├', '┼', '┤', widths));
            }
        }

        sb.Append(Border('└', '┴', '┘', widths));
        return sb.ToString();
    }

    /// <summary>直接打印到控制台。</summary>
    public void Print() => Console.WriteLine(Render());

    /// <summary>绘制一条横向边框。</summary>
    private static string Border(char left, char mid, char right, int[] widths) =>
        left + string.Join(mid.ToString(), widths.Select(w => new string('─', w + 2))) + right;

    /// <summary>绘制一行单元格。</summary>
    private static string Line(IReadOnlyList<string> cells, int[] widths)
    {
        var sb = new StringBuilder("│");
        for (var i = 0; i < widths.Length; i++)
        {
            var cell = cells[i];
            sb.Append(' ').Append(cell).Append(' ', widths[i] - DisplayWidth(cell)).Append(" │");
        }

        return sb.ToString();
    }

    /// <summary>计算字符串的终端显示宽度：中日韩全角字符按 2 列计算。</summary>
    internal static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var ch in text)
        {
            width += ch >= 0x1100 && (ch <= 0x115F || (ch >= 0x2E80 && ch <= 0xA4CF) || (ch >= 0xAC00 && ch <= 0xD7A3)
                                      || (ch >= 0xF900 && ch <= 0xFAFF) || (ch >= 0xFE30 && ch <= 0xFE4F)
                                      || (ch >= 0xFF00 && ch <= 0xFF60) || (ch >= 0xFFE0 && ch <= 0xFFE6))
                ? 2
                : 1;
        }

        return width;
    }
}
