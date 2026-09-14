using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model;
using Microsoft.Extensions.Logging;
using Fischless.GameCapture;
using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>宿主输入与策略复用同一Native Session；只有这个适配器连接游戏设备。</summary>
internal sealed class NativeCombatBattleHostIo(NativeCombatFlowRunner flow, CombatScenes scenes) : ICombatBattleHostIo
{
    private readonly NativeCombatIo _vision = new(scenes);
    private bool _partyRequested, _partyEvidence;
    private CaptureFrameStamp _partyBefore;
    private CaptureFrameStamp _targetSource;
    private CaptureFrameFence? _partyFence;
    public TimeProvider Clock => _vision.Clock;
    public Guid BattleId => flow.Context.BattleId;

    public CombatBattleObservation ObserveTarget()
    {
        var observation = AvatarRecognition.LatestPassiveObservation;
        _targetSource = observation.Source;
        var current = AvatarRecognition.PassiveCaptureGate;
        var quality = observation.Quality;
        if (observation.CaptureEpoch != current.Epoch || !current.CanCapture)
            quality = CombatObservationQuality.Unavailable;
        EnemySeekDecision? target = AutoFightSeek.TryCreatePassiveDecision(observation, Clock.GetUtcNow().UtcDateTime,
            out var decision, out _, out _) ? decision : null;
        return new(observation.Source, observation.BattleId, quality, target,
            observation.ImageWidth, observation.ImageHeight, observation.CueFingerprint);
    }

    public PartySetupFinishObservation ObservePartyBar()
    {
        using var capture = _vision.Capture();
        if (capture == null) return default;
        var observed = AutoFightTask.ObservePartySetupBar(capture, capture.FrameStamp.Sequence);
        if (!_partyRequested) _partyBefore = observed.Source;
        else _partyEvidence |= observed.BarVisible && observed.Source.IsFresh(Clock, TimeSpan.FromMilliseconds(150)) &&
            _partyFence?.Accepts(observed.Source) == true;
        return observed;
    }

    public ValueTask SendAsync(CombatBattleHostInput input, CancellationToken ct) =>
        flow.RunHostOperationAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            using var operation = UiOperation.Begin("combat-host-input", TimeSpan.FromMilliseconds(150), token, TaskControl.Logger);
            TaskControl.CheckAndSleep(0); // 沿用暂停/焦点规则，且由本次150ms作用域约束等待。
            operation.Check();
            if (input.Kind != CombatBattleHostInputKind.CloseParty)
            {
                var source = input.Kind == CombatBattleHostInputKind.OpenParty ? _partyBefore : _targetSource;
                if (!source.IsFresh(Clock, TimeSpan.FromMilliseconds(150)))
                    throw new TimeoutException("战斗宿主输入前源帧已过期，不发送输入");
            }
            switch (input.Kind)
            {
                case CombatBattleHostInputKind.Camera:
                    Simulation.SendInput.Mouse.MoveMouseBy(input.X, input.Y);
                    break;
                case CombatBattleHostInputKind.Approach:
                    try
                    {
                        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                        await Task.Delay(100, token);
                    }
                    finally { Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp); }
                    break;
                case CombatBattleHostInputKind.OpenParty:
                    _partyRequested = true;
                    _partyEvidence = false;
                    AutoFightTask.LastFightFinishCheckTime = DateTime.Now;
                    Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                    _partyFence = new(_partyBefore, Clock.GetTimestamp());
                    break;
                case CombatBattleHostInputKind.CloseParty:
                    CloseParty(input.PartyEvidence);
                    break;
            }
            operation.Check();
        }, ct);

    private void CloseParty(bool evidence)
    {
        Simulation.SendInput.SimulateAction(GIActions.Drop);
        if (evidence) Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
        _partyRequested = false;
        _partyEvidence = false;
        _partyFence = null;
    }

    public ValueTask DelayAsync(int milliseconds, CancellationToken ct) => new(Task.Delay(milliseconds, ct));
    public void ReleaseInput()
    {
        try
        {
            if (_partyRequested)
                flow.RunHostOperationAsync(_ =>
                {
                    CloseParty(_partyEvidence);
                    return ValueTask.CompletedTask;
                }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally { flow.ReleaseHostInput(); }
    }

    internal static CombatBattleHost Create(NativeCombatFlowRunner flow, CombatScenes scenes, AutoFightParam param)
    {
        var finish = new AutoFightTask.TaskFightFinishDetectConfig(param);
        return new(new NativeCombatBattleHostIo(flow, scenes), new()
        {
            TimeoutSeconds = param.Timeout,
            SeekEnabled = param.FinishDetectConfig.RotateFindEnemyEnabled || param.EnableCombatTargeting,
            FinishDetectionEnabled = param.FightFinishDetectEnabled,
            InitialBlockSeconds = finish.BlockCheckBeforeBattleSeconds,
            FinishCheckIntervalSeconds = Math.Max(.1, finish.CheckTime),
            FinishProbeDelayMilliseconds = finish.PaimonEndCheckEnabled ? finish.PaimonEndCheckDelayMs : finish.DetectDelayTime
        });
    }

    internal static bool ApplyResult(CombatBattleHost host, CombatBattleHostResult result,
        AutoFightTask.TaskFightFinishDetectConfig config)
    {
        if (result == CombatBattleHostResult.Unconfirmed)
            TaskExecutionScope.StopUnconfirmedCombat("战斗宿主未取得完成证据：" + host.Reason);
        if (result != CombatBattleHostResult.Completed) return false;
        config.EndConfirmed = true;
        TaskControl.Logger.LogInformation("战斗宿主确认结束：{Reason}", host.Reason);
        return true;
    }
}
