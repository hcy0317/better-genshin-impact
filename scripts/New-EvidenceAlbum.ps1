#requires -Version 7.0
<#
.SYNOPSIS
Build an offline, read-only evidence contact sheet (PNG, JSON/CSV manifest and HTML).
.EXAMPLE
pwsh -File scripts/New-EvidenceAlbum.ps1 -InputRoot C:\evidence -OutputDirectory C:\album
.NOTES
Differences are mean RGB thumbnail differences (0..255), not a business verdict.
No application is started. Input links are skipped; output must be a separate empty directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1, 24)][int]$PageSize = 12
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Assert-NoLinkPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Linked path is not allowed: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
function Encode-Html($Value) { [Net.WebUtility]::HtmlEncode([string]$Value) }
function Get-Text($Value, [int]$Limit = 512) {
    $text = [string]$Value
    if ($text.Length -gt $Limit) { return $text.Substring(0, $Limit) }
    return $text
}
function Get-SafeCsvText($Value) {
    $text = [string]$Value
    if ($text -match '^[\s]*[=+@-]') { return "'$text" }
    return $text
}

$inputPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($InputRoot))
$outputPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
Assert-NoLinkPath $inputPath
Assert-NoLinkPath $outputPath
if (-not [IO.Directory]::Exists($inputPath)) { throw 'InputRoot must be an existing directory.' }
$inputPrefix = $inputPath.TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
$outputPrefix = $outputPath.TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
if ($inputPath.Equals($outputPath, [StringComparison]::OrdinalIgnoreCase) -or
    $outputPath.StartsWith($inputPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $inputPath.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Input and output directories must be separate, not nested.'
}
if (Test-Path -LiteralPath $outputPath) {
    if (-not [IO.Directory]::Exists($outputPath) -or @(Get-ChildItem -LiteralPath $outputPath -Force).Count) {
        throw 'OutputDirectory must be empty; existing artifacts are never overwritten.'
    }
}

# Enumerate explicitly instead of recursive traversal, which may follow junctions.
$pending = [Collections.Generic.Stack[string]]::new()
$pending.Push($inputPath)
$frames = [Collections.Generic.List[object]]::new()
$skippedLinks = 0
$entries = 0
while ($pending.Count) {
    $directory = $pending.Pop()
    Assert-NoLinkPath $directory
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        if (++$entries -gt 200000) { throw 'Input exceeds the 200000-entry offline safety limit; select fewer runs.' }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { $skippedLinks++; continue }
        if ($item.PSIsContainer) { $pending.Push($item.FullName); continue }
        if ($item.Name -notmatch '^evidence-\d+\.json$') { continue }
        $frame = [pscustomobject][ordered]@{
            metadata = [IO.Path]::GetRelativePath($inputPath, $item.FullName)
            run = $item.Directory.Name; request = ''; phase = ''; sequence = 0L
            sourceSession = ''; sourceSequence = 0L; sourceTimestamp = 0L; capturedAt = ''
            windowId = ''; relativeIndex = $null; anchorSequence = $null
            status = 'ok'; error = ''; difference = $null; page = ''
        }
        try {
            if ($item.Length -gt 256KB) { throw 'Metadata exceeds 256 KiB.' }
            $metadata = Get-Content -LiteralPath $item.FullName -Raw | ConvertFrom-Json -AsHashtable
            if ($metadata -isnot [Collections.IDictionary]) { throw 'Metadata must be a JSON object.' }
            $frame.run = if ($metadata.RunId) { Get-Text $metadata.RunId } else { $item.Directory.Name }
            $frame.request = Get-Text $metadata.Request
            $frame.phase = Get-Text $metadata.Phase
            $frame.sequence = [long]$metadata.Sequence
            $source = $metadata.Source
            if ($source -is [Collections.IDictionary]) {
                $frame.sourceSession = Get-Text $source.SessionId
                $frame.sourceSequence = [long]$source.Sequence
                $frame.sourceTimestamp = [long]$source.CapturedTimestamp
                $frame.capturedAt = Get-Text $source.CapturedAt
            }
            if ($metadata.Window -is [Collections.IDictionary]) {
                $frame.windowId = Get-Text $metadata.Window.WindowId
                $frame.relativeIndex = $metadata.Window.RelativeIndex
                $frame.anchorSequence = $metadata.Window.Anchor.Sequence
            }
            $png = [IO.Path]::ChangeExtension($item.FullName, '.png')
            Assert-NoLinkPath $png
            if (-not [IO.File]::Exists($png)) { $frame.status = 'missing-png'; $frame.error = 'Matching PNG is missing.' }
            elseif (-not $frame.sourceSession -or $frame.sourceSession -eq [guid]::Empty.ToString() -or
                $frame.sourceSequence -le 0 -or -not $source.Contains('CapturedTimestamp') -or $frame.sourceTimestamp -lt 0) {
                $frame.status = 'unknown-source'; $frame.error = 'Source stamp is absent or invalid.'
            }
        }
        catch { $frame.status = 'invalid-metadata'; $frame.error = Get-Text $_.Exception.Message }
        $frames.Add($frame)
    }
}
$ordered = @($frames | Sort-Object run, request, phase, sourceSession, sourceTimestamp, sourceSequence, sequence, metadata)
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$font = [Drawing.Font]::new('Segoe UI', 9)
$titleFont = [Drawing.Font]::new('Segoe UI', 12)
$format = [Drawing.StringFormat]::new()
$format.Trimming = [Drawing.StringTrimming]::EllipsisCharacter
$previousSample = $null
$previousGroup = ''
$pages = [Collections.Generic.List[string]]::new()
try {
    for ($offset = 0; $offset -lt $ordered.Count; $offset += $PageSize) {
        $count = [Math]::Min($PageSize, $ordered.Count - $offset)
        $pageName = 'contact-{0:d4}.png' -f ($pages.Count + 1)
        $sheet = [Drawing.Bitmap]::new(960, (36 + 280 * [int][Math]::Ceiling($count / 3.0)))
        $graphics = [Drawing.Graphics]::FromImage($sheet)
        try {
            $graphics.Clear([Drawing.Color]::White)
            $graphics.DrawString("Evidence $($offset + 1)-$($offset + $count) / $($ordered.Count) | difference is not a verdict", $titleFont, [Drawing.Brushes]::Black, 8, 7)
            for ($cell = 0; $cell -lt $count; $cell++) {
                $frame = $ordered[$offset + $cell]
                $frame.page = $pageName
                $x = 320 * ($cell % 3) + 8
                $y = 36 + 280 * [int][Math]::Floor($cell / 3.0)
                $group = @($frame.run, $frame.request, $frame.phase, $frame.sourceSession) | ConvertTo-Json -Compress
                if ($group -ne $previousGroup) { $previousSample = $null }
                $previousGroup = $group
                if ($frame.status -eq 'ok') {
                    $stream = $null; $picture = $null; $sample = $null; $sampleGraphics = $null
                    try {
                        # Only a sibling PNG may be opened; no path from JSON is trusted.
                        $png = [IO.Path]::ChangeExtension((Join-Path $inputPath $frame.metadata), '.png')
                        Assert-NoLinkPath $png
                        $stream = [IO.File]::OpenRead($png)
                        if ($stream.Length -gt 32MB -or $stream.Length -lt 24) { throw 'PNG file size is outside limits.' }
                        $header = [byte[]]::new(24)
                        if ($stream.Read($header, 0, 24) -ne 24 -or [Convert]::ToHexString($header[0..7]) -ne '89504E470D0A1A0A' -or
                            [Text.Encoding]::ASCII.GetString($header, 12, 4) -ne 'IHDR') { throw 'Invalid PNG header.' }
                        $widthBytes = [byte[]]$header[16..19]; [Array]::Reverse($widthBytes)
                        $heightBytes = [byte[]]$header[20..23]; [Array]::Reverse($heightBytes)
                        $width = [BitConverter]::ToUInt32($widthBytes, 0)
                        $height = [BitConverter]::ToUInt32($heightBytes, 0)
                        if ($width -eq 0 -or $height -eq 0 -or $width -gt 8192 -or $height -gt 8192 -or [long]$width * $height -gt 16000000) {
                            throw 'PNG dimensions exceed the bounded decoder limit.'
                        }
                        $stream.Position = 0
                        $picture = [Drawing.Image]::FromStream($stream, $false, $true)
                        $scale = [Math]::Min(304.0 / $picture.Width, 176.0 / $picture.Height)
                        $graphics.DrawImage($picture, [Drawing.Rectangle]::new($x, $y, [int]($picture.Width * $scale), [int]($picture.Height * $scale)))
                        $sample = [Drawing.Bitmap]::new(16, 16)
                        $sampleGraphics = [Drawing.Graphics]::FromImage($sample)
                        $sampleGraphics.DrawImage($picture, 0, 0, 16, 16)
                        $values = [byte[]]::new(768)
                        $sum = 0L
                        for ($sy = 0; $sy -lt 16; $sy++) {
                            for ($sx = 0; $sx -lt 16; $sx++) {
                                $pixel = $sample.GetPixel($sx, $sy)
                                $index = ($sy * 16 + $sx) * 3
                                $values[$index] = $pixel.R; $values[$index + 1] = $pixel.G; $values[$index + 2] = $pixel.B
                                if ($null -ne $previousSample) {
                                    for ($c = 0; $c -lt 3; $c++) { $sum += [Math]::Abs([int]$values[$index + $c] - [int]$previousSample[$index + $c]) }
                                }
                            }
                        }
                        if ($null -ne $previousSample) { $frame.difference = [Math]::Round($sum / 768.0, 2) }
                        $previousSample = $values
                    }
                    catch { $frame.status = 'invalid-image'; $frame.error = Get-Text $_.Exception.Message; $previousSample = $null }
                    finally {
                        if ($sampleGraphics) { $sampleGraphics.Dispose() }
                        if ($sample) { $sample.Dispose() }
                        if ($picture) { $picture.Dispose() }
                        if ($stream) { $stream.Dispose() }
                    }
                }
                else { $previousSample = $null }
                if ($frame.status -ne 'ok') {
                    $graphics.DrawString($frame.status + ': ' + $frame.error, $font, [Drawing.Brushes]::DarkRed,
                        [Drawing.RectangleF]::new($x, $y, 304, 176), $format)
                }
                $label = "$($frame.run)`n$($frame.request) / $($frame.phase)`nsource=$($frame.sourceSession)/$($frame.sourceSequence) t=$($frame.sourceTimestamp)`nframe=$($frame.sequence) offset=$($frame.relativeIndex) diff=$($frame.difference)"
                $graphics.DrawString($label, $font, [Drawing.Brushes]::Black, [Drawing.RectangleF]::new($x, ($y + 180), 304, 96), $format)
            }
            $sheet.Save((Join-Path $outputPath $pageName), [Drawing.Imaging.ImageFormat]::Png)
            $pages.Add($pageName)
        }
        finally { $graphics.Dispose(); $sheet.Dispose() }
    }
}
finally { $format.Dispose(); $titleFont.Dispose(); $font.Dispose() }

