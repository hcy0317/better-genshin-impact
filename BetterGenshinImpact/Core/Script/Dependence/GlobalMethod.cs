using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Script.Dependence;

public class GlobalMethod
{
    private static readonly byte[] ScriptEvidenceKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    public static async Task Sleep(int millisecondsTimeout)
    {
        await Task.Delay(millisecondsTimeout, ScriptAsyncLifetime.Current?.Token ?? CancellationContext.Instance.Cts.Token);
    }
    
    public static string GetVersion()
    {
        return Global.Version;
    }

    #region 键盘操作

    // 鼠标键（VK_LBUTTON、VK_RBUTTON、VK_MBUTTON、VK_XBUTTON1、VK_XBUTTON2）由输入通道转成对应的鼠标键，
    // 扩展键的处理也在通道内部完成
    public static void KeyDown(string key)
    {
        InputHub.Foreground.Keyboard.KeyDown(KeyBindingsSettingsPageViewModel.MappingKey(ToVk(key)));
    }

    public static void KeyUp(string key)
    {
        InputHub.Foreground.Keyboard.KeyUp(KeyBindingsSettingsPageViewModel.MappingKey(ToVk(key)));
    }

    public static void KeyPress(string key)
    {
        InputHub.Foreground.Keyboard.KeyPress(KeyBindingsSettingsPageViewModel.MappingKey(ToVk(key)));
    }

    private static User32.VK ToVk(string key)
    {
        try
        {
            return User32Helper.ToVk(key);
        }
        catch
        {
            throw new ArgumentException($"键盘编码必须是VirtualKeyCodes枚举中的值，当前传入的 {key} 不合法");
        }
    }

    #endregion 键盘操作

    #region 鼠标操作

    private static int _gameWidth = 1920;
    private static int _gameHeight = 1080;
    private static double _dpi = 1;

    public static void SetGameMetrics(int width, int height, double dpi = 1)
    {
        // 必须16:9 的分辨率
        if (width * 9 != height * 16)
        {
            throw new ArgumentException("游戏分辨率必须是16:9的分辨率");
        }

        _gameWidth = width;
        _gameHeight = height;
        _dpi = dpi;
    }

    public static double[] GetGameMetrics()
    {
        return [_gameWidth, _gameHeight, _dpi];
    }

    public static void MoveMouseBy(int x, int y)
    {
        var realDpi = TaskContext.Instance().DpiScale;
        x = (int)(x * realDpi / _dpi);
        y = (int)(y * realDpi / _dpi);
        InputHub.Foreground.Mouse.MoveMouseBy(x, y);
    }

    public static void MoveMouseTo(int x, int y)
    {
        if (x < 0 || x > _gameWidth || y < 0 || y > _gameHeight)
        {
            throw new ArgumentException("鼠标坐标超出游戏窗口范围");
        }

        GameCaptureRegion.GameRegionMove((size, s2) =>
        {
            var scale = 1920.0 / _gameWidth;
            return (x * scale * s2, y * scale * s2);
        });
    }

    public static void Click(int x, int y)
    {
        MoveMouseTo(x, y);
        LeftButtonClick();
    }

    public static void LeftButtonClick()
    {
        InputHub.Foreground.Mouse.LeftButtonDown().Sleep(60).LeftButtonUp();
    }

    public static void LeftButtonDown()
    {
        InputHub.Foreground.Mouse.LeftButtonDown();
    }

    public static void LeftButtonUp()
    {
        InputHub.Foreground.Mouse.LeftButtonUp();
    }

    public static void RightButtonClick()
    {
        InputHub.Foreground.Mouse.RightButtonDown().Sleep(60).RightButtonUp();
    }

    public static void RightButtonDown()
    {
        InputHub.Foreground.Mouse.RightButtonDown();
    }

    public static void RightButtonUp()
    {
        InputHub.Foreground.Mouse.RightButtonUp();
    }

    public static void MiddleButtonClick()
    {
        InputHub.Foreground.Mouse.MiddleButtonClick();
    }

    public static void MiddleButtonDown()
    {
        InputHub.Foreground.Mouse.MiddleButtonDown();
    }

    public static void MiddleButtonUp()
    {
        InputHub.Foreground.Mouse.MiddleButtonUp();
    }

    public static void VerticalScroll(int scrollAmountInClicks)
    {
        InputHub.Foreground.Mouse.VerticalScroll(scrollAmountInClicks);
    }

