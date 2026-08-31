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
            var identityGuard = new ArtifactCharacterScanIdentityGuard();
            var scrollState = new ArtifactCharacterScrollState();
            var paginationCursor = new UnknownExtentYasCursor(
                ArtifactCharacterScrollPlanner.PageAdvanceRows);
            var clicked = 0;
            while (true)
            {
                var pageRows = await DetectPageRowsAsync(
                    gridParams.Roi, assetScale, cancellationToken);
                var pageSlice = paginationCursor.CurrentPage;
                var newRows = ArtifactCharacterPageTracker.SelectFromStartRow(
                        pageRows,
                        pageSlice.StartRow)
                    .Take(pageSlice.RowCount)
                    .ToArray();
                _logger.LogInformation(
                    "CHARACTER_PAGE_PLAN page={Page} visibleRows={VisibleRows} startRow={StartRow} rowCount={RowCount} cards={Cards} final={Final}",
                    pageSlice.PageIndex,
                    pageRows.Count,
                    pageSlice.StartRow,
                    newRows.Length,
                    newRows.Sum(row => row.Cards.Count),
                    pageSlice.IsFinalPage);
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

                paginationCursor.CommitRead();
                if (paginationCursor.Completed)
                {
                    _logger.LogInformation(
                        "角色配装检测：末页已按物理推进段数读取完成");
                    break;
                }
                await MovePointerToScrollAreaAsync(
                    gridParams.Roi,
                    cancellationToken);
                if (!scrollState.IsCalibrated)
                {
                    scrollState.PixelsPerInput = await MeasureScrollPixelsPerInputAsync(
                        gridParams.Roi,
                        cancellationToken);
                    scrollState.IsCalibrated = true;
                }
                var scrollPlan = paginationCursor.PlanScroll();
                var confirmedRows = await ScrollPageAsync(
                    gridParams.Roi,
                    scrollState,
                    scrollPlan.RequestedRows,
                    cancellationToken);
                paginationCursor.CommitScroll(new UnknownExtentScrollReceipt(
                    scrollPlan.RequestedRows,
                    confirmedRows,
                    IsStable: true));
                _logger.LogInformation(
                    "CHARACTER_SCROLL_RECEIPT page={Page} requestedRows={RequestedRows} confirmedRows={ConfirmedRows} completed={Completed}",
                    pageSlice.PageIndex,
                    scrollPlan.RequestedRows,
                    confirmedRows,
                    paginationCursor.Completed);
                if (paginationCursor.Completed) break;
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

    private async Task<int> ScrollPageAsync(
        Rect gridRoi,
        ArtifactCharacterScrollState scrollState,
        int requestedRows,
        CancellationToken cancellationToken)
    {
        var rowPitch = ArtifactCharacterScrollPlanner.RowPitchForGridHeight(
            gridRoi.Height);
        var plans = YasPixelScrollPlanner.CreateSegmentedPlans(
            rowPitch,
            scrollState.PixelsPerInput,
            scrollState.ResidualPixels,
            requestedRows);
        var confirmedRows = 0;
        foreach (var segment in plans)
        {
            var beforeRulers = CaptureMotionRulers(gridRoi);
            var scrollTrace = ArtifactCharacterScrollTraceRecorder.TryStart(
                gridRoi,
                segment.InputCount,
                direction: -1,
                YasPixelScrollPlanner.FastInputIntervalMilliseconds);
            try
            {
                await SendScrollInputsAsync(
                    segment.InputCount,
                    direction: -1,
                    YasPixelScrollPlanner.FastInputIntervalMilliseconds,
                    cancellationToken);
                await Delay(
                    YasPixelScrollPlanner.SegmentCooldownMilliseconds,
                    cancellationToken);
            }
            finally
            {
                scrollTrace?.Complete();
            }

            var afterRulers = CaptureMotionRulers(gridRoi);
            var maximumShift = Math.Max(
                8,
                (int)Math.Round(rowPitch * 1.5));
            var rulerShifts = beforeRulers.Zip(afterRulers)
                .Select(pair => YasPixelScrollPlanner.FindRulerShift(
                    pair.First,
                    pair.Second,
                    maximumShift))
                .ToArray();
            var advancedRows = ArtifactCharacterScrollPlanner
                .ClassifySegmentAdvance(rulerShifts, rowPitch);
            if (advancedRows == 0)
            {
                _logger.LogInformation(
                    "角色配装检测：第 {Segment}/{RequestedRows} 次短推进冷却后位移为 {Shifts}，已完整回弹到末页",
                    confirmedRows + 1,
                    requestedRows,
                    string.Join(",", rulerShifts));
                break;
            }
            if (advancedRows != 1)
            {
                throw new InvalidOperationException(
                    $"角色列表第 {confirmedRows + 1}/{requestedRows} 次短推进冷却后 ruler 位移为 {string.Join(",", rulerShifts)}px，超过一行，拒绝提交不连续分页。");
            }

            confirmedRows++;
            scrollState.ResidualPixels = segment.ResidualPixels;
            _logger.LogInformation(
                "角色配装检测：短推进 {Segment}/{RequestedRows} 已由 ruler 位移 {Shifts} 确认 1 行，{Inputs} 次输入，像素残差 {Residual:F2}",
                confirmedRows,
                requestedRows,
                string.Join(",", rulerShifts),
                segment.InputCount,
                scrollState.ResidualPixels);
        }

        if (confirmedRows > 0 && !await WaitForPageSettleAsync(
                gridRoi,
                cancellationToken))
        {
            throw new InvalidOperationException(
                $"角色列表 {confirmedRows} 次短推进完成后未连续稳定。");
        }
        return confirmedRows;
    }

    private static IReadOnlyList<Vec3b>[] CaptureMotionRulers(Rect gridRoi)
    {
        var rulerRects = ArtifactCharacterScrollPlanner.MotionRulerRects(
            gridRoi);
        using var capture = CaptureToRectArea();
        return rulerRects
            .Select(rect => (IReadOnlyList<Vec3b>)
                YasPixelScrollPlanner.ReadRuler(capture.SrcMat, rect).ToArray())
            .ToArray();
    }

    private static async Task<bool> WaitForPageSettleAsync(
        Rect gridRoi,
        CancellationToken cancellationToken)
    {
        await Delay(
            YasPixelScrollPlanner.CalibrationSettleDelayMilliseconds,
            cancellationToken);
        var rulerRects = ArtifactCharacterScrollPlanner.MotionRulerRects(
            gridRoi);
        using var baselineCapture = CaptureToRectArea();
        var previous = rulerRects
            .Select(rect => YasPixelScrollPlanner.ReadRuler(
                baselineCapture.SrcMat,
                rect))
            .ToArray();
        var settleTracker = new YasScrollSettleTracker();
        for (var sample = 0;
             sample < YasPixelScrollPlanner.MaximumPageSettleSamples;
             sample++)
        {
            await Delay(
                YasPixelScrollPlanner.PageSettleSampleIntervalMilliseconds,
                cancellationToken);
            using var capture = CaptureToRectArea();
            var current = rulerRects
                .Select(rect => YasPixelScrollPlanner.ReadRuler(
                    capture.SrcMat,
                    rect))
                .ToArray();
            var allStable = previous.Zip(current)
                .All(pair => YasPixelScrollPlanner.IsRulerStable(
                    pair.First,
                    pair.Second));
            if (settleTracker.Observe(allStable))
            {
                return true;
            }
            previous = current;
        }
        return false;
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
        internal bool IsCalibrated { get; set; }
        internal double PixelsPerInput { get; set; }
        internal double ResidualPixels { get; set; }
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
