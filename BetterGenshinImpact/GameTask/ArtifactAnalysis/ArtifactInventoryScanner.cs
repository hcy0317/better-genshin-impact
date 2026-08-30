using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Model.GameUI;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

public sealed class ArtifactInventoryScanner : IArtifactInventoryScanner
{
    public async Task<ArtifactSnapshotDto> ScanAsync(string uid, CancellationToken cancellationToken)
    {
        var task = new ArtifactInventoryScanTask(uid, cancellationToken);
        await new TaskRunner().RunSoloTaskAsync(task, propagateExceptions: true);
        return task.Result ?? throw new InvalidOperationException(
            "Artifact inventory scan ended without a complete snapshot.");
    }

    public async Task<ArtifactExecutionObservationDto> InspectForExecutionAsync(
        string uid,
        int expectedArtifactCount,
        IReadOnlyList<ArtifactLaunchTargetDto> targets,
        CancellationToken cancellationToken)
    {
        var task = new ArtifactInventoryExecutionInspectionTask(
            uid, expectedArtifactCount, targets, cancellationToken);
        await new TaskRunner().RunSoloTaskAsync(task, propagateExceptions: true);
        return task.Result ?? throw new InvalidOperationException(
            "Artifact execution inspection ended without a complete observation.");
    }
}

internal sealed class ArtifactInventoryExecutionInspectionTask(
    string uid,
    int expectedArtifactCount,
    IReadOnlyList<ArtifactLaunchTargetDto> targets,
    CancellationToken externalCancellationToken) : ISoloTask
{
    private readonly ILogger _logger = App.GetLogger<ArtifactInventoryExecutionInspectionTask>();
    public string Name => "圣遗物执行前核验";
    public ArtifactExecutionObservationDto? Result { get; private set; }

    public async Task Start(CancellationToken taskCancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            taskCancellationToken,
            externalCancellationToken);
        var ct = linked.Token;
        await new ReturnMainUiTask().Start(ct);
        ct.ThrowIfCancellationRequested();
        using var ocrSession = new ArtifactRecognitionOnlyOcrSession(
            forceCpuOcr: ArtifactInventoryUi.ForceCpuOcr,
            parallelRecognizerCount:
                ArtifactRecognitionOnlyOcrSession.InventoryParallelRecognizerCount);
        await ArtifactGameIdentityVerifier.EnsureExpectedUidAsync(
            uid, ocrSession.RecognizeWithoutDetector, ct);
        await ArtifactInventoryNavigation.PrepareAsync(_logger, ct);
        using var reader = new ArtifactInventoryUi(
            _logger,
            ocrSession.RecognizeWithoutDetector,
            ocrSession.RecognizeBatch);
        var observedCount = reader.ReadArtifactCount();

        if (observedCount != expectedArtifactCount)
        {
            var artifacts = await ArtifactInventoryScanSession.ReadItemsAsync(
                reader, observedCount, null, ct);
            var snapshot = ArtifactSnapshotDto.Create(
                uid, Guid.NewGuid().ToString(), "CURRENT_INVENTORY_ORDER", "genshin-7.0",
                artifacts, observedCount);
            Result = new ArtifactExecutionObservationDto(
                uid, observedCount, [], snapshot);
        }
        else
        {
            reader.AllowUnconfirmedCaptureForFingerprintValidation();
            if (targets.Any(target => target.ScanIndex < 0 || target.ScanIndex >= observedCount))
            {
                throw new InvalidDataException(
                    "Artifact execution target index exceeds the current inventory.");
            }
            var targetIndices = targets.Select(target => target.ScanIndex).ToHashSet();
            var artifacts = await ArtifactInventoryScanSession.ReadItemsAsync(
                reader, observedCount, targetIndices, ct);
            var expectedTargets = targets.ToDictionary(target => target.ScanIndex);
            foreach (var artifact in artifacts)
            {
                if (!expectedTargets.TryGetValue(artifact.ScanIndex, out var target)
                    || !string.Equals(
                        artifact.ContentFingerprint,
                        target.ExpectedFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"圣遗物执行前核验在索引 {artifact.ScanIndex} 读到的内容指纹与计划不一致。");
                }
            }
            _logger.LogInformation(
                "圣遗物数量未变化（{ArtifactCount} 件），已逐项核验 {TargetCount} 个执行目标的指纹与锁状态",
                observedCount,
                artifacts.Count);
            Result = new ArtifactExecutionObservationDto(
                uid, observedCount, artifacts, null, CountOnly: false);
        }
        if (Result.CountOnly)
        {
            _logger.LogInformation(
                "圣遗物数量预检完成，保持背包打开并原位交接锁定执行");
        }
        else
        {
            await new ReturnMainUiTask().Start(ct);
        }
    }

}

