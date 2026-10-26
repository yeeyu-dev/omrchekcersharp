using System.Diagnostics;
using OMRChecker.Config;
using OMRChecker.Constants;
using OMRChecker.Core;
using OMRChecker.Evaluation;
using OMRChecker.Logging;
using OMRChecker.Processors;
using OMRChecker.Utils;
using OpenCvSharp;

namespace OMRChecker;

/// <summary>命令行参数，对应 Python 版 argparse 结果。</summary>
public sealed class CliArgs
{
    /// <summary>输入目录列表，默认 ["inputs"]。</summary>
    public List<string> InputPaths { get; set; } = new() { "inputs" };

    /// <summary>
    /// 为 true 时打印完整异常堆栈。
    /// 说明：Python 版 -d 为 store_false（默认隐藏堆栈，加 -d 显示），语义相同。
    /// </summary>
    public bool Debug { get; set; }

    /// <summary>输出目录，默认 "outputs"。</summary>
    public string OutputDir { get; set; } = "outputs";

    /// <summary>
    /// 开启自动对齐（覆盖 config.json 的 alignment_params.auto_align）。
    /// 说明：Python 版虽然解析了 -a，但实际并未使用；C# 版按帮助文本实现。
    /// </summary>
    public bool AutoAlign { get; set; }

    /// <summary>布局调试模式：只显示模板叠加效果，不识别、不评分。</summary>
    public bool SetLayout { get; set; }
}

