using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoArtifactSalvage;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Model.GameUI;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

public sealed class ArtifactNativePlanExecutor : IArtifactNativePlanExecutor
{
    public async Task ReplaceAllAsync(
        ArtifactNativeSyncPlanDto plan,
        CancellationToken cancellationToken)
    {
        var task = new ArtifactNativePlanTask(plan);
        await new TaskRunner().RunSoloTaskAsync(task, propagateExceptions: true);
        if (!task.Completed)
        {
            throw new InvalidOperationException("Native artifact plan replacement did not complete.");
        }
    }
}

internal sealed class ArtifactNativePlanTask(ArtifactNativeSyncPlanDto plan) : ISoloTask
{
    private readonly ILogger _logger = App.GetLogger<ArtifactNativePlanTask>();
    private readonly ArtifactSetCatalog _catalog = new(Path.Combine(
        AppContext.BaseDirectory, "GameTask", "ArtifactAnalysis", "Assets", "artifact-sets.zh.json"));
    private readonly Dictionary<string, ControlCalibration> _controlCalibrations = new(StringComparer.Ordinal);
    private static readonly string[] MainStatKeys =
    [
        "hp_", "atk_", "def_", "eleMas", "enerRech_", "critRate_", "critDMG_", "heal_",
        "anemo_dmg_", "cryo_dmg_", "dendro_dmg_", "electro_dmg_", "geo_dmg_",
        "hydro_dmg_", "physical_dmg_", "pyro_dmg_"
    ];
    private static readonly string[] SubstatKeys =
    ["hp", "atk", "def", "hp_", "atk_", "def_", "eleMas", "enerRech_", "critRate_", "critDMG_"];
    public string Name => "重建原神圣遗物锁定方案";
    public bool Completed { get; private set; }

    public async Task Start(CancellationToken ct)
    {
        if (!plan.ReplaceAll || !plan.RequiresPreMutationEvidence || plan.Plans.Count == 0)
        {
            throw new InvalidDataException("Native artifact plan is not a complete replacement plan.");
        }
        var grouped = plan.Plans.GroupBy(item => item.SetKey, StringComparer.Ordinal).ToArray();
        // Read-only phase: prove every target editor and required control exists.
        foreach (var group in grouped)
        {
            await OpenLockAssistanceAsync(ct);
            await SelectSetAsync(_catalog.LocalizedName(group.Key), ct);
            await PreflightEditorAsync(group.ToArray(), ct);
        }
        await OpenLockAssistanceAsync(ct);
        if (!HasText("快速删除方案"))
        {
            throw new InvalidDataException("Lock Assistance does not expose quick plan deletion.");
        }
        WritePreMutationEvidence(plan);

        await ClickTextAsync("快速删除方案", ct);
        await Delay(250, ct);
        using (var confirm = CaptureToRectArea())
        {
            if (!Bv.ClickBlackConfirmButton(confirm) && !TryClickText("确认"))
            {
                throw new InvalidDataException("Unable to confirm native plan deletion.");
            }
        }
        await Delay(450, ct);

        foreach (var group in grouped)
        {
            await OpenLockAssistanceAsync(ct);
            await SelectSetAsync(_catalog.LocalizedName(group.Key), ct);
            await ConfigureSetAsync(group.ToArray(), ct);
        }
        foreach (var group in grouped)
        {
            await OpenLockAssistanceAsync(ct);
            await SelectSetAsync(_catalog.LocalizedName(group.Key), ct);
            await VerifySetAsync(group.ToArray(), ct);
        }
        Completed = true;
        await new ReturnMainUiTask().Start(ct);
    }

    private async Task OpenLockAssistanceAsync(CancellationToken ct)
    {
        await ArtifactInventoryNavigation.PrepareAsync(_logger, ct);
        await ClickTextAsync("锁定辅助", ct);
        await Delay(450, ct);
    }

