using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace BetterGenshinImpact.GameTask.AutoFight.SkillData;

/// <summary>SQLite 持久存储：事实、历史版本、用户覆盖和同步记录独立保存。</summary>
public sealed class SkillCatalogStore
{
    public string DatabasePath { get; }
    public SkillCatalogStore(string path)
    {
        DatabasePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current > 2) throw new InvalidOperationException("技能数据库版本高于当前程序，拒绝降级写入");
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS skills(id TEXT PRIMARY KEY, revision TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS skill_history(id TEXT NOT NULL, revision TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(id, revision));
            CREATE TABLE IF NOT EXISTS metric_overrides(skill_id TEXT NOT NULL, metric TEXT NOT NULL,
                payload TEXT NOT NULL, reason TEXT NOT NULL, updated_at TEXT NOT NULL, PRIMARY KEY(skill_id, metric));
            CREATE TABLE IF NOT EXISTS sync_history(sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                revision TEXT NOT NULL, at TEXT NOT NULL, status TEXT NOT NULL, skill_count INTEGER NOT NULL, error TEXT);
            CREATE TABLE IF NOT EXISTS mechanics_rule_history(version TEXT PRIMARY KEY, payload TEXT NOT NULL, at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS mechanics_rule_current(singleton INTEGER PRIMARY KEY CHECK(singleton=1), version TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS character_profiles(character_key TEXT PRIMARY KEY, payload TEXT NOT NULL, updated_at TEXT NOT NULL);
            PRAGMA user_version=2;
            """);
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, DefaultTimeout = 10, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public void Import(IReadOnlyCollection<SkillFact> skills, string revision, DateTimeOffset at, bool onlyIfEmpty = false,
        SkillMechanicsRulePack? rulePack = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        rulePack?.Validate();
        if (string.IsNullOrWhiteSpace(revision) || skills.Count == 0) throw new ArgumentException("空的技能同步批次");
        if (skills.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != skills.Count)
            throw new ArgumentException("同步批次有重复技能 ID");
        foreach (var skill in skills)
        {
            if (string.IsNullOrWhiteSpace(skill.Id) || string.IsNullOrWhiteSpace(skill.CharacterKey)
                || string.IsNullOrWhiteSpace(skill.SourceUrl) || skill.Revision != revision)
                throw new ArgumentException("技能来源或版本不完整：" + skill.Id);
            foreach (var metric in skill.Metrics.Values) ValidateMetric(metric);
        }
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM skills";
        if (onlyIfEmpty && Convert.ToInt64(count.ExecuteScalar()) != 0) return;
        if (rulePack != null) WriteRulePack(connection, transaction, rulePack, at);
        // 一次角色批次整体替换，源中已删除的被动/形态不能留在新事实集里。
        // 未请求的角色、历史事实以及独立用户覆盖均保留。
        foreach (var character in skills.Select(skill => skill.CharacterKey).Distinct(StringComparer.Ordinal))
            Execute(connection, transaction, "DELETE FROM skills WHERE json_extract(payload,'$.CharacterKey')=$character",
                ("$character", character));
        foreach (var skill in skills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = JsonConvert.SerializeObject(skill);
            Execute(connection, transaction, """
                INSERT INTO skills(id,revision,payload) VALUES($id,$revision,$payload)
                ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,payload=excluded.payload;
                INSERT OR IGNORE INTO skill_history(id,revision,payload) VALUES($id,$revision,$payload);
                """, ("$id", skill.Id), ("$revision", revision), ("$payload", payload));
        }
        Execute(connection, transaction,
            "INSERT INTO sync_history(revision,at,status,skill_count) VALUES($revision,$at,'success',$count)",
            ("$revision", revision), ("$at", at.ToString("O")), ("$count", skills.Count));
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public SkillCatalogSnapshot ReadSnapshot(string? revision = null, bool useOverrides = true)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = ReadSnapshot(connection, transaction, revision, useOverrides);
        transaction.Commit();
        return snapshot;
    }

    private static SkillCatalogSnapshot ReadSnapshot(SqliteConnection connection, SqliteTransaction transaction,
        string? revision = null, bool useOverrides = true)
    {
        var skills = new Dictionary<string, SkillFact>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = revision == null ? "SELECT payload FROM skills" : "SELECT payload FROM skill_history WHERE revision=$revision";
            if (revision != null) command.Parameters.AddWithValue("$revision", revision);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var skill = JsonConvert.DeserializeObject<SkillFact>(reader.GetString(0))
                    ?? throw new InvalidDataException("技能数据库包含空记录");
                skills.Add(skill.Id, skill);
            }
        }
        var rules = ReadRulePack(connection, transaction);
        if (rules != null) skills = rules.Apply(skills);
        var profiles = new Dictionary<string, CharacterSkillProfile>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT payload FROM character_profiles";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var profile = JsonConvert.DeserializeObject<CharacterSkillProfile>(reader.GetString(0))!;
                profile.Validate();
                profiles.Add(profile.CharacterKey, profile);
            }
        }
        if (useOverrides) using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT skill_id,metric,payload,reason FROM metric_overrides";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (skills.TryGetValue(reader.GetString(0), out var skill))
                {
                    var metric = JsonConvert.DeserializeObject<SkillMetric>(reader.GetString(2))!;
                    metric.SourceKind = "user-override";
                    metric.OverrideReason = reader.GetString(3);
                    skill.Metrics[reader.GetString(1)] = metric;
                }
        }
        return new SkillCatalogSnapshot(skills, profiles);
    }

    public SkillCatalogStatus ReadStatus()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = ReadSnapshot(connection, transaction);
        object? Scalar(string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
        static DateTimeOffset? Date(object? value) => value is string text ? DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : null;
        var status = new SkillCatalogStatus(DatabasePath, snapshot.Skills.Count,
            snapshot.Skills.Values.Select(skill => skill.CharacterKey).Distinct(StringComparer.Ordinal).Count(),
            snapshot.Skills.Values.Select(skill => skill.Revision).Distinct(StringComparer.Ordinal).Order().ToArray(),
            Scalar("SELECT version FROM mechanics_rule_current WHERE singleton=1") as string,
            snapshot.Skills.Values.Count(skill => skill.MechanicsStatus == "verified-source"),
            snapshot.Skills.Values.Count(skill => skill.MechanicsStatus == "needs-review"),
            Convert.ToInt32(Scalar("SELECT COUNT(*) FROM metric_overrides")), snapshot.Profiles.Count,
            Scalar("SELECT status FROM sync_history ORDER BY sequence DESC LIMIT 1") as string,
            Date(Scalar("SELECT at FROM sync_history ORDER BY sequence DESC LIMIT 1")),
            Date(Scalar("SELECT at FROM sync_history WHERE status='success' ORDER BY sequence DESC LIMIT 1")),
            Scalar("SELECT error FROM sync_history ORDER BY sequence DESC LIMIT 1") as string);
        transaction.Commit();
        return status;
    }

    public void SetCharacterProfile(CharacterSkillProfile profile)
    {
        profile.Validate();
        using var connection = Open();
        Execute(connection, null, """
            INSERT INTO character_profiles(character_key,payload,updated_at) VALUES($key,$payload,$at)
                ON CONFLICT(character_key) DO UPDATE SET payload=excluded.payload,updated_at=excluded.updated_at;
            """, ("$key", profile.CharacterKey), ("$payload", JsonConvert.SerializeObject(profile)),
            ("$at", DateTimeOffset.UtcNow.ToString("O")));
    }

    public void RemoveCharacterProfile(string characterKey)
    {
        using var connection = Open();
        Execute(connection, null, "DELETE FROM character_profiles WHERE character_key=$key", ("$key", characterKey));
    }

    public void SetMetricOverride(string skillId, string metric, SkillMetric value, string reason)
    {
        ValidateMetric(value);
        if (string.IsNullOrWhiteSpace(metric) || string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("覆盖必须提供数值项和原因");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM skills WHERE id=$id";
        exists.Parameters.AddWithValue("$id", skillId);
        if (Convert.ToInt64(exists.ExecuteScalar()) == 0) throw new ArgumentException("未知技能：" + skillId);
        Execute(connection, transaction, """
            INSERT INTO metric_overrides(skill_id,metric,payload,reason,updated_at) VALUES($id,$metric,$payload,$reason,$at)
            ON CONFLICT(skill_id,metric) DO UPDATE SET payload=excluded.payload,reason=excluded.reason,updated_at=excluded.updated_at
            """, ("$id", skillId), ("$metric", metric), ("$payload", JsonConvert.SerializeObject(value)),
            ("$reason", reason), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        transaction.Commit();
    }

    public void RemoveMetricOverride(string skillId, string metric)
    {
        using var connection = Open();
        Execute(connection, null, "DELETE FROM metric_overrides WHERE skill_id=$id AND metric=$metric", ("$id", skillId), ("$metric", metric));
    }

    public void ImportRulePack(SkillMechanicsRulePack rules)
    {
        rules.Validate();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        WriteRulePack(connection, transaction, rules, DateTimeOffset.UtcNow);
        transaction.Commit();
    }

    public SkillMechanicsRulePack? ReadRulePack(string? version = null)
    {
        using var connection = Open();
        return ReadRulePack(connection, null, version);
    }

    private static SkillMechanicsRulePack? ReadRulePack(SqliteConnection connection, SqliteTransaction? transaction, string? version = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = version == null
            ? "SELECT payload FROM mechanics_rule_history WHERE version=(SELECT version FROM mechanics_rule_current WHERE singleton=1)"
            : "SELECT payload FROM mechanics_rule_history WHERE version=$version";
        if (version != null) command.Parameters.AddWithValue("$version", version);
        return command.ExecuteScalar() is string payload ? JsonConvert.DeserializeObject<SkillMechanicsRulePack>(payload) : null;
    }

    private static void WriteRulePack(SqliteConnection connection, SqliteTransaction transaction, SkillMechanicsRulePack rules, DateTimeOffset at)
    {
        var payload = JsonConvert.SerializeObject(rules);
        var existing = ReadRulePack(connection, transaction, rules.Version);
        if (existing != null && JsonConvert.SerializeObject(existing) != payload)
            throw new InvalidDataException("同一机制规则版本不能覆盖不同内容，请使用新版本号");
        Execute(connection, transaction, """
            INSERT OR IGNORE INTO mechanics_rule_history(version,payload,at) VALUES($version,$payload,$at);
            INSERT INTO mechanics_rule_current(singleton,version) VALUES(1,$version)
                ON CONFLICT(singleton) DO UPDATE SET version=excluded.version;
            """, ("$version", rules.Version), ("$payload", payload), ("$at", at.ToString("O")));
    }

    public void RecordSyncFailure(string error, bool cancelled = false)
    {
        using var connection = Open();
        Execute(connection, null, "INSERT INTO sync_history(revision,at,status,skill_count,error) VALUES('',$at,$status,0,$error)",
            ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$status", cancelled ? "cancelled" : "failed"),
            ("$error", error[..Math.Min(error.Length, 2000)]));
    }

    private static void ValidateMetric(SkillMetric metric)
    {
        if (metric.Values.Length == 0 || metric.Values.Any(v => !double.IsFinite(v) || v < 0))
            throw new ArgumentException("技能数值必须是非负有限数，不能将未知值写成 NaN");
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }
}
