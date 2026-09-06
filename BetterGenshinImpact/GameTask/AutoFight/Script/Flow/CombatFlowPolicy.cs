using System;
using System.Globalization;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>执行器的保守工程预算（秒），不是游戏技能事实。高级参数只可在此处解析。</summary>
internal static class CombatFlowPolicy
{
    public const double RecoverySeconds = 0.25;
    public const double SwitchSeconds = 1;
    public const double ActionTimeoutSeconds = 8;
    public const double EpisodeTimeoutSeconds = 15;
    public const int EpisodeAttempts = 3;
    public const int NoProgressAttempts = 2;
    public const double AtomicSeconds = 8;
    public const double FeedWaitSeconds = 0.8;

    public static double CoverageSeconds(CombatCommand command) =>
        ActionSeconds(command) + SwitchSeconds + RecoverySeconds;

    public static int MaintenancePriority(CombatCommand command, CombatRecordSource source) =>
        source.Capability is "shield" or "healing" ? 2 :
        command.HasFlag("required") || source.EndsOnSwitch ? 1 : 0;

    public static double ActionTimeout(CombatCommand command, CombatTiming? timing)
    {
        var ordinary = Math.Max(ActionTimeoutSeconds, CoverageSeconds(command));
        if (command.Method == Method.Skill && command.HasFlag("wait"))
            ordinary = Math.Max(ordinary, (timing?.Cooldown ?? EpisodeTimeoutSeconds) + CoverageSeconds(command));
        return Timeout(command, ordinary);
    }

    public static double Timeout(CombatCommand command, double fallback = EpisodeTimeoutSeconds) =>
        CombatFlowProgram.Number(command, "timeout", positive: true) ?? fallback;

    public static int Attempts(CombatCommand command) => Count(command, "attempts", EpisodeAttempts);
    public static int NoProgress(CombatCommand command) => Count(command, "no-progress", NoProgressAttempts);

    private static int Count(CombatCommand command, string name, int fallback)
    {
        var attempts = CombatFlowProgram.Number(command, name, positive: true) ?? fallback;
        if (attempts != Math.Truncate(attempts) || attempts > 64)
            throw command.Error(name + " 必须为 1 到 64 的整数");
        return (int)attempts;
    }

    public static double ActionSeconds(CombatCommand command)
    {
        if (command.Method == Method.Burst) return 2;
        if (command.Method == Method.Skill) return (command.HasFlag("hold") ? 2 : 1) +
            (command.Options.ContainsKey("feed") ? SwitchSeconds + FeedWaitSeconds : 0);
        var index = command.Method == Method.Walk ? 1 : 0;
        if ((command.Method == Method.Wait || command.Method == Method.Attack || command.Method == Method.Charge ||
             command.Method == Method.Walk || command.Method == Method.W || command.Method == Method.A ||
             command.Method == Method.S || command.Method == Method.D || command.Method == Method.Dash) &&
            command.Args?.Count > index && double.TryParse(command.Args[index], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds >= 0)
            return seconds;
        return command.Method.IsFlowControl ? 0 : 0.25;
    }
}
