using OpenCvSharp;

namespace OMRChecker.Constants;

/// <summary>预定义题型：气泡取值列表与排列方向。</summary>
/// <param name="BubbleValues">每个气泡代表的值，按排列顺序。</param>
/// <param name="Direction">"vertical" 或 "horizontal"。</param>
public sealed record FieldTypeDefinition(string[] BubbleValues, string Direction);

/// <summary>通用常量，对应 Python 版 src/constants/common.py。</summary>
public static class CommonConstants
{
    // 目录内约定的配置文件名
    public const string TemplateFilename = "template.json";
    public const string EvaluationFilename = "evaluation.json";
    public const string ConfigFilename = "config.json";

    /// <summary>字段标签拆分为“前缀 + 数字”的正则，用于输出列自然排序。</summary>
    public const string FieldLabelNumberRegex = @"([^\d]+)(\d*)";

    /// <summary>错误码（与 Python ERROR_CODES 一致）。</summary>
    public const int MultiBubbleWarn = 1;
    public const int NoMarkerErr = 2;

    /// <summary>
    /// 内置题型。可在此追加自定义题型；schema 中 fieldType 的枚举需同步修改
    /// （Schemas/template.schema.json）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, FieldTypeDefinition> FieldTypes =
        new Dictionary<string, FieldTypeDefinition>
        {
            ["QTYPE_INT"] = new(new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" }, "vertical"),
            ["QTYPE_INT_FROM_1"] = new(new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" }, "vertical"),
            ["QTYPE_MCQ4"] = new(new[] { "A", "B", "C", "D" }, "horizontal"),
            ["QTYPE_MCQ5"] = new(new[] { "A", "B", "C", "D", "E" }, "horizontal"),
            ["QTYPE_MCQ4_RTL"] = new(new[] { "D", "C", "B", "A" }, "horizontal"),
            ["QTYPE_MCQ5_RTL"] = new(new[] { "E", "D", "C", "B", "A" }, "horizontal"),
        };

    // 绘制用字号与颜色（灰度图上只取第一个通道）
    public const double TextSize = 0.95;
    public static readonly Scalar ClrBlack = new(50, 150, 150);
    public static readonly Scalar ClrWhite = new(250, 250, 250);
    public static readonly Scalar ClrGray = new(130, 130, 130);
    public static readonly Scalar ClrDarkGray = new(100, 100, 100);

    // 全局阈值兜底值（白底 / 黑底纸张）
    public const double GlobalPageThresholdWhite = 200;
    public const double GlobalPageThresholdBlack = 100;
}

/// <summary>schema 相关常量，对应 Python 版 src/schemas/constants.py。</summary>
public static class SchemaConstants
{
    public const string DefaultSectionKey = "DEFAULT";
    public const string BonusSectionPrefix = "BONUS";
    public static readonly string[] MarkingVerdictTypes = { "correct", "incorrect", "unmarked" };

    /// <summary>区间字段串（如 q1..20）的分组正则：前缀、起始、结束。</summary>
    public const string FieldStringRegexGroups = @"([^\.\d]+)(\d+)\.{2,3}(\d+)";
}
