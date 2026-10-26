using OMRChecker.Utils;
using Xunit;

// 样例测试会切换工作目录并读写共享的输出目录，禁止并行执行
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OMRChecker.Tests;

/// <summary>测试公共辅助方法。</summary>
internal static class TestHelpers
{
    private static readonly Lazy<string> RepoRootLazy = new(FindRepoRoot);

    /// <summary>仓库根目录（包含 samples/ 与 OMRChecker.sln 的目录）。</summary>
    public static string RepoRoot => RepoRootLazy.Value;

    /// <summary>测试数据文件（tests/OMRChecker.Tests/TestData/ 下）的绝对路径。</summary>
    public static string TestDataPath(string fileName) =>
        Path.Combine(RepoRoot, "tests", "OMRChecker.Tests", "TestData", fileName);

    /// <summary>
    /// 切换到仓库根目录并开启无界面模式（样例配置里 show_image_level 较高，避免弹窗阻塞）。
    /// </summary>
    public static void EnterRepoRoot()
    {
        InteractionUtils.Headless = true;
        Directory.SetCurrentDirectory(RepoRoot);
    }

    /// <summary>从测试程序集目录向上查找仓库根目录。</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "samples")) && File.Exists(Path.Combine(dir.FullName, "OMRChecker.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("未找到仓库根目录（需包含 samples/ 与 OMRChecker.sln）");
    }
}
