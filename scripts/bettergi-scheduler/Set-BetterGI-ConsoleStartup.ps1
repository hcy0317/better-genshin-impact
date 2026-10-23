[CmdletBinding()]
param([Parameter(Mandatory)][string]$BetterGIRoot, [switch]$Apply,
    [string]$StartupDirectory = [Environment]::GetFolderPath('Startup'))
$ErrorActionPreference = 'Stop'
$BetterGIRoot = (Resolve-Path -LiteralPath $BetterGIRoot).Path
$scheduler = Join-Path $BetterGIRoot 'scripts\bettergi-scheduler'
$guard = Join-Path $scheduler 'Start-BetterGI-ConsoleStartup.ps1'
$settingsPath = Join-Path $scheduler 'console-startup-applications.json'
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupLink = Join-Path $StartupDirectory 'LOL.ahk.lnk'
$shellPath = (Get-Command pwsh.exe -ErrorAction Stop).Source
if (-not (Test-Path -LiteralPath $guard -PathType Leaf)) { throw 'Console startup guard has not been deployed' }
$existingCommand = [string](Get-ItemProperty -LiteralPath $registry -Name IDMan -ErrorAction Stop).IDMan
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($startupLink)
if ($existingCommand.Contains($guard) -and $link.TargetPath -eq $shellPath -and $link.Arguments.Contains($guard)) {
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) { throw 'Guarded startup is missing its original application metadata' }
    [pscustomobject]@{status='already_configured';started=$false} | ConvertTo-Json
    return
}
if ($existingCommand -notmatch '^"?(?<file>.+?IDMan\.exe)"?\s*(?<arguments>.*)$') { throw 'Unexpected IDM startup command; preserve it for review' }
$idmPath = $Matches.file
$idmArguments = $Matches.arguments
if (-not (Test-Path -LiteralPath $idmPath -PathType Leaf)) { throw 'Original IDM executable is missing' }
if (-not (Test-Path -LiteralPath $startupLink -PathType Leaf) -or
    [IO.Path]::GetFileName($link.TargetPath) -ne 'LOL.ahk') { throw 'Unexpected AHK startup shortcut; preserve it for review' }
if (Test-Path -LiteralPath $settingsPath) { throw 'Existing guard metadata requires reconciliation before replacing startup entries' }
$arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -ApplicationId {1}'
$idmGuardArguments = $arguments -f $guard,'IDM'
$ahkGuardArguments = $arguments -f $guard,'LOL'
if (-not $Apply) {
    [pscustomobject]@{status='preview';registryValue='IDMan';shortcut=$startupLink;consoleOnly=$true;affectsRunningProcesses=$false;started=$false} | ConvertTo-Json
    return
}
$backup = Join-Path $BetterGIRoot ('.codex-backups\console-startup-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup | Out-Null
$backupLink = Join-Path $backup 'LOL.ahk.lnk'
Copy-Item -LiteralPath $startupLink -Destination $backupLink
$kind = (Get-Item -LiteralPath $registry).GetValueKind('IDMan')
[pscustomobject]@{registryPath=$registry;valueName='IDMan';value=$existingCommand;valueKind=[string]$kind;shortcut=$startupLink;shortcutHash=(Get-FileHash -LiteralPath $startupLink).Hash} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $backup 'original-startup.json') -Encoding utf8
$settings = @{
    IDM=@{filePath=$idmPath;arguments=$idmArguments;workingDirectory=[IO.Path]::GetDirectoryName($idmPath)}
    LOL=@{filePath=$backupLink;arguments='';workingDirectory=[IO.Path]::GetDirectoryName($link.TargetPath)}
}
try {
    $settings | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $settingsPath -Encoding utf8
    Set-ItemProperty -LiteralPath $registry -Name IDMan -Value ('"{0}" {1}' -f $shellPath,$idmGuardArguments)
    $link.TargetPath = $shellPath
    $link.Arguments = $ahkGuardArguments
    $link.WorkingDirectory = $scheduler
    $link.WindowStyle = 7
    $link.Save()
    $after = $shell.CreateShortcut($startupLink)
    if ([string](Get-ItemProperty -LiteralPath $registry -Name IDMan).IDMan -ne ('"{0}" {1}' -f $shellPath,$idmGuardArguments) -or
        $after.TargetPath -ne $shellPath -or $after.Arguments -ne $ahkGuardArguments) { throw 'Startup entry readback mismatch' }
} catch {
    $failure = $_
    $rollbackErrors = @()
    try { Set-ItemProperty -LiteralPath $registry -Name IDMan -Value $existingCommand } catch { $rollbackErrors += $_.Exception.Message }
    try { Copy-Item -LiteralPath $backupLink -Destination $startupLink -Force } catch { $rollbackErrors += $_.Exception.Message }
    if ($rollbackErrors.Count -eq 0) { Remove-Item -LiteralPath $settingsPath -Force -ErrorAction SilentlyContinue }
    if ($rollbackErrors.Count) { throw "Startup migration failed: $($failure.Exception.Message); rollback failures: $($rollbackErrors -join '; '); backup=$backup" }
    throw $failure
}
[pscustomobject]@{status='console_only';backup=$backup;consoleStartupPreserved=$true;childStartupSuppressed=$true;affectsRunningProcesses=$false;started=$false} | ConvertTo-Json
