using System;
using System.Linq;
using System.Text.RegularExpressions;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal static class TimeSettingUiReader
{
    internal static TimeSettingObservation Read(ImageRegion image, IOcrService? ocr = null) =>
        image.ReadOnce((typeof(TimeSettingUiReader), ocr), () =>
        {
            if (image.Width * 9 != image.Height * 16) return new(false, null, null, false, image.FrameStamp);
            var scale = image.Width / 1920d;
            string Text(double x, double y, double width, double height)
            {
                var regions = image.FindMulti(RecognitionObject.Ocr(x * scale, y * scale, width * scale, height * scale), ocrService: ocr);
                try { return string.Join(" ", regions.Select(region => region.Text)); }
                finally { foreach (var region in regions) region.Dispose(); }
            }
            var current = Text(870, 325, 215, 135);
            var currentLabel = string.Concat(current.Where(character => !char.IsWhiteSpace(character)));
            if (!currentLabel.Contains("当前时间", StringComparison.Ordinal) && !currentLabel.Contains("當前時間", StringComparison.Ordinal))
                return new(false, null, null, false, image.FrameStamp);
            return Parse(current, Text(870, 475, 215, 170), Text(1270, 910, 430, 130), image.FrameStamp);
        });

    internal static TimeSettingObservation Parse(string current, string selected, string footer, CaptureFrameStamp source)
    {
        static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c))).Replace('：', ':');
        static int? Minutes(string text)
        {
            var match = Regex.Match(text, @"(?<!\d)(\d{1,2}):(\d{2})(?!\d)");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var hour) || hour > 23 ||
                !int.TryParse(match.Groups[2].Value, out var minute) || minute > 59) return null;
            return hour * 60 + minute;
        }
        current = Normalize(current); selected = Normalize(selected); footer = Normalize(footer);
        var now = Minutes(current); var target = Minutes(selected);
        var visible = (current.Contains("当前时间", StringComparison.Ordinal) || current.Contains("當前時間", StringComparison.Ordinal)) &&
            (selected.Contains("调整到", StringComparison.Ordinal) || selected.Contains("調整到", StringComparison.Ordinal)) && now != null && target != null;
        return new(visible, now, target, footer.Contains("时间少于30分钟", StringComparison.Ordinal) ||
            footer.Contains("時間少於30分鐘", StringComparison.Ordinal), source);
    }
}
