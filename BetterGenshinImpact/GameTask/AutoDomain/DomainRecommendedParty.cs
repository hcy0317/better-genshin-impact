using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition.OCR;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoDomain;

/// <summary>只识别“推荐元素”标签右侧的彩色图标，不从角色立绘或秘境名称推测元素。</summary>
public static class DomainRecommendedParty
{
    /// <summary>候选为空或游戏中没有候选队伍时，使用任务原本的队伍；默认名为空则保持当前队伍。</summary>
    public static async Task<bool> SwitchAsync(
        IReadOnlyList<string> recommendedElements,
        string? defaultPartyName,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>> tryRecommendedParty,
        Func<string, CancellationToken, Task<bool>> tryDefaultParty,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (recommendedElements.Count > 0 && await tryRecommendedParty(recommendedElements, ct)) return true;
        ct.ThrowIfCancellationRequested();
        return string.IsNullOrWhiteSpace(defaultPartyName) || await tryDefaultParty(defaultPartyName, ct);
    }

    public static IReadOnlyList<string> Recognize(Mat screen, OcrResult text)
    {
        var label = text.Regions.FirstOrDefault(region => region.Score >= 0.7f
            && region.Text.Replace(" ", "").Contains("推荐元素"));
        if (label == default || screen.Empty()) return [];

        var scale = screen.Height / 1080d;
        var bounds = label.Rect.BoundingRect();
        var height = Math.Max(bounds.Height * 2, (int)(48 * scale));
        var left = Math.Clamp(bounds.Right, 0, screen.Width);
        var top = Math.Clamp((int)label.Rect.Center.Y - height / 2, 0, screen.Height);
        var width = Math.Min((int)(350 * scale), screen.Width - left);
        height = Math.Min(height, screen.Height - top);
        if (width <= 0 || height <= 0) return [];

        using var strip = new Mat(screen, new Rect(left, top, width, height));
        using var bgr = new Mat();
        if (strip.Channels() == 4) Cv2.CvtColor(strip, bgr, ColorConversionCodes.BGRA2BGR);
        else strip.CopyTo(bgr);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        // 按列合并图标内部不连通的笔画，再检查尺寸和颜色一致性。
        var result = new List<string>();
        var start = -1;
        var last = -1;
        var gap = Math.Max(1, (int)(4 * scale));
        for (var x = 0; x <= width; x++)
        {
            var colored = false;
            if (x < width)
                for (var y = 0; y < height; y++)
                    if (Element(hsv.At<Vec3b>(y, x)) != null) { colored = true; break; }

            if (colored)
            {
                if (start < 0) start = x;
                last = x;
            }
            if (start >= 0 && (x == width || x - last > gap))
            {
                var element = ReadIcon(hsv, start, last, scale);
                if (element != null && !result.Contains(element)) result.Add(element);
                start = -1;
            }
        }
        return result;
    }

    private static string? ReadIcon(Mat hsv, int left, int right, double scale)
    {
        var width = right - left + 1;
        if (width < 8 * scale || width > 48 * scale) return null;
        var votes = new Dictionary<string, int>();
        var top = hsv.Height;
        var bottom = -1;
        for (var x = left; x <= right; x++)
        for (var y = 0; y < hsv.Height; y++)
        {
            var element = Element(hsv.At<Vec3b>(y, x));
            if (element == null) continue;
            votes[element] = votes.GetValueOrDefault(element) + 1;
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        var count = votes.Values.Sum();
        var height = bottom - top + 1;
        if (count < 24 * scale * scale || height < 8 * scale || height > 48 * scale
            || top == 0 || bottom == hsv.Height - 1 || votes.Count == 0) return null;
        var best = votes.MaxBy(pair => pair.Value);
        return best.Value >= count * 0.8 ? best.Key : null;
    }

    private static string? Element(Vec3b hsv)
    {
        // OpenCV hue: 0..179。低饱和文字、暗色背景和非元素色不参与投票。
        if (hsv.Item1 < 55 || hsv.Item2 < 120) return null;
        return hsv.Item0 switch
        {
            <= 12 or >= 173 => "火",
            >= 18 and <= 30 => "岩",
            >= 34 and <= 48 => "草",
            >= 70 and <= 85 => "风",
            >= 86 and <= 94 => "冰",
            >= 95 and <= 115 => "水",
            >= 125 and <= 150 => "雷",
            _ => null
        };
    }
}
