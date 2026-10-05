using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.QuickTeleport.Assets;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Localization;
using OpenCvSharp;
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;


namespace BetterGenshinImpact.GameTask.Common.BgiVision;

public enum GameUiCategory
{
    Unknown,
    Main,
    Talk,
    BigMap
}

internal enum ReviveUiState { None, FoodPrompt, FullPartyDefeat }

public static partial class Bv
{
    public static GameUiCategory WhichGameUi()
    {
        using var region = TaskControl.CaptureToRectArea();
        return WhichGameUi(region);
    }

    public static GameUiCategory WhichGameUi(ImageRegion region)
    {
        if (IsInTalkUi(region))
        {
            return GameUiCategory.Talk;
        }

        if (IsInBigMapUi(region))
        {
            return GameUiCategory.BigMap;
        }

        if (IsInMainUi(region))
        {
            return GameUiCategory.Main;
        }

        return GameUiCategory.Unknown;
    }

    public static GameUiCategory WhichGameUiForTriggers(ImageRegion region)
    {
        if (IsInTalkUi(region))
        {
            return GameUiCategory.Talk;
        }

        if (IsInBigMapUi(region))
        {
            return GameUiCategory.BigMap;
        }

        return GameUiCategory.Unknown;
    }


    /// <summary>
    /// 是否在主界面
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInMainUi(ImageRegion captureRa)
    {
        using var paimonMenu = captureRa.Find(
            ElementRecognition.Get("PaimonMenu", captureRa));
        if (paimonMenu.IsExist())
        {
            return !IsInRevivePrompt(captureRa);
        }

        // Notification badges can cover enough of the Paimon icon to make its
        // template miss. The friend-chat button is another stable main-HUD
        // element and prevents an already-loaded game from being treated as
        // the login door indefinitely.
        using var friendChat = captureRa.Find(
            ElementRecognition.Get("FriendChat", captureRa));
        return friendChat.IsExist() && !IsInRevivePrompt(captureRa);
    }

