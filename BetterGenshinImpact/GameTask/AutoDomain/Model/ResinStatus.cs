using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Fischless.GameCapture;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.AutoPick;

namespace BetterGenshinImpact.GameTask.AutoDomain.Model;

public class ResinStatus
{
    /// <summary>
    /// 原粹树脂（1自回体）
    /// </summary>
    public int OriginalResinCount { get; set; } = 0;

    /// <summary>
    /// 脆弱树脂（60）
    /// </summary>
    public int FragileResinCount { get; set; } = 0;

    /// <summary>
    /// 浓缩树脂（60）
    /// </summary>
    public int CondensedResinCount { get; set; } = 0;

    /// <summary>
    /// 须臾树脂（60壶内购买）
    /// </summary>
    public int TransientResinCount { get; set; } = 0;

    public static ResinStatus RecogniseFromRegion(ImageRegion region, ISystemInfo systemInfo, IOcrService ocrService)
        => RecogniseFromRegion(region, systemInfo, ocrService, allowPartial: false);

    public static ResinStatus RecogniseFromRegion(ImageRegion region, ISystemInfo systemInfo, IOcrService ocrService, bool allowPartial)
    {

        // 1. 原粹树脂
        var assetScale = systemInfo.AssetScale;
        var originalResinTopIconRa = new RecognitionObject
        {
            Name = "OriginalResinTopIcon",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "original_resin_top_icon.png", systemInfo),
            DrawOnWindow = false
        }.InitTemplate();
        using ImageRegion crop1 = region.DeriveCrop(GetOriginalResinSearchRect(assetScale));
        //Cv2.ImShow("test", crop1.SrcMat);
        //Cv2.WaitKey();
        using var originalResinRes = crop1.Find(originalResinTopIconRa);
        if (originalResinRes.IsEmpty() && !allowPartial)
        {
            throw new Exception("未找到原粹树脂图标");
        }

        Rect? originalResinCountRect = originalResinRes.IsExist()
            ? new Rect(crop1.X + originalResinRes.Right + (int)(25 * assetScale), (int)(37 * assetScale), (int)(110 * assetScale), (int)(24 * assetScale))
            : null;
        Rect? condensedResinCountRect = null;