internal sealed class ArtifactInventoryScanTask(
    string uid,
    CancellationToken externalCancellationToken) : ISoloTask
{
    private readonly ILogger _logger = App.GetLogger<ArtifactInventoryScanTask>();
    public string Name => "圣遗物扫描分析";
    public ArtifactSnapshotDto? Result { get; private set; }

    public async Task Start(CancellationToken taskCancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            taskCancellationToken,
            externalCancellationToken);
        var ct = linked.Token;
        await new ReturnMainUiTask().Start(ct);
        ct.ThrowIfCancellationRequested();
        using var ocrSession = new ArtifactRecognitionOnlyOcrSession(
            forceCpuOcr: ArtifactInventoryUi.ForceCpuOcr,
            parallelRecognizerCount:
                ArtifactRecognitionOnlyOcrSession.InventoryParallelRecognizerCount);
        await ArtifactGameIdentityVerifier.EnsureExpectedUidAsync(
            uid, ocrSession.RecognizeWithoutDetector, ct);
        await ArtifactInventoryNavigation.PrepareAsync(_logger, ct);
        using var reader = new ArtifactInventoryUi(
            _logger,
            ocrSession.RecognizeWithoutDetector,
            ocrSession.RecognizeBatch);
        var expectedCount = reader.ReadArtifactCount();
        var artifacts = await ArtifactInventoryScanSession.ReadItemsAsync(
            reader, expectedCount, null, ct);
        Result = ArtifactSnapshotDto.Create(
            uid, Guid.NewGuid().ToString(), "CURRENT_INVENTORY_ORDER", "genshin-7.0",
            artifacts, expectedCount);
        await new ReturnMainUiTask().Start(ct);
    }
}

internal sealed class ArtifactInventoryUi : IDisposable
{
    internal const bool ForceCpuOcr = false;
    private static readonly Regex CountPattern = new(@"(?<count>\d{1,5})\s*/", RegexOptions.Compiled);
    private readonly ArtifactSetCatalog _setCatalog;
    private readonly ArtifactRecognitionOnlyOcrSession? _ownedFixedRegionOcrSession;
    private readonly Func<Mat, string> _fixedRegionRecognizer;
    private readonly Func<Mat[], string[]> _fixedRegionBatchRecognizer;
    private readonly IOcrService? _injectedLegacyOcrService;
    private readonly ArtifactRetryableLazy<ArtifactPaddleOcrSession>
        _ownedLegacyOcrSession = new();
    private readonly ILogger _logger;
    private bool _allowUnconfirmedCapture;
    private ArtifactPanelSignature? _lastDetailSignature;

    internal ArtifactInventoryUi(ILogger logger)
        : this(logger, fixedRegionRecognizer: null, fixedRegionBatchRecognizer: null)
    {
    }

    internal ArtifactInventoryUi(ILogger logger, IOcrService ocrService)
        : this(
            logger,
            ocrService.OcrWithoutDetector,
            regions => regions.Select(ocrService.OcrWithoutDetector).ToArray())
    {
        _injectedLegacyOcrService = ocrService;
    }

    internal ArtifactInventoryUi(
        ILogger logger,
        Func<Mat, string>? fixedRegionRecognizer)
        : this(logger, fixedRegionRecognizer, fixedRegionBatchRecognizer: null)
    {
    }

    internal ArtifactInventoryUi(
        ILogger logger,
        Func<Mat, string>? fixedRegionRecognizer,
        Func<Mat[], string[]>? fixedRegionBatchRecognizer,
        bool allowUnconfirmedCapture = false)
    {
        _logger = logger;
        _allowUnconfirmedCapture = allowUnconfirmedCapture;
        var cultureName = TaskContext.Instance().Config.OtherConfig.GameCultureInfoName;
        if (!string.Equals(cultureName, "zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Artifact analysis currently requires the Simplified Chinese game UI.");
        }
        _setCatalog = new ArtifactSetCatalog(Path.Combine(
            AppContext.BaseDirectory, "GameTask", "ArtifactAnalysis", "Assets", "artifact-sets.zh.json"));
        if (fixedRegionRecognizer is null)
        {
            _ownedFixedRegionOcrSession = new ArtifactRecognitionOnlyOcrSession(
                forceCpuOcr: ForceCpuOcr);
            _fixedRegionRecognizer =
                _ownedFixedRegionOcrSession.RecognizeWithoutDetector;
            _fixedRegionBatchRecognizer =
                _ownedFixedRegionOcrSession.RecognizeBatch;
        }
        else
        {
            _fixedRegionRecognizer = fixedRegionRecognizer;
            _fixedRegionBatchRecognizer = fixedRegionBatchRecognizer
                ?? (regions => regions
                    .Select(_fixedRegionRecognizer)
                    .ToArray());
        }
    }

