using BetterGenshinImpact.GameTask.Model.GameUI;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.Model.GameUI;

public class YasPaginationCursorTests
{
    private const int Columns = 8;
    private const int VisibleRows = 5;

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(47)]
    [InlineData(48)]
    [InlineData(79)]
    [InlineData(80)]
    [InlineData(81)]
    public void KnownTotals_ProduceExactlyOneCopyOfEveryItem(int totalItems)
    {
        AssertCompleteTrajectory(totalItems);
    }

    [Fact]
    public void RandomKnownTotals_ProduceExactlyOneCopyOfEveryItem()
    {
        var random = new Random(0x59A5);

        for (var sample = 0; sample < 500; sample++)
        {
            AssertCompleteTrajectory(random.Next(1, 10_001));
        }
    }

    [Theory]
    [InlineData(1, 0, 1, 1, true)]
    [InlineData(8, 0, 1, 8, true)]
    [InlineData(39, 0, 5, 39, true)]
    [InlineData(40, 0, 5, 40, true)]
    [InlineData(41, 0, 5, 40, false)]
    public void FirstPage_DescribesOnlyVisibleItems(
        int totalItems,
        int expectedStartRow,
        int expectedRowCount,
        int expectedItemCount,
        bool expectedFinalPage)
    {
        var cursor = CreateCursor(totalItems);

        Assert.Equal(
            new YasPageSlice(0, expectedStartRow, expectedRowCount, expectedItemCount, expectedFinalPage),
            cursor.CurrentPage);
    }

    [Theory]
    [InlineData(41, 1, 4, 1, 1)]
    [InlineData(47, 1, 4, 1, 7)]
    [InlineData(48, 1, 4, 1, 8)]
    [InlineData(79, 5, 0, 5, 39)]
    [InlineData(80, 5, 0, 5, 40)]
    public void SecondPage_UsesYasBottomStartRowSemantics(
        int totalItems,
        int expectedScrollRows,
        int expectedStartRow,
        int expectedRowCount,
        int expectedItemCount)
    {
        var cursor = CreateCursor(totalItems);

        cursor.CommitRead();
        var plan = cursor.PlanNextScroll();
        Assert.Equal(expectedScrollRows, plan.RowsToScroll);
        cursor.CommitScroll(Receipt(expectedScrollRows));

        Assert.Equal(
            new YasPageSlice(1, expectedStartRow, expectedRowCount, expectedItemCount, true),
            cursor.CurrentPage);
    }

    [Fact]
    public void CompletedCursor_CannotReadPlanOrScrollAgain()
    {
        var cursor = CreateCursor(1);

        cursor.CommitRead();

        Assert.True(cursor.Completed);
        Assert.Throws<InvalidOperationException>(() => cursor.CommitRead());
        Assert.Throws<InvalidOperationException>(() => cursor.PlanNextScroll());
        Assert.Throws<InvalidOperationException>(() => cursor.CommitScroll(Receipt(1)));
    }

    [Fact]
    public void StateTransitions_CannotBeSkippedOrRepeated()
    {
        var cursor = CreateCursor(41);

        Assert.Throws<InvalidOperationException>(() => cursor.PlanNextScroll());
        Assert.Throws<InvalidOperationException>(() => cursor.CommitScroll(Receipt(1)));

        cursor.CommitRead();
        Assert.Throws<InvalidOperationException>(() => cursor.CommitRead());

        var plan = cursor.PlanNextScroll();
        Assert.Throws<InvalidOperationException>(() => cursor.PlanNextScroll());
        Assert.Throws<InvalidOperationException>(() => cursor.CommitScroll(
            Receipt(plan.RowsToScroll + 1)));

        cursor.CommitScroll(Receipt(plan.RowsToScroll));
        Assert.Throws<InvalidOperationException>(() => cursor.CommitScroll(
            Receipt(plan.RowsToScroll)));
    }

    [Fact]
    public void ScrollReceiptMustSettleAndConfirmExactlyThePlannedRows()
    {
        var cursor = CreateCursor(41);
        cursor.CommitRead();
        var plan = cursor.PlanNextScroll();

        Assert.Throws<InvalidOperationException>(() => cursor.CommitScroll(
            new YasScrollReceipt(
                plan.RowsToScroll,
                plan.RowsToScroll,
                Settled: false,
                PhysicallyVerified: true)));
        Assert.Throws<InvalidOperationException>(() => cursor.CommitScroll(
            new YasScrollReceipt(
                plan.RowsToScroll,
                0,
                Settled: true,
                PhysicallyVerified: true)));
        Assert.Throws<InvalidOperationException>(() => cursor.CommitScroll(
            new YasScrollReceipt(
                plan.RowsToScroll,
                plan.RowsToScroll,
                Settled: true,
                PhysicallyVerified: false)));
    }

    private static YasPaginationCursor CreateCursor(int totalItems) =>
        new(totalItems, Columns, VisibleRows, VisibleRows);

    private static void AssertCompleteTrajectory(int totalItems)
    {
        var cursor = CreateCursor(totalItems);
        var observedItems = new List<int>(totalItems);
        var screenTopRow = 0;
        var expectedPageIndex = 0;

        while (!cursor.Completed)
        {
            var page = cursor.CurrentPage;
            Assert.Equal(expectedPageIndex++, page.PageIndex);

            var firstItem = (screenTopRow + page.StartRow) * Columns;
            for (var offset = 0; offset < page.ItemCount; offset++)
            {
                observedItems.Add(firstItem + offset);
            }

            cursor.CommitRead();
            if (cursor.Completed)
            {
                Assert.True(page.IsFinalPage);
                break;
            }

            Assert.False(page.IsFinalPage);
            var plan = cursor.PlanNextScroll();
            Assert.InRange(plan.RowsToScroll, 1, VisibleRows);
            screenTopRow += plan.RowsToScroll;
            cursor.CommitScroll(Receipt(plan.RowsToScroll));
        }

        Assert.Equal(Enumerable.Range(0, totalItems), observedItems);
        Assert.Equal(totalItems, observedItems.Distinct().Count());
    }

    private static YasScrollReceipt Receipt(int rows) =>
        new(rows, rows, Settled: true, PhysicallyVerified: true);
}

