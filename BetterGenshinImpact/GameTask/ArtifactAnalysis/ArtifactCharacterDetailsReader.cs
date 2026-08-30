using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoFight.Config;
using BetterGenshinImpact.GameTask.Model.GameUI;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal sealed record ArtifactCharacterDetailSample(
    string CharacterName,
    int Level,
    bool Favorite);

internal sealed record ArtifactCharacterPartialDetail(
    string? CharacterName,
    int? Level,
    bool Favorite,
    InvalidOperationException? NameFailure,
    InvalidOperationException? LevelFailure)
{
    internal bool IsComplete => CharacterName is not null && Level.HasValue;

    internal IEnumerable<Exception> Failures
    {
        get
        {
            if (NameFailure is not null) yield return NameFailure;
            if (LevelFailure is not null) yield return LevelFailure;
        }
    }

    internal ArtifactCharacterPartialDetail Merge(ArtifactCharacterPartialDetail retry) =>
        new(
            CharacterName ?? retry.CharacterName,
            Level ?? retry.Level,
            retry.Favorite,
            CharacterName is null ? retry.NameFailure ?? NameFailure : null,
            Level is null ? retry.LevelFailure ?? LevelFailure : null);

    internal ArtifactCharacterDetailSample RequireComplete()
    {
        if (CharacterName is not null && Level.HasValue)
        {
            return new ArtifactCharacterDetailSample(
                CharacterName,
                Level.Value,
                Favorite);
        }

        throw new InvalidOperationException(
            "右侧角色详情仍有字段无法识别。",
            new AggregateException(Failures));
    }
}

/// <summary>
/// Reads the selected character from the right-hand detail panel.
/// The left grid is deliberately used only for locating, clicking and scrolling cards.
/// </summary>
internal sealed class ArtifactCharacterDetailsReader : IDisposable
{
    private const int MaximumRetrySignatureDistance = 2;
    internal const bool ForceCpuOcr = false;
    internal const bool LoadsDetectionModel = false;
    private static readonly Rect2d NameRoi = new(1221.6667, 105, 216.6667, 40);
    private static readonly Rect2d LevelRoi = new(1215, 170, 183.3333, 30);
    private static readonly Rect2d FavoriteRoi = new(1156.6667, 813.3333, 55, 55);
    private readonly IOcrService? _ocrService;
    private readonly ArtifactRecognitionOnlyOcrSession? _ownedOcrSession;