    internal static PaddleOcrService.PaddleOcrModelType SelectOcrModel(
        CultureInfo cultureInfo)
    {
        if (!string.Equals(
                cultureInfo.Name,
                "zh-Hans",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Artifact analysis currently requires the Simplified Chinese game UI.");
        }
        return PaddleOcrService.PaddleOcrModelType.V6;
    }

    internal int ReadArtifactCount()
    {
        using var capture = CaptureToRectArea();
        using var region = capture.DeriveCrop(
            ArtifactUiCoordinateMapper.ToCaptureRect(
                capture.SrcMat.Size(),
                1314.88, 27.09, 189.92, 25.83));
        var text = _fixedRegionRecognizer(region.SrcMat);
        var match = CountPattern.Match(text);
        if (!match.Success || !int.TryParse(match.Groups["count"].Value, out var count) || count < 0)
        {
            throw new InvalidDataException($"Unable to read artifact inventory count from '{text}'.");
        }
        return count;
    }

    internal async ValueTask<ArtifactCapturedItem> CaptureItemAsync(
        ImageRegion page,
        Rect itemRect,
        int scanIndex,
        CancellationToken ct)
    {
        return await SelectAndCaptureScanItemAsync(
            page, itemRect, scanIndex, ct);
    }

    internal async Task<ArtifactSelectedLockItem> SelectItemAsync(
        ImageRegion page,
        Rect itemRect,
        int scanIndex,
        CancellationToken cancellationToken)
    {
        var selected = await SelectAndCaptureLockItemAsync(
            page, itemRect, scanIndex, cancellationToken);
        using var capture = selected.Capture;
        return new ArtifactSelectedLockItem(
            selected.Locked,
            ComputeDetailSignature(capture));
    }

    internal async Task<ArtifactItemDto> ReadItemAsync(
        ImageRegion page,
        Rect itemRect,
        int scanIndex,
        CancellationToken cancellationToken)
    {
        using var frame = await CaptureItemAsync(
            page, itemRect, scanIndex, cancellationToken);
        return await ParseItemAsync(frame, cancellationToken);
    }

    internal Task<ArtifactItemDto> ParseItemAsync(
        ArtifactCapturedItem frame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var rarity = frame.Rarity;
        ArtifactItemDto item;
        try
        {
            item = ParseFastItem(frame, rarity);
            item = item with { LocalDetailSignature = frame.DetailSignature };
            _logger.LogDebug(
                "圣遗物 {ScanIndex} 固定区域 OCR 耗时 {ElapsedMilliseconds}ms",
                frame.ScanIndex,
                timer.ElapsedMilliseconds);
            return Task.FromResult(item);
        }
        catch (ArtifactEnhancementMaterialException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidDataException)
        {
            _logger.LogWarning(
                "圣遗物 {ScanIndex} 固定区域 OCR 失败，回退 Paddle：{Message}",
                frame.ScanIndex,
                exception.Message);
        }

        item = ParseLegacyItem(frame, rarity) with
        {
            LocalDetailSignature = frame.DetailSignature
        };
        _logger.LogDebug(
            "圣遗物 {ScanIndex} Paddle 回退解析耗时 {ElapsedMilliseconds}ms",
            frame.ScanIndex,
            timer.ElapsedMilliseconds);
        return Task.FromResult(item);
    }

    private ArtifactItemDto ParseFastItem(ArtifactCapturedItem frame, int rarity)
    {
        var fixedTexts = InferFixedBatch(frame,
        [
            (1110, 153, 190, 40),
            (1110, 224.3, 143.9, 24),
            (1110, 248.4, 136.8, 38.4),
            (1117, 360, 70, 22),
            (1130.2, 398.1, 230.0, 29.2),
            (1130.2, 427.3, 230.0, 30.9),
            (1130.2, 458.2, 230.0, 32.7),
            (1130.2, 490.9, 360.0, 32.1),
            (1110, 465, 280, 32),
            (1110, 500, 280, 32),
            (1110, 530, 280, 32),
            (1154.9, 762.6, 243.5, 25.2)
        ]);
        var slotText = fixedTexts[0];
        if (ArtifactFastTextParser.IsEnhancementMaterial(slotText))
        {
            throw new ArtifactEnhancementMaterialException(slotText);
        }
        var slot = ArtifactFastTextParser.ParseSlot(slotText);
        var mainStat = ArtifactFastTextParser.ParseAffix(
            fixedTexts[1],
            fixedTexts[2]);
        var level = ArtifactFastTextParser.ParseLevel(fixedTexts[3]);
        var substats = fixedTexts[4..8]
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => ArtifactFastTextParser.TryParseAffixLine(
                text, out var affix) ? affix : null)
            .OfType<ArtifactSubstatDto>()
            .ToArray();
        var requiredSubstats = level >= 4 ? 4 : rarity >= 5 ? 3 : 2;
        if (substats.Length < requiredSubstats)
        {
            throw new InvalidDataException(
                $"固定区域只识别到 {substats.Length}/{requiredSubstats} 条副词条");
        }

        var setText = fixedTexts[Math.Max(substats.Length, requiredSubstats) switch
        {
            >= 4 => 10,
            3 => 9,
            _ => 8
        }];
        var equipText = fixedTexts[11];

        return new ArtifactItemDto(
            frame.ScanIndex,
            _setCatalog.ResolveSetKey(setText),
            slot,
            level,
            rarity,
            mainStat.Key,
            substats,
            ResolveLocation(equipText),
            frame.Locked);
    }

