using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.AutoFight.Config;
using BetterGenshinImpact.GameTask.Model.GameUI;
using OpenCvSharp;
using System;
using System.Globalization;
using System.Linq;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal sealed record ArtifactCharacterDetailSample(
    string CharacterName,
    int Level,
    bool Favorite);

/// <summary>
/// Reads the selected character from the right-hand detail panel.
/// The left grid is deliberately used only for locating, clicking and scrolling cards.
/// </summary>
internal sealed class ArtifactCharacterDetailsReader : IDisposable
{
    internal const bool ForceCpuOcr = true;
    internal const bool LoadsDetectionModel = false;
    private static readonly Rect2d NameRoi = new(1221.6667, 105, 216.6667, 40);
    private static readonly Rect2d LevelRoi = new(1215, 161.6667, 183.3333, 48.3333);
    private static readonly Rect2d FavoriteRoi = new(1156.6667, 813.3333, 55, 55);
    private static readonly Rect2d DetailSignatureRoi = new(1206.6667, 98.3333, 266.6667, 125);
    private readonly BgiOnnxFactory _ocrFactory;
    private readonly Rec _ocrRecognizer;

    internal ArtifactCharacterDetailsReader()
    {
        var cultureName = TaskContext.Instance().Config.OtherConfig.GameCultureInfoName;
        if (!string.Equals(cultureName, "zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("角色配装检测当前仅支持简体中文游戏界面。");
        }

        _ocrFactory = new BgiOnnxFactory(
            App.GetLogger<BgiOnnxFactory>(),
            forceCpuOcr: ForceCpuOcr,
            excludeTensorRtForOcr: true);
        var model = ArtifactInventoryUi.SelectOcrModel(new CultureInfo(cultureName));
        _ocrRecognizer = new Rec(
            model.RecognitionModel,
            model.RecLabel(),
            model.RecognitionVersion,
            _ocrFactory);
    }

    internal ArtifactCharacterDetailSample Read(
        Mat capture,
        string? gameNickname,
        string? miliastraNickname)
    {
        var rawName = ReadText(capture, NameRoi);
        var characterName = ResolveCharacterName(
            rawName, gameNickname, miliastraNickname);
        var rawLevel = ReadText(capture, LevelRoi);
        if (!TryParseLevel(rawLevel, out var level))
        {
            throw new InvalidOperationException($"无法从右侧角色详情读取等级：{rawLevel}");
        }

        return new ArtifactCharacterDetailSample(
            characterName,
            level,
            IsFavorite(capture));
    }

    internal static string ResolveCharacterName(
        string? rawText,
        string? gameNickname = null,
        string? miliastraNickname = null)
    {
        var normalized = Normalize(rawText);
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("右侧角色名称 OCR 结果为空。");
        }

        var exactMatches = DefaultAutoFightConfig.CombatAvatarNames
            .Where(name => string.Equals(
                normalized, Normalize(name), StringComparison.Ordinal))
            .ToArray();
        if (exactMatches.Length > 0)
        {
            return exactMatches[0];
        }

        var normalizedNickname = Normalize(gameNickname);
        if (normalizedNickname.Length > 0
            && string.Equals(normalized, normalizedNickname, StringComparison.Ordinal))
        {
            return "旅行者";
        }

        var normalizedMiliastraNickname = Normalize(miliastraNickname);
        if (normalizedMiliastraNickname.Length > 0
            && string.Equals(normalized, normalizedMiliastraNickname, StringComparison.Ordinal))
        {
            return "奇偶·女性";
        }

        throw new InvalidOperationException($"无法把右侧角色名称 OCR 结果映射到标准角色：{rawText}");
    }

    internal static double DetailSignature(Mat capture)
    {
        using var detail = ArtifactUiCoordinateMapper.CropNormalized(
            capture,
            DetailSignatureRoi.X, DetailSignatureRoi.Y,
            DetailSignatureRoi.Width, DetailSignatureRoi.Height);
        var sum = Cv2.Sum(detail);
        return sum.Val0 + sum.Val1 + sum.Val2;
    }

    internal static bool IsFavorite(Mat capture)
    {
        using var favorite = ArtifactUiCoordinateMapper.CropNormalized(
            capture,
            FavoriteRoi.X, FavoriteRoi.Y,
            FavoriteRoi.Width, FavoriteRoi.Height);
        using var hsv = favorite.CvtColor(ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(12, 90, 150), new Scalar(42, 255, 255), mask);
        return Cv2.CountNonZero(mask) >= 80;
    }

    private string ReadText(Mat capture, Rect2d logicalRoi)
    {
        using var region = ArtifactUiCoordinateMapper.CropNormalized(
            capture,
            logicalRoi.X, logicalRoi.Y,
            logicalRoi.Width, logicalRoi.Height);
        return _ocrRecognizer.Run(region).Text.Trim();
    }

    internal static bool TryParseLevel(string? rawText, out int level)
    {
        level = 0;
        var digits = string.Concat((rawText ?? string.Empty).Where(char.IsDigit));
        if (digits.Length is 0 or > 5) return false;
        if (int.TryParse(digits, out var single) && single is >= 1 and <= 90)
        {
            level = single;
            return true;
        }

        int[] validLimits = [20, 40, 50, 60, 70, 80, 90];
        foreach (var validLimit in validLimits)
        {
            var limitText = validLimit.ToString();
            if (!digits.EndsWith(limitText, StringComparison.Ordinal)) continue;
            var currentText = digits[..^limitText.Length];
            if (currentText.Length == 3) currentText = currentText[..2];
            if (int.TryParse(currentText, out var current)
                && current is >= 1 and <= 90
                && current <= validLimit)
            {
                level = current;
                return true;
            }
        }

        for (var split = 1; split < digits.Length; split++)
        {
            if (digits[split] == '0'
                || !int.TryParse(digits[..split], out var current)
                || !int.TryParse(digits[split..], out var limit)
                || current is < 1 or > 90
                || current > limit
                || !validLimits.Contains(limit))
            {
                continue;
            }
            level = current;
            return true;
        }
        return false;
    }

    private static string Normalize(string? value) =>
        string.Concat((value ?? string.Empty).Where(character =>
            !char.IsWhiteSpace(character)
            && !char.IsPunctuation(character)
            && !char.IsSymbol(character)));

    public void Dispose()
    {
        _ocrRecognizer.Dispose();
        _ocrFactory.Dispose();
    }
}
