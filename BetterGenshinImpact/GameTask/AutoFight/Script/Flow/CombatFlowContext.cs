using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

public sealed record CombatRecordSource(string Actor, string Action, string? Effect = null,
    string? Revision = null, string? SkillId = null, string? Capability = null, bool EndsOnSwitch = false,
    string? Scope = null, string? TargetId = null, string? RangeId = null);

public sealed record CombatFlowRecord(long Generation, double OccurredAt, double? Duration,
    CombatRecordSource? Source, Guid? EffectInstanceId = null, long EffectVersion = 0);

/// <summary>本场的命名记录。物理技能冷却服务有自己的生命周期，不在此处清空。</summary>
public sealed class CombatFlowContext(TimeProvider? timeProvider = null) : IDisposable
{
    private sealed class EffectState(CombatRecordSource source, double occurredAt, double? duration)
    {
        public CombatRecordSource Source { get; } = source;
        public double OccurredAt = occurredAt;
        public double? Duration = duration;
        public long Version = 1;
        public bool Valid = true;
        public string? InvalidReason;
    }

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly long _started = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private readonly object _gate = new();
    private readonly Dictionary<string, CombatFlowRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, EffectState> _effects = new();
    private readonly Dictionary<(string Actor, string Effect), Guid> _currentEffects = new();
    private bool _closed;
    private long _generation;
    private CombatScopeObservation? _targetScope;
    private CombatScopeObservation? _rangeScope;
    private long _lastScopeFrame = -1;
    private double _lastScopeAt;
    public bool HasScopedEffects { get { lock (_gate) return !_closed && _effects.Values.Any(effect => effect.Valid && effect.Source.Scope is "target" or "range"); } }

    public Guid BattleId { get; } = Guid.NewGuid();
    public bool IsOpen { get { lock (_gate) return !_closed; } }
    public double Now => _clock.GetElapsedTime(_started).TotalSeconds;

    public CombatFlowRecord? Find(string name)
    {
        lock (_gate)
        {
            var record = _closed ? null : _records.GetValueOrDefault(NormalizeName(name));
            return record?.EffectInstanceId is { } id && _effects.TryGetValue(id, out var effect)
                ? record with { EffectVersion = effect.Version } : record;
        }
    }

    public double? Remaining(string name)
    {
        lock (_gate)
        {
            var record = _closed ? null : _records.GetValueOrDefault(NormalizeName(name));
            if (record?.EffectInstanceId is { } id)
            {
                if (!_effects.TryGetValue(id, out var effect) || !effect.Valid) return 0;
                return effect.Duration is { } lifetime ? Math.Max(0, effect.OccurredAt + lifetime - Now) : null;
            }
            return record?.Duration is { } duration ? Math.Max(0, record.OccurredAt + duration - Now) : null;
        }
    }

    public bool TryRecord(string name, double occurredAt, double? duration = null, CombatRecordSource? source = null)
    {
        name = NormalizeName(name);
        if (!double.IsFinite(occurredAt) || occurredAt < 0 ||
            duration is { } seconds && (!double.IsFinite(seconds) || seconds <= 0))
            throw new ArgumentOutOfRangeException(nameof(duration), "时间必须为有限非负值，持续时间必须大于零");
        lock (_gate)
        {
            if (_closed) return false;
            if (_records.TryGetValue(name, out var previous) && previous.OccurredAt > occurredAt) return false;
            Guid? effectId = null;
            if (source?.Effect is { } effectName)
            {
                var key = (source.Actor, effectName);
                if (_currentEffects.TryGetValue(key, out var priorId) && _effects.TryGetValue(priorId, out var prior))
                {
                    if (prior.OccurredAt > occurredAt) return false;
                    prior.Valid = false;
                    prior.Version++;
                }
                effectId = Guid.NewGuid();
                _effects[effectId.Value] = new(source, occurredAt, duration);
                _currentEffects[key] = effectId.Value;
            }
            _records[name] = new(++_generation, occurredAt, duration, source, effectId, effectId == null ? 0 : 1);
            PruneEffect(previous?.EffectInstanceId);
            return true;
        }
    }

