using System.Text.Json.Nodes;
using OMRChecker.Constants;
using OMRChecker.Logging;
using OMRChecker.Utils;

namespace OMRChecker.Evaluation;

/// <summary>
/// 分区评分方案，对应 Python 版 SectionMarkingScheme。
/// DEFAULT 分区直接写 {correct, incorrect, unmarked}；其他分区写 {questions, marking}。
/// </summary>
public sealed class SectionMarkingScheme
{
    /// <summary>
    /// 解析分区评分方案。
    /// </summary>
    /// <param name="sectionKey">分区名，"DEFAULT" 为默认分区。</param>
    /// <param name="sectionScheme">分区配置 JSON。</param>
    /// <param name="emptyVal">未作答时的取值（模板全局空值）。</param>
    public SectionMarkingScheme(string sectionKey, JsonObject sectionScheme, string emptyVal)
    {
        EmptyVal = emptyVal;
        SectionKey = sectionKey;
        if (sectionKey == SchemaConstants.DefaultSectionKey)
        {
            Questions = null;
            Marking = ParseSchemeMarking(sectionScheme);
        }
        else
        {
            // schema 允许 questions 为单个字段串或数组
            var questionsNode = sectionScheme["questions"];
            var questionStrings = questionsNode is JsonArray
                ? JsonUtils.GetStringList(questionsNode)
                : new List<string> { questionsNode!.GetValue<string>() };
            Questions = ParsingUtils.ParseFields(sectionKey, questionStrings);
            Marking = ParseSchemeMarking(sectionScheme["marking"]!.AsObject());
        }
    }

    public string EmptyVal { get; }

    public string SectionKey { get; }

    /// <summary>本分区包含的题目；DEFAULT 分区为 null。</summary>
    public List<string>? Questions { get; }

    /// <summary>correct / incorrect / unmarked → 分值。</summary>
    public Dictionary<string, double> Marking { get; }

    public override string ToString() => SectionKey;

    /// <summary>
    /// 解析三种判定的分值（支持分数字符串）。非 BONUS 分区给错题正分时给出警告。
    /// </summary>
    private Dictionary<string, double> ParseSchemeMarking(JsonObject marking)
    {
        var parsed = new Dictionary<string, double>();
        foreach (var verdictType in SchemaConstants.MarkingVerdictTypes)
        {
            var verdictMarking = ParsingUtils.ParseFloatOrFraction(marking[verdictType]);
            if (verdictMarking > 0 && verdictType == "incorrect"
                                   && !SectionKey.StartsWith(SchemaConstants.BonusSectionPrefix, StringComparison.Ordinal))
            {
                Logger.Warning(
                    $"Found positive marks({PyFormat.Float(PyFormat.Round(verdictMarking, 2))}) for incorrect answer in the schema '{SectionKey}'. For Bonus sections, add a prefix 'BONUS_' to them.");
            }

            parsed[verdictType] = verdictMarking;
        }

        return parsed;
    }
}
