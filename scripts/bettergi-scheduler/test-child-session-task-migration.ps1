param()

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'Set-BetterGI-ChildSessionScheduledTasks.ps1'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testRoot = Join-Path $tempRoot ('bettergi-task-migration-' + [guid]::NewGuid().ToString('N'))
$names = @('BetterGI-每日自动化总控', 'BetterGI-每周一4点10-自动化总控', 'BetterGI-下午16点10-角色养成一条龙')

# Every scheduler API is replaced; no real task can be modified or launched.
function Reset-TestState {
    $script:testState = @{tasks=@{}; originals=@{}; sets=0; restores=0; failAt=0; corruptAt=0; rollbackFail=$null}
    foreach ($name in $names) {
        $enabled = if ($name -eq $names[2]) { 'false' } else { 'true' }
        $xml = '<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><RegistrationInfo><Date>2026-10-04</Date></RegistrationInfo><Triggers><CalendarTrigger><StartBoundary>2026-10-04T04:10:00</StartBoundary></CalendarTrigger></Triggers><Principals><Principal><UserId>hcy</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><Enabled>{0}</Enabled></Settings><Actions Context="Author"><Exec><Command>pwsh.exe</Command><Arguments>-File "Start-BetterGI-RdpScheduledJob.ps1"</Arguments></Exec></Actions></Task>' -f $enabled
        $script:testState.tasks[$name] = @{xml=$xml;state='Ready'}
        $script:testState.originals[$name] = $xml
    }
    $script:testState.tasks[$names[2]].state = 'Disabled'
}
function Get-ScheduledTask {
    [CmdletBinding()]param($TaskName,$TaskPath)
    $task = $script:testState.tasks[$TaskName]
    $xml = [xml]$task.xml
    [pscustomobject]@{TaskName=$TaskName;State=$task.state;Actions=@([pscustomobject]@{Arguments=$xml.Task.Actions.Exec.Arguments})}
}
function Export-ScheduledTask {
    [CmdletBinding()]param($TaskName,$TaskPath)
    $script:testState.tasks[$TaskName].xml
}
function New-ScheduledTaskAction {
    param($Execute,$Argument,$WorkingDirectory)
    [pscustomobject]@{Execute=$Execute;Arguments=$Argument;WorkingDirectory=$WorkingDirectory}
}
function Set-ScheduledTask {
    [CmdletBinding()]param($TaskName,$TaskPath,$Action)
    $script:testState.sets++
    $xml = [xml]$script:testState.tasks[$TaskName].xml
    $xml.Task.Actions.Exec.Command = $Action.Execute
    $xml.Task.Actions.Exec.Arguments = $Action.Arguments
    $xml.Task.RegistrationInfo.Date = '2026-10-05'
    if ($script:testState.sets -eq $script:testState.corruptAt) { $xml.Task.Settings.Enabled = 'true' }
    $script:testState.tasks[$TaskName].xml = $xml.OuterXml
    if ($script:testState.sets -eq $script:testState.failAt) { throw 'Simulated failure after mutation' }
}
function Register-ScheduledTask {
    [CmdletBinding()]param($TaskName,$TaskPath,$Xml,[switch]$Force)
    $script:testState.restores++
    if ($TaskName -eq $script:testState.rollbackFail) { throw 'Simulated rollback failure' }
    $script:testState.tasks[$TaskName].xml = $Xml
}
function Assert([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Restored {
    foreach ($name in $names) {
        Assert ($script:testState.tasks[$name].xml -eq $script:testState.originals[$name]) "Task was not restored: $name"
    }
}
function Expect-Failure([string]$Pattern) {
    $failed = $false
    try { . $source -BetterGIRoot $testRoot -Apply | Out-Null }
    catch { $failed = $true; Assert ($_.Exception.Message -like $Pattern) "Unexpected error: $($_.Exception.Message)" }
    Assert $failed 'Expected migration failure'
}
if (-not ([IO.Path]::GetFullPath($testRoot)).StartsWith($tempRoot+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root' }
try {
    $scheduler = Join-Path $testRoot 'scripts\bettergi-scheduler'
    New-Item -ItemType Directory -Path $scheduler -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $scheduler 'Start-BetterGI-OneDragon.ps1') | Out-Null

    Reset-TestState
    $preview = . $source -BetterGIRoot $testRoot | ConvertFrom-Json
    Assert ($preview.Count -eq 3 -and $script:testState.sets -eq 0) 'Preview wrote tasks'
    Assert-Restored

    $result = . $source -BetterGIRoot $testRoot -Apply | ConvertFrom-Json
    Assert ($result.status -eq 'migrated' -and $script:testState.sets -eq 3) 'Migration did not update all three actions'
    foreach ($name in $names) {
        $before = [xml]$script:testState.originals[$name]
        $after = [xml]$script:testState.tasks[$name].xml
        foreach ($element in @('Triggers','Principals','Settings')) {
            Assert ($before.Task.$element.OuterXml -eq $after.Task.$element.OuterXml) "Policy changed: $name / $element"
        }
        Assert ($after.Task.Actions.Exec.Arguments.Contains('-ChildSessionAutomation -AutomationTimeoutSeconds 25200')) 'Missing native arguments or seven-hour budget'
    }
    Assert ($script:testState.tasks[$names[2]].state -eq 'Disabled') 'Retired task enabled'
    Assert (@(Get-ChildItem -LiteralPath $result.backup -Filter '*.xml').Count -eq 3) 'Missing XML backups'

    # Separate backup timestamp slots without sleeps.
    function Get-Date { param($Format) if ($Format) { [guid]::NewGuid().ToString('N') } else { [datetime]::Now } }
    Reset-TestState
    $script:testState.failAt = 2
    Expect-Failure '*Simulated failure after mutation*'
    Assert ($script:testState.restores -eq 2) 'Partially changed task was omitted from rollback'
    Assert-Restored

    Reset-TestState
    $script:testState.corruptAt = 3
    Expect-Failure '*changed unexpectedly*'
    Assert-Restored

    Reset-TestState
    $script:testState.tasks[$names[1]].state = 'Running'
    Expect-Failure '*Scheduled job is running*'
    Assert ($script:testState.sets -eq 0) 'Running task was modified'

    Reset-TestState
    $script:testState.failAt = 2
    $script:testState.rollbackFail = $names[0]
    Expect-Failure '*Rollback failures*Backup:*'
    Assert ($script:testState.restores -eq 2) 'Rollback stopped before restoring remaining tasks'
    Assert ($script:testState.tasks[$names[1]].xml -eq $script:testState.originals[$names[1]]) 'Second rollback was skipped'
    '6 checks passed: preview, action-only migration, partial-write rollback, policy rollback, running-task refusal, independent rollback attempts'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith($tempRoot+'\', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'bettergi-task-migration-*') { throw 'Unsafe cleanup' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
