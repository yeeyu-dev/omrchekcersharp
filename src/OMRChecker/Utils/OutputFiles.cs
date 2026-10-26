using System.Globalization;
using OMRChecker.Core;
using OMRChecker.Logging;

namespace OMRChecker.Utils;

/// <summary>某个输入目录对应的输出目录结构，对应 Python 版 Paths。</summary>
public sealed class OutputPaths
{
    public OutputPaths(string outputDir)
    {
        OutputDir = outputDir;
        SaveMarkedDir = Path.Combine(outputDir, "CheckedOMRs");
        ResultsDir = Path.Combine(outputDir, "Results");
        ManualDir = Path.Combine(outputDir, "Manual");
        EvaluationDir = Path.Combine(outputDir, "Evaluation");
        ErrorsDir = Path.Combine(ManualDir, "ErrorFiles");
        MultiMarkedDir = Path.Combine(ManualDir, "MultiMarkedFiles");
    }

    public string OutputDir { get; }

    /// <summary>识别结果标注图目录。</summary>
    public string SaveMarkedDir { get; }

    /// <summary>结果 CSV 目录。</summary>
    public string ResultsDir { get; }

    /// <summary>需人工复核的文件目录。</summary>
    public string ManualDir { get; }

    /// <summary>评分解释 CSV 目录。</summary>
    public string EvaluationDir { get; }

    public string ErrorsDir { get; }

    public string MultiMarkedDir { get; }
}

/// <summary>
/// 某模板的输出上下文（结果文件路径、表头、空响应等），对应 Python 版 setup_outputs_for_template 返回的 Namespace。
/// </summary>
public sealed class OutputsNamespace
{
    public const string ResultsKey = "Results";
    public const string MultiMarkedKey = "MultiMarked";
    public const string ErrorsKey = "Errors";

    public required OutputPaths Paths { get; init; }

    /// <summary>与输出列等长的空响应，用于错误行。</summary>
    public required List<string> EmptyResp { get; init; }

    /// <summary>CSV 表头：file_id, input_path, output_path, score + 输出列。</summary>
    public required List<string> SheetCols { get; init; }

    /// <summary>本次运行内存中累计的所有结果行。</summary>
    public List<List<string>> OutputSet { get; } = new();

    /// <summary>结果类型 → CSV 文件路径。</summary>
    public required Dictionary<string, string> FilesObj { get; init; }
}

/// <summary>输出目录与结果文件的初始化，对应 Python 版 src/utils/file.py。</summary>
public static class OutputFiles
{
    /// <summary>
    /// 创建输出目录结构（已存在则跳过）：CheckedOMRs（含 stack、_MULTI_/stack）、Manual、Results、Evaluation 等。
    /// </summary>
    public static void SetupDirsForPaths(OutputPaths paths)
    {
        Logger.Info("Checking Directories...");
        if (!Directory.Exists(paths.SaveMarkedDir))
        {
            Logger.Info($"Created : {paths.SaveMarkedDir}");
            Directory.CreateDirectory(Path.Combine(paths.SaveMarkedDir, "stack"));
            Directory.CreateDirectory(Path.Combine(paths.SaveMarkedDir, "_MULTI_", "stack"));
        }

        foreach (var dir in new[] { paths.ManualDir, paths.ResultsDir, paths.EvaluationDir, paths.MultiMarkedDir, paths.ErrorsDir })
        {
            if (!Directory.Exists(dir))
            {
                Logger.Info($"Created : {dir}");
                Directory.CreateDirectory(dir);
            }
        }
    }

    /// <summary>
    /// 准备结果 CSV：文件不存在时写入表头；已存在时追加。
    /// 结果文件名带当前小时（如 Results_05AM.csv），与 Python 版 strftime("%I%p") 一致。
    /// </summary>
    public static OutputsNamespace SetupOutputsForTemplate(OutputPaths paths, Template template)
    {
        Logger.Info("Checking Files...");
        var sheetCols = new List<string> { "file_id", "input_path", "output_path", "score" };
        sheetCols.AddRange(template.OutputColumns);

        var timeNowHrs = DateTime.Now.ToString("hhtt", CultureInfo.InvariantCulture);
        var filesMap = new Dictionary<string, string>
        {
            [OutputsNamespace.ResultsKey] = Path.Combine(paths.ResultsDir, $"Results_{timeNowHrs}.csv"),
            [OutputsNamespace.MultiMarkedKey] = Path.Combine(paths.ManualDir, "MultiMarkedFiles.csv"),
            [OutputsNamespace.ErrorsKey] = Path.Combine(paths.ManualDir, "ErrorFiles.csv"),
        };

        foreach (var fileName in filesMap.Values)
        {
            if (!File.Exists(fileName))
            {
                Logger.Info($"Created new file: '{fileName}'");
                CsvUtils.AppendRow(fileName, sheetCols);
            }
            else
            {
                Logger.Info($"Present : appending to '{fileName}'");
            }
        }

        return new OutputsNamespace
        {
            Paths = paths,
            EmptyResp = Enumerable.Repeat(string.Empty, template.OutputColumns.Count).ToList(),
            SheetCols = sheetCols,
            FilesObj = filesMap,
        };
    }
}
