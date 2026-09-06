using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.SkillData;

public sealed record SkillSyncProgress(int Completed, int Total, string CharacterKey);

/// <summary>固定公共源，整批固定到同一个 Git commit。失败或取消时不返回半批事实。</summary>
public sealed class GenshinDbSkillSource(HttpClient client)
{
    public const string Repository = "https://github.com/theBowja/genshin-db";
    private const string Raw = "https://raw.githubusercontent.com/theBowja/genshin-db/";

    public async Task<SkillCatalogSeed> FetchAsync(IEnumerable<string>? characterKeys = null,
        IProgress<SkillSyncProgress>? progress = null, CancellationToken ct = default)
    {
        var head = await GetJson("https://api.github.com/repos/theBowja/genshin-db/commits/main", ct);
        var revision = head.Value<string>("sha") ?? "";
        if (!Regex.IsMatch(revision, "^[0-9a-f]{40}$")) throw new InvalidOperationException("公开数据库没有返回有效的版本标识");
        var stats = (JObject)await GetJson(Raw + revision + "/src/data/stats/talents.json", ct);
        if (characterKeys == null)
        {
            var files = await GetJson("https://api.github.com/repos/theBowja/genshin-db/contents/src/data/English/talents?ref=" + revision, ct);
            if (files is not JArray) throw new InvalidDataException("技能源未返回公开角色文件列表");
            characterKeys = files.Children<JObject>().Where(file => file.Value<string>("type") == "file")
                .Select(file => file.Value<string>("name") ?? "").Where(name => name.EndsWith(".json", StringComparison.Ordinal))
                .Select(name => name[..^5]).ToArray();
        }
        var keys = characterKeys.Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0 || keys.Length > 256 || keys.Any(k => !Regex.IsMatch(k, "^[a-z0-9]+$") || stats[k] == null))
            throw new ArgumentException("同步角色键无效或不在公开数据库内");
        using var gate = new SemaphoreSlim(4);
        var completed = 0;
        var batches = await Task.WhenAll(keys.Select(async key =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var english = (JObject)await GetJson(Raw + revision + "/src/data/English/talents/" + key + ".json", ct);
                var chinese = (JObject)await GetJson(Raw + revision + "/src/data/ChineseSimplified/talents/" + key + ".json", ct);
                var result = ParseCharacter(key, revision, english, chinese, (JObject)stats[key]!);
                progress?.Report(new(Interlocked.Increment(ref completed), keys.Length, key));
                return result;
            }
            finally { gate.Release(); }
        }));
        return new SkillCatalogSeed { Revision = revision, RetrievedAt = DateTimeOffset.UtcNow, Skills = batches.SelectMany(x => x).ToList() };
    }

    private async Task<JToken> GetJson(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("BetterGI-SkillCatalog/1.0");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        const int maxBytes = 16 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("技能源响应超过大小限制");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (content.Length + read > maxBytes) throw new InvalidDataException("技能源响应超过大小限制");
            content.Write(buffer, 0, read);
        }
        ct.ThrowIfCancellationRequested();
        return JToken.Parse(Encoding.UTF8.GetString(content.GetBuffer(), 0, checked((int)content.Length)));
    }

    public static List<SkillFact> ParseCharacter(string key, string revision, JObject english, JObject chinese, JObject stats)
    {
        var character = chinese.Value<string>("name") ?? throw new InvalidOperationException("缺少角色中文名称");
        var result = new List<SkillFact>();
        foreach (var section in english.Properties().Where(p => Regex.IsMatch(p.Name, "^(combat|passive)[0-9]+$")))
        {
            var slot = section.Name switch { "combat1" => "a", "combat2" => "e", "combat3" => "q", _ => section.Name };
            var fact = new SkillFact
            {
                Id = key + "." + slot, CharacterKey = key, Character = character, Slot = slot,
                Name = chinese[section.Name]?.Value<string>("name") ?? section.Value.Value<string>("name") ?? slot,
                Description = chinese[section.Name]?.Value<string>("description") ?? "",
                EnglishDescription = section.Value.Value<string>("description") ?? "",
                Revision = revision, SourceUrl = Repository + "/blob/" + revision + "/src/data/English/talents/" + key + ".json"
            };
            var ambiguousMetrics = new HashSet<string>(StringComparer.Ordinal);
            foreach (var label in section.Value["attributes"]?["labels"]?.Values<string>() ?? [])
            {
                if (label == null) continue;
                var parts = label.Split('|', 2);
                if (parts.Length != 2) continue;
                var seconds = parts[1].EndsWith('s') && Regex.IsMatch(parts[0], "CD|Duration|Interval", RegexOptions.IgnoreCase);
                var energy = parts[0] == "Energy Cost";
                if (!seconds && !energy) continue;
                var parameters = Regex.Matches(parts[1], @"\{(param\d+):[^}]+\}");
                if (parameters.Count == 0) continue;
                foreach (Match sourceParameter in parameters)
                {
                var values = stats[section.Name]?[sourceParameter.Groups[1].Value]?.Values<double>().ToArray();
                if (values == null || values.Length == 0 || values.Any(v => !double.IsFinite(v) || v < 0))
                    throw new InvalidOperationException($"{key}/{section.Name}/{parts[0]} 缺少可靠数值");
                var metric = Regex.Replace(parts[0].ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
                var parameter = sourceParameter.Groups[1].Value;
                if (parameters.Count > 1) metric += "-" + parameter; // 例如班尼特点按/一段/二段 CD，分别保留。
                var value = new SkillMetric { Label = parts[0], SourceParameter = parameter, Unit = seconds ? "seconds" : "energy", Values = values };
                if (ambiguousMetrics.Contains(metric)) fact.Metrics[metric + "-" + parameter] = value;
                else if (fact.Metrics.Remove(metric, out var previous))
                {
                    ambiguousMetrics.Add(metric);
                    fact.Metrics[metric + "-" + previous.SourceParameter] = previous;
                    fact.Metrics[metric + "-" + parameter] = value;
                }
                else fact.Metrics.Add(metric, value);
                }
            }
            var describedDurations = Regex.Matches(fact.EnglishDescription, @"\b(\d+(?:\.\d+)?)s\b")
                .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToArray();
            if (describedDurations.Length == 1)
                fact.Metrics.TryAdd("description-duration", new SkillMetric { Label = "Single duration stated in description", SourceParameter = "description", Values = describedDurations });
            result.Add(fact);
        }
        if (!result.Any(f => f.Slot == "e") || !result.Any(f => f.Slot == "q"))
            throw new InvalidOperationException("源数据缺少 E/Q：" + key);
        return result;
    }
}