    private ArtifactItemDto ParseLegacyItem(
        ArtifactCapturedItem frame,
        int rarity)
    {
        var ocrService = LegacyOcrService;
        using var card = frame.CropBaseRect(
            LegacyDetectionRegion.X,
            LegacyDetectionRegion.Y,
            LegacyDetectionRegion.Width,
            LegacyDetectionRegion.Height);
        var detected = ocrService.OcrResult(card);
        var slot = ArtifactFastTextParser.ParseSlot(
            ReadDetectedBand(detected, 0, 55));
        var mainStat = ArtifactFastTextParser.ParseAffix(
            ReadDetectedBand(detected, 55, 48),
            ReadDetectedBand(detected, 90, 62));
        var level = ArtifactFastTextParser.ParseLevel(
            ReadDetectedBand(detected, 190, 48));
        var substats = new[]
            {
                (245.1, 29.2),
                (274.3, 30.9),
                (305.2, 32.7),
                (337.9, 32.1)
            }
            .Select(band => ReadDetectedBand(
                detected, band.Item1, band.Item2))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => ArtifactFastTextParser.TryParseAffixLine(
                text, out var affix) ? affix : null)
            .OfType<ArtifactSubstatDto>()
            .ToArray();
        var requiredSubstats = level >= 4 ? 4 : rarity >= 5 ? 3 : 2;
        if (substats.Length < requiredSubstats)
        {
            throw new InvalidDataException(
                $"Paddle 回退只识别到 {substats.Length}/{requiredSubstats} 条副词条");
        }
        var setNameBand = LegacyDetectedSetNameBand(
            Math.Max(substats.Length, requiredSubstats));
        var setText = ReadDetectedBand(
            detected, setNameBand.Top, setNameBand.Height);
        using var equipRegion = frame.CropBaseRect(1150, 745, 320, 50);
        var equipText = ocrService.OcrWithoutDetector(equipRegion).Trim();

