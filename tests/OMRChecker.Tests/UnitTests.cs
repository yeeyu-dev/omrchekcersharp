using OMRChecker.Utils;
using Xunit;

namespace OMRChecker.Tests;

/// <summary>字段串解析、排序与分值解析的单元测试。</summary>
public sealed class ParsingUtilsTests
{
    [Fact]
    public void ParseFieldString_ExpandsInclusiveRange()
    {
        Assert.Equal(new[] { "q1", "q2", "q3" }, ParsingUtils.ParseFieldString("q1..3"));
        Assert.Equal(new[] { "roll8", "roll9", "roll10" }, ParsingUtils.ParseFieldString("roll8...10"));
        Assert.Equal(new[] { "Medium" }, ParsingUtils.ParseFieldString("Medium"));
    }

    [Fact]
    public void ParseFieldString_RejectsNonIncreasingRange()
    {
        var e = Assert.Throws<OmrCheckerException>(() => ParsingUtils.ParseFieldString("q5..5"));
        Assert.Contains("start: 5 is not less than end: 5", e.Message);
    }

    [Fact]
    public void ParseFields_RejectsOverlap()
    {
        Assert.Throws<OmrCheckerException>(() => ParsingUtils.ParseFields("test", new[] { "q1..5", "q5" }));
    }

    [Fact]
    public void CompareOutputColumns_IsNaturalSort()
    {
        var columns = new List<string> { "q10", "Roll", "q2", "q1", "Booklet_No" };
        columns.Sort(ParsingUtils.CompareOutputColumns);
        // 与 Python 的序数比较一致：大写字母排在小写字母之前
        Assert.Equal(new[] { "Booklet_No", "Roll", "q1", "q2", "q10" }, columns);
    }

    [Theory]
    [InlineData("3/2", 1.5)]
    [InlineData("-1/2", -0.5)]
    [InlineData("3", 3.0)]
    [InlineData("-1", -1.0)]
    public void ParseFloatOrFraction_ParsesStrings(string input, double expected) =>
        Assert.Equal(expected, ParsingUtils.ParseFloatOrFraction(input));

    [Fact]
    public void ParseFloatOrFraction_ParsesNumbers() => Assert.Equal(2.0, ParsingUtils.ParseFloatOrFraction(2L));
}

/// <summary>Python 输出格式复刻的单元测试。</summary>
public sealed class PyFormatTests
{
    [Theory]
    [InlineData(5.0, "5.0")]
    [InlineData(10.0, "10.0")]
    [InlineData(5.5, "5.5")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(0.1 + 0.2, "0.30000000000000004")]
    [InlineData(1e-5, "1e-05")]
    public void Float_MatchesPythonRepr(double value, string expected) => Assert.Equal(expected, PyFormat.Float(value));

    [Fact]
    public void Repr_FormatsNestedLists()
    {
        var item = new List<object?> { new List<object?> { "B", 2L }, new List<object?> { "C", "3/2" } };
        Assert.Equal("[['B', 2], ['C', '3/2']]", PyFormat.Repr(item));
        Assert.Equal("C", PyFormat.Str("C"));
    }

    [Theory]
    [InlineData("correct-AB", "Correct-Ab")]
    [InlineData("incorrect", "Incorrect")]
    public void Title_MatchesPython(string input, string expected) => Assert.Equal(expected, PyFormat.Title(input));
}

/// <summary>Python 字面量解析器与 CSV 解析的单元测试。</summary>
public sealed class LiteralAndCsvTests
{
    [Fact]
    public void PyLiteralParser_ParsesWeightedAnswers()
    {
        var parsed = PyLiteralParser.Parse("[['A', 1], [\"B\", '3/2'], ['C', -2.5]]");
        Assert.Equal("[['A', 1], ['B', '3/2'], ['C', -2.5]]", PyFormat.Repr(parsed));
    }

    [Fact]
    public void PyLiteralParser_RejectsTrailingGarbage() =>
        Assert.Throws<FormatException>(() => PyLiteralParser.Parse("['A'] x"));

    [Fact]
    public void Csv_RoundTripsQuotedFields()
    {
        var row = CsvUtils.FormatRow(new[] { "q1", "['A', \"B\"]", "a,b" });
        Assert.Equal("\"q1\",\"['A', \"\"B\"\"]\",\"a,b\"", row);
        var parsed = CsvUtils.Parse(row + "\n\nq2,C\n");
        Assert.Equal(2, parsed.Count);
        Assert.Equal(new[] { "q1", "['A', \"B\"]", "a,b" }, parsed[0]);
        Assert.Equal(new[] { "q2", "C" }, parsed[1]);
    }

    [Fact]
    public void ResolvePages_HandlesAllForms()
    {
        Assert.Equal(new[] { 1, 2, 3 }, ImageUtils.ResolvePages(null, 3));
        Assert.Equal(new[] { 2, 3, 4 }, ImageUtils.ResolvePages(System.Text.Json.Nodes.JsonNode.Parse("\"2-\""), 4));
        Assert.Equal(new[] { 1, 3, 4, 5 },
            ImageUtils.ResolvePages(System.Text.Json.Nodes.JsonNode.Parse("[1, \"3-5\", 4]"), 9));
    }
}

/// <summary>入口与命令行的边界情况测试（对应 Python 版 test_edge_cases / test_template_validations）。</summary>
public sealed class EntryEdgeCaseTests
{
    [Fact]
    public void MissingInputDirectory_Throws()
    {
        var e = Assert.Throws<OmrCheckerException>(() => Entry.EntryPoint("X-does-not-exist", new CliArgs()));
        Assert.Equal("Given input directory does not exist: 'X-does-not-exist'", e.Message);
    }

    [Fact]
    public void ImagesWithoutTemplate_Throws()
    {
        TestHelpers.EnterRepoRoot();
        var dir = Path.Combine(Path.GetTempPath(), "omr-no-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(TestHelpers.TestDataPath("sample.png"),
                Path.Combine(dir, "sample.png"));
            var e = Assert.Throws<OmrCheckerException>(() =>
                Entry.EntryPoint(dir, new CliArgs { OutputDir = Path.Combine(dir, "out") }));
            Assert.StartsWith("No template file found in the directory tree of", e.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void InvalidTemplate_FailsSchemaValidation()
    {
        TestHelpers.EnterRepoRoot();
        var dir = Path.Combine(Path.GetTempPath(), "omr-bad-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 缺少必填的 bubbleDimensions
            File.WriteAllText(Path.Combine(dir, "template.json"),
                """{"pageDimensions": [300, 400], "preProcessors": [], "fieldBlocks": {}}""");
            var e = Assert.Throws<OmrCheckerException>(() => Entry.EntryPoint(dir, new CliArgs()));
            Assert.StartsWith("Provided Template JSON is Invalid", e.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Cli_ParsesArgumentsLikeArgparse()
    {
        var (args, unknown, help) = Cli.ParseArgs(new[] { "-i", "a", "b", "-o", "out", "-l", "--bogus" });
        Assert.Equal(new[] { "a", "b" }, args.InputPaths);
        Assert.Equal("out", args.OutputDir);
        Assert.True(args.SetLayout);
        Assert.False(help);
        Assert.Equal(new[] { "--bogus" }, unknown);
        Assert.Equal(11, Cli.Run(new[] { "--bogus" }));
    }
}
