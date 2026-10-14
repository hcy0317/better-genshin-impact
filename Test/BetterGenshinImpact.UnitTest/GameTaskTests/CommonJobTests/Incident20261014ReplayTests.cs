using System.Security.Cryptography;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class Incident20261014ReplayTests
{
    [Incident20261014ReplayFact]
    public void ExactMailToastPixelsAndRecordedOcrDoNotPauseRecovery()
    {
        using var frame = Load("evidence-0002", 124,
            "A055659B7514CD3611F835BF751E526D855B88E2BDB1F2AC2B6EF32C06248FC4");
        var metadata = Metadata("evidence-0002");
        Assert.Equal("connection-panel-unconfirmed", (string?)metadata["Detail"]);
        var text = (string)metadata["Fields"]!["ocr"]!;
        Assert.Equal("您的邮箱内附件已全部领取完毕", text);
        Assert.Null(WorldFrameAvailability.ReadConnectionWait(frame, new RecordedOcr(text)));
        Assert.Equal("connection-panel-unconfirmed", WorldFrameAvailability.ReadConnectionWait(frame, new RecordedOcr("")));
        Assert.Equal("reconnect-panel", WorldFrameAvailability.ReadConnectionWait(frame, new RecordedOcr("重新连接服务器")));
        Assert.Equal(WorldFrameKind.Unknown, WorldFrameAvailability.Read(frame, false, new RecordedOcr(text)));
    }

    [Incident20261014ReplayFact]
    public void TerminalContextArrowRemainsASearchHintAndCannotConfirmFightCompletion()
    {
        // 3480 是终止附近已保存帧，不冒充3486决策帧或3487编队反馈帧。
        using var frame = Load("evidence-0653", 3480,
            "91108029B93075191B0B044EFD0BD0EFF9D99DEC36AD1735C9FB65F2950453E5");
        AutoFightSeek.ResetSeekState();
        var hints = new List<EnemySeekVisual>();
        var decision = AutoFightSeek.RecognizeSeekDecision(frame, new Scalar(255, 90, 90), null,
            out _, out _, saveDiagnostics: false, unconfirmedDirection: hints.Add);
        Assert.Null(decision.Visual);
        Assert.Contains(hints, hint => hint.CenterX is >= 525 and <= 560 && hint.CenterY is >= 735 and <= 775);
        Assert.All(hints, hint => Assert.Null(hint.IndicatorBearingDegrees));
    }

    private static string Root => Environment.GetEnvironmentVariable("BGI_INCIDENT_20261014")!;
    private static JObject Metadata(string name) => JObject.Parse(File.ReadAllText(Path.Combine(Root, name + ".json")));
    private static ImageRegion Load(string name, long sequence, string hash)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var path = Path.Combine(Root, name + ".png");
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var stamp = Metadata(name)["Source"]!.ToObject<CaptureFrameStamp>();
        Assert.Equal(sequence, stamp.Sequence);
        Assert.Equal(Guid.Parse("11558d48-69fe-4fb3-a84d-b33030a03050"), stamp.SessionId);
        return new ImageRegion(Cv2.ImRead(path), 0, 0) { FrameStamp = stamp };
    }

    // OCR结果来自同次运行元数据；这里不调用真实模型或启动宿主。
    private sealed class RecordedOcr(string text) : IOcrService
    {
        public string Ocr(Mat mat) => text;
        public string OcrWithoutDetector(Mat mat) => text;
        public OcrResult OcrResult(Mat mat) => new([]);
    }
}

public sealed class Incident20261014ReplayFactAttribute : FactAttribute
{
    public Incident20261014ReplayFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BGI_INCIDENT_20261014")))
            Skip = "Private incident originals not supplied; skipped replay is not verification.";
    }
}
