using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>预算按本场未解决的目标保存；换命令行、调用帧或根轮次不补充预算。</summary>
internal sealed class CombatFlowEpisodes
{
    private sealed class Episode(double deadline, int limit)
    {
        public double Deadline = deadline;
        public int Limit = limit;
        public int Attempts;
        public int NoProgress;
        public int NoProgressLimit = int.MaxValue;
    }
    private readonly Dictionary<string, Episode> _episodes = new(StringComparer.Ordinal);

    public bool TrySpend(string objective, double now, double timeout, int limit, out double deadline,
        int noProgressLimit = int.MaxValue)
    {
        if (!_episodes.TryGetValue(objective, out var episode))
            _episodes.Add(objective, episode = new(now + timeout, limit));
        episode.Limit = Math.Min(episode.Limit, limit);
        episode.NoProgressLimit = Math.Min(episode.NoProgressLimit, noProgressLimit);
        episode.Deadline = Math.Min(episode.Deadline, now + timeout);
        deadline = episode.Deadline;
        if (now >= deadline || episode.Attempts >= episode.Limit || episode.NoProgress >= episode.NoProgressLimit) return false;
        episode.Attempts++;
        return true;
    }

    public void ReportProgress(string objective, bool? progressed)
    {
        if (!_episodes.TryGetValue(objective, out var episode) || progressed == null) return;
        if (progressed == true) episode.NoProgress = 0;
        else episode.NoProgress++;
    }

    public void Resolve(string objective) => _episodes.Remove(objective);
    // 只是观察在途输入/等待已知冷却，不消耗新一次执行尝试；原截止时间和已有失败仍保留。
    public void Defer(string objective)
    {
        if (_episodes.TryGetValue(objective, out var episode) && episode.Attempts > 0) episode.Attempts--;
    }
    public void Clear() => _episodes.Clear();
}
