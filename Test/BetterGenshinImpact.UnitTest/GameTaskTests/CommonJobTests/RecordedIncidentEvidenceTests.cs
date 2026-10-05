using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedIncidentEvidenceTests(ITestOutputHelper output)
{
    [OfflineNativeDecisionFact]
    public void ReportRecordedWorldAndTargetAdmissionWithoutLiveCapture()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var paths = Environment.GetEnvironmentVariable("BGI_INCIDENT_FRAME_FILES")?.Split('|') ?? [];
        Assert.NotEmpty(paths);
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        foreach (var path in paths)
        {
            Assert.True(File.Exists(path), "Only explicitly supplied saved frames may be replayed");
            using var pixels = Cv2.ImRead(path);
            Assert.False(pixels.Empty());
            using var frame = new ImageRegion(pixels.Clone(), 0, 0) { FrameStamp = new CaptureFrameSource().Next() };
            var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", frame), ocr,
                "复苏", "使用道具复苏角色");
            using var paimon = frame.Find(ElementRecognition.Get("PaimonMenu", frame));
            using var chat = frame.Find(ElementRecognition.Get("FriendChat", frame));
            var hud = detector.IsCombatHud(frame);
            var map = Bv.IsInBigMapUi(frame);
            var transformed = SaurianUiReader.IsKnownTransformation(frame);
            var world = WorldFrameAvailability.ReadWorld(frame, SaurianUiReader.IsKnownTransformation, detector.IsCombatHud, _ => map, ocr);
            var scale = frame.Height / 1080d;
            var hpX = (int)Math.Round(frame.Width / 2d - 152 * scale);
            var hpY = (int)Math.Round(frame.Height - 70 * scale);
            var hp = Enumerable.Range(0, 3).Select(i => pixels.At<Vec3b>(hpY, hpX + i * 2).ToString()).ToArray();
            var panelSamples = new[] { (36, 48), (64, 48), (36, 52), (64, 52) }
                .Select(p => pixels.At<Vec3b>(frame.Height * p.Item2 / 100, frame.Width * p.Item1 / 100).ToString()).ToArray();
            using var panelText = new Mat(pixels, new Rect(frame.Width * 39 / 100, frame.Height * 47 / 100,
                frame.Width * 22 / 100, frame.Height * 6 / 100));
            var reconnectText = ocr.OcrWithoutDetector(panelText);
            var cannon = CannonUiReader.Read(frame, ocr);
            var ui = NativeUiDriver.Read(frame, ocr: ocr, reviveDetector: detector);
            using var partyButton = frame.Find(ElementRecognition.Get("PartyBtnChooseView", frame));
            AutoFightSeek.ResetSeekState();
            var diagnostics = new SeekRecognitionDiagnostics();
            var hints = new List<EnemySeekVisual>();
            var decision = AutoFightSeek.RecognizeSeekDecision(frame, new Scalar(255, 90, 90), null,
                out _, out _, saveDiagnostics: false, diagnostics: diagnostics, unconfirmedDirection: hints.Add);
            using var mask = AutoFightSeek.CreateSeekColorMask(pixels, new Scalar(255, 90, 90), null);
            using var labels = new Mat();
            using var stats = new Mat();
            using var centers = new Mat();
            var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centers, PixelConnectivity.Connectivity4, MatType.CV_32S);
            var candidates = new List<object>();
            for (var i = 1; i < count; i++)
            {
                var visual = new EnemySeekVisual(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2),
                    stats.At<int>(i, 3), stats.At<int>(i, 4));
                if (visual.Width is < 12 or > 80 || visual.Height is < 12 or > 80 ||
                    AutoFightSeek.IsDirectionIndicatorHudNoise(visual, frame.Width, frame.Height)) continue;
                var detail = new SeekRecognitionDiagnostics();
                var accepted = AutoFightSeek.ClassifySeekVisual(mask, pixels, visual, frame.Width, frame.Height, detail);
                using var candidate = new Mat(mask, new Rect(visual.X, visual.Y, visual.Width, visual.Height));
                var contour = AutoFightSeek.GetLargestExternalContour(candidate);
                candidates.Add(new { visual, geometry = AutoFightSeek.IsDirectionIndicatorGeometry(visual, frame.Width, frame.Height),
                    pinkRedShare = AutoFightSeek.HasDirectionIndicatorPinkRedShare(pixels, visual), accepted,
                    withoutColorGate = AutoFightSeek.ClassifySeekVisual(mask, null, visual, frame.Width, frame.Height),
                    concavity = contour != null && AutoFightSeek.HasDirectionIndicatorConcavity(contour),
                    templateDistance = contour == null ? (double?)null : AutoFightSeek.GetDirectionIndicatorTemplateContours()
                        .Min(template => Cv2.MatchShapes(contour, template, ShapeMatchModes.I1)),
                    hollow = AutoFightSeek.GetDirectionIndicatorHollowRatio(candidate),
                    bearing = AutoFightSeek.MatchDirectionIndicatorTemplateBearing(candidate),
                    rejection = detail.ToCompactString() });
            }
            output.WriteLine(JsonConvert.SerializeObject(new { file = Path.GetFileName(path), hud, transformed,
                transformationEvidence = SaurianUiReader.Describe(frame), map, world,
                paimon = paimon.IsExist(), chat = chat.IsExist(), hp, panelSamples, reconnectText, cannon,
                ui = ui.Describe(), partyButton = partyButton.IsExist(), decision, hints,
                diagnostics = diagnostics.ToCompactString(), candidates }));
            Assert.Equal(0, Cv2.Norm(pixels, frame.SrcMat, NormTypes.L1));
        }
        Assert.Null(System.Windows.Application.Current);
    }
}
