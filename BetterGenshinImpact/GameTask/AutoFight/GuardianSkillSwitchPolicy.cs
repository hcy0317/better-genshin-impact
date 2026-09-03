using System;

namespace BetterGenshinImpact.GameTask.AutoFight;

public enum GuardianAttemptResult
{
    ConfirmedNewCast,
    SwitchRejected,
    SkillRejected,
    UnavailableCooldown,
    Cancelled
}

public enum GuardianCoverageMode
{
    BestEffort,
    RequireKnownCoverage
}

public enum GuardianBoundaryAction
{
    ProceedProtected,
    ProceedUnprotected,
    Retry,
    FailCombat,
    Cancel
}

internal static class GuardianSkillSwitchPolicy
{
    private const int MaxAttemptCount = 2;
    internal static readonly TimeSpan DefaultRefreshReserve = TimeSpan.FromSeconds(2);

    internal static int NormalizeAttemptCount(int requestedAttemptCount)
    {
        return Math.Clamp(requestedAttemptCount, 1, MaxAttemptCount);
    }

    internal static bool ShouldSkipCoveredGuardianSkill(
        bool guardianSkillHandled,
        bool commandTargetsGuardian,
        bool isSkillCommand,
        DateTime lastConfirmedCastAtUtc,
        double? shieldDurationSeconds,
        DateTime nowUtc)
    {
        return guardianSkillHandled &&
               commandTargetsGuardian &&
               isSkillCommand &&
               IsShieldCoverageActive(lastConfirmedCastAtUtc, shieldDurationSeconds, nowUtc);
    }

    internal static bool IsShieldCoverageActive(
        DateTime lastConfirmedCastAtUtc,
        double? shieldDurationSeconds,
        DateTime nowUtc)
    {
        if (shieldDurationSeconds is not > 0 || lastConfirmedCastAtUtc == default)
        {
            return false;
        }

        return nowUtc < lastConfirmedCastAtUtc.AddSeconds(shieldDurationSeconds.Value);
    }

    internal static bool ShouldEnsureGuardianSkill(bool guardianSkillHandled, bool shouldSwitch)
    {
        return !guardianSkillHandled && shouldSwitch;
    }

    internal static bool IsCoverageConfigurationValid(
        GuardianCoverageMode coverageMode,
        bool guardianConfigured,
        double? shieldDurationSeconds)
    {
        return coverageMode != GuardianCoverageMode.RequireKnownCoverage ||
               guardianConfigured && shieldDurationSeconds is > 0;
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

    internal static GuardianBoundaryAction DecideBoundary(
        GuardianAttemptResult result,
        GuardianCoverageMode coverageMode,
        bool knownCoverageValid,
        int failedAttempts)
    {
        return result switch
        {
            GuardianAttemptResult.ConfirmedNewCast =>
                GuardianBoundaryAction.ProceedProtected,
            GuardianAttemptResult.Cancelled =>
                GuardianBoundaryAction.Cancel,
            GuardianAttemptResult.UnavailableCooldown when knownCoverageValid =>
                GuardianBoundaryAction.ProceedProtected,
            GuardianAttemptResult.UnavailableCooldown =>
                coverageMode == GuardianCoverageMode.RequireKnownCoverage
                    ? GuardianBoundaryAction.FailCombat
                    : GuardianBoundaryAction.ProceedUnprotected,
            GuardianAttemptResult.SwitchRejected or GuardianAttemptResult.SkillRejected
                when failedAttempts < MaxAttemptCount =>
                GuardianBoundaryAction.Retry,
            GuardianAttemptResult.SwitchRejected or GuardianAttemptResult.SkillRejected =>
                coverageMode == GuardianCoverageMode.RequireKnownCoverage
                    ? GuardianBoundaryAction.FailCombat
                    : GuardianBoundaryAction.ProceedUnprotected,
            _ => GuardianBoundaryAction.FailCombat
        };
    }

    internal static bool IsKnownCoverageValid(
        DateTime lastConfirmedCastAtUtc,
        double? durationSeconds,
        DateTime nowUtc,
        TimeSpan? refreshReserve = null)
    {
        var deadline = GetRefreshDeadline(
            lastConfirmedCastAtUtc,
            durationSeconds,
            refreshReserve ?? DefaultRefreshReserve);
        return deadline is { } value && nowUtc < value;
    }

    internal static TimeSpan GetSafeSeekBudget(
        DateTime lastConfirmedCastAtUtc,
        double? durationSeconds,
        DateTime nowUtc,
        TimeSpan requestedBudget,
        TimeSpan? refreshReserve = null)
    {
        if (requestedBudget <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var deadline = GetRefreshDeadline(
            lastConfirmedCastAtUtc,
            durationSeconds,
            refreshReserve ?? DefaultRefreshReserve);
        if (deadline is null)
        {
            return requestedBudget;
        }

        var available = deadline.Value - nowUtc;
        if (available <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        return available < requestedBudget ? available : requestedBudget;
    }

    private static DateTime? GetRefreshDeadline(
        DateTime lastConfirmedCastAtUtc,
        double? durationSeconds,
        TimeSpan refreshReserve)
    {
        if (durationSeconds is not > 0 || lastConfirmedCastAtUtc == default)
        {
            return null;
        }
        return lastConfirmedCastAtUtc
            .AddSeconds(durationSeconds.Value)
            .Subtract(refreshReserve < TimeSpan.Zero ? TimeSpan.Zero : refreshReserve);
    }
}
