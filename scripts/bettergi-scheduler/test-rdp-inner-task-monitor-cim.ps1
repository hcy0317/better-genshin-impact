param(
    [string]$MonitorPath = (Join-Path $PSScriptRoot 'Wait-BetterGIInnerScheduledTask.ps1'),
    [switch]$OnlyCancellationCase
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Only function definitions are loaded. All OS observation and release operations below are fake.
. $MonitorPath

function New-MonitorCase {
    param([string]$Name)
    [pscustomobject]@{
        Name = $Name
        Now = [datetime]'2026-11-01T03:00:00+08:00'
        Snapshot = 0
        CimCalls = 0
        FactoryAttempts = 0
        ReleaseAttempts = 0
        Removed = 0
        CurrentTask = $null
        References = [System.Collections.Generic.List[object]]::new()
        ReleaseOrder = [System.Collections.Generic.List[string]]::new()
        Logs = [System.Collections.Generic.List[string]]::new()
    }
}

function New-FakeReference {
    param([string]$Kind)
    $reference = [pscustomobject]@{ Kind=$Kind; Step=$script:monitorCase.Snapshot; Closed=$false }
    $script:monitorCase.References.Add($reference)
    return $reference
}

function Assert-LiveReference {
    param($Reference)
    if ($Reference.Closed -or $Reference.Step -ne $script:monitorCase.Snapshot -or
        -not $script:monitorCase.References.Contains($Reference)) {
        throw 'An unowned, stale or released observation reference was used.'
    }
}

function Get-FakeTaskValue {
    param($Reference, [string]$Property)
    Assert-LiveReference $Reference
    if (-not [object]::ReferenceEquals($Reference, $script:monitorCase.CurrentTask)) {
        throw 'State, date and result must come from the same registered task.'
    }
    $name = $script:monitorCase.Name
    if (($Property -eq 'State' -and $name -in @('StateCancellation','Deadline')) -or
        ($Property -eq 'LastRunTime' -and $name -eq 'DateCancellation') -or
        ($Property -eq 'LastTaskResult' -and ($name -eq 'InfoCancellation' -or
            ($name -eq 'TransientInfoCancellation' -and $Reference.Step -eq 1)))) {
        throw [System.OperationCanceledException]::new("Native $Property read canceled.")
    }
    switch ($Property) {
        'State' {
            if ($name -eq 'Queued') { return 2 }
            if ($name -eq 'OldRun') { return 3 }
            if ($name -eq 'UnknownState') { return 0 }
            if ($name -eq 'InvalidState') { return 99 }
            if ($name -eq 'DisabledComplete') { return 1 }
            if ($Reference.Step -eq 1) { return 4 }
            return 3
        }
        'LastRunTime' {
            if ($name -eq 'OldRun') { return [datetime]'2026-11-01T02:59:59+08:00' }
            return [datetime]'2026-11-01T03:00:00+08:00'
        }
        'LastTaskResult' {
            if ($name -eq 'DisabledComplete') { return 0 }
            if ($Reference.Step -eq 1) { return 267009 }
            if ($name -eq 'NonzeroResult') { return 1 }
            return 0
        }
        default { throw 'Unexpected task property.' }
    }
}

function New-Object {
    [CmdletBinding()]
    param([string]$ComObject)
    if ($ComObject -ne 'Schedule.Service') { throw 'Only the fake local scheduler factory is allowed.' }
    $script:monitorCase.FactoryAttempts++
    $script:monitorCase.Snapshot++
    $script:monitorCase.CurrentTask = $null
    if ($script:monitorCase.Name -eq 'CreationFailure') { throw 'Native creation failed.' }
    if ($script:monitorCase.Name -ne 'ReleaseFailure' -and
        @($script:monitorCase.References | Where-Object { -not $_.Closed }).Count -ne 0) {
        throw 'The previous snapshot leaked an owned reference.'
    }
    $service = New-FakeReference 'Service'
    $service | Add-Member -MemberType ScriptMethod -Name Connect -Value {
        Assert-LiveReference $this
        if ($script:monitorCase.Name -eq 'ConnectFailure') { throw 'Native connection failed.' }
    }
    $service | Add-Member -MemberType ScriptMethod -Name GetFolder -Value {
        param([string]$Path)
        Assert-LiveReference $this
        if ($Path -ne '\') { throw 'Unexpected task folder.' }
        if ($script:monitorCase.Name -eq 'FolderFailure') { throw 'Native folder read failed.' }
        $folder = New-FakeReference 'Folder'
        $folder | Add-Member -MemberType ScriptMethod -Name GetTask -Value {
            param([string]$TaskName)
            Assert-LiveReference $this
            if ($TaskName -ne 'Offline-Native-Monitor') { throw 'Unexpected exact task identity.' }
            if ($script:monitorCase.Name -eq 'TaskFailure') { throw 'Native task read failed.' }
            if ($null -ne $script:monitorCase.CurrentTask) { throw 'A snapshot acquired more than one task object.' }
            $task = New-FakeReference 'Task'
            $task | Add-Member -MemberType ScriptProperty -Name State -Value { Get-FakeTaskValue $this 'State' }
            $task | Add-Member -MemberType ScriptProperty -Name LastRunTime -Value { Get-FakeTaskValue $this 'LastRunTime' }
            $task | Add-Member -MemberType ScriptProperty -Name LastTaskResult -Value { Get-FakeTaskValue $this 'LastTaskResult' }
            $script:monitorCase.CurrentTask = $task
            return $task
        }
        return $folder
    }
    return $service
}

function Remove-BetterGIInnerTaskComReference {
    [CmdletBinding()]
    param($Reference)
    Assert-LiveReference $Reference
    $script:monitorCase.ReleaseAttempts++
    $script:monitorCase.ReleaseOrder.Add(('{0}:{1}' -f $Reference.Step,$Reference.Kind))
    if ($script:monitorCase.Name -eq 'ReleaseFailure' -and $Reference.Kind -eq 'Task') {
        throw 'Native task release failed.'
    }
    $Reference.Closed = $true
    $script:monitorCase.Removed++
}

# S7 showed that even explicit new CIM sessions can be canceled. Every CIM entry is poisoned.
function New-CimSession {
    [CmdletBinding()]
    param()
    $script:monitorCase.CimCalls++
    throw [System.OperationCanceledException]::new('CIM observation canceled (0x80041032).')
}
function Get-ScheduledTask {
    [CmdletBinding()]
    param([string]$TaskName, $CimSession)
    $script:monitorCase.CimCalls++
    throw [System.OperationCanceledException]::new('CIM observation canceled (0x80041032).')
}
function Get-ScheduledTaskInfo {
    [CmdletBinding()]
    param([string]$TaskName, $CimSession)
    $script:monitorCase.CimCalls++
    throw [System.OperationCanceledException]::new('CIM observation canceled (0x80041032).')
}
function Remove-CimSession { throw 'Unexpected CIM cleanup in the native observer.' }
function Get-CimInstance { throw 'Real CIM reads are forbidden.' }
function Invoke-CimMethod { throw 'Real CIM methods are forbidden.' }
function Start-ScheduledTask { throw 'Real task launch is forbidden.' }
function Stop-ScheduledTask { throw 'Real task stop is forbidden.' }
function Register-ScheduledTask { throw 'Real task registration is forbidden.' }
function Unregister-ScheduledTask { throw 'Real task deletion is forbidden.' }
function Start-Process { throw 'Real process launch is forbidden.' }
function Stop-Process { throw 'Real process stop is forbidden.' }
function Start-Sleep { throw 'Real waiting is forbidden.' }
function mstsc { throw 'RDP is forbidden.' }

$cases = @(
    @{ Name='CancelledCimChannel'; Error=$null; Factories=2; References=6; Released=6; Removed=6 },
    @{ Name='StateCancellation'; Error='*after 6 consecutive failures:*Native State read canceled*'; Factories=6; References=18; Released=18; Removed=18 },
    @{ Name='DateCancellation'; Error='*after 6 consecutive failures:*Native LastRunTime read canceled*'; Factories=6; References=18; Released=18; Removed=18 },
    @{ Name='InfoCancellation'; Error='*after 6 consecutive failures:*Native LastTaskResult read canceled*'; Factories=6; References=18; Released=18; Removed=18 },
    @{ Name='TransientInfoCancellation'; Error=$null; Factories=2; References=6; Released=6; Removed=6 },
    @{ Name='CreationFailure'; Error='*after 6 consecutive failures:*Native creation failed*'; Factories=6; References=0; Released=0; Removed=0 },
    @{ Name='ConnectFailure'; Error='*after 6 consecutive failures:*Native connection failed*'; Factories=6; References=6; Released=6; Removed=6 },
    @{ Name='FolderFailure'; Error='*after 6 consecutive failures:*Native folder read failed*'; Factories=6; References=6; Released=6; Removed=6 },
    @{ Name='TaskFailure'; Error='*after 6 consecutive failures:*Native task read failed*'; Factories=6; References=12; Released=12; Removed=12 },
    @{ Name='ReleaseFailure'; Error='*after 6 consecutive failures:*Native task release failed*'; Factories=6; References=18; Released=18; Removed=12 },
    @{ Name='NonzeroResult'; Error='*failed with LastTaskResult 1.'; Factories=2; References=6; Released=6; Removed=6 },
    @{ Name='Deadline'; Error='Timed out waiting*'; Factories=2; References=6; Released=6; Removed=6 },
    @{ Name='OldRun'; Error='*did not start within 120 seconds.'; Factories=12; References=36; Released=36; Removed=36 },
    @{ Name='Queued'; Error='*did not start within 120 seconds.'; Factories=12; References=36; Released=36; Removed=36 },
    @{ Name='UnknownState'; Error='*after 6 consecutive failures:*Unknown native scheduled task state*'; Factories=6; References=18; Released=18; Removed=18 },
    @{ Name='InvalidState'; Error='*after 6 consecutive failures:*Unknown native scheduled task state*'; Factories=6; References=18; Released=18; Removed=18 },
    @{ Name='DisabledComplete'; Error=$null; Factories=1; References=3; Released=3; Removed=3 },
    @{ Name='InjectedProvider'; Error=$null; Factories=0; References=0; Released=0; Removed=0 }
)
if ($OnlyCancellationCase) { $cases = @($cases | Where-Object Name -eq 'CancelledCimChannel') }
$results = [System.Collections.Generic.List[object]]::new()
foreach ($case in $cases) {
    $script:monitorCase = New-MonitorCase $case.Name
    $deadline = $script:monitorCase.Now.AddMinutes(3)
    if ($case.Name -eq 'Deadline') { $deadline = $script:monitorCase.Now.AddSeconds(20) }
    $arguments = @{
        TaskName = 'Offline-Native-Monitor'
        PreviousLastRunTime = [datetime]'2026-11-01T02:59:59+08:00'
        Deadline = $deadline
        NowProvider = { $script:monitorCase.Now }
        SleepAction = { param([int]$Seconds) $script:monitorCase.Now = $script:monitorCase.Now.AddSeconds($Seconds) }
        LogAction = { param([string]$Message) $script:monitorCase.Logs.Add($Message) }
    }
    if ($case.Name -eq 'InjectedProvider') {
        $arguments.TaskSnapshotProvider = {
            param([string]$TaskName)
            if ($TaskName -ne 'Offline-Native-Monitor') { throw 'Unexpected injected identity.' }
            $script:monitorCase.Snapshot++
            [pscustomobject]@{
                State = $(if ($script:monitorCase.Snapshot -eq 1) { 'Running' } else { 'Ready' })
                LastRunTime = [datetime]'2026-11-01T03:00:00+08:00'
                LastTaskResult = $(if ($script:monitorCase.Snapshot -eq 1) { 267009 } else { 0 })
            }
        }
    }
    $failure = $null
    try { Wait-BetterGIInnerScheduledTask @arguments }
    catch { $failure = $_.Exception.Message }
    $completed = @($script:monitorCase.Logs | Where-Object { $_ -like '*completed successfully*' }).Count
    $errors = [System.Collections.Generic.List[string]]::new()
    if ($null -eq $case.Error) {
        if ($null -ne $failure -or $completed -ne 1) { $errors.Add('A fresh successful run must complete exactly once.') }
    }
    elseif ($completed -ne 0 -or $null -eq $failure -or $failure -notlike $case.Error) {
        $errors.Add('The original failure boundary must reject completion.')
    }
    if ($script:monitorCase.CimCalls -ne 0) { $errors.Add('The canceled CIM observation path was still used.') }
    if ($script:monitorCase.FactoryAttempts -ne $case.Factories -or
        $script:monitorCase.References.Count -ne $case.References -or
        $script:monitorCase.ReleaseAttempts -ne $case.Released -or
        $script:monitorCase.Removed -ne $case.Removed) {
        $errors.Add('Owned reference cleanup or the original failure/launch budget changed.')
    }
    $unreleased = @($script:monitorCase.References | Where-Object { -not $_.Closed })
    if ($case.Name -ne 'ReleaseFailure' -and $unreleased.Count -ne 0) { $errors.Add('A snapshot reference leaked.') }
    if ($case.Name -eq 'ReleaseFailure' -and @($unreleased | Where-Object Kind -ne 'Task').Count -ne 0) {
        $errors.Add('One failed release prevented other owned references from being released.')
    }
    if ($case.Name -eq 'TransientInfoCancellation' -and
        @($script:monitorCase.Logs | Where-Object { $_ -like '*query failed (1/6)*' }).Count -ne 1) {
        $errors.Add('A transient read failure must be logged before later completion.')
    }
    $results.Add([pscustomobject]@{
        Case=$case.Name; Passed=($errors.Count -eq 0); Completed=$completed; Error=$failure
        CimCalls=$script:monitorCase.CimCalls; FactoryAttempts=$script:monitorCase.FactoryAttempts
        References=$script:monitorCase.References.Count; ReleaseAttempts=$script:monitorCase.ReleaseAttempts
        Removed=$script:monitorCase.Removed; FailedAssertions=@($errors)
    })
}
$failed = @($results | Where-Object { -not $_.Passed }).Count
[pscustomobject]@{ Total=$results.Count; Failed=$failed; Results=@($results) } | ConvertTo-Json -Depth 7 -Compress
if ($failed -ne 0) { exit 1 }
