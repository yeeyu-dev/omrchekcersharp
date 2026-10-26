using System.Text.Json.Nodes;
using OpenCvSharp;
using OMRChecker.Core;
using OMRChecker.Logging;
using OMRChecker.Utils;
using C = OMRChecker.Constants.ImageProcessingConstants;

namespace OMRChecker.Processors;

/// <summary>
/// 基于四角定位标记的裁剪：在图像四个象限中做多尺度模板匹配找到标记中心，
/// 再以四个中心做透视矫正。对应 Python 版 CropOnMarkers。
/// options：relativePath（必填）、sheetToMarkerWidthRatio、min_matching_threshold、
/// max_matching_variation、marker_rescale_range、marker_rescale_steps、apply_erode_subtract。
/// </summary>
public sealed class CropOnMarkers : ImagePreprocessor
{
    private readonly string _markerPath;
    private readonly double _minMatchingThreshold;
    private readonly double _maxMatchingVariation;
    private readonly (int Low, int High) _markerRescaleRange;
    private readonly int _markerRescaleSteps;
    private readonly bool _applyErodeSubtract;
    private readonly Mat _marker;

    public CropOnMarkers(JsonObject options, string relativeDir, ImageInstanceOps ops)
        : base(options, relativeDir, ops)
    {
        _markerPath = Path.Combine(relativeDir, JsonUtils.GetString(options["relativePath"], "omr_marker.jpg")!);
        _minMatchingThreshold = JsonUtils.GetDouble(options["min_matching_threshold"], 0.3);
        _maxMatchingVariation = JsonUtils.GetDouble(options["max_matching_variation"], 0.41);
        var range = options["marker_rescale_range"] is JsonArray
            ? JsonUtils.GetIntArray(options["marker_rescale_range"])
            : new[] { 35, 100 };
        _markerRescaleRange = (range[0], range[1]);
        _markerRescaleSteps = JsonUtils.GetInt(options["marker_rescale_steps"], 10);
        _applyErodeSubtract = JsonUtils.GetBool(options["apply_erode_subtract"], true);
        _marker = LoadMarker(options);
    }

    /// <summary>阈值记录（分析用）：每张图四个象限匹配度的均值。</summary>
    public List<double> ThresholdCircles { get; } = new();

    public override string ToString() => _markerPath;

    /// <summary>标记图本身不参与识别。</summary>
    public override IEnumerable<string> ExcludeFiles() => new[] { _markerPath };

