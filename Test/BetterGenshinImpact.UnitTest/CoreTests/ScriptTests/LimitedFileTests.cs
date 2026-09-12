using BetterGenshinImpact.Core.Script.Dependence;
using Microsoft.ClearScript.V8;
using BetterGenshinImpact.GameTask;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public sealed class LimitedFileTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"bgi-limited-file-{Guid.NewGuid():N}");

    public LimitedFileTests()
    {
        Directory.CreateDirectory(_rootPath);
    }

    [Fact]
    public async Task ConfirmedRouteCompletionIsVisibleToJavascript()
    {
        var script = new AutoPathingScript(_rootPath, null, new LimitedFile(_rootPath), (_, _) => { },
            (_, _) => Task.FromResult(true), captureFailure: (_, _) => { });
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
        var completed = false;
        engine.AddHostObject("pathing", script);
        engine.AddHostObject("receive", (Action<bool>)(value => completed = value));
        await (Task)engine.Evaluate("(async () => { const result = await pathing.Run('{}'); receive(!!result && result.success === true); })()");

        Assert.True(completed, "The caller needs the native completion result rather than another global coordinate guess");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("file")]
    [InlineData("user-file")]
    public async Task AllRouteEntrypointsReturnTheSameCompletionContract(string entry)
    {
        File.WriteAllText(Path.Combine(_rootPath, "route.json"), "{}");
        var script = new AutoPathingScript(_rootPath, null, new LimitedFile(_rootPath), (_, _) => { },
            (_, _) => Task.FromResult(true), captureFailure: (_, _) => { });
        var result = await (entry switch
        {
            "file" => script.RunFile("route.json"),
            "user-file" => script.RunFileFromUser("route.json"),
            _ => script.Run("{}")
        });
        Assert.True(result.Success);
    }

    [Fact]
    public async Task AFailureReportedBeforeReturnCannotPublishCompletion()
    {
        using var owned = TaskExecutionScope.BeginOwned();
        var failure = new CombatNotFinishedException("battle not finished");
        var script = new AutoPathingScript(_rootPath, null, new LimitedFile(_rootPath), (_, _) => { },
            (_, _) => { TaskExecutionScope.Capture().Report(failure); return Task.FromResult(true); },
            captureFailure: (_, _) => { });
        Assert.Same(failure, await Record.ExceptionAsync(() => script.Run("{}")));
    }

    [Fact]
    public async Task NativeCancellationNeverPublishesCompletion()
    {
        var failure = new OperationCanceledException("cancelled");
        var script = new AutoPathingScript(_rootPath, null, new LimitedFile(_rootPath), (_, _) => { },
            (_, _) => Task.FromException<bool>(failure), captureFailure: (_, _) => { });
        Assert.Same(failure, await Record.ExceptionAsync(() => script.Run("{}")));
    }

    [Fact]
    public void ReadTextSyncOrThrowReturnsFileContent()
    {
        File.WriteAllText(Path.Combine(_rootPath, "route.json"), "{\"name\":\"test\"}");

        var content = new LimitedFile(_rootPath).ReadTextSyncOrThrow("route.json");

        Assert.Equal("{\"name\":\"test\"}", content);
    }

    [Fact]
    public void ReadTextSyncOrThrowPreservesMissingFileException()
    {
        var exception = Assert.Throws<FileNotFoundException>(() =>
            new LimitedFile(_rootPath).ReadTextSyncOrThrow("missing.json"));

        Assert.EndsWith("missing.json", exception.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutoPathingRunFilePreservesMissingFileException()
    {
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new AutoPathingScript(_rootPath, config: null, new LimitedFile(_rootPath), (_, _) => { })
                .RunFile("missing-route.json"));

        Assert.EndsWith("missing-route.json", exception.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutoPathingRunFileFromUserPreservesMissingFileException()
    {
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new AutoPathingScript(_rootPath, config: null, new LimitedFile(_rootPath), (_, _) => { })
                .RunFileFromUser("missing-user-route.json"));

        Assert.EndsWith("missing-user-route.json", exception.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutoPathingRunFileDoesNotReportExecutionFailureAsReadFailure()
    {
        File.WriteAllText(Path.Combine(_rootPath, "invalid-route.json"), "not-json");
        var failureMessages = new List<string>();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new AutoPathingScript(
                    _rootPath,
                    config: null,
                    new LimitedFile(_rootPath),
                    (message, _) => failureMessages.Add(message),
                    captureFailure: (_, _) => { })
                .RunFile("invalid-route.json"));

        Assert.Equal(["执行地图追踪时候发生错误"], failureMessages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScriptRouteResolvesOnlyWhenNativeExecutionReportsCompletion(bool completed)
    {
        var script = new AutoPathingScript(_rootPath, null, new LimitedFile(_rootPath), (_, _) => { },
            (_, _) => Task.FromResult(completed), captureFailure: (_, _) => { });
        const string route = """{"info":{"name":"completion-contract"},"positions":[]}""";
        if (completed) await script.Run(route);
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => script.Run(route));
            Assert.Contains("未完整完成", error.Message);
        }
    }

    public void Dispose()
    {
        Directory.Delete(_rootPath, recursive: true);
    }
}
