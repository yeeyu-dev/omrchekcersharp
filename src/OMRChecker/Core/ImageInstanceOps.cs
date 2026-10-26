using OMRChecker.Config;
using OMRChecker.Constants;
using OMRChecker.Logging;
using OMRChecker.Utils;
using OpenCvSharp;

namespace OMRChecker.Core;

/// <summary>一次识别的结果。</summary>
/// <param name="Response">字段标签 → 识别出的值（多涂时为多个值拼接）。</param>
/// <param name="FinalMarked">叠加了识别标注的图像。</param>
/// <param name="MultiMarked">是否存在多涂字段。</param>
/// <param name="MultiRoll">保留字段，恒为 false（与 Python 版一致）。</param>
public sealed record OmrReadResult(Dictionary<string, string> Response, Mat FinalMarked, bool MultiMarked, bool MultiRoll);

/// <summary>
/// 一组图像（同一模板）共用的识别流程，对应 Python 版 src/core.py 中的 ImageInstanceOps：
/// 预处理 → 缩放到模板尺寸 → （可选）自动对齐 → 计算每个气泡平均灰度 → 全局/局部阈值判定 → 绘制标注。
/// </summary>
public sealed class ImageInstanceOps
{
    private static bool _plotWarningShown;
    private readonly Dictionary<int, List<Mat>> _saveImgList = new();

    public ImageInstanceOps(TuningConfig tuningConfig)
    {
        TuningConfig = tuningConfig;
        SaveImageLevel = tuningConfig.Outputs.SaveImageLevel;
    }

    public TuningConfig TuningConfig { get; }

    public int SaveImageLevel { get; }

    /// <summary>
    /// 先缩放到 processing 尺寸，再依次执行模板中的预处理器。
    /// 边界：任一预处理器返回 null（如未找到标记 / 页面边界）时立即停止并返回 null。
    /// </summary>
    public Mat? ApplyPreprocessors(string filePath, Mat inOmr, Template template)
    {
        var dims = TuningConfig.Dimensions;
        Mat? current = ImageUtils.ResizeUtil(inOmr, dims.ProcessingWidth, dims.ProcessingHeight);
        foreach (var preProcessor in template.PreProcessors)
        {
            current = preProcessor.ApplyFilter(current, filePath);
            if (current is null)
            {
                break;
            }
        }

        return current;
    }