    /// <summary>
    /// 等待主界面加载完成
    /// </summary>
    /// <param name="ct"></param>
    /// <param name="retryTimes"></param>
    /// <returns></returns>
    public static async Task<bool> WaitForMainUi(CancellationToken ct, int retryTimes = 10)
    {
        for (var i = 0; i < retryTimes; i++)
        {
            await TaskControl.Delay(1000, ct);
            using var ra3 = TaskControl.CaptureToRectArea();
            if (IsInMainUi(ra3))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 是否在秘境中
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInDomain(ImageRegion captureRa)
    {
        return IsInDomainIncludingRevivePrompt(captureRa) && !IsInRevivePrompt(captureRa);
    }

    /// <summary>
    /// 是否在秘境中，复苏界面存在时也保持秘境判断。
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInDomainIncludingRevivePrompt(ImageRegion captureRa)
    {
        using var matchRegion = captureRa.Find(ElementRecognition.Get("InDomain", captureRa));
        if (matchRegion.IsEmpty())
        {
            return false;
        }

        bool IsWhite(int r, int g, int b)
        {
            return (r >= 240 && r <= 255) &&
                   (g >= 240 && g <= 255) &&
                   (b >= 240 && b <= 255);
        }

        // 若全部为白色则视为不在秘境中
        var samplePoints = new[]
        {
            new Point(matchRegion.X + matchRegion.Width / 2, matchRegion.Y + matchRegion.Height / 2),
            new Point(matchRegion.X + matchRegion.Width / 4, matchRegion.Y + matchRegion.Height / 4),
            new Point(matchRegion.X + matchRegion.Width * 3 / 4, matchRegion.Y + matchRegion.Height / 4),
            new Point(matchRegion.X + matchRegion.Width / 4, matchRegion.Y + matchRegion.Height * 3 / 4),
            new Point(matchRegion.X + matchRegion.Width * 3 / 4, matchRegion.Y + matchRegion.Height * 3 / 4)
        };

        bool allWhite = samplePoints.All(pt =>
        {
            var v = captureRa.SrcMat.At<Vec3b>(pt.Y, pt.X);
            return IsWhite(v.Item2, v.Item1, v.Item0);
        });

        return !allWhite;
    }

    /// <summary>
    /// 在任意可以关闭的UI界面（识别关闭按钮）
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInAnyClosableUi(ImageRegion captureRa)
    {
        using var ra = captureRa.Find(RecognitionAssets.Get("QuickTeleport", "MapCloseButton", captureRa));
        return ra.IsExist();
    }

    /// <summary>
    /// 是否在队伍选择界面
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInPartyViewUi(ImageRegion captureRa)
    {
        using var ra = captureRa.Find(ElementRecognition.Get("PartyBtnChooseView", captureRa));
        return ra.IsExist();
    }

    /// <summary>
    /// 等待队伍选择界面加载完成
    /// </summary>
    /// <param name="ct"></param>
    /// <param name="retryTimes"></param>
    /// <returns></returns>
    public static async Task<bool> WaitForPartyViewUi(CancellationToken ct, int retryTimes = 5)
    {
        return await NewRetry.WaitForAction(() =>
        {
            using var ra = TaskControl.CaptureToRectArea();
            return IsInPartyViewUi(ra);
        }, ct, retryTimes);
    }

    /// <summary>
    /// 是否在大地图界面
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInBigMapUi(ImageRegion captureRa)
    {
        // 派蒙菜单的侧栏可能命中缩放模板；先用菜单返回按钮排除这个已知重叠页。
        using var menuBack = captureRa.Find(RecognitionAssets.Get("UseRedeemCode", "MenuBack", captureRa));
        if (menuBack.IsExist()) return false;
        // 地图重叠地点候选列表会隐藏关闭按钮，但仍保留左侧缩放控件。
        using var scaleRa = captureRa.Find(RecognitionAssets.Get("QuickTeleport", "MapScaleButton", captureRa));
        if (scaleRa.IsExist())
        {
            return true;
        }

        // 派蒙菜单也可能命中设置图案；此回退分支仍需独立关闭按钮佐证。
        using var closeRa = captureRa.Find(RecognitionAssets.Get("QuickTeleport", "MapCloseButton", captureRa));
        if (!closeRa.IsExist()) return false;
        using var settingsRa = captureRa.Find(RecognitionAssets.Get("QuickTeleport", "MapSettingsButton", captureRa));
        return settingsRa.IsExist();
    }

    /// <summary>
    /// 大地图界面是否在地底
    /// 鼠标悬浮在地下图标或者处于切换动画的时候可能会误识别
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool BigMapIsUnderground(ImageRegion captureRa)
    {
        using var ra = captureRa.Find(RecognitionAssets.Get("QuickTeleport", "MapUndergroundSwitchButton", captureRa));
        return ra.IsExist();
    }

    public static double GetBigMapScale(ImageRegion region)
    {
        using var scaleRa = region.Find(RecognitionAssets.Get("QuickTeleport", "MapScaleButton", region));
        if (scaleRa.IsEmpty())
        {
            throw new Exception("当前未处于大地图界面，不能使用GetBigMapScale方法");
        }

        // 原先这里的起止区间和config里写死的值差1
        var start = TaskContext.Instance().Config.TpConfig.ZoomStartY;
        var end = TaskContext.Instance().Config.TpConfig.ZoomEndY;
        var cur = (scaleRa.Y + scaleRa.Height / 2.0) * TaskContext.Instance().SystemInfo.ZoomOutMax1080PRatio; // 转换到1080p坐标系,主要是小于1080p的情况

        return (end * 1.0 - cur) / (end - start);
    }

    public static MotionStatus GetMotionStatus(ImageRegion captureRa)
    {
        using var spaceRa = captureRa.Find(ElementRecognition.Get("SpaceKey", captureRa));
        var spaceExist = spaceRa.IsExist();
        using var xRa = captureRa.Find(ElementRecognition.Get("XKey", captureRa));
        var xExist = xRa.IsExist();
        if (spaceExist)
        {
            return xExist ? MotionStatus.Climb : MotionStatus.Fly;
        }
        else
        {
            return MotionStatus.Normal;
        }
    }

    /// <summary>
    /// 是否出现复苏提示
    /// </summary>
    /// <param name="region"></param>
    /// <returns></returns>
    internal static bool IsInRevivePrompt(ImageRegion region)
        => ReadReviveState(region) != ReviveUiState.None;

    internal static ReviveUiState ClassifyReviveEvidence(bool confirmation, bool title, bool bottomButton)
        => confirmation ? (title ? ReviveUiState.FoodPrompt : ReviveUiState.None)
            : bottomButton ? ReviveUiState.FullPartyDefeat : ReviveUiState.None;

    internal static ReviveUiState ReadReviveState(ImageRegion region)
        => CreateReviveDetector(region).Read(region);

    internal static ReviveUiObservation ReadReviveObservation(ImageRegion region)
        => CreateReviveDetector(region).Observe(region);

    internal static bool IsCombatHud(ImageRegion region) => CreateReviveDetector(region).IsCombatHud(region);

    private static ReviveUiDetector CreateReviveDetector(ImageRegion region)
    {
        var culture = new CultureInfo(TaskContext.Instance().Config.OtherConfig.GameCultureInfoName);
        var localizer = App.GetService<IStringLocalizer<BvResxHelper>>() ?? throw new Exception();
        var revival = localizer.WithCultureGet(culture, "复苏");
        var foodTitle = localizer.WithCultureGet(culture, "使用道具复苏角色");
        return new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", region),
            static () => Core.Recognition.OCR.OcrFactory.Paddle, revival, foodTitle);
    }

    /// <summary>
    /// 是否出现全队死亡和复苏提示
    /// </summary>
    /// <param name="region"></param>
    /// <returns></returns>
    public static bool ClickIfInReviveModal(ImageRegion region, CancellationToken ct = default)
    {
        if (ReadReviveState(region) != ReviveUiState.FullPartyDefeat) return false;
        TaskControl.CheckAndSleep(0);
        ct.ThrowIfCancellationRequested();
        using var current = TaskControl.CaptureToRectArea();
        var observed = ReadReviveObservation(current);
        var bounds = observed.ButtonBounds;
        if (observed.State != ReviveUiState.FullPartyDefeat || !observed.DefeatOverlay ||
            !current.FrameStamp.IsAfter(region.FrameStamp) ||
            bounds.Width <= 0 || bounds.Height <= 0 || bounds.X < current.Width / 4 ||
            bounds.Right > current.Width * 3 / 4 || bounds.Y < current.Height * 2 / 3 || bounds.Bottom > current.Height)
            return false;
        using var body = current.DeriveCrop(bounds);
        void Admit()
        {
            ct.ThrowIfCancellationRequested();
            Common.Ui.UiOperation.Current?.Check();
            if (!current.FrameStamp.IsFresh(TimeProvider.System, Common.Ui.UiSnapshot.RecoveryMaximumAge))
                throw new InvalidOperationException("Revive source expired before native input.");
        }
        Common.Ui.DomainTipClick.Run(Admit, body.Move,
            () => Core.Input.InputHub.Foreground.Mouse.LeftButtonDown(),
            () => Core.Input.InputHub.Foreground.Mouse.LeftButtonUp(), Thread.Sleep);
        return true;
    }

    internal static bool IsReviveFoodTitle(string? text, string? localizedRevive, string? localizedFoodTitle)
        => IsReviveText(text, localizedRevive) || IsReviveText(text, localizedFoodTitle);

    internal static bool IsReviveText(string? text, string? localizedRevive)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(localizedRevive))
        {
            return false;
        }