        return new ArtifactItemDto(
            frame.ScanIndex,
            _setCatalog.ResolveSetKey(setText),
            slot,
            level,
            rarity,
            mainStat.Key,
            substats,
            ResolveLocation(equipText),
            frame.Locked);
    }

    internal static string ReadDetectedBand(
        OcrResult result,
        double top,
        double height)
    {
        var bottom = top + height;
        return string.Concat(result.Regions
            .Where(region => region.Score > 0.5
                && region.Rect.Center.Y >= top
                && region.Rect.Center.Y < bottom)
            .OrderBy(region => region.Rect.Center.X)
            .Select(region => region.Text));
    }

    private static int ReadRarity(Mat capture)
    {
        return ArtifactRarityDetector.Detect(capture);
    }

    private async Task<ArtifactCapturedItem> SelectAndCaptureScanItemAsync(
        ImageRegion page,
        Rect itemRect,
        int scanIndex,
        CancellationToken cancellationToken)
    {
        using var item = page.DeriveCrop(itemRect);
        var gridLocked = ArtifactGridLockDetector.IsLocked(item.SrcMat);
        var baselineSelectionScore = ArtifactGridSelectionDetector.Score(item.SrcMat);
        var liveItemRect = ToLiveItemRect(page, itemRect);
        EnsureInitialDetailSignature();
        item.Click();
        var captured = await CaptureAfterDetailConfirmedAsync(
            _lastDetailSignature.Value,
            baselineSelectionScore,
            liveItemRect,
            scanIndex,
            "扫描",
            capture =>
            {
                var signature = ComputeDetailSignature(capture);
                var detailLocked = ArtifactDetailLockDetector.IsLocked(capture);
                var frame = ArtifactCapturedItem.Create(
                    scanIndex,
                    gridLocked,
                    capture,
                    ReadRarity(capture));
                return (signature, detailLocked, frame);
            },
            cancellationToken);
        _lastDetailSignature = captured.signature;
        LogLockSignalMismatch(scanIndex, gridLocked, captured.detailLocked);
        return captured.frame;
    }

    private async Task<(bool Locked, Mat Capture)> SelectAndCaptureLockItemAsync(
        ImageRegion page,
        Rect itemRect,
        int scanIndex,
        CancellationToken cancellationToken)
    {
        using var item = page.DeriveCrop(itemRect);
        var baselineSelectionScore = ArtifactGridSelectionDetector.Score(item.SrcMat);
        var liveItemRect = ToLiveItemRect(page, itemRect);
        EnsureInitialDetailSignature();
        item.Click();
        var capture = await CaptureAfterDetailConfirmedAsync(
            _lastDetailSignature.Value,
            baselineSelectionScore,
            liveItemRect,
            scanIndex,
            "锁定核验",
            static capture => capture.Clone(),
            cancellationToken);
        _lastDetailSignature = ComputeDetailSignature(capture);
        var detailLocked = ArtifactDetailLockDetector.IsLocked(capture);
        var gridLocked = ReadSelectedGridLockState(page, itemRect);
        LogLockSignalMismatch(scanIndex, gridLocked, detailLocked);
        return (gridLocked, capture);
    }

    private void EnsureInitialDetailSignature()
    {
        if (_lastDetailSignature is not null) return;

        using var initialCapture = CaptureToRectArea();
        _lastDetailSignature = ComputeDetailSignature(initialCapture.SrcMat);
    }

    private void LogLockSignalMismatch(
        int scanIndex,
        bool gridLocked,
        bool detailLocked)
    {
        if (detailLocked != gridLocked)
        {
            _logger.LogWarning(
                "圣遗物 {ScanIndex} 锁状态信号不一致：网格标记={GridLocked}，详情按钮={DetailLocked}；按 YAS 网格标记处理",
                scanIndex,
                gridLocked,
                detailLocked);
        }
    }

    private static bool ReadSelectedGridLockState(
        ImageRegion page,
        Rect itemRect)
    {
        using var liveCapture = CaptureToRectArea();
        using var cell = liveCapture.DeriveCrop(new Rect(
            page.X + itemRect.X,
            page.Y + itemRect.Y,
            itemRect.Width,
            itemRect.Height));
        return ArtifactGridLockDetector.IsLocked(cell.SrcMat);
    }

    private static Rect ToLiveItemRect(ImageRegion page, Rect itemRect) =>
        new(
            page.X + itemRect.X,
            page.Y + itemRect.Y,
            itemRect.Width,
            itemRect.Height);

    private string InferFixed(
        ArtifactCapturedItem frame,
        double left,
        double top,
        double width,
        double height)
    {
        using var region = frame.CropBaseRect(left, top, width, height);
        return _fixedRegionRecognizer(region).Trim();
    }

    private string[] InferFixedBatch(
        ArtifactCapturedItem frame,
        IReadOnlyList<(double Left, double Top, double Width, double Height)> rects)
    {
        var regions = rects
            .Select(rect => frame.CropBaseRect(
                rect.Left,
                rect.Top,
                rect.Width,
                rect.Height))
            .ToArray();
        try
        {
            var results = _fixedRegionBatchRecognizer(regions);
            if (results.Length != regions.Length)
            {
                throw new InvalidDataException(
                    $"固定区域批量 OCR 返回 {results.Length}/{regions.Length} 条结果");
            }
            return results.Select(text => text.Trim()).ToArray();
        }
        finally
        {
            foreach (var region in regions) region.Dispose();
        }
    }

    internal void AllowUnconfirmedCaptureForFingerprintValidation() =>
        _allowUnconfirmedCapture = true;

    private IOcrService LegacyOcrService
    {
        get
        {
            if (_injectedLegacyOcrService is not null)
            {
                return _injectedLegacyOcrService;
            }

            return _ownedLegacyOcrSession.GetOrCreate(() =>
            {
                _logger.LogInformation(
                    "圣遗物固定区域解析失败，按需加载 PpOcrDetV6 + PpOcrRecV6 回退（排除 TensorRT）");
                return new ArtifactPaddleOcrSession(
                    forceCpuOcr: ForceCpuOcr);
            }).Service;
        }
    }

    internal static double SetNameTop(int substatCount)
    {
        return substatCount switch
        {
            >= 4 => 530,
            3 => 500,
            _ => 465
        };
    }

    internal static (double Top, double Height) LegacySetNameRegion(int substatCount) =>
        (SetNameTop(substatCount) - 5, 37);

    internal static Rect LegacyDetectionRegion { get; } =
        new(1090, 153, 410, 409);

    internal static (double Top, double Height) LegacyDetectedSetNameBand(
        int substatCount)
    {
        var absolute = LegacySetNameRegion(substatCount);
        return (absolute.Top - LegacyDetectionRegion.Y, absolute.Height);
    }

    private async Task<T> CaptureAfterDetailConfirmedAsync<T>(
        ArtifactPanelSignature initialSignature,
        double baselineSelectionScore,
        Rect liveItemRect,
        int scanIndex,
        string operation,
        Func<Mat, T> projector,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var detector = new ArtifactDetailSwitchDetector(
            initialSignature, maximumStableDistance: 4, lockTolerance: 0.5);
        var sameDetailDetector = new ArtifactSameDetailSelectionDetector(
            initialSignature,
            baselineSelectionScore,
            minimumSelectionIncrease: 0.08);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var capture = CaptureToRectArea();
            var detailSignature = ComputeDetailSignature(capture.SrcMat);
            var changedAndStable = detector.Observe(
                detailSignature,
                ArtifactDetailLockDetector.VisualSignature(capture.SrcMat));
            var sameDetailButSelected = false;
            if (!changedAndStable)
            {
                using var liveItem = capture.DeriveCrop(liveItemRect);
                sameDetailButSelected = sameDetailDetector.Observe(
                    detailSignature,
                    ArtifactGridSelectionDetector.Score(liveItem.SrcMat));
            }
            var confirmed = changedAndStable || sameDetailButSelected;
            var decision = ArtifactDetailCapturePolicy.Decide(
                scanIndex,
                timer.ElapsedMilliseconds,
                confirmed);
            if (decision == ArtifactDetailCaptureDecision.Confirmed)
            {
                var evidence = changedAndStable
                    ? "详情已切换"
                    : "目标格已选中且详情相同";
                _logger.LogDebug(
                    "圣遗物 {ScanIndex} {Operation}详情确认等待 {ElapsedMilliseconds}ms，依据={Evidence}",
                    scanIndex,
                    operation,
                    timer.ElapsedMilliseconds,
                    evidence);
                return projector(capture.SrcMat);
            }
            if (decision == ArtifactDetailCaptureDecision.TimedOut)
            {
                if (_allowUnconfirmedCapture)
                {
                    _logger.LogWarning(
                        "圣遗物 {ScanIndex} {Operation}在 {ElapsedMilliseconds}ms 内未取得选中证明；仅供随后精确指纹核验，不得直接执行写操作",
                        scanIndex,
                        operation,
                        timer.ElapsedMilliseconds);
                    return projector(capture.SrcMat);
                }
                throw new InvalidDataException(
                    $"圣遗物 {scanIndex} {operation}在 {timer.ElapsedMilliseconds}ms 内未证明目标格已选中，拒绝解析可能的旧详情帧。");
            }
            await Delay(16, cancellationToken);
        }
    }

    internal static ArtifactPanelSignature ComputeDetailSignature(Mat capture) =>
        new(
            ArtifactVisualSignature.Compute(
                capture, 1090, 100, 410, 230),
            ArtifactVisualSignature.Compute(
                capture, 1090, 220, 310, 300));

    public void Dispose()
    {
        try
        {
            _ownedLegacyOcrSession.Take()?.Dispose();
        }
        finally
        {
            _ownedFixedRegionOcrSession?.Dispose();
        }
    }

    private static string ResolveLocation(string equipText)
    {
        const string suffix = "已装备";
        return equipText.EndsWith(suffix, StringComparison.Ordinal)
            ? equipText[..^suffix.Length].Trim()
            : string.Empty;
    }

}

