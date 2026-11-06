using BetterGenshinImpact.GameTask.Model.GameUI;
using BetterGenshinImpact.UnitTest.CoreTests.RecognitionTests.OCRTests;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.Model.GameUI
{
    public class GridScrollMotionTests
    {
        [Fact]
        public void HighConfidenceVerticalTranslationDoesNotSignalBottom()
        {
            using var before = CreateTexture(104729);
            using var after = Translate(before, 0, 16);
            using var beforeFloat = new Mat();
            using var afterFloat = new Mat();
            before.ConvertTo(beforeFloat, MatType.CV_32FC1);
            after.ConvertTo(afterFloat, MatType.CV_32FC1);
            using var window = new Mat();
            var measured = Cv2.PhaseCorrelate(beforeFloat, afterFloat, window, out var response);
            Assert.True(response > 0.95, $"Fixture must have high real correlation: response={response:R}");
            Assert.InRange(measured.Y, 15.9, 16.1);
            Assert.True(GridScroller.IsScrolling(before, after, out var shift),
                $"Actual vertical movement must not signal bottom: response={response:R}, shift={measured}, actual={shift}");
        }

        [Fact]
        public void HighConfidenceUpwardTranslationAlsoCountsAsMovement()
        {
            using var before = CreateTexture(104729);
            using var after = Translate(before, 0, -16);
            Assert.True(GridScroller.IsScrolling(before, after, out var shift));
            Assert.InRange(shift.Y, -16.1, -15.9);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(16)]
        [InlineData(-16)]
        public void StationaryAndHorizontalFramesDoNotSignalVerticalScroll(int x)
        {
            using var before = CreateTexture(104729);
            using var after = Translate(before, x, 0);
            Assert.False(GridScroller.IsScrolling(before, after, out _));
        }

        [Theory]
        [InlineData(0.25)]
        [InlineData(-0.25)]
        public void SubPixelVerticalChangeDoesNotPassOnePixelMovementGate(double y)
        {
            using var before = CreateTexture(104729);
            using var after = Translate(before, 0, y, InterpolationFlags.Linear);
            Assert.False(GridScroller.IsScrolling(before, after, out var shift));
            Assert.InRange(Math.Abs(shift.Y), 0, 1);
        }

        [Fact]
        public void LowConfidenceUnrelatedTexturesDoNotTurnLargeShiftIntoMovement()
        {
            using var before = CreateTexture(104729);
            using var after = CreateTexture(130363);
            using var beforeFloat = new Mat();
            using var afterFloat = new Mat();
            before.ConvertTo(beforeFloat, MatType.CV_32FC1);
            after.ConvertTo(afterFloat, MatType.CV_32FC1);
            using var window = new Mat();
            var measured = Cv2.PhaseCorrelate(beforeFloat, afterFloat, window, out var response);
            Assert.True(response < 0.5, $"Fixture must have low real correlation: {response:R}");
            Assert.True(Math.Abs(measured.Y) > 1, $"Fixture must expose the unsafe large-shift OR: {measured}");
            Assert.False(GridScroller.IsScrolling(before, after, out _));
        }

        [Fact]
        public void UniformFramesDoNotProvideMovementEvidence()
        {
            using var blank = new Mat(256, 256, MatType.CV_8UC1, Scalar.Black);
            Assert.False(GridScroller.IsScrolling(blank, blank, out _));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void NonFiniteImageDataDoesNotBecomeMovement(double value)
        {
            using var before = new Mat(256, 256, MatType.CV_32FC1, Scalar.All(value));
            using var after = new Mat(256, 256, MatType.CV_32FC1, Scalar.All(value));
            Assert.False(GridScroller.IsScrolling(before, after, out _));
        }

        private static Mat CreateTexture(int seed)
        {
            var result = new Mat(256, 256, MatType.CV_8UC1);
            var pixels = new byte[256 * 256];
            new Random(seed).NextBytes(pixels);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, result.Data, pixels.Length);
            return result;
        }

        private static Mat Translate(Mat source, double x, double y, InterpolationFlags interpolation = InterpolationFlags.Nearest)
        {
            using var transform = Mat.Eye(2, 3, MatType.CV_64FC1).ToMat();
            transform.Set(0, 2, x);
            transform.Set(1, 2, y);
            var result = new Mat();
            Cv2.WarpAffine(source, result, transform, source.Size(), interpolation, BorderTypes.Wrap);
            return result;
        }
    }

    [Collection("Init Collection")]
    public class GridScreenTests
    {
        private readonly PaddleFixture paddle;
        public GridScreenTests(PaddleFixture paddle)
        {
            this.paddle = paddle;
        }

        [Theory]
        [InlineData(@"AutoArtifactSalvage\ArtifactGrid.png", 4, 2)]
        [InlineData(@"GetGridIcons\FoodGrid.png", 32, 8)]
        [InlineData(@"GetGridIcons\WeaponGrid.png", 4, 2)]
        [InlineData(@"GetGridIcons\WeaponGrid3.png", 32, 8)]
        /// <summary>
        /// 测试获取各种界面中的物品图标，经过算法的后处理，结果应正确
        /// </summary>
        public void GetGridIcons_ShouldBeRight(string screenshot, int count, int columns)
        {
            //
            using Mat mat = new Mat(@$"..\..\..\Assets\{screenshot}");

            //
            var rects = GridScreen.GridEnumerator.GetGridItems(mat, columns);
            var cells = GridScreen.GridEnumerator.PostProcess(mat, rects, (int)(0.025 * mat.Height));

            //
            Assert.Equal(count, cells.Count());
        }

        [Theory]
        [InlineData(@"AutoArtifactSalvage\ArtifactGrid.png", 4, 2)]
        [InlineData(@"GetGridIcons\FoodGrid.png", 32, 8)]
        [InlineData(@"GetGridIcons\WeaponGrid.png", 4, 2)]
        [InlineData(@"GetGridIcons\WeaponGrid3.png", 32, 8)]
        /// <summary>
        /// 测试获取各种界面中的物品图标，使用复杂的cv算法，经过算法的后处理，结果应正确
        /// </summary>
        public void GetGridIconsAlpha_ShouldBeRight(string screenshot, int count, int columns)
        {
            //
            using Mat mat = new Mat(@$"..\..\..\Assets\{screenshot}");

            //
            var rects = GridScreen.GridEnumerator.GetGridItems(mat, columns, findContoursAlpha: true);
            var cells = GridScreen.GridEnumerator.PostProcess(mat, rects, (int)(0.025 * mat.Height));

            //
            Assert.Equal(count, cells.Count());
        }

        [Fact]
        /// <summary>
        /// 测试判断前后两张图是否属于滚动，结果应正确
        /// </summary>
        public void IsScrolling_ResultShouldBeRight()
        {
            //
            using Mat mat = new Mat(@$"..\..\..\Assets\AutoArtifactSalvage\ArtifactGrid.png", flags: ImreadModes.Grayscale);
            using Mat cropped = mat[new Rect(0, 0, mat.Width, mat.Height - 10)];
            using Mat black = new Mat(mat.Size(), mat.Type(), Scalar.Black);
            using Mat scrolled = black.Clone();    // 一个向下平移了10像素的图
            using Mat pos = scrolled[new Rect(0, 10, mat.Width, mat.Height - 10)];
            cropped.CopyTo(pos);

            //
            bool result1 = GridScroller.IsScrolling(mat, scrolled, out Point2d shift, upperThreshold: 0.99);
            bool result2 = GridScroller.IsScrolling(mat, mat, out Point2d _);
            bool result3 = GridScroller.IsScrolling(mat, black, out Point2d _);

            //
            Assert.True(result1);
            Assert.True(shift.Y <= 10 && shift.Y > 9.9);
            Assert.False(result2);
            Assert.False(result3);
        }

        public static IEnumerable<object[]> GetGridItemIconTextTestData()
        {
            yield return new object[] { @"GetGridIcons\FoodGrid.png", 8, new[] { 1850, 1073, 167, 85, 69, 6, 1, 90,
            6, 5, 1, 10, 15, 3, 2, 1,
            2, 2, 2, 1, 3, 2, 2, 4,
            1, 1, 3, 2, 2, 1, 1, 1} };
        }

        [Fact]
        public void FixedCountCropCanRecognizeDigitsWithoutDetection()
        {
            using var mat = new Mat(@"..\..\..\Assets\GetGridIcons\FoodGrid.png");
            var cells = GridCell.ClusterToCells(GridScreen.GridEnumerator.GetGridItems(mat, 8, true), 10)
                .OrderBy(cell => cell.RowNum).ThenBy(cell => cell.ColNum).ToArray();
            using var item = mat.SubMat(cells[2].Rect);
            using var count = item.SubMat(item.Height * 128 / 153, item.Height * 150 / 153,
                item.Width * 5 / 125, item.Width * 120 / 125);
            using var resized = count.Resize(new Size(count.Width * 2, count.Height * 2));
            Assert.Equal("167", paddle.Get().OcrWithoutDetector(resized));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(255)]
        public void BlankCountCropDoesNotBecomeAQuantity(int color)
        {
            using var item = new Mat(153, 125, MatType.CV_8UC3, Scalar.All(color));
            Assert.Equal(string.Empty, item.GetGridItemIconText(paddle.Get()));
        }

        [Theory]
        [MemberData(nameof(GetGridItemIconTextTestData))]
        /// <summary>
        /// 测试获取各种界面中的物品图标文字，全部转数字，结果应正确
        /// </summary>
        public void GetGridItemIconText_Number_ShouldBeRight(string screenshot, int columns, int[] numbers)
        {
            //
            using Mat mat = new Mat(@$"..\..\..\Assets\{screenshot}");

            //
            var gridItems = GridScreen.GridEnumerator.GetGridItems(mat, columns, findContoursAlpha: true);
            var cells = GridCell.ClusterToCells(gridItems, 10).OrderBy(c => c.RowNum).ThenBy(c => c.ColNum);

            var result = new List<string>();
            foreach (var cell in cells)
            {
                using Mat gridItemMat = mat.SubMat(cell.Rect);
                string numStr = gridItemMat.GetGridItemIconText(paddle.Get());
                result.Add(numStr);
            }

            //
            Assert.Equal(numbers.Length, result.Count);
            for (int i = 0; i < numbers.Length; i++)
            {
                Assert.True(int.TryParse(result[i], out int intResult), $"第{i + 1}个图标文字解析失败-->{result[i]}<--");
                Assert.Equal(numbers[i], intResult);
            }
        }
    }
}
