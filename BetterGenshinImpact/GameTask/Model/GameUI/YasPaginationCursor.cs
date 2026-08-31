using System;

namespace BetterGenshinImpact.GameTask.Model.GameUI;

internal readonly record struct YasPageSlice(
    int PageIndex,
    int StartRow,
    int RowCount,
    int ItemCount,
    bool IsFinalPage);

internal readonly record struct YasScrollPlan(int RowsToScroll);

internal readonly record struct YasScrollReceipt(
    int RequestedRows,
    int ConfirmedRows,
    bool Settled,
    bool PhysicallyVerified);

/// <summary>
/// Known-total pagination state used by YAS-style grid scans.
/// Physical scrolling remains outside this type; this cursor only owns the
/// monotonic relationship between committed reads, logical row advances and
/// the portion of the next viewport that has not been read before.
/// </summary>
internal sealed class YasPaginationCursor
{
    private enum CursorState
    {
        AwaitingRead,
        AwaitingScrollPlan,
        AwaitingScrollCommit,
        Completed,
    }

    private readonly int _totalItems;
    private readonly int _columns;
    private readonly int _visibleRows;
    private readonly int _maxScrollRows;
    private CursorState _state = CursorState.AwaitingRead;
    private int _readItems;
    private YasScrollPlan _pendingScroll;

    internal YasPaginationCursor(
        int totalItems,
        int columns,
        int visibleRows,
        int maxScrollRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(visibleRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxScrollRows);
        if (maxScrollRows > visibleRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxScrollRows),
                maxScrollRows,
                "每次滚动行数不能超过可见行数。");
        }

        _totalItems = totalItems;
        _columns = columns;
        _visibleRows = visibleRows;
        _maxScrollRows = maxScrollRows;

        var initialRows = Math.Min(visibleRows, DivideRoundUp(totalItems, columns));
        var initialItems = Math.Min(totalItems, initialRows * columns);
        CurrentPage = new YasPageSlice(
            PageIndex: 0,
            StartRow: 0,
            RowCount: initialRows,
            ItemCount: initialItems,
            IsFinalPage: initialItems == totalItems);
    }

    internal YasPageSlice CurrentPage { get; private set; }

    internal bool Completed => _state == CursorState.Completed;

    internal int TotalItems => _totalItems;

    internal int EmittedItems => _readItems;

    internal void CommitRead()
    {
        EnsureState(CursorState.AwaitingRead, "当前页不处于可提交读取的状态。");

        _readItems += CurrentPage.ItemCount;
        if (_readItems == _totalItems)
        {
            _state = CursorState.Completed;
            return;
        }

        if (_readItems > _totalItems)
        {
            throw new InvalidOperationException("分页读取数超过已知总数。");
        }

        _state = CursorState.AwaitingScrollPlan;
    }

    internal YasScrollPlan PlanNextScroll()
    {
        EnsureState(CursorState.AwaitingScrollPlan, "当前状态不能规划下一次滚动。");

        var remainingItems = _totalItems - _readItems;
        var remainingRows = DivideRoundUp(remainingItems, _columns);
        _pendingScroll = new YasScrollPlan(Math.Min(_maxScrollRows, remainingRows));
        _state = CursorState.AwaitingScrollCommit;
        return _pendingScroll;
    }

    internal void CommitScroll(YasScrollReceipt receipt)
    {
        EnsureState(CursorState.AwaitingScrollCommit, "当前状态没有等待提交的滚动计划。");
        if (!receipt.Settled || !receipt.PhysicallyVerified ||
            receipt.RequestedRows != _pendingScroll.RowsToScroll ||
            receipt.ConfirmedRows != _pendingScroll.RowsToScroll)
        {
            throw new InvalidOperationException(
                $"滚动回执必须稳定、物理验证且确认 {_pendingScroll.RowsToScroll} 行，实际请求 {receipt.RequestedRows}、确认 {receipt.ConfirmedRows}、稳定={receipt.Settled}、物理验证={receipt.PhysicallyVerified}。");
        }

        var remainingItems = _totalItems - _readItems;
        var rowCount = _pendingScroll.RowsToScroll;
        var itemCount = Math.Min(remainingItems, rowCount * _columns);
        CurrentPage = new YasPageSlice(
            PageIndex: CurrentPage.PageIndex + 1,
            StartRow: _visibleRows - rowCount,
            RowCount: rowCount,
            ItemCount: itemCount,
            IsFinalPage: itemCount == remainingItems);
        _pendingScroll = default;
        _state = CursorState.AwaitingRead;
    }

    private void EnsureState(CursorState expected, string message)
    {
        if (_state != expected)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static int DivideRoundUp(int dividend, int divisor) =>
        (dividend + divisor - 1) / divisor;
}