    /// <summary>
    /// 在四个象限中分别匹配标记并透视矫正。
    /// 边界：任一象限匹配度低于 min_matching_threshold，或与全局最佳匹配度差异超过
    /// max_matching_variation 时返回 null。
    /// </summary>
    public override Mat? ApplyFilter(Mat image, string filePath)
    {
        var config = TuningConfig;

        // 注意：此处条件与选项名含义相反（为 true 时反而不做腐蚀相减），这是 Python 版的原有逻辑，
        // 为保证识别结果一致而保留；标记模板本身在 apply_erode_subtract=true 时做了腐蚀相减。
        Mat source;
        if (_applyErodeSubtract)
        {
            source = image;
        }
        else
        {
            source = new Mat();
            using var eroded = Erode(image);
            Cv2.Subtract(image, eroded, source);
        }

        var imageErodedSub = ImageUtils.NormalizeUtil(source);
        var (h1, w1) = (imageErodedSub.Rows, imageErodedSub.Cols);
        var (midh, midw) = (h1 / C.QuadrantHeightFactor, w1 / C.QuadrantWidthFactor);
        var origins = new[] { new Point(0, 0), new Point(midw, 0), new Point(0, midh), new Point(midw, midh) };

        // 象限为共享内存的 ROI 视图，与 numpy 切片一致：后续画线 / 画框会反映到象限中
        var quads = new[]
        {
            new Mat(imageErodedSub, new Rect(0, 0, midw, midh)),
            new Mat(imageErodedSub, new Rect(midw, 0, w1 - midw, midh)),
            new Mat(imageErodedSub, new Rect(0, midh, midw, h1 - midh)),
            new Mat(imageErodedSub, new Rect(midw, midh, w1 - midw, h1 - midh)),
        };

        // 绘制象限分隔线
        imageErodedSub.ColRange(midw, Math.Min(midw + 2, w1)).SetTo(new Scalar(C.DefaultWhiteColor));
        imageErodedSub.RowRange(midh, Math.Min(midh + 2, h1)).SetTo(new Scalar(C.DefaultWhiteColor));

        var (bestScale, allMaxT) = GetBestMatch(imageErodedSub);
        if (bestScale is null)
        {
            if (config.Outputs.ShowImageLevel >= 1)
            {
                InteractionUtils.Show("Quads", imageErodedSub, config: config);
            }

            return null;
        }

        using var optimalMarker = ImageUtils.ResizeUtilH(_marker, (int)(_marker.Rows * bestScale.Value));
        var (markerH, markerW) = (optimalMarker.Rows, optimalMarker.Cols);
        var centres = new List<Point2f>();
        double sumT = 0;
        var quarterMatchLog = "Matching Marker:  ";
        for (var k = 0; k < 4; k++)
        {
            using var res = new Mat();
            Cv2.MatchTemplate(quads[k], optimalMarker, res, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(res, out _, out double maxT, out _, out Point maxLoc);
            quarterMatchLog += $"Quarter{k + 1}: {PyFormat.Float(PyFormat.Round(maxT, 3))}\t";
            if (maxT < _minMatchingThreshold || Math.Abs(allMaxT - maxT) >= _maxMatchingVariation)
            {
                Logger.Error(filePath, "\nError: No circle found in Quad", k + 1,
                    "\n\t min_matching_threshold", PyFormat.Float(_minMatchingThreshold),
                    "\t max_matching_variation", PyFormat.Float(_maxMatchingVariation),
                    "\t max_t", PyFormat.Float(maxT),
                    "\t all_max_t", PyFormat.Float(allMaxT));
                if (config.Outputs.ShowImageLevel >= 1)
                {
                    InteractionUtils.Show($"No markers: {filePath}", imageErodedSub, pause: false, config: config);
                    InteractionUtils.Show($"res_Q{k + 1} ({PyFormat.Float(maxT)})", res, pause: true, config: config);
                }

                return null;
            }

            var pt = new Point(maxLoc.X + origins[k].X, maxLoc.Y + origins[k].Y);
            // 在输入图上标出标记位置（随后会被透视矫正带入输出），与 Python 版一致
            Cv2.Rectangle(image, pt, new Point(pt.X + markerW, pt.Y + markerH), C.MarkerRectangleColor,
                C.DefaultLineWidth);
            Cv2.Rectangle(imageErodedSub, pt, new Point(pt.X + markerW, pt.Y + markerH),
                _applyErodeSubtract ? C.ErodeRectColor : C.NormalRectColor, 4);
            centres.Add(new Point2f(pt.X + (markerW / 2f), pt.Y + (markerH / 2f)));
            sumT += maxT;
        }

        Logger.Info(quarterMatchLog);
        Logger.Info($"Optimal Scale: {PyFormat.Float(bestScale.Value)}");
        ThresholdCircles.Add(sumT / 4);

        var warped = ImageUtils.FourPointTransform(image, centres);
        ImageInstanceOps.AppendSaveImg(2, imageErodedSub);

        if (config.Outputs.ShowImageLevel is >= 2 and < 4)
        {
            using var resizedSub = ImageUtils.ResizeUtilH(imageErodedSub, warped.Rows);
            resizedSub.ColRange(Math.Max(0, resizedSub.Cols - C.DefaultBorderRemove), resizedSub.Cols)
                .SetTo(new Scalar(C.DefaultBlackColor));
            using var hStack = new Mat();
            Cv2.HConcat(new[] { resizedSub, warped }, hStack);
            InteractionUtils.Show($"Warped: {filePath}",
                ImageUtils.ResizeUtil(hStack, (int)(config.Dimensions.DisplayWidth * 1.6)),
                pause: false, resize: false, resetPos: new[] { 0, 0 }, config: config);
        }

        foreach (var quad in quads)
        {
            quad.Dispose();
        }

        return warped;
    }

    /// <summary>
    /// 加载并预处理标记模板：按 sheetToMarkerWidthRatio 缩放 → 高斯模糊 → 归一化 →（可选）腐蚀相减。
    /// 边界：标记文件不存在时抛出异常（Python 版此处 exit(31)）。
    /// </summary>
    private Mat LoadMarker(JsonObject options)
    {
        var marker = ImageUtils.ImReadGray(_markerPath);
        if (marker is null)
        {
            Logger.Error("Marker not found at path provided in template:", _markerPath);
            throw new OmrCheckerException($"Marker not found at path provided in template: {_markerPath}");
        }

        if (options["sheetToMarkerWidthRatio"] is not null)
        {
            var ratio = JsonUtils.GetInt(options["sheetToMarkerWidthRatio"], 1);
            marker = ImageUtils.ResizeUtil(marker, TuningConfig.Dimensions.ProcessingWidth / (double)ratio);
        }

        var blurred = new Mat();
        Cv2.GaussianBlur(marker, blurred, C.MarkerGaussianKernel, C.MarkerGaussianSigmaX);
        var normalized = new Mat();
        Cv2.Normalize(blurred, normalized, 0, 255, NormTypes.MinMax);

        if (_applyErodeSubtract)
        {
            using var eroded = Erode(normalized);
            Cv2.Subtract(normalized, eroded, normalized);
        }

        return normalized;
    }

    /// <summary>
    /// 多尺度匹配：在 marker_rescale_range 内由大到小缩放标记，返回匹配度最高的比例与匹配度。
    /// 边界：没有任何比例产生正匹配度时比例为 null。
    /// </summary>
    private (double? BestScale, double AllMaxT) GetBestMatch(Mat imageErodedSub)
    {
        var config = TuningConfig;
        var descentPerStep = Math.Max(1, (_markerRescaleRange.High - _markerRescaleRange.Low) / _markerRescaleSteps);
        var markerH = _marker.Rows;
        double? bestScale = null;
        double allMaxT = 0;
        Mat? lastRes = null;

        // 等价于 np.arange(high, low, -step)：由大到小，不含 low
        for (var r0 = _markerRescaleRange.High; r0 > _markerRescaleRange.Low; r0 -= descentPerStep)
        {
            var s = r0 / 100.0;
            if (s == 0.0)
            {
                continue;
            }

            using var rescaledMarker = ImageUtils.ResizeUtilH(_marker, (int)(markerH * s));
            lastRes?.Dispose();
            lastRes = new Mat();
            // res 为黑底白点的匹配度图
            Cv2.MatchTemplate(imageErodedSub, rescaledMarker, lastRes, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(lastRes, out _, out double maxT);
            if (allMaxT < maxT)
            {
                (bestScale, allMaxT) = (s, maxT);
            }
        }

        if (allMaxT < _minMatchingThreshold)
        {
            Logger.Warning("\tTemplate matching too low! Consider rechecking preProcessors applied before this.");
            if (config.Outputs.ShowImageLevel >= 1 && lastRes is not null)
            {
                InteractionUtils.Show("res", lastRes, pause: true, config: config);
            }
        }

        lastRes?.Dispose();

        if (bestScale is null)
        {
            Logger.Warning("No matchings for given scaleRange:",
                $"({_markerRescaleRange.Low}, {_markerRescaleRange.High})");
        }

        return (bestScale, allMaxT);
    }

    /// <summary>5x5 全 1 核腐蚀 5 次。</summary>
    private static Mat Erode(Mat image)
    {
        using var kernel = new Mat(C.ErosionKernelSize, MatType.CV_8UC1, Scalar.All(1));
        var dst = new Mat();
        Cv2.Erode(image, dst, kernel, iterations: C.ErosionIterations);
        return dst;
    }
}
