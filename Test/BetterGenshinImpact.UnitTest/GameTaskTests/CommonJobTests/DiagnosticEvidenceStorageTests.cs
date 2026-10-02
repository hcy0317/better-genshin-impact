using BetterGenshinImpact.GameTask.Common;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class DiagnosticEvidenceStorageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ErrorReclaimsLowerPriorityMetadataButKeepsTheSharedImage(int lowerPriority)
    {
        var lower = (DiagnosticEvidencePriority)lowerPriority;
        using var fixture = new EvidenceDirectory();
        var run = Guid.NewGuid();
        const long budget = 32768;
        var evicted = new List<int>();
        using var storage = new DiagnosticEvidenceStorage(fixture.Path, run, budget, onEvicted: (sequence, _) => evicted.Add(sequence));
        using var image = new Mat(2, 2, MatType.CV_8UC3, Scalar.Black);
        var stamp = new CaptureFrameSource().Next();
        await storage.WriteAsync(new(run, 1, "earlier-error", "terminal", stamp, "preserve"), image);
        var sequence = 2;
        for (; sequence < 100; sequence++)
        {
            try { await storage.WriteAsync(new(run, sequence, "lower-" + sequence, "sample", stamp,
                new string('x', 1024), Priority: lower), image); }
            catch (DiagnosticEvidenceBudgetException) { break; }
        }
        Assert.InRange(sequence, 3, 99);
        await storage.WriteAsync(new(run, ++sequence, "incoming-error", "combat-terminal", stamp, new string('y', 14000)), image);
        await storage.CompleteAsync(new { });
        var directory = System.IO.Path.Combine(fixture.Path, run.ToString("N"));
        Assert.True(File.Exists(System.IO.Path.Combine(directory, "evidence-0001.json")));
        Assert.False(File.Exists(System.IO.Path.Combine(directory, "evidence-0002.json")));
        Assert.Contains(2, evicted);
        Assert.DoesNotContain(1, evicted);
        Assert.Single(Directory.GetFiles(directory, "*.png"));
        var document = Newtonsoft.Json.Linq.JObject.Parse(await File.ReadAllTextAsync(
            System.IO.Path.Combine(directory, $"evidence-{sequence:D4}.json")));
        Assert.Equal("evidence-0001.png", (string?)document["ImageFile"]);
        Assert.True(File.Exists(System.IO.Path.Combine(directory, (string)document["ImageFile"]!)));
        Assert.InRange(Directory.GetFiles(directory).Sum(path => new FileInfo(path).Length), 1, budget);
    }

    [Fact]
    public async Task ANewFaultReclaimsRoutineEvidenceWithoutDeletingEarlierFaults()
    {
        using var fixture = new EvidenceDirectory();
        var run = Guid.NewGuid();
        const long budget = 32768;
        using var storage = new DiagnosticEvidenceStorage(fixture.Path, run, budget);
        using var image = new Mat(2, 2, MatType.CV_8UC3, Scalar.Black);
        var source = new CaptureFrameSource();
        var sequence = 1;
        for (; sequence < 100; sequence++)
        {
            try { await storage.WriteAsync(new(run, sequence, "routine", "ordinary", source.Next(), new string('x', 1024)), image); }
            catch (DiagnosticEvidenceBudgetException) { break; }
        }
        var firstFault = ++sequence;
        await storage.WriteAsync(new(run, sequence, "first-fault", "terminal", source.Next(), "first fault"), image);
        var laterFault = ++sequence;
        var anchor = source.Next();
        await storage.WriteAsync(new(run, sequence, "later-fault", "deadline", anchor, new string('y', 10000),
            Window: new(Guid.NewGuid(), anchor, 0)), image);
        await storage.CompleteAsync(new { });
        var directory = System.IO.Path.Combine(fixture.Path, run.ToString("N"));
        Assert.True(File.Exists(System.IO.Path.Combine(directory, $"evidence-{firstFault:D4}.json")));
        Assert.True(File.Exists(System.IO.Path.Combine(directory, $"evidence-{laterFault:D4}.json")));
        Assert.False(File.Exists(System.IO.Path.Combine(directory, "evidence-0001.json")));
        Assert.InRange(Directory.GetFiles(directory).Sum(path => new FileInfo(path).Length), 1, budget);
    }

    [Fact]
    public async Task OverlappingIncidentsReusePixelsAndKeepIndependentMetadata()
    {
        using var fixture = new EvidenceDirectory();
        var run = Guid.NewGuid();
        using var storage = new DiagnosticEvidenceStorage(fixture.Path, run);
        using var image = new Mat(2, 2, MatType.CV_8UC3, Scalar.Black);
        var stamp = new CaptureFrameSource().Next();
        await storage.WriteAsync(new(run, 1, "incident-a", "deadline", stamp, "first"), image);
        await storage.WriteAsync(new(run, 2, "incident-b", "deadline", stamp, "overlap"), image);
        var directory = System.IO.Path.Combine(fixture.Path, run.ToString("N"));
        Assert.Single(Directory.GetFiles(directory, "*.png"));
        var second = Newtonsoft.Json.Linq.JObject.Parse(await File.ReadAllTextAsync(System.IO.Path.Combine(directory, "evidence-0002.json")));
        Assert.Equal("evidence-0001.png", (string?)second["ImageFile"]);
        Assert.Equal("incident-b", (string?)second["Request"]);
        Assert.NotNull(second["WrittenAt"]);
        Assert.NotNull(second["ObservedAt"]);
        var identity = Newtonsoft.Json.Linq.JObject.Parse(await File.ReadAllTextAsync(System.IO.Path.Combine(directory, "run.json")));
        Assert.NotNull(identity["Build"]?["ModuleVersionId"]);
        Assert.Equal(10L * 1024 * 1024 * 1024, DiagnosticEvidenceStorage.DefaultMaximumBytes);
    }

    [Fact]
    public void StartupRemovesOnlyExpiredRecognizedInactiveRunsOldestFirst()
    {
        using var fixture = new EvidenceDirectory();
        var now = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        string Run(int ageHours, string? extra = null)
        {
            var path = System.IO.Path.Combine(fixture.Path, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            File.WriteAllText(System.IO.Path.Combine(path, "evidence-0001.json"), "{}");
            if (extra != null) File.WriteAllText(System.IO.Path.Combine(path, extra), "preserve");
            foreach (var file in Directory.GetFiles(path)) File.SetLastWriteTimeUtc(file, now.UtcDateTime.AddHours(-ageHours));
            return path;
        }
        var older = Run(100);
        var expired = Run(73);
        var recent = Run(71);
        var boundary = Run(72);
        var unknown = Run(100, "user-notes.txt");
        var active = Run(100, ".active");
        using var lease = new FileStream(System.IO.Path.Combine(active, ".active"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var events = new List<string>();
        using var storage = new DiagnosticEvidenceStorage(fixture.Path, Guid.NewGuid(), clock: clock, diagnostic: events.Add);
        storage.Initialize();
        Assert.False(Directory.Exists(older));
        Assert.False(Directory.Exists(expired));
        Assert.All(new[] { recent, boundary, unknown, active }, path => Assert.True(Directory.Exists(path)));
        Assert.Contains(events, item => item.Contains(System.IO.Path.GetFileName(unknown)) && item.StartsWith("EVIDENCE_CLEANUP_SKIPPED"));
        Assert.Equal(new[] { System.IO.Path.GetFileName(older), System.IO.Path.GetFileName(expired) },
            events.Where(item => item.StartsWith("EVIDENCE_CLEANUP_REMOVED run=")).Select(item => item.Split('=')[1]));
        var laterExpired = Run(100);
        storage.Initialize();
        Assert.True(Directory.Exists(laterExpired)); // 同一次运行不重复清理。
    }

    [Fact]
    public void StartupRejectsLinkedRootAndPreservesLinkedRunContents()
    {
        using var fixture = new EvidenceDirectory();
        using var outside = new EvidenceDirectory();
        var preserved = System.IO.Path.Combine(outside.Path, "evidence-0001.json");
        File.WriteAllText(preserved, "preserve linked evidence");
        File.SetLastWriteTimeUtc(preserved, DateTime.UtcNow.AddDays(-5));
        var linked = System.IO.Path.Combine(fixture.Path, Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            // Windows符号链接要求额外特权；junction不提权。只启动无profile/无窗口的系统shell创建夹具。
            var start = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                         "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:BGI_TEST_LINK -Target $env:BGI_TEST_TARGET | Out-Null" })
                start.ArgumentList.Add(argument);
            start.Environment["BGI_TEST_LINK"] = linked;
            start.Environment["BGI_TEST_TARGET"] = outside.Path;
            using var process = System.Diagnostics.Process.Start(start)!;
            if (!process.WaitForExit(10000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Junction fixture creation timed out"); }
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        }
        else Directory.CreateSymbolicLink(linked, outside.Path);
        try
        {
            using var rootLinkStorage = new DiagnosticEvidenceStorage(linked, Guid.NewGuid());
            Assert.Throws<IOException>(rootLinkStorage.Initialize);
            using var storage = new DiagnosticEvidenceStorage(fixture.Path, Guid.NewGuid());
            storage.Initialize();
            Assert.Equal("preserve linked evidence", File.ReadAllText(preserved));
            Assert.True(Directory.Exists(linked));
        }
        finally { Directory.Delete(linked, recursive: false); }
    }

    [Fact]
    public async Task PartialPairFailureRemovesOnlyItsOwnImageAndPreservesCollidingMetadata()
    {
        using var fixture = new EvidenceDirectory();
        var run = Guid.NewGuid();
        using var storage = new DiagnosticEvidenceStorage(fixture.Path, run);
        storage.Initialize();
        var name = System.IO.Path.Combine(fixture.Path, run.ToString("N"), "evidence-0001");
        await File.WriteAllTextAsync(name + ".json", "existing metadata");
        using var image = new Mat(2, 2, MatType.CV_8UC3, Scalar.Black);
        await Assert.ThrowsAsync<IOException>(() => storage.WriteAsync(new(run, 1, "r", "p", new CaptureFrameSource().Next(), ""), image));
        Assert.False(File.Exists(name + ".png"));
        Assert.Equal("existing metadata", await File.ReadAllTextAsync(name + ".json"));
    }

    [Fact]
    public async Task DiskBudgetIncludesImageMetadataAndSummaryWithoutExceedingTheLimit()
    {
        using var fixture = new EvidenceDirectory();
        var clock = new FakeTimeProvider();
        var run = Guid.NewGuid();
        using var storage = new DiagnosticEvidenceStorage(fixture.Path, run, maximumBytes: 4096, clock: clock);
        using var image = new Mat(2, 2, MatType.CV_8UC3, Scalar.Black);
        var stamp = new CaptureFrameSource(clock).Next();
        var written = 0;
        for (var i = 1; i <= 20; i++)
        {
            try { await storage.WriteAsync(new(run, i, "request", "phase", stamp, "fixture"), image); written++; }
            catch (DiagnosticEvidenceBudgetException) { break; }
        }
        Assert.InRange(written, 1, 19);
        await storage.CompleteAsync(new { accepted = written });
        var files = Directory.GetFiles(System.IO.Path.Combine(fixture.Path, run.ToString("N")));
        Assert.InRange(files.Sum(file => new FileInfo(file).Length), 1, 4096);
        Assert.Single(files.Where(file => file.EndsWith(".png"))); // 多条元数据引用同一源帧。
        Assert.Contains(files, file => file.EndsWith("summary.json"));
    }

    [Fact]
    public async Task OrdinaryBudgetExhaustionStillAllowsTerminalEvidenceWithinTheSameHardLimit()
    {
        using var fixture = new EvidenceDirectory();
        var run = Guid.NewGuid();
        const long maximumBytes = 16384;
        using var storage = new DiagnosticEvidenceStorage(fixture.Path, run, maximumBytes);
        using var image = new Mat(2, 2, MatType.CV_8UC3, Scalar.Black);
        var source = new CaptureFrameSource();
        var sequence = 1;
        for (; sequence < 100; sequence++)
        {
            try { await storage.WriteAsync(new(run, sequence, "ordinary", "return-main", source.Next(), "completed"), image); }
            catch (DiagnosticEvidenceBudgetException) { break; }
        }
        Assert.InRange(sequence, 2, 99);
        await storage.WriteAsync(new(run, sequence + 1, "battle", "combat-terminal", source.Next(), "unconfirmed"), image);
        await storage.CompleteAsync(new { terminal = sequence + 1 });
        var directory = System.IO.Path.Combine(fixture.Path, run.ToString("N"));
        Assert.True(File.Exists(System.IO.Path.Combine(directory, $"evidence-{sequence + 1:D4}.png")));
        Assert.InRange(Directory.GetFiles(directory).Sum(file => new FileInfo(file).Length), 1, maximumBytes);
    }

    private sealed class EvidenceDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bettergi-evidence-test-" + Guid.NewGuid().ToString("N"));
        internal EvidenceDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Path);
            var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(full).StartsWith("bettergi-evidence-test-"))
                throw new InvalidOperationException("Unexpected fixture root");
            Directory.Delete(full, recursive: true);
        }
    }
}