$manifest = [ordered]@{ schemaVersion = 1; difference = 'Mean RGB difference of adjacent 16x16 thumbnails in the same run/request/phase/source; not a verdict'; skippedLinks = $skippedLinks; pages = @($pages); frames = $ordered }
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $outputPath 'manifest.json') -Encoding utf8
$ordered | ForEach-Object {
    $row = [ordered]@{}
    foreach ($property in $_.PSObject.Properties) {
        $row[$property.Name] = if ($property.Value -is [string]) { Get-SafeCsvText $property.Value } else { $property.Value }
    }
    [pscustomobject]$row
} |
    Export-Csv -LiteralPath (Join-Path $outputPath 'manifest.csv') -NoTypeInformation -Encoding utf8
$html = [Text.StringBuilder]::new()
[void]$html.Append('<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src ''none''; img-src ''self''; style-src ''unsafe-inline''"><title>Evidence album</title><style>body{font:14px sans-serif;margin:24px;color:#222}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:6px;text-align:left;overflow-wrap:anywhere}img{max-width:100%}</style><h1>帧证据离线成册</h1><p>差异仅为相邻缩略图平均 RGB 差值（0–255），不代表业务成功或失败。输入保持只读；链接不跟随。</p>')
[void]$html.Append("<p>Frames: $($ordered.Count); skipped links: $skippedLinks</p>")
foreach ($page in $pages) { [void]$html.Append("<details><summary>$page</summary><a href=`"$page`"><img src=`"$page`" alt=`"$page`"></a></details>") }
[void]$html.Append('<table><thead><tr><th>Run / request / phase</th><th>Source / time</th><th>Window / offset</th><th>Status / difference</th><th>Metadata / page</th></tr></thead><tbody>')
foreach ($frame in $ordered) {
    [void]$html.Append('<tr><td>' + (Encode-Html "$($frame.run) / $($frame.request) / $($frame.phase)") + '</td><td>' +
        (Encode-Html "$($frame.sourceSession)/$($frame.sourceSequence) t=$($frame.sourceTimestamp) $($frame.capturedAt)") + '</td><td>' +
        (Encode-Html "$($frame.windowId) offset=$($frame.relativeIndex) anchor=$($frame.anchorSequence)") + '</td><td>' +
        (Encode-Html "$($frame.status) diff=$($frame.difference) $($frame.error)") + '</td><td>' + (Encode-Html $frame.metadata) +
        " <a href=`"$($frame.page)`">page</a></td></tr>")
}
[void]$html.Append('</tbody></table></html>')
$html.ToString() | Set-Content -LiteralPath (Join-Path $outputPath 'index.html') -Encoding utf8
[pscustomobject]@{ frames = $ordered.Count; pages = $pages.Count; skippedLinks = $skippedLinks; output = $outputPath }
