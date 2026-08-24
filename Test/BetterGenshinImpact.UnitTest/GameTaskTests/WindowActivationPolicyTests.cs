using BetterGenshinImpact.GameTask;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class WindowActivationPolicyTests
{
    [Fact]
    public void Execute_ShouldAttachForegroundAndTargetQueuesBeforeActivation()
    {
        var operations = new List<string>();

        WindowActivationPolicy.Execute(
            currentThreadId: 1,
            foregroundThreadId: 2,
            targetThreadId: 3,
            attachThreadInput: (source, target, attach) =>
            {
                operations.Add($"attach:{source}:{target}:{attach}");
                return true;
            },
            activate: () => operations.Add("activate"));

        Assert.Equal(
            [
                "attach:1:2:True",
                "attach:1:3:True",
                "activate",
                "attach:1:3:False",
                "attach:1:2:False"
            ],
            operations);
    }

    [Fact]
    public void Execute_ShouldAlwaysDetachQueuesWhenActivationFails()
    {
        var operations = new List<string>();

        Assert.Throws<InvalidOperationException>(() =>
            WindowActivationPolicy.Execute(
                currentThreadId: 1,
                foregroundThreadId: 2,
                targetThreadId: 3,
                attachThreadInput: (source, target, attach) =>
                {
                    operations.Add($"attach:{source}:{target}:{attach}");
                    return true;
                },
                activate: () => throw new InvalidOperationException("activation failed")));

        Assert.Equal("attach:1:3:False", operations[^2]);
        Assert.Equal("attach:1:2:False", operations[^1]);
    }

    [Fact]
    public void Execute_ShouldNotAttachTheSameQueueTwice()
    {
        var operations = new List<string>();

        WindowActivationPolicy.Execute(
            currentThreadId: 1,
            foregroundThreadId: 2,
            targetThreadId: 2,
            attachThreadInput: (source, target, attach) =>
            {
                operations.Add($"attach:{source}:{target}:{attach}");
                return true;
            },
            activate: () => operations.Add("activate"));

        Assert.Equal(
            ["attach:1:2:True", "activate", "attach:1:2:False"],
            operations);
    }
}