internal readonly record struct ArtifactSelectedLockItem(
    bool Locked,
    ArtifactPanelSignature DetailSignature);

internal sealed class ArtifactCapturedItem : IDisposable
{
    private static readonly Size OcrReferenceSize = ArtifactUiCoordinateMapper.LogicalSize;
    private const double CoreLeft = 1090;
    private const double CoreTop = 100;
    private const double CoreWidth = 410;
    private const double CoreHeight = 462;
    private const double EquipmentLeft = 1140;
    private const double EquipmentTop = 740;
    private const double EquipmentWidth = 340;
    private const double EquipmentHeight = 70;

    private readonly Rect _coreBounds;
    private readonly Rect _equipmentBounds;

    private ArtifactCapturedItem(
        int scanIndex,
        bool locked,
        int rarity,
        ArtifactPanelSignature detailSignature,
        Size sourceSize,
        Rect coreBounds,
        Mat coreCapture,
        Rect equipmentBounds,
        Mat equipmentCapture)
    {
        ScanIndex = scanIndex;
        Locked = locked;
        Rarity = rarity;
        DetailSignature = detailSignature;
        SourceSize = sourceSize;
        _coreBounds = coreBounds;
        CoreCapture = coreCapture;
        _equipmentBounds = equipmentBounds;
        EquipmentCapture = equipmentCapture;
    }

