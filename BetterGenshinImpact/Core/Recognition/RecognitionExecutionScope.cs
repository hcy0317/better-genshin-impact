using System;
using System.Threading;

namespace BetterGenshinImpact.Core.Recognition;

/// <summary>当前观察的协作取消；不拥有共享模型初始化或原生资源。</summary>
internal sealed class RecognitionExecutionScope : IDisposable
{
    private static readonly AsyncLocal<CancellationToken> Active = new();
    private readonly CancellationToken _previous = Active.Value;
    internal static CancellationToken Token => Active.Value;
    internal RecognitionExecutionScope(CancellationToken token) => Active.Value = token;
    public void Dispose() => Active.Value = _previous;

    internal static LockLease Enter(object gate)
    {
        var token = Token;
        token.ThrowIfCancellationRequested();
        while (!System.Threading.Monitor.TryEnter(gate, 25)) token.ThrowIfCancellationRequested();
        try { token.ThrowIfCancellationRequested(); return new(gate); }
        catch { System.Threading.Monitor.Exit(gate); throw; }
    }

    internal readonly struct LockLease(object gate) : IDisposable
    {
        public void Dispose() => System.Threading.Monitor.Exit(gate);
    }
}
