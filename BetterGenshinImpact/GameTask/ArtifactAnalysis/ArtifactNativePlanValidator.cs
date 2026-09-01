using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal sealed record ArtifactNativeLockScheme(
    string BuildId,
    string BuildName,
    IReadOnlyList<ArtifactNativeSetPlanDto> Slots);

internal sealed record ArtifactNativeLockSetGroup(
    string SetKey,
    IReadOnlyList<ArtifactNativeLockScheme> Schemes);

internal static class ArtifactNativePlanValidator
{
    internal const string TranslationMode =
        "BUILD_SCOPED_LOCK_AND_QUICK_EQUIP_V1";

    internal static void Validate(ArtifactNativeSyncPlanDto plan)
    {
        if (!string.Equals(plan.TranslationMode, TranslationMode, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported native artifact translation mode '{plan.TranslationMode}'.");
        }
        if (plan.Issues.Count > 0)
        {
            throw new InvalidDataException(
                "Ready native artifact plan cannot contain unresolved issues.");
        }
        if (plan.LockPlans.Count == 0 && plan.QuickEquipPlans.Count == 0)
        {
            throw new InvalidDataException("Native artifact plan has no target plans.");
        }
        if (plan.ReplaceLockPlans != (plan.LockPlans.Count > 0))
        {
            throw new InvalidDataException(
                "Native artifact lock replacement flag does not match its targets.");
        }

        var lockGroups = GroupLockSchemes(plan);
        if (plan.LockPlans.Any(item =>
                string.IsNullOrWhiteSpace(item.BuildId)
                || string.IsNullOrWhiteSpace(item.SetKey)
                || string.IsNullOrWhiteSpace(item.SlotKey)))
        {
            throw new InvalidDataException(
                "Native artifact lock plan contains an empty identity.");
        }
        foreach (var group in lockGroups)
        {
            if (group.Schemes.Count > 3)
            {
                throw new InvalidDataException(
                    $"Artifact set '{group.SetKey}' supports at most three build plans.");
            }
            foreach (var scheme in group.Schemes)
            {
                if (scheme.Slots.Select(slot => slot.SlotKey)
                    .Distinct(StringComparer.Ordinal).Count() != scheme.Slots.Count)
                {
                    throw new InvalidDataException(
                        $"Artifact lock build '{scheme.BuildId}' repeats a slot.");
                }
            }
        }

        foreach (var character in plan.QuickEquipPlans
                     .GroupBy(item => item.CharacterKey, StringComparer.Ordinal))
        {
            if (character.Key.Length == 0 || character.Count() > 2)
            {
                throw new InvalidDataException(
                    "Quick-equip character supports at most two build plans.");
            }
            var presets = character.Select(item => item.PresetIndex).ToArray();
            if (presets.Any(index => index is < 1 or > 2)
                || presets.Distinct().Count() != presets.Length)
            {
                throw new InvalidDataException(
                    $"Quick-equip character '{character.Key}' has an invalid preset slot.");
            }
            if (character.Select(item => item.BuildId)
                .Distinct(StringComparer.Ordinal).Count() != character.Count())
            {
                throw new InvalidDataException(
                    $"Quick-equip character '{character.Key}' repeats a build.");
            }
            foreach (var quick in character)
            {
                if (string.IsNullOrWhiteSpace(quick.BuildId)
                    || quick.Sets.Count is < 1 or > 2
                    || quick.Sets.Count == 1 && quick.Sets[0].Pieces != 4
                    || quick.Sets.Count == 2 && quick.Sets.Any(rule => rule.Pieces != 2)
                    || quick.PrioritySubstats.Count > 3
                    || quick.SecondarySubstats.Count > 3
                    || quick.PrioritySubstats.Intersect(
                        quick.SecondarySubstats, StringComparer.Ordinal).Any())
                {
                    throw new InvalidDataException(
                        $"Quick-equip build '{quick.BuildId}' is not representable.");
                }
            }
        }

        var selectedBuildCount = plan.LockPlans.Select(item => item.BuildId)
            .Concat(plan.QuickEquipPlans.Select(item => item.BuildId))
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (selectedBuildCount != plan.SourceBuildCount)
        {
            throw new InvalidDataException(
                "Native artifact source build count does not match its targets.");
        }
    }

    internal static IReadOnlyList<ArtifactNativeLockSetGroup> GroupLockSchemes(
        ArtifactNativeSyncPlanDto plan) => plan.LockPlans
        .GroupBy(item => item.SetKey, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => new ArtifactNativeLockSetGroup(
            group.Key,
            group.GroupBy(item => item.BuildId, StringComparer.Ordinal)
                .OrderBy(scheme => scheme.Key, StringComparer.Ordinal)
                .Select(scheme => new ArtifactNativeLockScheme(
                    scheme.Key,
                    scheme.First().BuildName,
                    scheme.OrderBy(item => item.SlotKey, StringComparer.Ordinal).ToArray()))
                .ToArray()))
        .ToArray();
}
