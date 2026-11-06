using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.Common.Job;

public class SwitchPartyTask
{
    private readonly SwitchPartyTaskIo _io;
    private readonly double _assetScale;
    private Microsoft.Extensions.Logging.ILogger Logger => _io.Logger;

    public SwitchPartyTask() : this(SwitchPartyTaskIo.CreateNative()) { }
    internal SwitchPartyTask(SwitchPartyTaskIo io) { _io = io; _assetScale = io.Scale; }

    public string Name => "切换队伍";

    private Task Delay(int milliseconds, CancellationToken token) => _io.Delay(milliseconds, token);

    public Task<bool> Start(string partyName, CancellationToken ct)
        => StartCore(partyName, name => PartyNameAliases.IsMatch(name, partyName), false, ct);

    internal Task<bool> StartForDomain(string partyName, CancellationToken ct)
        => StartCore(partyName, name => PartyNameAliases.IsMatch(name, partyName), true, ct, true);

    /// <summary>当前队伍满足候选时保留，否则查找首个可用候选；未找到时留在队伍页供默认队伍回退。</summary>
    public Task<bool> StartAny(IReadOnlyList<string> partyNames, CancellationToken ct)
        => StartAnyCore(partyNames, ct, false);

    internal Task<bool> StartAnyForDomain(IReadOnlyList<string> partyNames, CancellationToken ct)
        => StartAnyCore(partyNames, ct, true);