internal readonly record struct UnknownExtentPageSlice(
    int PageIndex,
    int StartRow,
    int RowCount,
    bool IsFinalPage);

internal readonly record struct UnknownExtentScrollPlan(int RequestedRows);

internal readonly record struct UnknownExtentScrollReceipt(
    int RequestedRows,
    int ConfirmedRows,
    bool IsStable);

/// <summary>
/// Unknown-total variant of the YAS pagination cursor. The physical scrolling
/// layer is the sole source of confirmed row movement. A partial movement is
/// the final page, while zero movement completes without exposing another page.
/// </summary>
internal sealed class UnknownExtentYasCursor
{
    private enum CursorState
    {
        AwaitingRead,
        AwaitingScrollPlan,
        AwaitingScrollCommit,
        Completed,
    }

    private readonly int _pageRows;
    private CursorState _state = CursorState.AwaitingRead;
    private UnknownExtentScrollPlan _pendingScroll;

    internal UnknownExtentYasCursor(int pageRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageRows);
        _pageRows = pageRows;
        CurrentPage = new UnknownExtentPageSlice(
            PageIndex: 0,
            StartRow: 0,
            RowCount: pageRows,
            IsFinalPage: false);
    }

    internal UnknownExtentPageSlice CurrentPage { get; private set; }

    internal bool Completed => _state == CursorState.Completed;

    internal void CommitRead()
    {
        EnsureState(CursorState.AwaitingRead, "当前页不处于可提交读取的状态。");
        _state = CurrentPage.IsFinalPage
            ? CursorState.Completed
            : CursorState.AwaitingScrollPlan;
    }

    internal UnknownExtentScrollPlan PlanScroll()
    {
        EnsureState(CursorState.AwaitingScrollPlan, "当前状态不能规划下一次滚动。");
        _pendingScroll = new UnknownExtentScrollPlan(_pageRows);
        _state = CursorState.AwaitingScrollCommit;
        return _pendingScroll;
    }

    internal void CommitScroll(UnknownExtentScrollReceipt receipt)
    {
        EnsureState(CursorState.AwaitingScrollCommit, "当前状态没有等待提交的滚动计划。");
        ValidateReceipt(receipt);

        if (receipt.ConfirmedRows == 0)
        {
            _pendingScroll = default;
            _state = CursorState.Completed;
            return;
        }

        var isFinalPage = receipt.ConfirmedRows < _pageRows;
        CurrentPage = new UnknownExtentPageSlice(
            PageIndex: CurrentPage.PageIndex + 1,
            StartRow: _pageRows - receipt.ConfirmedRows,
            RowCount: receipt.ConfirmedRows,
            IsFinalPage: isFinalPage);
        _pendingScroll = default;
        _state = CursorState.AwaitingRead;
    }

    private void ValidateReceipt(UnknownExtentScrollReceipt receipt)
    {
        if (!receipt.IsStable)
        {
            throw new InvalidOperationException("滚动回执未稳定，拒绝提交分页进度。");
        }

        if (receipt.RequestedRows != _pendingScroll.RequestedRows)
        {
            throw new InvalidOperationException(
                $"滚动回执请求行数 {receipt.RequestedRows} 与计划 {_pendingScroll.RequestedRows} 不符。");
        }

        if (receipt.ConfirmedRows < 0 || receipt.ConfirmedRows > receipt.RequestedRows)
        {
            throw new InvalidOperationException(
                $"滚动回执确认行数 {receipt.ConfirmedRows} 超出 0..{receipt.RequestedRows}。");
        }
    }

    private void EnsureState(CursorState expected, string message)
    {
        if (_state != expected)
        {
            throw new InvalidOperationException(message);
        }
    }
}