public class UnknownExtentYasCursorTests
{
    private const int PageRows = 6;

    [Fact]
    public void InitialPage_StartsAtZeroAndIsNotFinal()
    {
        var cursor = new UnknownExtentYasCursor(PageRows);

        Assert.Equal(new UnknownExtentPageSlice(0, 0, PageRows, false), cursor.CurrentPage);
        Assert.False(cursor.Completed);
    }

    [Fact]
    public void FullFullPartialTrajectory_ReadsOnlyNewRowsThenCompletes()
    {
        var cursor = new UnknownExtentYasCursor(PageRows);
        var pages = new List<UnknownExtentPageSlice> { cursor.CurrentPage };

        CommitPageAndScroll(cursor, 6);
        pages.Add(cursor.CurrentPage);
        CommitPageAndScroll(cursor, 6);
        pages.Add(cursor.CurrentPage);
        CommitPageAndScroll(cursor, 3);
        pages.Add(cursor.CurrentPage);

        Assert.Equal(
            [
                new UnknownExtentPageSlice(0, 0, 6, false),
                new UnknownExtentPageSlice(1, 0, 6, false),
                new UnknownExtentPageSlice(2, 0, 6, false),
                new UnknownExtentPageSlice(3, 3, 3, true),
            ],
            pages);
        Assert.False(cursor.Completed);

        cursor.CommitRead();

        Assert.True(cursor.Completed);
        Assert.Throws<InvalidOperationException>(() => cursor.PlanScroll());
        Assert.Throws<InvalidOperationException>(() =>
            cursor.CommitScroll(new UnknownExtentScrollReceipt(6, 0, true)));
    }

