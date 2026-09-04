param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigName,

    [switch]$NoLaunch,

    [switch]$ChildSessionAutomation,

    [string]$AutomationResultPath,

    [string]$AutomationRunId,

    [ValidateRange(60, 86400)]
    [int]$AutomationTimeoutSeconds = 14400,

    [bool]$UseNoSingleWhenRunning = $true,

    [bool]$EnableRdpLifecycle = $true,

    [string]$RdpFilePath,

    [string]$RdpUserName = 'Cyan',

    [bool]$OpenRdpClientOnStart = $true,

    [bool]$RequireCurrentRdpSession = $false,

    [int]$RdpWarmupSeconds = 20,

    [int]$RdpCloseTimeoutSeconds = 10,

    [bool]$LogoffRdpUserOnStop = $true,

    [bool]$SetRdpSessionVolumeOnStart = $true,

    [ValidateRange(0, 100)]
    [int]$RdpSessionVolumePercent = 0,

    [bool]$MuteRdpSessionAudio = $true,

    [int]$BetterGIExitTimeoutMinutes = 420,

    [string]$OwnedProcessPath,

    [string]$OneDragonLaunchLockName = 'Global\BetterGI-OneDragonLaunch',

    [ValidateRange(1, 1440)]
    [int]$OneDragonLaunchLockTimeoutMinutes = 480
)

$ErrorActionPreference = 'Stop'

$SchedulerDir = $PSScriptRoot
$ScriptsDir = Split-Path -Parent $SchedulerDir
$BetterGIRoot = Split-Path -Parent $ScriptsDir
$BetterGIExe = Join-Path $BetterGIRoot 'BetterGI.exe'
$OneDragonConfigPath = Join-Path $BetterGIRoot "User\OneDragon\$ConfigName.json"
$RequiredCaptureMode = 'BitBlt'
$RequiredShowLogBox = 'True'
$RequiredShowStatus = 'True'
$RequiredAutoPickKey = 'F'
$RequiredCpuOcr = 'True'
$RdpTargetAddress = '127.0.0.2'
$CaptureModeScript = Join-Path $SchedulerDir 'Set-BetterGICaptureMode.ps1'
$RdpLifecycleScript = Join-Path $SchedulerDir 'Use-BetterGI-RdpSession.ps1'
$RdpSessionVolumeScript = Join-Path $ScriptsDir 'rdp-background\Set-BetterGI-RdpSessionVolume.ps1'
$LogDir = Join-Path $SchedulerDir 'logs'
$LogPath = Join-Path $LogDir 'bettergi-scheduler.log'
if ([string]::IsNullOrWhiteSpace($OwnedProcessPath)) {
    $safeConfigName = $ConfigName -replace '[^\p{L}\p{N}._-]', '_'
    $OwnedProcessPath = Join-Path $LogDir ("owned-process-{0}.json" -f $safeConfigName)
}

if ($ChildSessionAutomation) {
    $EnableRdpLifecycle = $false
    $OpenRdpClientOnStart = $false
    $RequireCurrentRdpSession = $false
    $SetRdpSessionVolumeOnStart = $false
    if ([string]::IsNullOrWhiteSpace($AutomationRunId)) {
        $AutomationRunId = [Guid]::NewGuid().ToString('N')
    }
    if ([string]::IsNullOrWhiteSpace($AutomationResultPath)) {
        $AutomationResultPath = Join-Path $LogDir ("child-session-automation-{0}.json" -f $AutomationRunId)
    }
}

if ([string]::IsNullOrWhiteSpace($RdpFilePath)) {
    $RdpFilePath = Join-Path $BetterGIRoot 'scripts\rdp-background\BetterGI-Local-Loopback-1920x1080.rdp'
}

