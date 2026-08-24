using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask;

internal sealed class ManagedTaskFailureCollector
{
    private readonly List<Exception> _failures = [];

    public void Add(Exception exception)
    {
        IEnumerable<Exception> failures = exception is AggregateException aggregateException
            ? aggregateException.Flatten().InnerExceptions
            : new[] { exception };

        foreach (var failure in failures)
        {
            if (failure is OperationCanceledException or NormalEndException)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            _failures.Add(failure);
        }
    }

    public void ThrowIfAny(string message)
    {
        if (_failures.Count > 0)
        {
            throw new AggregateException(message, _failures);
        }
    }
}
