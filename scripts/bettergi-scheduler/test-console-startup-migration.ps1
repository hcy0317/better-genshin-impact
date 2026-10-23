param()
$ErrorActionPreference='Stop'
$migrationSource=Join-Path $PSScriptRoot 'Set-BetterGI-ConsoleStartup.ps1'
$tempBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testRoot=Join-Path $tempBase ('bettergi-console-migration-'+[guid]::NewGuid().ToString('N'))
if(-not ([IO.Path]::GetFullPath($testRoot)).StartsWith($tempBase+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test root'}
function Reset-State {
    $script:state=@{run='"'+(Join-Path $testRoot 'IDMan.exe')+'" /onboot';writes=0;saveFails=$false}
    $script:link=[pscustomobject]@{TargetPath=(Join-Path $testRoot 'LOL.ahk');Arguments='';WorkingDirectory=$testRoot;WindowStyle=1}
    $script:link | Add-Member ScriptMethod Save {
        if($script:state.saveFails){throw 'Simulated shortcut save failure'}
        'guarded shortcut' | Set-Content -LiteralPath (Join-Path $testRoot 'startup\LOL.ahk.lnk')
    }
    $script:shell=[pscustomobject]@{}
    $script:shell | Add-Member ScriptMethod CreateShortcut {param($Path) return $script:link}
    'original shortcut bytes' | Set-Content -LiteralPath (Join-Path $testRoot 'startup\LOL.ahk.lnk')
}
# Registry and COM operations are entirely replaced, including rollback.
function Get-ItemProperty {
    [CmdletBinding()]param($LiteralPath,$Name)
    if($LiteralPath -notlike 'HKCU:*'){throw 'Unexpected registry read'}
    [pscustomobject]@{IDMan=$script:state.run}
}
function Set-ItemProperty {
    [CmdletBinding()]param($LiteralPath,$Name,$Value)
    if($LiteralPath -notlike 'HKCU:*' -or $Name -ne 'IDMan'){throw 'Unexpected registry write'}
    $script:state.writes++; $script:state.run=$Value
}
function Get-Item {
    [CmdletBinding()]param($LiteralPath)
    if($LiteralPath -notlike 'HKCU:*'){throw 'Unexpected registry metadata read'}
    $value=[pscustomobject]@{}
    $value | Add-Member ScriptMethod GetValueKind {param($Name) return [Microsoft.Win32.RegistryValueKind]::String}
    return $value
}
function New-Object {param($ComObject) if($ComObject -ne 'WScript.Shell'){throw 'Unexpected COM object'}; return $script:shell}
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
try {
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'scripts\bettergi-scheduler'),(Join-Path $testRoot 'startup') -Force | Out-Null
    foreach($file in @('IDMan.exe','LOL.ahk','scripts\bettergi-scheduler\Start-BetterGI-ConsoleStartup.ps1')) { New-Item -ItemType File -Path (Join-Path $testRoot $file) | Out-Null }
    Reset-State
    $beforeRun=$script:state.run
    $beforeHash=(Get-FileHash -LiteralPath (Join-Path $testRoot 'startup\LOL.ahk.lnk')).Hash
    $result=. $migrationSource -BetterGIRoot $testRoot -StartupDirectory (Join-Path $testRoot 'startup') | ConvertFrom-Json
    Assert ($result.status -eq 'preview' -and $script:state.writes -eq 0) 'Preview modified registry'
    Assert ((Get-FileHash -LiteralPath (Join-Path $testRoot 'startup\LOL.ahk.lnk')).Hash -eq $beforeHash) 'Preview modified shortcut'

    $result=. $migrationSource -BetterGIRoot $testRoot -StartupDirectory (Join-Path $testRoot 'startup') -Apply | ConvertFrom-Json
    $data=Get-Content -LiteralPath (Join-Path $testRoot 'scripts\bettergi-scheduler\console-startup-applications.json') -Raw | ConvertFrom-Json
    Assert ($result.status -eq 'console_only' -and -not $result.started) 'Migration failed'
    Assert ($data.IDM.filePath -eq (Join-Path $testRoot 'IDMan.exe') -and $data.IDM.arguments -eq '/onboot') 'Original IDM action lost'
    Assert ((Get-FileHash -LiteralPath $data.LOL.filePath).Hash -eq $beforeHash) 'Original AHK shortcut lost'
    Assert ($script:state.run -like '*-ApplicationId IDM' -and $script:link.Arguments -like '*-ApplicationId LOL') 'Startup entries did not use guard'
    $writes=$script:state.writes
    $result=. $migrationSource -BetterGIRoot $testRoot -StartupDirectory (Join-Path $testRoot 'startup') -Apply | ConvertFrom-Json
    Assert ($result.status -eq 'already_configured' -and $script:state.writes -eq $writes) 'Repeated migration rewrote original metadata'

    # Remove only this verified test-owned settings file before testing rollback.
    $settings=Join-Path $testRoot 'scripts\bettergi-scheduler\console-startup-applications.json'
    Remove-Item -LiteralPath $settings
    Reset-State
    $beforeRun=$script:state.run
    $script:state.saveFails=$true
    $failed=$false
    try {. $migrationSource -BetterGIRoot $testRoot -StartupDirectory (Join-Path $testRoot 'startup') -Apply | Out-Null} catch { $failed=$true }
    Assert $failed 'Expected shortcut migration failure'
    Assert ($script:state.run -eq $beforeRun) 'Registry rollback failed'
    Assert ((Get-FileHash -LiteralPath (Join-Path $testRoot 'startup\LOL.ahk.lnk')).Hash -eq $beforeHash) 'Shortcut rollback failed'
    Assert (-not (Test-Path -LiteralPath $settings)) 'Failed migration left active guard metadata'
    '4 checks passed: read-only preview, original actions preserved, idempotent apply, failure rollback; no registry/COM/process APIs invoked'
} finally {
    $resolved=[IO.Path]::GetFullPath($testRoot)
    if(-not $resolved.StartsWith($tempBase+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'bettergi-console-migration-*'){throw 'Unsafe cleanup'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
