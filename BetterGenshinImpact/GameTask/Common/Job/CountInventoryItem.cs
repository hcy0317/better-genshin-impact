using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoArtifactSalvage;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Model.GameUI;
using BetterGenshinImpact.View.Drawable;
using BetterGenshinImpact.Helpers;
using Fischless.WindowsInput;
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
    internal class CountInventoryItem : ISoloTask<object>
    {
        public string Name => "背包数物品";

        private readonly ILogger logger = App.GetLogger<CountInventoryItem>();
        private readonly InputSimulator input = Simulation.SendInput;
        private CancellationToken ct;
        private readonly GridScreenName gridScreenName;
        private readonly string? itemName;
        private readonly IReadOnlyCollection<string>? itemNames;
        private readonly ItemIconRecognitionMode iconRecognitionMode;

        public CountInventoryItem(CountInventoryItemParam param)
        {
            if (param == null)
            {
                throw new ArgumentNullException(nameof(param));
            }

            param.Validate();

            this.gridScreenName = param.GridScreenName;
            this.itemName = param.ItemName;
            this.itemNames = param.GetItemNamesOrNull()?.ToList();
            this.iconRecognitionMode = param.IconRecognitionMode;
        }

        public async Task<object> Start(CancellationToken ct)
        {
            if (this.itemName != null)
            {
                logger.LogInformation("打开背包并在{grid}寻找{name}……", this.gridScreenName, this.itemName!);
            }
            else
            {
                logger.LogInformation("打开背包并在{grid}寻找{first}等{count}类物品……", this.gridScreenName, this.itemNames!.First(), this.itemNames!.Count());
            }
            try
            {
                return await RunWithTimeout<object>(async scanToken =>
                {
                    // GridScreen 使用构造时传入的令牌，必须贯穿打开背包、预滚动和每页扫描。
                    this.ct = scanToken;
                    await new ReturnMainUiTask().Start(scanToken);
                    await AutoArtifactSalvageTask.OpenInventory(this.gridScreenName, input, logger, scanToken);
                    using IItemIconRecognizer iconRecognizer = ItemIconRecognizerFactory.Create(this.iconRecognitionMode);
                    object result = this.itemName != null
                        ? await FindOne(iconRecognizer)
                        : await FindMulti(iconRecognizer);
                    await new ReturnMainUiTask().Start(scanToken);
                    return result;
                }, ct);
            }
            catch (TimeoutException exception)
            {
                logger.LogWarning(exception, "背包计数超时：页面={Grid}，材料={Names}；未确认数量保留未知，不继续无限翻页",
                    this.gridScreenName, this.itemName ?? string.Join("、", this.itemNames!));
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

        private GridParams CreateGridParams()
        {
            return GridParams.Templates[this.gridScreenName];
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
            var gridScroller = new GridScroller(CreateGridParams(), logger, input, ct);
            while (await gridScroller.TryVerticalScollDown((src, columns) => GridScreen.GridEnumerator.GetGridItems(src, columns)))
            {
                await TaskControl.Delay(300, ct);
            }
        }

        private async Task<int> FindOne(IItemIconRecognizer iconRecognizer)
        {
            GridScreen gridScreen = new GridScreen(CreateGridParams(), logger, ct);
            gridScreen.OnAfterTurnToNewPage += GridScreen.DrawItemsAfterTurnToNewPage;
            gridScreen.OnBeforeScroll += () => VisionContext.Instance().DrawContent.ClearAll();
            int? count = null;
            try
            {
                //如果是武器页的武器经验道具，直接翻页到最底部
                if (gridScreenName == GridScreenName.Weapons && itemName!.StartsWith("精锻用"))
                {
                    await PreScrollToBottomForWeaponOre();
                }
                
                //开始识别
                await foreach ((ImageRegion pageRegion, Rect itemRect) in gridScreen)
                {
                    ct.ThrowIfCancellationRequested();
                    using ImageRegion itemRegion = pageRegion.DeriveCrop(itemRect);
                    string? predName = RecognizeItemName(itemRegion, iconRecognizer);
                    if (predName == null)
                    {
                        continue;
                    }

                    if (predName == this.itemName!)
                    {
                        count = ReadItemCount(itemRegion);
                        break;
                    }
                }
            }
            finally
            {
                VisionContext.Instance().DrawContent.ClearAll();
            }
            if (count == null)
            {
                count = -1;
                logger.LogInformation("没有找到{name}", this.itemName!);
            }
            return count.Value;
        }

        private async Task<Dictionary<string, int>> FindMulti(IItemIconRecognizer iconRecognizer)
        {
            Dictionary<string, int> itemsCountDic = new Dictionary<string, int>();
            List<string> notFoundItemNames = this.itemNames!.ToList();

            GridScreen gridScreen = new GridScreen(CreateGridParams(), logger, ct);
            gridScreen.OnAfterTurnToNewPage += GridScreen.DrawItemsAfterTurnToNewPage;
            gridScreen.OnBeforeScroll += () => VisionContext.Instance().DrawContent.ClearAll();
            try
            {
                //如果包含武器页的武器经验道具，直接翻页到最底部
                bool hasOre = itemNames!.Any(name => name.StartsWith("精锻用"));
                if (gridScreenName == GridScreenName.Weapons && hasOre)
                {
                    await PreScrollToBottomForWeaponOre();
                }
                await foreach ((ImageRegion pageRegion, Rect itemRect) in gridScreen)
                {
                    ct.ThrowIfCancellationRequested();
                    using ImageRegion itemRegion = pageRegion.DeriveCrop(itemRect);
                    string? predName = RecognizeItemName(itemRegion, iconRecognizer);
                    if (predName == null)
                    {
                        continue;
                    }

                    if (this.itemNames!.Contains(predName) && !itemsCountDic!.ContainsKey(predName))
                    {
                        int count = ReadItemCount(itemRegion);

                        if (!itemsCountDic!.TryAdd(predName, count))
                        {
                            logger.LogWarning("重复的名称：{name}", predName);
                        }

                        notFoundItemNames.RemoveAll(n => n == predName);

                        if (notFoundItemNames.Count <= 0)
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                VisionContext.Instance().DrawContent.ClearAll();
            }

            if (notFoundItemNames.Count > 0)
            {
                logger.LogInformation("没有找到{name}", String.Join(", ", notFoundItemNames));
            }
            return itemsCountDic;
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
