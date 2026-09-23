using System;
using System.Globalization;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service;

/// <summary>只提交现有缓存原帧；不识别、不发输入、不延长启动预算。</summary>
internal sealed class GameStartupWaitEvidence : IAsyncDisposable
{
    private readonly DiagnosticEvidenceScope? _owned;
    private readonly DiagnosticEvidenceScope _scope;
    private readonly ILogger _logger;
    private readonly string _request = "startup:" + Guid.NewGuid().ToString("N");
    private bool _waitingAttempted, _terminalAttempted, _disposed;

    private GameStartupWaitEvidence(ILogger logger)
    {
        _logger = logger;
        _owned = DiagnosticEvidenceScope.CreateOwned();
        _scope = DiagnosticEvidenceScope.Current!;
    }

    internal static GameStartupWaitEvidence? Begin(ILogger logger)
    {
        try { return new(logger); }
        catch (Exception error)
        {
            try { logger.LogWarning(error, "STARTUP_EVIDENCE_UNAVAILABLE phase=begin"); } catch { }
            return null;
        }
    }

    internal void Observe(ImageRegion frame, bool admitted, TimeSpan elapsed, TimeSpan age, string observedUi)
    {
        if (_disposed || admitted || _waitingAttempted || elapsed < TimeSpan.FromSeconds(30)) return;
        _waitingAttempted = true;
        Capture(frame, "waiting", elapsed, age, observedUi);
    }

    internal void CaptureTimeout(Func<(ImageRegion? Frame, TimeSpan Age)> clone,
        TimeSpan elapsed, string observedUi)
    {
        if (_disposed || _terminalAttempted) return;
        _terminalAttempted = true;
        try
        {
            var latest = clone();
            using var frame = latest.Frame;
            if (frame == null) { Missing("timeout", "cache-empty"); return; }
            Capture(frame, "timeout", elapsed, latest.Age, observedUi);
        }
        catch (Exception error) { Missing("timeout", "cache-clone-failed:" + error.GetType().Name); }
    }

    private void Capture(ImageRegion frame, string phase, TimeSpan elapsed, TimeSpan age, string observedUi)
    {
        try
        {
            var detail = string.Format(CultureInfo.InvariantCulture,
                "elapsedSeconds={0:F3} observedUi={1} cacheAgeSeconds={2:F3}; cached-producer-frame, not a new capture",
                elapsed.TotalSeconds, observedUi, age.TotalSeconds);
            if (!_scope.TryCapture(frame, _request, phase, detail, _logger)) Missing(phase, "not-accepted-see-evidence-reason");
        }
        catch (Exception error) { Missing(phase, "diagnostic-failed:" + error.GetType().Name); }
    }

    private void Missing(string phase, string reason)
    {
        try { _logger.LogWarning("STARTUP_EVIDENCE_MISSING request={Request} phase={Phase} reason={Reason}", _request, phase, reason); }
        catch { /* 诊断不得替换原启动结果。 */ }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        // 在调用者上下文同步退役AsyncLocal，然后异步排空；不关闭借用的外层scope。
        try { return _owned == null ? ValueTask.CompletedTask : DrainAsync(_owned.DisposeAsync()); }
        catch (Exception error) { Missing("dispose", error.GetType().Name); return ValueTask.CompletedTask; }
    }

    private async ValueTask DrainAsync(ValueTask drain)
    {
        try { await drain; }
        catch (Exception error) { Missing("drain", error.GetType().Name); }
    }
}
