using OpenCvSharp;

namespace OMRChecker.Constants;

/// <summary>图像处理常量，对应 Python 版 src/constants/image_processing.py。</summary>
public static class ImageProcessingConstants
{
    // 通用
    public const int DefaultWhiteColor = 255;
    public const int DefaultBlackColor = 0;
    public const int DefaultLineWidth = 2;
    public const int DefaultMarkerLineWidth = 4;
    public const int DefaultBorderRemove = 5;

    // CropOnMarkers 中标记模板的高斯模糊参数
    public static readonly Size MarkerGaussianKernel = new(5, 5);
    public const double MarkerGaussianSigmaX = 0;

    // CropPage
    public const double MinPageAreaThreshold = 80000;
    public const double MaxCosineThreshold = 0.35;
    public static readonly Size DefaultGaussianBlurKernel = new(3, 3);
    public const double PageThresholdValue = 200;
    public const double PageMaxPixelValue = 255;

    // Canny 阈值：注意下限大于上限，OpenCV 内部会自动交换，保持与 Python 版一致
    public const double CannyLowerThreshold = 185;
    public const double CannyUpperThreshold = 55;
    public const double ApproxPolyEpsilonFactor = 0.025;

    // CropOnMarkers
    public const int QuadrantHeightFactor = 3;
    public const int QuadrantWidthFactor = 2;
    public static readonly Scalar MarkerRectangleColor = new(150, 150, 150);
    public static readonly Scalar ErodeRectColor = new(50, 50, 50);
    public static readonly Scalar NormalRectColor = new(155, 155, 155);
    public static readonly Size ErosionKernelSize = new(5, 5);
    public const int ErosionIterations = 5;

    // FeatureBasedAlignment
    public const int DefaultMaxFeatures = 500;
    public const double DefaultGoodMatchPercent = 0.15;

    // 内置预处理器
    public const int DefaultMedianBlurKernelSize = 5;
    public static readonly int[] DefaultGaussianBlurKernelSize = { 3, 3 };
    public const double DefaultGaussianBlurSigmaX = 0;
}
