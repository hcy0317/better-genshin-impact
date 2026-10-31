function Wait-BetterGIInnerScheduledTask {
    param(
        [Parameter(Mandatory)]
        [string]$TaskName,

        [Parameter(Mandatory)]
        [datetime]$PreviousLastRunTime,

        [Parameter(Mandatory)]
        [datetime]$Deadline,

        [ValidateRange(5, 300)]
        [int]$PollIntervalSeconds = 10,

        [ValidateRange(1, 60)]
        [int]$MaxConsecutiveQueryFailures = 6,

        [ValidateRange(10, 3600)]
        [int]$ProgressLogIntervalSeconds = 60,

        [ValidateRange(10, 3600)]
        [int]$LaunchTimeoutSeconds = 120,

        [scriptblock]$LogAction = { param([string]$Message) },

        [scriptblock]$TaskSnapshotProvider,

        [scriptblock]$NowProvider = { Get-Date },

        [scriptblock]$SleepAction = { param([int]$Seconds) Start-Sleep -Seconds $Seconds },

        [scriptblock]$HeartbeatAction
    )

    if (-not $TaskSnapshotProvider) {
        $TaskSnapshotProvider = {
            param([string]$SnapshotTaskName)

            $snapshotSession = New-CimSession -ErrorAction Stop
            try {
                $task = Get-ScheduledTask -TaskName $SnapshotTaskName -CimSession $snapshotSession -ErrorAction Stop
                $info = Get-ScheduledTaskInfo -TaskName $SnapshotTaskName -CimSession $snapshotSession -ErrorAction Stop
                [pscustomobject]@{
                    State = [string]$task.State
                    LastRunTime = $info.LastRunTime
                    LastTaskResult = $info.LastTaskResult
                }
            }
            finally {
                Remove-CimSession -CimSession $snapshotSession -ErrorAction Stop
            }
        }
    }

    $launchObserved = $false
    $launchObservedAt = $null
    $nextProgressLogAt = $null
    $consecutiveQueryFailures = 0
    $launchDeadline = $null

    while ($true) {
        if ($HeartbeatAction) {
            try {
                & $HeartbeatAction
            }
            catch {
                & $LogAction ("Inner task monitor heartbeat failed without stopping automation: {0}" -f $_.Exception.Message)
            }
        }

        $now = & $NowProvider
        if ($null -eq $launchDeadline) { $launchDeadline = $now.AddSeconds($LaunchTimeoutSeconds) }
        if ($now -ge $Deadline) {
            throw "Timed out waiting for Inner scheduled task '$TaskName'."
        }
        if (-not $launchObserved -and $now -ge $launchDeadline) {
            throw "Inner scheduled task '$TaskName' did not start within $LaunchTimeoutSeconds seconds."
        }

        try {
            $snapshot = & $TaskSnapshotProvider $TaskName
            $consecutiveQueryFailures = 0
        }
        catch {
            $consecutiveQueryFailures++
            & $LogAction (
                "Inner task monitor query failed ({0}/{1}): {2}" -f
                $consecutiveQueryFailures,
                $MaxConsecutiveQueryFailures,
                $_.Exception.Message)

            if ($consecutiveQueryFailures -ge $MaxConsecutiveQueryFailures) {
                throw (
                    "Unable to query Inner scheduled task '{0}' after {1} consecutive failures: {2}" -f
                    $TaskName,
                    $consecutiveQueryFailures,
                    $_.Exception.Message)
            }

            $backoffMultiplier = [Math]::Min(
                6,
                [Math]::Max(
                    1,
                    [Math]::Ceiling($consecutiveQueryFailures / [double]$MaxConsecutiveQueryFailures)))
            $retryDelaySeconds = [Math]::Min(300, $PollIntervalSeconds * $backoffMultiplier)
            & $SleepAction $retryDelaySeconds
            continue
        }

        if ($snapshot.State -ne 'Queued' -and $snapshot.LastRunTime -gt $PreviousLastRunTime) {
            if (-not $launchObserved) {
                & $LogAction (
                    "Observed Inner task '{0}' launch: state={1}, lastRun={2:O}." -f
                    $TaskName,
                    $snapshot.State,
                    $snapshot.LastRunTime)
                $launchObservedAt = $now
                $nextProgressLogAt = $now.AddSeconds($ProgressLogIntervalSeconds)
            }
            $launchObserved = $true
        }

        if ($launchObserved -and $snapshot.State -eq 'Running' -and
            $nextProgressLogAt -and $now -ge $nextProgressLogAt) {
            $elapsedSeconds = [int][Math]::Floor(($now - $launchObservedAt).TotalSeconds)
            & $LogAction (
                "Inner task '{0}' is still running: elapsed={1}s, state={2}, lastRun={3:O}, lastResult={4}." -f
                $TaskName,
                $elapsedSeconds,
                $snapshot.State,
                $snapshot.LastRunTime,
                $snapshot.LastTaskResult)
            $nextProgressLogAt = $now.AddSeconds($ProgressLogIntervalSeconds)
        }

        if ($launchObserved -and $snapshot.State -notin @('Running', 'Queued')) {
            if ($snapshot.LastTaskResult -ne 0) {
                throw (
                    "Inner scheduled task '{0}' failed with LastTaskResult {1}." -f
                    $TaskName,
                    $snapshot.LastTaskResult)
            }
            & $LogAction ("Inner task '{0}' completed successfully." -f $TaskName)
            return
        }

        & $SleepAction $PollIntervalSeconds
    }
}
