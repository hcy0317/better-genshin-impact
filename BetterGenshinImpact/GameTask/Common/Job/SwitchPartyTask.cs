using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.View.Drawable;
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
    private readonly double _assetScale = TaskContext.Instance().SystemInfo.AssetScale;

    public string Name => "切换队伍";

    private readonly ReturnMainUiTask _returnMainUiTask = new();

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
            Logger, captureFailure: (error, context) => TaskFailureDiagnostics.CaptureScreenshotOnce(error, context));

    private async Task<bool> StartVerifiedCore(string partyName, Func<string, bool> matches,
        bool stayInPartyViewOnFailure, CancellationToken ct, bool deferApplyToCaller)
    {
        bool isInPartyViewUi = false;
        using var driver = new NativeUiDriver();

        Logger.LogInformation("尝试切换至队伍: {Name}", partyName);
        var initial = driver.Capture();
        if (initial.PartyList)
            await driver.ActAsync(UiAction.Escape, initial, ct);

        if (!initial.Party && !initial.PartyList && !deferApplyToCaller)
        {
            isInPartyViewUi = true;
            await _returnMainUiTask.Start(ct);

            // 尝试打开队伍配置页面
            const int maxAttempts = 2;
            bool isOpened = false;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);

                // 考虑加载时间 2s，共检查 4.2s，如果失败则抛出异常

                for (int i = 0; i < 7; i++) // 检查 7 次
                {
                    await Delay(600, ct);
                    using var raCheck = CaptureToRectArea();
                    if (Bv.IsInPartyViewUi(raCheck))
                    {
                        isOpened = true;
                        break;
                    }
                }

                if (isOpened)
                {
                    break; // 页面已打开，跳出循环
                }
            }

            if (!isOpened)
            {
                throw new PartySetupFailedException("未能打开队伍配置界面");
            }
        }

        await UiTransition.WaitAsync("party-ready", UiTarget.Party, driver, ct, TimeSpan.FromSeconds(10), logger: Logger);
        await Delay(500, ct);

        using var ra = CaptureToRectArea();
        if (!NativeUiDriver.Read(ra).Matches(UiTarget.Party))
            throw new PartySetupFailedException("编队页面出现遮挡，不能读取队伍名称");
        using var partyViewBtn = ra.Find(ElementRecognition.Get("PartyBtnChooseView", ra));
        if (!partyViewBtn.IsExist()) throw new PartySetupFailedException("编队页面已变化，不能读取队伍名称");

        // OCR 当前队伍名称（无法单字，中间禁止空格）
        using var currTeamNameRegion = ra.Find(new RecognitionObject
        {
            RecognitionType = RecognitionTypes.Ocr,
            RegionOfInterest = new Rect(partyViewBtn.Right, partyViewBtn.Top, (int)(350 * _assetScale),
                partyViewBtn.Height)
        });
        var currTeamName = currTeamNameRegion.Text;
        
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
            if (isInPartyViewUi)
            {
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                await Delay(500, ct);
                await _returnMainUiTask.Start(ct);
            }

            return true;
        }

        using (var current = CaptureToRectArea())
        using (var choose = current.Find(ElementRecognition.Get("PartyBtnChooseView", current)))
        {
            if (!NativeUiDriver.Read(current).Matches(UiTarget.Party) || !choose.IsExist())
                throw new PartySetupFailedException("当前编队页面或队伍列表按钮未确认，不能点击");
            ct.ThrowIfCancellationRequested();
            UiOperation.Current?.Check();
            choose.Click();
        }
        await UiTransition.WaitAsync("party-list-open", UiTarget.PartyList, driver, ct, TimeSpan.FromSeconds(5), logger: Logger);
        Rect regionOfInterest;
        using (var current = CaptureToRectArea())
        using (var delete = current.Find(ElementRecognition.Get("PartyBtnDelete", current)))
        {
            if (!delete.IsExist()) throw new PartySetupFailedException("队伍列表已变化，不能继续使用旧截图");
            regionOfInterest = new Rect(0, (int)(80 * _assetScale), delete.Right, delete.Top - (int)(80 * _assetScale));
        }

        // 点击到最上方
        await Task.Delay(50, ct);
        GameCaptureRegion.GameRegion1080PPosClick(700, 125);
        await Task.Delay(50, ct);
        Simulation.SendInput.Mouse.LeftButtonDown();
        try { await Task.Delay(450, ct); }
        finally { Simulation.SendInput.Mouse.LeftButtonUp(); }
        await Task.Delay(100, ct);

        RecognitionObject recognitionObject = new RecognitionObject
        {
            RecognitionType = RecognitionTypes.Ocr,
            RegionOfInterest = regionOfInterest,
            DrawOnWindow = true,
            Name = "队伍名称",
            DrawOnWindowPen= System.Drawing.Pens.White
        };
        // 逐页查找
        try
        {
            for (var i = 0; i < 16; i++)    // 6.0版本最多20个队伍
            {
                using var page = CaptureToRectArea();
                if (!NativeUiDriver.Read(page).Matches(UiTarget.PartyList))
                    throw new PartySetupFailedException("队伍列表已变化或出现遮挡，不能继续翻页或选队");

                var partySwitchNameRaList = page.FindMulti(recognitionObject);

                if (partySwitchNameRaList == null || partySwitchNameRaList.Count <= 0)
                {
                    Logger.LogInformation("管理队伍界面文字识别失败");
                    break;
                }

                // 当前页存在则直接点击
                foreach (var textRegion in partySwitchNameRaList)
                {
                    if (matches(textRegion.Text))
                    {
                        page.ClickTo(textRegion.Right + textRegion.Width, textRegion.Bottom);
                        await Delay(200, ct);
                        await ConfirmParty(driver, ct, isInPartyViewUi, deferApplyToCaller);
                        Logger.LogInformation(deferApplyToCaller ? "队伍选择已确认，等待调用方开始挑战: {Text}" : "切换队伍成功: {Text}", textRegion.Text);

                        RunnerContext.Instance.ClearCombatScenes();
                        return true;
                    }
                }

                Region? lowest = partySwitchNameRaList.Where(r => r.X > 35 * _assetScale && r.X < 100 * _assetScale).OrderBy(r => r.Y).LastOrDefault();
                if (lowest == null)
                {
                    Logger.LogWarning("未识别到队伍列表的行序号，停止翻页");
                    break;
                }
                lowest.DrawSelf("底部的队伍");

                if (lowest.Y < 777 * _assetScale)   // 如果最底下是空队伍则不会有队伍名，以此判断是否已遍历完成
                {
                    Logger.LogInformation("已抵达最后一个队伍");
                    break;
                }

                // 点击下一页
                if (i == 0)
                {
                    // #ebe4d8 首次点一下第一个，防止第五个被点击过
                    page.ClickTo(600 * _assetScale, 200 * _assetScale);
                    await Task.Delay(300, ct); // 等待动画
                }

                page.ClickTo(regionOfInterest.X + regionOfInterest.Width / 2, lowest.Bottom); // 点击最下方队伍下移
                await Delay(400, ct);
            }
        }
        finally
        {
            VisionContext.Instance().DrawContent.ClearAll();
        }

        // 未找到
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
        await _returnMainUiTask.Start(ct);
        return false;
    }

    private async Task ConfirmParty(IUiDriver driver, CancellationToken ct, bool openedHere, bool deferApplyToCaller)
    {
        Task<bool> ClickConfirm(CancellationToken token, bool left)
        {
            token.ThrowIfCancellationRequested();
            UiOperation.Current?.Check();
            CheckAndSleep(0);
            using var image = CaptureToRectArea();
            var target = left ? UiTarget.PartyList : UiTarget.Party;
            var observed = NativeUiDriver.Read(image);
            UiOperation.Current?.Observe(observed, target, "pre-input");
            token.ThrowIfCancellationRequested();
            UiOperation.Current?.Check();
            if (!observed.Matches(target)) return Task.FromResult(false);
            var roi = new Rect(left ? 0 : image.Width - image.Width / 4, image.Height / 4,
                image.Width / 4, image.Height - image.Height / 4);
            return Task.FromResult(Bv.ClickWhiteConfirmButton(image, roi));
        }

        await UiRecovery.ConfirmPartyAsync(driver, token => ClickConfirm(token, true),
            token => ClickConfirm(token, false), ct, deferApplyToCaller, Logger);
        if (openedHere) await _returnMainUiTask.Start(ct);
    }
}
