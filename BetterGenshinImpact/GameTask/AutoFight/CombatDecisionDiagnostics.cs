using System;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>单场、单生产者的节流诊断；只消费已有观察，不执行识别或输入。</summary>
internal sealed class CombatDecisionDiagnostics(ILogger logger, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long? _lastTimestamp;
    public long AtomicSteps { get; private set; }
    public long SeekCalls { get; private set; }
    public string LastSeek { get; private set; } = "not-invoked";
    public int LastCameraPulse { get; private set; }

    public void ObserveAtomicStep() => AtomicSteps++;
    public void RecordSeek(string outcome, int cameraPulse = 0)
    {
        SeekCalls++;
        LastSeek = outcome;
        LastCameraPulse = cameraPulse;
    }

    public bool Write(string marker, string battle, Func<string> describe, bool force = false)
    {
        if (!CombatFlowDiagnosticWriter.IsEnabled(logger)) return false;
        var now = _clock.GetTimestamp();
        if (!force && _lastTimestamp is { } previous && _clock.GetElapsedTime(previous, now).TotalSeconds < 2)
            return false;
        _lastTimestamp = now;
        try
        {
            logger.LogDebug("{Marker} battle={Battle} {Decision}", marker, battle, describe());
            return true;
        }
        catch { return false; }
    }
}