    internal int ScanIndex { get; }
    internal bool Locked { get; }
    internal int Rarity { get; }
    internal ArtifactPanelSignature DetailSignature { get; }
    internal Size SourceSize { get; }
    internal Mat CoreCapture { get; }
    internal Mat EquipmentCapture { get; }

    internal static ArtifactCapturedItem Create(
        int scanIndex,
        bool locked,
        Mat fullCapture,
        int rarity = 5)
    {
        var sourceSize = fullCapture.Size();
        var sourceCoreBounds = ScaleBaseRect(
            sourceSize, CoreLeft, CoreTop, CoreWidth, CoreHeight);
        var sourceEquipmentBounds = ScaleBaseRect(
            sourceSize,
            EquipmentLeft,
            EquipmentTop,
            EquipmentWidth,
            EquipmentHeight);
        var coreBounds = ScaleBaseRect(
            OcrReferenceSize, CoreLeft, CoreTop, CoreWidth, CoreHeight);
        var equipmentBounds = ScaleBaseRect(
            OcrReferenceSize,
            EquipmentLeft,
            EquipmentTop,
            EquipmentWidth,
            EquipmentHeight);
        using var core = fullCapture.SubMat(sourceCoreBounds);
        using var equipment = fullCapture.SubMat(sourceEquipmentBounds);
        return new ArtifactCapturedItem(
            scanIndex,
            locked,
            rarity,
            ArtifactInventoryUi.ComputeDetailSignature(fullCapture),
            sourceSize,
            coreBounds,
            NormalizeCapture(core, coreBounds.Size),
            equipmentBounds,
            NormalizeCapture(equipment, equipmentBounds.Size));
    }

    internal Mat CropBaseRect(
        double left,
        double top,
        double width,
        double height)
    {
        return CropSourceRect(ScaleBaseRect(
            OcrReferenceSize,
            left,
            top,
            width,
            height));
    }

    internal Mat CreateOcrDebugCapture()
    {
        return CoreCapture.Clone();
    }

    internal Mat CropSourceRect(Rect sourceRect)
    {
        var capture = SelectCapture(sourceRect, out var captureBounds);
        var local = new Rect(
            sourceRect.X - captureBounds.X,
            sourceRect.Y - captureBounds.Y,
            sourceRect.Width,
            sourceRect.Height);
        return capture.SubMat(local);
    }

    private Mat SelectCapture(Rect sourceRect, out Rect captureBounds)
    {
        if (Contains(_coreBounds, sourceRect))
        {
            captureBounds = _coreBounds;
            return CoreCapture;
        }
        if (Contains(_equipmentBounds, sourceRect))
        {
            captureBounds = _equipmentBounds;
            return EquipmentCapture;
        }
        throw new InvalidDataException(
            $"OCR 区域 {sourceRect} 不在圣遗物核心面板或装备信息区域内");
    }

