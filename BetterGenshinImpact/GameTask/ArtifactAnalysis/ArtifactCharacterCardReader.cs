using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoFight.Config;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal static partial class ArtifactCharacterCardReader
{
    private static readonly IReadOnlyDictionary<string, string> ArtifactKeyAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Alhatham"] = "Alhaitham",
            ["Ambor"] = "Amber",
            ["Itto"] = "AratakiItto",
            ["Baizhuer"] = "Baizhu",
            ["Hutao"] = "HuTao",
            ["Qin"] = "Jean",
            ["Kazuha"] = "KaedeharaKazuha",
            ["Ayaka"] = "KamisatoAyaka",
            ["Ayato"] = "KamisatoAyato",
            ["Momoka"] = "Kirara",
            ["Sara"] = "KujouSara",
            ["Shinobu"] = "KukiShinobu",
            ["Lanyan"] = "LanYan",
            ["Linette"] = "Lynette",
            ["Liney"] = "Lyney",
            ["Noel"] = "Noelle",
            ["Olorun"] = "Ororon",
            ["Shougun"] = "RaidenShogun",
            ["Marionette"] = "Sandrone",
            ["Kokomi"] = "SangonomiyaKokomi",
            ["Heizo"] = "ShikanoinHeizou",
            ["SkirkNew"] = "Skirk",
            ["Tohma"] = "Thoma",
            ["Liuyun"] = "Xianyun",
            ["Yae"] = "YaeMiko",
            ["Feiyan"] = "Yanfei",
            ["Mizuki"] = "YumemizukiMizuki",
            ["Yunjin"] = "YunJin"
        };

    internal static int ReadLevel(Mat card, double assetScale)
    {
        var y = Math.Clamp((int)Math.Round(112 * assetScale), 0, card.Height - 1);
        using var levelRegion = card.SubMat(new Rect(0, y, card.Width, card.Height - y));
        using var enlarged = levelRegion.Resize(new Size(), 2, 2, InterpolationFlags.Cubic);
        var text = OcrFactory.Paddle.Ocr(enlarged);
        if (!TryParseLevel(text, out var level))
        {
            throw new InvalidOperationException($"无法识别角色卡片等级：{text}");
        }
        return level;
    }

    internal static bool TryParseLevel(string? text, out int level)
    {
        level = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (Match match in LevelNumberRegex().Matches(text))
        {
            if (int.TryParse(match.Value, out var candidate) && candidate is >= 1 and <= 90)
            {
                level = candidate;
                return true;
            }
        }
        return false;
    }

    internal static bool IsFavorite(Mat card, double assetScale)
    {
        var x = Math.Clamp((int)Math.Round(82 * assetScale), 0, card.Width - 1);
        var y = Math.Clamp((int)Math.Round(2 * assetScale), 0, card.Height - 1);
        var width = Math.Min(card.Width - x, Math.Max(1, (int)Math.Round(31 * assetScale)));
        var height = Math.Min(card.Height - y, Math.Max(1, (int)Math.Round(31 * assetScale)));
        using var roi = card.SubMat(new Rect(x, y, width, height));
        using var hsv = roi.CvtColor(ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(10, 80, 140), new Scalar(42, 255, 255), mask);
        var minimumPixels = Math.Max(8, (int)Math.Round(12 * assetScale * assetScale));
        return Cv2.CountNonZero(mask) >= minimumPixels;
    }

    internal static string ToCharacterKey(string characterName)
    {
        if (!DefaultAutoFightConfig.CombatAvatarMap.TryGetValue(characterName, out var avatar))
        {
            throw new InvalidOperationException($"角色原型缺少标准英文键：{characterName}");
        }
        return ArtifactKeyAliases.GetValueOrDefault(avatar.NameEn, avatar.NameEn);
    }

    [GeneratedRegex(@"\d{1,2}")]
    private static partial Regex LevelNumberRegex();
}
