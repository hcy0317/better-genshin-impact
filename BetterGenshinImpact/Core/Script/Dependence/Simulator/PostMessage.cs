using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.GameTask;
using System;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Core.Script.Dependence.Simulator;

/// <summary>
/// JS 脚本中的 new PostMessage()，走后台通道。
/// 每次调用时从 InputHub 取通道，后端切换后自动生效；网页版下与前台通道相同
/// </summary>
public class PostMessage
{
    private readonly TaskExecutionScope.Guard _taskGuard = TaskExecutionScope.Capture();
    private readonly IInputChannel? _channel;
    private IInputChannel Channel => _channel ?? InputHub.Background;
    public PostMessage() { }
    internal PostMessage(IInputChannel? channel) => _channel = channel;

    public void KeyDown(string key)
    {
        using var ownedTask = _taskGuard.Enter();
        Channel.Keyboard.KeyDown(ToVk(key));
    }

    public void KeyUp(string key)
    {
        using var ownedTask = _taskGuard.Enter();
        Channel.Keyboard.KeyUp(ToVk(key));
    }

    public void KeyPress(string key)
    {
        using var ownedTask = _taskGuard.Enter();
        Channel.Keyboard.KeyPress(ToVk(key));
    }

    public void Click()
    {
        using var ownedTask = _taskGuard.Enter();
        Channel.Mouse.LeftButtonClick();
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
}
