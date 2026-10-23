param(
    [string]$BetterGIRoot,

    [ValidateNotNullOrEmpty()]
    [string]$CaptureMode = 'BitBlt',

    [ValidateSet('Keep', 'True', 'False')]
    [string]$ShowLogBox = 'Keep',

    [ValidateSet('Keep', 'True', 'False')]
    [string]$ShowStatus = 'Keep',

    [ValidateNotNullOrEmpty()]
    [string]$AutoPickKey = 'Keep',

    [ValidateSet('Keep', 'True', 'False')]
    [string]$CpuOcr = 'Keep',

    [ValidateSet('Keep', 'True', 'False')]
    [string]$AutoPickEnabled = 'Keep',

    [switch]$NoChange
)

$ErrorActionPreference = 'Stop'
$Utf8NoBom = [System.Text.UTF8Encoding]::new($false)

if ([string]::IsNullOrWhiteSpace($BetterGIRoot)) {
    $SchedulerDir = $PSScriptRoot
    $ScriptsDir = Split-Path -Parent $SchedulerDir
    $BetterGIRoot = Split-Path -Parent $ScriptsDir
}

$ConfigPath = Join-Path $BetterGIRoot 'User\config.json'

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "BetterGI config not found: $ConfigPath"
}

$configText = [System.IO.File]::ReadAllText($ConfigPath, $Utf8NoBom)
$config = $configText | ConvertFrom-Json
$changes = @()

function Set-ConfigProperty {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Target,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName
    )

    $currentValue = $Target.$Name
    if ($currentValue -eq $Value) {
        return "$DisplayName already $Value"
    }

    $Target | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
    return "$DisplayName changed from $currentValue to $Value"
}

$changes += Set-ConfigProperty -Target $config -Name 'captureMode' -Value $CaptureMode -DisplayName 'captureMode'

if ($ShowLogBox -ne 'Keep' -or $ShowStatus -ne 'Keep') {
    if (-not $config.maskWindowConfig) {
        throw "BetterGI maskWindowConfig not found in: $ConfigPath"
    }

    if ($ShowLogBox -ne 'Keep') {
        $changes += Set-ConfigProperty `
            -Target $config.maskWindowConfig `
            -Name 'showLogBox' `
            -Value ([bool]::Parse($ShowLogBox)) `
            -DisplayName 'maskWindowConfig.showLogBox'
    }

    if ($ShowStatus -ne 'Keep') {
        $changes += Set-ConfigProperty `
            -Target $config.maskWindowConfig `
            -Name 'showStatus' `
            -Value ([bool]::Parse($ShowStatus)) `
            -DisplayName 'maskWindowConfig.showStatus'
    }
}

if ($AutoPickKey -ne 'Keep' -or $AutoPickEnabled -ne 'Keep') {
    if (-not $config.autoPickConfig) {
        throw "BetterGI autoPickConfig not found in: $ConfigPath"
    }

    if ($AutoPickKey -ne 'Keep') {
        $changes += Set-ConfigProperty `
            -Target $config.autoPickConfig `
            -Name 'pickKey' `
            -Value $AutoPickKey `
            -DisplayName 'autoPickConfig.pickKey'
    }
    if ($AutoPickEnabled -ne 'Keep') {
        $changes += Set-ConfigProperty -Target $config.autoPickConfig -Name 'enabled' `
            -Value ([bool]::Parse($AutoPickEnabled)) -DisplayName 'autoPickConfig.enabled'
    }
}

if ($CpuOcr -ne 'Keep') {
    if (-not $config.hardwareAccelerationConfig) {
        throw "BetterGI hardwareAccelerationConfig not found in: $ConfigPath"
    }

    $changes += Set-ConfigProperty `
        -Target $config.hardwareAccelerationConfig `
        -Name 'cpuOcr' `
        -Value ([bool]::Parse($CpuOcr)) `
        -DisplayName 'hardwareAccelerationConfig.cpuOcr'
}

if ($NoChange) { $changes; return }

$tempPath = '{0}.tmp-{1}' -f $ConfigPath, $PID
try {
    $json = $config | ConvertTo-Json -Depth 100
    [System.IO.File]::WriteAllText($tempPath, "$json`n", $Utf8NoBom)
    Move-Item -LiteralPath $tempPath -Destination $ConfigPath -Force
}
catch {
    if (Test-Path -LiteralPath $tempPath -PathType Leaf) {
        Remove-Item -LiteralPath $tempPath -Force
    }
    throw
}

$changes
