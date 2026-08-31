using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.CharacterDevelopment;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.GameUI;
using BetterGenshinImpact.Service;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

public sealed class ArtifactCharacterRosterScanner : IArtifactCharacterRosterScanner
{
    private readonly ILogger<ArtifactCharacterRosterScanner> _logger =
        App.GetLogger<ArtifactCharacterRosterScanner>();

    public async Task<ArtifactCharacterRosterDto> ScanAsync(
        string uid,
        string? gameNickname,
        string? miliastraNickname,
        string? miliastraCharacterKey,
        CancellationToken cancellationToken)
    {
        var task = new ArtifactCharacterRosterScanTask(
            ct => ScanCoreAsync(
                uid, gameNickname, miliastraNickname,
                miliastraCharacterKey, ct),
            cancellationToken);
        await new TaskRunner().RunSoloTaskAsync(task, propagateExceptions: true);
        return task.Result ?? throw new InvalidOperationException(
            "角色配装检测结束但没有生成完整名单。");
    }

    private async Task<ArtifactCharacterRosterDto> ScanCoreAsync(
        string uid,
        string? gameNickname,
        string? miliastraNickname,
        string? miliastraCharacterKey,
        CancellationToken cancellationToken)
    {
        await ArtifactHostGameContext.EnsureReadyAsync(
            () => ScriptService.StartGameTask(),
            () => TaskContext.Instance().IsInitialized);
        var systemInfo = TaskContext.Instance().SystemInfo;
        var assetScale = systemInfo.AssetScale;
        var captureRect = systemInfo.ScaleMax1080PCaptureRect;
        var assets = CharacterDevelopmentAssets.Get(captureRect.Width, captureRect.Height);

        await new ReturnMainUiTask().Start(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var detailsReader = new ArtifactCharacterDetailsReader();
        await ArtifactGameIdentityVerifier.EnsureExpectedUidAsync(
            uid, detailsReader.RecognizeWithoutDetector, cancellationToken);
        _logger.LogInformation(
            "角色配装检测 OCR：PpOcrRecV6 固定区域（排除 TensorRT）；不加载 DetV6，第二帧仅在首帧失败时启用");
        try
        {
            Simulation.SendInput.SimulateAction(GIActions.OpenCharacterScreen);
            await OpenCharacterListAsync(assets, cancellationToken);
            var gridParams = GridParams.CharacterDevelopmentForCapture(
                new Size(captureRect.Width, captureRect.Height));
            var characters = new Dictionary<string, ArtifactCharacterRosterEntryDto>(StringComparer.Ordinal);
            var pageTracker = new ArtifactCharacterPageTracker();
            var identityGuard = new ArtifactCharacterScanIdentityGuard();
            var scrollState = new ArtifactCharacterScrollState();
            var clicked = 0;
            while (true)
            {
                var pageRows = await DetectPageRowsAsync(
                    gridParams.Roi, assetScale, cancellationToken);
                var nextStartRow = scrollState.ConsumeNextStartRow();
                var newRows = nextStartRow.HasValue
                    ? ArtifactCharacterPageTracker.SelectFromStartRow(
                        pageRows,
                        nextStartRow.Value)
                    : pageTracker.SelectUnprocessedRows(pageRows);
                if (pageTracker.HasPreviousPage && newRows.Count == 0)
                {
                    _logger.LogInformation("角色配装检测：分页没有继续前进，已到末页");
                    break;
                }
                _logger.LogInformation(
                    "角色配装检测：当前可见 {VisibleRows} 行，本轮只读取 {NewRows} 个未处理行、{Cards} 张卡片",
                    pageRows.Count, newRows.Count, newRows.Sum(row => row.Cards.Count));
                foreach (var rect in newRows.SelectMany(row => row.Cards))
                {
                    ArtifactCharacterDetailSample? detail = null;
                    string? characterKey = null;
                    TimeoutException? firstClickTimeout = null;
                    for (var clickAttempt = 1; clickAttempt <= 2; clickAttempt++)
                    {
                        var clickBaseline = ClickCharacterCard(
                            gridParams.Roi, rect);
                        ArtifactCharacterCapturedDetail captured;
                        try
                        {
                            captured = await CaptureSelectedDetailAsync(
                                clickBaseline,
                                cancellationToken);
                        }
                        catch (TimeoutException exception) when (clickAttempt == 1)
                        {
                            firstClickTimeout = exception;
                            _logger.LogWarning(
                                exception,
                                "角色配装检测：首次点击后详情未确认，补点同一头像一次");
                            continue;
                        }
                        catch (TimeoutException exception)
                        {
                            throw new TimeoutException(
                                "角色配装检测连续两次点击都未能确认右侧详情。",
                                firstClickTimeout is null
                                    ? exception
                                    : new AggregateException(
                                        firstClickTimeout, exception));
                        }
                        using (captured)
                        {
                        detail = await ParseDetailWithOneRetryAsync(
                            detailsReader, captured,
                            gameNickname, miliastraNickname,
                            miliastraCharacterKey,
                            cancellationToken);
                        characterKey = ArtifactCharacterCardReader.ToCharacterKey(
                            detail.CharacterName);
                        }
                        if (!identityGuard.DidNotChange(characterKey)) break;
                        if (clickAttempt == 2)
                        {
                            throw new TimeoutException(
                                $"补点角色卡片后右侧角色仍为 {characterKey}。");
                        }
                        _logger.LogWarning(
                            "角色配装检测：点击后右侧角色仍为 {CharacterKey}，补点同一头像并只重试一次 OCR",
                            characterKey);
                    }

                    if (!identityGuard.TryCommit(characterKey!))
                    {
                        _logger.LogWarning(
                            "角色配装检测：分页重叠再次识别到 {CharacterKey}，已跳过重复项并继续",
                            characterKey);
                        continue;
                    }
                    detail = await ConfirmFavoriteAsync(detail!, cancellationToken);
                    AddCharacter(characters, detail);
                    clicked++;
                }
                using (var bottomCapture = CaptureToRectArea())
                {
                    if (ArtifactCharacterScrollbarDetector.IsAtBottom(
                            bottomCapture.SrcMat,
                            gridParams.Roi))
                    {
                        _logger.LogInformation(
                            "角色配装检测：滚动条滑块已到轨道底部，结束扫描");
                        break;
                    }
                }
                await MovePointerToScrollAreaAsync(
                    gridParams.Roi,
                    cancellationToken);
                var stableRows = await DetectPageRowsAsync(
                    gridParams.Roi, assetScale, cancellationToken);
                if (!scrollState.IsCalibrated)
                {
                    scrollState.PixelsPerInput = await MeasureScrollPixelsPerInputAsync(
                        gridParams.Roi,
                        cancellationToken);
                    scrollState.IsCalibrated = true;
                }
                pageTracker.Commit(stableRows);

                var moved = await ScrollPageAsync(
                    stableRows,
                    gridParams.Roi,
                    assetScale,
                    scrollState,
                    cancellationToken);
                if (!moved) break;
            }

            if (characters.Count == 0)
                throw new InvalidOperationException("未识别到任何游戏角色。");
            _logger.LogInformation(
                "角色配装检测完成：点击 {Captured} 张，识别 {Characters} 个角色",
                clicked, characters.Count);
            return new ArtifactCharacterRosterDto(
                uid,
                characters.Values
                    .OrderBy(character => character.CharacterKey, StringComparer.Ordinal)
                    .ToArray());
        }
        catch (Exception exception)
        {
            TaskFailureDiagnostics.CaptureScreenshotOnce(exception, "角色配装检测-角色详情");
            throw;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                await new ReturnMainUiTask().Start(cancellationToken);
        }
    }

    private async Task OpenCharacterListAsync(
        CharacterDevelopmentAssets assets,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            using var capture = CaptureToRectArea();
            using var filter = capture.Find(assets.FilterRo);
            if (filter.IsExist())
            {
                await Delay(700, cancellationToken);
                return;
            }
            using var menu = capture.Find(assets.MenuRo);
            if (menu.IsExist()) menu.Click();
            await Delay(300, cancellationToken);
        }
        throw new InvalidOperationException("打开角色列表超时。");
    }

