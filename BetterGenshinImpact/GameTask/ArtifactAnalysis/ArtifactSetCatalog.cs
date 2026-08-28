using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal sealed class ArtifactSetCatalog
{
    private readonly IReadOnlyDictionary<string, string> _localizedNames;

    internal ArtifactSetCatalog(string path)
        : this(JsonSerializer.Deserialize<Dictionary<string, string>>(
                   File.ReadAllText(path))
               ?? throw new InvalidDataException("Artifact set catalog is empty."))
    {
    }

    internal ArtifactSetCatalog(IReadOnlyDictionary<string, string> localizedNames)
    {
        _localizedNames = localizedNames;
    }

    internal string ResolveSetKey(string recognizedText)
    {
        var match = _localizedNames
            .OrderByDescending(pair => pair.Value.Length)
            .FirstOrDefault(pair => recognizedText.Contains(pair.Value, StringComparison.Ordinal));
        if (string.IsNullOrEmpty(match.Key))
        {
            var fragments = Regex.Split(recognizedText, @"[\s:：,，。;；]+")
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .ToArray();
            match = ResolveUniqueEditDistance(fragments);
            if (string.IsNullOrEmpty(match.Key))
            {
                match = ResolveUniquePrefix(fragments);
            }
        }
        if (string.IsNullOrEmpty(match.Key))
        {
            throw new InvalidDataException(
                $"Unable to resolve artifact set from OCR text '{recognizedText}'.");
        }
        return string.Concat(match.Key.Split('_').Select(part =>
            char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private KeyValuePair<string, string> ResolveUniqueEditDistance(
        IEnumerable<string> fragments)
    {
        var candidates = fragments
            .SelectMany(fragment => _localizedNames.Select(pair => new
            {
                Pair = pair,
                Distance = EditDistance(fragment, pair.Value),
                Limit = pair.Value.Length >= 4 ? 2 : 1
            }))
            .Where(candidate => candidate.Distance <= candidate.Limit)
            .ToArray();
        if (candidates.Length == 0) return default;

        var bestDistance = candidates.Min(candidate => candidate.Distance);
        var best = candidates
            .Where(candidate => candidate.Distance == bestDistance)
            .Select(candidate => candidate.Pair)
            .DistinctBy(pair => pair.Key)
            .ToArray();
        return best.Length == 1 ? best[0] : default;
    }

    private KeyValuePair<string, string> ResolveUniquePrefix(
        IEnumerable<string> fragments)
    {
        var candidates = fragments
            .SelectMany(fragment => _localizedNames.Select(pair => new
            {
                Pair = pair,
                FragmentIsTruncated = fragment.Length < pair.Value.Length,
                PrefixLength = CommonPrefixLength(fragment, pair.Value)
            }))
            .Where(candidate => candidate.FragmentIsTruncated && candidate.PrefixLength >= 2)
            .ToArray();
        if (candidates.Length == 0) return default;

        var bestPrefixLength = candidates.Max(candidate => candidate.PrefixLength);
        var best = candidates
            .Where(candidate => candidate.PrefixLength == bestPrefixLength)
            .Select(candidate => candidate.Pair)
            .DistinctBy(pair => pair.Key)
            .ToArray();
        return best.Length == 1 ? best[0] : default;
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < length && left[index] == right[index]) index++;
        return index;
    }

    private static int EditDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            var current = new int[right.Length + 1];
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = previous[rightIndex - 1]
                                   + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    substitution);
            }
            previous = current;
        }
        return previous[right.Length];
    }

    internal string LocalizedName(string setKey)
    {
        var snake = string.Concat(setKey.Select((character, index) =>
                index > 0 && char.IsUpper(character) ? "_" + character : character.ToString()))
            .ToLowerInvariant();
        return _localizedNames.TryGetValue(snake, out var value)
            ? value
            : throw new InvalidDataException($"Unknown artifact set key '{setKey}'.");
    }
}
