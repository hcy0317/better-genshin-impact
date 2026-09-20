using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using OpenCvSharp;
using Compunet.YoloSharp;
using Compunet.YoloSharp.Data;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Model;

/// <summary>借用单帧和原生视觉依赖。生产与离线测量共用，结果不跨帧缓存。</summary>
internal static class CombatHudReader
{
    internal static HashSet<int> ReadSideBurstReady(ImageRegion frame)
    {
        // 侧栏算法会调整像素，不能污染同帧的其他观察。
        using var clone = new ImageRegion(frame.SrcMat.Clone(), 0, 0) { FrameStamp = frame.FrameStamp };
        return AutoFightSkill.AvatarQSkillAsync(clone).GetAwaiter().GetResult().ToHashSet();
    }

    internal readonly record struct CooldownReading(string Raw, double Seconds);
    internal readonly record struct BurstReading(string Label, double Confidence, BurstObservation Observation);

    internal static CooldownReading ReadCooldown(ImageRegion frame, IOcrService ocr) =>
        frame.ReadOnce((typeof(CombatHudReader), "e-cooldown"), () =>
        {
            using var area = frame.DeriveCrop(AutoFightAssets.Get(frame).ECooldownRect);
            // 实拍冷却数字可为灰白色（约211），不能要求接近纯白而抹掉已施放证据。
            using var white = OpenCvCommonHelper.InRangeHsv(area.SrcMat, new Scalar(0, 0, 200), new Scalar(0, 25, 255));
            var raw = ocr.OcrWithoutDetector(white);
            return new CooldownReading(raw, StringUtils.TryParseDouble(raw));
        });

    internal static BurstReading ReadBurst(ImageRegion frame, BgiYoloPredictor predictor) =>
        frame.ReadOnce((typeof(CombatHudReader), "burst"), () =>
        {
            using var area = frame.DeriveCrop(AutoFightAssets.Get(frame).QRectForClassify);
            if (RecognitionReadinessScope.IsNonBlocking && !predictor.IsClassificationPrepared(area.Width, area.Height))
                throw new RecognitionNotReadyException("当前Q识别尺寸尚未完成首推理准备");
            var top = predictor.UsePredictor(p => p.Classify(area.CacheImage).GetTopClass());
            return new BurstReading(top.Name.Name, top.Confidence, BurstObservation.FromClassifier(top.Name.Name, top.Confidence));
        });

    internal static bool HasCooldownPixels(ImageRegion frame, bool burst) =>
        frame.ReadOnce((typeof(CombatHudReader), burst ? "q-pixels" : "e-pixels"), () =>
        {
            var rect = burst
                ? new Rect(frame.Width * 1809 / 1920, frame.Height * 968 / 1080, frame.Width * 30 / 1920, frame.Height * 15 / 1080)
                : new Rect(frame.Width * 1688 / 1920, frame.Height * 988 / 1080, frame.Width * 22 / 1920, frame.Height * 12 / 1080);
            using var area = frame.DeriveCrop(rect);
            using var mask = OpenCvCommonHelper.Threshold(area.SrcMat, Scalar.All(255), Scalar.All(255));
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            return Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids, PixelConnectivity.Connectivity4, MatType.CV_32S) > 2;
        });
}
