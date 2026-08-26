using System;

namespace BetterGenshinImpact.GameTask.AutoFight;

internal static class GuardianSkillSwitchPolicy
{
    private const int MaxAttemptCount = 2;

    internal static int NormalizeAttemptCount(int requestedAttemptCount)
    {
        return Math.Clamp(requestedAttemptCount, 1, MaxAttemptCount);
    }

    internal static bool ShouldSkipDuplicateSkill(
        bool guardianSkillHandled,
        bool commandTargetsGuardian,
        bool isSkillCommand)
    {
        return guardianSkillHandled && commandTargetsGuardian && isSkillCommand;
    }

    internal static bool ShouldEnsureGuardianSkill(bool guardianSkillHandled, bool shouldSwitch)
    {
        return !guardianSkillHandled && shouldSwitch;
    }
}
