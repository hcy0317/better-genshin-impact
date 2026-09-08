using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Model.Area;

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

    internal static bool Read(ImageRegion image)
    {
        var scale = image.Width / 1920d;
        IEnumerable<string> Texts(double x, double y, double width, double height)
        {
            foreach (var region in image.FindMulti(RecognitionObject.Ocr(x * scale, y * scale, width * scale, height * scale)))
                yield return region.Text;
        }
        return IsCommissionPage(Texts(200, 180, 190, 630), Texts(380, 180, 1220, 730));
    }
}