    /// <summary>
    /// 读取 OMR 响应：核心识别算法。
    /// </summary>
    /// <param name="template">模板。</param>
    /// <param name="image">已预处理的灰度图。</param>
    /// <param name="name">文件名（用于保存标注图）。</param>
    /// <param name="saveDir">标注图保存目录；为 null 时不保存（如从图片生成答案）。</param>
    public OmrReadResult ReadOmrResponse(Template template, Mat image, string name, string? saveDir = null)
    {
        var config = TuningConfig;
        var outputs = config.Outputs;
        var autoAlign = config.AlignmentParams.AutoAlign;

        var img = ImageUtils.ResizeUtil(image, template.PageDimensions[0], template.PageDimensions[1]);
        Cv2.MinMaxLoc(img, out double minValue, out double maxValue);
        if (maxValue > minValue)
        {
            img = ImageUtils.NormalizeUtil(img);
        }

        // 处理副本：透明叠加底图与最终标注图
        using var transpLayer = img.Clone();
        var finalMarked = img.Clone();

        var morph = img.Clone();
        AppendSaveImg(3, morph);

        if (autoAlign)
        {
            // 注意：CLAHE 适合形态学处理，不适合阈值判定
            var clahe = new Mat();
            ImageUtils.ClaheHelper.Apply(morph, clahe);
            morph = clahe;
            AppendSaveImg(3, morph);
            // 进一步去阴影，使列/框更暗（降低 gamma）
            morph = ImageUtils.AdjustGamma(morph, config.ThresholdParams.GammaLow);
            Cv2.Threshold(morph, morph, 220, 220, ThresholdTypes.Trunc);
            morph = ImageUtils.NormalizeUtil(morph);
            AppendSaveImg(3, morph);
            if (outputs.ShowImageLevel >= 4)
            {
                InteractionUtils.Show("morph1", morph, pause: false, resize: true, config: config);
            }
        }

        const double alpha = 0.65;
        var omrResponse = new Dictionary<string, string>();
        var multiMarked = false;
        const bool multiRoll = false;

        // 在计算阈值之前先求出各字段块的水平偏移
        if (autoAlign)
        {
            AlignFieldBlocks(template, morph);
        }

        Mat? finalAlign = null;
        if (outputs.ShowImageLevel >= 2)
        {
            var initialAlign = DrawTemplateLayout(img, template, shifted: false);
            finalAlign = DrawTemplateLayout(img, template, shifted: true, drawQvals: true);
            AppendSaveImg(2, initialAlign);
            AppendSaveImg(2, finalAlign);

            if (autoAlign)
            {
                var stacked = new Mat();
                Cv2.HConcat(new[] { initialAlign, finalAlign }, stacked);
                finalAlign = stacked;
            }
        }

        AppendSaveImg(5, img);

        // 统计每个气泡的平均灰度，以及每条条带的标准差
        var allQVals = new List<double>();
        var allQStripArrs = new List<List<double>>();
        var allQStdVals = new List<double>();
        foreach (var fieldBlock in template.FieldBlocks)
        {
            var (boxW, boxH) = (fieldBlock.BubbleDimensions[0], fieldBlock.BubbleDimensions[1]);
            foreach (var fieldBlockBubbles in fieldBlock.TraverseBubbles)
            {
                var qStripVals = fieldBlockBubbles
                    .Select(pt => BubbleMean(img, pt.X + fieldBlock.Shift, pt.Y, boxW, boxH))
                    .ToList();
                allQStdVals.Add(PyFormat.Round(StdDev(qStripVals), 2));
                allQStripArrs.Add(qStripVals);
                allQVals.AddRange(qStripVals);
            }
        }

        var globalStdThresh = GetGlobalThreshold(allQStdVals);
        var globalThr = GetGlobalThreshold(allQVals, looseness: 4);

        Logger.Info(
            $"Thresholding: \tglobal_thr: {PyFormat.Float(PyFormat.Round(globalThr, 2))} \tglobal_std_THR: {PyFormat.Float(PyFormat.Round(globalStdThresh, 2))}\t{(globalThr == 255 ? "(Looks like a Xeroxed OMR)" : string.Empty)}");

        if (outputs.ShowImageLevel >= 6 && !_plotWarningShown)
        {
            _plotWarningShown = true;
            Logger.Warning("show_image_level >= 6 的 matplotlib 统计图在 C# 版中未移植，已跳过。");
        }

        double perOmrThresholdAvg = 0;
        int totalQStripNo = 0, totalQBoxNo = 0;
        foreach (var fieldBlock in template.FieldBlocks)
        {
            var (boxW, boxH) = (fieldBlock.BubbleDimensions[0], fieldBlock.BubbleDimensions[1]);
            foreach (var fieldBlockBubbles in fieldBlock.TraverseBubbles)
            {
                // 全黑或全白条带（无离群值）时局部阈值会退回全局阈值
                var noOutliers = allQStdVals[totalQStripNo] < globalStdThresh;
                var perQStripThreshold = GetLocalThreshold(allQStripArrs[totalQStripNo], globalThr, noOutliers);
                perOmrThresholdAvg += perQStripThreshold;

                var detectedBubbles = new List<Bubble>();
                foreach (var bubble in fieldBlockBubbles)
                {
                    var bubbleIsMarked = perQStripThreshold > allQVals[totalQBoxNo];
                    totalQBoxNo++;
                    var (x, y) = (bubble.X + fieldBlock.Shift, bubble.Y);
                    if (bubbleIsMarked)
                    {
                        detectedBubbles.Add(bubble);
                        Cv2.Rectangle(finalMarked,
                            new Point((int)(x + (boxW / 12.0)), (int)(y + (boxH / 12.0))),
                            new Point((int)(x + boxW - (boxW / 12.0)), (int)(y + boxH - (boxH / 12.0))),
                            CommonConstants.ClrDarkGray, 3);
                        Cv2.PutText(finalMarked, bubble.FieldValue, new Point(x, y), HersheyFonts.HersheySimplex,
                            CommonConstants.TextSize, new Scalar(20, 20, 10), (int)(1 + (3.5 * CommonConstants.TextSize)));
                    }
                    else
                    {
                        // 说明：Python 版这里误用了上一个已涂气泡的坐标，C# 版改为当前气泡坐标
                        Cv2.Rectangle(finalMarked,
                            new Point((int)(x + (boxW / 10.0)), (int)(y + (boxH / 10.0))),
                            new Point((int)(x + boxW - (boxW / 10.0)), (int)(y + boxH - (boxH / 10.0))),
                            CommonConstants.ClrGray, -1);
                    }
                }

                foreach (var bubble in detectedBubbles)
                {
                    // 同一字段检测到多个已涂气泡 → 多涂，取值按顺序拼接
                    if (omrResponse.TryGetValue(bubble.FieldLabel, out var existing))
                    {
                        omrResponse[bubble.FieldLabel] = existing + bubble.FieldValue;
                        multiMarked = true;
                    }
                    else
                    {
                        omrResponse[bubble.FieldLabel] = bubble.FieldValue;
                    }
                }

                if (detectedBubbles.Count == 0)
                {
                    omrResponse[fieldBlockBubbles[0].FieldLabel] = fieldBlock.EmptyVal;
                }

                totalQStripNo++;
            }
        }

        if (totalQStripNo > 0)
        {
            perOmrThresholdAvg = PyFormat.Round(perOmrThresholdAvg / totalQStripNo, 2);
        }

        // 半透明叠加
        Cv2.AddWeighted(finalMarked, alpha, transpLayer, 1 - alpha, 0, finalMarked);

        if (outputs.ShowImageLevel >= 3 && finalAlign is not null)
        {
            finalAlign = ImageUtils.ResizeUtilH(finalAlign, config.Dimensions.DisplayHeight);
            InteractionUtils.Show("Template Alignment Adjustment", finalAlign, pause: false, config: config);
        }

        if (outputs.SaveDetections && saveDir is not null)
        {
            var targetDir = multiRoll ? Path.Combine(saveDir, "_MULTI_") : saveDir;
            ImageUtils.SaveImg(Path.Combine(targetDir, name), finalMarked);
        }

        AppendSaveImg(2, finalMarked);

        if (saveDir is not null)
        {
            for (var i = 0; i < outputs.SaveImageLevel; i++)
            {
                SaveImageStacks(i + 1, name, saveDir);
            }
        }

        return new OmrReadResult(omrResponse, finalMarked, multiMarked, multiRoll);
    }

