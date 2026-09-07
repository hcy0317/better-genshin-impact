[CmdletBinding()]
param(
    [string]$EngineRef = '1de5a42438791757a7178b16e59ec97dc1690d61',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin\packages')
)
$ErrorActionPreference = 'Stop'
if ($EngineRef -notmatch '^[a-zA-Z0-9._/-]{1,100}$') { throw 'Invalid public upstream ref.' }
$revision = $EngineRef
if ($EngineRef -notmatch '^[0-9a-f]{40}$') {
    $revisionInfo = Invoke-RestMethod -Uri ('https://api.github.com/repos/genshinsim/gcsim/commits/' + [Uri]::EscapeDataString($EngineRef)) -TimeoutSec 30
    $revision = [string]$revisionInfo.sha
}
if ($revision -notmatch '^[0-9a-f]{40}$') { throw 'The upstream ref did not resolve to a full revision.' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('gcsim-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
# This is an isolated build copy; neither go.mod nor running installations change.
foreach ($name in @('cmd','internal','go.mod','go.sum')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $work -Recurse }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$env:GOMAXPROCS = '2'
$env:GOOS = 'windows'; $env:GOARCH = 'amd64'; $env:CGO_ENABLED = '0'
Push-Location $work
try {
    & go get "github.com/genshinsim/gcsim@$revision"
    if ($LASTEXITCODE -ne 0) { throw 'SDK download/resolve failed; no package published.' }
    $module = (& go list -m -json github.com/genshinsim/gcsim | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or $module.Version -notmatch '^v[0-9][0-9A-Za-z.+-]*$') { throw 'Invalid resolved SDK version.' }
    & go run ./cmd/sync-localization -module-dir $module.Dir
    if ($LASTEXITCODE -ne 0) { throw 'Upstream Chinese name synchronization failed; no package published.' }
    $flags = '-s -w -X github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine.Revision=' + $revision + ' -X main.expectedSDKVersion=' + $module.Version
    & go test -p 2 -ldflags $flags ./... -count=1 -timeout 120s
    if ($LASTEXITCODE -ne 0) { throw 'Alignment/constraints/termination regressions failed; old engine remains valid.' }
    foreach ($platform in @('windows','linux')) {
        $folder = Join-Path $work ('package-' + $platform)
        New-Item -ItemType Directory -Path $folder | Out-Null
        $env:GOOS = $platform; $env:GOARCH = 'amd64'; $env:CGO_ENABLED = '0'
        $fileName = if ($platform -eq 'windows') { 'gcsim-bridge.exe' } else { 'gcsim-bridge' }
        $binary = Join-Path $folder $fileName
        & go build -p 2 -buildvcs=false -trimpath -ldflags $flags -o $binary ./cmd/gcsim-bridge
        if ($LASTEXITCODE -ne 0) { throw "Build failed for $platform; active engine not changed." }
        $manifest = [ordered]@{schemaVersion=1;engineRevision=$revision;adapterVersion='1';sdkVersion=$module.Version;platform=$platform;architecture='amd64';executable=$fileName;sha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant();source='https://github.com/hcy0317/better-genshin-impact';regression='go-test-alignment-constraints-termination'}
        [IO.File]::WriteAllText((Join-Path $folder 'manifest.json'),($manifest | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\..\LICENSE') -Destination (Join-Path $folder 'LICENSE')
        Copy-Item -LiteralPath (Join-Path $module.Dir 'LICENSE') -Destination (Join-Path $folder 'GCSIM-LICENSE')
        $zip = Join-Path $destination ('gcsim-bridge-' + $platform + '-amd64.zip')
        if (Test-Path -LiteralPath $zip) { throw "Output already exists; choose a new version output directory: $zip" }
        Compress-Archive -LiteralPath @($binary,(Join-Path $folder 'manifest.json'),(Join-Path $folder 'LICENSE'),(Join-Path $folder 'GCSIM-LICENSE')) -DestinationPath $zip
        Get-Item -LiteralPath $zip | Select-Object FullName,Length
    }
} finally { Pop-Location }
Write-Output "Verified build copy retained at $work. No active installation was replaced."
