param()

$ErrorActionPreference = 'Stop'

$launcherSource = Join-Path $PSScriptRoot 'Start-BetterGI-OneDragon.ps1'
if (-not (Test-Path -LiteralPath $launcherSource -PathType Leaf)) {
    throw "Launcher source not found: $launcherSource"
}

$systemTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$testRoot = Join-Path $systemTempRoot ("bettergi-startup-contract-{0}" -f [Guid]::NewGuid().ToString('N'))
$resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
if (-not $resolvedTestRoot.StartsWith($systemTempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe temporary test root: $resolvedTestRoot"
}

try {
    $schedulerRoot = Join-Path $testRoot 'scripts\bettergi-scheduler'
    $rdpHelperRoot = Join-Path $testRoot 'scripts\rdp-background'
    $oneDragonRoot = Join-Path $testRoot 'User\OneDragon'
    New-Item -ItemType Directory -Force -Path $schedulerRoot, $rdpHelperRoot, $oneDragonRoot | Out-Null

    $launcherPath = Join-Path $schedulerRoot 'Start-BetterGI-OneDragon.ps1'
    Copy-Item -LiteralPath $launcherSource -Destination $launcherPath
    New-Item -ItemType File -Path (Join-Path $testRoot 'BetterGI.exe') | Out-Null

    $captureRecordPath = Join-Path $testRoot 'capture-settings.json'
    $env:BETTERGI_STARTUP_TEST_CAPTURE_RECORD = $captureRecordPath
    @'
param(
    [string]$BetterGIRoot,
    [string]$CaptureMode,
    [string]$ShowLogBox,
    [string]$ShowStatus,
    [string]$AutoPickKey,
    [string]$CpuOcr
)
[PSCustomObject]@{
    betterGIRoot = $BetterGIRoot
    captureMode = $CaptureMode
    showLogBox = $ShowLogBox
    showStatus = $ShowStatus
    autoPickKey = $AutoPickKey
    cpuOcr = $CpuOcr
} | ConvertTo-Json | Set-Content -LiteralPath $env:BETTERGI_STARTUP_TEST_CAPTURE_RECORD -Encoding UTF8
'capture settings recorded'
'@ | Set-Content -LiteralPath (Join-Path $schedulerRoot 'Set-BetterGICaptureMode.ps1') -Encoding UTF8

    @'
function Start-BetterGIRdpSession {
    [CmdletBinding()]
    param(
        [string]$RdpFilePath,
        [string]$TargetAddress,
        [string]$RdpUserName,
        [bool]$OpenRdpClient,
        [bool]$RequireCurrentRdpSession,
        [bool]$EnableRdpLifecycle,
        [switch]$NoLaunch,
        [int]$WarmupSeconds,
        [scriptblock]$LogAction
    )
    [PSCustomObject]@{
        rdpFilePath = $RdpFilePath
        userName = $RdpUserName
        wouldOpenRdp = $false
        wouldLogoffRdpUser = $false
    }
}

function Stop-BetterGIRdpSession {
    param(
        $Session,
        [int]$CloseTimeoutSeconds,
        [bool]$LogoffRdpUser,
        [scriptblock]$LogAction
    )
}
'@ | Set-Content -LiteralPath (Join-Path $schedulerRoot 'Use-BetterGI-RdpSession.ps1') -Encoding UTF8

    $configPath = Join-Path $oneDragonRoot '每日自动化总控.json'
    [IO.File]::WriteAllText($configPath, 'user-owned content that is intentionally not parsed', [Text.UTF8Encoding]::new($false))
    $beforeHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash

    $resultText = & $launcherPath `
        -ConfigName '每日自动化总控' `
        -NoLaunch `
        -EnableRdpLifecycle $false `
        -SetRdpSessionVolumeOnStart $false
    $result = $resultText | ConvertFrom-Json

    if ($result.configName -ne '每日自动化总控' -or $result.wouldLaunch) {
        throw 'NoLaunch did not return the expected daily-controller launch plan.'
    }
    if ($result.requiredCaptureMode -ne 'BitBlt' -or
        -not $result.requiredShowLogBox -or
        -not $result.requiredShowStatus -or
        $result.requiredAutoPickKey -ne 'F' -or
        -not $result.requiredCpuOcr) {
        throw 'NoLaunch did not preserve the required BetterGI stable settings.'
    }

    $captureRecord = Get-Content -LiteralPath $captureRecordPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($captureRecord.captureMode -ne 'BitBlt' -or
        $captureRecord.showLogBox -ne 'True' -or
        $captureRecord.showStatus -ne 'True' -or
        $captureRecord.autoPickKey -ne 'F' -or
        $captureRecord.cpuOcr -ne 'True') {
        throw 'Capture-mode synchronization did not receive the five stable BetterGI settings.'
    }

    $afterHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
    if ($afterHash -ne $beforeHash) {
        throw 'Daily-controller startup validation changed user-owned config content.'
    }

    Remove-Item -LiteralPath $configPath -Force
    $missingConfigFailure = $null
    try {
        & $launcherPath `
            -ConfigName '每日自动化总控' `
            -NoLaunch `
            -EnableRdpLifecycle $false `
            -SetRdpSessionVolumeOnStart $false | Out-Null
    }
    catch {
        $missingConfigFailure = $_
    }
    if ($null -eq $missingConfigFailure -or
        -not $missingConfigFailure.Exception.Message.Contains('OneDragon config not found', [StringComparison]::Ordinal)) {
        throw 'Missing daily-controller config did not fail with the existence-only gate.'
    }

    Write-Output 'one-dragon startup contract passed'
}
finally {
    Remove-Item Env:BETTERGI_STARTUP_TEST_CAPTURE_RECORD -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
