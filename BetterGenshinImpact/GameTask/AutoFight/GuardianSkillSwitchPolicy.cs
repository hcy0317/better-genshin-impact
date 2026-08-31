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

    internal static bool ShouldRetryBlock(
        bool guardianSkillRequired,
        bool guardianSkillHandled)
    {
        return guardianSkillRequired && !guardianSkillHandled;
    }

    internal static bool IsSkillCastConfirmed(
        bool baselineCooldownVisible,
        bool cooldownVisibleAfterInput,
        bool guardianStillActive)
    {
        return !baselineCooldownVisible
            && cooldownVisibleAfterInput
            && guardianStillActive;
    }

    internal static bool CanReuseConfirmedCooldown(
        bool skillReady,
        bool hasConfirmedSkillCooldown)
    {
        return !skillReady && hasConfirmedSkillCooldown;
    }
}
