using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.AutoFight.Config;
using BetterGenshinImpact.GameTask.Model.GameUI;
using OpenCvSharp;
using System;
using System.Collections.Generic;
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
        string? miliastraNickname,
        string? miliastraCharacterKey = null)
    {
        var rawName = ReadText(capture, NameRoi);
        var characterName = ResolveCharacterName(
            rawName, gameNickname, miliastraNickname,
            miliastraCharacterKey);
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
        string? miliastraNickname = null,
        string? miliastraCharacterKey = null)
    {
        var normalized = Normalize(rawText);
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("右侧角色名称 OCR 结果为空。");
        }

        var normalizedNickname = Normalize(gameNickname);
        var normalizedMiliastraNickname = Normalize(miliastraNickname);
        var exactMatches = DefaultAutoFightConfig.CombatAvatarNames
            .Where(name => string.Equals(
                normalized, Normalize(name), StringComparison.Ordinal))
            .ToArray();
        if ((normalizedNickname.Length > 0
                && string.Equals(normalized, normalizedNickname, StringComparison.Ordinal))
            || (normalizedMiliastraNickname.Length > 0
                && string.Equals(normalized, normalizedMiliastraNickname, StringComparison.Ordinal)))
        {
            if (exactMatches.Length > 0
                || (normalizedNickname.Length > 0
                    && normalizedMiliastraNickname.Length > 0
                    && string.Equals(
                        normalizedNickname,
                        normalizedMiliastraNickname,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"配置的昵称与标准角色名或另一昵称冲突：{rawText}");
            }
        }
        if (exactMatches.Length > 0)
        {
            return exactMatches[0];
        }

        if (normalizedNickname.Length > 0
            && string.Equals(normalized, normalizedNickname, StringComparison.Ordinal))
        {
            return "旅行者";
        }

        if (normalizedMiliastraNickname.Length > 0
            && string.Equals(normalized, normalizedMiliastraNickname, StringComparison.Ordinal))
        {
            return miliastraCharacterKey switch
            {
                null or "" or "MannequinGirl" => "奇偶·女性",
                "MannequinBoy" => "奇偶·男性",
                _ => throw new InvalidOperationException(
                    $"千星奇域角色键无效：{miliastraCharacterKey}")
            };
        }

        throw new InvalidOperationException($"无法把右侧角色名称 OCR 结果映射到标准角色：{rawText}");
    }

    internal static ulong DetailSignature(Mat capture)
    {
        using var detail = ArtifactUiCoordinateMapper.CropNormalized(
            capture,
            NameRoi.X, NameRoi.Y,
            NameRoi.Width, NameRoi.Height);
        using var gray = detail.Channels() == 4
            ? detail.CvtColor(ColorConversionCodes.BGRA2GRAY)
            : detail.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var textMask = gray.Threshold(170, 255, ThresholdTypes.Binary);
        using var reduced = textMask.Resize(
            new Size(9, 8), 0, 0, InterpolationFlags.Area);
        ulong signature = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            if (reduced.At<byte>(y, x) > reduced.At<byte>(y, x + 1))
                signature |= 1UL << bit;
            bit++;
        }
        return signature;
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
        var normalized = string.Concat((rawText ?? string.Empty).Where(character =>
            !char.IsWhiteSpace(character)));
        if (normalized.StartsWith("等级", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Length == 0) return false;

        int[] validLimits = [20, 40, 50, 60, 70, 80, 90];
        var slash = normalized.IndexOf('/');
        if (slash >= 0)
        {
            if (slash == 0
                || slash != normalized.LastIndexOf('/')
                || !int.TryParse(normalized[..slash], out var current)
                || !int.TryParse(normalized[(slash + 1)..], out var limit)
                || current is < 1 or > 90
                || current > limit
                || !validLimits.Contains(limit))
            {
                return false;
            }
            level = current;
            return true;
        }

        if (normalized.Any(character => !char.IsDigit(character))) return false;
        var digits = normalized;
        if (digits.Length is 0 or > 4) return false;
        if (int.TryParse(digits, out var single) && single is >= 1 and <= 90)
        {
            level = single;
            return true;
        }

        var candidates = new List<int>();
        foreach (var validLimit in validLimits)
        {
            var limitText = validLimit.ToString();
            if (!digits.EndsWith(limitText, StringComparison.Ordinal)) continue;
            var currentText = digits[..^limitText.Length];
            if (int.TryParse(currentText, out var current)
                && current is >= 1 and <= 90
                && current <= validLimit)
            {
                candidates.Add(current);
            }
        }
        if (candidates.Distinct().Count() != 1) return false;
        level = candidates[0];
        return true;
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
