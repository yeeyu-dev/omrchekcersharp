namespace OMRChecker.Logging;

/// <summary>日志级别，与 Python logging 的级别一一对应。</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Critical,
}

/// <summary>
/// 全局控制台日志器，对应 Python 版 src/logger.py（rich 的 RichHandler）。
/// 多个参数以空格拼接，null 输出为 "None"，与 Python 版 stringify 行为一致。
/// </summary>
public static class Logger
{
    private static readonly object SyncRoot = new();

    /// <summary>最低输出级别，默认 Info（与 Python basicConfig(level=INFO) 一致）。</summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>输出调试日志。</summary>
    public static void Debug(params object?[] msg) => Log(LogLevel.Debug, msg);

    /// <summary>输出普通信息日志。</summary>
    public static void Info(params object?[] msg) => Log(LogLevel.Info, msg);

    /// <summary>输出警告日志。</summary>
    public static void Warning(params object?[] msg) => Log(LogLevel.Warning, msg);

    /// <summary>输出错误日志。</summary>
    public static void Error(params object?[] msg) => Log(LogLevel.Error, msg);

    /// <summary>输出严重错误日志。</summary>
    public static void Critical(params object?[] msg) => Log(LogLevel.Critical, msg);

    /// <summary>
    /// 按级别着色输出一条日志；低于 <see cref="MinLevel"/> 的日志直接丢弃。
    /// 多行消息的后续行会对齐到消息起始列。
    /// </summary>
    private static void Log(LogLevel level, object?[] msg)
    {
        if (level < MinLevel)
        {
            return;
        }

        var text = string.Join(" ", msg.Select(m => m?.ToString() ?? "None"));
        var prefix = $"[{DateTime.Now:HH:mm:ss}] {LevelLabel(level),-8} ";
        text = text.Replace("\n", "\n" + new string(' ', prefix.Length));

        lock (SyncRoot)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
            Console.ForegroundColor = LevelColor(level);
            Console.Write($"{LevelLabel(level),-8} ");
            Console.ForegroundColor = previous;
            Console.WriteLine(text);
        }
    }

    /// <summary>级别对应的显示文字。</summary>
    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        _ => "CRITICAL",
    };

    /// <summary>级别对应的控制台颜色。</summary>
    private static ConsoleColor LevelColor(LogLevel level) => level switch
    {
        LogLevel.Debug => ConsoleColor.Green,
        LogLevel.Info => ConsoleColor.Blue,
        LogLevel.Warning => ConsoleColor.Yellow,
        LogLevel.Error => ConsoleColor.Red,
        _ => ConsoleColor.Magenta,
    };
}
