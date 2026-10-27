using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OMRChecker.Config;
using OMRChecker.Constants;
using OMRChecker.Core;
using OMRChecker.Logging;
using OMRChecker.Utils;

namespace OMRChecker.Evaluation;

/// <summary>
/// 评分配置（evaluation.json），对应 Python 版 EvaluationConfig。同一实例会复用于多张答题卡。
/// 答案来源：source_type = "csv"（答案 CSV，或从答案卡图片识别生成）/ "custom"（JSON 内联）。
/// </summary>
public sealed class EvaluationConfig
{
    private readonly Dictionary<string, SectionMarkingScheme> _questionToScheme = new();
    private readonly Dictionary<string, AnswerMatcher> _questionToAnswerMatcher;
    private readonly SectionMarkingScheme _defaultMarkingScheme = null!;
    private ConsoleTable? _explanationTable;

    /// <summary>
    /// 加载并校验评分配置。
    /// </summary>
    /// <param name="currDir">evaluation.json 所在目录（答案 CSV / 图片路径以此为基准）。</param>
    /// <param name="evaluationPath">evaluation.json 路径。</param>
    /// <param name="template">当前生效的模板（用于空值与从图片生成答案）。</param>
    /// <param name="tuningConfig">当前生效的调优配置。</param>
    public EvaluationConfig(string currDir, string evaluationPath, Template template, TuningConfig tuningConfig)
    {
        Path = evaluationPath;
        var evaluationJson = ParsingUtils.OpenEvaluationWithValidation(evaluationPath);
        var options = evaluationJson["options"]!.AsObject();
        var markingSchemes = evaluationJson["marking_schemes"]!.AsObject();
        var sourceType = evaluationJson["source_type"]!.GetValue<string>();

        ShouldExplainScoring = JsonUtils.GetBool(options["should_explain_scoring"], false);
        EnableEvaluationTableToCsv = JsonUtils.GetBool(options["enable_evaluation_table_to_csv"], false);

        List<object?> answersInOrder;
        if (sourceType == "csv")
        {
            answersInOrder = LoadCsvAnswers(currDir, options, template);
        }
        else
        {
            QuestionsInOrder = ParseQuestionsInOrder(options["questions_in_order"]);
            answersInOrder = options["answers_in_order"]!.AsArray().Select(JsonUtils.ToPlainObject).ToList();
        }

        ValidateQuestions(answersInOrder);

        foreach (var (sectionKey, sectionScheme) in markingSchemes)
        {
            var scheme = new SectionMarkingScheme(sectionKey, sectionScheme!.AsObject(), template.GlobalEmptyVal);
            if (sectionKey != SchemaConstants.DefaultSectionKey)
            {
                SectionMarkingSchemes[sectionKey] = scheme;
                foreach (var q in scheme.Questions!)
                {
                    _questionToScheme[q] = scheme;
                }

                HasNonDefaultSection = true;
            }
            else
            {
                _defaultMarkingScheme = scheme;
            }
        }

        ValidateMarkingSchemes();
        _questionToAnswerMatcher = ParseAnswersAndMapQuestions(answersInOrder);
        ValidateAnswers(answersInOrder, tuningConfig);
    }

    public string Path { get; }

    /// <summary>是否在控制台打印逐题评分解释表。</summary>
    public bool ShouldExplainScoring { get; }

    /// <summary>是否把评分解释表另存为 CSV。</summary>
    public bool EnableEvaluationTableToCsv { get; }

    /// <summary>是否存在 DEFAULT 之外的分区（决定解释表是否显示 Section 列）。</summary>
    public bool HasNonDefaultSection { get; }

    /// <summary>需要排除的文件（保留接口，当前恒为空）。</summary>
    public List<string> ExcludeFiles { get; } = new();

    /// <summary>参与评分的题目，按顺序。</summary>
    public List<string> QuestionsInOrder { get; private set; } = new();

    public Dictionary<string, SectionMarkingScheme> SectionMarkingSchemes { get; } = new();

    public override string ToString() => Path;

