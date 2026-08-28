using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.AutoArtifactSalvage;
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
        await ArtifactGameIdentityVerifier.EnsureExpectedUidAsync(uid, ct);
        await ArtifactInventoryNavigation.PrepareAsync(_logger, ct);
        using var reader = new ArtifactInventoryUi(_logger);
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
            if (targets.Any(target => target.ScanIndex < 0 || target.ScanIndex >= observedCount))
            {
                throw new InvalidDataException(
                    "Artifact execution target index exceeds the current inventory.");
            }
            var targetIndices = targets.Select(target => target.ScanIndex).ToHashSet();
            var artifacts = await ArtifactInventoryScanSession.ReadItemsAsync(
                reader, observedCount, targetIndices, ct);
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
        await ArtifactGameIdentityVerifier.EnsureExpectedUidAsync(uid, ct);
        await ArtifactInventoryNavigation.PrepareAsync(_logger, ct);
        using var reader = new ArtifactInventoryUi(_logger);
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
    internal const bool ForceCpuOcr = true;
    private static readonly Regex CountPattern = new(@"(?<count>\d{1,5})\s*/", RegexOptions.Compiled);
    private readonly AutoArtifactSalvageTask _artifactParser;
    private readonly ArtifactSetCatalog _setCatalog;
    private readonly BgiOnnxFactory _ocrFactory;
    private readonly PaddleOcrService _ocrService;
    private readonly ILogger _logger;
    private double? _lastDetailSignature;

    internal ArtifactInventoryUi(ILogger logger)
    {
        _logger = logger;
        var cultureName = TaskContext.Instance().Config.OtherConfig.GameCultureInfoName;
        if (!string.Equals(cultureName, "zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Artifact analysis currently requires the Simplified Chinese game UI.");
        }
        _artifactParser = new AutoArtifactSalvageTask(
            new AutoArtifactSalvageTaskParam(5, null, null, null, null, new CultureInfo(cultureName)), logger);
        _ocrFactory = new BgiOnnxFactory(
            App.GetLogger<BgiOnnxFactory>(),
            forceCpuOcr: ForceCpuOcr,
            excludeTensorRtForOcr: true);
        _ocrService = new PaddleOcrService(
            _ocrFactory,
            SelectOcrModel(new CultureInfo(cultureName)));
        _setCatalog = new ArtifactSetCatalog(Path.Combine(
            AppContext.BaseDirectory, "GameTask", "ArtifactAnalysis", "Assets", "artifact-sets.zh.json"));
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
        var text = _ocrService.OcrWithoutDetector(region.SrcMat);
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
        var selected = await SelectAndCaptureScanItemAsync(page, itemRect, scanIndex, ct);
        using var capture = selected.Capture;
        return ArtifactCapturedItem.Create(
            scanIndex,
            selected.Locked,
            capture,
            ReadRarity(capture));
    }

    internal async Task<bool> SelectItemAsync(
        ImageRegion page,
        Rect itemRect,
        int scanIndex,
        CancellationToken cancellationToken)
    {
        var selected = await SelectAndCaptureLockItemAsync(
            page, itemRect, scanIndex, cancellationToken);
        selected.Capture.Dispose();
        return selected.Locked;
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

    internal async Task<ArtifactItemDto> ReadInitiallySelectedItemAsync(
        ImageRegion page,
        Rect itemRect,
        int scanIndex,
        CancellationToken cancellationToken)
    {
        using var frame = CaptureInitiallySelectedItem(
            page, itemRect, scanIndex, cancellationToken);
        return await ParseItemAsync(frame, cancellationToken);
    }

    internal ArtifactCapturedItem CaptureInitiallySelectedItem(
        ImageRegion page,
        Rect itemRect,
        int scanIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var capture = CaptureToRectArea();
        using var item = page.DeriveCrop(itemRect);
        return ArtifactCapturedItem.Create(
            scanIndex,
            ArtifactGridLockDetector.IsLocked(item.SrcMat),
            capture.SrcMat,
            ReadRarity(capture.SrcMat));
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
        catch (Exception exception)
        {
            _logger.LogWarning(
                "圣遗物 {ScanIndex} 固定区域 OCR 失败，回退 Paddle：{Message}",
                frame.ScanIndex,
                exception.Message);
        }

        item = ParseLegacyItem(frame, rarity);
        _logger.LogDebug(
            "圣遗物 {ScanIndex} Paddle 回退解析耗时 {ElapsedMilliseconds}ms",
            frame.ScanIndex,
            timer.ElapsedMilliseconds);
        return Task.FromResult(item);
    }

    private ArtifactItemDto ParseFastItem(ArtifactCapturedItem frame, int rarity)
    {
        var slotText = InferFixed(frame, 1110, 153, 190, 40);
        if (ArtifactFastTextParser.IsEnhancementMaterial(slotText))
        {
            throw new ArtifactEnhancementMaterialException(slotText);
        }
        var slot = ArtifactFastTextParser.ParseSlot(slotText);
        var mainStat = ArtifactFastTextParser.ParseAffix(
            InferFixed(frame, 1110, 224.3, 143.9, 24),
            InferFixed(frame, 1110, 248.4, 136.8, 38.4));
        var level = ArtifactFastTextParser.ParseLevel(
            InferFixed(frame, 1117, 360, 70, 22));
        var substats = new[]
            {
                (1130.2, 398.1, 230.0, 29.2),
                (1130.2, 427.3, 230.0, 30.9),
                (1130.2, 458.2, 230.0, 32.7),
                (1130.2, 490.9, 360.0, 32.1)
            }
            .Select(rect => InferFixed(frame, rect.Item1, rect.Item2, rect.Item3, rect.Item4))
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

        var setText = InferFixed(
            frame, 1110, SetNameTop(Math.Max(substats.Length, requiredSubstats)), 280, 32);
        var equipText = InferFixed(frame, 1154.9, 762.6, 243.5, 25.2);

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
        using var card = frame.CropBaseRect(1120, 100.8, 380, 450);
        var stat = _artifactParser.GetArtifactStat(card, _ocrService, out _);
        var legacySetNameRegion = LegacySetNameRegion(stat.MinorAffixes.Count());
        using var setRegion = frame.CropBaseRect(
            1090,
            legacySetNameRegion.Top,
            340,
            legacySetNameRegion.Height);
        var setText = _ocrService.OcrResult(setRegion).Text;
        using var equipRegion = frame.CropBaseRect(1150, 745, 320, 50);
        var equipText = _ocrService.OcrWithoutDetector(equipRegion).Trim();
        var substats = stat.MinorAffixes.Select(affix =>
            new ArtifactSubstatDto(StatKey(affix.Type), affix.Value)).ToArray();
        if (stat.Level == 0 && substats.Length >= 4)
        {
            try
            {
                var fourthLineText = InferFixed(frame, 1130.2, 490.9, 360.0, 32.1);
                substats = ArtifactFastTextParser.ApplyDormantFourthLine(
                    substats, stat.Level, fourthLineText);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "旧解析回退未能读取待激活第四词条标记");
            }
        }

        return new ArtifactItemDto(
            frame.ScanIndex,
            _setCatalog.ResolveSetKey(setText),
            ResolveSlot(stat.TypeName),
            stat.Level,
            rarity,
            StatKey(stat.MainAffix.Type),
            substats,
            ResolveLocation(equipText),
            frame.Locked);
    }

    private static int ReadRarity(Mat capture)
    {
        return ArtifactRarityDetector.Detect(capture);
    }

    private async Task<(bool Locked, Mat Capture)> SelectAndCaptureScanItemAsync(
        ImageRegion page,
        Rect itemRect,
        int scanIndex,
        CancellationToken cancellationToken)
    {
        using var item = page.DeriveCrop(itemRect);
        var gridLocked = ArtifactGridLockDetector.IsLocked(item.SrcMat);
        EnsureInitialDetailSignature();
        item.Click();
        var capture = await CaptureAfterScanDetailChangeAsync(
            _lastDetailSignature.Value, scanIndex, cancellationToken);
        _lastDetailSignature = DetailSignature(capture);
        var detailLocked = ArtifactDetailLockDetector.IsLocked(capture);
        LogLockSignalMismatch(scanIndex, gridLocked, detailLocked);
        return (gridLocked, capture);
    }

    private async Task<(bool Locked, Mat Capture)> SelectAndCaptureLockItemAsync(
        ImageRegion page,
        Rect itemRect,
        int scanIndex,
        CancellationToken cancellationToken)
    {
        using var item = page.DeriveCrop(itemRect);
        EnsureInitialDetailSignature();
        item.Click();
        var capture = await CaptureAfterLockDetailStableAsync(
            _lastDetailSignature.Value, scanIndex, cancellationToken);
        _lastDetailSignature = DetailSignature(capture);
        var detailLocked = ArtifactDetailLockDetector.IsLocked(capture);
        var gridLocked = ReadSelectedGridLockState(page, itemRect);
        LogLockSignalMismatch(scanIndex, gridLocked, detailLocked);
        return (gridLocked, capture);
    }

    private void EnsureInitialDetailSignature()
    {
        if (_lastDetailSignature is not null) return;

        using var initialCapture = CaptureToRectArea();
        _lastDetailSignature = DetailSignature(initialCapture.SrcMat);
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

    private string InferFixed(
        ArtifactCapturedItem frame,
        double left,
        double top,
        double width,
        double height)
    {
        using var region = frame.CropBaseRect(left, top, width, height);
        return _ocrService.OcrWithoutDetector(region).Trim();
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

    private async Task<Mat> CaptureAfterScanDetailChangeAsync(
        double initialSignature,
        int scanIndex,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var detector = new ArtifactScanDetailChangeDetector(initialSignature, 0.5);
        while (timer.ElapsedMilliseconds < 450)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var capture = CaptureToRectArea();
            if (detector.Observe(DetailSignature(capture.SrcMat)))
            {
                _logger.LogDebug(
                    "圣遗物 {ScanIndex} 扫描详情切换等待 {ElapsedMilliseconds}ms",
                    scanIndex,
                    timer.ElapsedMilliseconds);
                return capture.SrcMat.Clone();
            }
            await Delay(8, cancellationToken);
        }

        throw new InvalidDataException(
            $"圣遗物 {scanIndex} 扫描详情切换在 450ms 内未发生，拒绝解析旧详情帧。");
    }

    private async Task<Mat> CaptureAfterLockDetailStableAsync(
        double initialSignature,
        int scanIndex,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var detector = new ArtifactDetailSwitchDetector(initialSignature, 0.5);
        while (timer.ElapsedMilliseconds < 450)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var capture = CaptureToRectArea();
            if (detector.Observe(
                    DetailSignature(capture.SrcMat),
                    ArtifactDetailLockDetector.VisualSignature(capture.SrcMat)))
            {
                _logger.LogDebug(
                    "圣遗物 {ScanIndex} 详情切换等待 {ElapsedMilliseconds}ms",
                    scanIndex,
                    timer.ElapsedMilliseconds);
                return capture.SrcMat.Clone();
            }
            await Delay(16, cancellationToken);
        }

        throw new InvalidDataException(
            $"圣遗物 {scanIndex} 详情切换在 450ms 内未稳定，拒绝解析旧详情帧。");
    }

    private static double DetailSignature(Mat capture)
    {
        var rect = ArtifactUiCoordinateMapper.ToCaptureRect(
            capture.Size(),
            1144.64, 166.68, 15.04, 392.13);
        var bounded = AutoArtifactSalvageTask.ClampRectToBounds(
                          rect, capture.Width, capture.Height)
                      ?? throw new InvalidDataException("详情切换检测区域超出截图范围");
        using var region = capture.SubMat(bounded);
        return Cv2.Sum(region).Val0;
    }

    public void Dispose()
    {
        _ocrService.Dispose();
        _ocrFactory.Dispose();
    }

    private static string ResolveSlot(string typeName) => typeName switch
    {
        var value when value.Contains("生之花", StringComparison.Ordinal) => "flower",
        var value when value.Contains("死之羽", StringComparison.Ordinal) => "plume",
        var value when value.Contains("时之沙", StringComparison.Ordinal) => "sands",
        var value when value.Contains("空之杯", StringComparison.Ordinal) => "goblet",
        var value when value.Contains("理之冠", StringComparison.Ordinal) => "circlet",
        _ => throw new InvalidDataException($"Unable to resolve artifact slot from '{typeName}'.")
    };

    private static string ResolveLocation(string equipText)
    {
        const string suffix = "已装备";
        return equipText.EndsWith(suffix, StringComparison.Ordinal)
            ? equipText[..^suffix.Length].Trim()
            : string.Empty;
    }

    private static string StatKey(ArtifactAffixType type) => type switch
    {
        ArtifactAffixType.HP => "hp", ArtifactAffixType.ATK => "atk", ArtifactAffixType.DEF => "def",
        ArtifactAffixType.HPPercent => "hp_", ArtifactAffixType.ATKPercent => "atk_",
        ArtifactAffixType.DEFPercent => "def_", ArtifactAffixType.ElementalMastery => "eleMas",
        ArtifactAffixType.EnergyRecharge => "enerRech_", ArtifactAffixType.CRITRate => "critRate_",
        ArtifactAffixType.CRITDMG => "critDMG_", ArtifactAffixType.HealingBonus => "heal_",
        ArtifactAffixType.AnemoDMGBonus => "anemo_dmg_", ArtifactAffixType.CryoDMGBonus => "cryo_dmg_",
        ArtifactAffixType.DendroDMGBonus => "dendro_dmg_", ArtifactAffixType.ElectroDMGBonus => "electro_dmg_",
        ArtifactAffixType.GeoDMGBonus => "geo_dmg_", ArtifactAffixType.HydroDMGBonus => "hydro_dmg_",
        ArtifactAffixType.PhysicalDMGBonus => "physical_dmg_", ArtifactAffixType.PyroDMGBonus => "pyro_dmg_",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}

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
        Size sourceSize,
        Rect coreBounds,
        Mat coreCapture,
        Rect equipmentBounds,
        Mat equipmentCapture)
    {
        ScanIndex = scanIndex;
        Locked = locked;
        Rarity = rarity;
        SourceSize = sourceSize;
        _coreBounds = coreBounds;
        CoreCapture = coreCapture;
        _equipmentBounds = equipmentBounds;
        EquipmentCapture = equipmentCapture;
    }

    internal int ScanIndex { get; }
    internal bool Locked { get; }
    internal int Rarity { get; }
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
                    var frame = index == 0
                        ? reader.CaptureInitiallySelectedItem(page, rect, index, ct)
                        : await reader.CaptureItemAsync(page, rect, index, ct);
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
