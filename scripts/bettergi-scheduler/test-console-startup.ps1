param()
$ErrorActionPreference='Stop'
$source=Join-Path $PSScriptRoot 'Start-BetterGI-ConsoleStartup.ps1'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors | Out-String)}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-ConsoleStartupSession'},$true)
Invoke-Expression $definition.Extent.Text
if(-not (Test-ConsoleStartupSession 1 1)){throw 'Console startup must be preserved'}
if(Test-ConsoleStartupSession 12 1){throw 'Child session must not start console apps'}
if(Test-ConsoleStartupSession 12 ([uint32]::MaxValue)){throw 'Unavailable console must not trigger startup'}
$tempBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$root=Join-Path $tempBase ('bettergi-console-startup-'+[guid]::NewGuid().ToString('N'))
if(-not ([IO.Path]::GetFullPath($root)).StartsWith($tempBase+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test root'}
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    # Load the actual guard in a module with fake session detection and a throwing process API.
    $text=[IO.File]::ReadAllText($source)
    $text=$text.Replace('$current = [Diagnostics.Process]::GetCurrentProcess().SessionId','$current = 12').Replace('$console = [BetterGIConsoleStartup.Session]::WTSGetActiveConsoleSessionId()','$console = 1')
    $script=Join-Path $root 'guard.ps1'
    [IO.File]::WriteAllText($script,$text)
    function Start-Process { throw 'An offline guard test must never start a process' }
    foreach($application in @('IDM','LOL')){
        $result= & $script -ApplicationId $application | ConvertFrom-Json
        if($result.status -ne 'skipped_non_console' -or $result.started){throw 'Guard did not stop child-session launch'}
    }
    '5 checks passed: console preserved, child skipped, missing console skipped, IDM/AHK stopped before reading or launching originals'
} finally {
    $resolved=[IO.Path]::GetFullPath($root)
    if(-not $resolved.StartsWith($tempBase+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'bettergi-console-startup-*'){throw 'Unsafe cleanup'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
