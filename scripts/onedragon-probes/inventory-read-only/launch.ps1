param(
    [Parameter(Mandatory)][string]$RuntimeRoot,
    [Parameter(Mandatory)][int]$ExpectedSessionId,
    [ValidateSet('Codex-Inventory-Probe-20260927','Codex-Inventory-Pages-Probe-20260927')]
    [string]$GroupName = 'Codex-Inventory-Probe-20260927'
)
$ErrorActionPreference = 'Stop'
$session = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
if ($session -ne $ExpectedSessionId -or $identity.Split('\')[-1] -ine 'Cyan') {
    throw "RDP identity mismatch: session=$session user=$identity"
}
$running = @(Get-Process -Name BetterGI -ErrorAction SilentlyContinue)
if ($running.Count -ne 0) { throw 'An existing BetterGI process prevents an isolated probe.' }
$foreignGame = @(Get-Process -Name YuanShen,GenshinImpact -ErrorAction SilentlyContinue | Where-Object SessionId -NE $session)
if ($foreignGame.Count -ne 0) { throw 'A game in another session prevents an isolated probe.' }
$exe = Join-Path $RuntimeRoot 'BetterGI.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Runtime executable missing.' }
$process = Start-Process -FilePath $exe -ArgumentList '--startGroups',$GroupName -WorkingDirectory $RuntimeRoot -WindowStyle Hidden -PassThru
if ($process.SessionId -ne $session) { throw 'Launched process session mismatch; no further action permitted.' }
[pscustomobject]@{ProcessId=$process.Id; SessionId=$session; User=$identity; Exe=$exe} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'launch-receipt.json') -Encoding utf8
