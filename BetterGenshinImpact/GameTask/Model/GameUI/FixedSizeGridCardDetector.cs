using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.Model.GameUI;

internal enum FixedSizeGridCardLayout
{
    CharacterDevelopment,
    PartySetupCharacters
}

internal sealed record FixedSizeGridCardDetectionParams(
    int CardWidth1080,
    int CardHeight1080,
    int AvatarSize1080,
    int MinBottomArea1080,
    int MaxBottomArea1080);

internal sealed record FixedSizeGridCard(Rect CardRect, Rect AvatarRect);

/// <summary>
/// 通过卡片底部白色区域反推固定尺寸网格卡片。
/// </summary>
internal static class FixedSizeGridCardDetector
{
    private static readonly IReadOnlyDictionary<FixedSizeGridCardLayout, FixedSizeGridCardDetectionParams> LayoutParams =
        new Dictionary<FixedSizeGridCardLayout, FixedSizeGridCardDetectionParams>
        {
            [FixedSizeGridCardLayout.CharacterDevelopment] = new(115, 140, 115, 1500, 4500),
            [FixedSizeGridCardLayout.PartySetupCharacters] = new(132, 161, 132, 2500, 4000)
        };

    internal static List<FixedSizeGridCard> Detect(
        Mat gridMat,
        double assetScale,
        FixedSizeGridCardLayout layout,
        out int rejectedCount,
        out int connectedComponentCount)
    {
        var parameters = GetParams(layout);
        using var hsv = gridMat.CvtColor(ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        var lowerColor = layout == FixedSizeGridCardLayout.CharacterDevelopment
            ? new Scalar(0, 0, 225)
            : new Scalar(20, 12, 233);
        var upperColor = layout == FixedSizeGridCardLayout.CharacterDevelopment
            ? new Scalar(40, 60, 255)
            : new Scalar(35, 16, 237);
        Cv2.InRange(hsv, lowerColor, upperColor, mask);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var closedMask = new Mat();
        Cv2.MorphologyEx(mask, closedMask, MorphTypes.Close, kernel, iterations: 1);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var labelCount = Cv2.ConnectedComponentsWithStats(
            closedMask,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8,
            MatType.CV_32S);
        connectedComponentCount = Math.Max(0, labelCount - 1);

        var minArea = parameters.MinBottomArea1080 * assetScale * assetScale;
        var maxArea = parameters.MaxBottomArea1080 * assetScale * assetScale;
        var expectedCardWidth = parameters.CardWidth1080 * assetScale;
        var expectedCardHeight = parameters.CardHeight1080 * assetScale;
        var bottomRects = new List<Rect>();
        for (var label = 1; label < labelCount; label++)
        {
            var area = stats.At<int>(label, 4);
            if (area < minArea || area > maxArea)
            {
                continue;
            }

            var width = stats.At<int>(label, 2);
            var height = stats.At<int>(label, 3);
            if (layout == FixedSizeGridCardLayout.CharacterDevelopment
                && (width < expectedCardWidth * 0.75 || height > expectedCardHeight * 0.5))
            {
                continue;
            }

            bottomRects.Add(new Rect(
                stats.At<int>(label, 0),
                stats.At<int>(label, 1),
                width,
                height));
        }

        var cards = BuildCards(bottomRects, gridMat.Size(), assetScale, layout, out rejectedCount);
        return layout == FixedSizeGridCardLayout.CharacterDevelopment
            ? FillMissingCharacterCards(cards, gridMat, assetScale)
            : cards;
    }

    internal static List<FixedSizeGridCard> BuildCards(
        IReadOnlyList<Rect> bottomRects,
        Size gridSize,
        double assetScale,
        FixedSizeGridCardLayout layout,
        out int rejectedCount)
    {
        var parameters = GetParams(layout);
        rejectedCount = 0;
        if (bottomRects.Count == 0)
        {
            return [];
        }

        var cardWidth = Math.Max(1, (int)Math.Round(parameters.CardWidth1080 * assetScale));
        var cardHeight = Math.Max(1, (int)Math.Round(parameters.CardHeight1080 * assetScale));
        var avatarSize = Math.Max(1, (int)Math.Round(parameters.AvatarSize1080 * assetScale));
        var columnTolerance = Math.Max(3, cardWidth / 3);
        var rowTolerance = Math.Max(3, cardHeight / 3);
        var correctedRights = CorrectByMedian(bottomRects.Select(rect => rect.Right).ToArray(), columnTolerance);
        var correctedBottoms = CorrectByMedian(bottomRects.Select(rect => rect.Bottom).ToArray(), rowTolerance);
        var cards = new List<FixedSizeGridCard>();
        var seen = new HashSet<(int Right, int Bottom)>();

        for (var i = 0; i < bottomRects.Count; i++)
        {
            var right = correctedRights[i];
            var bottom = correctedBottoms[i];
            if (!seen.Add((right, bottom)))
            {
                continue;
            }

            var cardRect = new Rect(right - cardWidth, bottom - cardHeight, cardWidth, cardHeight);
            if (cardRect.X < 0 || cardRect.Y < 0 || cardRect.Right > gridSize.Width || cardRect.Bottom > gridSize.Height)
            {
                rejectedCount++;
                continue;
            }

            cards.Add(new FixedSizeGridCard(
                cardRect,
                new Rect(cardRect.X, cardRect.Y, avatarSize, avatarSize)));
        }

        return cards;
    }

    private static FixedSizeGridCardDetectionParams GetParams(FixedSizeGridCardLayout layout)
    {
        if (!LayoutParams.TryGetValue(layout, out var parameters))
        {
            throw new ArgumentOutOfRangeException(nameof(layout), layout, "未知的固定尺寸网格卡片布局");
        }

        return parameters;
    }

    private static List<FixedSizeGridCard> FillMissingCharacterCards(
        List<FixedSizeGridCard> cards,
        Mat gridMat,
        double assetScale)
    {
        if (cards.Count < 10) return cards;
        var parameters = GetParams(FixedSizeGridCardLayout.CharacterDevelopment);
        var cardWidth = Math.Max(1, (int)Math.Round(parameters.CardWidth1080 * assetScale));
        var cardHeight = Math.Max(1, (int)Math.Round(parameters.CardHeight1080 * assetScale));
        var avatarSize = Math.Max(1, (int)Math.Round(parameters.AvatarSize1080 * assetScale));
        var xs = cards.Select(card => card.CardRect.X).Distinct().OrderBy(value => value).ToArray();
        var ys = cards.Select(card => card.CardRect.Y).Distinct().OrderBy(value => value).ToArray();
        if (xs.Length != 5 || ys.Length < 2) return cards;

        var existing = cards.Select(card => (card.CardRect.X, card.CardRect.Y)).ToHashSet();
        foreach (var y in ys)
        foreach (var x in xs)
        {
            if (existing.Contains((x, y))) continue;
            var cardRect = new Rect(x, y, cardWidth, cardHeight);
            if (cardRect.X < 0 || cardRect.Y < 0
                || cardRect.Right > gridMat.Width || cardRect.Bottom > gridMat.Height
                || !LooksLikeCharacterCard(gridMat, cardRect))
            {
                continue;
            }
            cards.Add(new FixedSizeGridCard(
                cardRect,
                new Rect(x, y, avatarSize, avatarSize)));
        }
        return cards;
    }

    private static bool LooksLikeCharacterCard(Mat gridMat, Rect cardRect)
    {
        using var card = gridMat.SubMat(cardRect);
        using var gray = card.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var edges = gray.Canny(20, 40);
        return Cv2.CountNonZero(edges) >= cardRect.Width * cardRect.Height * 0.08;
    }

    private static int[] CorrectByMedian(IReadOnlyList<int> values, int tolerance)
    {
        var indexed = values
            .Select((value, index) => (Value: value, Index: index))
            .OrderBy(item => item.Value)
            .ToList();
        var result = new int[values.Count];
        var group = new List<(int Value, int Index)>();

        void FlushGroup()
        {
            if (group.Count == 0)
            {
                return;
            }

            var ordered = group.Select(item => item.Value).OrderBy(value => value).ToArray();
            var median = ordered.Length % 2 == 1
                ? ordered[ordered.Length / 2]
                : (int)Math.Round((ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2d);
            foreach (var item in group)
            {
                result[item.Index] = median;
            }
            group.Clear();
        }

        foreach (var item in indexed)
        {
            if (group.Count > 0 && item.Value - group[^1].Value > tolerance)
            {
                FlushGroup();
            }
            group.Add(item);
        }
        FlushGroup();
        return result;
    }
}
