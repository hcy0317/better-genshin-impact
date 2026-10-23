[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BetterGIRoot,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$BetterGIRoot = (Resolve-Path -LiteralPath $BetterGIRoot).Path
$scheduler = Join-Path $BetterGIRoot 'scripts\bettergi-scheduler'
$launcher = Join-Path $scheduler 'Start-BetterGI-OneDragon.ps1'
$shell = (Get-Command pwsh.exe -ErrorAction Stop).Source
$definitions = @(
    @{name='BetterGI-每日自动化总控';config='每日自动化总控'},
    @{name='BetterGI-每周一4点10-自动化总控';config='每周一4点10-自动化总控'},
    @{name='BetterGI-下午16点10-角色养成一条龙';config='下午16点10-角色养成一条龙'}
)
$plans = @(foreach ($definition in $definitions) {
    $task = Get-ScheduledTask -TaskName $definition.name -TaskPath '\' -ErrorAction Stop
    if ($task.State -eq 'Running') { throw "Scheduled job is running; wait for completion: $($task.TaskName)" }
    if ($task.Actions.Count -ne 1 -or $task.Actions[0].Arguments -notmatch 'Start-BetterGI-(RdpScheduledJob|OneDragon)\.ps1') {
        throw "Unexpected task action; preserve it for review: $($task.TaskName)"
    }
    $arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -ConfigName "{1}" -ChildSessionAutomation -AutomationTimeoutSeconds 25200' -f $launcher,$definition.config
    [pscustomobject]@{taskName=$task.TaskName;configName=$definition.config;execute=$shell;arguments=$arguments;workingDirectory=$scheduler;beforeXml=(Export-ScheduledTask -TaskName $task.TaskName -TaskPath '\')}
})
if (-not $Apply) {
    $plans | Select-Object taskName,configName,execute,arguments,workingDirectory | ConvertTo-Json -Depth 4
    return
}
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { throw 'Native automation launcher is not deployed.' }
$backup = Join-Path $BetterGIRoot ('.codex-backups\child-session-scheduler-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $backup) { throw 'Backup already exists.' }
New-Item -ItemType Directory -Path $backup -Force | Out-Null
foreach ($plan in $plans) {
    $plan.beforeXml | Set-Content -LiteralPath (Join-Path $backup ($plan.taskName+'.xml')) -Encoding utf8
}
function DefinitionWithoutActions([string]$Xml) {
    $document = [xml]$Xml
    # Registration timestamps may change when Windows updates an action.
    # Preserve the actual scheduling and execution policy, rather than timestamp formatting.
    return (@('Triggers','Principals','Settings') | ForEach-Object {
        $document.SelectSingleNode(('/*[local-name()="Task"]/*[local-name()="{0}"]' -f $_)).OuterXml
    }) -join "`n"
}
$changed = @()
try {
    foreach ($plan in $plans) {
        $current = Get-ScheduledTask -TaskName $plan.taskName -TaskPath '\' -ErrorAction Stop
        if ($current.State -eq 'Running') { throw 'Scheduled job started during migration; no active job will be stopped.' }
        $currentXml = Export-ScheduledTask -TaskName $plan.taskName -TaskPath '\'
        if ([string]$currentXml -ne [string]$plan.beforeXml) { throw 'Scheduled task changed since migration preview.' }
        $action = New-ScheduledTaskAction -Execute $plan.execute -Argument $plan.arguments -WorkingDirectory $plan.workingDirectory
        # Include this task in rollback even if the API throws after changing its definition.
        $changed += $plan
        Set-ScheduledTask -TaskName $plan.taskName -TaskPath '\' -Action $action -ErrorAction Stop | Out-Null
        $afterXml = Export-ScheduledTask -TaskName $plan.taskName -TaskPath '\'
        if ((DefinitionWithoutActions $afterXml) -ne (DefinitionWithoutActions $plan.beforeXml)) {
            throw 'Triggers, enabled state, principal, or settings changed unexpectedly.'
        }
    }
} catch {
    $migrationError = $_
    $rollbackErrors = @()
    foreach ($plan in $changed) {
        try {
            Register-ScheduledTask -TaskName $plan.taskName -TaskPath '\' -Xml $plan.beforeXml -Force -ErrorAction Stop | Out-Null
        } catch {
            $rollbackErrors += "$($plan.taskName): $($_.Exception.Message)"
        }
    }
    if ($rollbackErrors.Count -gt 0) {
        throw "Migration failed: $($migrationError.Exception.Message). Rollback failures: $($rollbackErrors -join '; '). Backup: $backup"
    }
    throw $migrationError
}
[pscustomobject]@{status='migrated';backup=$backup;tasks=@($plans.taskName);started=$false;triggersPreserved=$true;enabledStatesPreserved=$true} | ConvertTo-Json -Depth 4
