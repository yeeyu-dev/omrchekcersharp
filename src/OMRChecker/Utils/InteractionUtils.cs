using System.Runtime.InteropServices;
using OMRChecker.Config;
using OMRChecker.Logging;
using OpenCvSharp;

namespace OMRChecker.Utils;

/// <summary>
/// 调试窗口显示，对应 Python 版 src/utils/interaction.py。
/// 窗口按屏幕从左到右、从上到下平铺，暂停时按 Q 或 Esc 继续。
/// </summary>
public static class InteractionUtils
{
    private static readonly (int Width, int Height) Screen = DetectScreenSize();
    private static int _windowX;
    private static int _windowY;

    /// <summary>
    /// 无界面模式：为 true 时所有显示调用直接跳过（自动化测试、服务器环境）。
    /// 也可通过环境变量 OMR_HEADLESS=1 开启。
    /// </summary>
    public static bool Headless { get; set; } = Environment.GetEnvironmentVariable("OMR_HEADLESS") == "1";

    /// <summary>
    /// 在窗口中显示图像。
    /// </summary>
    /// <param name="name">窗口标题。</param>
    /// <param name="origin">要显示的图像；为 null 时仅记录日志。</param>
    /// <param name="pause">是否阻塞等待 Q / Esc。</param>
    /// <param name="resize">是否按 display_width 缩放（需提供 config）。</param>
    /// <param name="resetPos">可选：重置下一个窗口的位置 [x, y]。</param>
    /// <param name="config">调优配置。</param>
    public static void Show(string name, Mat? origin, bool pause = true, bool resize = false, int[]? resetPos = null,
        TuningConfig? config = null)
    {
        if (Headless)
        {
            return;
        }

        if (origin is null)
        {
            Logger.Info($"'{name}' - NoneType image to show!");
            if (pause)
            {
                Cv2.DestroyAllWindows();
            }

            return;
        }

        Mat image;
        if (resize)
        {
            if (config is null)
            {
                throw new InvalidOperationException("config not provided for resizing the image to show");
            }

            image = ImageUtils.ResizeUtil(origin, config.Dimensions.DisplayWidth);
        }
        else
        {
            image = origin;
        }

        Cv2.NamedWindow(name);
        Cv2.ImShow(name, image);

        if (resetPos is not null)
        {
            _windowX = resetPos[0];
            _windowY = resetPos[1];
        }

        Cv2.MoveWindow(name, _windowX, _windowY);

        // 计算下一个窗口的位置
        const int margin = 25;
        var w = (image.Cols + margin) / 2;
        var h = (image.Rows + margin) / 2;
        if (_windowX + w > Screen.Width)
        {
            _windowX = 0;
            _windowY = _windowY + h > Screen.Height ? 0 : _windowY + h;
        }
        else
        {
            _windowX += w;
        }

        if (pause)
        {
            Logger.Info($"Showing '{name}'\n\t Press Q on image to continue. Press Ctrl + C in terminal to exit");
            WaitQ(name);
            _windowX = 0;
            _windowY = 0;
        }
    }

    /// <summary>
    /// 阻塞直到按下 Q / Esc，或用户关闭了该窗口（Python 版关闭窗口后会一直空转，这里顺带修正）。
    /// </summary>
    private static void WaitQ(string name)
    {
        const int escKey = 27;
        while (true)
        {
            var key = Cv2.WaitKey(50) & 0xFF;
            if (key == 'q' || key == escKey)
            {
                break;
            }

            try
            {
                if (Cv2.GetWindowProperty(name, WindowPropertyFlags.Visible) < 1)
                {
                    break;
                }
            }
            catch (OpenCVException)
            {
                break;
            }
        }

        Cv2.DestroyAllWindows();
    }

    /// <summary>获取主屏幕分辨率；非 Windows 平台返回 1920x1080。</summary>
    private static (int Width, int Height) DetectScreenSize()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return (GetSystemMetrics(0), GetSystemMetrics(1));
            }
            catch (DllNotFoundException)
            {
                // 忽略，使用默认值
            }
        }

        return (1920, 1080);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

/// <summary>处理统计（移动 / 未移动的文件数）。</summary>
public sealed class Stats
{
    public int FilesMoved { get; set; }

    public int FilesNotMoved { get; set; }
}
