using BetterGenshinImpact.Core.Recognition.OpenCv;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.CoreTests.RecognitionTests;

public class RecognitionFailureTests
{
    [Fact]
    public void NativeMatcherFailureCannotBeReportedAsAnEmptyMatchList()
    {
        using var source = new Mat(32, 32, MatType.CV_8UC3, Scalar.Black);
        using var template = new Mat(4, 4, MatType.CV_8UC1, Scalar.White);
        Assert.Throws<OpenCVException>(() => MatchTemplateHelper.FindMatches(source, template,
            TemplateMatchModes.CCoeffNormed, null, .8, 1));
    }

    [Fact]
    public void MissingCaptureIsUnavailableButAValidTooSmallSearchHasNoMatches()
    {
        using var missing = new Mat();
        using var template = new Mat(4, 4, MatType.CV_8UC1, Scalar.White);
        Assert.Throws<InvalidOperationException>(() => MatchTemplateHelper.FindMatches(missing, template,
            TemplateMatchModes.CCoeffNormed, null, .8, 1));
        using var smaller = new Mat(2, 2, MatType.CV_8UC1, Scalar.Black);
        Assert.Empty(MatchTemplateHelper.FindMatches(smaller, template, TemplateMatchModes.CCoeffNormed, null, .8, 1));
    }
}
