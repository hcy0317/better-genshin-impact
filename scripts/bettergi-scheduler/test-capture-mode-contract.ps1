param([string]$SourcePath = (Join-Path $PSScriptRoot 'Set-BetterGICaptureMode.ps1'))
$ErrorActionPreference = 'Stop'
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$root = Join-Path $tempBase ('bettergi-capture-contract-' + [guid]::NewGuid().ToString('N'))
if (-not $root.StartsWith($tempBase + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root' }
function Start-Process { throw 'Offline regression must not launch processes' }
function Stop-Process { throw 'Offline regression must not stop processes' }
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
try {
    New-Item -ItemType Directory -Path (Join-Path $root 'User') -Force | Out-Null
    $path = Join-Path $root 'User\config.json'
    $text = '{"captureMode":"WindowsGraphicsCapture","maskWindowConfig":{"showLogBox":false,"showStatus":false},"autoPickConfig":{"enabled":false,"pickKey":"Y","whiteList":["keep"],"blackList":["never"]},"hardwareAccelerationConfig":{"cpuOcr":false},"unrelated":{"secret":"literal-$()-`-中文-\\-\"","number":12345}}'
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
    $before = (Get-FileHash -LiteralPath $path).Hash
    $original = $text | ConvertFrom-Json
    & $SourcePath -BetterGIRoot $root -CaptureMode BitBlt -ShowLogBox True -ShowStatus True -AutoPickKey F -CpuOcr True -NoChange | Out-Null
    Assert ((Get-FileHash -LiteralPath $path).Hash -eq $before) 'Preview changed original config bytes'
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $root 'User') -File).Count -eq 1) 'Preview created temporary files'
    & $SourcePath -BetterGIRoot $root -CaptureMode BitBlt -ShowLogBox True -ShowStatus True -AutoPickKey F -CpuOcr True | Out-Null
    $after = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    Assert ($after.captureMode -eq 'BitBlt' -and $after.maskWindowConfig.showLogBox -and $after.maskWindowConfig.showStatus -and $after.autoPickConfig.pickKey -eq 'F' -and $after.hardwareAccelerationConfig.cpuOcr) 'Requested settings not applied'
    Assert (-not $after.autoPickConfig.enabled -and $after.autoPickConfig.whiteList[0] -eq 'keep' -and $after.autoPickConfig.blackList[0] -eq 'never') 'Unrequested pickup configuration changed'
    Assert ($after.unrelated.secret -ceq $original.unrelated.secret -and $after.unrelated.number -eq 12345) 'Unrelated values changed'
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $root 'User') -File).Count -eq 1) 'Apply left temporary files'
    '6 checks passed: real helper preview bytes/temp files, apply settings, pickup preservation, unrelated values, temporary cleanup; isolated config only'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith($tempBase + '\', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'bettergi-capture-contract-*') { throw 'Unsafe cleanup' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
