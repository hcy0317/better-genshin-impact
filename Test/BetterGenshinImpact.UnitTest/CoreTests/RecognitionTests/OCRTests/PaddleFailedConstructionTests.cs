using System.Runtime.CompilerServices;
using BetterGenshinImpact.Core.Recognition.OCR.Engine;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;

namespace BetterGenshinImpact.UnitTest.CoreTests.RecognitionTests.OCRTests;

public class PaddleFailedConstructionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSessionConstructionCannotCrashTheFinalizerThread(bool recognizer)
    {
        FailBeforeSessionIsAssigned(recognizer);
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailBeforeSessionIsAssigned(bool recognizer)
    {
        // 公共构造在native依赖未返回会话前失败；只在独立testhost里验证半构造对象回收。
        Assert.Throws<NullReferenceException>(() =>
        {
            if (recognizer) _ = new Rec(null!, [], OcrVersionConfig.PpOcrV5, (BgiOnnxSessionFactory)null!);
            else _ = new Det(null!, OcrVersionConfig.PpOcrV5, (BgiOnnxSessionFactory)null!);
        });
    }
}