        static string Normalize(string value)
        {
            return string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        }

        return Normalize(text).Equals(Normalize(localizedRevive), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsReviveRecoveryConfirmed(bool clicked, bool returnedToMainUi)
    {
        return clicked && returnedToMainUi;
    }

    /// <summary>
    /// 当前角色是否低血量
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool CurrentAvatarIsLowHp(ImageRegion captureRa)
        => ObserveCurrentAvatarLowHp(captureRa) == true;

    internal static bool CurrentAvatarIsLowHp(ImageRegion captureRa, double assetScale)
        => ObserveCurrentAvatarLowHp(captureRa, assetScale) == true;

    internal static bool? ObserveCurrentAvatarLowHp(ImageRegion captureRa)
        => ObserveCurrentAvatarLowHp(captureRa, Math.Min(1, captureRa.Width / 1920d));

    internal static bool? ObserveCurrentAvatarLowHp(ImageRegion captureRa, double assetScale)
    {
        if (CriticalHealthHud.IsKnownLowHp(captureRa)) return true;
        var mat = captureRa.SrcMat;
        if (!double.IsFinite(assetScale) || assetScale <= 0 || mat.Empty() || mat.Type() != MatType.CV_8UC3) return null;
        var x = (int)(808 * assetScale);
        var y = (int)(1010 * assetScale);
        var rx = Math.Max(1, (int)Math.Round(3 * assetScale));
        var ry = Math.Max(1, (int)Math.Round(2 * assetScale));
        if (x - rx < 0 || y - ry < 0 || x + rx >= mat.Width || y + ry >= mat.Height) return null;
        var red = 0;
        var green = 0;
        for (var row = y - ry; row <= y + ry; row++)
        for (var column = x - rx; column <= x + rx; column++)
        {
            var p = mat.At<Vec3b>(row, column);
            if (p.Item2 >= 180 && p.Item2 > p.Item1 * 1.5 && p.Item2 > p.Item0 * 1.5) red++;
            else if (p.Item1 >= 130 && p.Item1 > p.Item2 * 1.15 && p.Item1 > p.Item0 * 1.15) green++;
        }
        // 只解释有颜色证据的生命条；空白/遮挡不是健康，混色边界也不强行二选一。
        var pixels = (rx * 2 + 1) * (ry * 2 + 1);
        if (red + green < (pixels + 1) / 2) return null;
        if (red >= (red + green) * .8) return true;
        if (green >= (red + green) * .8) return false;
        return null;
    }

    /// <summary>
    /// 在空月祝福界面
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInBlessingOfTheWelkinMoon(ImageRegion captureRa)
    {
        var ra = captureRa;

        using var girlRa = ra.Find(RecognitionAssets.Get("GameLoading", "GirlMoon", ra));
        if (girlRa.IsExist())
        {
            return true;
        }

        using var moonRa = ra.Find(RecognitionAssets.Get("GameLoading", "WelkinMoon", ra));
        return moonRa.IsExist();
    }

    /// <summary>
    /// 是否在对话界面
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInTalkUi(ImageRegion captureRa)
    {
        using var ra = captureRa.Find(RecognitionAssets.Get("AutoSkip", "DisabledUiButton", captureRa.Width, captureRa.Height));
        return ra.IsExist();
    }

    /// <summary>
    /// 等到对话界面加载完成
    /// </summary>
    /// <param name="ct"></param>
    /// <param name="retryTimes"></param>
    /// <returns></returns>
    public static async Task<bool> WaitAndSkipForTalkUi(CancellationToken ct, int retryTimes = 5)
    {
        return await NewRetry.WaitForAction(() =>
        {
            using var ra = TaskControl.CaptureToRectArea();
            return IsInTalkUi(ra);
        }, ct, retryTimes, 500);
    }

    /// <summary>
    /// 是否存在提示框/确认框
    /// 黑白款都能识别
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInPromptDialog(ImageRegion captureRa)
    {
        var template = ElementRecognition.Get("PromptDialogLeftBottomStar", captureRa);
        using var left = captureRa.Find(template);
        if (!left.IsExist()) return false;
        // 选队背景的单个闪光也会命中左下角标；弹窗还须有居中面板的对称右下角。
        var padding = Math.Max(3, (int)Math.Ceiling(6 * captureRa.Height / 1080d));
        var x = captureRa.Width - left.Right - padding;
        var y = left.Top - padding;
        var bounds = new Rect(Math.Max(0, x), Math.Max(0, y), left.Width + padding * 2, left.Height + padding * 2);
        bounds = bounds.Intersect(new Rect(0, 0, captureRa.Width, captureRa.Height));
        if (bounds.Width < left.Width || bounds.Height < left.Height) return false;
        using var mirrored = new Mat();
        using var mirroredGray = new Mat();
        Cv2.Flip(template.TemplateImageMat!, mirrored, FlipMode.Y);
        Cv2.Flip(template.TemplateImageGreyMat!, mirroredGray, FlipMode.Y);
        var rightTemplate = template.Clone();
        rightTemplate.TemplateImageMat = mirrored;
        rightTemplate.TemplateImageGreyMat = mirroredGray;
        rightTemplate.RegionOfInterest = bounds;
        rightTemplate.ReferenceBoundingBox = null;
        using var right = captureRa.Find(rightTemplate);
        return right.IsExist();
    }
    
    
        
    /// <summary>
    /// 通过 OCR 识别当前角色的 UID
    /// </summary>
    /// <returns>UID 数字，如果识别失败则返回 0</returns>
    public static int Uid()
    {
        try
        {
            var x = 1683;
            var y = 1051;
            var width = 234;
            var height = 28;
            var scale = TaskContext.Instance().SystemInfo.AssetScale;
            
            x = (int)Math.Round(x * scale);
            y = (int)Math.Round(y * scale);
            width = (int)Math.Round(width * scale);
            height = (int)Math.Round(height * scale);
            
            using var region = TaskControl.CaptureToRectArea();
            var recognitionObjectOcr = RecognitionObject.Ocr(x, y, width, height);
            var res = region.Find(recognitionObjectOcr);
            if (res?.Text == null) return 0;
            var matches = Regex.Matches(res.Text, @"\d+");
            if (matches.Count == 0) return 0;
            var numberStr = string.Join("", matches.Select(m => m.Value));
            return int.TryParse(numberStr, out var uid) ? uid : 0;
        }
        catch (Exception e)
        {
            TaskControl.Logger.LogError(e, "OCR 识别 UID 异常");
            return 0;
        }
    }
}

public enum MotionStatus
{
    Normal, // 正常
    Fly, // 飞行
    Climb, // 攀爬
    Unknown, // 没有足够的姿态证据，不能授权战斗移动
}
