using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.Ui;

/// <summary>冒险之证委托页的组合证据；只在通用特征无法识别时按限定区域补查。</summary>
internal static class HandbookUiRecognition
{
    private static readonly HashSet<string> Tabs = new(StringComparer.Ordinal)
        { "见闻", "委托", "秘境", "讨伐", "向导", "备战", "見聞", "委託", "討伐", "嚮導", "備戰" };
    private static readonly string[] CommissionLabels =
        ["每日委托奖励", "选择委托任务倾向地域", "长效历练点", "每日委託獎勵", "長效歷練點"];

    internal static bool IsCommissionPage(IEnumerable<string> tabs, IEnumerable<string> contents)
    {
        static string Normalize(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        var side = tabs.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        return (side.Contains("委托") || side.Contains("委託")) && side.Count(Tabs.Contains) >= 2
            && contents.Select(Normalize).Any(text => CommissionLabels.Any(label => text.Contains(label, StringComparison.Ordinal)));
    }

    internal readonly record struct CommissionFeatures(bool ShellVisible, bool TabVisible, bool RewardVisible, Rect TabBounds);

    internal static CommissionFeatures ReadCommission(ImageRegion image, IOcrService ocr, bool selectedCommission = false)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        var scale = image.Width / 1920d;
        static string Normalize(string text) => string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        var sidebarVisible = false;
        if (!selectedCommission)
        {
            var sidebar = image.FindMulti(RecognitionObject.Ocr(200 * scale, 180 * scale, 190 * scale, 630 * scale), ocrService: ocr);
            try
            {
                var labels = sidebar.Select(region => Normalize(region.Text)).ToHashSet(StringComparer.Ordinal);
                sidebarVisible = (labels.Contains("委托") || labels.Contains("委託")) &&
                    labels.Any(label => Tabs.Contains(label) && label is not ("委托" or "委託"));
            }
            finally { foreach (var region in sidebar) region.Dispose(); }
        }
        var tabs = image.FindMulti(RecognitionObject.Ocr(260 * scale, 317 * scale, 89 * scale, 47 * scale), ocrService: ocr);
        var tabBounds = default(Rect);
        try
        {
            var tab = tabs.FirstOrDefault(region => Normalize(region.Text) is "委托" or "委託");
            if (tab != null) tabBounds = new((int)tab.X, (int)tab.Y, (int)tab.Width, (int)tab.Height);
        }
        finally { foreach (var region in tabs) region.Dispose(); }
        var rewards = image.FindMulti(RecognitionObject.Ocr(427 * scale, 345 * scale, 142 * scale, 36 * scale), ocrService: ocr);
        var rewardVisible = false;
        try
        {
            rewardVisible = rewards.Any(region => Normalize(region.Text) is "每日委托奖励" or "每日委託獎勵");
        }
        finally { foreach (var region in rewards) region.Dispose(); }
        var tabVisible = tabBounds.Width > 0 && tabBounds.Height > 0;
        var shellVisible = selectedCommission || (sidebarVisible && tabVisible);
        return new(shellVisible, shellVisible && tabVisible, shellVisible && rewardVisible, tabBounds);
    }

    internal static bool Read(ImageRegion image, IOcrService? ocr = null)
    {
        var scale = image.Width / 1920d;
        IEnumerable<string> Texts(double x, double y, double width, double height)
        {
            var regions = image.FindMulti(RecognitionObject.Ocr(x * scale, y * scale, width * scale, height * scale), ocrService: ocr);
            try { foreach (var region in regions) yield return region.Text; }
            finally { foreach (var region in regions) region.Dispose(); }
        }
        return IsCommissionPage(Texts(200, 180, 190, 630), Texts(380, 180, 1220, 730));
    }
}