    public static string NormalizeName(string name)
    {
        var normalized = name.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0) throw new ArgumentException("记录名不能为空", nameof(name));
        return normalized;
    }

    /// <summary>提交已经通过机制校验的刷新；只更新读取的那一代，过期窗口不能复活。</summary>
    public bool TryRefresh(string name, long expectedGeneration, double occurredAt, double duration,
        long? expectedEffectVersion = null)
    {
        name = NormalizeName(name);
        if (!double.IsFinite(occurredAt) || !double.IsFinite(duration) || duration <= 0)
            throw new ArgumentOutOfRangeException(nameof(duration));
        lock (_gate)
        {
            if (_closed || !_records.TryGetValue(name, out var previous) || previous.Generation != expectedGeneration)
                return false;
            EffectState? effect = null;
            if (previous.EffectInstanceId is { } id &&
                (!_effects.TryGetValue(id, out effect) || !effect.Valid ||
                 expectedEffectVersion is { } expected && effect.Version != expected)) return false;
            var priorAt = effect?.OccurredAt ?? previous.OccurredAt;
            var priorDuration = effect?.Duration ?? previous.Duration;
            if (priorDuration == null || occurredAt < priorAt || occurredAt >= priorAt + priorDuration) return false;
            if (effect != null) { effect.OccurredAt = occurredAt; effect.Duration = duration; effect.Version++; }
            _records[name] = previous with { Generation = ++_generation, OccurredAt = occurredAt, Duration = duration,
                EffectVersion = effect?.Version ?? 0 };
            return true;
        }
    }

    public bool CanConsume(string name, CombatFlowRecord expected, double requiredSeconds)
    {
        lock (_gate)
        {
            var current = Find(name);
            return current != null && current.Generation == expected.Generation &&
                current.EffectVersion == expected.EffectVersion && Remaining(name) is { } remaining &&
                remaining > requiredSeconds;
        }
    }

    /// <summary>给同一个已确认实例建立另一引用；这不是重新施放或刷新。</summary>
    public bool TryReference(string name, string existingName)
    {
        name = NormalizeName(name);
        existingName = NormalizeName(existingName);
        lock (_gate)
        {
            if (_closed || name == existingName || !_records.TryGetValue(existingName, out var source) ||
                source.EffectInstanceId is not { } id || !_effects.TryGetValue(id, out var effect) || !effect.Valid)
                return false;
            var previous = _records.GetValueOrDefault(name);
            if (previous != null && previous.OccurredAt > source.OccurredAt) return false;
            _records[name] = source with { Generation = ++_generation, EffectVersion = effect.Version };
            PruneEffect(previous?.EffectInstanceId);
            return true;
        }
    }

    /// <summary>只接受归属于当前实例版本的可信失效通知；事件历史仍保留。</summary>
    public bool InvalidateEffect(Guid instanceId, long expectedVersion, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (_closed || !_effects.TryGetValue(instanceId, out var effect) ||
                !effect.Valid || effect.Version != expectedVersion) return false;
            effect.Valid = false;
            effect.InvalidReason = reason;
            effect.Version++;
            return true;
        }
    }

    public void ObserveActiveActor(string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        lock (_gate)
        {
            if (_closed) return;
            foreach (var (id, effect) in _effects)
                if (effect.Valid && effect.Source.EndsOnSwitch && effect.Source.Actor != actor)
                    InvalidateEffect(id, effect.Version, "切换角色导致效果失效");
        }
    }

    public bool ObserveScope(CombatScopeObservation observation)
    {
        lock (_gate)
        {
            if (_closed || observation.BattleId != BattleId || observation.FrameId < 0 ||
                !double.IsFinite(observation.CapturedAt) || observation.CapturedAt < 0 ||
                observation.CapturedAt > Now || Now - observation.CapturedAt > 0.5 ||
                observation.TargetId?.Length > 256 || observation.RangeId?.Length > 256 ||
                string.IsNullOrWhiteSpace(observation.TargetId) && (string.IsNullOrWhiteSpace(observation.RangeId) || observation.InRange == null) ||
                observation.FrameId <= _lastScopeFrame || observation.CapturedAt < _lastScopeAt) return false;
            if (!string.IsNullOrWhiteSpace(observation.TargetId) && _targetScope?.TargetId != observation.TargetId)
                foreach (var (id, effect) in _effects)
                    if (effect.Valid && effect.Source.Scope == "target" && effect.Source.TargetId != observation.TargetId)
                        InvalidateEffect(id, effect.Version, "已确认当前目标改变，旧目标窗口不再授权");
            if (!string.IsNullOrWhiteSpace(observation.RangeId) && observation.InRange == false)
                foreach (var (id, effect) in _effects)
                    if (effect.Valid && effect.Source.Scope == "range" && effect.Source.RangeId == observation.RangeId)
                        InvalidateEffect(id, effect.Version, "已确认离开该范围，旧范围消费者不再授权");
            if (!string.IsNullOrWhiteSpace(observation.TargetId)) _targetScope = observation;
            if (!string.IsNullOrWhiteSpace(observation.RangeId) && observation.InRange != null) _rangeScope = observation;
            _lastScopeFrame = observation.FrameId;
            _lastScopeAt = observation.CapturedAt;
            return true;
        }
    }

    internal CombatRecordSource BindScope(CombatRecordSource source, double inputAt)
    {
        lock (_gate)
        {
            bool Fresh(CombatScopeObservation? observation) => !_closed && observation != null &&
                observation.CapturedAt >= inputAt && Now - observation.CapturedAt <= 0.5;
            if (source.Scope == "target" && Fresh(_targetScope)) return source with { TargetId = _targetScope!.TargetId };
            // 范围身份必须确认归属于本次技能输入，不能把旁边已有的另一个领域绑定给新效果。
            if (source.Scope == "range" && Fresh(_rangeScope) && _rangeScope!.InRange == true &&
                _rangeScope.SkillId == source.SkillId && _rangeScope.ProducedAt == inputAt)
                return source with { RangeId = _rangeScope.RangeId };
            return source;
        }
    }

    private void PruneEffect(Guid? id)
    {
        // 实例只为仍被具名记录引用的效果保留；长战斗不积累每次施放的废弃实例。
        if (id is not { } value || _records.Values.Any(record => record.EffectInstanceId == value) ||
            !_effects.Remove(value, out var effect)) return;
        var key = (effect.Source.Actor, effect.Source.Effect!);
        if (_currentEffects.GetValueOrDefault(key) == value) _currentEffects.Remove(key);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            _records.Clear();
            _effects.Clear();
            _currentEffects.Clear();
            _targetScope = _rangeScope = null;
        }
    }
}
