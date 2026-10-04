[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallationRoot,
    [string]$BackupRoot,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$InstallationRoot = (Resolve-Path -LiteralPath $InstallationRoot).Path.TrimEnd('\')
if (-not $BackupRoot) {
    $BackupRoot = Join-Path $InstallationRoot ('.codex-backups\wpf-runtime-repair-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$BackupRoot = [IO.Path]::GetFullPath($BackupRoot)
if (-not $BackupRoot.StartsWith($InstallationRoot + '\.codex-backups\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Backup must be inside the installation .codex-backups directory.'
}
function Assert-NoLink([string]$Path) {
    for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and
            ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Linked repair path forbidden: $cursor"
        }
    }
}
function Assert-Stopped {
    if (Get-Process -Name BetterGI,BetterGenshinImpact -ErrorAction SilentlyContinue) {
        throw 'BetterGI is running; no process was stopped and no repair is permitted.'
    }
}
Assert-Stopped
Assert-NoLink $InstallationRoot
Assert-NoLink $BackupRoot
# This repair is only for framework-dependent installations. Keep self-contained packages intact.
$runtimeConfig = Join-Path $InstallationRoot 'BetterGI.runtimeconfig.json'
Assert-NoLink $runtimeConfig
$config = Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json
if (-not (@($config.runtimeOptions.frameworks) | Where-Object name -eq 'Microsoft.WindowsDesktop.App')) {
    throw 'Installation does not declare a shared WindowsDesktop runtime.'
}
foreach ($name in @('coreclr.dll', 'PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll')) {
    if (Test-Path -LiteralPath (Join-Path $InstallationRoot $name)) {
        throw "App-local framework assembly found; investigate package type first: $name"
    }
}
$names = @('wpfgfx_cor3.dll', 'PresentationNative_cor3.dll', 'PenImc_cor3.dll', 'D3DCompiler_47_cor3.dll')
$entries = @(foreach ($name in $names) {
    $path = Join-Path $InstallationRoot $name
    Assert-NoLink $path
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        [pscustomobject]@{name=$name;path=$path;sha256=(Get-FileHash -LiteralPath $path).Hash}
    }
})
if (-not $Apply) {
    [pscustomobject]@{mode='dry-run';files=$entries;backup=$BackupRoot} | ConvertTo-Json -Depth 4
    return
}
if ($entries.Count -eq 0) { Write-Output 'No app-local WPF runtime files found.'; return }
if (Test-Path -LiteralPath $BackupRoot) { throw 'Backup already exists; rollback data will not be overwritten.' }
$protected = @{}
foreach ($name in @('BetterGI.exe', 'BetterGI.runtimeconfig.json', 'User\config.json', 'User\CombatSkills\skills.db')) {
    $path = Join-Path $InstallationRoot $name
    if (Test-Path -LiteralPath $path -PathType Leaf) { $protected[$path] = (Get-FileHash -LiteralPath $path).Hash }
}
New-Item -ItemType Directory -Path $BackupRoot | Out-Null
# Verify every backup before removing the first exact file; never recursively clean the installation.
foreach ($entry in $entries) {
    Copy-Item -LiteralPath $entry.path -Destination (Join-Path $BackupRoot $entry.name)
    if ((Get-FileHash -LiteralPath (Join-Path $BackupRoot $entry.name)).Hash -ne $entry.sha256) {
        throw 'WPF runtime backup verification failed.'
    }
}
$entries | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $BackupRoot 'manifest.json') -Encoding utf8
foreach ($entry in $entries) {
    Assert-Stopped
    Assert-NoLink $entry.path
    if ((Get-FileHash -LiteralPath $entry.path).Hash -ne $entry.sha256) { throw 'Installation changed during repair.' }
    Remove-Item -LiteralPath $entry.path
}
foreach ($entry in $entries) {
    if (Test-Path -LiteralPath $entry.path) { throw 'App-local WPF runtime file remains.' }
}
foreach ($path in $protected.Keys) {
    if ((Get-FileHash -LiteralPath $path).Hash -ne $protected[$path]) { throw "Protected file changed: $path" }
}
$receipt = [pscustomobject]@{status='repaired';files=$entries;backup=$BackupRoot;started=$false;startupVerified=$false}
$receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $BackupRoot 'receipt.json') -Encoding utf8
$receipt | ConvertTo-Json -Depth 4
