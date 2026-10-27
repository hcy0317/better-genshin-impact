using System;
using System.IO;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatComponentHealthBarTests
{
    [Fact]
    public void ComponentEvidenceFeedsTheExistingSpatialTargetSelectionWithoutARedHealthBar()
    {
        using var pixels = ComponentFrame();
        using var frame = new ImageRegion(pixels.Clone(), 0, 0);
        var decision = AutoFightSeek.RecognizeSeekDecision(frame, new Scalar(255, 90, 90), null,
            out _, out _, saveDiagnostics: false);
        Assert.Equal(SeekCueKind.HealthBar, decision.Cue);
        Assert.Equal(1, decision.SignalCount);
        Assert.Equal(920, decision.Visual!.Value.X);
        Assert.Equal(450, decision.Visual.Value.Y);
        Assert.Equal(80, decision.Visual.Value.Width);
    }

    [RecordedConstructFact]
    public void RecordedConstructPartSurvivesTheCompletePassiveTargetSelection()
    {
        var root = Environment.GetEnvironmentVariable("BGI_CONSTRUCT_EVIDENCE_ROOT")!;
        using var frame = new ImageRegion(Cv2.ImRead(Path.Combine(root, "evidence-5155.png")), 0, 0);
        var decision = AutoFightSeek.RecognizeSeekDecision(frame, new Scalar(255, 90, 90), null,
            out _, out _, saveDiagnostics: false);
        Assert.Equal(SeekCueKind.HealthBar, decision.Cue);
        Assert.InRange(decision.Visual!.Value.X, 1035, 1042);
        Assert.InRange(decision.Visual.Value.Y, 531, 535);
    }
}
