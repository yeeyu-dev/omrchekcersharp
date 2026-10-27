using System.Text.RegularExpressions;
using OMRChecker.Utils;
using Xunit;

namespace OMRChecker.Tests;

/// <summary>
/// 端到端回归测试：运行 samples/ 下的样例，与 Python 版 pytest 快照
/// （由 Python 版 pytest 生成，已迁移至 tests/OMRChecker.Tests/TestData/test_all_samples.ambr）中的 CSV 输出逐行比对。
/// 只比对快照中存在的文件；快照生成后新增的输出（如 PDF 样例）不在比对范围内。
/// </summary>
public sealed class SampleSnapshotTests
{
    private const string OutputRoot = "outputs-csharp-test";
    private static readonly Regex ResultsFileRegex = new(@"Results_\d{2}[AP]M", RegexOptions.Compiled);
    private static readonly Regex PdfRowRegex = new(@"^""[^""]*"",""[^""]*\.pdf"",", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>快照测试名 → 样例目录。</summary>
    public static TheoryData<string, string> Samples => new()
    {
        { "test_run_answer_key_using_csv", "answer-key/using-csv" },
        { "test_run_answer_key_weighted_answers", "answer-key/weighted-answers" },
        { "test_run_sample1", "sample1" },
        { "test_run_sample2", "sample2" },
        { "test_run_sample3", "sample3" },
        { "test_run_sample4", "sample4" },
        { "test_run_sample5", "sample5" },
        { "test_run_sample6", "sample6" },
        { "test_run_community_Antibodyy", "community/Antibodyy" },
        { "test_run_community_ibrahimkilic", "community/ibrahimkilic" },
        { "test_run_community_Sandeep_1507", "community/Sandeep-1507" },
        { "test_run_community_Shamanth", "community/Shamanth" },
        { "test_run_community_UmarFarootAPS", "community/UmarFarootAPS" },
        { "test_run_community_UPSC_mock", "community/UPSC-mock" },
        { "test_run_community_celin_mampilly", "community/celin-mampilly" },
    };

    /// <summary>运行样例并与 Python 快照比对 CSV 内容。</summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void SampleOutputMatchesPythonSnapshot(string snapshotName, string samplePath)
    {
        TestHelpers.EnterRepoRoot();
        var expected = LoadSnapshot(snapshotName);
        Assert.NotEmpty(expected);

        var outputDir = Path.Combine(OutputRoot, samplePath);
        if (Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, recursive: true);
        }

        try
        {
            var args = new CliArgs { InputPaths = new() { $"samples/{samplePath}" }, OutputDir = outputDir };
            Entry.EntryPoint($"samples/{samplePath}", args);

            var actual = Directory.GetFiles(outputDir, "*.csv", SearchOption.AllDirectories)
                .ToDictionary(
                    f => NormalizeKey(NetFxCompat.GetRelativePath(outputDir, f)),
                    f => NormalizeLines(File.ReadAllText(f)));

            foreach (var (file, expectedLines) in expected)
            {
                Assert.True(actual.ContainsKey(file), $"缺少输出文件: {file}");
                // 快照生成时尚不支持 PDF：来源为 .pdf 且快照中没有的行不参与比对
                var actualLines = actual[file]
                    .Where(l => expectedLines.Contains(l) || !PdfRowRegex.IsMatch(l))
                    .ToList();
                Assert.Equal(expectedLines, actualLines);
            }
        }
        finally
        {
            // 测试输出根目录仅供本测试使用，整体删除
            if (Directory.Exists(OutputRoot))
            {
                Directory.Delete(OutputRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// 解析 syrupy 的 .ambr 快照：取出指定测试下每个 CSV 文件的内容行。
    /// </summary>
    private static Dictionary<string, List<string>> LoadSnapshot(string testName)
    {
        var path = TestHelpers.TestDataPath("test_all_samples.ambr");
        var lines = File.ReadAllLines(path);
        var result = new Dictionary<string, List<string>>();
        var inTest = false;
        string? currentKey = null;
        var buffer = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("# name: ", StringComparison.Ordinal))
            {
                inTest = line == $"# name: {testName}";
                continue;
            }

            if (!inTest)
            {
                continue;
            }

            var keyMatch = Regex.Match(line, @"^    '(.+)': '''$");
            if (keyMatch.Success)
            {
                currentKey = NormalizeKey(keyMatch.Groups[1].Value);
                buffer = new List<string>();
                continue;
            }

            if (currentKey is not null && line == "    ''',")
            {
                result[currentKey] = NormalizeLines(string.Join("\n", buffer));
                currentKey = null;
                continue;
            }

            if (currentKey is not null)
            {
                buffer.Add(line.Length >= 6 ? line[6..] : line.Trim());
            }
        }

        return result;
    }

    /// <summary>规范化文件键：统一分隔符为 /，结果文件名中的小时替换为占位符。</summary>
    private static string NormalizeKey(string key) =>
        ResultsFileRegex.Replace(key.Replace("\\\\", "/").Replace('\\', '/'), "Results_HH");

    /// <summary>规范化内容：统一换行与路径分隔符、替换测试输出根目录，并去掉空行。</summary>
    private static List<string> NormalizeLines(string content) =>
        content.Replace("\r\n", "\n")
            .Replace("\\\\", "/")
            .Replace('\\', '/')
            .Replace($"{OutputRoot}/", "outputs/")
            .Split('\n')
            .Where(l => l.Trim().Length > 0)
            .ToList();
}
