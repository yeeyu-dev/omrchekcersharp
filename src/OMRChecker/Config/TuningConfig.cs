using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OMRChecker.Config;

/// <summary>
/// 调优配置（config.json），对应 Python 版 CONFIG_DEFAULTS（DotMap）。
/// 加载流程：默认值 JSON 与用户 JSON 深度合并 → schema 校验 → 反序列化为本类型。
/// </summary>
public sealed class TuningConfig
{
    /// <summary>默认配置（与 Python 版 src/defaults/config.py 保持一致）。</summary>
    public const string DefaultsJson = """
    {
      "dimensions": {
        "display_height": 2480,
        "display_width": 1640,
        "processing_height": 820,
        "processing_width": 666
      },
      "threshold_params": {
        "GAMMA_LOW": 0.7,
        "MIN_GAP": 30,
        "MIN_JUMP": 25,
        "CONFIDENT_SURPLUS": 5,
        "JUMP_DELTA": 30,
        "PAGE_TYPE_FOR_THRESHOLD": "white"
      },
      "alignment_params": {
        "auto_align": false,
        "match_col": 5,
        "max_steps": 20,
        "stride": 1,
        "thickness": 3
      },
      "pdf_params": {
        "pdf_dpi": "auto",
        "pdf_page": 1
      },
      "outputs": {
        "show_image_level": 0,
        "save_image_level": 0,
        "save_detections": true,
        "filter_out_multimarked_files": false
      }
    }
    """;

    [JsonPropertyName("dimensions")]
    public DimensionsConfig Dimensions { get; set; } = new();

    [JsonPropertyName("threshold_params")]
    public ThresholdParams ThresholdParams { get; set; } = new();

    [JsonPropertyName("alignment_params")]
    public AlignmentParams AlignmentParams { get; set; } = new();

    [JsonPropertyName("pdf_params")]
    public PdfParams PdfParams { get; set; } = new();

    [JsonPropertyName("outputs")]
    public OutputsConfig Outputs { get; set; } = new();

    /// <summary>返回默认值 JSON 的一份新副本（可安全修改）。</summary>
    public static JsonObject DefaultsNode() => JsonNode.Parse(DefaultsJson)!.AsObject();

    /// <summary>构造一份全新的默认配置对象。</summary>
    public static TuningConfig CreateDefault() => FromNode(DefaultsNode());

    /// <summary>将（已合并、已校验的）JSON 节点反序列化为配置对象。</summary>
    public static TuningConfig FromNode(JsonNode node) =>
        node.Deserialize<TuningConfig>() ?? throw new InvalidOperationException("无法解析配置 JSON");

    /// <summary>深拷贝当前配置（通过 JSON 往返实现）。</summary>
    public TuningConfig Clone() => FromNode(JsonSerializer.SerializeToNode(this)!);
}

/// <summary>显示与处理尺寸。</summary>
public sealed class DimensionsConfig
{
    [JsonPropertyName("display_height")]
    public int DisplayHeight { get; set; }

    [JsonPropertyName("display_width")]
    public int DisplayWidth { get; set; }

    [JsonPropertyName("processing_height")]
    public int ProcessingHeight { get; set; }

    [JsonPropertyName("processing_width")]
    public int ProcessingWidth { get; set; }
}

/// <summary>阈值算法参数。</summary>
public sealed class ThresholdParams
{
    [JsonPropertyName("GAMMA_LOW")]
    public double GammaLow { get; set; }

    [JsonPropertyName("MIN_GAP")]
    public int MinGap { get; set; }

    [JsonPropertyName("MIN_JUMP")]
    public int MinJump { get; set; }

    [JsonPropertyName("CONFIDENT_SURPLUS")]
    public int ConfidentSurplus { get; set; }

    [JsonPropertyName("JUMP_DELTA")]
    public int JumpDelta { get; set; }

    /// <summary>"white" 或 "black"，决定全局阈值兜底值。</summary>
    [JsonPropertyName("PAGE_TYPE_FOR_THRESHOLD")]
    public string PageTypeForThreshold { get; set; } = "white";
}

/// <summary>模板自动对齐参数。</summary>
public sealed class AlignmentParams
{
    [JsonPropertyName("auto_align")]
    public bool AutoAlign { get; set; }

    [JsonPropertyName("match_col")]
    public int MatchCol { get; set; }

    [JsonPropertyName("max_steps")]
    public int MaxSteps { get; set; }

    [JsonPropertyName("stride")]
    public int Stride { get; set; }

    [JsonPropertyName("thickness")]
    public int Thickness { get; set; }
}

/// <summary>
/// PDF 参数。两个字段类型多态（整数 / 字符串 / 数组 / null），因此保留为 JSON 节点，
/// 由 <see cref="Utils.ImageUtils"/> 在使用时解析。
/// </summary>
public sealed class PdfParams
{
    /// <summary>渲染 DPI：72~600 的整数，或 "auto"（按内嵌图片推断）。</summary>
    [JsonPropertyName("pdf_dpi")]
    public JsonNode? PdfDpi { get; set; }

    /// <summary>页码：整数 / "1-5" / "3-" / 数组 / null（全部页）。</summary>
    [JsonPropertyName("pdf_page")]
    public JsonNode? PdfPage { get; set; }
}

/// <summary>输出与调试显示参数。</summary>
public sealed class OutputsConfig
{
    /// <summary>0~6，越大弹出的调试窗口越多。</summary>
    [JsonPropertyName("show_image_level")]
    public int ShowImageLevel { get; set; }

    /// <summary>0~6，越大保存的中间图像堆叠越多。</summary>
    [JsonPropertyName("save_image_level")]
    public int SaveImageLevel { get; set; }

    [JsonPropertyName("save_detections")]
    public bool SaveDetections { get; set; }

    /// <summary>为 true 时多涂文件写入 MultiMarkedFiles.csv 而非结果表。</summary>
    [JsonPropertyName("filter_out_multimarked_files")]
    public bool FilterOutMultimarkedFiles { get; set; }
}