    private async Task SelectSetAsync(string localizedName, CancellationToken ct)
    {
        var screen = new ArtifactSetFilterScreen(
            GridParams.Templates[GridScreenName.ArtifactSetFilter], _logger, ct);
        await foreach ((ImageRegion page, Rect rect) in screen.WithCancellation(ct))
        {
            using var row = page.DeriveCrop(rect);
            var text = OcrFactory.Paddle.OcrResult(row.SrcMat).Text;
            if (!text.Contains(localizedName, StringComparison.Ordinal)) continue;
            row.Click();
            await Delay(250, ct);
            return;
        }
        throw new InvalidDataException($"Unable to find native artifact set '{localizedName}'.");
    }

    private async Task ConfigureSetAsync(
        IReadOnlyList<ArtifactNativeSetPlanDto> setPlans,
        CancellationToken ct)
    {
        if (!TryClickText("编辑") && !TryClickText("添加方案"))
        {
            throw new InvalidDataException("Unable to open native artifact plan editor.");
        }
        await Delay(250, ct);
        foreach (var slotPlan in setPlans)
        {
            if (!IsConfigurableMainSlot(slotPlan.SlotKey)) continue;
            await ClickTextAsync(SlotLabel(slotPlan.SlotKey), ct);
            var desired = slotPlan.MainStats.ToHashSet(StringComparer.Ordinal);
            await SetVisibleControlStatesAsync(
                slotPlan.SetKey, slotPlan.SlotKey, MainStatKeys, desired, ct);
        }
        await ClickTextAsync("副词条", ct);
        var desiredSubstats = setPlans.SelectMany(item => item.Substats)
            .ToHashSet(StringComparer.Ordinal);
        await SetVisibleControlStatesAsync(
            setPlans[0].SetKey, "substats", SubstatKeys, desiredSubstats, ct);
        await ClickTextAsync("保存", ct);
        await Delay(250, ct);
        if (!TryClickText("应用此方案") && !TryClickText("应用"))
        {
            throw new InvalidDataException("Unable to apply native artifact plan.");
        }
        await Delay(250, ct);
    }

    private async Task VerifySetAsync(
        IReadOnlyList<ArtifactNativeSetPlanDto> setPlans,
        CancellationToken ct)
    {
        if (!TryClickText("编辑"))
        {
            throw new InvalidDataException(
                $"Native artifact plan '{setPlans[0].SetKey}' was not visible after apply.");
        }
        await Delay(250, ct);
        foreach (var slotPlan in setPlans)
        {
            if (!IsConfigurableMainSlot(slotPlan.SlotKey)) continue;
            await ClickTextAsync(SlotLabel(slotPlan.SlotKey), ct);
            VerifyVisibleControlStates(
                slotPlan.SetKey,
                slotPlan.SlotKey,
                MainStatKeys,
                slotPlan.MainStats.ToHashSet(StringComparer.Ordinal));
        }
        await ClickTextAsync("副词条", ct);
        VerifyVisibleControlStates(
            setPlans[0].SetKey,
            "substats",
            SubstatKeys,
            setPlans.SelectMany(item => item.Substats).ToHashSet(StringComparer.Ordinal));
        await ClickTextAsync("取消", ct);
    }

    private async Task PreflightEditorAsync(
        IReadOnlyList<ArtifactNativeSetPlanDto> setPlans,
        CancellationToken ct)
    {
        if (!TryClickText("编辑") && !TryClickText("添加方案"))
        {
            throw new InvalidDataException("Unable to open native artifact plan editor during preflight.");
        }
        await Delay(250, ct);
        foreach (var slotPlan in setPlans)
        {
            if (!IsConfigurableMainSlot(slotPlan.SlotKey)) continue;
            await ClickTextAsync(SlotLabel(slotPlan.SlotKey), ct);
            await CalibrateVisibleControlsAsync(
                slotPlan.SetKey,
                slotPlan.SlotKey,
                MainStatKeys,
                slotPlan.MainStats,
                ct);
        }
        await ClickTextAsync("副词条", ct);
        await CalibrateVisibleControlsAsync(
            setPlans[0].SetKey,
            "substats",
            SubstatKeys,
            setPlans.SelectMany(item => item.Substats),
            ct);
        RequireText("保存");
        await ClickTextAsync("取消", ct);
    }

