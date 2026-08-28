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
        try
        {
            Simulation.SendInput.SimulateAction(GIActions.OpenCharacterScreen);
            await OpenCharacterListAsync(assets, cancellationToken);
            var gridParams = GridParams.CharacterDevelopmentForCapture(
                new Size(captureRect.Width, captureRect.Height));
            var scroller = new GridScroller(
                gridParams, _logger, Simulation.SendInput, cancellationToken);
            using var detailsReader = new ArtifactCharacterDetailsReader();
            var characters = new Dictionary<string, ArtifactCharacterRosterEntryDto>(StringComparer.Ordinal);
            var pageTracker = new ArtifactCharacterPageTracker();
            var identityGuard = new ArtifactCharacterScanIdentityGuard();
            double? detailSignature = null;
            var clicked = 0;
            while (true)
            {
                var pageRows = await DetectPageRowsAsync(
                    gridParams.Roi, assetScale, cancellationToken);
                var newRows = pageTracker.SelectUnprocessedRows(pageRows);
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
                    ClickCharacterCard(gridParams.Roi, rect);

                    using var captured = await CaptureSelectedDetailAsync(
                        detailSignature, cancellationToken);
                    detailSignature = captured.DetailSignature;
                    var detail = await ParseDetailWithOneRetryAsync(
                        detailsReader, captured,
                        gameNickname, miliastraNickname,
                        miliastraCharacterKey,
                        cancellationToken);
                    var characterKey = ArtifactCharacterCardReader.ToCharacterKey(
                        detail.CharacterName);
                    if (identityGuard.DidNotChange(characterKey))
                    {
                        _logger.LogWarning(
                            "角色配装检测：点击后右侧角色仍为 {CharacterKey}，补点同一头像并只重试一次 OCR",
                            characterKey);
                        ClickCharacterCard(gridParams.Roi, rect);
                        await Delay(160, cancellationToken);
                        using var retryCapture = CaptureToRectArea();
                        detail = detailsReader.Read(
                            retryCapture.SrcMat,
                            gameNickname, miliastraNickname,
                            miliastraCharacterKey);
                        detailSignature = ArtifactCharacterDetailsReader.DetailSignature(
                            retryCapture.SrcMat);
                        characterKey = ArtifactCharacterCardReader.ToCharacterKey(
                            detail.CharacterName);
                    }

                    identityGuard.Commit(characterKey);
                    detail = await ConfirmFavoriteAsync(detail, cancellationToken);
                    AddCharacter(characters, detail);
                    clicked++;
                }
                pageTracker.Commit(pageRows);

                var moved = await scroller.TryVerticalScollDown((src, columns) =>
                    CharacterSelectionHelper.DetectCharacterCards(
                            src, assetScale, out _, out _)
                        .Select(card => card.CardRect));
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

    private static async Task<ArtifactCharacterCapturedDetail> CaptureSelectedDetailAsync(
        double? previousDetailSignature,
        CancellationToken cancellationToken)
    {
        if (previousDetailSignature.HasValue)
        {
            var timer = Stopwatch.StartNew();
            var detector = new ArtifactScanDetailChangeDetector(
                previousDetailSignature.Value, 0.5);
            while (timer.ElapsedMilliseconds < 450)
            {
                using var capture = CaptureToRectArea();
                var signature = ArtifactCharacterDetailsReader.DetailSignature(capture.SrcMat);
                if (detector.Observe(signature))
                {
                    await Delay(40, cancellationToken);
                    break;
                }
                await Delay(12, cancellationToken);
            }
        }
        else
        {
            await Delay(80, cancellationToken);
        }

        using var latest = CaptureToRectArea();
        var latestSignature = ArtifactCharacterDetailsReader.DetailSignature(latest.SrcMat);
        return new ArtifactCharacterCapturedDetail(
            latest.SrcMat.Clone(), latestSignature);
    }

    private async Task<ArtifactCharacterDetailSample> ParseDetailWithOneRetryAsync(
        ArtifactCharacterDetailsReader reader,
        ArtifactCharacterCapturedDetail frame,
        string? gameNickname,
        string? miliastraNickname,
        string? miliastraCharacterKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return reader.Read(
                frame.Capture,
                gameNickname, miliastraNickname,
                miliastraCharacterKey);
        }
        catch (InvalidOperationException)
        {
            await Delay(160, cancellationToken);
            using var retryCapture = CaptureToRectArea();
            try
            {
                return reader.Read(
                    retryCapture.SrcMat,
                    gameNickname, miliastraNickname,
                    miliastraCharacterKey);
            }
            catch (Exception retryFailure)
            {
                TaskFailureDiagnostics.CaptureScreenshotOnce(
                    retryFailure,
                    "角色配装检测-右侧OCR重试帧",
                    _ => SaveFailureFrame(retryCapture.SrcMat));
                throw;
            }
        }
    }

    private static void ClickCharacterCard(Rect gridRoi, Rect cardRect)
    {
        using var capture = CaptureToRectArea();
        using var page = capture.DeriveCrop(gridRoi);
        using var item = page.DeriveCrop(cardRect);
        item.Click();
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

    private sealed class ArtifactCharacterCapturedDetail(
        Mat capture,
        double detailSignature) : IDisposable
    {
        internal Mat Capture { get; } = capture;
        internal double DetailSignature { get; } = detailSignature;
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
