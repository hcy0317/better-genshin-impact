using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoFight.SkillData;

public sealed record SkillCatalogStatus(string DatabasePath, int SkillCount, int CharacterCount,
    IReadOnlyList<string> SourceRevisions, string? RuleVersion, int VerifiedRuleCount, int NeedsReviewCount,
    int MetricOverrideCount, int CharacterProfileCount, string? LastSyncStatus, DateTimeOffset? LastSyncAt,
    DateTimeOffset? LastSuccessfulSyncAt, string? LastError);