    /// <summary>
    /// 绘制模板布局：字段块外框、每个气泡框，可选在气泡上标注平均灰度值。
    /// </summary>
    /// <param name="img">原图（会先缩放到模板尺寸）。</param>
    /// <param name="template">模板。</param>
    /// <param name="shifted">是否应用自动对齐偏移。</param>
    /// <param name="drawQvals">是否标注每个气泡的平均灰度。</param>
    /// <param name="border">气泡框线宽，-1 为填充。</param>
    public static Mat DrawTemplateLayout(Mat img, Template template, bool shifted = true, bool drawQvals = false,
        int border = -1)
    {
        var resized = ImageUtils.ResizeUtil(img, template.PageDimensions[0], template.PageDimensions[1]);
        var finalAlign = resized.Clone();
        foreach (var fieldBlock in template.FieldBlocks)
        {
            var (s, d) = (fieldBlock.Origin, fieldBlock.Dimensions);
            var (boxW, boxH) = (fieldBlock.BubbleDimensions[0], fieldBlock.BubbleDimensions[1]);
            var shift = shifted ? fieldBlock.Shift : 0;
            Cv2.Rectangle(finalAlign, new Point(s[0] + shift, s[1]), new Point(s[0] + shift + d[0], s[1] + d[1]),
                CommonConstants.ClrBlack, 3);

            foreach (var fieldBlockBubbles in fieldBlock.TraverseBubbles)
            {
                foreach (var pt in fieldBlockBubbles)
                {
                    var (x, y) = (pt.X + shift, pt.Y);
                    Cv2.Rectangle(finalAlign,
                        new Point((int)(x + (boxW / 10.0)), (int)(y + (boxH / 10.0))),
                        new Point((int)(x + boxW - (boxW / 10.0)), (int)(y + boxH - (boxH / 10.0))),
                        CommonConstants.ClrGray, border);
                    if (drawQvals)
                    {
                        var mean = (int)BubbleMean(resized, x, y, boxW, boxH);
                        Cv2.PutText(finalAlign, mean.ToString(), new Point(x + 2, y + (boxH * 2 / 3)),
                            HersheyFonts.HersheySimplex, 0.6, CommonConstants.ClrBlack, 2);
                    }
                }
            }

            if (shifted)
            {
                var textSize = Cv2.GetTextSize(fieldBlock.Name, HersheyFonts.HersheySimplex, CommonConstants.TextSize, 4,
                    out _);
                Cv2.PutText(finalAlign, fieldBlock.Name, new Point(s[0] + d[0] - textSize.Width, s[1] - textSize.Height),
                    HersheyFonts.HersheySimplex, CommonConstants.TextSize, CommonConstants.ClrBlack, 4);
            }
        }

        return finalAlign;
    }