    #endregion 鼠标操作

    #region 识图操作

    public static ImageRegion CaptureGameRegion()
    {
        var region = TaskControl.CaptureToRectArea();
        region.BindInputToCurrentTask();
        return region;
    }

    public static void RequestEvidenceWindow(string request, string phase, string detail)
    {
        // 保留旧脚本调用签名，但任意脚本文本不是可信诊断字段，不得原样落盘。
        // 具体HTTP码由宿主HTTP桥提取；脚本事件只保留枚举、不可逆请求身份及白名单标量。
        try
        {
            var boundedRequest = request == null ? "" : request[..Math.Min(request.Length, 512)];
            var safeRequest = Guid.TryParse(request, out var id) ? "script:" + id.ToString("N")
                : "script:" + Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
                    ScriptEvidenceKey, System.Text.Encoding.UTF8.GetBytes(boundedRequest)));
            var safePhase = phase is "api-busy" or "http-failed" or "script-failed" or
                "commission-before" or "commission-result" or "commission-exit" ? phase : "script-failed";
            var fields = new System.Collections.Generic.Dictionary<string, string>();
            if (safePhase == "commission-result" && detail is { Length: <= 1024 })
            {
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(detail);
                    if (json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (json.RootElement.TryGetProperty("completed", out var completed) &&
                            completed.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                            fields["completed"] = completed.GetBoolean().ToString();
                        if (json.RootElement.TryGetProperty("index", out var index) &&
                            index.ValueKind == System.Text.Json.JsonValueKind.Number && index.TryGetInt32(out var slot) && slot is >= 0 and < 4)
                            fields["slotIndex"] = slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                catch (System.Text.Json.JsonException) { }
            }
            DiagnosticEvidenceScope.Current?.RequestLatestWindow(safeRequest, safePhase,
                "scriptDetail=redacted:untrusted-text; structuredFields=whitelisted-only", fields: fields);
        }
        catch { }
    }

    public static string[] GetAvatars()
    {
        var combatScenes = new CombatScenes().InitializeTeam(CaptureGameRegion());
        ReadOnlyCollection<Avatar> avatars = combatScenes.GetAvatars();
        return avatars.Count > 0
            ? avatars.Select(avatar => avatar.Name).ToArray()
            : [];
    }
    #endregion 识图操作

    #region 文字输入操作

    /// <summary>
    /// 通过本机剪贴板 + Ctrl+V 输入文字。
    /// <para>
    /// 注意：云原神网页版（WebSdk 后端）尚未适配。写本机剪贴板对云端无效，Ctrl+V 粘贴不到内容。
    /// 需要时可以在 <c>InputHub.Backend is WebSdkInputBackend web</c> 分支里，把"写本机剪贴板"换成
    /// <c>web.Sdk.Invoke("sendClipboard", text)</c> 后再发 Ctrl+V，或直接调用 <c>web.Sdk.Invoke("sendIme", text)</c>。
    /// 两种方式都未实测。
    /// </para>
    /// </summary>
    public static void InputText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // 保存当前剪贴板内容 保存恢复的功能不太正常
        // string? originalClipboardText = null;
        // UIDispatcherHelper.Invoke(() => originalClipboardText = Clipboard.GetText());
        try
        {
            // 将要输入的文本复制到剪贴板
            UIDispatcherHelper.Invoke(() => Clipboard.SetDataObject(text));

            // 通过剪贴板 + Ctrl+V 输入文字，只面向键鼠后端。
            // 手柄场景用不到文字输入，这里不做手柄适配；将来如果需要，请单独实现，不要依赖 Ctrl 的映射。
            InputHub.Foreground.Keyboard.KeyDown(VK.VK_CONTROL);
            Sleep(20);
            InputHub.Foreground.Keyboard.KeyPress(VK.VK_V);
            Sleep(20);
            InputHub.Foreground.Keyboard.KeyUp(VK.VK_CONTROL);

            // 等待一小段时间确保粘贴完成
            Sleep(100);
        }
        catch (Exception ex)
        {
            TaskControl.Logger.LogDebug("输入文本时发生错误: {Msg}", ex.Message);
        }
        finally
        {
            // // 恢复原始剪贴板内容
            // if (!string.IsNullOrEmpty(originalClipboardText))
            // {
            //     UIDispatcherHelper.Invoke(() => Clipboard.SetDataObject(originalClipboardText));
            // }
        }
    }

    #endregion 文字输入操作
}