/// <summary>
/// 处理入口，对应 Python 版 src/entry.py：递归遍历输入目录，
/// 子目录继承上级的 template.json / config.json / evaluation.json，并可在本级覆盖。
/// </summary>
public static class Entry
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".pdf" };
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly Stats Stats = new();

    /// <summary>
    /// 处理一个输入根目录。
    /// </summary>
    /// <exception cref="OmrCheckerException">目录不存在，或目录树中有图片却没有模板。</exception>
    public static void EntryPoint(string inputDir, CliArgs args)
    {
        if (!Directory.Exists(inputDir))
        {
            throw new OmrCheckerException($"Given input directory does not exist: '{inputDir}'");
        }

        var root = NormalizeDir(inputDir);
        var tuningConfig = TuningConfig.CreateDefault();
        ApplyCliOverrides(tuningConfig, args);
        ProcessDir(root, root, args, null, tuningConfig, null);
    }

    /// <summary>
    /// 递归处理目录：加载本级配置/模板/评分 → 识别本级图片 → 递归子目录。
    /// </summary>
    private static void ProcessDir(string rootDir, string currDir, CliArgs args, Template? template,
        TuningConfig tuningConfig, EvaluationConfig? evaluationConfig)
    {
        // 更新本级调优配置（只影响当前递归栈）
        var localConfigPath = Path.Combine(currDir, CommonConstants.ConfigFilename);
        var localConfigExists = File.Exists(localConfigPath);
        if (localConfigExists)
        {
            tuningConfig = ParsingUtils.OpenConfigWithDefaults(localConfigPath);
            ApplyCliOverrides(tuningConfig, args);
        }

        // 更新本级模板
        var localTemplatePath = Path.Combine(currDir, CommonConstants.TemplateFilename);
        var localTemplateExists = File.Exists(localTemplatePath);
        if (localTemplateExists)
        {
            template = new Template(localTemplatePath, tuningConfig);
        }

        var subdirs = Directory.GetDirectories(currDir)
            .OrderBy(d => d, StringComparer.FromComparison(PathComparison))
            .ToList();

        var relative = Path.GetRelativePath(rootDir, currDir);
        var outputDir = relative == "." ? args.OutputDir : Path.Combine(args.OutputDir, relative);
        var paths = new OutputPaths(outputDir);

        // 本级待处理图片
        var omrFiles = Directory.GetFiles(currDir)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.FromComparison(PathComparison))
            .ToList();

        // 排除预处理器使用的文件（标记图、参考图等）
        var excludedFiles = new List<string>();
        if (template is not null)
        {
            foreach (var pp in template.PreProcessors)
            {
                excludedFiles.AddRange(pp.ExcludeFiles());
            }
        }

        var localEvaluationPath = Path.Combine(currDir, CommonConstants.EvaluationFilename);
        if (!args.SetLayout && File.Exists(localEvaluationPath))
        {
            if (!localTemplateExists)
            {
                Logger.Warning($"Found an evaluation file without a parent template file: {localEvaluationPath}");
            }

            if (template is null)
            {
                throw new OmrCheckerException($"No template file found in the directory tree of {currDir}");
            }

            evaluationConfig = new EvaluationConfig(currDir, localEvaluationPath, template, tuningConfig);
            excludedFiles.AddRange(evaluationConfig.ExcludeFiles);
        }

        var excludedFull = excludedFiles.Select(Path.GetFullPath).ToList();
        omrFiles = omrFiles
            .Where(f => !excludedFull.Any(e => string.Equals(e, Path.GetFullPath(f), PathComparison)))
            .ToList();

        if (omrFiles.Count > 0)
        {
            if (template is null)
            {
                Logger.Error(
                    $"Found images, but no template in the directory tree of '{currDir}'. \nPlace {CommonConstants.TemplateFilename} in the appropriate directory.");
                throw new OmrCheckerException($"No template file found in the directory tree of {currDir}");
            }

            OutputFiles.SetupDirsForPaths(paths);
            var outputsNamespace = OutputFiles.SetupOutputsForTemplate(paths, template);

            PrintConfigSummary(currDir, omrFiles, template, tuningConfig, localConfigExists ? localConfigPath : null,
                evaluationConfig, args);
            if (args.SetLayout)
            {
                ShowTemplateLayouts(omrFiles, template, tuningConfig, outputsNamespace);
            }
            else
            {
                ProcessFiles(omrFiles, template, tuningConfig, evaluationConfig, outputsNamespace);
            }
        }
        else if (subdirs.Count == 0)
        {
            // 叶子目录必须包含图片
            Logger.Info($"No valid images or sub-folders found in {currDir}. Empty directories not allowed.");
        }

        // 递归处理子目录
        foreach (var d in subdirs)
        {
            ProcessDir(rootDir, d, args, template, tuningConfig, evaluationConfig);
        }
    }

    /// <summary>打印当前目录生效的配置汇总表。</summary>
    private static void PrintConfigSummary(string currDir, List<string> omrFiles, Template template,
        TuningConfig tuningConfig, string? localConfigPath, EvaluationConfig? evaluationConfig, CliArgs args)
    {
        Logger.Info(string.Empty);
        var table = new ConsoleTable("Current Configurations", showHeader: false);
        table.AddColumn("Key");
        table.AddColumn("Value");
        table.AddRow("Directory Path", currDir);
        table.AddRow("Count of Images", omrFiles.Count.ToString());
        table.AddRow("Set Layout Mode ", args.SetLayout ? "ON" : "OFF");
        var preProcessorNames = template.PreProcessors.Select(pp => pp.GetType().Name).ToList();
        table.AddRow("Markers Detection", preProcessorNames.Contains(nameof(CropOnMarkers)) ? "ON" : "OFF");
        table.AddRow("Auto Alignment", tuningConfig.AlignmentParams.AutoAlign ? "True" : "False");
        table.AddRow("Detected Template Path", template.ToString());
        if (localConfigPath is not null)
        {
            table.AddRow("Detected Local Config", localConfigPath);
        }

        if (evaluationConfig is not null)
        {
            table.AddRow("Detected Evaluation Config", evaluationConfig.ToString());
        }

        table.AddRow("Detected pre-processors", string.Join(", ", preProcessorNames));
        table.Print();
    }

    /// <summary>布局调试模式：依次显示每张图叠加模板后的效果。</summary>
    private static void ShowTemplateLayouts(List<string> omrFiles, Template template, TuningConfig tuningConfig,
        OutputsNamespace outputsNamespace)
    {
        foreach (var filePath in omrFiles)
        {
            foreach (var (imgName, image) in ImageUtils.LoadOmrImage(filePath, tuningConfig))
            {
                var inOmr = template.ImageInstanceOps.ApplyPreprocessors(filePath, image, template);
                if (inOmr is null)
                {
                    RecordErrorFile(filePath, imgName, outputsNamespace);
                    continue;
                }

                var templateLayout = ImageInstanceOps.DrawTemplateLayout(inOmr, template, shifted: false, border: 2);
                InteractionUtils.Show($"Template Layout: {imgName}", templateLayout, pause: true, resize: true,
                    config: tuningConfig);
            }
        }
    }

    /// <summary>
    /// 识别单张图像并写入结果：预处理失败 → ErrorFiles.csv；
    /// 多涂且开启 filter_out_multimarked_files → MultiMarkedFiles.csv；否则 → Results_XX.csv。
    /// </summary>
    private static void ProcessSingleImage(string filePath, string imgName, Mat inOmr, Template template,
        TuningConfig tuningConfig, EvaluationConfig? evaluationConfig, OutputsNamespace outputsNamespace,
        int filesCounter)
    {
        Logger.Info(string.Empty);
        Logger.Info($"({filesCounter}) Opening image: \t'{imgName}'\tResolution: ({inOmr.Rows}, {inOmr.Cols})");

        var ops = template.ImageInstanceOps;
        ops.ResetAllSaveImg();
        ops.AppendSaveImg(1, inOmr);

        var processed = ops.ApplyPreprocessors(imgName, inOmr, template);
        if (processed is null)
        {
            RecordErrorFile(filePath, imgName, outputsNamespace);
            return;
        }

        var fileId = imgName;
        var saveDir = outputsNamespace.Paths.SaveMarkedDir;
        var result = ops.ReadOmrResponse(template, processed, fileId, saveDir);

        // 拼接多列字段（如学号各位）、填充未作答值
        var omrResponse = ParsingUtils.GetConcatenatedResponse(result.Response, template);

        if (evaluationConfig is null || !evaluationConfig.ShouldExplainScoring)
        {
            Logger.Info($"Read Response: \n{PyFormat.Repr(omrResponse)}");
        }

        // 分数：未配置评分时为整数 0（CSV 中写 "0"），否则为浮点（如 "5.0"）
        string scoreText;
        if (evaluationConfig is not null)
        {
            var score = Evaluator.EvaluateConcatenatedResponse(omrResponse, evaluationConfig, imgName,
                outputsNamespace.Paths.EvaluationDir);
            Logger.Info(
                $"(/{filesCounter}) Graded with score: {PyFormat.Float(PyFormat.Round(score, 2))}\t for file: '{fileId}'");
            scoreText = PyFormat.Float(score);
        }
        else
        {
            Logger.Info($"(/{filesCounter}) Processed file: '{fileId}'");
            scoreText = "0";
        }

        if (tuningConfig.Outputs.ShowImageLevel >= 2)
        {
            InteractionUtils.Show($"Final Marked Bubbles : '{fileId}'",
                ImageUtils.ResizeUtilH(result.FinalMarked, (int)(tuningConfig.Dimensions.DisplayHeight * 1.3)),
                pause: true, resize: true, config: tuningConfig);
        }

        var respArray = template.OutputColumns.Select(k => omrResponse[k]).ToList();
        outputsNamespace.OutputSet.Add(new List<string> { imgName }.Concat(respArray).ToList());

        if (!result.MultiMarked || !tuningConfig.Outputs.FilterOutMultimarkedFiles)
        {
            Stats.FilesNotMoved++;
            var newFilePath = Path.Combine(saveDir, fileId);
            CsvUtils.AppendRow(outputsNamespace.FilesObj[OutputsNamespace.ResultsKey],
                new[] { imgName, filePath, newFilePath, scoreText }.Concat(respArray));
        }
        else
        {
            Logger.Info($"[{filesCounter}] Found multi-marked file: '{fileId}'");
            var newFilePath = Path.Combine(outputsNamespace.Paths.MultiMarkedDir, imgName);
            if (CheckAndMove(CommonConstants.MultiBubbleWarn, filePath, newFilePath))
            {
                CsvUtils.AppendRow(outputsNamespace.FilesObj[OutputsNamespace.MultiMarkedKey],
                    new[] { imgName, filePath, newFilePath, "NA" }.Concat(respArray));
            }
        }
    }

    /// <summary>预处理失败时写入错误表。</summary>
    private static void RecordErrorFile(string filePath, string imgName, OutputsNamespace outputsNamespace)
    {
        var newFilePath = Path.Combine(outputsNamespace.Paths.ErrorsDir, imgName);
        outputsNamespace.OutputSet.Add(new List<string> { imgName }.Concat(outputsNamespace.EmptyResp).ToList());
        if (CheckAndMove(CommonConstants.NoMarkerErr, filePath, newFilePath))
        {
            CsvUtils.AppendRow(outputsNamespace.FilesObj[OutputsNamespace.ErrorsKey],
                new[] { imgName, filePath, newFilePath, "NA" }.Concat(outputsNamespace.EmptyResp));
        }
    }

    /// <summary>依次处理本级所有图片（PDF 会展开为多页），最后打印统计。</summary>
    private static void ProcessFiles(List<string> omrFiles, Template template, TuningConfig tuningConfig,
        EvaluationConfig? evaluationConfig, OutputsNamespace outputsNamespace)
    {
        var stopwatch = Stopwatch.StartNew();
        var filesCounter = 0;
        Stats.FilesNotMoved = 0;

        // 收集非 PDF 文件名，用于检测 PDF 页面命名冲突
        var imageFileNames = omrFiles
            .Where(f => !Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToHashSet();

        foreach (var filePath in omrFiles)
        {
            var isPdf = Path.GetExtension(filePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
            foreach (var (imgName, inOmr) in ImageUtils.LoadOmrImage(filePath, tuningConfig))
            {
                if (isPdf && imageFileNames.Contains(imgName))
                {
                    Logger.Warning(
                        $"PDF page name '{imgName}' collides with an existing image file, output may be overwritten.");
                }

                filesCounter++;
                ProcessSingleImage(filePath, imgName, inOmr, template, tuningConfig, evaluationConfig,
                    outputsNamespace, filesCounter);
                inOmr.Dispose();
            }
        }

        PrintStats(stopwatch.Elapsed.TotalSeconds, filesCounter, tuningConfig);
    }

    /// <summary>
    /// 文件移动占位（Python 版同样尚未实现移动，仅计数）。
    /// </summary>
    private static bool CheckAndMove(int errorCode, string filePath, string newFilePath)
    {
        Stats.FilesNotMoved++;
        return true;
    }

    /// <summary>打印处理统计与耗时。</summary>
    private static void PrintStats(double elapsedSeconds, int filesCounter, TuningConfig tuningConfig)
    {
        var timeChecking = Math.Max(1, Math.Round(elapsedSeconds, 2));
        Logger.Info(string.Empty);
        Logger.Info($"{"Total file(s) moved",-27}: {Stats.FilesMoved}");
        Logger.Info($"{"Total file(s) not moved",-27}: {Stats.FilesNotMoved}");
        Logger.Info("--------------------------------");
        var tallied = filesCounter == Stats.FilesMoved + Stats.FilesNotMoved ? "Sum Tallied!" : "Not Tallying!";
        Logger.Info($"{"Total file(s) processed",-27}: {filesCounter} ({tallied})");

        if (tuningConfig.Outputs.ShowImageLevel <= 0)
        {
            Logger.Info(
                $"\nFinished Checking {filesCounter} file(s) in {Math.Round(timeChecking, 1)} seconds i.e. ~{Math.Round(timeChecking / 60, 1)} minute(s).");
            if (filesCounter > 0)
            {
                Logger.Info($"{"OMR Processing Rate",-27}: \t ~ {Math.Round(timeChecking / filesCounter, 2)} seconds/OMR");
                Logger.Info($"{"OMR Processing Speed",-27}: \t ~ {Math.Round(filesCounter * 60 / timeChecking, 2)} OMRs/minute");
            }
        }
        else
        {
            Logger.Info($"\n{"Total script time",-27}: {timeChecking} seconds");
        }

        if (tuningConfig.Outputs.ShowImageLevel <= 1)
        {
            Logger.Info("\nTip: To see some awesome visuals, open config.json and increase 'show_image_level'");
        }
    }

    /// <summary>应用命令行对配置的覆盖（目前只有 -a 自动对齐）。</summary>
    private static void ApplyCliOverrides(TuningConfig tuningConfig, CliArgs args)
    {
        if (args.AutoAlign)
        {
            tuningConfig.AlignmentParams.AutoAlign = true;
        }
    }

    /// <summary>规范化目录路径：统一分隔符并去掉末尾分隔符（与 Python pathlib 输出一致）。</summary>
    private static string NormalizeDir(string dir)
    {
        var normalized = dir.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var trimmed = normalized.TrimEnd(Path.DirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? normalized : trimmed;
    }
}
