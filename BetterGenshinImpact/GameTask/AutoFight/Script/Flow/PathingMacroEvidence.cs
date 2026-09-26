using System;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal sealed class PathingMacroEvidence(string context, string commands)
{
    private readonly string _request = "pathing-macro:" + Guid.NewGuid().ToString("N");

    internal void Capture(ImageRegion frame, string phase, PathingMacroObservation observation, ILogger? logger = null)
    {
        try
        {
            // 只保存既有边界观察；同一阶段首次unknown与首次已识别各一帧，不逐帧输出。
            if (phase is not ("entry" or "post" or "scene-transition" or "before-fire" or
                "before-cannon-handshake" or "cannon-handshake-complete")) return;
            var evidencePhase = observation.Scene == PathingMacroScene.Unknown ? phase + "-unknown" : phase;
            DiagnosticEvidenceScope.Current?.TryCapture(frame, _request, evidencePhase,
                $"{context}; commands={commands}; scene={observation.Scene}; phase observation only, not completion proof", logger);
        }
        catch { /* 诊断不能影响原宏的准入、期限和结果。 */ }
    }
}
