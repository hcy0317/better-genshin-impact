using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoArtifactSalvage;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Model.GameUI;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask.Common.Job
{
    internal partial class CountInventoryItem : ISoloTask<object>
    {
        public string Name => "背包数物品";

        private readonly ILogger logger = App.GetLogger<CountInventoryItem>();
        private IInputChannel input => InputHub.Foreground;
        private CancellationToken ct;
        private readonly GridScreenName? gridScreenName;
        private readonly IReadOnlyCollection<string> itemNames;
        private readonly ItemIconRecognitionMode iconRecognitionMode;
        private readonly bool stopByItemSort;
        private readonly string? itemName;
        private readonly bool includeScanEvidence;

        public CountInventoryItem(CountInventoryItemParam param)
        {
            if (param == null)
            {
                throw new ArgumentNullException(nameof(param));
            }

            param.UseDefaultIconRecognitionMode(TaskContext.Instance().Config.OtherConfig.ItemIconRecognitionMode);
            param.Validate();

            this.gridScreenName = param.GridScreenName;
            List<string> distinctItemNames = (param.ItemName == null ? param.ItemNames : new List<string> { param.ItemName }).Distinct().ToList();
            if (param.ItemName == null && distinctItemNames.Count != param.ItemNames.Count)
            {
                logger.LogWarning("目标物品列表存在重复名称，已自动去重：{original} → {distinct}", param.ItemNames.Count, distinctItemNames.Count);
            }
            this.itemNames = distinctItemNames;
            this.iconRecognitionMode = param.IconRecognitionMode;
            this.stopByItemSort = param.StopByItemSort;
            this.itemName = param.ItemName;
            this.includeScanEvidence = param.IncludeScanEvidence;
        }

        public async Task<object> Start(CancellationToken ct)
        {
            try
            {
                return await RunWithTimeout<object>(async scanToken =>
                {
                    this.ct = scanToken;
                    var batches = BuildPageBatches();
                    using IItemIconRecognizer recognizer = ItemIconRecognizerFactory.Create(iconRecognitionMode);
                    var results = new Dictionary<string, int>();
                    InventoryScanEvidence? evidence = null;
                    await new ReturnMainUiTask().Start(scanToken);
                    for (var index = 0; index < batches.Count; index++)
                    {
                        var (page, names) = batches[index];
                        scanToken.ThrowIfCancellationRequested();
                        await RequireInventoryOpenAsync(() => index == 0
                            ? AutoArtifactSalvageTask.TryOpenInventory(page, input, logger, scanToken)
                            : AutoArtifactSalvageTask.TrySwitchInventoryTab(page, input, logger, scanToken), scanToken);
                        if (includeScanEvidence)
                        {
                            if (names.Any(name => !recognizer.SupportsName(name)))
                                throw new InvalidOperationException("当前物品模型不包含请求名称，不能证明缺项为零");
                            await ResetPreciousInventoryToTopAsync(scanToken);
                            evidence = new(names, CreateGridParams(page).Columns);
                        }
                        logger.LogInformation("在{page}寻找{first}等，共{count}类物品……", page, names.First(), names.Count);
                        await ScanPageForTargets(recognizer, page, names, results, evidence);
                    }
                    await new ReturnMainUiTask().Start(scanToken);
                    if (itemName != null) return results.GetValueOrDefault(itemName, -1);
                    return evidence == null ? results : evidence.Finish(results);
                }, ct);
            }
            catch (TimeoutException exception)
            {
                logger.LogWarning(exception, "背包计数超时：页面={Grid}，材料={Names}；未确认数量保留未知，不继续无限翻页",
                    gridScreenName, string.Join("、", itemNames));
                throw;
            }
            finally { this.ct = ct; }
        }

        internal static async Task<T> RunWithTimeout<T>(Func<CancellationToken, Task<T>> scan,
            CancellationToken ct, TimeSpan? timeout = null)
        {
            ct.ThrowIfCancellationRequested();
            var budget = timeout ?? TimeSpan.FromMinutes(3);
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            scanCts.CancelAfter(budget);
            try
            {
                // 合作取消后等待当前扫描及其 finally 完成，不能遗留后台滚动/输入任务。
                var result = await scan(scanCts.Token);
                ct.ThrowIfCancellationRequested();
                scanCts.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception exception) when (!ct.IsCancellationRequested && scanCts.IsCancellationRequested
                && exception is OperationCanceledException or NormalEndException)
            {
                throw new TimeoutException($"背包计数扫描超过 {budget.TotalSeconds:0.###} 秒预算；未确认物品数量保持未知", exception);
            }
        }

        internal static async Task RequireInventoryOpenAsync(Func<Task<bool>> open, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var confirmed = await open();
            ct.ThrowIfCancellationRequested();
            if (!confirmed) throw new InvalidOperationException("背包目标分类页未确认，不开始计数或推断零库存");
        }

        /// <summary>
        /// 构造扫描批次：传入 gridScreenName 时直接构造单批次；未传入时自动按所在背包页分组。
        /// </summary>
        /// <returns>按背包页顺序排序的批次列表。</returns>
        private List<(GridScreenName Page, List<string> PageItemNames)> BuildPageBatches()
        {

            if (this.gridScreenName.HasValue)
            {
                return [(this.gridScreenName.Value, this.itemNames.ToList())];
            }

            // 未传入 gridScreenName 时，自动按所在背包页分组
            var batchesByPage = new Dictionary<GridScreenName, List<string>>();
            List<string> skipped = [];
            foreach (string name in this.itemNames)
            {
                if (ItemMetadataCache.TryGetPage(name, out GridScreenName page))
                {
                    if (!batchesByPage.TryGetValue(page, out var list))
                    {
                        list = [];
                        batchesByPage[page] = list;
                    }
                    list.Add(name);
                }
                else
                {
                    skipped.Add(name);
                }
            }

            if (skipped.Count > 0)
            {
                throw new InvalidOperationException($"以下目标物品无法解析所在的背包页面: {string.Join(", ", skipped)}");
            }

            var pageBatches = new List<(GridScreenName Page, List<string> PageItemNames)>();
            foreach (GridScreenName page in Enum.GetValues<GridScreenName>().OrderBy(p => (int)p))
            {
                if (batchesByPage.TryGetValue(page, out var list))
                {
                    pageBatches.Add((page, list));
                }
            }

            if (pageBatches.Count == 0)
            {
                throw new InvalidOperationException("无法解析任何目标物品所在的背包页面");
            }

            return pageBatches;
        }

        /// <summary>
        /// 扫描当前页的目标物品
        /// </summary>
        /// <param name="iconRecognizer">物品图标识别器。</param>
        /// <param name="page">当前扫描页面。</param>
        /// <param name="pageItemNames">本页目标物品名列表。</param>
        /// <param name="results">累积结果字典。</param>
        private async Task ScanPageForTargets(IItemIconRecognizer iconRecognizer, GridScreenName page, List<string> pageItemNames, Dictionary<string, int> results, InventoryScanEvidence? evidence = null)
        {
            GridScreen gridScreen = new GridScreen(CreateGridParams(page), logger, ct);
            gridScreen.OnAfterTurnToNewPage += GridScreen.DrawItemsAfterTurnToNewPage;
            gridScreen.OnBeforeScroll += () => TaskContext.Instance().Runtime?.MaskWindowDrawingBoard.ClearAll();

            var observation = evidence == null ? null : new InventoryPageEvidenceObserver(evidence, CreateGridParams(page), ct);
            observation?.Attach(gridScreen);

            // 本页尚未找到的目标物品
            List<string> notFound = pageItemNames.Where(name => !results.ContainsKey(name)).ToList();

            try
            {
                // 如果包含武器页的武器经验道具，直接翻页到最底部
                bool hasOre = pageItemNames.Any(name => name.StartsWith("精锻用"));
                if (page == GridScreenName.Weapons && hasOre)
                {
                    await PreScrollToBottomForWeaponOre();
                }

                // 覆盖证明必须完成保守分页，排序提前终止不能冒充零库存证明。
                int? maxSortOfItem = evidence == null ? ComputeMaxSortOfItem(page, pageItemNames) : null;

                await foreach ((ImageRegion pageRegion, Rect itemRect) in gridScreen)
                {
                    ct.ThrowIfCancellationRequested();
                    using ImageRegion itemRegion = pageRegion.DeriveCrop(itemRect);
                    string? predName = RecognizeItemName(itemRegion, iconRecognizer);
                    observation?.RecordItem(predName);
                    if (predName == null)
                    {
                        continue;
                    }

                    // 背包物品顺序固定；遍历到排序晚于目标的条目仍未找到目标，即可提前退出扫描。
                    if (maxSortOfItem.HasValue
                        && ItemMetadataCache.TryGetSortOrder(predName, out int predSort)
                        && predSort > maxSortOfItem.Value)
                    {
                        logger.LogInformation("识别到 {name} 页有物品排序 (sort_order={sort}) 超过阈值 {threshold}，提前结束 {page} 扫描", predName, predSort, maxSortOfItem.Value, page);
                        break;
                    }

                    if (pageItemNames.Contains(predName) && !results.ContainsKey(predName))
                    {
                        int count = ReadItemCount(itemRegion);
                        results.TryAdd(predName, count);
                        notFound.RemoveAll(n => n == predName);

                        if (notFound.Count == 0)
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                TaskContext.Instance().Runtime?.MaskWindowDrawingBoard.ClearAll();
            }
            observation?.FinishPage();
        }

        private int? ComputeMaxSortOfItem(GridScreenName page, List<string> pageItemNames)
        {
            if (!this.stopByItemSort)
            {
                return null;
            }

            int maxSort = int.MinValue;
            foreach (string pageItemName in pageItemNames)
            {
                if (!ItemMetadataCache.TryGetSortOrder(pageItemName, out int sort))
                {
                    logger.LogWarning("无法获取目标物品 {name} 在 {page} 的排序值，不启用按物品顺序提前结束扫描", pageItemName, page);
                    return null;
                }
                if (sort > maxSort)
                {
                    maxSort = sort;
                }
            }
            return maxSort;
        }

        private GridParams CreateGridParams(GridScreenName page)
        {
            var parameters = GridParams.Templates[page];
            return includeScanEvidence ? parameters.WithConservativePaging() : parameters;
        }

        private async Task PreScrollToBottomForWeaponOre()
        {
            // 长按滑动栏底部，快速翻页到底部后，再继续滚动确保在最后一页
            GameCaptureRegion.GameRegion1080PPosMove(1289, 936);
            try
            {
                GlobalMethod.LeftButtonDown();
                await TaskControl.Delay(2000, ct);
            }
            finally
            {
                GlobalMethod.LeftButtonUp();
            }
            var gridScroller = new GridScroller(GridParams.Templates[GridScreenName.Weapons], logger, input, ct);
            while (await gridScroller.TryVerticalScollDown((src, columns) => GridScreen.GridEnumerator.GetGridItems(src, columns)))
            {
                await TaskControl.Delay(300, ct);
            }
        }

        private static string? RecognizeItemName(ImageRegion itemRegion, IItemIconRecognizer iconRecognizer)
        {
            using Mat icon = itemRegion.SrcMat.GetGridIcon();
            return iconRecognizer.Recognize(icon);
        }

        private int ReadItemCount(ImageRegion itemRegion)
        {
            using GridItemCountRecognitionResult result =
                GridItemCountRecognizer.RecognizeCropped(itemRegion.SrcMat, OcrFactory.Paddle);
            if (result.Count >= 0)
            {
                return result.Count;
            }

            logger.LogWarning("无法识别数量，OCR 原文：{Text}，原因：{Reason}", result.RawText, result.Reason);
            return -2;
        }

        async Task ISoloTask.Start(CancellationToken ct)
        {
            await Start(ct);
        }
    }
}
