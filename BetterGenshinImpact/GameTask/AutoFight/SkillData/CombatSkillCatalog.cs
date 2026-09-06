using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using Newtonsoft.Json;

namespace BetterGenshinImpact.GameTask.AutoFight.SkillData;

public sealed class CombatSkillCatalog
{
    private static readonly Lazy<CombatSkillCatalog> Current = new(() => new(
        new SkillCatalogStore(Global.Absolute("User/CombatSkills/skills.db")),
        new GenshinDbSkillSource(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }),
        Global.Absolute("GameTask/AutoFight/Assets/SkillData/builtin-skills.json")));
    public static CombatSkillCatalog Default => Current.Value;
    public SkillCatalogStore Store { get; }
    private readonly GenshinDbSkillSource _source;
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public CombatSkillCatalog(SkillCatalogStore store, GenshinDbSkillSource source, string? seedPath = null, string? rulePackPath = null)
    {
        Store = store;
        _source = source;
        rulePackPath ??= seedPath == null ? null : Path.Combine(Path.GetDirectoryName(seedPath)!, "builtin-mechanics.json");
        var rules = rulePackPath != null && File.Exists(rulePackPath) && Store.ReadRulePack() == null
            ? ParseLocalJson<SkillMechanicsRulePack>(ReadLocalJsonFile(rulePackPath)) : null;
        if (seedPath != null && File.Exists(seedPath) && Store.ReadSnapshot().Skills.Count == 0)
        {
            var seed = ParseLocalJson<SkillCatalogSeed>(ReadLocalJsonFile(seedPath));
            Store.Import(seed.Skills, seed.Revision, seed.RetrievedAt, onlyIfEmpty: true, rulePack: rules);
        }
        else if (rules != null) Store.ImportRulePack(rules);
    }

    public async Task<SkillCatalogSeed> SyncAsync(IEnumerable<string>? characters = null,
        IProgress<SkillSyncProgress>? progress = null, CancellationToken ct = default)
    {
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(TimeSpan.FromMinutes(10));
            var seed = await _source.FetchAsync(characters, progress, bounded.Token).ConfigureAwait(false);
            bounded.Token.ThrowIfCancellationRequested();
            Store.Import(seed.Skills, seed.Revision, seed.RetrievedAt, cancellationToken: bounded.Token);
            return seed;
        }
        catch (Exception exception)
        {
            try { Store.RecordSyncFailure(exception.Message, exception is OperationCanceledException); }
            catch (Exception logFailure) { exception.Data["SkillCatalogFailureLogError"] = logFailure.Message; }
            throw;
        }
        finally { _syncGate.Release(); }
    }

    public SkillCatalogStatus ReadStatus() => Store.ReadStatus();

    public SkillCatalogStatus ImportRules(string json)
    {
        Store.ImportRulePack(ParseLocalJson<SkillMechanicsRulePack>(json));
        return ReadStatus();
    }

    public SkillCatalogStatus ImportProfile(string json)
    {
        Store.SetCharacterProfile(ParseLocalJson<CharacterSkillProfile>(json));
        return ReadStatus();
    }

    public SkillCatalogStatus SetMetricOverride(string skillId, string metric, string valueJson, string reason)
    {
        Store.SetMetricOverride(skillId, metric, ParseLocalJson<SkillMetric>(valueJson), reason);
        return ReadStatus();
    }

    public static T ParseLocalJson<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json) || System.Text.Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024)
            throw new InvalidDataException("本地技能数据为空或超过 8 MiB 限制");
        return JsonConvert.DeserializeObject<T>(json, new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None, MissingMemberHandling = MissingMemberHandling.Error,
            MaxDepth = 32, CheckAdditionalContent = true
        }) ?? throw new InvalidDataException("本地技能数据为 null");
    }

    public static string ReadLocalJsonFile(string path)
    {
        // 先限制文件，再用同一读取边界限制最终字符串，避免大文件在反序列化前无限分配。
        using var stream = File.OpenRead(path);
        if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("本地技能数据超过 8 MiB 限制");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024) throw new InvalidDataException("本地技能数据超过 8 MiB 限制");
        return json;
    }
}
