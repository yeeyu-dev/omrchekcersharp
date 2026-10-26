using System.Text.Json.Nodes;
using OMRChecker.Utils;

namespace OMRChecker.Core;

/// <summary>
/// 字段块：一组相邻、等间距排列的字段，对应 Python 版 FieldBlock。
/// 每个字段对应一条气泡“条带”（strip），条带内每个气泡代表一个取值。
/// </summary>
public sealed class FieldBlock
{
    /// <summary>
    /// 由（已填充默认值的）字段块 JSON 构造。
    /// </summary>
    /// <param name="blockName">字段块名称。</param>
    /// <param name="fieldBlockObject">合并默认值后的字段块配置。</param>
    public FieldBlock(string blockName, JsonObject fieldBlockObject)
    {
        Name = blockName;
        Shift = 0;

        var bubbleDimensions = JsonUtils.GetIntArray(fieldBlockObject["bubbleDimensions"]);
        var bubbleValues = JsonUtils.GetStringList(fieldBlockObject["bubbleValues"]);
        var bubblesGap = JsonUtils.GetDouble(fieldBlockObject["bubblesGap"], 0);
        var direction = JsonUtils.GetString(fieldBlockObject["direction"], "vertical")!;
        var fieldLabels = JsonUtils.GetStringList(fieldBlockObject["fieldLabels"]);
        var fieldType = JsonUtils.GetString(fieldBlockObject["fieldType"], "__CUSTOM__")!;
        var labelsGap = JsonUtils.GetDouble(fieldBlockObject["labelsGap"], 0);
        EmptyVal = JsonUtils.GetString(fieldBlockObject["emptyValue"], string.Empty)!;

        ParsedFieldLabels = ParsingUtils.ParseFields($"Field Block Labels: {Name}", fieldLabels);
        Origin = JsonUtils.GetIntArray(fieldBlockObject["origin"]);
        BubbleDimensions = bubbleDimensions;
        Dimensions = CalculateBlockDimensions(bubbleDimensions, bubbleValues, bubblesGap, direction, labelsGap);
        TraverseBubbles = GenerateBubbleGrid(bubbleValues, bubblesGap, direction, fieldType, labelsGap);
    }

    public string Name { get; }

    /// <summary>自动对齐得到的水平偏移（像素）。</summary>
    public int Shift { get; set; }

    /// <summary>展开后的字段标签。</summary>
    public List<string> ParsedFieldLabels { get; }

    /// <summary>左上角坐标 [x, y]。</summary>
    public int[] Origin { get; }

    /// <summary>气泡尺寸 [宽, 高]。</summary>
    public int[] BubbleDimensions { get; }

    /// <summary>整个字段块的尺寸 [宽, 高]。</summary>
    public int[] Dimensions { get; }

    /// <summary>未涂答时的取值。</summary>
    public string EmptyVal { get; }

    /// <summary>按字段分组的气泡：外层为字段（条带），内层为取值。</summary>
    public List<List<Bubble>> TraverseBubbles { get; }

    /// <summary>
    /// 计算字段块尺寸：取值方向长度 = 气泡间距 × (取值数 - 1) + 气泡尺寸，
    /// 字段方向长度 = 标签间距 × (字段数 - 1) + 气泡尺寸；按 Python int() 截断。
    /// </summary>
    private int[] CalculateBlockDimensions(int[] bubbleDimensions, List<string> bubbleValues, double bubblesGap,
        string direction, double labelsGap)
    {
        var vertical = direction == "vertical";
        var (h, v) = vertical ? (1, 0) : (0, 1);

        var valuesDimension = (int)((bubblesGap * (bubbleValues.Count - 1)) + bubbleDimensions[h]);
        var fieldsDimension = (int)((labelsGap * (ParsedFieldLabels.Count - 1)) + bubbleDimensions[v]);
        return vertical
            ? new[] { fieldsDimension, valuesDimension }
            : new[] { valuesDimension, fieldsDimension };
    }

    /// <summary>
    /// 生成气泡网格：vertical 时取值沿 y 方向排列、字段沿 x 方向排列；horizontal 反之。
    /// </summary>
    private List<List<Bubble>> GenerateBubbleGrid(List<string> bubbleValues, double bubblesGap, string direction,
        string fieldType, double labelsGap)
    {
        var (h, v) = direction == "vertical" ? (1, 0) : (0, 1);
        var traverse = new List<List<Bubble>>();
        var leadPoint = new double[] { Origin[0], Origin[1] };
        foreach (var fieldLabel in ParsedFieldLabels)
        {
            var bubblePoint = (double[])leadPoint.Clone();
            var fieldBubbles = new List<Bubble>();
            foreach (var bubbleValue in bubbleValues)
            {
                fieldBubbles.Add(new Bubble(bubblePoint[0], bubblePoint[1], fieldLabel, fieldType, bubbleValue));
                bubblePoint[h] += bubblesGap;
            }

            traverse.Add(fieldBubbles);
            leadPoint[v] += labelsGap;
        }

        return traverse;
    }
}

/// <summary>
/// 单个气泡（OMR 上的一个可涂点），对应 Python 版 Bubble。
/// 坐标按 Python round()（银行家舍入）取整。
/// </summary>
public sealed class Bubble
{
    public Bubble(double x, double y, string fieldLabel, string fieldType, string fieldValue)
    {
        X = (int)Math.Round(x, MidpointRounding.ToEven);
        Y = (int)Math.Round(y, MidpointRounding.ToEven);
        FieldLabel = fieldLabel;
        FieldType = fieldType;
        FieldValue = fieldValue;
    }

    public int X { get; }

    public int Y { get; }

    /// <summary>所属字段（如 roll1、q5d1）。</summary>
    public string FieldLabel { get; }

    public string FieldType { get; }

    /// <summary>该气泡代表的值（如 "A"、"7"）。</summary>
    public string FieldValue { get; }

    public override string ToString() => $"[{X}, {Y}]";
}
