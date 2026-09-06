using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using BetterGenshinImpact.View.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BetterGenshinImpact.ViewModel.Pages.View;

public partial class AutoFightViewModel
{
    [ObservableProperty] private string _skillCharacterKeys = "";
    [ObservableProperty] private string _skillCatalogStatus = "尚未读取。内置离线库可直接使用；同步仅影响下一场战斗。";
    [ObservableProperty] private string _skillCatalogDetails = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCombatSkillsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReadCombatSkillSourcesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCombatSkillRulesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCombatSkillProfileCommand))]
    private bool _skillCatalogBusy;

    private bool CanUseSkillCatalog() => !SkillCatalogBusy;

    private string[]? SelectedSkillCharacters()
    {
        var keys = SkillCharacterKeys.Split([',', '，', ';', '；', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return keys.Length == 0 ? null : keys;
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanUseSkillCatalog))]
    private async Task SyncCombatSkills(CancellationToken ct)
    {
        var keys = SelectedSkillCharacters();
        var progress = new Progress<SkillSyncProgress>(item => SkillCatalogStatus = $"同步 {item.Completed}/{item.Total}：{item.CharacterKey}");
        await RunSkillCatalogOperation(async () => await Task.Run(async () =>
        {
            var catalog = CombatSkillCatalog.Default;
            await catalog.SyncAsync(keys, progress, ct);
            return catalog.ReadStatus();
        }, ct));
    }

    [RelayCommand(CanExecute = nameof(CanUseSkillCatalog))]
    private async Task ReadCombatSkillSources()
    {
        var keys = SelectedSkillCharacters();
        await RunSkillCatalogOperation(async () =>
        {
            var result = await Task.Run(() =>
            {
                var catalog = CombatSkillCatalog.Default;
                var snapshot = catalog.Store.ReadSnapshot();
                var facts = snapshot.Skills.Values.Where(skill => keys == null || keys.Contains(skill.CharacterKey, StringComparer.Ordinal));
                var details = string.Join("\n\n", facts.Select(skill =>
                    $"{skill.Character} / {skill.Slot} / {skill.Name}\n{skill.SourceUrl}\n事实 {skill.Revision}；机制 {skill.MechanicsVersion ?? "无"} ({skill.MechanicsStatus})\n" +
                    string.Join("；", skill.Metrics.Select(metric => $"{metric.Key}=[{string.Join(",", metric.Value.Values)}] {metric.Value.Unit} ({metric.Value.SourceKind}{(metric.Value.OverrideReason == null ? "" : ":" + metric.Value.OverrideReason)})"))));
                return (Status: catalog.ReadStatus(), Details: details.Length == 0 ? "没有匹配的角色英文键。" : details);
            });
            SkillCatalogDetails = result.Details;
            return result.Status;
        });
    }

    [RelayCommand(CanExecute = nameof(CanUseSkillCatalog))]
    private Task ImportCombatSkillRules() => ImportSkillCatalogFile(profile: false);

    [RelayCommand(CanExecute = nameof(CanUseSkillCatalog))]
    private Task ImportCombatSkillProfile() => ImportSkillCatalogFile(profile: true);

    private Task ImportSkillCatalogFile(bool profile)
    {
        var dialog = new OpenFileDialog { Filter = "JSON 数据 (*.json)|*.json", CheckFileExists = true,
            Title = profile ? "导入个人角色覆盖（不会改写公共来源）" : "导入经核验的机制规则包" };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        var path = dialog.FileName;
        return RunSkillCatalogOperation(() => Task.Run(() =>
        {
            var json = CombatSkillCatalog.ReadLocalJsonFile(path);
            return profile ? CombatSkillCatalog.Default.ImportProfile(json) : CombatSkillCatalog.Default.ImportRules(json);
        }));
    }

    private async Task RunSkillCatalogOperation(Func<Task<SkillCatalogStatus>> operation)
    {
        SkillCatalogBusy = true;
        try
        {
            var status = await operation();
            SkillCatalogStatus = $"{status.CharacterCount} 个角色 / {status.SkillCount} 个技能；机制 {status.RuleVersion ?? "未导入"}，需复核 {status.NeedsReviewCount}；" +
                $"数值覆盖 {status.MetricOverrideCount}，角色覆盖 {status.CharacterProfileCount}。\n" +
                $"最近成功同步：{status.LastSuccessfulSyncAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "无"}；最近结果：{status.LastSyncStatus ?? "无"}\n{status.DatabasePath}";
        }
        catch (OperationCanceledException) { SkillCatalogStatus = "同步已取消；上一有效数据库保留，正在战斗的快照不变。"; }
        catch (Exception exception)
        {
            SkillCatalogStatus = "技能数据操作失败：" + exception.Message;
            ThemedMessageBox.Error(SkillCatalogStatus);
        }
        finally { SkillCatalogBusy = false; }
    }
}
