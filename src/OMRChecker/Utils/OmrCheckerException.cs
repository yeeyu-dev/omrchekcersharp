namespace OMRChecker.Utils;

/// <summary>
/// 业务异常：模板 / 配置 / 评分文件不合法、输入目录不存在等。
/// 对应 Python 版中大量的 raise Exception(...)，异常消息保持一致以便测试比对。
/// </summary>
public sealed class OmrCheckerException : Exception
{
    public OmrCheckerException(string message)
        : base(message)
    {
    }
}
