using System.Text.Json.Nodes;
using OpenCvSharp;
using OMRChecker.Constants;
using OMRChecker.Core;
using OMRChecker.Logging;
using OMRChecker.Utils;
using C = OMRChecker.Constants.ImageProcessingConstants;

namespace OMRChecker.Processors;

/// <summary>
/// 页面裁剪：在图像中寻找最大的近似矩形轮廓作为纸张边界，并做透视矫正。
/// 对应 Python 版 CropPage。options：morphKernel [w, h]（闭运算核，默认 10x10）。
/// </summary>
public sealed class CropPage : ImagePreprocessor
{
    private readonly Size _morphKernel;

    public CropPage(JsonObject options, string relativeDir, ImageInstanceOps ops)
        : base(options, relativeDir, ops)
    {
        var kernel = options["morphKernel"] is JsonArray ? JsonUtils.GetIntArray(options["morphKernel"]) : new[] { 10, 10 };
        _morphKernel = new Size(kernel[0], kernel[1]);
    }

    /// <summary>
    /// 模糊 + 归一化后寻找页面四角并透视矫正。边界：找不到页面边界时返回 null。
    /// </summary>
    public override Mat? ApplyFilter(Mat image, string filePath)
    {
        using var blurred = new Mat();
        Cv2.GaussianBlur(image, blurred, C.DefaultGaussianBlurKernel, 0);
        using var normalized = ImageUtils.NormalizeUtil(blurred);

        var sheet = FindPage(normalized);
        if (sheet is null)
        {
            Logger.Error(
                $"\tError: Paper boundary not found for: '{filePath}'\nHave you accidentally included CropPage preprocessor?");
            return null;
        }

        Logger.Info($"Found page corners: \t [{string.Join(", ", sheet.Select(p => $"[{p.X}, {p.Y}]"))}]");
        return ImageUtils.FourPointTransform(normalized, sheet.Select(p => new Point2f(p.X, p.Y)).ToArray());
    }

    /// <summary>
    /// 寻找页面：截断高亮 → 闭运算补全边缘 → Canny → 轮廓凸包 → 取面积最大的 5 个，
    /// 返回第一个面积足够大且四角接近直角的四边形顶点。边界：都不满足时返回 null。
    /// </summary>
    private Point[]? FindPage(Mat image)
    {
        using var normalized = ImageUtils.NormalizeUtil(image);
        using var truncated = new Mat();
        Cv2.Threshold(normalized, truncated, C.PageThresholdValue, C.PageMaxPixelValue, ThresholdTypes.Trunc);
        using var renormalized = ImageUtils.NormalizeUtil(truncated);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, _morphKernel);
        // 闭运算填补小孔洞，补全 Canny 边缘
        using var closed = new Mat();
        Cv2.MorphologyEx(renormalized, closed, MorphTypes.Close, kernel);

        using var edge = new Mat();
        Cv2.Canny(closed, edge, C.CannyLowerThreshold, C.CannyUpperThreshold);

        if (TuningConfig.Outputs.ShowImageLevel >= 5)
        {
            InteractionUtils.Show("edge", edge, config: TuningConfig);
        }

        // findContours：外轮廓顺时针，内轮廓逆时针；凸包用于消除噪声造成的凹陷
        Cv2.FindContours(edge, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        var candidates = contours
            .Select(c => Cv2.ConvexHull(c))
            .OrderByDescending(c => Cv2.ContourArea(c))
            .Take(5);

        foreach (var c in candidates)
        {
            if (Cv2.ContourArea(c) < C.MinPageAreaThreshold)
            {
                continue;
            }

            var peri = Cv2.ArcLength(c, true);
            var approx = Cv2.ApproxPolyDP(c, C.ApproxPolyEpsilonFactor * peri, true);
            if (ValidateRect(approx))
            {
                return approx;
            }
        }

        return null;
    }

    /// <summary>四边形且各角余弦绝对值都小于阈值（接近直角）才视为矩形。</summary>
    private static bool ValidateRect(Point[] approx) => approx.Length == 4 && CheckMaxCosine(approx);

    /// <summary>检查四边形的最大角余弦（假定恰有 4 个点）。</summary>
    private static bool CheckMaxCosine(Point[] approx)
    {
        double maxCosine = 0;
        for (var i = 2; i < 5; i++)
        {
            var cosine = Math.Abs(Angle(approx[i % 4], approx[i - 2], approx[i - 1]));
            maxCosine = Math.Max(cosine, maxCosine);
        }

        if (maxCosine >= C.MaxCosineThreshold)
        {
            Logger.Warning("Quadrilateral is not a rectangle.");
            return false;
        }

        return true;
    }

    /// <summary>以 p0 为顶点、p1/p2 为两端的夹角余弦。</summary>
    private static double Angle(Point p1, Point p2, Point p0)
    {
        double dx1 = p1.X - p0.X, dy1 = p1.Y - p0.Y;
        double dx2 = p2.X - p0.X, dy2 = p2.Y - p0.Y;
        return ((dx1 * dx2) + (dy1 * dy2)) /
               Math.Sqrt((((dx1 * dx1) + (dy1 * dy1)) * ((dx2 * dx2) + (dy2 * dy2))) + 1e-10);
    }
}
