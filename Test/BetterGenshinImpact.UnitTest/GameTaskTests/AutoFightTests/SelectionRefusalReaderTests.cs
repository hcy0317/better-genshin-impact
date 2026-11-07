using System;
using System.IO;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public sealed class SelectionRefusalReaderTests
{
    // Manual annotation of the S33 source1488 toast, not a recorded PaddleOCR result.
    private const string RefusalToast = "无法切换目标角色";

    public SelectionRefusalReaderTests() => Assert.True(ApplicationHostBootstrapGuard.IsProhibited);

    [Fact]
    public void MissingPreparedServiceIsUnknownAndLeavesTheSourceFrameOwnedByItsCaller()
    {
        using var frame = CreateFrame(400, 300);
        var source = frame.FrameStamp;

        var cue = CombatMotionReader.ReadSelectionRefusal(frame, null);

        Assert.Equal(SelectionRefusalCue.Unknown, cue);
        Assert.NotEqual(SelectionRefusalCue.Absent, cue);
        AssertParentStillAlive(frame, source, 100, 30);
    }

    [Fact]
    public void ANotReadyPreparedServiceCannotBeReportedAsAnAbsentToast()
    {
        using var frame = CreateFrame(400, 300);
        var source = frame.FrameStamp;
        var before = RecognitionReadinessScope.IsNonBlocking;
        using var ocr = new ExternalOcr(_ => throw new RecognitionNotReadyException("external fake service is not ready"));

        var cue = CombatMotionReader.ReadSelectionRefusal(frame, ocr);

        Assert.Equal(SelectionRefusalCue.Unknown, cue);
        Assert.NotEqual(SelectionRefusalCue.Absent, cue);
        Assert.Equal(1, ocr.OcrCalls);
        Assert.Equal(0, ocr.AlternateCalls);
        Assert.True(ocr.SawNonBlocking);
        Assert.Equal(before, RecognitionReadinessScope.IsNonBlocking);
        Assert.False(ocr.IsDisposed);
        AssertParentStillAlive(frame, source, 100, 30);
    }

    [Theory]
    [InlineData(400, 300, 100, 30, 200, 100)]
    [InlineData(800, 600, 200, 60, 400, 200)]
    public void ExplicitBorrowedServiceReadsOnlyTheCurrentFrameToastRegionOnce(
        int width, int height, int left, int top, int roiWidth, int roiHeight)
    {
        using var frame = CreateFrame(width, height);
        var source = frame.FrameStamp;
        var before = RecognitionReadinessScope.IsNonBlocking;
        using var ocr = new ExternalOcr(mat =>
        {
            Assert.False(mat.IsDisposed);
            Assert.Equal(roiWidth, mat.Cols);
            Assert.Equal(roiHeight, mat.Rows);
            Assert.Equal(new Vec3b(41, 73, 119), mat.At<Vec3b>(0, 0));
            Assert.Equal(new Vec3b(139, 53, 17), mat.At<Vec3b>(roiHeight - 1, roiWidth - 1));
            return RefusalToast;
        });

        var cue = CombatMotionReader.ReadSelectionRefusal(frame, ocr);

        Assert.Equal(SelectionRefusalCue.GameRefusedSelection, cue);
        Assert.Equal(1, ocr.OcrCalls);
        Assert.Equal(0, ocr.AlternateCalls);
        Assert.True(ocr.SawNonBlocking);
        Assert.Equal(before, RecognitionReadinessScope.IsNonBlocking);
        Assert.False(ocr.IsDisposed);
        AssertParentStillAlive(frame, source, left, top);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("  \n \t", false)]
    [InlineData("切换角色冷却中", false)]
    [InlineData("无法切换目标", false)]
    [InlineData("无法切换角色", false)]
    [InlineData("无法切换目标角", false)]
    [InlineData("无法切换目标角色", true)]
    [InlineData("无 法\n切换 目标角色", true)]
    public void ACompletedExplicitReadDistinguishesTheCompleteToastFromOtherOrPartialText(
        string text, bool expectedPresent)
    {
        using var frame = CreateFrame(400, 300);
        using var ocr = new ExternalOcr(_ => text);

        var expected = expectedPresent ? SelectionRefusalCue.GameRefusedSelection : SelectionRefusalCue.Absent;
        Assert.Equal(expected, CombatMotionReader.ReadSelectionRefusal(frame, ocr));

        Assert.Equal(1, ocr.OcrCalls);
        Assert.Equal(0, ocr.AlternateCalls);
        Assert.False(ocr.IsDisposed);
    }

    [Fact]
    public void OneSharedServiceReadsEachProvidedFrameInsteadOfReusingAnEarlierFrameResult()
    {
        using var first = CreateFrame(400, 300);
        using var second = CreateFrame(400, 300);
        second.SrcMat.Set(30, 100, new Vec3b(9, 10, 11));
        var firstSource = first.FrameStamp;
        var secondSource = second.FrameStamp;
        using var ocr = new ExternalOcr(mat =>
            mat.At<Vec3b>(0, 0).Equals(new Vec3b(41, 73, 119)) ? RefusalToast : string.Empty);

        Assert.Equal(SelectionRefusalCue.GameRefusedSelection, CombatMotionReader.ReadSelectionRefusal(first, ocr));
        Assert.Equal(SelectionRefusalCue.Absent, CombatMotionReader.ReadSelectionRefusal(second, ocr));

        Assert.Equal(2, ocr.OcrCalls);
        Assert.NotEqual(firstSource, secondSource);
        Assert.Equal(firstSource, first.FrameStamp);
        Assert.Equal(secondSource, second.FrameStamp);
        Assert.False(ocr.IsDisposed);
        Assert.False(first.SrcMat.IsDisposed);
        Assert.False(second.SrcMat.IsDisposed);
    }

    [Fact]
    public void AnUnrelatedReadFailureIsNotSilentlyConvertedIntoAnAbsentToast()
    {
        using var frame = CreateFrame(400, 300);
        var source = frame.FrameStamp;
        var before = RecognitionReadinessScope.IsNonBlocking;
        var failure = new IOException("external fake read failure");
        using var ocr = new ExternalOcr(_ => throw failure);

        Assert.Same(failure, Assert.Throws<IOException>(() => CombatMotionReader.ReadSelectionRefusal(frame, ocr)));

        Assert.True(ocr.SawNonBlocking);
        Assert.Equal(before, RecognitionReadinessScope.IsNonBlocking);
        Assert.Equal(1, ocr.OcrCalls);
        Assert.False(ocr.IsDisposed);
        AssertParentStillAlive(frame, source, 100, 30);
    }

    private static ImageRegion CreateFrame(int width, int height)
    {
        var pixels = new Mat(height, width, MatType.CV_8UC3, new Scalar(13, 29, 47));
        pixels.Set(height / 10, width / 4, new Vec3b(41, 73, 119));
        pixels.Set(height / 10 + height / 3 - 1, width / 4 + width / 2 - 1, new Vec3b(139, 53, 17));
        return new ImageRegion(pixels, 0, 0) { FrameStamp = new CaptureFrameSource(TimeProvider.System).Next() };
    }

    private static void AssertParentStillAlive(ImageRegion frame, CaptureFrameStamp source, int left, int top)
    {
        Assert.Equal(source, frame.FrameStamp);
        Assert.False(frame.SrcMat.IsDisposed);
        Assert.Equal(new Vec3b(41, 73, 119), frame.SrcMat.At<Vec3b>(top, left));
        Assert.Equal(new Vec3b(13, 29, 47), frame.SrcMat.At<Vec3b>(0, 0));
    }

    // This external fake records scalars only; it never retains a Mat or ImageRegion.
    private sealed class ExternalOcr(Func<Mat, string> read) : IOcrService, IDisposable
    {
        public int OcrCalls { get; private set; }
        public int AlternateCalls { get; private set; }
        public bool SawNonBlocking { get; private set; }
        public bool IsDisposed { get; private set; }

        public string Ocr(Mat mat)
        {
            Assert.False(IsDisposed);
            OcrCalls++;
            SawNonBlocking = RecognitionReadinessScope.IsNonBlocking;
            return read(mat);
        }

        public string OcrWithoutDetector(Mat mat)
        {
            AlternateCalls++;
            throw new InvalidOperationException("The refusal reader must use its one explicit Ocr call.");
        }

        public OcrResult OcrResult(Mat mat)
        {
            AlternateCalls++;
            throw new InvalidOperationException("The refusal reader must not select another OCR API.");
        }

        public void Dispose() => IsDisposed = true;
    }
}
