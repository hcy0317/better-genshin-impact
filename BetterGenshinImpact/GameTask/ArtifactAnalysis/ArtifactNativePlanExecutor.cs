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
        string expectedUid,
        CancellationToken cancellationToken)
    {
        var task = new ArtifactNativePlanTask(
            plan, expectedUid, cancellationToken);
        try
        {
            await new TaskRunner().RunSoloTaskAsync(task, propagateExceptions: true);
        }
        catch (OperationCanceledException) when (task.Completed)
        {
            // Cancellation that arrived after the destructive commit point is
            // deliberately ignored because the reviewed target is verified.
        }
        if (!task.Completed)
        {
            throw new InvalidOperationException("Native artifact plan replacement did not complete.");
        }
    }
}

internal sealed class ArtifactNativePlanTask(
    ArtifactNativeSyncPlanDto plan,
    string expectedUid,
    CancellationToken externalCancellationToken) : ISoloTask
{
    private readonly ILogger _logger = App.GetLogger<ArtifactNativePlanTask>();
    private readonly ArtifactSetCatalog _catalog = new(Path.Combine(
        AppContext.BaseDirectory, "GameTask", "ArtifactAnalysis", "Assets", "artifact-sets.zh.json"));
    private readonly Dictionary<string, ControlCalibration> _controlCalibrations = new(StringComparer.Ordinal);
    private IOcrService? _ocrService;
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
    private IOcrService OcrService => _ocrService
        ?? throw new InvalidOperationException("Artifact OCR session is unavailable.");

    public async Task Start(CancellationToken taskCancellationToken)
    {
        using var ocrSession = new ArtifactPaddleOcrSession();
        _ocrService = ocrSession.Service;
        try
        {
            await StartCoreAsync(taskCancellationToken);
        }
        finally
        {
            _ocrService = null;
        }
    }

    private async Task StartCoreAsync(CancellationToken taskCancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            taskCancellationToken,
            externalCancellationToken);
        var ct = linked.Token;
        await new ReturnMainUiTask().Start(ct);
        await ArtifactGameIdentityVerifier.EnsureExpectedUidAsync(
            expectedUid, OcrService, ct);
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
        var recoveryRoot = WritePreMutationEvidence(plan);
        WriteRecoveryState(recoveryRoot, "PREPARED", []);

        var destructiveCommitStarted = false;
        try
        {
            await ClickTextAsync("快速删除方案", ct);
            await Delay(250, ct);
            using (var confirm = CaptureToRectArea())
            {
                if (!Bv.ClickBlackConfirmButton(confirm) && !TryClickText("确认"))
                {
                    throw new InvalidDataException("Unable to confirm native plan deletion.");
                }
            }
            destructiveCommitStarted = true;
            // Confirmation is the destructive commit point. From here the reviewed
            // target plan, not a caller cancellation, owns forward recovery.
            var commitToken = CancellationToken.None;
            WriteRecoveryState(recoveryRoot, "OLD_PLANS_DELETED", []);
            await Delay(450, commitToken);

            var configuredSets = new List<string>();
            foreach (var group in grouped)
            {
                await OpenLockAssistanceAsync(commitToken);
                await SelectSetAsync(_catalog.LocalizedName(group.Key), commitToken);
                await ConfigureSetAsync(group.ToArray(), commitToken);
                configuredSets.Add(group.Key);
                WriteRecoveryState(
                    recoveryRoot, "CONFIGURING_TARGET", configuredSets);
            }
            foreach (var group in grouped)
            {
                await OpenLockAssistanceAsync(commitToken);
                await SelectSetAsync(_catalog.LocalizedName(group.Key), commitToken);
                await VerifySetAsync(group.ToArray(), commitToken);
            }
            WriteRecoveryState(recoveryRoot, "VERIFIED", configuredSets);
            Completed = true;
            await new ReturnMainUiTask().Start(commitToken);
        }
        catch (Exception exception) when (destructiveCommitStarted)
        {
            WriteRecoveryState(recoveryRoot, "FORWARD_RECOVERY_REQUIRED", []);
            throw new ArtifactForwardRecoveryRequiredException(
                recoveryRoot,
                exception);
        }
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
            var text = OcrService.OcrResult(row.SrcMat).Text;
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

    private string WritePreMutationEvidence(ArtifactNativeSyncPlanDto targetPlan)
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
            "target-plan.json is the machine-readable forward-recovery source. "
            + "If execution is interrupted after deletion, rerun the same reviewed plan; "
            + "recovery-state.json records the last completed phase and set keys. "
            + "Genshin does not expose an export capable of restoring the prior custom plans.");
        return root;
    }

    private static void WriteRecoveryState(
        string root,
        string phase,
        IReadOnlyCollection<string> configuredSets)
    {
        var temporary = Path.Combine(root, "recovery-state.json.tmp");
        var target = Path.Combine(root, "recovery-state.json");
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(
                new
                {
                    phase,
                    configuredSets = configuredSets
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray(),
                    updatedAtUtc = DateTimeOffset.UtcNow
                },
                new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, target, overwrite: true);
    }

    private bool HasText(string expected)
    {
        using var capture = CaptureToRectArea();
        return capture.FindMulti(
                RecognitionObject.Ocr(capture.ToRect()),
                ocrService: OcrService)
            .Any(region => ExactText(region.Text, expected));
    }

    private void RequireText(string expected)
    {
        if (!HasText(expected))
        {
            throw new InvalidDataException(
                $"Unable to find native plan control '{expected}' during preflight.");
        }
    }

    private bool TryClickText(string expected)
    {
        using var capture = CaptureToRectArea();
        var match = capture.FindMulti(
                RecognitionObject.Ocr(capture.ToRect()),
                ocrService: OcrService)
            .FirstOrDefault(region => ExactText(region.Text, expected));
        if (match is null) return false;
        match.Click();
        return true;
    }

    private async Task ClickTextAsync(string expected, CancellationToken ct)
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

    private async Task<ControlCalibration> CalibrateControlAsync(
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

    private double CaptureSelectionScore(string expected)
    {
        using var capture = CaptureToRectArea();
        var match = capture.FindMulti(
                RecognitionObject.Ocr(capture.ToRect()),
                ocrService: OcrService)
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

internal sealed class ArtifactForwardRecoveryRequiredException(
    string recoveryRoot,
    Exception innerException)
    : InvalidOperationException(
        $"原神方案已进入破坏性提交阶段，需要按目标方案继续前向恢复：{recoveryRoot}",
        innerException)
{
    internal string RecoveryRoot { get; } = recoveryRoot;
}