        // 2. 浓缩树脂
        // Partial preflight can locate the independent condensed icon in the same header even if the original icon is unavailable.
        var condensedSearchRect = originalResinRes.IsExist()
            ? new Rect(crop1.X + originalResinRes.Left - (int)(180 * assetScale), (int)(25 * assetScale), (int)(90 * assetScale), (int)(50 * assetScale))
            : GetOriginalResinSearchRect(assetScale);
        var condensedResinTopIconRa = new RecognitionObject
        {
            Name = "CondensedResinTopIcon",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "condensed_resin_top_icon.png", systemInfo),
            DrawOnWindow = false
        }.InitTemplate();
        using ImageRegion crop2 = region.DeriveCrop(condensedSearchRect);
        using var condensedResinRes = crop2.Find(condensedResinTopIconRa);
        if (condensedResinRes.IsExist())
        {
            // 找出 icon 的位置 + 25 ~ icon 的位置+45 就是浓缩树脂的数字，数字宽20
            condensedResinCountRect = new Rect(crop2.X + condensedResinRes.Right + (int)(20 * assetScale), crop2.Y + condensedResinRes.Y, (int)(30 * assetScale), condensedResinRes.Height);
        }

        var observed = ReadFromImage(region, originalResinCountRect, condensedResinCountRect, (crop, kind) =>
        {
            if (kind == "original")
                return new(crop.FrameStamp, new[] { ocrService.OcrWithoutDetector(crop.SrcMat) });
            using Mat threshold = crop.CacheGreyMat.Threshold(180, 255, ThresholdTypes.Binary);
            using Mat bitwiseNot = new();
            Cv2.BitwiseNot(threshold, bitwiseNot);
            return new(crop.FrameStamp, new[] { ocrService.OcrWithoutDetector(bitwiseNot) });
        }, validate: () =>
        {
            TaskExecutionScope.ThrowIfFailed();
            UiOperation.Current?.Check();
        });
        return ToNativeStatus(observed, allowPartial);
    }

    internal static ResinStatus ToNativeStatus(FrameReading observed, bool allowPartial = false)
    {
        if (!observed.Original.Known && (!allowPartial || !observed.Condensed.Known))
            throw new InvalidOperationException("无法识别原粹树脂数量：" + observed.Original.Reason);
        if (!observed.Condensed.Known && !allowPartial)
            throw new InvalidOperationException("无法识别浓缩树脂数量：" + observed.Condensed.Reason);
        return new ResinStatus
        {
            OriginalResinCount = observed.Original.Count ?? -1,
            CondensedResinCount = observed.Condensed.Count ?? -1
        };
    }

    internal sealed record OcrText(CaptureFrameStamp Source, IReadOnlyList<string> Texts);
    internal sealed record QuantityReading(int? Count, string Reason, Rect? Roi, IReadOnlyList<string> RawText)
    {
        public bool Known => Count.HasValue;
    }
    internal sealed record FrameReading(CaptureFrameStamp SourceBefore, CaptureFrameStamp SourceAfter,
        long ReadStarted, long ReadCompleted, long ClockFrequency, bool ContextUnchanged,
        QuantityReading Original, QuantityReading Condensed);

    internal static int ParseOriginalResinCount(string? text) =>
        ParseTexts(new[] { text ?? string.Empty }, true, null).Count
        ?? throw new InvalidOperationException($"无法识别原粹树脂数量：{text}");

    internal static int ParseCondensedResinCount(string? text) =>
        ParseTexts(new[] { text ?? string.Empty }, false, null).Count
        ?? throw new InvalidOperationException($"无法识别浓缩树脂数量：{text}");

    internal static QuantityReading ParseTexts(IReadOnlyList<string> texts, bool ratio, Rect? roi = null)
    {
        var raw = texts.ToArray();
        QuantityReading Unknown(string reason) => new(null, reason, roi, raw);
        string[] normalized;
        try { normalized = raw.Select(text => (text ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim()).Where(text => text.Length != 0).ToArray(); }
        catch (ArgumentException) { return Unknown("invalid-text"); }
        if (normalized.Length == 0) return Unknown("empty-text");
        var pattern = ratio ? @"^([0-9]+)\s*/\s*([0-9]+)$" : @"^(?:[xX×]\s*)?([0-9]+)$";
        bool TryValue(string text, out (int Count, int Capacity) value)
        {
            value = default;
            var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
            // Preserve the native reader's single-line OCR 1/7 separator alias, with a whole ratio and known capacity.
            if (!match.Success && ratio && normalized.Length == 1)
                match = Regex.Match(text, @"^([0-9]+)\s*[17]\s*(200|20|2)$", RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)) return false;
            var capacity = 0;
            if (ratio && (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out capacity) || capacity is not (2 or 20 or 200))) return false;
            value = (count, capacity);
            return true;
        }
        // Joining with spaces accepts split slash/marker tokens without concatenating separate numbers.
        if (TryValue(string.Join(" ", normalized), out var joined)) return new(joined.Count, "recognized", roi, raw);
        var complete = new List<(int Count, int Capacity)>();
        foreach (var token in normalized)
            if (TryValue(token, out var value)) complete.Add(value);
        if (complete.Count == normalized.Length)
        {
            if (complete.Distinct().Count() != 1) return Unknown("conflicting-text");
            return new(complete[0].Count, "recognized", roi, raw);
        }
        if (complete.Count != 0) return Unknown("ambiguous-text");
        return Unknown("unrecognized-text");
    }

    internal static FrameReading ReadFromImage(ImageRegion image, Rect? originalRoi, Rect? condensedRoi,
        Func<ImageRegion, string, OcrText> readText, TimeProvider? clock = null, Action? validate = null,
        Func<bool>? sameContext = null, bool requireKnownSource = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(readText);
        clock ??= TimeProvider.System;
        validate ??= () => { };
        sameContext ??= () => true;
        var before = image.FrameStamp;
        var started = clock.GetTimestamp();
        validate();
        QuantityReading Unknown(Rect? roi, string reason, IReadOnlyList<string>? raw = null) => new(null, reason, roi, raw ?? Array.Empty<string>());
        FrameReading Unavailable(string reason) => new(before, image.FrameStamp, started, clock.GetTimestamp(), clock.TimestampFrequency,
            sameContext(), Unknown(originalRoi, reason), Unknown(condensedRoi, reason));
        if (!sameContext()) return Unavailable("runtime-context-changed");
        if (requireKnownSource && !before.IsKnown) return Unavailable("source-unknown");
        if (before.IsKnown && (before.TimestampFrequency != clock.TimestampFrequency || before.CapturedTimestamp > started))
            return Unavailable("source-clock-mismatch");
        QuantityReading Read(Rect? roi, bool ratio)
        {
            validate();
            if (!sameContext()) return Unknown(roi, "runtime-context-changed");
            if (roi is not { } bounds) return Unknown(roi, "icon-or-roi-missing");
            if (bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0 ||
                (long)bounds.X + bounds.Width > image.SrcMat.Cols || (long)bounds.Y + bounds.Height > image.SrcMat.Rows)
                return Unknown(roi, "roi-outside-image");
            try
            {
                using var crop = image.DeriveCrop(bounds);
                var answer = readText(crop, ratio ? "original" : "condensed");
                validate();
                if (crop.FrameStamp != before || answer.Source != before)
                    return Unknown(roi, "ocr-source-mismatch", answer.Texts);
                return ParseTexts(answer.Texts, ratio, roi);
            }
            catch (Exception error) when (!IsTerminalReadFailure(error))
            {
                validate();
                return Unknown(roi, "ocr-failed:" + error.GetType().Name);
            }
        }
        var original = Read(originalRoi, true);
        var condensed = Read(condensedRoi, false);
        validate();
        var after = image.FrameStamp;
        var stable = sameContext();
        if (after != before || !stable)
        {
            var reason = after != before ? "frame-source-changed" : "runtime-context-changed";
            original = Unknown(originalRoi, reason, original.RawText);
            condensed = Unknown(condensedRoi, reason, condensed.RawText);
        }
        return new(before, after, started, clock.GetTimestamp(), clock.TimestampFrequency, stable,
            original, condensed);
    }

    internal static bool IsTerminalReadFailure(Exception error)
    {
        if (error is OperationCanceledException or TimeoutException or
            BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.NormalEndException or
            Microsoft.ClearScript.ScriptInterruptedException || TaskFailureRecoveryPolicy.IsRecoveryFailure(error) ||
            TaskExecutionScope.IsUnconfirmedCombat(error)) return true;
        if (error is AggregateException aggregate && aggregate.InnerExceptions.Any(IsTerminalReadFailure)) return true;
        return error.InnerException != null && IsTerminalReadFailure(error.InnerException);
    }
    internal static Rect GetOriginalResinSearchRect(double assetScale)
    {
        return new Rect(
            (int)(1200 * assetScale),
            (int)(25 * assetScale),
            (int)(580 * assetScale),
            (int)(50 * assetScale));
    }

    public void Print(ILogger logger)
    {
        logger.LogInformation("原粹树脂：{Cnt1}，浓缩树脂：{Cnt2}，须臾树脂：{Cnt3}，脆弱树脂：{Cnt4}",
            OriginalResinCount, CondensedResinCount, TransientResinCount, FragileResinCount);
    }
}
