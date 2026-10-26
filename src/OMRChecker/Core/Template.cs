using System.Text.Json.Nodes;
using OMRChecker.Config;
using OMRChecker.Constants;
using OMRChecker.Logging;
using OMRChecker.Processors;
using OMRChecker.Utils;

namespace OMRChecker.Core;

/// <summary>
/// OMR 模板（template.json），对应 Python 版 src/template.py 中的 Template。
/// 负责解析字段块、自定义标签、输出列与预处理器，并做一致性校验。
/// </summary>
public sealed class Template
{
    private readonly List<string> _allParsedLabelsOrdered = new();

    /// <summary>
    /// 加载模板。
    /// </summary>
    /// <param name="templatePath">template.json 路径。</param>
    /// <param name="tuningConfig">当前目录生效的调优配置。</param>
    /// <exception cref="OmrCheckerException">模板不合法（标签重叠、越界、输出列缺失等）。</exception>
    public Template(string templatePath, TuningConfig tuningConfig)
    {
        Path = templatePath;
        ImageInstanceOps = new ImageInstanceOps(tuningConfig);

        var json = ParsingUtils.OpenTemplateWithDefaults(templatePath);
        BubbleDimensions = JsonUtils.GetIntArray(json["bubbleDimensions"]);
        GlobalEmptyVal = JsonUtils.GetString(json["emptyValue"], string.Empty)!;
        Options = json["options"] as JsonObject;
        PageDimensions = JsonUtils.GetIntArray(json["pageDimensions"]);

        var customLabelsObject = json["customLabels"]!.AsObject();

        ParseOutputColumns(JsonUtils.GetStringList(json["outputColumns"]));
        SetupPreProcessors(json["preProcessors"]!.AsArray(),
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(templatePath))!);
        SetupFieldBlocks(json["fieldBlocks"]!.AsObject());
        ParseCustomLabels(customLabelsObject);

        var nonCustomColumns = NonCustomLabels.ToList();
        var allCustomColumns = customLabelsObject.Select(kv => kv.Key).ToList();

        if (OutputColumns.Count == 0)
        {
            FillOutputColumns(nonCustomColumns, allCustomColumns);
        }

