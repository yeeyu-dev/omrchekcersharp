// OMRChecker C# 版
// 原作者: Udayraj Deshmukh (https://github.com/Udayraj123/OMRChecker)，本项目为其 Python 版的 .NET 移植。

using System.Text;
using OMRChecker;
using OMRChecker.Logging;

Console.OutputEncoding = Encoding.UTF8;
return Cli.Run(args);

namespace OMRChecker
{
    /// <summary>命令行入口，参数与 Python 版 main.py 保持一致。</summary>
    public static class Cli
    {
        private const string HelpText = """
            usage: OMRChecker [-h] [-i [INPUT_PATHS ...]] [-d] [-o OUTPUT_DIR] [-a] [-l]

            options:
              -h, --help            show this help message and exit
              -i [INPUT_PATHS ...], --inputDir [INPUT_PATHS ...]
                                    Specify an input directory.
              -d, --debug           Enables debugging mode for showing detailed errors
              -o OUTPUT_DIR, --outputDir OUTPUT_DIR
                                    Specify an output directory.
              -a, --autoAlign       (experimental) Enables automatic template alignment - use if the scans show
                                    slight misalignments.
              -l, --setLayout       Set up OMR template layout - modify your json file and run again until the
                                    template is set.
            """;

        /// <summary>
        /// 解析参数并运行。返回进程退出码：0 成功；1 处理出错；11 存在未知参数。
        /// </summary>
        public static int Run(string[] argv)
        {
            var (args, unknown, showHelp) = ParseArgs(argv);
            if (showHelp)
            {
                Console.WriteLine(HelpText);
                return 0;
            }

            if (unknown.Count > 0)
            {
                Logger.Warning($"\nError: Unknown arguments: [{string.Join(", ", unknown.Select(u => $"'{u}'"))}]");
                Console.WriteLine(HelpText);
                return 11;
            }

            try
            {
                foreach (var root in args.InputPaths)
                {
                    Entry.EntryPoint(root, args);
                }

                return 0;
            }
            catch (Exception e)
            {
                // 默认只输出异常消息（同 Python 版 sys.tracebacklimit = 0），-d 时输出完整堆栈
                Console.Error.WriteLine(args.Debug ? e.ToString() : $"{e.GetType().Name}: {e.Message}");
                return 1;
            }
        }

        /// <summary>
        /// 解析命令行参数（argparse 风格）：-i 可跟零个或多个目录，直到遇到下一个以 - 开头的参数。
        /// </summary>
        internal static (CliArgs Args, List<string> Unknown, bool ShowHelp) ParseArgs(string[] argv)
        {
            var args = new CliArgs();
            var unknown = new List<string>();
            var showHelp = false;

            for (var i = 0; i < argv.Length; i++)
            {
                switch (argv[i])
                {
                    case "-h":
                    case "--help":
                        showHelp = true;
                        break;
                    case "-i":
                    case "--inputDir":
                        args.InputPaths = new List<string>();
                        while (i + 1 < argv.Length && !argv[i + 1].StartsWith("-", StringComparison.Ordinal))
                        {
                            args.InputPaths.Add(argv[++i]);
                        }

                        break;
                    case "-d":
                    case "--debug":
                        args.Debug = true;
                        break;
                    case "-o":
                    case "--outputDir":
                        if (i + 1 < argv.Length)
                        {
                            args.OutputDir = argv[++i];
                        }
                        else
                        {
                            unknown.Add(argv[i]);
                        }

                        break;
                    case "-a":
                    case "--autoAlign":
                        args.AutoAlign = true;
                        break;
                    case "-l":
                    case "--setLayout":
                        args.SetLayout = true;
                        break;
                    default:
                        unknown.Add(argv[i]);
                        break;
                }
            }

            return (args, unknown, showHelp);
        }
    }
}
