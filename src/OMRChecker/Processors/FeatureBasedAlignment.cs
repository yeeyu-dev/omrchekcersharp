using System.Text.Json.Nodes;
using OpenCvSharp;
using OMRChecker.Core;
using OMRChecker.Logging;
using OMRChecker.Utils;
using C = OMRChecker.Constants.ImageProcessingConstants;

namespace OMRChecker.Processors;

/// <summary>
/// 基于 ORB 特征点的图像对齐：与参考图做特征匹配，求单应性（或 2D 仿射）后变换到参考图坐标系。
/// 对应 Python 版 FeatureBasedAlignment。options：reference（必填）、maxFeatures、goodMatchPercent、2d。
/// </summary>
public sealed class FeatureBasedAlignment : ImagePreprocessor
{
    private readonly string _refPath;
    private readonly Mat _refImg;
    private readonly double _goodMatchPercent;
    private readonly bool _transform2D;
    private readonly ORB _orb;
    private readonly KeyPoint[] _toKeypoints;
    private readonly Mat _toDescriptors = new();

    public FeatureBasedAlignment(JsonObject options, string relativeDir, ImageInstanceOps ops)
        : base(options, relativeDir, ops)
    {
        var config = TuningConfig;
        _refPath = Path.Combine(relativeDir, JsonUtils.GetString(options["reference"])!);
        var refImg = ImageUtils.ImReadGray(_refPath)
                     ?? throw new OmrCheckerException($"Reference image not found: '{_refPath}'");
        _refImg = ImageUtils.ResizeUtil(refImg, config.Dimensions.ProcessingWidth, config.Dimensions.ProcessingHeight);

        var maxFeatures = JsonUtils.GetInt(options["maxFeatures"], C.DefaultMaxFeatures);
        _goodMatchPercent = JsonUtils.GetDouble(options["goodMatchPercent"], C.DefaultGoodMatchPercent);
        _transform2D = JsonUtils.GetBool(options["2d"], false);

        // 预先提取参考图的特征点与描述子
        _orb = ORB.Create(maxFeatures);
        _orb.DetectAndCompute(_refImg, null, out _toKeypoints, _toDescriptors);
    }

    public override string ToString() => Path.GetFileName(_refPath);

    /// <summary>参考图本身不参与识别。</summary>
    public override IEnumerable<string> ExcludeFiles() => new[] { _refPath };

    /// <summary>
    /// 特征匹配后对齐到参考图。边界：匹配点不足以求解变换时记录错误并返回 null。
    /// </summary>
    public override Mat? ApplyFilter(Mat image, string filePath)
    {
        var config = TuningConfig;
        using var normalized = ImageUtils.NormalizeUtil(image);

        using var fromDescriptors = new Mat();
        _orb.DetectAndCompute(normalized, null, out var fromKeypoints, fromDescriptors);

        using var matcher = DescriptorMatcher.Create("BruteForce-Hamming");
        var matches = matcher.Match(fromDescriptors, _toDescriptors)
            .OrderBy(m => m.Distance) // 稳定排序，与 Python sorted 一致
            .ToArray();

        // 只保留最好的一部分匹配
        var numGoodMatches = (int)(matches.Length * _goodMatchPercent);
        matches = matches.Take(numGoodMatches).ToArray();

        if (config.Outputs.ShowImageLevel > 2)
        {
            using var imMatches = new Mat();
            Cv2.DrawMatches(normalized, fromKeypoints, _refImg, _toKeypoints, matches, imMatches);
            InteractionUtils.Show("Aligning", imMatches, resize: true, config: config);
        }

        var points1 = matches.Select(m => new Point2d(fromKeypoints[m.QueryIdx].Pt.X, fromKeypoints[m.QueryIdx].Pt.Y)).ToArray();
        var points2 = matches.Select(m => new Point2d(_toKeypoints[m.TrainIdx].Pt.X, _toKeypoints[m.TrainIdx].Pt.Y)).ToArray();
        var size = new Size(_refImg.Cols, _refImg.Rows);

        var minPoints = _transform2D ? 3 : 4;
        if (matches.Length < minPoints)
        {
            Logger.Error($"Not enough feature matches ({matches.Length}) to align '{filePath}'");
            return null;
        }

        var result = new Mat();
        if (_transform2D)
        {
            using var affine = Cv2.EstimateAffine2D(InputArray.Create(points1), InputArray.Create(points2));
            if (affine is null || affine.Empty())
            {
                Logger.Error($"Could not estimate affine transform for '{filePath}'");
                return null;
            }

            Cv2.WarpAffine(normalized, result, affine, size);
            return result;
        }

        using var homography = Cv2.FindHomography(points1, points2, HomographyMethods.Ransac);
        if (homography.Empty())
        {
            Logger.Error($"Could not find homography for '{filePath}'");
            return null;
        }

        Cv2.WarpPerspective(normalized, result, homography, size);
        return result;
    }
}
