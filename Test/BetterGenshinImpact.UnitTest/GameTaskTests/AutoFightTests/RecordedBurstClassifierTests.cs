using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

// 显式提供现有模型与录制帧才执行。只读文件、CPU推理，不使用App/DI/真实输入。
public class RecordedBurstClassifierTests(ITestOutputHelper output)
{
    [RecordedBurstFact]
    public void ReportRecordedFrameClassificationWithoutClaimingVisualAccuracy()
    {
        using var predictor = new BgiYoloPredictor(BgiOnnxModel.BgiQClassify,
            Environment.GetEnvironmentVariable("BGI_BURST_MODEL")!, new SessionOptions());
        foreach (var path in Environment.GetEnvironmentVariable("BGI_BURST_FRAMES")!.Split(';'))
        {
            using var frame = new ImageRegion(Cv2.ImRead(path), 0, 0);
            Assert.False(frame.SrcMat.Empty());
            var reading = CombatHudReader.ReadBurst(frame, predictor);
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                Path = path, Roi = AutoFightAssets.Get(frame).QRectForClassify.ToString(),
                reading.Label, reading.Confidence, reading.Observation.EnergyFull,
                reading.Observation.CoolingDown, reading.Observation.Ready
            }));
            Assert.Equal(reading, CombatHudReader.ReadBurst(frame, predictor));
        }
        Assert.Null(System.Windows.Application.Current);
    }

    private sealed class RecordedBurstFactAttribute : FactAttribute
    {
        public RecordedBurstFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BGI_BURST_MODEL")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BGI_BURST_FRAMES")))
                Skip = "需要显式提供已有Q模型与真实录制帧；不自动启动游戏或下载资源";
        }
    }
}