    private Task<bool> StartAnyCore(IReadOnlyList<string> partyNames, CancellationToken ct, bool deferApplyToCaller)
    {
        ArgumentNullException.ThrowIfNull(partyNames);
        if (partyNames.Count == 0 || partyNames.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("候选队伍名称不能为空", nameof(partyNames));
        var names = partyNames.ToArray();
        return StartCore(string.Join("、", names),
            name => names.Any(candidate => PartyNameAliases.IsMatch(name, candidate)), true, ct, deferApplyToCaller);
    }

    private Task<bool> StartCore(string partyName, Func<string, bool> matches,
        bool stayInPartyViewOnFailure, CancellationToken ct, bool deferApplyToCaller = false)
        => UiOperation.RunAsync("party-setup", TimeSpan.FromSeconds(60), ct,
            operation => StartVerifiedCore(partyName, matches, stayInPartyViewOnFailure, operation.Token, deferApplyToCaller),
            Logger, clock: _io.Clock, captureFailure: _io.NativeDiagnostics ? (error, context) => TaskFailureDiagnostics.CaptureScreenshotOnce(error, context) : null);

    internal static async Task<ImageRegion> CapturePartyFrameAsync(Func<ImageRegion> capture,
        Func<ImageRegion, UiSnapshot> read, Func<int, CancellationToken, Task> delay,
        CancellationToken ct, TimeProvider? clock = null, Func<CancellationToken, Task>? prepare = null)
    {
        clock ??= TimeProvider.System;
        ImageRegion? accepted = null;
        try
        {
            await UiOperation.RunAsync("party-readable-frame", TimeSpan.FromSeconds(10), ct, async operation =>
            {
                if (prepare != null) await prepare(operation.Token);
                var requestedAt = clock.GetTimestamp();
                Fischless.GameCapture.CaptureFrameStamp previous = default;
                while (true)
                {
                    operation.Check();
                    var image = capture();
                    try
                    {
                        var observed = read(image);
                        operation.Check();
                        var source = image.FrameStamp;
                        if (previous.IsKnown && source.IsKnown && source.SessionId != previous.SessionId)
                            throw new PartySetupFailedException("编队读取期间截图来源改变，不能继续读取队伍名称");
                        operation.Observe("编队名称可读新帧", observed.HasUsableEvidence ? observed.Describe() : "source-stale", source.Sequence);
                        if (observed.SourceBound && observed.SourceStamp == source && source.CapturedTimestamp >= requestedAt &&
                            (!previous.IsKnown || source.IsAfter(previous)) && observed.Matches(UiTarget.Party))
                        {
                            accepted = image;
                            return true;
                        }
                        if (source.IsKnown) previous = source;
                    }
                    finally { if (!ReferenceEquals(accepted, image)) image.Dispose(); }
                    await delay(100, operation.Token);
                }
            }, clock: clock);
            return accepted!;
        }
        catch { accepted?.Dispose(); throw; }
    }

    private async Task<bool> StartVerifiedCore(string partyName, Func<string, bool> matches,
        bool stayInPartyViewOnFailure, CancellationToken ct, bool deferApplyToCaller)
    {
        bool isInPartyViewUi = false;
        var driver = _io.Driver();
        using var driverLifetime = driver as IDisposable;

        Logger.LogInformation("尝试切换至队伍: {Name}", partyName);
        var initial = driver.Capture();
        if (initial.PartyList)
            await driver.ActAsync(UiAction.Escape, initial, ct);

        if (!initial.Party && !initial.PartyList && !deferApplyToCaller)
        {
            isInPartyViewUi = true;
            await _io.ReturnMain(ct);

            // 原8.4秒预算内只提交一次探测；危险世界由外层现有恢复owner处理。
            await UiTransition.EnterPartyAsync(driver, ct, Logger);
        }

        using var ra = await CapturePartyFrameAsync(() => _io.Capture(), image => _io.Read(image),
            (milliseconds, token) => Delay(milliseconds, token), ct, _io.Clock, prepare: async token =>
            {
                await UiTransition.WaitAsync("party-ready", UiTarget.Party, driver, token, TimeSpan.FromSeconds(10), logger: Logger);
                await Delay(500, token);
            });
        using var partyViewBtn = _io.Element(ra, "PartyBtnChooseView");
        if (!partyViewBtn.IsExist()) throw new PartySetupFailedException("编队页面已变化，不能读取队伍名称");

        // OCR 当前队伍名称（无法单字，中间禁止空格）
        var currTeamName = _io.OcrOne(ra, new RecognitionObject
        {
            RecognitionType = RecognitionTypes.Ocr,
            RegionOfInterest = new Rect(partyViewBtn.Right, partyViewBtn.Top, (int)(350 * _assetScale),
                partyViewBtn.Height)
        });

        
        var tempName = currTeamName
            .Replace("\"", "")        // 移除所有双引号（核心新增，解决日志里的""问题）
            .Replace("\r\n", "")      // 清理Windows换行符
            .Replace("\r", "");   // 先清理所有双引号，避免引号干扰后续处理
                              
        // 核心逻辑：找到第一个换行符(\n)的位置，截断并删除换行+后面所有字符
        int firstNewLineIndex = tempName.IndexOf('\n');
        if (firstNewLineIndex != -1) // 存在换行符，截取到换行符前
        {
            tempName = tempName.Substring(0, firstNewLineIndex);
        }
                          
        // 最后统一去首尾所有空白（空格、制表符、回车符\r等），得到纯净队伍名
        currTeamName = tempName.Trim();

        Logger.LogInformation("切换队伍，当前队伍名称: {Text}，使用正则表达式规则进行模糊匹配", currTeamName);
        if (matches(currTeamName))
        {
            Logger.LogInformation("当前队伍[{Name}]即为目标队伍，无需切换", currTeamName);
            await UiRecovery.CompletePartyHandoffAsync(driver, deferApplyToCaller, ct, Logger);

            return true;
        }

        using (var current = _io.Capture())
        using (var choose = _io.Element(current, "PartyBtnChooseView"))
        {
            if (!_io.Read(current).Matches(UiTarget.Party) || !choose.IsExist())
                throw new PartySetupFailedException("当前编队页面或队伍列表按钮未确认，不能点击");
            ct.ThrowIfCancellationRequested();
            UiOperation.Current?.Check();
            AdmitPartyInput(current, _io.Read(current), UiTarget.Party, ct);
            _io.Click(current, choose.Right, choose.Bottom);
        }
        await UiTransition.WaitAsync("party-list-open", UiTarget.PartyList, driver, ct, TimeSpan.FromSeconds(5), logger: Logger);
        Rect regionOfInterest;
        using (var current = _io.Capture())
        using (var delete = _io.Element(current, "PartyBtnDelete"))
        {
            if (!delete.IsExist()) throw new PartySetupFailedException("队伍列表已变化，不能继续使用旧截图");
            regionOfInterest = new Rect(0, (int)(80 * _assetScale), delete.Right, delete.Top - (int)(80 * _assetScale));
        }

        // Original top reset, 16-page cap and parent deadline remain the traversal owner.
        using (var admission = _io.Capture())
            AdmitPartyInput(admission, _io.Read(admission), UiTarget.PartyList, ct);
        await _io.ResetTop(ct);
        PartyListPageObservation? previousPage = null;
        long previousInputCompleted = 0;
        Guid? listSession = null;
        for (var i = 0; i < 16; i++)
        {
            await Delay(50, ct);
            try
            {
                using var page = _io.Capture();
                var observed = _io.Read(page);
                if (!observed.Matches(UiTarget.PartyList) || !observed.SourceBound || observed.SourceStamp != page.FrameStamp ||
                    !page.FrameStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge) || !_io.SameTarget())
                    throw new PartySetupFailedException("队伍列表当前来源或窗口失效，不能继续分页");
                listSession ??= page.FrameStamp.SessionId;
                if (listSession != page.FrameStamp.SessionId)
                    throw new PartySetupFailedException("队伍列表捕获会话改变，不能继续分页");
                var recognitionObject = new RecognitionObject
                { RecognitionType = RecognitionTypes.Ocr, RegionOfInterest = regionOfInterest, DrawOnWindow = true, Name = "队伍识别区域" };
                var namePage = _io.ReadNames(page, recognitionObject);
                var names = namePage.Names;
                try
                {
                    if (!observed.HasUsableEvidence || namePage.Source != page.FrameStamp || !page.FrameStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge) || !_io.SameTarget())
                        throw new PartySetupFailedException("队伍OCR完成后来源或窗口已失效，不能判定末页或继续输入");
                    var titles = names.Where(t => t.X > 35 * _assetScale && t.X < 100 * _assetScale).ToArray();
                    var state = PartyListPageObservation.Read(page.SrcMat, page.FrameStamp, _assetScale, titles.Select(t => new PartyListRow(t.Text, new Rect(t.X, t.Y, t.Width, t.Height))));
                    if (previousPage != null && !state.AdvancesFrom(previousPage, previousInputCompleted, _io.Clock))
                        throw new PartySetupFailedException("队伍列表翻页未取得新页正证，停止继续输入");
                    async Task SelectTitle(Region title)
                    {
                        AdmitPartyInput(page, observed, UiTarget.PartyList, ct);
                        _io.Click(page, title.Right + 100 * _assetScale, title.Bottom);
                        await Delay(400, ct);
                        await ConfirmParty(driver, ct, deferApplyToCaller);
                        _io.ClearCombatScenes();
                    }
                    foreach (var title in titles)
                    {
                        if (!matches(title.Text)) continue;
                        await SelectTitle(title); return true;
                    }
                    foreach (var title in titles)
                    {
                        var left = Math.Max(regionOfInterest.Left, (int)(35 * _assetScale));
                        if (title.Width <= 0 || title.Height <= 0 || title.Right <= left || title.Right > regionOfInterest.Right ||
                            title.Y < regionOfInterest.Top || title.Bottom > regionOfInterest.Bottom) continue;
                        var band = new Rect(left, title.Y, title.Right - left, title.Height);
                        CheckPartyInput(page, observed, UiTarget.PartyList, ct, requireForeground: true);
                        if (namePage.Source != page.FrameStamp) throw new PartySetupFailedException("队伍名字行来源不属于当前页");
                        var completeName = namePage.ReadLine(band);
                        CheckPartyInput(page, observed, UiTarget.PartyList, ct, requireForeground: true);
                        if (namePage.Source != page.FrameStamp) throw new PartySetupFailedException("队伍名字行读取期间来源改变");
                        if (!matches(completeName)) continue;
                        await SelectTitle(title); return true;
                    }
                    if (state.End == PartyListEnd.End)
                    { Logger.LogInformation("已确认队伍列表滚动到底，当前页未找到目标"); break; }
                    if (state.End != PartyListEnd.More || titles.Length == 0)
                        throw new PartySetupFailedException("队伍列表剩余页状态未知，不能盲目翻页或宣称搜索结束");
                    if (i == 15) break;
                    AdmitPartyInput(page, observed, UiTarget.PartyList, ct);
                    if (!state.TryPlanPage(regionOfInterest, out var pagePlan))
                        throw new PartySetupFailedException("当前队伍行与滚动条不足以规划有重叠的新页，停止输入");
                    await _io.DragPageAsync(page, pagePlan, ct, () => CheckPartyInput(page, observed, UiTarget.PartyList, ct, requireForeground: true));
                    previousInputCompleted = _io.Clock.GetTimestamp();
                    previousPage = state;
                    // Two 60ms drag settles share the existing 400ms per-page wait.
                    await Delay(280, ct);
                }
                finally { foreach (var region in names) region.Dispose(); }
            }
            finally { _io.ClearDrawings(); }
        }
        if (stayInPartyViewOnFailure && !isInPartyViewUi)
        {
            Logger.LogWarning("未找到推荐队伍: {Name}，关闭队伍列表并回退默认队伍", partyName);
            // 只退出列表，不返回主界面，否则会丢失秘境的开始挑战页面。
            await UiTransition.WaitAsync("party-list-close", UiTarget.Party, driver, ct, TimeSpan.FromSeconds(5),
                observed => observed.PartyList ? UiAction.Escape : null, logger: Logger);
            return false;
        }
        Logger.LogError("未找到队伍: {Name}，返回主界面", partyName);
        Logger.LogInformation("如果找不到设定的队伍名，有可能是文字识别效果不佳，请尝试正则表达式");
        await _io.ReturnMain(ct);
        return false;
    }

    private void CheckPartyInput(ImageRegion image, UiSnapshot observed, UiTarget target, CancellationToken ct, bool requireForeground = false)
    {
        ct.ThrowIfCancellationRequested(); UiOperation.Current?.Check();
        if (!_io.SameTarget() || (requireForeground && !_io.ForegroundReady()) || !observed.Matches(target) || !observed.SourceBound || observed.SourceStamp != image.FrameStamp ||
            !image.FrameStamp.IsFresh(_io.Clock, UiSnapshot.RecoveryMaximumAge))
            throw new PartySetupFailedException("队伍输入的当前列表、来源或窗口正证失效");
    }

    private void AdmitPartyInput(ImageRegion image, UiSnapshot observed, UiTarget target, CancellationToken ct)
    {
        CheckPartyInput(image, observed, target, ct); _io.Prepare(); CheckPartyInput(image, observed, target, ct, requireForeground: true);
    }

    private async Task ConfirmParty(IUiDriver driver, CancellationToken ct, bool deferApplyToCaller)
    {
        Task<bool> ClickConfirm(CancellationToken token, bool left)
        {
            token.ThrowIfCancellationRequested();
            UiOperation.Current?.Check();
            _io.Prepare();
            using var image = _io.Capture();
            var target = left ? UiTarget.PartyList : UiTarget.Party;
            var observed = _io.Read(image);
            UiOperation.Current?.Observe(observed, target, "pre-input");
            token.ThrowIfCancellationRequested();
            UiOperation.Current?.Check();
            if (!observed.Matches(target)) return Task.FromResult(false);
            AdmitPartyInput(image, observed, target, token);
            var roi = new Rect(left ? 0 : image.Width - image.Width / 4, image.Height / 4,
                image.Width / 4, image.Height - image.Height / 4);
            return Task.FromResult(_io.Confirm(image, roi));
        }

        await UiRecovery.ConfirmPartyAsync(driver, token => ClickConfirm(token, true),
            token => ClickConfirm(token, false), ct, deferApplyToCaller, Logger);
        await UiRecovery.CompletePartyHandoffAsync(driver, deferApplyToCaller, ct, Logger);
    }
}
