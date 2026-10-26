using System.Text.Json.Nodes;
using OpenCvSharp;
using OMRChecker.Constants;
using OMRChecker.Core;
using OMRChecker.Utils;

namespace OMRChecker.Processors;

/// <summary>
/// 色阶调整（类似 Photoshop Levels），对应 Python 版 Levels。
/// options：low / high（0~1，黑白场）、gamma（0~1）。
/// </summary>
public sealed class Levels : ImagePreprocessor
{
    private readonly byte[] _gammaTable = new byte[256];

    public Levels(JsonObject options, string relativeDir, ImageInstanceOps ops)
        : base(options, relativeDir, ops)
    {
        var low = (int)(255 * JsonUtils.GetDouble(options["low"], 0));
        var high = (int)(255 * JsonUtils.GetDouble(options["high"], 1));
        var gamma = JsonUtils.GetDouble(options["gamma"], 1.0);
        for (var i = 0; i < 256; i++)
        {
            _gammaTable[i] = OutputLevel(i, low, high, gamma);
        }
    }

    /// <summary>应用查找表。</summary>
    public override Mat? ApplyFilter(Mat image, string filePath) => ImageUtils.ApplyLut(image, _gammaTable);

    /// <summary>
    /// 单个灰度值的映射：≤low 为 0，≥high 为 255，中间按 gamma 曲线拉伸（结果截断取整）。
    /// </summary>
    private static byte OutputLevel(int value, int low, int high, double gamma)
    {
        if (value <= low)
        {
            return 0;
        }

        if (value >= high)
        {
            return 255;
        }

        var invGamma = 1.0 / gamma;
        return (byte)(Math.Pow((value - low) / (double)(high - low), invGamma) * 255);
    }
}

/// <summary>中值滤波，对应 Python 版 MedianBlur。options：kSize（奇数）。</summary>
public sealed class MedianBlur : ImagePreprocessor
{
    private readonly int _kSize;

    public MedianBlur(JsonObject options, string relativeDir, ImageInstanceOps ops)
        : base(options, relativeDir, ops)
    {
        _kSize = JsonUtils.GetInt(options["kSize"], ImageProcessingConstants.DefaultMedianBlurKernelSize);
    }

    /// <summary>执行中值滤波。</summary>
    public override Mat? ApplyFilter(Mat image, string filePath)
    {
        var dst = new Mat();
        Cv2.MedianBlur(image, dst, _kSize);
        return dst;
    }
}

/// <summary>高斯模糊，对应 Python 版 GaussianBlur。options：kSize [w, h]、sigmaX。</summary>
public sealed class GaussianBlur : ImagePreprocessor
{
    private readonly Size _kSize;
    private readonly double _sigmaX;

    public GaussianBlur(JsonObject options, string relativeDir, ImageInstanceOps ops)
        : base(options, relativeDir, ops)
    {
        var kSize = options["kSize"] is JsonArray
            ? JsonUtils.GetIntArray(options["kSize"])
            : ImageProcessingConstants.DefaultGaussianBlurKernelSize;
        _kSize = new Size(kSize[0], kSize[1]);
        // Python 版对 sigmaX 做了 int() 截断，这里保持一致
        _sigmaX = JsonUtils.GetInt(options["sigmaX"], (int)ImageProcessingConstants.DefaultGaussianBlurSigmaX);
    }

    /// <summary>执行高斯模糊。</summary>
    public override Mat? ApplyFilter(Mat image, string filePath)
    {
        var dst = new Mat();
        Cv2.GaussianBlur(image, dst, _kSize, _sigmaX);
        return dst;
    }
}