    /// <summary>
    /// 评分前校验：答案中的题目必须都在识别结果中；
    /// 识别结果中以 q 开头但未给答案的题目给出警告。同时重置解释表。
    /// </summary>
    public void PrepareAndValidateOmrResponse(IReadOnlyDictionary<string, string> omrResponse)
    {
        ResetExplanationTable();

        var allQuestions = QuestionsInOrder.ToHashSet();
        var missingQuestions = allQuestions.Where(q => !omrResponse.ContainsKey(q))
            .OrderBy(q => q, StringComparer.Ordinal).ToList();
        if (missingQuestions.Count > 0)
        {
            Logger.Critical($"Missing OMR response for: {PyFormat.Repr(missingQuestions)}");
            throw new OmrCheckerException("Some questions are missing in the OMR response for the given answer key");
        }

        var missingPrefixed = omrResponse.Keys.Where(k => k.StartsWith("q", StringComparison.Ordinal) && !allQuestions.Contains(k))
            .OrderBy(q => q, StringComparer.Ordinal).ToList();
        if (missingPrefixed.Count > 0)
        {
            Logger.Warning($"No answer given for potential questions in OMR response: {PyFormat.Repr(missingPrefixed)}");
        }
    }

    /// <summary>对单题评分，返回分值增量，并按需记录解释表。</summary>
    public double MatchAnswerForQuestion(double currentScore, string question, string markedAnswer)
    {
        var answerMatcher = _questionToAnswerMatcher[question];
        var (verdict, delta) = answerMatcher.GetVerdictMarking(markedAnswer);
        ConditionallyAddExplanation(answerMatcher, delta, markedAnswer, verdict, question, currentScore);
        return delta;
    }

    /// <summary>按配置打印解释表。</summary>
    public void ConditionallyPrintExplanation()
    {
        if (ShouldExplainScoring && _explanationTable is not null)
        {
            _explanationTable.Print();
        }
    }

    /// <summary>
    /// 按配置将解释表追加写入 {evaluationOutputDir}/{文件名}_evaluation.csv（含表头）。
    /// </summary>
    public void ConditionallySaveExplanationCsv(string fileName, string evaluationOutputDir)
    {
        if (!EnableEvaluationTableToCsv || _explanationTable is null)
        {
            return;
        }

        var outputPath = System.IO.Path.Combine(evaluationOutputDir,
            $"{System.IO.Path.GetFileNameWithoutExtension(fileName)}_evaluation.csv");
        var rows = new List<IEnumerable<string>> { _explanationTable.Columns };
        rows.AddRange(_explanationTable.Rows);
        CsvUtils.AppendRows(outputPath, rows);
    }

    /// <summary>
    /// 解析答案 CSV 的答案列：以 [ 开头按 Python 字面量解析；含逗号按逗号拆分为多选；否则为单答案。
    /// </summary>
    public static object? ParseAnswerColumn(string answerColumn)
    {
        if (answerColumn.Length == 0)
        {
            throw new OmrCheckerException("Found an empty answer in the answer key csv");
        }

        if (answerColumn[0] == '[')
        {
            return PyLiteralParser.Parse(answerColumn);
        }

        return answerColumn.Contains(',')
            ? answerColumn.Split(',').Cast<object?>().ToList()
            : answerColumn;
    }

