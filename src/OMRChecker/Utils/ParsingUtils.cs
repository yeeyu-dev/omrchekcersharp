using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OMRChecker.Config;
using OMRChecker.Constants;
using OMRChecker.Core;

namespace OMRChecker.Utils;

/// <summary>模板 / 配置解析辅助方法，对应 Python 版 src/utils/parsing.py。</summary>
public static class ParsingUtils
{
    /// <summary>模板默认值（与 Python 版 TEMPLATE_DEFAULTS 一致）。</summary>
    private const string TemplateDefaultsJson = """
    {
      "preProcessors": [],
      "emptyValue": "",
      "customLabels": {},
      "outputColumns": []
    }
    """;

    private static readonly Regex FieldStringRegex = new(SchemaConstants.FieldStringRegexGroups, RegexOptions.Compiled);
    private static readonly Regex FieldLabelNumberRegex = new(CommonConstants.FieldLabelNumberRegex, RegexOptions.Compiled);

    /// <summary>
    /// 拼接多列字段：customLabels 中的每个自定义标签按顺序拼接其子字段，
    /// 其余非自定义字段原样保留。顺序：先自定义标签，后非自定义标签。
    /// </summary>
    public static Dictionary<string, string> GetConcatenatedResponse(
        IReadOnlyDictionary<string, string> omrResponse, Template template)
    {
        var concatenated = new Dictionary<string, string>();
        foreach (var (fieldLabel, concatenateKeys) in template.CustomLabels)
        {
            concatenated[fieldLabel] = string.Concat(concatenateKeys.Select(k => omrResponse[k]));
        }

        foreach (var fieldLabel in template.NonCustomLabels)
        {
            concatenated[fieldLabel] = omrResponse[fieldLabel];
        }

        return concatenated;
    }

    /// <summary>读取 config.json，与默认配置深度合并后校验并转为强类型对象。</summary>
    public static TuningConfig OpenConfigWithDefaults(string configPath)
    {
        var user = JsonUtils.LoadJson(configPath).AsObject();
        var merged = JsonUtils.DeepMerge(TuningConfig.DefaultsNode(), user);
        SchemaValidation.ValidateConfigJson(merged, configPath);
        return TuningConfig.FromNode(merged);
    }

    /// <summary>读取 template.json，与模板默认值深度合并后校验。</summary>
    public static JsonObject OpenTemplateWithDefaults(string templatePath)
    {
        var user = JsonUtils.LoadJson(templatePath).AsObject();
        var merged = JsonUtils.DeepMerge(JsonNode.Parse(TemplateDefaultsJson)!.AsObject(), user);
        SchemaValidation.ValidateTemplateJson(merged, templatePath);
        return merged;
    }

    /// <summary>读取并校验 evaluation.json。</summary>
    public static JsonObject OpenEvaluationWithValidation(string evaluationPath)
    {
        var user = JsonUtils.LoadJson(evaluationPath);
        SchemaValidation.ValidateEvaluationJson(user, evaluationPath);
        return user.AsObject();
    }

    /// <summary>
    /// 展开字段串列表（如 ["q1..3", "roll"] → q1,q2,q3,roll）。
    /// 边界：不同字段串展开后若有重叠，抛出异常。
    /// </summary>
    /// <param name="key">用于错误信息的上下文名称。</param>
    /// <param name="fields">字段串列表。</param>
    public static List<string> ParseFields(string key, IEnumerable<string> fields)
    {
        var fieldList = fields.ToList();
        var parsedFields = new List<string>();
        var fieldsSet = new HashSet<string>();
        foreach (var fieldString in fieldList)
        {
            var fieldsArray = ParseFieldString(fieldString);
            if (fieldsArray.Any(fieldsSet.Contains))
            {
                throw new OmrCheckerException(
                    $"Given field string '{fieldString}' has overlapping field(s) with other fields in '{key}': {PyFormat.Repr(fieldList)}");
            }

            fieldsSet.UnionWith(fieldsArray);
            parsedFields.AddRange(fieldsArray);
        }

        return parsedFields;
    }

    /// <summary>
    /// 展开单个字段串：含 "." 时按 前缀+起始..结束 展开（含两端），否则原样返回。
    /// 边界：起始不小于结束时抛出异常。
    /// </summary>
    public static List<string> ParseFieldString(string fieldString)
    {
        if (!fieldString.Contains('.'))
        {
            return new List<string> { fieldString };
        }

        var match = FieldStringRegex.Match(fieldString);
        if (!match.Success)
        {
            throw new OmrCheckerException($"Invalid fields string: '{fieldString}'");
        }

        var prefix = match.Groups[1].Value;
        var start = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var end = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (start >= end)
        {
            throw new OmrCheckerException(
                $"Invalid range in fields string: '{fieldString}', start: {start} is not less than end: {end}");
        }

        return Enumerable.Range(start, end - start + 1).Select(n => $"{prefix}{n}").ToList();
    }

    /// <summary>
    /// 输出列的自然排序比较器：先按非数字前缀（序数比较，同 Python 字符串比较），
    /// 再按紧随其后的数字大小（无数字视为 0）。
    /// </summary>
    public static int CompareOutputColumns(string a, string b)
    {
        var (prefixA, numberA) = SplitLabel(a);
        var (prefixB, numberB) = SplitLabel(b);
        var cmp = string.CompareOrdinal(prefixA, prefixB);
        return cmp != 0 ? cmp : numberA.CompareTo(numberB);
    }

    /// <summary>
    /// 将分值解析为 double：支持 "3/2"、"-1/2" 等分数字符串，以及普通数字或数字字符串。
    /// </summary>
    public static double ParseFloatOrFraction(object? result)
    {
        switch (result)
        {
            case string s when s.Contains('/'):
            {
                var parts = s.Split('/');
                var numerator = double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                var denominator = double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                return numerator / denominator;
            }

            case string s:
                return double.Parse(s.Trim(), CultureInfo.InvariantCulture);
            case JsonNode node:
                return ParseFloatOrFraction(JsonUtils.ToPlainObject(node));
            case null:
                throw new OmrCheckerException("Missing marking score");
            default:
                return Convert.ToDouble(result, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>按 FIELD_LABEL_NUMBER_REGEX 拆分标签为（前缀, 数字）。</summary>
    private static (string Prefix, long Number) SplitLabel(string label)
    {
        var match = FieldLabelNumberRegex.Match(label);
        if (!match.Success)
        {
            return (string.Empty, 0);
        }

        var suffix = match.Groups[2].Value;
        return (match.Groups[1].Value, suffix.Length > 0 ? long.Parse(suffix, CultureInfo.InvariantCulture) : 0);
    }
}
