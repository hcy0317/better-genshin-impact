using System;
using Microsoft.ML.OnnxRuntime;

namespace BetterGenshinImpact.Core.Recognition.OCR.Paddle;

internal static class OcrInference
{
    internal static T Run<T>(Func<RunOptions, T> run)
    {
        var token = RecognitionExecutionScope.Token;
        token.ThrowIfCancellationRequested();
        using var options = new RunOptions();
        // 不遗弃Run：注册只请求原生协作终止，调用返回后才释放其资源。
        using var registration = token.Register(() => options.Terminate = true);
        try { return run(options); }
        catch (OnnxRuntimeException error) when (token.IsCancellationRequested &&
            error.Message.Contains("terminate", StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationCanceledException("OCR inference terminated by its caller", error, token);
        }
    }
}
