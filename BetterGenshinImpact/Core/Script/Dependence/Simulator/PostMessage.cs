using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Helpers;
using System;
using BetterGenshinImpact.GameTask;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Core.Script.Dependence.Simulator;

public class PostMessage
{
    private readonly TaskExecutionScope.Guard _taskGuard = TaskExecutionScope.Capture();
    private readonly PostMessageSimulator _postMessageSimulator;

    public PostMessage() : this(TaskContext.Instance().PostMessageSimulator) { }
    internal PostMessage(PostMessageSimulator? simulator) => _postMessageSimulator = simulator!;

    public void KeyDown(string key)
    {
        using var ownedTask = _taskGuard.Enter();
        _postMessageSimulator.KeyDownBackground(ToVk(key));
    }

    public void KeyUp(string key)
    {
        using var ownedTask = _taskGuard.Enter();
        _postMessageSimulator.KeyUpBackground(ToVk(key));
    }

    public void KeyPress(string key)
    {
        using var ownedTask = _taskGuard.Enter();
        _postMessageSimulator.KeyPressBackground(ToVk(key));
    }

    public void Click()
    {
        using var ownedTask = _taskGuard.Enter();
        _postMessageSimulator.LeftButtonClick();
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
