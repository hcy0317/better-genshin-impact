using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoDomain;

public partial class AutoDomainTask
{
    // 仅固定里程碑，不记录OCR正文；结束检测线程和领奖线程共享同一轮事实。
    private readonly ConcurrentDictionary<string, string> _domainEvidence = new();

    private void BeginDomainEvidence()
    {
        _domainEvidence.Clear();
        _domainEvidence["domain:round"] = Guid.NewGuid().ToString("N");
        _domainEvidence["domain:completion"] = "unknown:not-observed-this-round";
        _domainEvidence["domain:reward"] = "unknown:not-observed-this-round";
        _domainEvidence["domain:cancel"] = "unknown:not-requested-by-domain-detector";
    }

    private void RecordDomainEvidence(string milestone, string state, CaptureFrameStamp source = default)
    {
        _domainEvidence["domain:" + milestone] = source.IsKnown
            ? $"state={state} source={source.SessionId}/{source.Sequence} capturedAt={source.CapturedAt:O}"
            : $"state={state} source=unknown:not-exposed-at-this-transition";
    }

    private IReadOnlyDictionary<string, string> DomainEvidenceSnapshot() => new Dictionary<string, string>(_domainEvidence);
}
