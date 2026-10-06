using System;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal static class TeleportRejection
{
    internal static bool Read(ImageRegion frame, IOcrService ocr) => frame.ReadOnce(typeof(TeleportRejection), () =>
    {
        if (frame.Width < 320 || frame.Height < 180 || frame.Width * 9 != frame.Height * 16) return false;
        // 传送输入后角色倒下会弹出此提示并关闭地图，随后仍回到原战场。
        // 只读取这条居中提示，不把任务文字、未知HUD或单纯地图关闭当拒绝。
        using var area = frame.DeriveCrop(new Rect(frame.Width * 38 / 100, frame.Height * 17 / 100,
            frame.Width * 24 / 100, frame.Height * 6 / 100));
        var text = ocr.OcrWithoutDetector(area.SrcMat).Replace(" ", "", StringComparison.Ordinal);
        return text.Contains("角色无法继续战斗", StringComparison.Ordinal) ||
            text.Contains("角色無法繼續戰鬥", StringComparison.Ordinal);
    });

    internal static void Check(ImageRegion frame, string request, IOcrService? ocr = null)
    {
        if (!Read(frame, ocr ?? OcrFactory.Paddle)) return;
        try
        {
            DiagnosticEvidenceScope.Current?.TryCapture(frame, request, "teleport-rejected",
                "角色无法继续战斗；地图关闭不能作为传送到达证明",
                priority: DiagnosticEvidencePriority.Warning);
        }
        catch { /* 保存失败不改变明确拒绝的业务结果。 */ }
        throw new InvalidOperationException("传送被游戏拒绝：角色无法继续战斗；尚未到达目标，不签发恢复证明");
    }
}
