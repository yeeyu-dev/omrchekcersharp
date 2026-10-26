using System.Text.Json.Nodes;
using OpenCvSharp;
using OMRChecker.Config;
using OMRChecker.Core;

namespace OMRChecker.Processors;

/// <summary>
/// 图像预处理器基类，对应 Python 版 Processor + ImagePreprocessor。
/// 子类在构造函数中读取 options，在 <see cref="ApplyFilter"/> 中处理图像。
/// </summary>
public abstract class ImagePreprocessor
{
    /// <summary>
    /// 初始化预处理器。
    /// </summary>
    /// <param name="options">模板中该预处理器的 options。</param>
    /// <param name="relativeDir">模板所在目录，用于解析相对路径。</param>
    /// <param name="imageInstanceOps">共享的图像处理实例。</param>
    protected ImagePreprocessor(JsonObject options, string relativeDir, ImageInstanceOps imageInstanceOps)
    {
        Options = options;
        RelativeDir = relativeDir;
        ImageInstanceOps = imageInstanceOps;
        TuningConfig = imageInstanceOps.TuningConfig;
    }

    public JsonObject Options { get; }

    public string RelativeDir { get; }

    public ImageInstanceOps ImageInstanceOps { get; }

    public TuningConfig TuningConfig { get; }

    /// <summary>
    /// 处理图像并返回新图像。边界：无法处理（如找不到标记）时返回 null，该文件会被记入错误表。
    /// </summary>
    /// <param name="image">输入灰度图（实现可能就地修改它）。</param>
    /// <param name="filePath">当前文件路径，仅用于日志。</param>
    public abstract Mat? ApplyFilter(Mat image, string filePath);

    /// <summary>需要从待识别文件中排除的路径（如标记图、参考图）。</summary>
    public virtual IEnumerable<string> ExcludeFiles() => Array.Empty<string>();
}
