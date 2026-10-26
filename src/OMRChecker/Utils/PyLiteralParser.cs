using System.Globalization;
using System.Text;

namespace OMRChecker.Utils;

/// <summary>
/// Python 字面量的最小解析器，替代 ast.literal_eval，用于答案 CSV 中的列表写法，
/// 例如 "['A', 'B']" 或 "[['A', 1], ['B', '3/2']]"。
/// 支持：列表 [] / 元组 ()、单双引号字符串、整数（long）、浮点数（double）、True / False / None。
/// </summary>
public sealed class PyLiteralParser
{
    private readonly string _text;
    private int _pos;

    private PyLiteralParser(string text)
    {
        _text = text;
    }

    /// <summary>
    /// 解析字面量文本。边界：存在未消费的尾随字符或语法错误时抛出 <see cref="FormatException"/>。
    /// </summary>
    public static object? Parse(string text)
    {
        var parser = new PyLiteralParser(text);
        var value = parser.ParseValue();
        parser.SkipWhitespace();
        if (parser._pos != text.Length)
        {
            throw new FormatException($"Unexpected trailing characters in literal: '{text}'");
        }

        return value;
    }

    /// <summary>解析一个值（按首字符分派）。</summary>
    private object? ParseValue()
    {
        SkipWhitespace();
        if (_pos >= _text.Length)
        {
            throw new FormatException($"Unexpected end of literal: '{_text}'");
        }

        var ch = _text[_pos];
        return ch switch
        {
            '[' => ParseSequence(']'),
            '(' => ParseSequence(')'),
            '\'' or '"' => ParseString(),
            _ when char.IsDigit(ch) || ch is '-' or '+' or '.' => ParseNumber(),
            _ => ParseKeyword(),
        };
    }

    /// <summary>解析列表或元组，允许尾随逗号。</summary>
    private List<object?> ParseSequence(char close)
    {
        _pos++;
        var items = new List<object?>();
        while (true)
        {
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == close)
            {
                _pos++;
                return items;
            }

            items.Add(ParseValue());
            SkipWhitespace();
            if (_pos >= _text.Length)
            {
                throw new FormatException($"Unterminated sequence in literal: '{_text}'");
            }

            if (_text[_pos] == ',')
            {
                _pos++;
            }
            else if (_text[_pos] != close)
            {
                throw new FormatException($"Expected ',' or '{close}' in literal: '{_text}'");
            }
        }
    }

    /// <summary>解析带引号的字符串，支持常见反斜杠转义。</summary>
    private string ParseString()
    {
        var quote = _text[_pos++];
        var sb = new StringBuilder();
        while (_pos < _text.Length)
        {
            var ch = _text[_pos++];
            if (ch == quote)
            {
                return sb.ToString();
            }

            if (ch == '\\' && _pos < _text.Length)
            {
                var next = _text[_pos++];
                sb.Append(next switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    _ => next,
                });
            }
            else
            {
                sb.Append(ch);
            }
        }

        throw new FormatException($"Unterminated string in literal: '{_text}'");
    }

    /// <summary>解析数字：不含小数点 / 指数时为 long，否则为 double。</summary>
    private object ParseNumber()
    {
        var start = _pos;
        while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] is '-' or '+' or '.' or 'e' or 'E' or '_'))
        {
            _pos++;
        }

        var token = _text[start.._pos].Replace("_", string.Empty);
        if (token.IndexOfAny(new[] { '.', 'e', 'E' }) < 0
            && long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>解析 True / False / None。</summary>
    private object? ParseKeyword()
    {
        foreach (var (word, value) in new (string, object?)[] { ("True", true), ("False", false), ("None", null) })
        {
            if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) == 0)
            {
                _pos += word.Length;
                return value;
            }
        }

        throw new FormatException($"Malformed literal: '{_text}'");
    }

    /// <summary>跳过空白字符。</summary>
    private void SkipWhitespace()
    {
        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
        {
            _pos++;
        }
    }
}