        ValidateTemplateColumns(nonCustomColumns, allCustomColumns);
    }

    /// <summary>模板文件路径。</summary>
    public string Path { get; }

    /// <summary>与本模板绑定的图像处理实例。</summary>
    public ImageInstanceOps ImageInstanceOps { get; }

    /// <summary>全局气泡尺寸 [宽, 高]。</summary>
    public int[] BubbleDimensions { get; }

    /// <summary>未涂答时的全局默认值。</summary>
    public string GlobalEmptyVal { get; }

    /// <summary>保留字段（schema 不允许出现，恒为 null，与 Python 版一致）。</summary>
    public JsonObject? Options { get; }

    /// <summary>识别前页面缩放到的尺寸 [宽, 高]。</summary>
    public int[] PageDimensions { get; }

    /// <summary>输出 CSV 的列顺序。</summary>
    public List<string> OutputColumns { get; private set; } = new();

    /// <summary>按顺序执行的预处理器。</summary>
    public List<ImagePreprocessor> PreProcessors { get; } = new();

    /// <summary>字段块（按模板中的声明顺序）。</summary>
    public List<FieldBlock> FieldBlocks { get; } = new();

    /// <summary>所有字段块展开后的标签集合。</summary>
    public HashSet<string> AllParsedLabels { get; } = new();

    /// <summary>自定义标签 → 需要拼接的子字段。</summary>
    public Dictionary<string, List<string>> CustomLabels { get; } = new();

    /// <summary>不属于任何自定义标签的字段（保持声明顺序）。</summary>
    public List<string> NonCustomLabels { get; private set; } = new();

    public override string ToString() => Path;

    /// <summary>解析 outputColumns（支持区间写法）。</summary>
    private void ParseOutputColumns(List<string> outputColumnsArray) =>
        OutputColumns = ParsingUtils.ParseFields("Output Columns", outputColumnsArray);

    /// <summary>按名称实例化预处理器。相对路径（如标记图）以模板所在目录为基准。</summary>
    private void SetupPreProcessors(JsonArray preProcessorsObject, string relativeDir)
    {
        foreach (var node in preProcessorsObject)
        {
            var name = node!["name"]!.GetValue<string>();
            var options = node["options"] as JsonObject ?? new JsonObject();
            PreProcessors.Add(ProcessorManager.Create(name, options, relativeDir, ImageInstanceOps));
        }
    }

    /// <summary>解析全部字段块。</summary>
    private void SetupFieldBlocks(JsonObject fieldBlocksObject)
    {
        foreach (var (blockName, blockObject) in fieldBlocksObject)
        {
            ParseAndAddFieldBlock(blockName, blockObject!.AsObject());
        }
    }

    /// <summary>
    /// 解析 customLabels。边界：引用了不存在的字段，或不同自定义标签之间字段重叠时抛出异常。
    /// </summary>
    private void ParseCustomLabels(JsonObject customLabelsObject)
    {
        var allParsedCustomLabels = new HashSet<string>();
        foreach (var (customLabel, labelStrings) in customLabelsObject)
        {
            var labelStringList = JsonUtils.GetStringList(labelStrings);
            var parsedLabels = ParsingUtils.ParseFields($"Custom Label: {customLabel}", labelStringList);
            CustomLabels[customLabel] = parsedLabels;

            var missing = parsedLabels.Where(l => !AllParsedLabels.Contains(l))
                .Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                Logger.Critical($"For '{customLabel}', Missing labels - {PyFormat.Repr(missing)}");
                throw new OmrCheckerException(
                    $"Missing field block label(s) in the given template for {PyFormat.Repr(missing)} from '{customLabel}'");
            }

            if (parsedLabels.Any(allParsedCustomLabels.Contains))
            {
                Logger.Critical(
                    $"field strings overlap for labels: {PyFormat.Repr(labelStringList)} and existing custom labels: {PyFormat.Repr(allParsedCustomLabels.ToList())}");
                throw new OmrCheckerException(
                    $"The field strings for custom label '{customLabel}' overlap with other existing custom labels");
            }

            allParsedCustomLabels.UnionWith(parsedLabels);
        }

        NonCustomLabels = _allParsedLabelsOrdered.Where(l => !allParsedCustomLabels.Contains(l)).ToList();
    }

    /// <summary>未指定 outputColumns 时：全部列按自然顺序排序（稳定排序）。</summary>
    private void FillOutputColumns(List<string> nonCustomColumns, List<string> allCustomColumns)
    {
        var all = nonCustomColumns.Concat(allCustomColumns).ToList();
        OutputColumns = all.Order(Comparer<string>.Create(ParsingUtils.CompareOutputColumns)).ToList();
    }

    /// <summary>
    /// 校验输出列：引用未知字段时抛出异常；存在未被输出的字段时给出警告。
    /// </summary>
    private void ValidateTemplateColumns(List<string> nonCustomColumns, List<string> allCustomColumns)
    {
        var outputColumnsSet = OutputColumns.ToHashSet();
        var allCustomColumnsSet = allCustomColumns.ToHashSet();

        var missingOutputColumns = outputColumnsSet
            .Where(c => !allCustomColumnsSet.Contains(c) && !AllParsedLabels.Contains(c))
            .OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (missingOutputColumns.Count > 0)
        {
            Logger.Critical($"Missing output columns: {PyFormat.Repr(missingOutputColumns)}");
            throw new OmrCheckerException("Some columns are missing in the field blocks for the given output columns");
        }

        var missingLabelColumns = nonCustomColumns.Concat(allCustomColumns)
            .Where(c => !outputColumnsSet.Contains(c))
            .Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (missingLabelColumns.Count > 0)
        {
            Logger.Warning(
                $"Some label columns are not covered in the given output columns: {PyFormat.Repr(missingLabelColumns)}");
        }
    }

    /// <summary>填充默认值后构造字段块并校验。</summary>
    private void ParseAndAddFieldBlock(string blockName, JsonObject fieldBlockObject)
    {
        var filled = PreFillFieldBlock(fieldBlockObject);
        var block = new FieldBlock(blockName, filled);
        FieldBlocks.Add(block);
        ValidateParsedLabels(JsonUtils.GetStringList(filled["fieldLabels"]), block);
    }

    /// <summary>
    /// 合并字段块默认值，优先级（低 → 高）：
    /// 全局默认（vertical / 全局空值 / 全局气泡尺寸）→ 预定义题型 → 字段块自身配置。
    /// 未声明 fieldType 时标记为 "__CUSTOM__"。
    /// </summary>
    private JsonObject PreFillFieldBlock(JsonObject fieldBlockObject)
    {
        var result = new JsonObject
        {
            ["direction"] = "vertical",
            ["emptyValue"] = GlobalEmptyVal,
            ["bubbleDimensions"] = new JsonArray(BubbleDimensions.Select(d => (JsonNode)d).ToArray()),
        };

        var fieldType = JsonUtils.GetString(fieldBlockObject["fieldType"]);
        if (fieldType is not null)
        {
            var definition = CommonConstants.FieldTypes[fieldType];
            result["bubbleValues"] = new JsonArray(definition.BubbleValues.Select(v => (JsonNode)v).ToArray());
            result["direction"] = definition.Direction;
        }

        foreach (var (key, value) in fieldBlockObject)
        {
            result[key] = value?.DeepClone();
        }

        if (fieldType is null)
        {
            result["fieldType"] = "__CUSTOM__";
        }

        return result;
    }

    /// <summary>
    /// 校验字段块：标签不能与已有字段块重叠；块的范围必须完全位于页面内。
    /// </summary>
    private void ValidateParsedLabels(List<string> fieldLabels, FieldBlock block)
    {
        if (block.ParsedFieldLabels.Any(AllParsedLabels.Contains))
        {
            Logger.Critical(
                $"An overlap found between field string: {PyFormat.Repr(fieldLabels)} in block '{block.Name}' and existing labels: {PyFormat.Repr(_allParsedLabelsOrdered)}");
            throw new OmrCheckerException(
                $"The field strings for field block {block.Name} overlap with other existing fields");
        }

        foreach (var label in block.ParsedFieldLabels)
        {
            if (AllParsedLabels.Add(label))
            {
                _allParsedLabelsOrdered.Add(label);
            }
        }

        var (pageWidth, pageHeight) = (PageDimensions[0], PageDimensions[1]);
        var (blockWidth, blockHeight) = (block.Dimensions[0], block.Dimensions[1]);
        var (startX, startY) = (block.Origin[0], block.Origin[1]);
        var (endX, endY) = (startX + blockWidth, startY + blockHeight);

        if (endX >= pageWidth || endY >= pageHeight || startX < 0 || startY < 0)
        {
            throw new OmrCheckerException(
                $"Overflowing field block '{block.Name}' with origin {PyFormat.Repr(block.Origin)} and dimensions {PyFormat.Repr(block.Dimensions)} in template with dimensions {PyFormat.Repr(PageDimensions)}");
        }
    }
}