    private void WritePreMutationEvidence(ArtifactNativeSyncPlanDto targetPlan)
    {
        var root = Path.Combine(
            AppContext.BaseDirectory, "log", "artifact-native-plan-backup",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        using var capture = CaptureToRectArea();
        Cv2.ImWrite(Path.Combine(root, "before.png"), capture.SrcMat);
        File.WriteAllText(
            Path.Combine(root, "target-plan.json"),
            JsonSerializer.Serialize(targetPlan, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            Path.Combine(root, "README.txt"),
            "This captures pre-mutation evidence only. Genshin does not expose a restorable export for prior custom plans.");
    }

    private static bool HasText(string expected)
    {
        using var capture = CaptureToRectArea();
        return capture.FindMulti(RecognitionObject.Ocr(capture.ToRect()))
            .Any(region => ExactText(region.Text, expected));
    }

    private static void RequireText(string expected)
    {
        if (!HasText(expected))
        {
            throw new InvalidDataException(
                $"Unable to find native plan control '{expected}' during preflight.");
        }
    }

    private static bool TryClickText(string expected)
    {
        using var capture = CaptureToRectArea();
        var match = capture.FindMulti(RecognitionObject.Ocr(capture.ToRect()))
            .FirstOrDefault(region => ExactText(region.Text, expected));
        if (match is null) return false;
        match.Click();
        return true;
    }

    private static async Task ClickTextAsync(string expected, CancellationToken ct)
    {
        if (!TryClickText(expected))
        {
            throw new InvalidDataException($"Unable to find native plan control '{expected}'.");
        }
        await Delay(140, ct);
    }

    private static string ControlKey(string setKey, string section, string statKey) =>
        $"{setKey}|{section}|{statKey}";

    private async Task CalibrateVisibleControlsAsync(
        string setKey,
        string section,
        IEnumerable<string> candidateKeys,
        IEnumerable<string> requiredKeys,
        CancellationToken ct)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statKey in candidateKeys)
        {
            var label = StatLabel(statKey);
            if (!HasText(label)) continue;
            visible.Add(statKey);
            _controlCalibrations[ControlKey(setKey, section, statKey)] =
                await CalibrateControlAsync(label, ct);
        }
        var missing = requiredKeys.Distinct(StringComparer.Ordinal)
            .Where(key => !visible.Contains(key)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"Native plan editor is missing required controls: {string.Join(",", missing)}.");
        }
    }

    private static async Task<ControlCalibration> CalibrateControlAsync(
        string label,
        CancellationToken ct)
    {
        await MovePointerAwayAsync(ct);
        var before = CaptureSelectionScore(label);
        await ClickTextAsync(label, ct);
        await MovePointerAwayAsync(ct);
        var after = CaptureSelectionScore(label);
        await ClickTextAsync(label, ct);
        await MovePointerAwayAsync(ct);
        var restored = CaptureSelectionScore(label);
        if (Math.Abs(before - restored) > 0.02)
        {
            throw new InvalidDataException(
                $"Native plan control '{label}' did not return to its original state during preflight.");
        }
        if (Math.Abs(before - after) < 0.002)
        {
            throw new InvalidDataException(
                $"Native plan control '{label}' has no stable selectable visual state.");
        }
        return new ControlCalibration(Math.Max(before, after), Math.Min(before, after));
    }

    private async Task SetVisibleControlStatesAsync(
        string setKey,
        string section,
        IEnumerable<string> candidateKeys,
        IReadOnlySet<string> desiredKeys,
        CancellationToken ct)
    {
        foreach (var statKey in candidateKeys)
        {
            var key = ControlKey(setKey, section, statKey);
            if (!_controlCalibrations.TryGetValue(key, out var calibration)) continue;
            var label = StatLabel(statKey);
            await MovePointerAwayAsync(ct);
            var selected = calibration.IsSelected(CaptureSelectionScore(label));
            var desired = desiredKeys.Contains(statKey);
            if (selected == desired) continue;
            await ClickTextAsync(label, ct);
            await MovePointerAwayAsync(ct);
            if (calibration.IsSelected(CaptureSelectionScore(label)) != desired)
            {
                throw new InvalidDataException(
                    $"Native plan control '{key}' did not reach its required state.");
            }
        }
    }

    private void VerifyVisibleControlStates(
        string setKey,
        string section,
        IEnumerable<string> candidateKeys,
        IReadOnlySet<string> desiredKeys)
    {
        foreach (var statKey in candidateKeys)
        {
            var key = ControlKey(setKey, section, statKey);
            if (!_controlCalibrations.TryGetValue(key, out var calibration)) continue;
            var selected = calibration.IsSelected(CaptureSelectionScore(StatLabel(statKey)));
            if (selected != desiredKeys.Contains(statKey))
            {
                throw new InvalidDataException(
                    $"Native artifact control '{key}' did not preserve its required state.");
            }
        }
    }

    private static double CaptureSelectionScore(string expected)
    {
        using var capture = CaptureToRectArea();
        var match = capture.FindMulti(RecognitionObject.Ocr(capture.ToRect()))
            .FirstOrDefault(region => ExactText(region.Text, expected))
            ?? throw new InvalidDataException($"Unable to find native plan control '{expected}'.");
        var paddingX = ArtifactUiCoordinateMapper.ScaleLogicalX(
            capture.SrcMat.Size(), 40);
        var paddingY = ArtifactUiCoordinateMapper.ScaleLogicalY(
            capture.SrcMat.Size(), 20);
        var left = Math.Max(0, match.X - paddingX);
        var top = Math.Max(0, match.Y - paddingY);
        var right = Math.Min(capture.Width, match.Right + paddingX);
        var bottom = Math.Min(capture.Height, match.Bottom + paddingY);
        using var control = capture.DeriveCrop(new Rect(left, top, right - left, bottom - top));
        using var hsv = new Mat();
        Cv2.CvtColor(control.SrcMat, hsv, ColorConversionCodes.BGR2HSV);
        using var warmHighlight = hsv.InRange(
            new Scalar(5, 20, 80),
            new Scalar(45, 255, 255));
        return Cv2.CountNonZero(warmHighlight) / (double)(warmHighlight.Rows * warmHighlight.Cols);
    }

    private static async Task MovePointerAwayAsync(CancellationToken ct)
    {
        using var capture = CaptureToRectArea();
        var away = ArtifactUiCoordinateMapper.ToCapturePoint(
            capture.SrcMat.Size(), 800, 30);
        capture.MoveTo(away.X, away.Y);
        await Delay(90, ct);
    }

    private static bool IsConfigurableMainSlot(string slotKey) =>
        slotKey is "sands" or "goblet" or "circlet";

    private sealed record ControlCalibration(double SelectedScore, double UnselectedScore)
    {
        internal bool IsSelected(double score) =>
            Math.Abs(score - SelectedScore) < Math.Abs(score - UnselectedScore);
    }

    private static bool ExactText(string actual, string expected) =>
        string.Equals(NormalizeText(actual), NormalizeText(expected), StringComparison.Ordinal);

    private static string NormalizeText(string value) => new(
        value.Normalize(NormalizationForm.FormKC)
            .Where(character => char.IsLetterOrDigit(character) || character is '%' or '+')
            .ToArray());

    private static string SlotLabel(string slotKey) => slotKey switch
    {
        "sands" => "时之沙", "goblet" => "空之杯", "circlet" => "理之冠",
        "flower" => "生之花", "plume" => "死之羽",
        _ => throw new InvalidDataException($"Unknown artifact slot '{slotKey}'.")
    };

    private static string StatLabel(string statKey) => statKey switch
    {
        "hp" => "生命值", "atk" => "攻击力", "def" => "防御力",
        "hp_" => "生命值%", "atk_" => "攻击力%", "def_" => "防御力%",
        "eleMas" => "元素精通", "enerRech_" => "元素充能效率",
        "critRate_" => "暴击率", "critDMG_" => "暴击伤害", "heal_" => "治疗加成",
        "anemo_dmg_" => "风元素伤害加成", "cryo_dmg_" => "冰元素伤害加成",
        "dendro_dmg_" => "草元素伤害加成", "electro_dmg_" => "雷元素伤害加成",
        "geo_dmg_" => "岩元素伤害加成", "hydro_dmg_" => "水元素伤害加成",
        "physical_dmg_" => "物理伤害加成", "pyro_dmg_" => "火元素伤害加成",
        _ => throw new InvalidDataException($"Unknown artifact stat '{statKey}'.")
    };
}