    /// <summary>
    /// 读取 CSV 来源的答案；CSV 不存在但配置了 answer_key_image_path 时，从答案卡图片识别生成答案。
    /// </summary>
    private List<object?> LoadCsvAnswers(string currDir, JsonObject options, Template template)
    {
        var csvPath = System.IO.Path.Combine(currDir, options["answer_key_csv_path"]!.GetValue<string>());
        if (!File.Exists(csvPath))
        {
            Logger.Warning($"Answer key csv does not exist at: '{csvPath}'.");
        }

        var answerKeyImagePath = JsonUtils.GetString(options["answer_key_image_path"]);
        if (File.Exists(csvPath))
        {
            var rows = CsvUtils.Parse(File.ReadAllText(csvPath));
            QuestionsInOrder = rows.Select(r => r[0]).ToList();
            // 多于两列时（未加引号的多选答案），其余列按逗号重新拼回
            return rows.Select(r => ParseAnswerColumn(r.Count > 2 ? string.Join(",", r.Skip(1)) : r.ElementAtOrDefault(1) ?? string.Empty))
                .ToList();
        }

        if (string.IsNullOrEmpty(answerKeyImagePath))
        {
            throw new OmrCheckerException($"Answer key csv not found at '{csvPath}'");
        }

        var imagePath = System.IO.Path.Combine(currDir, answerKeyImagePath);
        if (!File.Exists(imagePath))
        {
            throw new OmrCheckerException($"Answer key image not found at '{imagePath}'");
        }

        Logger.Debug($"Attempting to generate answer key from image: '{imagePath}'");
        var inOmr = ImageUtils.ImReadGray(imagePath)
                    ?? throw new OmrCheckerException($"Could not read answer key from image {imagePath}");
        var processed = template.ImageInstanceOps.ApplyPreprocessors(imagePath, inOmr, template)
                        ?? throw new OmrCheckerException($"Could not read answer key from image {imagePath}");
        var result = template.ImageInstanceOps.ReadOmrResponse(template, processed, imagePath, saveDir: null);
        var omrResponse = ParsingUtils.GetConcatenatedResponse(result.Response, template);

        var emptyVal = template.GlobalEmptyVal;
        var emptyAnswerRegex = new Regex(emptyVal != string.Empty ? $"{Regex.Escape(emptyVal)}+" : "^$");

        if (options["questions_in_order"] is not null)
        {
            QuestionsInOrder = ParseQuestionsInOrder(options["questions_in_order"]);
            var emptyAnswered = QuestionsInOrder
                .Where(q => emptyAnswerRegex.IsMatch(omrResponse.GetValueOrDefault(q) ?? string.Empty)).ToList();
            if (emptyAnswered.Count > 0)
            {
                Logger.Error(
                    $"Found empty answers for questions: {PyFormat.Repr(emptyAnswered)}, empty value used: '{emptyVal}'");
                throw new OmrCheckerException(
                    $"Found empty answers in file '{imagePath}'. Please check your template again in the --setLayout mode.");
            }
        }
        else
        {
            Logger.Warning("questions_in_order not provided, proceeding to use non-empty values as answer key");
            QuestionsInOrder = omrResponse.Where(kv => !emptyAnswerRegex.IsMatch(kv.Value))
                .Select(kv => kv.Key).OrderBy(q => q, StringComparer.Ordinal).ToList();
        }

        return QuestionsInOrder.Select(q => (object?)omrResponse[q]).ToList();
    }

    /// <summary>展开 questions_in_order（支持区间写法）。</summary>
    private static List<string> ParseQuestionsInOrder(JsonNode? questionsInOrder) =>
        ParsingUtils.ParseFields("questions_in_order", JsonUtils.GetStringList(questionsInOrder));

    /// <summary>
    /// 开启 filter_out_multimarked_files 时，答案中不允许出现多字符（即多涂）答案，否则抛出异常。
    /// </summary>
    private void ValidateAnswers(List<object?> answersInOrder, TuningConfig tuningConfig)
    {
        if (!tuningConfig.Outputs.FilterOutMultimarkedFiles)
        {
            return;
        }

        for (var i = 0; i < QuestionsInOrder.Count; i++)
        {
            var answerItem = answersInOrder[i];
            var multiMarkedAnswer = _questionToAnswerMatcher[QuestionsInOrder[i]].AnswerType switch
            {
                AnswerMatcher.Standard => ((string)answerItem!).Length > 1,
                AnswerMatcher.MultipleCorrect => ((List<object?>)answerItem!).Any(a => ((string)a!).Length > 1),
                AnswerMatcher.MultipleCorrectWeighted =>
                    ((List<object?>)answerItem!).Cast<List<object?>>().Any(p => ((string)p[0]!).Length > 1),
                _ => false,
            };

            if (multiMarkedAnswer)
            {
                throw new OmrCheckerException(
                    "Provided answer key contains multiple correct answer(s), but config.filter_out_multimarked_files is True. Scoring will get skipped.");
            }
        }
    }

    /// <summary>题目数与答案数必须一致。</summary>
    private void ValidateQuestions(List<object?> answersInOrder)
    {
        if (QuestionsInOrder.Count != answersInOrder.Count)
        {
            Logger.Critical(
                $"questions_in_order({QuestionsInOrder.Count}): {PyFormat.Repr(QuestionsInOrder)}\nanswers_in_order({answersInOrder.Count}): {PyFormat.Repr(answersInOrder)}");
            throw new OmrCheckerException(
                $"Unequal lengths for questions_in_order and answers_in_order ({QuestionsInOrder.Count} != {answersInOrder.Count})");
        }
    }

