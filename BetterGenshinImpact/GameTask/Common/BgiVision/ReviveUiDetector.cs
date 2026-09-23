using System;
using System.Linq;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.BgiVision;

/// <summary>借用当前帧与识别依赖，只返回复苏观察，不读取应用状态或发送输入。</summary>
internal sealed class ReviveUiDetector(
    RecognitionObject confirmation,
    Func<IOcrService> ocr,
    string revival,
    string foodTitle)
{
    internal ReviveUiDetector(RecognitionObject confirmation, IOcrService ocr,
        string revival, string foodTitle) : this(confirmation, () => ocr, revival, foodTitle) { }

    internal ReviveUiState Read(ImageRegion region) => region.ReadOnce(
        (typeof(ReviveUiDetector), confirmation, revival, foodTitle), () => ReadCore(region));

    private (bool Confirmation, bool LivingHud) Inspect(ImageRegion region) => region.ReadOnce(
        (typeof(ReviveUiDetector), confirmation), () =>
        {
            using var confirm = region.Find(confirmation);
            var found = !confirm.IsEmpty();
            return (found, !found && HasUnobscuredLivingHud(region));
        });

    internal bool IsCombatHud(ImageRegion region) => Inspect(region).LivingHud;

    private ReviveUiState ReadCore(ImageRegion region)
    {
        var hud = Inspect(region);
        if (hud.Confirmation)
        {
            var title = region.FindMulti(new RecognitionObject
            {
                RecognitionType = RecognitionTypes.Ocr,
                // 弹窗分类属于恢复阶段，保留完整标题搜索范围，不为战斗延迟目标裁掉证据。
                RegionOfInterest = new Rect(0, 0, region.Width, region.Height / 2)
            }, ocrService: ocr());
            try
            {
                return Bv.ClassifyReviveEvidence(true,
                    title.Any(item => Bv.IsReviveFoodTitle(item.Text, revival, foodTitle)), false);
            }
            finally { foreach (var item in title) item.Dispose(); }
        }

        if (hud.LivingHud) return ReviveUiState.None;

        var buttons = region.FindMulti(RecognitionObject.Ocr(region.Width / 4d,
            region.Height * 2d / 3, region.Width / 2d, region.Height / 3d), ocrService: ocr());
        try
        {
            return Bv.ClassifyReviveEvidence(false, false,
                buttons.Any(item => Bv.IsReviveText(item.Text, revival)));
        }
        finally { foreach (var item in buttons) item.Dispose(); }
    }

    private static bool HasUnobscuredLivingHud(ImageRegion image)
    {
        var mat = image.SrcMat;
        // 不把未知布局/像素格式套进已知HUD规则；它们继续走原来的识别路径。
        if ((mat.Type() != MatType.CV_8UC3 && mat.Type() != MatType.CV_8UC4)
            || image.Width * 9 != image.Height * 16) return false;
        var scale = image.Height / 1080d;
        var x = (int)Math.Round(image.Width / 2d - 152 * scale);
        var y = (int)Math.Round(image.Height - 70 * scale);
        if (x < 0 || y < 0 || x + 4 >= image.Width || y >= image.Height) return false;
        foreach (var offset in new[] { 0, 2, 4 })
        {
            byte blue, green, red;
            if (mat.Channels() == 3)
            {
                var pixel = mat.At<Vec3b>(y, x + offset);
                (blue, green, red) = (pixel.Item0, pixel.Item1, pixel.Item2);
            }
            else
            {
                var pixel = mat.At<Vec4b>(y, x + offset);
                (blue, green, red) = (pixel.Item0, pixel.Item1, pixel.Item2);
            }
            // 血条会随HUD动画改变亮度。这里只判断是否仍有绿/红血条，
            // 是否被弹窗遮暗由独立HUD锚点判断，不能把正常淡出误作复苏。
            var healthy = green >= 100 && green > red * 1.2 && green > blue * 3;
            var low = red >= 150 && red > green * 1.6 && Math.Abs(green - blue) <= 15;
            if (!healthy && !low) return false;
        }
        using var paimon = image.Find(ElementRecognition.Get("PaimonMenu", image));
        if (HasBrightAnchor(image, paimon)) return true;
        using var chat = image.Find(ElementRecognition.Get("FriendChat", image));
        // 正常聊天图标的白色前景可为约230；维护公告遮住派蒙时仍须可作备用锚点。
        // 只调整已匹配的聊天图标，保留血条、确认弹窗和像素数量检查。
        return HasBrightAnchor(image, chat, 220);
    }

    private static bool HasBrightAnchor(ImageRegion image, Region anchor, int minimumBrightness = 235)
    {
        if (!anchor.IsExist()) return false;
        using var area = image.DeriveCrop(new Rect(anchor.X, anchor.Y, anchor.Width, anchor.Height));
        using var white = new Mat();
        Cv2.InRange(area.SrcMat, new Scalar(minimumBrightness, minimumBrightness, minimumBrightness, 0), Scalar.All(255), white);
        // 归一化模板匹配可能命中弹窗后的暗化图标；只有实际白色前景仍可见才授予HUD证据。
        return Cv2.CountNonZero(white) >= Math.Max(4, anchor.Width * anchor.Height / 20);
    }
}
