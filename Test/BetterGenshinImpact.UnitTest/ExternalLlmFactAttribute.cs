namespace BetterGenshinImpact.UnitTest;

/// <summary>真实 LLM 请求需要在测试进程中显式启用，默认发现为跳过而不是成功。</summary>
public sealed class ExternalLlmFactAttribute : FactAttribute
{
    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("BGI_TEST_ENABLE_LLM"), "1", StringComparison.Ordinal);

    public ExternalLlmFactAttribute()
    {
        if (!IsEnabled)
            Skip = "真实 LLM 集成未授权；仅显式设置 BGI_TEST_ENABLE_LLM=1 时执行。";
    }
}
