using System.Net;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class SkillCatalogSyncTests
{
    [Fact]
    public async Task FailedDownloadPreservesThePreviousSnapshotAndExposesFailureStatus()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bgi-skill-sync-" + Guid.NewGuid().ToString("N"));
        var store = new SkillCatalogStore(Path.Combine(directory, "skills.db"));
        store.Import([new() { Id = "old.e", CharacterKey = "old", Slot = "e", Revision = "old", SourceUrl = "https://example.com/old" }],
            "old", DateTimeOffset.UtcNow);
        using var client = new HttpClient(new SourceHandler(failChinese: true));
        var catalog = new CombatSkillCatalog(store, new GenshinDbSkillSource(client));
        await Assert.ThrowsAsync<HttpRequestException>(() => catalog.SyncAsync());
        Assert.Single(store.ReadSnapshot().Skills);
        Assert.True(store.ReadSnapshot().Skills.ContainsKey("old.e"));
        var status = catalog.ReadStatus();
        Assert.Equal("failed", status.LastSyncStatus);
        Assert.NotNull(status.LastSuccessfulSyncAt);
        Assert.Contains("old", status.SourceRevisions);
    }

    [Fact]
    public async Task FullSyncUsesThePublishedTalentFilesNotEveryInternalStatsKey()
    {
        using var client = new HttpClient(new SourceHandler());
        var source = new GenshinDbSkillSource(client);
        var batch = await source.FetchAsync();
        Assert.Equal(2, batch.Skills.Count);
        Assert.All(batch.Skills, skill => Assert.Equal("one", skill.CharacterKey));
        Assert.All(batch.Skills, skill => Assert.Equal(new string('a', 40), skill.Revision));
    }

    private sealed class SourceHandler(bool failChinese = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (failChinese && path.Contains("/ChineseSimplified/")) throw new HttpRequestException("synthetic network failure");
            var json = path.EndsWith("/commits/main") ? "{\"sha\":\"" + new string('a', 40) + "\"}"
                : path.EndsWith("/stats/talents.json") ? """
                    {"one":{"combat2":{"param1":[8]},"combat3":{"param1":[15]}},"unpublished":{}}
                    """
                : path.EndsWith("/contents/src/data/English/talents") ? "[{\"name\":\"one.json\",\"type\":\"file\"}]"
                : path.EndsWith("/talents/one.json") ? """
                    {"name":"测试角色","combat2":{"name":"E","attributes":{"labels":["CD|{param1:F1}s"]}},
                     "combat3":{"name":"Q","attributes":{"labels":["CD|{param1:F1}s"]}}}
                    """ : null;
            return Task.FromResult(new HttpResponseMessage(json == null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                { Content = new StringContent(json ?? "not found") });
        }
    }
}