function Write-RunLog {
    param([string]$Message)

    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

function Test-BetterGIOneDragonCompletedSinceOffset {
    param(
        [string]$Path,
        [long]$StartOffset
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite)
    try {
        $offset = if ($StartOffset -ge 0 -and $StartOffset -le $stream.Length) { $StartOffset } else { 0 }
        $null = $stream.Seek($offset, [System.IO.SeekOrigin]::Begin)
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
        try {
            $appended = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
    return $appended.Contains('一条龙和配置组任务结束') -and
        $appended.Contains('游戏已退出，BetterGI 自动停止截图器')
}

function Write-OwnedProcessRecord {
    param([System.Diagnostics.Process]$OwnedProcess)

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OwnedProcessPath) | Out-Null
    $temporaryPath = $OwnedProcessPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [PSCustomObject]@{
        configName = $ConfigName
        ownedProcessId = $OwnedProcess.Id
        processStartTimeUtc = $OwnedProcess.StartTime.ToUniversalTime().ToString('O')
        recordedAtUtc = [DateTime]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
    Move-Item -LiteralPath $temporaryPath -Destination $OwnedProcessPath -Force
}

function Remove-OwnedProcessRecord {
    param([System.Diagnostics.Process]$OwnedProcess)

    if (-not $OwnedProcess -or -not (Test-Path -LiteralPath $OwnedProcessPath -PathType Leaf)) {
        return
    }
    try {
        $record = Get-Content -LiteralPath $OwnedProcessPath -Raw | ConvertFrom-Json
        if ([int]$record.ownedProcessId -eq $OwnedProcess.Id) {
            Remove-Item -LiteralPath $OwnedProcessPath -Force
        }
    }
    catch {
        Write-RunLog ("Unable to remove owned-process record safely: {0}" -f $_.Exception.Message)
    }
}

function Test-ShouldSetRdpSessionVolume {
    if (-not $SetRdpSessionVolumeOnStart) {
        return $false
    }

    if ($RequireCurrentRdpSession) {
        return $true
    }

    return [string]::Equals($env:USERNAME, $RdpUserName, [System.StringComparison]::OrdinalIgnoreCase)
}

function Invoke-RdpSessionVolumeSync {
    if (-not (Test-ShouldSetRdpSessionVolume)) {
        Write-RunLog ("Skipping RDP session volume sync; current user '{0}' is not '{1}' and RequireCurrentRdpSession is {2}." -f $env:USERNAME, $RdpUserName, $RequireCurrentRdpSession)
        return $false
    }

    if (-not (Test-Path -LiteralPath $RdpSessionVolumeScript -PathType Leaf)) {
        throw "RDP session volume script not found: $RdpSessionVolumeScript"
    }

    if ($NoLaunch) {
        Write-RunLog ("NoLaunch validation: would set current RDP session volume to {0}% and mute={1}." -f $RdpSessionVolumePercent, $MuteRdpSessionAudio)
        & $RdpSessionVolumeScript -VolumePercent $RdpSessionVolumePercent -Mute $MuteRdpSessionAudio -NoChange |
            ForEach-Object { Write-RunLog ("RDP session volume sync: {0}" -f $_) }
    }
    else {
        Write-RunLog ("Setting current RDP session volume to {0}% and mute={1} before BetterGI launch." -f $RdpSessionVolumePercent, $MuteRdpSessionAudio)
        & $RdpSessionVolumeScript -VolumePercent $RdpSessionVolumePercent -Mute $MuteRdpSessionAudio |
            ForEach-Object { Write-RunLog ("RDP session volume sync: {0}" -f $_) }
    }

    return $true
}

function Wait-ChildSessionAutomationResult {
    param(
        [string]$Path,
        [string]$RunId,
        [int]$TimeoutSeconds,
        [System.Diagnostics.Process]$BetterGIProcess,
        [int]$ProcessExitResultGraceSeconds = 3
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $processExitObservedAt = $null
    do {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            try {
                $result = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
                if ($result.runId -eq $RunId -and $result.status -in @(
                    'succeeded',
                    'failed',
                    'cancelled',
                    'timed_out',
                    'cleanup_failed'
                )) {
                    return $result
                }
            }
            catch {
                Write-RunLog ("Waiting for an atomic Child Session result update: {0}" -f $_.Exception.Message)
            }
        }

        if ($null -ne $BetterGIProcess) {
            $BetterGIProcess.Refresh()
            if ($BetterGIProcess.HasExited) {
                if ($null -eq $processExitObservedAt) {
                    $processExitObservedAt = Get-Date
                    Write-RunLog (
                        "BetterGI PID {0} exited with code {1} before Child Session run {2} published a terminal result; waiting up to {3} second(s) for the final atomic update." -f
                        $BetterGIProcess.Id,
                        $BetterGIProcess.ExitCode,
                        $RunId,
                        $ProcessExitResultGraceSeconds
                    )
                }
                elseif (((Get-Date) - $processExitObservedAt).TotalSeconds -ge $ProcessExitResultGraceSeconds) {
                    throw (
                        "BetterGI process {0} exited with code {1}, but Child Session automation '{2}' remained non-terminal for more than {3} second(s)." -f
                        $BetterGIProcess.Id,
                        $BetterGIProcess.ExitCode,
                        $RunId,
                        $ProcessExitResultGraceSeconds
                    )
                }
            }
            else {
                $processExitObservedAt = $null
            }
        }

        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    throw "Child Session automation '$RunId' did not finish within $TimeoutSeconds second(s)."
}

if (-not (Test-Path -LiteralPath $BetterGIExe -PathType Leaf)) {
    throw "BetterGI.exe not found: $BetterGIExe"
}

if (-not (Test-Path -LiteralPath $CaptureModeScript -PathType Leaf)) {
    throw "Capture mode script not found: $CaptureModeScript"
}

if (-not (Test-Path -LiteralPath $RdpLifecycleScript -PathType Leaf)) {
    throw "RDP lifecycle script not found: $RdpLifecycleScript"
}

. $RdpLifecycleScript
$rdpLogAction = { param([string]$Message) Write-RunLog $Message }
$rdpSession = $null
$didOrWouldSetRdpSessionVolume = $false
$oneDragonLaunchMutex = $null
$oneDragonLaunchLockTaken = $false
$process = $null

try {
    if (-not (Test-Path -LiteralPath $OneDragonConfigPath -PathType Leaf)) {
        throw "OneDragon config not found: $OneDragonConfigPath"
    }
    Write-RunLog ("Verified OneDragon config exists without inspecting user-owned content: {0}" -f $OneDragonConfigPath)

    $oneDragonLaunchMutex = [System.Threading.Mutex]::new($false, $OneDragonLaunchLockName)
    Write-RunLog ("Waiting for one-dragon launch lock '{0}' for up to {1} minute(s)." -f $OneDragonLaunchLockName, $OneDragonLaunchLockTimeoutMinutes)
    try {
        $oneDragonLaunchLockTaken = $oneDragonLaunchMutex.WaitOne([TimeSpan]::FromMinutes($OneDragonLaunchLockTimeoutMinutes))
    }
    catch [System.Threading.AbandonedMutexException] {
        $oneDragonLaunchLockTaken = $true
        Write-RunLog ("Recovered abandoned one-dragon launch lock '{0}' from a previous terminated launcher." -f $OneDragonLaunchLockName)
    }
    if (-not $oneDragonLaunchLockTaken) {
        throw "Timed out waiting for one-dragon launch lock '$OneDragonLaunchLockName'. Another BetterGI one-dragon launcher is still running."
    }
    Write-RunLog ("Acquired one-dragon launch lock '{0}'." -f $OneDragonLaunchLockName)

    $rdpSession = Start-BetterGIRdpSession `
        -RdpFilePath $RdpFilePath `
        -TargetAddress $RdpTargetAddress `
        -RdpUserName $RdpUserName `
        -OpenRdpClient $OpenRdpClientOnStart `
        -RequireCurrentRdpSession $RequireCurrentRdpSession `
        -EnableRdpLifecycle $EnableRdpLifecycle `
        -NoLaunch:$NoLaunch `
        -WarmupSeconds $RdpWarmupSeconds `
        -LogAction $rdpLogAction

    $didOrWouldSetRdpSessionVolume = Invoke-RdpSessionVolumeSync

    if ($NoLaunch) {
        Write-RunLog "Ensuring BetterGI capture mode is $RequiredCaptureMode before NoLaunch validation."
    }
    else {
        Write-RunLog "Ensuring BetterGI capture mode is $RequiredCaptureMode before BetterGI launch."
    }
    & $CaptureModeScript `
        -BetterGIRoot $BetterGIRoot `
        -CaptureMode $RequiredCaptureMode `
        -ShowLogBox $RequiredShowLogBox `
        -ShowStatus $RequiredShowStatus `
        -AutoPickKey $RequiredAutoPickKey `
        -CpuOcr $RequiredCpuOcr |
        ForEach-Object { Write-RunLog ("Capture mode sync: {0}" -f $_) }

    $existingBetterGI = @(Get-Process -Name 'BetterGI' -ErrorAction SilentlyContinue)
    if ($ChildSessionAutomation) {
        $argsForBetterGI = @(
            '--child-session-one-dragon',
            $ConfigName,
            '--automation-result',
            $AutomationResultPath,
            '--automation-run-id',
            $AutomationRunId,
            '--automation-timeout-seconds',
            [string]$AutomationTimeoutSeconds
        )
        Write-RunLog ("Using BetterGI Child Session automation for config '{0}' (run {1}); --no-single is intentionally disabled." -f $ConfigName, $AutomationRunId)
    }
    else {
        $argsForBetterGI = @('startOneDragon', $ConfigName)
    }

    if (-not $ChildSessionAutomation -and $existingBetterGI.Count -gt 0) {
        if ($UseNoSingleWhenRunning) {
            # BetterGI parses the business action from argv[1], so --no-single must stay after the config name.
            $argsForBetterGI += '--no-single'
            Write-RunLog ("Detected {0} existing BetterGI process(es); using startOneDragon with --no-single for config '{1}'." -f $existingBetterGI.Count, $ConfigName)
        }
        else {
            Write-RunLog ("Detected {0} existing BetterGI process(es); starting without --no-single would only focus the existing window." -f $existingBetterGI.Count)
        }
    }
    elseif (-not $ChildSessionAutomation) {
        Write-RunLog ("No existing BetterGI process detected; starting config '{0}' normally." -f $ConfigName)
    }

    if ($NoLaunch) {
        Write-RunLog ("NoLaunch validation: {0} {1}" -f $BetterGIExe, ($argsForBetterGI -join ' '))
        [PSCustomObject]@{
            betterGIExe = $BetterGIExe
            workingDirectory = $BetterGIRoot
            configName = $ConfigName
            existingBetterGIProcessCount = $existingBetterGI.Count
            argumentList = $argsForBetterGI
            childSessionAutomation = [bool]$ChildSessionAutomation
            automationResultPath = $AutomationResultPath
            automationRunId = $AutomationRunId
            automationTimeoutSeconds = $AutomationTimeoutSeconds
            requiredCaptureMode = $RequiredCaptureMode
            requiredShowLogBox = [bool]::Parse($RequiredShowLogBox)
            requiredShowStatus = [bool]::Parse($RequiredShowStatus)
            requiredAutoPickKey = $RequiredAutoPickKey
            requiredCpuOcr = [bool]::Parse($RequiredCpuOcr)
            rdpLifecycleEnabled = $EnableRdpLifecycle
            rdpFilePath = $rdpSession.rdpFilePath
            rdpTargetAddress = '127.0.0.2'
            rdpUserName = $rdpSession.userName
            openRdpClientOnStart = $OpenRdpClientOnStart
            requireCurrentRdpSession = $RequireCurrentRdpSession
            rdpWarmupSeconds = $RdpWarmupSeconds
            betterGIExitTimeoutMinutes = $BetterGIExitTimeoutMinutes
            setRdpSessionVolumeOnStart = $SetRdpSessionVolumeOnStart
            rdpSessionVolumePercent = $RdpSessionVolumePercent
            muteRdpSessionAudio = $MuteRdpSessionAudio
            wouldSetRdpSessionVolume = $didOrWouldSetRdpSessionVolume
            wouldOpenRdp = [bool]$rdpSession.wouldOpenRdp
            wouldCloseRdp = [bool]$rdpSession.wouldOpenRdp
            wouldLogoffRdpUser = [bool]$rdpSession.wouldLogoffRdpUser
            wouldLaunch = $false
        } | ConvertTo-Json -Depth 4
        return
    }

    $dailyBetterGiLogPath = Join-Path $BetterGIRoot ("log\better-genshin-impact{0}.log" -f (Get-Date -Format 'yyyyMMdd'))
    $dailyBetterGiLogOffset = if (Test-Path -LiteralPath $dailyBetterGiLogPath -PathType Leaf) {
        (Get-Item -LiteralPath $dailyBetterGiLogPath).Length
    } else { 0L }
    if ($ChildSessionAutomation -and (Test-Path -LiteralPath $AutomationResultPath -PathType Leaf)) {
        Remove-Item -LiteralPath $AutomationResultPath -Force
    }
    $processStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processStartInfo.FileName = $BetterGIExe
    $processStartInfo.WorkingDirectory = $BetterGIRoot
    $processStartInfo.UseShellExecute = $false
    foreach ($argument in $argsForBetterGI) {
        $processStartInfo.ArgumentList.Add([string]$argument)
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $processStartInfo
    if (-not $process.Start()) {
        throw "Failed to start BetterGI for config '$ConfigName'."
    }
    Write-OwnedProcessRecord -OwnedProcess $process
    Write-RunLog ("Started BetterGI PID {0}: {1}" -f $process.Id, ($argsForBetterGI -join ' '))

    if ($ChildSessionAutomation) {
        $automationResult = Wait-ChildSessionAutomationResult `
            -Path $AutomationResultPath `
            -RunId $AutomationRunId `
            -TimeoutSeconds $AutomationTimeoutSeconds `
            -BetterGIProcess $process
        Write-RunLog ("Child Session automation run {0} finished with status {1}: {2}" -f $AutomationRunId, $automationResult.status, $automationResult.message)
        if ($automationResult.status -ne 'succeeded') {
            throw "Child Session automation '$AutomationRunId' finished with status '$($automationResult.status)': $($automationResult.message)"
        }
        return
    }
    elseif ($BetterGIExitTimeoutMinutes -gt 0) {
        $timeoutMilliseconds = [int][TimeSpan]::FromMinutes($BetterGIExitTimeoutMinutes).TotalMilliseconds
        if (-not $process.WaitForExit($timeoutMilliseconds)) {
            throw "BetterGI process $($process.Id) did not exit within $BetterGIExitTimeoutMinutes minute(s); closing tracked RDP client and failing the scheduled task."
        }
    }
    else {
        $process.WaitForExit()
    }

    Write-RunLog ("BetterGI PID {0} exited with code {1}; closing tracked RDP client." -f $process.Id, $process.ExitCode)
    if ($process.ExitCode -ne 0) {
        if (Test-BetterGIOneDragonCompletedSinceOffset -Path $dailyBetterGiLogPath -StartOffset $dailyBetterGiLogOffset) {
            Write-RunLog ("Accepted BetterGI exit code {0} because this launch recorded both one-dragon completion and game-exit markers." -f $process.ExitCode)
        }
        else {
            throw "BetterGI process $($process.Id) exited with code $($process.ExitCode)."
        }
    }
}
catch {
    $exceptionType = if ($_.Exception) { $_.Exception.GetType().FullName } else { '<unknown>' }
    $scriptStack = if ($_.ScriptStackTrace) { $_.ScriptStackTrace } else { '<unavailable>' }
    Write-RunLog (
        "FAILED: OneDragon launch failed; config='{0}'; user='{1}'; session={2}; exceptionType='{3}'; message={4}" -f
        $ConfigName,
        $env:USERNAME,
        (Get-Process -Id $PID).SessionId,
        $exceptionType,
        $_.Exception.Message)
    Write-RunLog ("FAILED STACK: {0}" -f $scriptStack)
    throw
}
finally {
    try {
        if ($process) {
            $process.Refresh()
            if ($process.HasExited) {
                Remove-OwnedProcessRecord -OwnedProcess $process
            }
            else {
                Write-RunLog ("Keeping owned-process record for live BetterGI PID {0} so the outer scheduler can clean up this launch." -f $process.Id)
            }
        }
        Stop-BetterGIRdpSession -Session $rdpSession -CloseTimeoutSeconds $RdpCloseTimeoutSeconds -LogoffRdpUser $LogoffRdpUserOnStop -LogAction $rdpLogAction
    }
    finally {
        if ($oneDragonLaunchLockTaken) {
            $oneDragonLaunchMutex.ReleaseMutex()
            Write-RunLog ("Released one-dragon launch lock '{0}'." -f $OneDragonLaunchLockName)
        }
        if ($oneDragonLaunchMutex) {
            $oneDragonLaunchMutex.Dispose()
        }
    }
}
