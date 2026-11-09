using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal sealed record CommissionPageObservation(UiSnapshot Scene, bool ShellVisible, bool TabVisible, bool RewardVisible, Rect TabBounds);

internal sealed partial class NativeUiDriver
{
    internal TimeProvider CommissionClock => _io.Clock;

    internal CommissionPageObservation CaptureCommission(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        return CaptureOwned((image, scene) => ReadCommissionOwned(image, scene, ct));
    }

    private static bool CommissionSceneConflicts(UiSnapshot scene) =>
        scene.MainHud || scene.BigMap || scene.Party || scene.PartyList || scene.Talk || scene.Prompt || scene.Revive ||
        scene.FullPartyDefeat || scene.DefeatOverlay || scene.ExitDoor || scene.BlackConfirm || scene.MenuBack ||
        scene.Crafting || scene.TimeSetting || scene.Cannon || scene.Reward.IsCandidate || scene.DomainExit.Visible ||
        scene.DomainTip.IsCandidate || scene.LoginNetwork.IsCandidate;

    private bool CommissionSourceIsUsable(ImageRegion image, UiSnapshot scene) =>
        !CommissionSceneConflicts(scene) && scene.SourceBound && scene.HasUsableEvidence && scene.SourceStamp == image.FrameStamp &&
        scene.SourceStamp.TimestampFrequency == _io.Clock.TimestampFrequency &&
        scene.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge) &&
        (_inputFence is not { } fence || fence.Accepts(scene.SourceStamp));

    private CommissionPageObservation ReadCommissionOwned(ImageRegion image, UiSnapshot scene, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        if (!CommissionSourceIsUsable(image, scene)) return new(scene, false, false, false, default);
        var source = image.FrameStamp;
        var features = HandbookUiRecognition.ReadCommission(image, _io.Ocr(), scene.Handbook);
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        if (image.FrameStamp != source || !CommissionSourceIsUsable(image, scene))
            return new(scene, false, false, false, default);
        return new(scene, features.ShellVisible, features.TabVisible, features.RewardVisible, features.TabBounds);
    }

    internal Task<bool> SelectCommissionTabAsync(CommissionPageObservation observed, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        _io.Focus();
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        var applied = CaptureOwned((image, scene) =>
        {
            var current = ReadCommissionOwned(image, scene, ct);
            bool Admitted() => observed.ShellVisible && observed.TabVisible && !CommissionSceneConflicts(observed.Scene) &&
                observed.Scene.SourceBound && observed.Scene.HasUsableEvidence && current.ShellVisible && current.TabVisible &&
                CommissionSourceIsUsable(image, current.Scene) &&
                (_inputFence is not { } observedFence || observedFence.Accepts(observed.Scene.SourceStamp)) &&
                current.Scene.SourceStamp.SessionId == observed.Scene.SourceStamp.SessionId && current.Scene.IsAfter(observed.Scene) &&
                current.Scene.SourceStamp.CapturedTimestamp > observed.Scene.SourceStamp.CapturedTimestamp &&
                observed.Scene.SourceStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge) &&
                current.TabBounds.Width > 0 && current.TabBounds.Height > 0 && current.TabBounds.X >= 0 && current.TabBounds.Y >= 0 &&
                current.TabBounds.Right <= image.Width && current.TabBounds.Bottom <= image.Height;
            if (!Admitted()) return false;
            void AdmitClick()
            {
                ct.ThrowIfCancellationRequested();
                UiOperation.Current?.Check();
                if (!Admitted()) throw new InvalidOperationException("委托标签的同源后继观察在点击前失效。");
            }
            _io.Click(image, current.TabBounds, AdmitClick);
            MarkInputCompleted(current.Scene);
            return true;
        });
        return Task.FromResult(applied);
    }
}