    private static bool Contains(Rect bounds, Rect inner)
    {
        return inner.X >= bounds.X && inner.Y >= bounds.Y &&
               inner.Right <= bounds.Right && inner.Bottom <= bounds.Bottom;
    }

    private static Mat NormalizeCapture(Mat source, Size targetSize)
    {
        if (source.Size() == targetSize) return source.Clone();

        var normalized = new Mat();
        Cv2.Resize(source, normalized, targetSize, 0, 0, InterpolationFlags.Area);
        return normalized;
    }

    private static Rect ScaleBaseRect(
        Size sourceSize,
        double left,
        double top,
        double width,
        double height)
    {
        return ArtifactUiCoordinateMapper.ToCaptureRect(
            sourceSize, left, top, width, height);
    }

    public void Dispose()
    {
        CoreCapture.Dispose();
        EquipmentCapture.Dispose();
    }
}

internal static class ArtifactInventoryScanSession
{
    internal static async Task<IReadOnlyList<ArtifactItemDto>> ReadItemsAsync(
        ArtifactInventoryUi reader,
        int artifactCount,
        HashSet<int>? targetIndices,
        CancellationToken cancellationToken)
    {
        if (artifactCount == 0 || targetIndices is { Count: 0 }) return [];

        var logger = App.GetLogger<ArtifactInventoryScanTask>();
        var timer = Stopwatch.StartNew();
        using var gridCapture = CaptureToRectArea();
        var grid = new GridScreen(
            GridParams.ArtifactsForCapture(gridCapture.SrcMat.Size(), artifactCount),
            logger,
            cancellationToken);
        var index = 0;
        var capturedTargets = 0;
        var debugCollector = ArtifactOcrDebugCollector.TryCreate();

        async IAsyncEnumerable<ArtifactCapturedItem> CaptureFrames(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach ((ImageRegion page, Rect rect) in grid.WithCancellation(ct))
            {
                if (index >= artifactCount ||
                    (targetIndices is not null && capturedTargets >= targetIndices.Count)) yield break;

                if (targetIndices is null || targetIndices.Contains(index))
                {
                    var frame = await reader.CaptureItemAsync(
                        page, rect, index, ct);
                    capturedTargets++;
                    yield return frame;
                }
                index++;
            }
        }

        var outcomes = await ArtifactCaptureParsePipeline.RunAsync(
            CaptureFrames(cancellationToken),
            (frame, ct) => ArtifactOcrDebugMode.ParseAsync(
                debugCollector is not null,
                () => reader.ParseItemAsync(frame, ct),
                exception => debugCollector!.RecordAsync(frame, exception, ct)),
            capacity: 3,
            cancellationToken);

        var expected = targetIndices?.Count ?? artifactCount;
        if (outcomes.Count != expected)
        {
            throw new InvalidDataException(
                $"Artifact inventory scan stopped at {outcomes.Count} of {expected} items.");
        }

        var artifacts = outcomes
            .Where(outcome => outcome.Value is not null)
            .Select(outcome => outcome.Value!)
            .ToArray();
        var failures = outcomes.Count(outcome => outcome.Error is not null);
        var skippedEnhancementMaterials = outcomes.Count(outcome =>
            outcome.Value is null && outcome.Error is null);
        if (skippedEnhancementMaterials > 0)
        {
            logger.LogInformation(
                "已识别并跳过 {Count} 件圣遗物强化素材，库存总数保持 {ArtifactCount}",
                skippedEnhancementMaterials,
                artifactCount);
        }

        var rows = Math.Max(1, Math.Ceiling(outcomes.Count / 8.0));
        logger.LogInformation(
            "圣遗物扫描完成 {ArtifactCount} 件，成功识别 {RecognizedCount} 件，OCR 失败 {FailureCount} 件，总耗时 {ElapsedSeconds:F2}s，折算每行 {SecondsPerRow:F2}s",
            outcomes.Count,
            artifacts.Length,
            failures,
            timer.Elapsed.TotalSeconds,
            timer.Elapsed.TotalSeconds / rows);
        var debugReport = debugCollector?.Complete(artifactCount);
        if (failures > 0)
        {
            throw new InvalidDataException(
                $"OCR 调试全量扫描完成，共 {outcomes.Count} 件、{failures} 个识别错误；报告：{debugReport}");
        }
        return artifacts;
    }
}
