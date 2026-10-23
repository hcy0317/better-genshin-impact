param(
    [Parameter(Mandatory)][ValidateSet('IDM','LOL')][string]$ApplicationId,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'

function Test-ConsoleStartupSession([int]$CurrentSession, [uint32]$ConsoleSession) {
    $ConsoleSession -ne [uint32]::MaxValue -and $CurrentSession -eq $ConsoleSession
}
if (-not ('BetterGIConsoleStartup.Session' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
namespace BetterGIConsoleStartup {
    public static class Session {
        [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    }
}
'@
}
$current = [Diagnostics.Process]::GetCurrentProcess().SessionId
$console = [BetterGIConsoleStartup.Session]::WTSGetActiveConsoleSessionId()
if (-not (Test-ConsoleStartupSession $current $console)) {
    [pscustomobject]@{application=$ApplicationId;status='skipped_non_console';session=$current;started=$false} | ConvertTo-Json
    return
}
$configuration = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'console-startup-applications.json') -Raw | ConvertFrom-Json
$application = $configuration.$ApplicationId
if ($null -eq $application -or -not (Test-Path -LiteralPath $application.filePath -PathType Leaf)) { throw 'Original startup application is unavailable' }
if ($NoLaunch) {
    [pscustomobject]@{application=$ApplicationId;status='preview_console';session=$current;filePath=$application.filePath;arguments=$application.arguments;started=$false} | ConvertTo-Json
    return
}
$parameters = @{FilePath=[string]$application.filePath;WorkingDirectory=[string]$application.workingDirectory;WindowStyle='Hidden'}
if (-not [string]::IsNullOrWhiteSpace($application.arguments)) { $parameters.ArgumentList = [string]$application.arguments }
Start-Process @parameters