    private async Task<IReadOnlyList<ArtifactCharacterPageRow>> DetectPageRowsAsync(
        Rect gridRoi,
        double assetScale,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            using var capture = CaptureToRectArea();
            using var page = capture.DeriveCrop(gridRoi);
            var rows = ArtifactCharacterPageDetector.Detect(page.SrcMat, assetScale);
            if (rows.Count > 0) return rows;
            if (attempt < 8) await Delay(120, cancellationToken);
        }
        throw new InvalidOperationException("当前角色页没有检测到可点击卡片。");
    }

    private async Task<double> MeasureScrollPixelsPerInputAsync(
        Rect gridRoi,
        CancellationToken cancellationToken)
    {
        var rulerRects = ArtifactCharacterScrollPlanner.RulerRects(gridRoi);
        using var baselineCapture = CaptureToRectArea();
        var baselines = rulerRects
            .Select(rect => YasPixelScrollPlanner.ReadRuler(
                baselineCapture.SrcMat, rect))
            .ToArray();
        var inputCount = 0;
        try
        {
            for (inputCount = 1;
                 inputCount <= YasPixelScrollPlanner.CalibrationInputLimit;
                 inputCount++)
            {
                await SendScrollInputsAsync(
                    1,
                    direction: -1,
                    YasPixelScrollPlanner.FirstPageInputIntervalMilliseconds,
                    cancellationToken);
                await Delay(
                    YasPixelScrollPlanner.CalibrationSettleDelayMilliseconds,
                    cancellationToken);
                using var shiftedCapture = CaptureToRectArea();
                for (var rulerIndex = 0;
                     rulerIndex < rulerRects.Count;
                     rulerIndex++)
                {
                    var shifted = YasPixelScrollPlanner.ReadRuler(
                        shiftedCapture.SrcMat,
                        rulerRects[rulerIndex]);
                    var pixelShift = YasPixelScrollPlanner.FindRulerShift(
                        baselines[rulerIndex],
                        shifted,
                        baselines[rulerIndex].Count - 8);
                    if (pixelShift <= 0) continue;
                    var pixelsPerInput = pixelShift / (double)inputCount;
                    _logger.LogInformation(
                        "角色配装检测：YAS ruler 标定 {Inputs} 次输入移动 {Pixels}px，每次 {PixelsPerInput:F2}px",
                        inputCount,
                        pixelShift,
                        pixelsPerInput);
                    return pixelsPerInput;
                }
            }
            throw new InvalidOperationException(
                "角色列表 YAS ruler 在5次输入内未能标定滚动速度");
        }
        finally
        {
            var inputsToUndo = Math.Min(
                inputCount,
                YasPixelScrollPlanner.CalibrationInputLimit);
            if (inputsToUndo > 0)
            {
                await SendScrollInputsAsync(
                    inputsToUndo,
                    direction: 1,
                    YasPixelScrollPlanner.FirstPageInputIntervalMilliseconds,
                    CancellationToken.None);
                await Delay(
                    YasPixelScrollPlanner.CalibrationSettleDelayMilliseconds,
                    CancellationToken.None);
            }
        }
    }

    private async Task<bool> ScrollPageAsync(
        IReadOnlyList<ArtifactCharacterPageRow> initialRows,
        Rect gridRoi,
        double assetScale,
        ArtifactCharacterScrollState scrollState,
        CancellationToken cancellationToken)
    {
        var rowPitch = ArtifactCharacterScrollPlanner.RowPitchForGridHeight(
            gridRoi.Height);
        var plan = YasPixelScrollPlanner.CreatePlan(
            rowPitch,
            scrollState.PixelsPerInput,
            scrollState.ResidualPixels,
            ArtifactCharacterScrollPlanner.PageAdvanceRows);
        var inputInterval = scrollState.IsFirstPage
            ? YasPixelScrollPlanner.FirstPageInputIntervalMilliseconds
            : YasPixelScrollPlanner.FastInputIntervalMilliseconds;
        await SendScrollInputsAsync(
            plan.InputCount,
            direction: -1,
            inputInterval,
            cancellationToken);
        scrollState.ResidualPixels = plan.ResidualPixels;
        scrollState.IsFirstPage = false;
        await Delay(
            YasPixelScrollPlanner.CalibrationSettleDelayMilliseconds,
            cancellationToken);
        var nextRows = await DetectPageRowsAsync(
            gridRoi, assetScale, cancellationToken);
        var overlap = ArtifactCharacterPageTracker.FindOverlap(
            initialRows, nextRows);
        var moved = overlap < initialRows.Count;
        if (!moved)
        {
            _logger.LogInformation("角色配装检测：YAS 整页滚动未产生新行，已到末页");
            return false;
        }

        scrollState.SetNextStartRow(overlap);

        _logger.LogInformation(
            "角色配装检测：YAS 整页实际推进 {Rows}/{TargetRows} 行，{Inputs} 次输入，像素残差 {Residual:F2}",
            Math.Min(initialRows.Count, initialRows.Count - overlap),
            ArtifactCharacterScrollPlanner.PageAdvanceRows,
            plan.InputCount,
            scrollState.ResidualPixels);
        return true;
    }

    private static async Task SendScrollInputsAsync(
        int inputCount,
        int direction,
        int intervalMilliseconds,
        CancellationToken cancellationToken)
    {
        using var pacer = new YasScrollInputPacer();
        for (var inputIndex = 0; inputIndex < inputCount; inputIndex++)
        {
            Simulation.SendInput.Mouse.VerticalScroll(direction);
            if (intervalMilliseconds > 0)
            {
                await pacer.DelayAsync(intervalMilliseconds, cancellationToken);
            }
        }
    }

    private static async Task MovePointerToScrollAreaAsync(
        Rect gridRoi,
        CancellationToken cancellationToken)
    {
        using var capture = CaptureToRectArea();
        var scrollX = Math.Max(gridRoi.X, gridRoi.Right - 12);
        var scrollY = gridRoi.Y + gridRoi.Height / 2;
        capture.MoveTo(scrollX, scrollY);
        await Delay(
            ArtifactCharacterScrollPlanner.SettleDelayMilliseconds,
            cancellationToken);
    }

    private static async Task<ArtifactCharacterCapturedDetail> CaptureSelectedDetailAsync(
        ArtifactCharacterClickBaseline clickBaseline,
        CancellationToken cancellationToken)
    {
        await Delay(80, cancellationToken);
        var timer = Stopwatch.StartNew();
        var detector = new ArtifactCharacterDetailSwitchDetector(
            clickBaseline.DetailSignature, 2);
        var alreadySelectedDetector = new ArtifactCharacterSameDetailSelectionDetector(
            clickBaseline.DetailSignature,
            clickBaseline.SelectionScore);
        while (timer.ElapsedMilliseconds < 900)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var capture = CaptureToRectArea();
            var signature = ArtifactCharacterDetailsReader.DetailSignature(
                capture.SrcMat);
            if (detector.Observe(signature) ||
                alreadySelectedDetector.Observe(signature))
            {
                return new ArtifactCharacterCapturedDetail(
                    capture.SrcMat.Clone(), signature);
            }
            await Delay(16, cancellationToken);
        }

        throw new TimeoutException("点击角色卡片后右侧角色详情在 900ms 内未稳定。");
    }

    private async Task<ArtifactCharacterDetailSample> ParseDetailWithOneRetryAsync(
        ArtifactCharacterDetailsReader reader,
        ArtifactCharacterCapturedDetail frame,
        string? gameNickname,
        string? miliastraNickname,
        string? miliastraCharacterKey,
        CancellationToken cancellationToken)
    {
        var first = reader.ReadPartial(
            frame.Capture,
            gameNickname, miliastraNickname,
            miliastraCharacterKey);
        if (first.IsComplete) return first.RequireComplete();

        await Delay(160, cancellationToken);
        using var retryCapture = CaptureToRectArea();
        var sameDetail = ArtifactCharacterDetailsReader.IsSameDetailForRetry(
            frame.DetailSignature,
            ArtifactCharacterDetailsReader.DetailSignature(retryCapture.SrcMat));
        var retry = reader.ReadPartial(
            retryCapture.SrcMat,
            gameNickname, miliastraNickname,
            miliastraCharacterKey,
            readName: !sameDetail || first.CharacterName is null,
            readLevel: !sameDetail || !first.Level.HasValue);
        var resolved = sameDetail ? first.Merge(retry) : retry;
        if (resolved.IsComplete)
        {
            _logger.LogDebug(
                sameDetail
                    ? "角色详情 OCR 通过同一详情的首帧与唯一重试帧字段合并成功"
                    : "角色详情 OCR 重试帧身份已变化，已丢弃首帧字段并使用完整重试帧");
            return resolved.RequireComplete();
        }

        var failure = new InvalidOperationException(
            "角色详情 OCR 首帧与唯一重试帧合并后仍有字段失败。",
            new AggregateException(first.Failures.Concat(retry.Failures)));
        TaskFailureDiagnostics.CaptureScreenshotOnce(
            failure,
            "角色配装检测-右侧OCR重试帧",
            _ => SaveFailureFrame(retryCapture.SrcMat));
        throw failure;
    }

    private static ArtifactCharacterClickBaseline ClickCharacterCard(
        Rect gridRoi,
        Rect cardRect)
    {
        using var capture = CaptureToRectArea();
        using var page = capture.DeriveCrop(gridRoi);
        using var item = page.DeriveCrop(cardRect);
        var baseline = new ArtifactCharacterClickBaseline(
            ArtifactCharacterDetailsReader.DetailSignature(capture.SrcMat),
            ArtifactGridSelectionDetector.Score(item.SrcMat));
        item.Click();
        return baseline;
    }

    private static async Task<ArtifactCharacterDetailSample> ConfirmFavoriteAsync(
        ArtifactCharacterDetailSample detail,
        CancellationToken cancellationToken)
    {
        if (detail.Favorite) return detail;

        await Delay(80, cancellationToken);
        using var confirmationCapture = CaptureToRectArea();
        return ArtifactCharacterDetailsReader.IsFavorite(confirmationCapture.SrcMat)
            ? detail with { Favorite = true }
            : detail;
    }

    private void SaveFailureFrame(Mat capture)
    {
        var directory = Global.Absolute(@"log\screenshot");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"error-character-ocr-{DateTime.Now:yyyyMMddHHmmssfff}.png");
        Cv2.ImWrite(path, capture);
        _logger.LogInformation("角色详情 OCR 失败帧已保存：{Path}", path);
    }

    private static void AddCharacter(
        IDictionary<string, ArtifactCharacterRosterEntryDto> characters,
        ArtifactCharacterDetailSample detail)
    {
        var entry = new ArtifactCharacterRosterEntryDto(
            ArtifactCharacterCardReader.ToCharacterKey(detail.CharacterName),
            detail.Level,
            detail.Favorite);
        if (characters.TryGetValue(entry.CharacterKey, out var existing))
        {
            throw new InvalidOperationException(
                existing == entry
                    ? $"角色卡片被重复识别：{entry.CharacterKey}"
                    : $"角色卡片重复识别结果不一致：{entry.CharacterKey}");
        }
        characters.Add(entry.CharacterKey, entry);
    }

    private sealed record ArtifactCharacterClickBaseline(
        ulong DetailSignature,
        double SelectionScore);

    private sealed class ArtifactCharacterScrollState
    {
        private int? nextStartRow;

        internal bool IsCalibrated { get; set; }
        internal bool IsFirstPage { get; set; } = true;
        internal double PixelsPerInput { get; set; }
        internal double ResidualPixels { get; set; }

        internal void SetNextStartRow(int startRow) =>
            this.nextStartRow = Math.Max(0, startRow);

        internal int? ConsumeNextStartRow()
        {
            var value = this.nextStartRow;
            this.nextStartRow = null;
            return value;
        }
    }

    private sealed class ArtifactCharacterCapturedDetail(
        Mat capture,
        ulong detailSignature) : IDisposable
    {
        internal Mat Capture { get; } = capture;
        internal ulong DetailSignature { get; } = detailSignature;
        public void Dispose() => Capture.Dispose();
    }

    private sealed class ArtifactCharacterRosterScanTask(
        Func<CancellationToken, Task<ArtifactCharacterRosterDto>> scan,
        CancellationToken externalCancellationToken) : ISoloTask
    {
        internal ArtifactCharacterRosterDto? Result { get; private set; }
        public string Name => "角色配装检测";

        public async Task Start(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                externalCancellationToken);
            Result = await scan(linked.Token);
        }
    }
}
