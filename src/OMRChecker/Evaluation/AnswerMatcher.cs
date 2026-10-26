using OMRChecker.Logging;
using OMRChecker.Utils;

namespace OMRChecker.Evaluation;

/// <summary>
/// 单题答案匹配器，对应 Python 版 AnswerMatcher。答案项支持三种形态：
/// <list type="bullet">
/// <item>standard：单个字符串，如 "C"（也可是 "AB" 表示需同时涂 A、B）。</item>
/// <item>multiple-correct：字符串数组（至少 2 个），如 ["A", "C"]，任一即得分。</item>
/// <item>multiple-correct-weighted：[答案, 分值] 二元组数组，如 [["B", 2], ["C", "3/2"]]。</item>
/// </list>
/// 答案项以 Python 风格对象树表示：string / long / double / List&lt;object?&gt;。
/// </summary>
public sealed class AnswerMatcher
{
    public const string Standard = "standard";
    public const string MultipleCorrect = "multiple-correct";
    public const string MultipleCorrectWeighted = "multiple-correct-weighted";

    public AnswerMatcher(object? answerItem, SectionMarkingScheme sectionMarkingScheme)
    {
        SectionMarkingScheme = sectionMarkingScheme;
        AnswerItem = answerItem;
        AnswerType = ValidateAndGetAnswerType(answerItem);
        EmptyVal = sectionMarkingScheme.EmptyVal;
        Marking = new Dictionary<string, double>(sectionMarkingScheme.Marking);
        SetDefaultsFromScheme();
    }

    public SectionMarkingScheme SectionMarkingScheme { get; }

    public object? AnswerItem { get; }

    /// <summary>答案类型：standard / multiple-correct / multiple-correct-weighted。</summary>
    public string AnswerType { get; }

    public string EmptyVal { get; }

    /// <summary>判定结果 → 分值（含 correct-X 这类针对单个答案的条目）。</summary>
    public Dictionary<string, double> Marking { get; }

    /// <summary>分值元素只能是字符串或整数（与 Python 版类型检查一致，浮点数不允许）。</summary>
    public static bool IsAMarkingScore(object? answerElement) => answerElement is string or long or int;

    /// <summary>标准答案：非空字符串。</summary>
    public static bool IsStandardAnswer(object? answerElement) => answerElement is string { Length: >= 1 };

    /// <summary>该题所在的分区说明：加权题型返回自定义分值表，其余返回分区名。</summary>
    public string GetSectionExplanation() => AnswerType == MultipleCorrectWeighted
        ? $"Custom: {PyFormat.Repr(Marking)}"
        : SectionMarkingScheme.SectionKey;

    /// <summary>
    /// 判定作答结果并返回（判定, 分值）。判定为 unmarked / incorrect / correct / correct-X。
    /// </summary>
    public (string Verdict, double Delta) GetVerdictMarking(string markedAnswer)
    {
        var verdict = AnswerType switch
        {
            Standard => GetStandardVerdict(markedAnswer),
            MultipleCorrect => GetMultipleCorrectVerdict(markedAnswer),
            MultipleCorrectWeighted => GetMultipleCorrectWeightedVerdict(markedAnswer),
            _ => "incorrect",
        };
        return (verdict, Marking[verdict]);
    }

    /// <summary>等价于 Python str(answer_item)，用于解释表。</summary>
    public override string ToString() => PyFormat.Str(AnswerItem);

    /// <summary>
    /// 判断答案项类型。边界：无法识别时记录 critical 日志并抛出异常。
    /// </summary>
    private static string ValidateAndGetAnswerType(object? answerItem)
    {
        if (IsStandardAnswer(answerItem))
        {
            return Standard;
        }

        if (answerItem is List<object?> list)
        {
            if (list.Count >= 2 && list.All(IsStandardAnswer))
            {
                return MultipleCorrect;
            }

            if (list.Count >= 1 && list.All(item =>
                    item is List<object?> { Count: 2 } pair && IsStandardAnswer(pair[0]) && IsAMarkingScore(pair[1])))
            {
                return MultipleCorrectWeighted;
            }
        }

        Logger.Critical($"Unable to determine answer type for answer item: {PyFormat.Str(answerItem)}");
        throw new OmrCheckerException("Unable to determine answer type");
    }

    /// <summary>
    /// 按答案类型补充分值表：多选题每个可接受答案沿用 correct 分值；加权题使用各自给定分值。
    /// </summary>
    private void SetDefaultsFromScheme()
    {
        switch (AnswerType)
        {
            case MultipleCorrect:
                foreach (var allowed in (List<object?>)AnswerItem!)
                {
                    Marking[$"correct-{allowed}"] = Marking["correct"];
                }

                break;
            case MultipleCorrectWeighted:
                foreach (var pair in ((List<object?>)AnswerItem!).Cast<List<object?>>())
                {
                    Marking[$"correct-{pair[0]}"] = ParsingUtils.ParseFloatOrFraction(pair[1]);
                }

                break;
        }
    }

    /// <summary>单答案判定。</summary>
    private string GetStandardVerdict(string markedAnswer)
    {
        if (markedAnswer == EmptyVal)
        {
            return "unmarked";
        }

        return markedAnswer == (string)AnswerItem! ? "correct" : "incorrect";
    }

    /// <summary>多个可接受答案的判定。</summary>
    private string GetMultipleCorrectVerdict(string markedAnswer)
    {
        if (markedAnswer == EmptyVal)
        {
            return "unmarked";
        }

        var allowed = ((List<object?>)AnswerItem!).Cast<string>();
        return allowed.Contains(markedAnswer) ? $"correct-{markedAnswer}" : "incorrect";
    }

    /// <summary>加权答案的判定。</summary>
    private string GetMultipleCorrectWeightedVerdict(string markedAnswer)
    {
        if (markedAnswer == EmptyVal)
        {
            return "unmarked";
        }

        var allowed = ((List<object?>)AnswerItem!).Cast<List<object?>>().Select(p => (string)p[0]!);
        return allowed.Contains(markedAnswer) ? $"correct-{markedAnswer}" : "incorrect";
    }
}
