using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.Helpers;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class TaskControlForegroundTests
{
    [Fact]
    public void ExactForegroundWindowNeedsNeitherProcessLookupNorActivation()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        using var window = new Window { Foreground = true };
        TaskControl.CheckAndActivateGameWindow(window);
        Assert.Equal(0, window.Activations);
    }

    [Fact]
    public void BackgroundRuntimeWithoutForegroundRequirementRemainsUntouched()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        using var window = new Window { RequiresForeground = false };
        TaskControl.CheckAndActivateGameWindow(window);
        Assert.Equal(0, window.Activations);
    }

    private sealed class Window : IGameWindow
    {
        public nint Handle => 123;
        // The fast path must not inspect processes, the viewport, or App services.
        public int ProcessId => throw new InvalidOperationException("Unexpected process lookup");
        public GameViewport Viewport => throw new InvalidOperationException("Unexpected viewport lookup");
        public bool IsAlive => true;
        public bool Foreground { get; init; }
        public bool IsForeground => Foreground;
        public bool IsMinimized => false;
        public bool RequiresForeground { get; init; } = true;
        public int Activations { get; private set; }
        public void Activate() => Activations++;
        public event EventHandler? ViewportChanged { add { } remove { } }
        public void Dispose() { }
    }
}
