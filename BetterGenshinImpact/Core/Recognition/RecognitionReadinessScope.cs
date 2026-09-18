using System;
using System.Threading;

namespace BetterGenshinImpact.Core.Recognition;

/// <summary>实时观察只借用已就绪且空闲的模型；准备/非战斗恢复可显式等待，二者不共享输入权限。</summary>
internal sealed class RecognitionReadinessScope : IDisposable
{
    private static readonly AsyncLocal<bool> NonBlocking = new();
    private readonly bool _previous = NonBlocking.Value;
    internal static bool IsNonBlocking => NonBlocking.Value;
    internal RecognitionReadinessScope(bool nonBlocking = true) => NonBlocking.Value = nonBlocking;
    public void Dispose() => NonBlocking.Value = _previous;
}

internal sealed class RecognitionNotReadyException(string reason) : InvalidOperationException(reason);
