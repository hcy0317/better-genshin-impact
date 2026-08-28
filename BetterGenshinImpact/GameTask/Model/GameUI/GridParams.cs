using BetterGenshinImpact.Core.Recognition.OpenCv;
using OpenCvSharp;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text;

namespace BetterGenshinImpact.GameTask.Model.GameUI
{
    public class GridParams
    {
        internal Rect Roi { get; private set; }
        internal int Columns { get; private set; }
        internal int S1Round { get; private set; }
        internal int RoundMilliseconds { get; private set; }
        internal int S2Round { get; private set; }
        internal double S3Scale { get; private set; }
        internal bool FastScroll { get; private set; }
        internal int PreScrollDelayMilliseconds { get; private set; }
        internal int TotalItems { get; private set; }
        internal int VisibleRows { get; private set; }
        internal int FastScrollRows { get; private set; }
        internal Size CaptureSize { get; private set; }

        public GridParams(Rect roi1080p, int columns, int s1Round, int roundMilliseconds, int s2Round, double s3Scale)
            : this(
                roi1080p.Multiply(TaskContext.Instance().SystemInfo.AssetScale),
                columns, s1Round, roundMilliseconds, s2Round, s3Scale, true, false, 300)
        {
        }

        private GridParams(
            Rect roi,
            int columns,
            int s1Round,
            int roundMilliseconds,
            int s2Round,
            double s3Scale,
            bool useRawRoi,
            bool fastScroll,
            int preScrollDelayMilliseconds,
            int totalItems = 0,
            int visibleRows = 0,
            int fastScrollRows = 0,
            Size captureSize = default)
        {
            Roi = roi;
            Columns = columns;
            S1Round = s1Round;
            RoundMilliseconds = roundMilliseconds;
            S2Round = s2Round;
            S3Scale = s3Scale;
            FastScroll = fastScroll;
            PreScrollDelayMilliseconds = preScrollDelayMilliseconds;
            TotalItems = totalItems;
            VisibleRows = visibleRows;
            FastScrollRows = fastScrollRows;
            CaptureSize = captureSize;
        }

        internal static GridParams ArtifactsForCapture(Size captureSize, int totalItems)
        {
            if (totalItems <= 0) throw new ArgumentOutOfRangeException(nameof(totalItems));
            var roi = ArtifactRoiForCapture(captureSize);
            return new GridParams(
                roi,
                8, 3, 40, 32, 0.024, true, true, 60,
                totalItems, visibleRows: 5, fastScrollRows: 5,
                captureSize: captureSize);
        }

        internal static GridParams CharacterDevelopmentForCapture(Size captureSize)
        {
            var roi = ArtifactUiCoordinateMapper.ToCaptureRect(
                captureSize,
                16.6667, 46.6667, 567.5, 764.1667);
            return new GridParams(
                roi, 5, 3, 40, 50, 0.024,
                true, false, 300,
                captureSize: captureSize);
        }

        internal static Rect ArtifactRoiForCapture(Size captureSize)
        {
            return ArtifactUiCoordinateMapper.ToCaptureRect(
                captureSize,
                88.3333, 135, 975.8333, 765);
        }

        private static readonly GridParams weapons = new GridParams(new Rect(106, 110, 1171, 845), 8, 3, 40, 32, 0.024);

        internal static FrozenDictionary<GridScreenName, GridParams> Templates { get; } = new Dictionary<GridScreenName, GridParams>() {
            { GridScreenName.Weapons, weapons },
            { GridScreenName.Artifacts, new GridParams(new Rect(106, 162, 1171, 783), 8, 3, 40, 32, 0.024)},
            { GridScreenName.CharacterDevelopmentItems, weapons },
            { GridScreenName.Food, weapons },
            { GridScreenName.Materials, weapons },
            { GridScreenName.Gadget, weapons },
            { GridScreenName.Quest, weapons },
            { GridScreenName.PreciousItems, weapons },
            { GridScreenName.Furnishings, weapons },
            { GridScreenName.ArtifactSalvage, new GridParams(new Rect(48, 106, 1267, 768), 9, 3, 40, 28, 0.018) },
            { GridScreenName.Crafting, new GridParams(new Rect(45, 170, 705, 790), 5, 3, 40, 32, 0.024)},
            { GridScreenName.PartySetupCharacters, new GridParams(new Rect(24, 86, 766, 743), 5, 3, 40, 28, 0.018)}
        }.ToFrozenDictionary();
    }
}
