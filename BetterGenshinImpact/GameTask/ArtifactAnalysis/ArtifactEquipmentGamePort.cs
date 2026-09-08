using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.CharacterDevelopment;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.GameUI;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

/// <summary>Runs only within the existing solo-task/input owner. Every equip is
/// followed by a complete observed inventory, not assumed UI success.</summary>
internal sealed class ArtifactEquipmentGamePort(ILogger logger) : IArtifactEquipmentPort
{
    private int _inventoryCount;
    public async Task<ArtifactSnapshotDto> ObserveAsync(string uid, CancellationToken ct)
    {
        var scan = new ArtifactInventoryScanTask(uid, ct);
        await scan.Start(ct);
        var observation = scan.Result ?? throw new InvalidDataException("Equipment observation did not complete.");
        _inventoryCount = observation.ArtifactCount;
        return observation;
    }

    public async Task EquipAsync(ArtifactEquipTargetDto target, ArtifactItemDto observedItem, CancellationToken ct)
    {
        await ArtifactInventoryNavigation.PrepareAsync(logger, ct);
        using var reader = new ArtifactInventoryUi(logger);
        if (reader.ReadArtifactCount() != _inventoryCount) throw new InvalidDataException("Inventory changed before equip.");
        using var initial = CaptureToRectArea();
        var grid = new GridScreen(GridParams.ArtifactsForCapture(initial.SrcMat.Size(), _inventoryCount), logger, ct);
        var index = 0;
        var found = false;
        await foreach (var (page, rectangle) in grid.WithCancellation(ct))
        {
            if (index++ != observedItem.ScanIndex) continue;
            var current = await reader.ReadItemAsync(page, rectangle, observedItem.ScanIndex, ct);
            if (current.ContentFingerprint != observedItem.ContentFingerprint || current.Locked != observedItem.Locked)
                throw new InvalidDataException("Artifact changed after equipment preflight.");
            found = true;
            break;
        }
        if (!found) throw new InvalidDataException("Reviewed artifact could not be found.");
        using var ocr = new ArtifactPaddleOcrSession();
        bool Click(string[] labels, bool lowerRight = false)
        {
            using var capture = CaptureToRectArea();
            var regions = capture.FindMulti(RecognitionObject.Ocr(capture.ToRect()), ocrService: ocr.Service).ToArray();
            try
            {
                var matches = regions.Where(r => labels.Contains(r.Text.Replace(" ", "").Trim())
                    && (!lowerRight || r.X > capture.Width / 2 && r.Y > capture.Height * .6)).ToArray();
                if (matches.Length != 1) return false;
                ct.ThrowIfCancellationRequested();
                matches[0].Click();
                return true;
            }
            finally { foreach (var region in regions) region.Dispose(); }
        }
        string ReadText()
        {
            using var capture = CaptureToRectArea();
            var regions = capture.FindMulti(RecognitionObject.Ocr(capture.ToRect()), ocrService: ocr.Service).ToArray();
            try { return string.Join("", regions.Select(r => r.Text.Replace(" ", ""))); }
            finally { foreach (var region in regions) region.Dispose(); }
        }
        if (!Click(["装备", "替换"], lowerRight: true)) throw new InvalidDataException("Reviewed equip control is not uniquely visible.");
        await Delay(300, ct);
        var text = ReadText();
        if (!text.Contains("选择角色", StringComparison.Ordinal) && !text.Contains("角色选择", StringComparison.Ordinal))
            throw new InvalidDataException("Character selection state was not confirmed; stopping equipment input.");
        using var recognizer = new AvatarGridIconRecognizer();
        var choice = await CharacterSelectionHelper.FindAndClickAvatar(CharacterSelectionHelper.CreateTarget(target.NativeName),
            recognizer, TaskContext.Instance().SystemInfo.AssetScale, logger, ct);
        if (choice is null) throw new InvalidDataException("Target character could not be identified.");
        await Delay(250, ct);
        text = ReadText();
        if (text.Contains("圣遗物", StringComparison.Ordinal) && text.Contains("装备", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(observedItem.Location) && text.Contains(observedItem.Location, StringComparison.Ordinal))
        {
            // Only acknowledge a borrowing/replacement dialogue tied to the
            // observed donor. Never click generic confirmation elsewhere.
            _ = Click(["确认", "确定"]);
        }
        await Delay(250, ct);
    }
}
