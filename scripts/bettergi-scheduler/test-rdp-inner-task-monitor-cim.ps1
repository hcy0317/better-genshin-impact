param(
    [string]$MonitorPath = (Join-Path $PSScriptRoot 'Wait-BetterGIInnerScheduledTask.ps1')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Load only the pure monitor. No scheduler entrypoint or application is loaded.
. $MonitorPath

function New-MonitorCase {
    param([string]$Name)
    [pscustomobject]@{
        Name = $Name
        Now = [datetime]'2026-10-31T20:00:00+08:00'
        Snapshot = 0
        DefaultQueries = 0
        Created = 0
        ReleaseAttempts = 0
        Removed = 0
        SnapshotSession = $null
        Sessions = [System.Collections.Generic.List[object]]::new()
        Logs = [System.Collections.Generic.List[string]]::new()
    }
}

function New-CimSession {
    [CmdletBinding()]
    param()
    $script:monitorCase.Created++
    if ($script:monitorCase.Name -eq 'CreationFailure') { throw 'Session creation failed.' }
    $session = [pscustomobject]@{ Id = $script:monitorCase.Created; Closed = $false }
    $script:monitorCase.Sessions.Add($session)
    return $session
}

function Remove-CimSession {
    [CmdletBinding()]
    param($CimSession)
    if ($null -eq $CimSession -or $CimSession.Closed -or
        -not $script:monitorCase.Sessions.Contains($CimSession)) {
        throw 'Only a live session owned by this snapshot may be released.'
    }
    $script:monitorCase.ReleaseAttempts++
    if ($script:monitorCase.Name -eq 'ReleaseFailure') { throw 'Session release failed.' }
    $CimSession.Closed = $true
    $script:monitorCase.Removed++
}

function Get-ScheduledTask {
    [CmdletBinding()]
    param([string]$TaskName, $CimSession)
    if ($TaskName -ne 'Offline-Cim-Monitor') { throw 'Unexpected task identity.' }
    if ($null -eq $CimSession) {
        $script:monitorCase.DefaultQueries++
        throw [System.OperationCanceledException]::new('调用已取消')
    }
    if ($CimSession.Closed) { throw 'A released observation session was reused.' }
    $script:monitorCase.Snapshot++
    $script:monitorCase.SnapshotSession = $CimSession
    if ($script:monitorCase.Name -in @('StateCancellation', 'Deadline')) {
        throw [System.OperationCanceledException]::new('调用已取消')
    }
    return [pscustomobject]@{
        State = $(if ($script:monitorCase.Name -eq 'Queued') { 'Queued' }
            elseif ($script:monitorCase.Name -eq 'OldRun') { 'Ready' }
            elseif ($script:monitorCase.Snapshot -eq 1) { 'Running' }
            else { 'Ready' })
    }
}

function Get-ScheduledTaskInfo {
    [CmdletBinding()]
    param([string]$TaskName, $CimSession)
    if ($TaskName -ne 'Offline-Cim-Monitor') { throw 'Unexpected task info identity.' }
    if ($null -eq $CimSession -or
        -not [object]::ReferenceEquals($CimSession, $script:monitorCase.SnapshotSession)) {
        throw 'Task state and result must use the same owned observation session.'
    }
    if ($script:monitorCase.Name -eq 'InfoCancellation' -or
        ($script:monitorCase.Name -eq 'TransientInfoCancellation' -and $script:monitorCase.Snapshot -eq 1)) {
        throw [System.OperationCanceledException]::new('调用已取消')
    }
    return [pscustomobject]@{
        LastRunTime = $(if ($script:monitorCase.Name -eq 'OldRun') {
            [datetime]'2026-10-31T19:59:59+08:00'
        } else { [datetime]'2026-10-31T20:00:00+08:00' })
        LastTaskResult = $(if ($script:monitorCase.Snapshot -eq 1) { 267009 }
            elseif ($script:monitorCase.Name -eq 'NonzeroResult') { 1 }
            else { 0 })
    }
}

function Start-ScheduledTask { throw 'Real task launch is forbidden in this offline test.' }
function Stop-ScheduledTask { throw 'Real task stop is forbidden in this offline test.' }
function Start-Process { throw 'Real process launch is forbidden in this offline test.' }
function Stop-Process { throw 'Real process stop is forbidden in this offline test.' }
function Start-Sleep { throw 'Real waiting is forbidden in this offline test.' }
function mstsc { throw 'RDP is forbidden in this offline test.' }

# Expectations describe observable completion/failure and ownership at the CIM boundary.
$cases = @(
    @{ Name='CancelledImplicitChannel'; Error=$null; Created=2; Released=2; Removed=2 },
    @{ Name='StateCancellation'; Error='*after 6 consecutive failures: 调用已取消'; Created=6; Released=6; Removed=6 },
    @{ Name='InfoCancellation'; Error='*after 6 consecutive failures: 调用已取消'; Created=6; Released=6; Removed=6 },
    @{ Name='TransientInfoCancellation'; Error=$null; Created=2; Released=2; Removed=2 },
    @{ Name='CreationFailure'; Error='*after 6 consecutive failures: Session creation failed.'; Created=6; Released=0; Removed=0 },
    @{ Name='ReleaseFailure'; Error='*after 6 consecutive failures: Session release failed.'; Created=6; Released=6; Removed=0 },
    @{ Name='NonzeroResult'; Error='*failed with LastTaskResult 1.'; Created=2; Released=2; Removed=2 },
    @{ Name='Deadline'; Error='Timed out waiting*'; Created=2; Released=2; Removed=2 },
    @{ Name='OldRun'; Error='*did not start within 120 seconds.'; Created=12; Released=12; Removed=12 },
    @{ Name='Queued'; Error='*did not start within 120 seconds.'; Created=12; Released=12; Removed=12 },
    @{ Name='InjectedProvider'; Error=$null; Created=0; Released=0; Removed=0 }
)
$results = [System.Collections.Generic.List[object]]::new()
foreach ($case in $cases) {
    $script:monitorCase = New-MonitorCase -Name $case.Name
    $deadline = $script:monitorCase.Now.AddMinutes(3)
    if ($case.Name -eq 'Deadline') { $deadline = $script:monitorCase.Now.AddSeconds(20) }
    $monitorArguments = @{
        TaskName = 'Offline-Cim-Monitor'
        PreviousLastRunTime = [datetime]'2026-10-31T19:59:59+08:00'
        Deadline = $deadline
        NowProvider = { $script:monitorCase.Now }
        SleepAction = {
            param([int]$Seconds)
            $script:monitorCase.Now = $script:monitorCase.Now.AddSeconds($Seconds)
        }
        LogAction = {
            param([string]$Message)
            $script:monitorCase.Logs.Add($Message)
        }
    }
    if ($case.Name -eq 'InjectedProvider') {
        $monitorArguments.TaskSnapshotProvider = {
            param([string]$TaskName)
            if ($TaskName -ne 'Offline-Cim-Monitor') { throw 'Unexpected injected task identity.' }
            $script:monitorCase.Snapshot++
            [pscustomobject]@{
                State = $(if ($script:monitorCase.Snapshot -eq 1) { 'Running' } else { 'Ready' })
                LastRunTime = [datetime]'2026-10-31T20:00:00+08:00'
                LastTaskResult = $(if ($script:monitorCase.Snapshot -eq 1) { 267009 } else { 0 })
            }
        }
    }
    $failure = $null
    try { Wait-BetterGIInnerScheduledTask @monitorArguments }
    catch { $failure = $_.Exception.Message }

    $completed = @($script:monitorCase.Logs | Where-Object { $_ -like '*completed successfully*' }).Count
    $expectedSuccess = $null -eq $case.Error
    $errors = [System.Collections.Generic.List[string]]::new()
    if ($expectedSuccess) {
        if ($null -ne $failure -or $completed -ne 1) { $errors.Add('The same launched task must complete exactly once without a query failure escaping.') }
    }
    elseif ($completed -ne 0 -or $null -eq $failure -or $failure -notlike $case.Error) {
        $errors.Add('Failure must retain its original boundary and must never report completion.')
    }
    if ($script:monitorCase.DefaultQueries -ne 0) { $errors.Add('A poisoned implicit CIM session was reused.') }
    if ($script:monitorCase.Created -ne $case.Created -or
        $script:monitorCase.ReleaseAttempts -ne $case.Released -or
        $script:monitorCase.Removed -ne $case.Removed) {
        $errors.Add('Snapshot session ownership or the existing failure/launch budget changed.')
    }
    if ($case.Name -ne 'ReleaseFailure' -and
        @($script:monitorCase.Sessions | Where-Object { -not $_.Closed }).Count -ne 0) {
        $errors.Add('A successfully created snapshot session was not released.')
    }
    if ($case.Name -eq 'TransientInfoCancellation' -and
        @($script:monitorCase.Logs | Where-Object { $_ -like '*query failed (1/6)*' }).Count -ne 1) {
        $errors.Add('A transient info-read failure must be recorded before later fresh completion.')
    }
    $results.Add([pscustomobject]@{
        Case = $case.Name
        Passed = $errors.Count -eq 0
        Completed = $completed
        Error = $failure
        DefaultQueries = $script:monitorCase.DefaultQueries
        Created = $script:monitorCase.Created
        ReleaseAttempts = $script:monitorCase.ReleaseAttempts
        Removed = $script:monitorCase.Removed
        FailedAssertions = @($errors)
    })
}

$failed = @($results | Where-Object { -not $_.Passed }).Count
[pscustomobject]@{ Total=$results.Count; Failed=$failed; Results=@($results) } |
    ConvertTo-Json -Depth 6 -Compress
if ($failed -ne 0) { exit 1 }
