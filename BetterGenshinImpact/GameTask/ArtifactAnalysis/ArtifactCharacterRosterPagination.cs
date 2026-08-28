using BetterGenshinImpact.GameTask.CharacterDevelopment;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal sealed record ArtifactCharacterPageRow(
    IReadOnlyList<Rect> Cards,
    IReadOnlyList<ulong> CardSignatures);

internal static class ArtifactCharacterPageDetector
{
    private const int Columns = 5;
    private const int MaximumRows = 6;

    internal static IReadOnlyList<ArtifactCharacterPageRow> Detect(
        Mat grid,
        double assetScale)
    {
        var cards = CharacterSelectionHelper.DetectCharacterCards(
                grid, assetScale, out _, out _)
            .OrderBy(card => card.CardRect.Y)
            .ThenBy(card => card.CardRect.X)
            .Select(card => card.CardRect)
            .ToList();
        if (cards.Count == 0) return [];

        var rows = cards
            .GroupBy(card => card.Y)
            .OrderBy(group => group.Key)
            .Select(group => group.OrderBy(card => card.X).ToList())
            .ToList();
        AddVisibleSixthRow(grid, rows, assetScale);

        return rows
            .Take(MaximumRows)
            .Select(row => new ArtifactCharacterPageRow(
                row,
                row.Select(card => CardSignature(grid, card)).ToArray()))
            .ToArray();
    }

    private static void AddVisibleSixthRow(
        Mat grid,
        List<List<Rect>> rows,
        double assetScale)
    {
        if (rows.Count >= MaximumRows || rows.Count < 2) return;
        var xs = rows.SelectMany(row => row).Select(card => card.X)
            .Distinct().OrderBy(x => x).ToArray();
        if (xs.Length != Columns) return;

        var rowYs = rows.Select(row => row[0].Y).ToArray();
        var pitches = rowYs.Zip(rowYs.Skip(1), (top, next) => next - top)
            .Where(pitch => pitch > 0).OrderBy(pitch => pitch).ToArray();
        if (pitches.Length == 0) return;
        var pitch = pitches[pitches.Length / 2];
        var nextY = rowYs[^1] + pitch;
        var cardWidth = Math.Max(1, (int)Math.Round(115 * assetScale));
        var cardHeight = Math.Max(1, (int)Math.Round(140 * assetScale));
        var visibleHeight = Math.Min(cardHeight, grid.Height - nextY);
        if (visibleHeight < Math.Round(40 * assetScale)) return;

        var visibleCards = xs
            .Select(x => new Rect(x, nextY, cardWidth, visibleHeight))
            .Where(card => card.Right <= grid.Width && LooksLikeCard(grid, card))
            .ToList();
        if (visibleCards.Count > 0) rows.Add(visibleCards);
    }

    private static bool LooksLikeCard(Mat grid, Rect cardRect)
    {
        using var card = grid.SubMat(cardRect);
        using var gray = card.Channels() == 4
            ? card.CvtColor(ColorConversionCodes.BGRA2GRAY)
            : card.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var edges = gray.Canny(20, 40);
        return Cv2.CountNonZero(edges) >= cardRect.Width * cardRect.Height * 0.05;
    }

    private static ulong CardSignature(Mat grid, Rect cardRect)
    {
        var insetX = Math.Max(2, cardRect.Width / 10);
        var top = cardRect.Y + Math.Max(2, cardRect.Height / 14);
        var height = Math.Min(
            Math.Max(1, (int)Math.Round(70 * cardRect.Width / 115.0)),
            cardRect.Bottom - top - 2);
        var inner = new Rect(
            cardRect.X + insetX,
            top,
            cardRect.Width - insetX * 2,
            height);
        using var portrait = grid.SubMat(inner);
        using var gray = portrait.Channels() == 4
            ? portrait.CvtColor(ColorConversionCodes.BGRA2GRAY)
            : portrait.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var reduced = gray.Resize(new Size(9, 8), 0, 0, InterpolationFlags.Area);

        ulong signature = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            if (reduced.At<byte>(y, x) > reduced.At<byte>(y, x + 1))
                signature |= 1UL << bit;
            bit++;
        }
        return signature;
    }
}

internal sealed class ArtifactCharacterPageTracker
{
    private IReadOnlyList<ArtifactCharacterPageRow>? _previousRows;

    internal bool HasPreviousPage => _previousRows is not null;

    internal IReadOnlyList<ArtifactCharacterPageRow> SelectUnprocessedRows(
        IReadOnlyList<ArtifactCharacterPageRow> currentRows)
    {
        if (_previousRows is null) return currentRows;
        var overlap = FindOverlap(_previousRows, currentRows);
        return currentRows.Skip(overlap).ToArray();
    }

    internal void Commit(IReadOnlyList<ArtifactCharacterPageRow> rows) =>
        _previousRows = rows.ToArray();

    internal static int FindOverlap(
        IReadOnlyList<ArtifactCharacterPageRow> previousRows,
        IReadOnlyList<ArtifactCharacterPageRow> currentRows)
    {
        var maximum = Math.Min(previousRows.Count, currentRows.Count);
        for (var count = maximum; count >= 1; count--)
        {
            var matches = true;
            for (var index = 0; index < count; index++)
            {
                if (!RowsMatch(
                        previousRows[previousRows.Count - count + index],
                        currentRows[index]))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return count;
        }
        return 0;
    }

    private static bool RowsMatch(
        ArtifactCharacterPageRow left,
        ArtifactCharacterPageRow right)
    {
        if (left.CardSignatures.Count != right.CardSignatures.Count) return false;
        return left.CardSignatures.Zip(right.CardSignatures)
            .All(pair => BitOperations.PopCount(pair.First ^ pair.Second) <= 10);
    }
}
