param([string]$LauncherPath = (Join-Path $PSScriptRoot 'Start-BetterGI-OneDragon.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($LauncherPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Wait-ChildSessionAutomationResult'
}, $true)
if ($null -eq $definition) { throw 'Result wait function missing' }
# Load only the wait function, never the launcher's executable top-level statements.
. ([scriptblock]::Create($definition.Extent.Text))
function Start-Process { throw 'Offline regression must not launch processes' }
function Stop-Process { throw 'Offline regression must not stop processes' }
function Write-RunLog { param($Message) }
function Get-Date { $script:now }
function Start-Sleep { param($Milliseconds)
    $script:now = $script:now.AddMilliseconds($Milliseconds)
    $script:poll++
}
function Test-Path { param($LiteralPath, $PathType)
    if ($LiteralPath -ne 'isolated-result') { throw 'Unexpected filesystem access' }
    return $script:resultExists
}
function Get-Content { param($LiteralPath, [switch]$Raw)
    if ($LiteralPath -ne 'isolated-result') { throw 'Unexpected filesystem access' }
    return $script:results[[Math]::Min($script:poll, $script:results.Count - 1)]
}
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Run-Case([string[]]$Results, [int]$ExitAt, [int]$ExitCode,
    [string]$ExpectedError, [string]$ExpectedStatus, [double]$MaximumSeconds = 2,
    [bool]$ResultExists = $true) {
    $script:now = [datetime]'2026-10-14T00:00:00Z'; $started = $script:now
    $script:poll = 0; $script:results = $Results; $script:exitAt = $ExitAt
    $script:resultExists = $ResultExists
    $process = [pscustomobject]@{ Id = 123; HasExited = $false; ExitCode = $ExitCode }
    $process | Add-Member ScriptMethod Refresh { $this.HasExited = $script:poll -ge $script:exitAt }
    $failure = $null; $result = $null
    try {
        $result = Wait-ChildSessionAutomationResult -Path 'isolated-result' -RunId 'current' `
            -TimeoutSeconds 10 -BetterGIProcess $process -ProcessExitResultGraceSeconds 1
    } catch { $failure = $_.Exception.Message }
    if ($ExpectedError) { Assert ($failure -like $ExpectedError) "Wrong failure: $failure" }
    else { Assert ($null -eq $failure -and $result.status -eq $ExpectedStatus) "Wrong terminal result: $failure" }
    Assert (($script:now - $started).TotalSeconds -le $MaximumSeconds) 'Old progress must not extend the exit grace period'
}

$checks = 0
foreach ($status in @('starting', 'accepted', 'running')) {
    Run-Case @("{`"runId`":`"current`",`"status`":`"$status`"}") 0 0 '*remained non-terminal*' ''
    $checks++
}
Run-Case @('{"runId":"current","status":"running"}') 2 0 '*remained non-terminal*' '' 3
$checks++
Run-Case @('{"runId":"current","status":"running"}') 0 5 '*remained non-terminal*' ''
$checks++
foreach ($status in @('succeeded', 'failed', 'cancelled', 'timed_out', 'cleanup_failed')) {
    Run-Case @("{`"runId`":`"current`",`"status`":`"$status`"}") 0 0 '' $status
    $checks++
}
Run-Case @('{"runId":"current","status":"running"}', '{"runId":"current","status":"succeeded"}') 0 0 '' 'succeeded'
$checks++
Run-Case @('{"runId":"other","status":"succeeded"}') 0 0 '*remained non-terminal*' ''
$checks++
Run-Case @('{partial-json') 0 0 '*remained non-terminal*' ''
$checks++
Run-Case @('') 0 0 '*remained non-terminal*' '' 2 $false
$checks++
Run-Case @('{"runId":"current","status":"running"}', '{"runId":"current","status":"running"}', '{"runId":"current","status":"succeeded"}') 100 0 '' 'succeeded'
$checks++
Run-Case @('{"runId":"current","status":"running"}') 100 0 '*did not finish within 10 second(s)*' '' 10
$checks++
"$checks checks passed: progress/death, late terminal, all terminal states, wrong run, partial/missing results, live executor, hard timeout; fake clock/process/result only"