    internal ArtifactCharacterDetailsReader(IOcrService? ocrService = null)
    {
        var cultureName = TaskContext.Instance().Config.OtherConfig.GameCultureInfoName;
        if (!string.Equals(cultureName, "zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("角色配装检测当前仅支持简体中文游戏界面。");
        }

        if (ocrService is not null)
        {
            _ocrService = ocrService;
            return;
        }

        _ownedOcrSession = new ArtifactRecognitionOnlyOcrSession(
            forceCpuOcr: ForceCpuOcr);
    }

    internal ArtifactCharacterDetailSample Read(
        Mat capture,
        string? gameNickname,
        string? miliastraNickname,
        string? miliastraCharacterKey = null)
        => ReadPartial(
                capture,
                gameNickname,
                miliastraNickname,
                miliastraCharacterKey)
            .RequireComplete();

    internal ArtifactCharacterPartialDetail ReadPartial(
        Mat capture,
        string? gameNickname,
        string? miliastraNickname,
        string? miliastraCharacterKey = null,
        bool readName = true,
        bool readLevel = true)
    {
        string? rawName = null;
        string? rawLevel = null;
        InvalidOperationException? nameOcrFailure = null;
        InvalidOperationException? levelOcrFailure = null;
        try
        {
            if (readName) rawName = ReadText(capture, NameRoi);
        }
        catch (Exception exception)
        {
            nameOcrFailure = new InvalidOperationException(
                "右侧角色名称 OCR 执行失败。",
                exception);
        }

        try
        {
            if (readLevel) rawLevel = ReadText(capture, LevelRoi);
        }
        catch (Exception exception)
        {
            levelOcrFailure = new InvalidOperationException(
                "右侧角色等级 OCR 执行失败。",
                exception);
        }

        var partial = ParseRawPartial(
            rawName,
            rawLevel,
            IsFavorite(capture),
            gameNickname,
            miliastraNickname,
            miliastraCharacterKey);
        return partial with
        {
            NameFailure = nameOcrFailure ?? partial.NameFailure,
            LevelFailure = levelOcrFailure ?? partial.LevelFailure
        };
    }

    internal static ArtifactCharacterPartialDetail ParseRawPartial(
        string? rawName,
        string? rawLevel,
        bool favorite,
        string? gameNickname = null,
        string? miliastraNickname = null,
        string? miliastraCharacterKey = null)
    {
        string? characterName = null;
        InvalidOperationException? nameFailure = null;
        try
        {
            characterName = ResolveCharacterName(
                rawName,
                gameNickname,
                miliastraNickname,
                miliastraCharacterKey);
        }
        catch (InvalidOperationException exception)
        {
            nameFailure = exception;
        }

        int? level = null;
        InvalidOperationException? levelFailure = null;
        if (TryParseLevel(rawLevel, out var parsedLevel))
        {
            level = parsedLevel;
        }
        else
        {
            levelFailure = new InvalidOperationException(
                $"无法从右侧角色详情读取等级：{rawLevel}");
        }

        return new ArtifactCharacterPartialDetail(
            characterName,
            level,
            favorite,
            nameFailure,
            levelFailure);
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

        var fuzzyMatches = DefaultAutoFightConfig.CombatAvatarNames
            .Select(name => new
            {
                Name = name,
                Normalized = Normalize(name)
            })
            .Where(candidate => candidate.Normalized.Length == normalized.Length
                                && EditDistance(normalized, candidate.Normalized) == 1)
            .Select(candidate => candidate.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (fuzzyMatches.Length == 1)
        {
            return fuzzyMatches[0];
        }

        throw new InvalidOperationException($"无法把右侧角色名称 OCR 结果映射到标准角色：{rawText}");
    }

    private static int EditDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            var current = new int[right.Length + 1];
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = previous[rightIndex - 1]
                                   + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    substitution);
            }
            previous = current;
        }
        return previous[right.Length];
    }

    internal static ulong DetailSignature(Mat capture)
        => ArtifactVisualSignature.Compute(
            capture,
            NameRoi.X, NameRoi.Y,
            NameRoi.Width, NameRoi.Height);

    internal static bool IsSameDetailForRetry(
        ulong initialSignature,
        ulong retrySignature) =>
        ArtifactVisualSignature.Distance(initialSignature, retrySignature)
        <= MaximumRetrySignatureDistance;

    internal static Rect NameRegionForCapture(Size captureSize) =>
        ArtifactUiCoordinateMapper.ToCaptureRect(
            captureSize,
            NameRoi.X, NameRoi.Y,
            NameRoi.Width, NameRoi.Height);

    internal static Rect LevelRegionForCapture(Size captureSize) =>
        ArtifactUiCoordinateMapper.ToCaptureRect(
            captureSize,
            LevelRoi.X, LevelRoi.Y,
            LevelRoi.Width, LevelRoi.Height);

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
        return RecognizeWithoutDetector(region);
    }

    internal string RecognizeWithoutDetector(Mat region) =>
        (_ocrService?.OcrWithoutDetector(region)
            ?? _ownedOcrSession!.RecognizeWithoutDetector(region))
        .Trim();

    internal static bool TryParseLevel(string? rawText, out int level)
    {
        level = 0;
        var normalized = string.Concat((rawText ?? string.Empty).Where(character =>
            !char.IsWhiteSpace(character)));
        if (normalized.StartsWith("等级", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Length == 0 || normalized.Any(char.IsLetter)) return false;

        int[] validLimits = [20, 40, 50, 60, 70, 80, 90];
        var numberGroups = Regex.Matches(normalized, @"\d+")
            .Select(match => match.Value)
            .ToArray();
        if (numberGroups.Length == 2)
        {
            if (!int.TryParse(numberGroups[1], out var limit)
                || !validLimits.Contains(limit)
                || !TryParseCurrentLevel(numberGroups[0], limit, out var current))
            {
                return false;
            }
            level = current;
            return true;
        }
        if (numberGroups.Length != 1) return false;

        var digits = numberGroups[0];
        if (digits.Length is 0 or > 5) return false;
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
            if (currentText.Length == 3 && IsObservedSeparatorDigit(currentText[^1]))
            {
                currentText = currentText[..^1];
            }
            else if (currentText.Length > 2)
            {
                continue;
            }
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

    private static bool TryParseCurrentLevel(
        string digits,
        int limit,
        out int current)
    {
        if (int.TryParse(digits, out current)
            && current is >= 1 and <= 90
            && current <= limit)
        {
            return true;
        }

        if (digits.Length == 3
            && IsObservedSeparatorDigit(digits[^1])
            && int.TryParse(digits[..^1], out current)
            && current is >= 1 and <= 90
            && current <= limit)
        {
            return true;
        }

        current = 0;
        return false;
    }

    private static bool IsObservedSeparatorDigit(char value) =>
        value is '1' or '7';

    private static string Normalize(string? value) =>
        string.Concat((value ?? string.Empty).Where(character =>
            !char.IsWhiteSpace(character)
            && !char.IsPunctuation(character)
            && !char.IsSymbol(character)));

    public void Dispose()
    {
        _ownedOcrSession?.Dispose();
    }
}