    /// <summary>分区之间题目不能重叠；分区引用的题目必须都在答案中。</summary>
    private void ValidateMarkingSchemes()
    {
        var sectionQuestions = new HashSet<string>();
        foreach (var (sectionKey, scheme) in SectionMarkingSchemes)
        {
            if (sectionKey == SchemaConstants.DefaultSectionKey)
            {
                continue;
            }

            if (scheme.Questions!.Any(sectionQuestions.Contains))
            {
                throw new OmrCheckerException($"Section '{sectionKey}' has overlapping question(s) with other sections");
            }

            sectionQuestions.UnionWith(scheme.Questions!);
        }

        var allQuestions = QuestionsInOrder.ToHashSet();
        var missing = sectionQuestions.Where(q => !allQuestions.Contains(q)).OrderBy(q => q, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            Logger.Critical($"Missing answer key for: {PyFormat.Repr(missing)}");
            throw new OmrCheckerException("Some questions are missing in the answer key for the given marking scheme");
        }
    }

    /// <summary>为每道题创建答案匹配器。加权答案会忽略所在自定义分区的分值并给出警告。</summary>
    private Dictionary<string, AnswerMatcher> ParseAnswersAndMapQuestions(List<object?> answersInOrder)
    {
        var map = new Dictionary<string, AnswerMatcher>();
        for (var i = 0; i < QuestionsInOrder.Count; i++)
        {
            var question = QuestionsInOrder[i];
            var scheme = GetMarkingSchemeForQuestion(question);
            var matcher = new AnswerMatcher(answersInOrder[i], scheme);
            map[question] = matcher;
            if (matcher.AnswerType == AnswerMatcher.MultipleCorrectWeighted
                && scheme.SectionKey != SchemaConstants.DefaultSectionKey)
            {
                Logger.Warning(
                    $"The custom scheme '{scheme}' will not apply to question '{question}' as it will use the given answer weights f{PyFormat.Str(answersInOrder[i])}");
            }
        }

        return map;
    }

    /// <summary>重新创建解释表（表头：Question / Marked / Answer(s) / Verdict / Delta / Score [/ Section]）。</summary>
    private void ResetExplanationTable()
    {
        _explanationTable = null;
        // Python 版仅在 should_explain_scoring 时建表，导致单独开启 CSV 导出会报错；这里两者任一开启即建表
        if (!ShouldExplainScoring && !EnableEvaluationTableToCsv)
        {
            return;
        }

        var table = new ConsoleTable("Evaluation Explanation Table", showLines: true);
        foreach (var column in new[] { "Question", "Marked", "Answer(s)", "Verdict", "Delta", "Score" })
        {
            table.AddColumn(column);
        }

        if (HasNonDefaultSection)
        {
            table.AddColumn("Section");
        }

        _explanationTable = table;
    }

    /// <summary>取题目所属分区，未指定时为 DEFAULT。</summary>
    private SectionMarkingScheme GetMarkingSchemeForQuestion(string question) =>
        _questionToScheme.GetValueOrDefault(question, _defaultMarkingScheme);

    /// <summary>向解释表追加一行。</summary>
    private void ConditionallyAddExplanation(AnswerMatcher answerMatcher, double delta, string markedAnswer,
        string verdict, string question, double currentScore)
    {
        if (_explanationTable is null)
        {
            return;
        }

        var nextScore = currentScore + delta;
        var row = new List<string>
        {
            question,
            markedAnswer,
            answerMatcher.ToString(),
            PyFormat.Title(verdict),
            PyFormat.Float(PyFormat.Round(delta, 2)),
            PyFormat.Float(PyFormat.Round(nextScore, 2)),
        };
        if (HasNonDefaultSection)
        {
            row.Add(answerMatcher.GetSectionExplanation());
        }

        _explanationTable.AddRow(row.ToArray());
    }
}

/// <summary>评分入口，对应 Python 版 evaluate_concatenated_response。</summary>
public static class Evaluator
{
    /// <summary>
    /// 对一张答题卡的（已拼接）识别结果按题目顺序累加得分，并按需输出解释表。
    /// </summary>
    /// <returns>总分。</returns>
    public static double EvaluateConcatenatedResponse(IReadOnlyDictionary<string, string> concatenatedResponse,
        EvaluationConfig evaluationConfig, string fileName, string evaluationOutputDir)
    {
        evaluationConfig.PrepareAndValidateOmrResponse(concatenatedResponse);
        var currentScore = 0.0;
        foreach (var question in evaluationConfig.QuestionsInOrder)
        {
            var markedAnswer = concatenatedResponse[question];
            currentScore += evaluationConfig.MatchAnswerForQuestion(currentScore, question, markedAnswer);
        }

        evaluationConfig.ConditionallyPrintExplanation();
        evaluationConfig.ConditionallySaveExplanationCsv(fileName, evaluationOutputDir);
        return currentScore;
    }
}
