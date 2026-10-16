using BetterGenshinImpact.GameTask.AutoBoss;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;
using System.IO;
using System.Text.Json;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

// Existing private original pixels only; no capture device, App, window, or input is initialized.
public class RecordedMatrixSceneTests(ITestOutputHelper output)
{
    [RecordedMatrixFact]
    public void DeclaredMatrixSceneRetainsRecordedHudWithoutInventingSpatialAuthority()
    {
        var root = Environment.GetEnvironmentVariable("BGI_MATRIX_SCENE_EVIDENCE_ROOT")!;
        var scene = AutoBossTask.SceneForBoss("半永恒统辖矩阵");
        foreach (var (sequence, visible) in new[]
            { (2753, true), (2755, true), (2759, false), (3000, true), (3178, true), (3180, false) })
        {
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, $"evidence-{sequence}.json")));
            Assert.Equal("ed6a5311-ddbb-4fbc-98e4-fa3772627c66", metadata.RootElement.GetProperty("RunId").GetString());
            Assert.Contains("battle=2584587d-339c-4a24-893e-f946fa786238",
                metadata.RootElement.GetProperty("Detail").GetString());
            var source = metadata.RootElement.GetProperty("Source").Deserialize<CaptureFrameStamp>();
            using var frame = new ImageRegion(Cv2.ImRead(Path.Combine(root, $"evidence-{sequence}.png")), 0, 0)
                { FrameStamp = source };
            Assert.False(frame.SrcMat.Empty());
            Assert.Equal(1920, frame.SrcMat.Width);
            Assert.Equal(1080, frame.SrcMat.Height);
            var decision = AvatarRecognition.ReadPassiveTargetEvidence(frame, scene).Decision;
            output.WriteLine($"evidence-{sequence}: {decision}");
            if (visible)
            {
                Assert.NotEqual(SeekCueKind.HealthBar, decision.Cue);
                Assert.Equal(AutoFightSeekAction.KeepFighting, decision.Action);
            }
            Assert.Equal(visible, decision.FixedTopHealth != null);
            if (visible)
            {
                // Spatial candidates remain independent; the fixed HUD cannot become their location.
                if (decision.Cue == SeekCueKind.FixedTopHealth)
                    Assert.Equal(AutoFightSeekAction.KeepFighting, decision.Action);
                else
                    Assert.NotEqual(decision.FixedTopHealth, decision.Visual);
                Assert.InRange(decision.FixedTopHealth!.Value.X, 716, 726);
                Assert.InRange(decision.FixedTopHealth.Value.Y, 80, 86);
                if (sequence == 3178) Assert.InRange(decision.FixedTopHealth.Value.Width, 6, 24);
                if (sequence == 3000) Assert.InRange(decision.FixedTopHealth.Value.Width, 100, 250);
            }
            // Same pixels in an undeclared scene must not acquire the Matrix interpretation.
            Assert.Null(AvatarRecognition.ReadPassiveTargetEvidence(frame,
                AutoBossTask.SceneForBoss("unknown")).Decision.FixedTopHealth);
        }
        Assert.False(scene.AllowTargetLock);
        Assert.False(scene.PreferFixedTarget);
        Assert.Null(System.Windows.Application.Current);
    }

    internal sealed class RecordedMatrixFactAttribute : FactAttribute
    {
        public RecordedMatrixFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BGI_MATRIX_SCENE_EVIDENCE_ROOT")))
                Skip = "Requires explicitly supplied existing Matrix evidence; missing pixels are not a pass.";
        }
    }
}
