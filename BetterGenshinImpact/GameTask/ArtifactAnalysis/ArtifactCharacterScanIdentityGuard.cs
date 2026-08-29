using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal sealed class ArtifactCharacterScanIdentityGuard
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private string? _previousCharacterKey;

    internal bool DidNotChange(string characterKey) =>
        _previousCharacterKey is not null
        && string.Equals(
            _previousCharacterKey,
            characterKey,
            StringComparison.Ordinal);

    internal void Commit(string characterKey)
    {
        if (DidNotChange(characterKey))
        {
            throw new InvalidOperationException(
                $"点击头像后右侧角色没有切换，仍为：{characterKey}");
        }
        if (!_seen.Add(characterKey))
        {
            throw new InvalidOperationException(
                $"角色分页重复返回了已处理角色：{characterKey}");
        }
        _previousCharacterKey = characterKey;
    }
}
