using System;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal static class SereniteaPotExitPolicy
{
    public static bool ShouldRecoverMainUi(TalkOptionRes quitOption, bool isMainUi)
    {
        return quitOption != TalkOptionRes.FoundAndClick && !isMainUi;
    }

    public static void EnsureRecovered(bool isMainUi)
    {
        if (!isMainUi)
        {
            throw new InvalidOperationException("退出阿圆对话后仍未回到主界面。");
        }
    }
}