    /// <summary>
    /// 全局阈值：在排序后的取值中寻找第一个“大跳变”（跨度为 looseness 的最大差值），取跳变中点。
    /// 跳变都不超过 MIN_JUMP 时返回纸张类型对应的兜底阈值（白纸 200 / 黑纸 100）。
    /// 边界：取值数量不足 2*ls+1 时同样返回兜底阈值。
    /// </summary>
    /// <remarks>Python 版中的 thr2 / JUMP_DELTA 已废弃且只用于绘图，此处不再计算。</remarks>
    public double GetGlobalThreshold(IReadOnlyList<double> qValsOrig, int looseness = 1)
    {
        var tp = TuningConfig.ThresholdParams;
        var globalDefaultThreshold = tp.PageTypeForThreshold == "white"
            ? CommonConstants.GlobalPageThresholdWhite
            : CommonConstants.GlobalPageThresholdBlack;

        var qVals = qValsOrig.Order().ToList();
        var ls = (looseness + 1) / 2;
        var l = qVals.Count - ls;
        double max1 = tp.MinJump, thr1 = globalDefaultThreshold;
        for (var i = ls; i < l; i++)
        {
            var jump = qVals[i + ls] - qVals[i - ls];
            if (jump > max1)
            {
                max1 = jump;
                thr1 = qVals[i - ls] + (jump / 2);
            }
        }

        return thr1;
    }

    /// <summary>
    /// 局部（单条带）阈值：
    /// 少于 3 个气泡时，极差小于 MIN_GAP 用全局阈值，否则取均值；
    /// 否则寻找最大跳变取中点（初值 255），若最大跳变不够“自信”（&lt; MIN_JUMP + CONFIDENT_SURPLUS）
    /// 且条带无离群值（全涂或全空），退回全局阈值。
    /// </summary>
    public double GetLocalThreshold(IReadOnlyList<double> qValsInput, double globalThr, bool noOutliers)
    {
        var tp = TuningConfig.ThresholdParams;
        var qVals = qValsInput.Order().ToList();

        if (qVals.Count < 3)
        {
            return qVals.Max() - qVals.Min() < tp.MinGap ? globalThr : qVals.Average();
        }

        var l = qVals.Count - 1;
        double max1 = tp.MinJump, thr1 = 255;
        for (var i = 1; i < l; i++)
        {
            var jump = qVals[i + 1] - qVals[i - 1];
            if (jump > max1)
            {
                max1 = jump;
                thr1 = qVals[i - 1] + (jump / 2);
            }
        }

        var confidentJump = tp.MinJump + tp.ConfidentSurplus;
        if (max1 < confidentJump && noOutliers)
        {
            thr1 = globalThr;
        }

        return thr1;
    }

    /// <summary>当 save_image_level ≥ key 时，记录一张中间图像的副本。</summary>
    public void AppendSaveImg(int key, Mat img)
    {
        if (SaveImageLevel >= key)
        {
            if (!_saveImgList.TryGetValue(key, out var list))
            {
                list = new List<Mat>();
                _saveImgList[key] = list;
            }

            list.Add(img.Clone());
        }
    }

    /// <summary>
    /// 将某级别的中间图像横向拼接后保存到 {saveDir}/stack/{name}_{key}_stack.jpg。
    /// 说明：Python 版拼接路径时漏了分隔符（写成 CheckedOMRsstack/），C# 版已修正。
    /// </summary>
    public void SaveImageStacks(int key, string filename, string saveDir)
    {
        var dims = TuningConfig.Dimensions;
        if (SaveImageLevel < key || !_saveImgList.TryGetValue(key, out var list) || list.Count == 0)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(filename);
        var resized = list.Select(m => ImageUtils.ResizeUtilH(m, dims.DisplayHeight)).ToArray();
        using var result = new Mat();
        Cv2.HConcat(resized, result);
        var targetWidth = Math.Min(list.Count * dims.DisplayWidth / 3, (int)(dims.DisplayWidth * 2.5));
        using var final = ImageUtils.ResizeUtil(result, targetWidth);
        ImageUtils.SaveImg(Path.Combine(saveDir, "stack", $"{name}_{key}_stack.jpg"), final);
        foreach (var m in resized)
        {
            m.Dispose();
        }
    }

    /// <summary>每张图像开始处理前清空中间图像缓存。</summary>
    public void ResetAllSaveImg()
    {
        foreach (var list in _saveImgList.Values)
        {
            foreach (var m in list)
            {
                m.Dispose();
            }
        }

        _saveImgList.Clear();
    }

