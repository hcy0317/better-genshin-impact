using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>只在编译时翻译旧参数；运行期使用普通 record/watch/required 协议。</summary>
internal static class CombatFlowCompatibility
{
    public const string ReservedPrefix = "$compat:";
    private const string ForcedRefreshChannel = ReservedPrefix + "forced-refresh";

    public static void Compile(IReadOnlyList<CombatCommand> commands)
    {
        foreach (var command in commands)
        {
            if (command.IsCompilerGenerated) continue;
            foreach (var key in new[] { "record", "keep", "maintain", "watch", "refresh", "timing", "name", "id", "onfail", "then", "else", "unknown", "watch-target" })
                if (command.Options.TryGetValue(key, out var name) && name.StartsWith('$'))
                    throw command.Error("策略不能寻址编译器的保留命名空间：" + name);
            if ((command.Method == Method.Record || command.Method == Method.Timing || command.Method == Method.Call || command.Method == Method.JumpTo) &&
                command.Args?.FirstOrDefault()?.StartsWith('$') == true ||
                command.Options.TryGetValue("from", out var source) && source.StartsWith("segment:$", System.StringComparison.Ordinal))
                throw command.Error("策略不能寻址编译器的保留命名空间");
        }
        foreach (var command in commands.Where(command => command.Method == Method.Skill && command.HasFlag("refresh")))
        {
            if (command.Options.ContainsKey("record")) continue; // 显式具名新生成端保留作者的通道选择。
            command.Options["record"] = ForcedRefreshChannel;
            command.Options.TryAdd("watch", ForcedRefreshChannel);
            command.Options.TryAdd("before", CombatFlowPolicy.CoverageSeconds(command).ToString(CultureInfo.InvariantCulture));
        }
    }
}
