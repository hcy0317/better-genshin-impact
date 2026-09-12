using System.Text.Json;
using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactHostRequestReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bgi-artifact-host-" + Guid.NewGuid());

    [Fact]
    public async Task ExpiredUnclaimedRequestDoesNotAttemptAGameFailureScreenshot()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            version = 1, kind = "artifact-analysis", uid = "fixture", jobId = "expired-job", operation = "ANALYZE",
            createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10), expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        }));
        var screenshots = 0;
        using var service = new ArtifactHostService(null!, NullLogger<ArtifactHostService>.Instance,
            _root, (_, _) => screenshots++);
        await service.RunObservedAsync(path);

        Assert.Equal(0, screenshots);
        Assert.True(File.Exists(path), "The rejected request remains available for audit instead of being deleted");
    }

    [Theory]
    [InlineData("missing-expiry")]
    [InlineData("missing-binding")]
    public async Task MalformedRequestsAreNotReclassifiedAsBenignExpiry(string fault)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid() + ".json");
        var request = new Dictionary<string, object>
        {
            ["version"] = 1, ["kind"] = "artifact-analysis", ["uid"] = "fixture", ["jobId"] = "invalid-job",
            ["operation"] = fault == "missing-binding" ? "EXECUTE_EQUIP_PLAN" : "ANALYZE",
            ["createdAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-10),
            ["expiresAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        if (fault == "missing-expiry") request.Remove("expiresAtUtc");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ArtifactHostRequestReader(_root).ReadAsync(path, default, allowExpiredClaimed: true));
    }

    [Fact]
    public async Task RecoveryFlagCannotAdmitAnExpiredUnclaimedRequest()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            version = 1, kind = "artifact-analysis", uid = "fixture", jobId = "expired-job", operation = "ANALYZE",
            createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10), expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        }));
        await Assert.ThrowsAsync<ArtifactHostRequestExpiredException>(() =>
            new ArtifactHostRequestReader(_root).ReadAsync(path, default, allowExpiredClaimed: true));
    }

    [Fact]
    public async Task CancelledObservedRequestDoesNotCaptureOrExecute()
    {
        var screenshots = 0;
        using var service = new ArtifactHostService(null!, NullLogger<ArtifactHostService>.Instance,
            _root, (_, _) => screenshots++);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await service.RunObservedAsync(Path.Combine(_root, Guid.NewGuid() + ".json"), cts.Token);
        Assert.Equal(0, screenshots);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void CommandLine_ParsesArtifactHostRequestPathExactly()
    {
        var options = CommandLineOptions.Parse(
            ["BetterGI.exe", "--artifact-host-request", @"C:\requests\token.json"]);

        Assert.Equal(CommandLineAction.ArtifactHost, options.Action);
        Assert.Equal(@"C:\requests\token.json", options.ArtifactHostRequestPath);
        Assert.True(options.ShouldDeferGameStart);
    }

    [Fact]
    public async Task Reader_AcceptsValidRequestInsideControlledRoot()
    {
        Directory.CreateDirectory(_root);
        var token = Guid.NewGuid().ToString();
        var path = Path.Combine(_root, token + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            version = 1,
            kind = "artifact-analysis",
            uid = "102550550",
            jobId = "job-1",
            operation = "EXECUTE_LOCK_PLAN",
            createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(4),
            sourceArtifactCount = 1,
            targets = new[]
            {
                new { scanIndex = 0, expectedFingerprint = new string('a', 64), expectedLocked = false }
            }
        }));

        var launch = await new ArtifactHostRequestReader(_root).ReadAsync(path, CancellationToken.None);

        Assert.Equal(token, launch.RequestToken);
        Assert.Equal(ArtifactHostOperation.ExecuteLockPlan, launch.Request.Operation);
        Assert.Equal("job-1", launch.Request.JobId);
    }

    [Fact]
    public async Task Reader_RejectsPathsOutsideRootAndExpiredRequests()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(outside, "{}");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ArtifactHostRequestReader(_root).ReadAsync(outside, CancellationToken.None));

        var expired = Path.Combine(_root, Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(expired, JsonSerializer.Serialize(new
        {
            version = 1,
            kind = "artifact-analysis",
            uid = "102550550",
            jobId = "job-1",
            operation = "ANALYZE",
            createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        }));
        await Assert.ThrowsAsync<ArtifactHostRequestExpiredException>(() =>
            new ArtifactHostRequestReader(_root).ReadAsync(expired, CancellationToken.None));

        File.Delete(outside);
    }

    [Fact]
    public async Task Reader_AllowsExpiredRequestOnlyFromClaimedRecoveryDirectory()
    {
        var consumed = Path.Combine(_root, "consumed");
        Directory.CreateDirectory(consumed);
        var path = Path.Combine(consumed, Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            version = 1,
            kind = "artifact-analysis",
            uid = "102550550",
            jobId = "job-recovery",
            operation = "ANALYZE",
            createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        }));
        var reader = new ArtifactHostRequestReader(_root);

        await Assert.ThrowsAsync<ArtifactHostRequestExpiredException>(() =>
            reader.ReadAsync(path, CancellationToken.None));
        var recovered = await reader.ReadAsync(
            path, CancellationToken.None, allowExpiredClaimed: true);

        Assert.True(recovered.Recovery);
        Assert.Equal("job-recovery", recovered.Request.JobId);
    }

    [Fact]
    public async Task Reader_RequiresCharacterActivationSettingsForRosterScan()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            version = 1,
            kind = "artifact-analysis",
            uid = "102550550",
            jobId = "job-roster",
            operation = "SCAN_CHARACTER_ROSTER",
            createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(4),
            characterLevelThreshold = 80,
            favoriteOverride = true,
            miliastraCharacterKey = "MannequinBoy"
        }));

        var launch = await new ArtifactHostRequestReader(_root)
            .ReadAsync(path, CancellationToken.None);

        Assert.Equal(ArtifactHostOperation.ScanCharacterRoster, launch.Request.Operation);
        Assert.Equal(80, launch.Request.CharacterLevelThreshold);
        Assert.True(launch.Request.FavoriteOverride);
        Assert.Equal("MannequinBoy", launch.Request.MiliastraCharacterKey);
    }

    [Fact]
    public async Task Reader_RejectsUnknownMiliastraCharacterKey()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            version = 1,
            kind = "artifact-analysis",
            uid = "102550550",
            jobId = "job-roster",
            operation = "SCAN_CHARACTER_ROSTER",
            createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(4),
            characterLevelThreshold = 80,
            favoriteOverride = true,
            miliastraCharacterKey = "unknown"
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ArtifactHostRequestReader(_root).ReadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Reader_RejectsRequestWithoutExplicitOperation()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            version = 1,
            kind = "artifact-analysis",
            uid = "102550550",
            jobId = "job-missing-operation",
            createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(4)
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ArtifactHostRequestReader(_root).ReadAsync(path, CancellationToken.None));

        Assert.Contains("operation", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