    /// <summary>
    /// 自动对齐：对竖直方向做开运算提取列线，然后对每个字段块左右试探移动，
    /// 直到两侧边缘的平均亮度达到平衡（或达到 max_steps），结果写入 FieldBlock.Shift。
    /// </summary>
    private void AlignFieldBlocks(Template template, Mat morph)
    {
        var config = TuningConfig;
        var outputs = config.Outputs;

        using var vKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2, 10));
        var morphV = new Mat();
        Cv2.MorphologyEx(morph, morphV, MorphTypes.Open, vKernel, iterations: 3);
        Cv2.Threshold(morphV, morphV, 200, 200, ThresholdTypes.Trunc);
        using (var normalized = ImageUtils.NormalizeUtil(morphV))
        {
            // 8 位图像上 255 - x 等价于按位取反
            Cv2.BitwiseNot(normalized, morphV);
        }

        if (outputs.ShowImageLevel >= 3)
        {
            InteractionUtils.Show("morphed_vertical", morphV, pause: false, resize: true, config: config);
        }

        AppendSaveImg(3, morphV);

        const int morphThr = 60; // 手机拍摄 60，扫描件 40
        Cv2.Threshold(morphV, morphV, morphThr, 255, ThresholdTypes.Binary);
        using (var kernel = new Mat(5, 5, MatType.CV_8UC1, Scalar.All(1)))
        {
            Cv2.Erode(morphV, morphV, kernel, iterations: 2);
        }

        AppendSaveImg(3, morphV);
        if (outputs.ShowImageLevel >= 3)
        {
            InteractionUtils.Show("morph_thr_eroded", morphV, pause: false, resize: true, config: config);
        }

        AppendSaveImg(6, morphV);

        var ap = config.AlignmentParams;
        foreach (var fieldBlock in template.FieldBlocks)
        {
            var (s, d) = (fieldBlock.Origin, fieldBlock.Dimensions);
            int shift = 0, steps = 0;
            while (steps < ap.MaxSteps)
            {
                var leftMean = PySliceMean(morphV, s[1], s[1] + d[1],
                    s[0] + shift - ap.Thickness, -ap.Thickness + s[0] + shift + ap.MatchCol);
                var rightMean = PySliceMean(morphV, s[1], s[1] + d[1],
                    s[0] + shift - ap.MatchCol + d[0] + ap.Thickness, ap.Thickness + s[0] + shift + d[0]);

                // NaN（空切片）与任何数比较都为 false，与 numpy 行为一致
                bool leftShift = leftMean > 100, rightShift = rightMean > 100;
                if (leftShift)
                {
                    if (rightShift)
                    {
                        break;
                    }

                    shift -= ap.Stride;
                }
                else
                {
                    if (!rightShift)
                    {
                        break;
                    }

                    shift += ap.Stride;
                }

                steps++;
            }

            fieldBlock.Shift = shift;
        }

        morphV.Dispose();
    }

    /// <summary>
    /// 计算气泡矩形区域的平均灰度，对应 cv2.mean(img[y:y+h, x:x+w])[0]。
    /// 边界：区域按 Python 切片语义裁剪到图像内；完全越界时返回 0。
    /// </summary>
    private static double BubbleMean(Mat img, int x, int y, int boxW, int boxH)
    {
        var rect = PySliceRect(img, y, y + boxH, x, x + boxW);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return 0;
        }

        using var roi = new Mat(img, rect);
        return Cv2.Mean(roi).Val0;
    }

    /// <summary>
    /// 计算 numpy 切片 img[r0:r1, c0:c1] 的均值（支持负下标回绕）。
    /// 边界：切片为空时返回 NaN（同 np.mean 空数组）。
    /// </summary>
    private static double PySliceMean(Mat img, int r0, int r1, int c0, int c1)
    {
        var rect = PySliceRect(img, r0, r1, c0, c1);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return double.NaN;
        }

        using var roi = new Mat(img, rect);
        return Cv2.Mean(roi).Val0;
    }

    /// <summary>按 Python 切片规则（负数回绕、越界裁剪）将行列区间转换为矩形。</summary>
    private static Rect PySliceRect(Mat img, int r0, int r1, int c0, int c1)
    {
        var (rs, re) = (PyIndex(r0, img.Rows), PyIndex(r1, img.Rows));
        var (cs, ce) = (PyIndex(c0, img.Cols), PyIndex(c1, img.Cols));
        return new Rect(cs, rs, Math.Max(0, ce - cs), Math.Max(0, re - rs));
    }

    /// <summary>Python 切片下标归一化：负数加上长度，然后裁剪到 [0, length]。</summary>
    private static int PyIndex(int index, int length)
    {
        if (index < 0)
        {
            index += length;
        }

        return Math.Min(Math.Max(index, 0), length);
    }

    /// <summary>总体标准差（ddof = 0，同 np.std）。</summary>
    private static double StdDev(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
    }
}