    [Fact]
    public void FullThenZeroTrajectory_CompletesWithoutCreatingAnotherPage()
    {
        var cursor = new UnknownExtentYasCursor(PageRows);

        CommitPageAndScroll(cursor, 6);
        var lastReadablePage = cursor.CurrentPage;
        cursor.CommitRead();
        var plan = cursor.PlanScroll();
        cursor.CommitScroll(new UnknownExtentScrollReceipt(plan.RequestedRows, 0, true));

        Assert.True(cursor.Completed);
        Assert.Equal(lastReadablePage, cursor.CurrentPage);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 4)]
    [InlineData(3, 3)]
    [InlineData(4, 2)]
    [InlineData(5, 1)]
    public void PartialBottomPage_StartsAfterOverlappingRows(
        int confirmedRows,
        int expectedStartRow)
    {
        var cursor = new UnknownExtentYasCursor(PageRows);

        CommitPageAndScroll(cursor, confirmedRows);

        Assert.Equal(
            new UnknownExtentPageSlice(1, expectedStartRow, confirmedRows, true),
            cursor.CurrentPage);
        Assert.False(cursor.Completed);

        cursor.CommitRead();
        Assert.True(cursor.Completed);
    }

    [Theory]
    [InlineData(6, 6, false)]
    [InlineData(6, -1, true)]
    [InlineData(6, 7, true)]
    [InlineData(5, 5, true)]
    public void InvalidReceipt_FailsClosedWithoutAdvancing(
        int requestedRows,
        int confirmedRows,
        bool isStable)
    {
        var cursor = new UnknownExtentYasCursor(PageRows);
        var initialPage = cursor.CurrentPage;
        cursor.CommitRead();
        var plan = cursor.PlanScroll();

        Assert.Throws<InvalidOperationException>(() =>
            cursor.CommitScroll(new UnknownExtentScrollReceipt(requestedRows, confirmedRows, isStable)));
        Assert.False(cursor.Completed);
        Assert.Equal(initialPage, cursor.CurrentPage);

        cursor.CommitScroll(new UnknownExtentScrollReceipt(plan.RequestedRows, 1, true));
        Assert.Equal(1, cursor.CurrentPage.PageIndex);
    }

    [Fact]
    public void AbandonedOrCancelledScroll_DoesNotCommitAnyProgress()
    {
        var cursor = new UnknownExtentYasCursor(PageRows);
        var initialPage = cursor.CurrentPage;

        cursor.CommitRead();
        var plan = cursor.PlanScroll();

        Assert.Equal(PageRows, plan.RequestedRows);
        Assert.False(cursor.Completed);
        Assert.Equal(initialPage, cursor.CurrentPage);
        Assert.Throws<InvalidOperationException>(() => cursor.CommitRead());
    }

    [Fact]
    public void TransactionOrder_CannotBeSkippedOrRepeated()
    {
        var cursor = new UnknownExtentYasCursor(PageRows);

        Assert.Throws<InvalidOperationException>(() => cursor.PlanScroll());
        Assert.Throws<InvalidOperationException>(() =>
            cursor.CommitScroll(new UnknownExtentScrollReceipt(6, 6, true)));

        cursor.CommitRead();
        Assert.Throws<InvalidOperationException>(() => cursor.CommitRead());
        cursor.PlanScroll();
        Assert.Throws<InvalidOperationException>(() => cursor.PlanScroll());
        cursor.CommitScroll(new UnknownExtentScrollReceipt(6, 6, true));
        Assert.Throws<InvalidOperationException>(() =>
            cursor.CommitScroll(new UnknownExtentScrollReceipt(6, 6, true)));
    }

    private static void CommitPageAndScroll(UnknownExtentYasCursor cursor, int confirmedRows)
    {
        cursor.CommitRead();
        var plan = cursor.PlanScroll();
        cursor.CommitScroll(new UnknownExtentScrollReceipt(
            plan.RequestedRows,
            confirmedRows,
            true));
    }
}
