param([string]$FixtureRoot = (Join-Path $PSScriptRoot '../.codex-tmp/s71-c-validation/album-fixture'))
$ErrorActionPreference = 'Stop'
$album = Join-Path $PSScriptRoot 'New-EvidenceAlbum.ps1'
if (-not (Test-Path -LiteralPath $album)) { throw 'Missing offline evidence album implementation' }
Add-Type -AssemblyName System.Drawing
$caseRoot = Join-Path ([IO.Path]::GetFullPath($FixtureRoot)) ([guid]::NewGuid().ToString('N'))
$inputRoot = Join-Path $caseRoot 'input'
$run = Join-Path $inputRoot ([guid]::NewGuid().ToString('N'))
$outputRoot = Join-Path $caseRoot 'output'
[IO.Directory]::CreateDirectory($run) | Out-Null
for ($i = 1; $i -le 14; $i++) {
    $name = Join-Path $run ('evidence-{0:d4}' -f $i)
    $bitmap = [Drawing.Bitmap]::new(20,20)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try { $graphics.Clear([Drawing.Color]::FromArgb($i*15,40,80)); $bitmap.Save($name+'.png',[Drawing.Imaging.ImageFormat]::Png) }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
    $metadata = @{RunId='fixture';Sequence=$i;Request='<script>fixture</script>';Phase='phase';Source=@{SessionId='fixture-source';Sequence=$i;CapturedTimestamp=$i;CapturedAt='2026-09-27T00:00:00Z'}}
    if ($i -eq 1) { $metadata.Source.CapturedTimestamp = 0 }
    if($i -eq 14){ $metadata.Window=@{WindowId='fixture-window';RelativeIndex=1;Anchor=@{Sequence=13}} }
    [IO.File]::WriteAllText($name+'.json',($metadata | ConvertTo-Json -Depth 8))
}
[IO.File]::WriteAllText((Join-Path $run 'evidence-0015.json'), '{broken')
[IO.File]::WriteAllText((Join-Path $run 'evidence-0016.json'), '{"Request":"missing image","Phase":"phase","Source":{}}')
$before = @(Get-ChildItem -LiteralPath $inputRoot -File -Recurse | Get-FileHash | ForEach-Object Hash)
& $album -InputRoot $inputRoot -OutputDirectory $outputRoot -PageSize 12
$manifest = Get-Content -Raw -LiteralPath (Join-Path $outputRoot 'manifest.json') | ConvertFrom-Json
if ($manifest.frames.Count -ne 16) { throw 'Legacy/window/bad/missing records not preserved' }
if (@($manifest.frames | Where-Object status -eq 'ok').Count -ne 14) { throw 'Expected 14 rendered frames' }
if (@(Get-ChildItem -LiteralPath $outputRoot -Filter 'contact-*.png').Count -ne 2) { throw 'Pagination missing' }
if (-not @($manifest.frames | Where-Object { $_.difference -gt 0 }).Count) { throw 'Frame difference missing' }
$html = Get-Content -Raw -LiteralPath (Join-Path $outputRoot 'index.html')
if ($html.Contains('<script>fixture</script>') -or -not $html.Contains('&lt;script&gt;fixture&lt;/script&gt;')) { throw 'Unsafe HTML' }
$after = @(Get-ChildItem -LiteralPath $inputRoot -File -Recurse | Get-FileHash | ForEach-Object Hash)
if (Compare-Object $before $after) { throw 'Input changed' }

function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Expected path protection rejection' }
}
Assert-Rejected { & $album -InputRoot $inputRoot -OutputDirectory (Join-Path $inputRoot 'nested-output') }
Assert-Rejected { & $album -InputRoot $inputRoot -OutputDirectory $outputRoot }
$edgeRoot = Join-Path $caseRoot 'edge-input'
[IO.Directory]::CreateDirectory($edgeRoot) | Out-Null
foreach ($i in 1..4) {
    $metadata = @{RunId='edge';Sequence=$i;Request='=1+2';Phase='edge';Source=@{SessionId='@source';Sequence=$i;CapturedTimestamp=$i};File=(Join-Path $run 'evidence-0001.png')}
    if ($i -eq 3) { $metadata.Source = @{} }
    [IO.File]::WriteAllText((Join-Path $edgeRoot ('evidence-{0:d4}.json' -f $i)), ($metadata | ConvertTo-Json -Depth 8))
}
$oversized = [byte[]]::new(24)
[byte[]]@(137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,32,1,0,0,0,1) | ForEach-Object -Begin { $j = 0 } -Process { $oversized[$j++] = $_ }
[IO.File]::WriteAllBytes((Join-Path $edgeRoot 'evidence-0001.png'), $oversized)
[IO.File]::WriteAllText((Join-Path $edgeRoot 'evidence-0002.png'), 'not a png')
Copy-Item -LiteralPath (Join-Path $run 'evidence-0001.png') -Destination (Join-Path $edgeRoot 'evidence-0003.png')
$link = Join-Path $edgeRoot 'linked-run'
New-Item -ItemType Junction -Path $link -Target $run | Out-Null
Assert-Rejected { & $album -InputRoot $link -OutputDirectory (Join-Path $caseRoot 'linked-input-output') }
Assert-Rejected { & $album -InputRoot $inputRoot -OutputDirectory (Join-Path $link 'linked-output') }
$edgeOutput = Join-Path $caseRoot 'edge-output'
& $album -InputRoot $edgeRoot -OutputDirectory $edgeOutput -PageSize 1 | Out-Null
$edge = Get-Content -Raw -LiteralPath (Join-Path $edgeOutput 'manifest.json') | ConvertFrom-Json
if ($edge.frames.Count -ne 4 -or $edge.skippedLinks -ne 1 -or $edge.pages.Count -ne 4) { throw 'Link skipping or bounded pagination failed' }
if (@($edge.frames | Where-Object status -eq 'invalid-image').Count -ne 2) { throw 'Oversized/corrupt PNG was not retained as an error' }
if (@($edge.frames | Where-Object status -eq 'unknown-source').Count -ne 1) { throw 'Unknown source was not retained as an error' }
if (@($edge.frames | Where-Object status -eq 'missing-png').Count -ne 1) { throw 'Tool must not load a path supplied in JSON' }
$csv = Import-Csv -LiteralPath (Join-Path $edgeOutput 'manifest.csv')
if (@($csv | Where-Object request -ne "'=1+2").Count) { throw 'Unsafe CSV formula' }
if (@($csv | Where-Object { $_.sourceSession -eq '@source' }).Count) { throw 'Unsafe CSV source field' }
[pscustomobject]@{passed=$true;frames=16;pages=2;inputUnchanged=$true;pathAndImageGuards=$true;output=$outputRoot} | ConvertTo-Json
