using System;
using System.Linq;
using System.Runtime.CompilerServices;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.BgiVision;

internal readonly record struct GameStatusToastObservation(CaptureFrameStamp Source, bool SelectionRefused, bool PartyRejected);

internal static class GameStatusToastReader
{
    internal static GameStatusToastObservation Read(ImageRegion frame, IOcrService prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return frame.ReadOnce(new PreparedServiceKey(prepared), () =>
        {
            var source = frame.FrameStamp;
            using var line = frame.DeriveCrop(new Rect((int)(frame.Width * .30), (int)(frame.Height * .18),
                (int)(frame.Width * .40), (int)(frame.Height * .055)));
            var raw = prepared.OcrWithoutDetector(line.SrcMat);
            var selectionText = string.Concat(raw.Where(c => !char.IsWhiteSpace(c)));
            return new GameStatusToastObservation(source,
                selectionText.Contains("无法切换目标角色", StringComparison.Ordinal),
                raw.Contains("当前状态不可进行队伍配置", StringComparison.Ordinal) || raw.Contains("战斗中无法", StringComparison.Ordinal));
        });
    }

    // The existing ImageRegion cache owns only this frame-local borrowed service identity and value result.
    private readonly struct PreparedServiceKey(IOcrService prepared) : IEquatable<PreparedServiceKey>
    {
        private readonly IOcrService _prepared = prepared;
        public bool Equals(PreparedServiceKey other) => ReferenceEquals(_prepared, other._prepared);
        public override bool Equals(object? other) => other is PreparedServiceKey key && Equals(key);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(_prepared);
    }
}
