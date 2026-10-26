using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Docnet.Core;
using Docnet.Core.Models;
using OMRChecker.Config;
using OMRChecker.Logging;
using OpenCvSharp;
using UglyToad.PdfPig;

namespace OMRChecker.Utils;

/// <summary>
/// 通用图像处理工具（OpenCV 封装），对应 Python 版 src/utils/image.py。
/// 读写文件统一走字节流（ImDecode/ImEncode），以支持 Windows 下的中文路径。
/// </summary>
public static class ImageUtils
{
    /// <summary>全局 CLAHE 实例（clipLimit=5，网格 8x8），用于自动对齐前的形态学预处理。</summary>
    public static readonly CLAHE ClaheHelper = Cv2.CreateCLAHE(5.0, new Size(8, 8));

    /// <summary>
    /// 保存图像到指定路径（自动创建父目录）。扩展名决定编码格式。
    /// </summary>
    public static void SaveImg(string path, Mat image)
    {
        Logger.Info($"Saving Image to '{path}'");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".png";
        }

        Cv2.ImEncode(extension, image, out var buffer);
        File.WriteAllBytes(path, buffer);
    }

    /// <summary>
    /// 以灰度方式读取图像。边界：文件不存在或无法解码时返回 null。
    /// </summary>
    public static Mat? ImReadGray(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var mat = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Grayscale);
        return mat.Empty() ? null : mat;
    }

    /// <summary>
    /// 按宽度缩放；未给高度时按原图宽高比计算（截断取整，同 Python int()）。
    /// </summary>
    public static Mat ResizeUtil(Mat image, double uWidth, double? uHeight = null)
    {
        var height = uHeight ?? (int)(image.Rows * uWidth / image.Cols);
        var dst = new Mat();
        Cv2.Resize(image, dst, new Size((int)uWidth, (int)height));
        return dst;
    }

    /// <summary>
    /// 按高度缩放；未给宽度时按原图宽高比计算（截断取整，同 Python int()）。
    /// </summary>
    public static Mat ResizeUtilH(Mat image, double uHeight, double? uWidth = null)
    {
        var width = uWidth ?? (int)(image.Cols * uHeight / image.Rows);
        var dst = new Mat();
        Cv2.Resize(image, dst, new Size((int)width, (int)uHeight));
        return dst;
    }

    /// <summary>
    /// MinMax 归一化到 [0, 255]。边界：常量图像（max == min）会被归一为全 0，同 OpenCV 行为。
    /// </summary>
    public static Mat NormalizeUtil(Mat image)
    {
        var dst = new Mat();
        Cv2.Normalize(image, dst, 0, 255, NormTypes.MinMax);
        return dst;
    }

    /// <summary>基于中值自动选取阈值的 Canny 边缘检测（保留以与 Python 版接口一致）。</summary>
    public static Mat AutoCanny(Mat image, double sigma = 0.93)
    {
        var median = Median(image);
        var lower = (int)Math.Max(0, (1.0 - sigma) * median);
        var upper = (int)Math.Min(255, (1.0 + sigma) * median);
        var edged = new Mat();
        Cv2.Canny(image, edged, lower, upper);
        return edged;
    }

    /// <summary>
    /// Gamma 校正：通过查找表映射 [0,255]，结果按 numpy astype("uint8") 截断。
    /// </summary>
    public static Mat AdjustGamma(Mat image, double gamma = 1.0)
    {
        var invGamma = 1.0 / gamma;
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            table[i] = (byte)(Math.Pow(i / 255.0, invGamma) * 255);
        }

        return ApplyLut(image, table);
    }

    /// <summary>对 8 位图像应用 256 项查找表。</summary>
    public static Mat ApplyLut(Mat image, byte[] table)
    {
        using var lut = Mat.FromPixelData(1, 256, MatType.CV_8UC1, table);
        var dst = new Mat();
        Cv2.LUT(image, lut, dst);
        return dst;
    }

    /// <summary>
    /// 四点透视变换（“鸟瞰”矫正）：先对 4 个点排序为 左上/右上/右下/左下，
    /// 再以两对边的最大长度作为输出宽高。
    /// </summary>
    public static Mat FourPointTransform(Mat image, IReadOnlyList<Point2f> pts)
    {
        var rect = OrderPoints(pts);
        var (tl, tr, br, bl) = (rect[0], rect[1], rect[2], rect[3]);

        var widthA = Math.Sqrt(Math.Pow(br.X - bl.X, 2) + Math.Pow(br.Y - bl.Y, 2));
        var widthB = Math.Sqrt(Math.Pow(tr.X - tl.X, 2) + Math.Pow(tr.Y - tl.Y, 2));
        var maxWidth = Math.Max((int)widthA, (int)widthB);

        var heightA = Math.Sqrt(Math.Pow(tr.X - br.X, 2) + Math.Pow(tr.Y - br.Y, 2));
        var heightB = Math.Sqrt(Math.Pow(tl.X - bl.X, 2) + Math.Pow(tl.Y - bl.Y, 2));
        var maxHeight = Math.Max((int)heightA, (int)heightB);

        var dst = new[]
        {
            new Point2f(0, 0),
            new Point2f(maxWidth - 1, 0),
            new Point2f(maxWidth - 1, maxHeight - 1),
            new Point2f(0, maxHeight - 1),
        };

        using var transform = Cv2.GetPerspectiveTransform(rect, dst);
        var warped = new Mat();
        Cv2.WarpPerspective(image, warped, transform, new Size(maxWidth, maxHeight));
        return warped;
    }

    /// <summary>
    /// 将 4 个点排序为 左上、右上、右下、左下：
    /// x+y 最小为左上、最大为右下；y-x 最小为右上、最大为左下（并列时取第一个）。
    /// </summary>
    public static Point2f[] OrderPoints(IReadOnlyList<Point2f> pts)
    {
        var sums = pts.Select(p => p.X + p.Y).ToArray();
        var diffs = pts.Select(p => p.Y - p.X).ToArray();
        return new[]
        {
            pts[ArgMin(sums)],
            pts[ArgMin(diffs)],
            pts[ArgMax(sums)],
            pts[ArgMax(diffs)],
        };
    }

    /// <summary>
    /// 解析用户页码设置为 1 起始的页码列表（保持顺序并去重）：
    /// null → 全部页；3 → [3]；"3" → [3]；"1-5" → [1..5]；"3-" → [3..末页]；数组 → 逐项展开。
    /// 边界：此处不做越界过滤，由调用方处理。
    /// </summary>
    public static List<int> ResolvePages(JsonNode? pageSpec, int docLength)
    {
        switch (pageSpec)
        {
            case null:
                return Enumerable.Range(1, docLength).ToList();
            case JsonArray array:
            {
                var result = new List<int>();
                var seen = new HashSet<int>();
                foreach (var item in array)
                {
                    foreach (var page in ResolvePages(item, docLength))
                    {
                        if (seen.Add(page))
                        {
                            result.Add(page);
                        }
                    }
                }

                return result;
            }

            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                return new List<int> { JsonUtils.GetInt(value, 1) };
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
            {
                var spec = value.GetValue<string>();
                if (spec.Contains('-'))
                {
                    var parts = spec.Split(new[] { '-' }, 2);
                    var start = parts[0].Length > 0 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 1;
                    var end = parts[1].Length > 0 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : docLength;
                    return start > end ? new List<int>() : Enumerable.Range(start, end - start + 1).ToList();
                }

                return new List<int> { int.Parse(spec, CultureInfo.InvariantCulture) };
            }

            default:
                return new List<int>();
        }
    }

    /// <summary>
    /// 加载 OMR 图像，返回（显示名, 灰度图）列表。
    /// 普通图片返回 1 项；PDF 按 pdf_params 渲染指定页，多页 PDF 命名为 {文件名}_p{页码}.png。
    /// 边界：PDF 打开或渲染失败时记录错误并返回空列表；图片无法解码时同样返回空列表。
    /// </summary>
    public static List<(string Name, Mat Image)> LoadOmrImage(string filePath, TuningConfig tuningConfig)
    {
        if (Path.GetExtension(filePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return LoadPdfPages(filePath, tuningConfig);
        }

        var image = ImReadGray(filePath);
        if (image is null)
        {
            Logger.Error($"Failed to read image: '{filePath}'");
            return new List<(string, Mat)>();
        }

        return new List<(string, Mat)> { (Path.GetFileName(filePath), image) };
    }

    /// <summary>
    /// 渲染 PDF 的指定页为灰度图。页数与内嵌图片信息由 PdfPig 读取，渲染由 PDFium（Docnet）完成，
    /// 透明背景按白底合成。
    /// </summary>
    private static List<(string Name, Mat Image)> LoadPdfPages(string filePath, TuningConfig tuningConfig)
    {
        var fileName = Path.GetFileName(filePath);
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var pdfParams = tuningConfig.PdfParams;
        var images = new List<(string, Mat)>();

        try
        {
            int docLength;
            var pages = new List<int>();
            int dpi;
            using (var document = PdfDocument.Open(filePath))
            {
                docLength = document.NumberOfPages;
                Logger.Info(
                    $"Processing PDF '{fileName}' (pdf_dpi={DescribeNode(pdfParams.PdfDpi)}, pdf_page={DescribeNode(pdfParams.PdfPage)})");

                // 用户页码为 1 起始，转换为 0 起始并过滤越界页
                foreach (var page in ResolvePages(pdfParams.PdfPage, docLength))
                {
                    if (page < 1 || page > docLength)
                    {
                        Logger.Warning(
                            $"PDF page {page} out of range for '{filePath}' (has {docLength} pages), skipping page.");
                    }
                    else
                    {
                        pages.Add(page - 1);
                    }
                }

                if (pdfParams.PdfDpi is JsonValue dpiValue && dpiValue.GetValueKind() == JsonValueKind.String)
                {
                    dpi = DetectPdfNativeDpi(document, pages);
                    Logger.Info($"Auto-detected PDF native DPI: {dpi}");
                }
                else
                {
                    dpi = JsonUtils.GetInt(pdfParams.PdfDpi, 72);
                }
            }

            using var reader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(dpi / 72.0));
            foreach (var pageIndex in pages)
            {
                using var pageReader = reader.GetPageReader(pageIndex);
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();
                var bgra = pageReader.GetImage();
                var name = docLength > 1 ? $"{stem}_p{pageIndex + 1}.png" : $"{stem}.png";
                images.Add((name, BgraToGrayOnWhite(bgra, width, height)));
            }
        }
        catch (Exception e)
        {
            Logger.Error($"Failed to render PDF: '{filePath}' - {e.Message}");
            return new List<(string, Mat)>();
        }

        return images;
    }

    /// <summary>
    /// 推断 PDF 原生 DPI：以内嵌图片像素尺寸对比页面尺寸（单位 pt，1pt = 1/72 英寸），
    /// 取所有图片中最大的 DPI。边界：没有内嵌图片时返回 72。
    /// </summary>
    private static int DetectPdfNativeDpi(PdfDocument document, IEnumerable<int> pages)
    {
        var dpis = new List<int>();
        foreach (var p in pages)
        {
            var page = document.GetPage(p + 1);
            var pageWidth = page.Width;
            var pageHeight = page.Height;
            foreach (var image in page.GetImages())
            {
                var dpiW = (int)(image.WidthInSamples * 72 / pageWidth);
                var dpiH = (int)(image.HeightInSamples * 72 / pageHeight);
                dpis.Add(Math.Max(dpiW, dpiH));
            }
        }

        return dpis.Count > 0 ? dpis.Max() : 72;
    }

    /// <summary>将 PDFium 输出的 BGRA 像素按 alpha 合成到白底并转为灰度。</summary>
    private static Mat BgraToGrayOnWhite(byte[] bgra, int width, int height)
    {
        using var source = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra);
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
        using var alpha = new Mat();
        Cv2.ExtractChannel(source, alpha, 3);

        using var grayF = new Mat();
        using var alphaF = new Mat();
        gray.ConvertTo(grayF, MatType.CV_32F);
        alpha.ConvertTo(alphaF, MatType.CV_32F, 1.0 / 255);

        // 结果 = 灰度 * a + 255 * (1 - a)
        using var weighted = new Mat();
        Cv2.Multiply(grayF, alphaF, weighted);
        using var inverseAlpha = new Mat();
        Cv2.Subtract(new Scalar(1.0), alphaF, inverseAlpha);
        using var composed = new Mat();
        Cv2.ScaleAdd(inverseAlpha, 255.0, weighted, composed);

        var result = new Mat();
        composed.ConvertTo(result, MatType.CV_8U);
        return result;
    }

    /// <summary>以 Python 风格描述 JSON 节点（用于日志）。</summary>
    private static string DescribeNode(JsonNode? node) =>
        node is null ? "None" : PyFormat.Str(JsonUtils.ToPlainObject(node));

    /// <summary>计算 8 位单通道图像的中值（同 numpy.median）。</summary>
    private static double Median(Mat image)
    {
        image.GetArray(out byte[] data);
        if (data.Length == 0)
        {
            return 0;
        }

        Array.Sort(data);
        var mid = data.Length / 2;
        return data.Length % 2 == 1 ? data[mid] : (data[mid - 1] + data[mid]) / 2.0;
    }

    /// <summary>返回最小值的下标（并列时取第一个）。</summary>
    private static int ArgMin(float[] values)
    {
        var index = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] < values[index])
            {
                index = i;
            }
        }

        return index;
    }

    /// <summary>返回最大值的下标（并列时取第一个）。</summary>
    private static int ArgMax(float[] values)
    {
        var index = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[index])
            {
                index = i;
            }
        }

        return index;
    }
}
