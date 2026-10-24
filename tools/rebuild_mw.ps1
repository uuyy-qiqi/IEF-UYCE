# Rebuild MainWindow.xaml from the damaged copy, structure-aware.
#
# The PowerShell splice left three kinds of text in the file:
#   1. head  - pre-existing UTF-8 content that Get-Content decoded as cp936
#   2. block - a freshly inserted clean block (LF-only line endings, one element)
#   3. tail  - more pre-existing content, same damage as head
# plus a leftover fragment (old Grid header) right before the block.
#
# Damage model: disk = utf8( cp936_decode( original_utf8 ) )
# Inverse:      text = utf8_decode( cp936_encode( utf8_decode(disk) ) )
#
# ASCII-only for the same reason as the other scripts.

param(
    [Parameter(Mandatory=$true)][string]$In,
    [Parameter(Mandatory=$true)][string]$Out
)

$cp936 = [System.Text.Encoding]::GetEncoding(936)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$t = [System.IO.File]::ReadAllText($In, [System.Text.Encoding]::UTF8)

# --- locate the clean block -------------------------------------------------
$blockStartMarker = '<!-- ' + [char]0x2550 + [char]0x2550 + [char]0x2550 + [char]0x2550 + ' ' + [char]0x542F + [char]0x52A8 + [char]0x9875 + ' ' + [char]0x2550 + [char]0x2550 + [char]0x2550 + [char]0x2550 + ' -->'
$i0 = $t.IndexOf($blockStartMarker)
if ($i0 -lt 0) { Write-Host "FATAL: block start marker not found"; exit 1 }

# the whole block is a single element, so it ends at the first CRLF after it
$i1 = $t.IndexOf("`r`n", $i0)
if ($i1 -lt 0) { $i1 = $t.Length }

$head = $t.Substring(0, $i0)
$block = $t.Substring($i0, $i1 - $i0)
$tail = $t.Substring($i1)

Write-Host "head=$($head.Length) block=$($block.Length) tail=$($tail.Length)"

# --- drop the leftover fragment in head -------------------------------------
$gridAnchor = '<Grid x:Name="PageLaunch" Visibility="Visible">'
$gi = $head.IndexOf($gridAnchor)
if ($gi -ge 0) {
    $lineStart = $head.LastIndexOf("`n", $gi) + 1
    # walk back one more line: the mangled "start page" comment above it
    $prevLineStart = $head.LastIndexOf("`n", [Math]::Max($lineStart - 2, 0)) + 1
    $rowMarker = '<RowDefinition Height="*"/>'
    $ri = $head.IndexOf($rowMarker, $gi)
    $rowEnd = $head.IndexOf("`n", $ri) + 1
    Write-Host "dropping fragment: $($rowEnd - $prevLineStart) chars"
    $head = $head.Substring(0, $prevLineStart) + $head.Substring($rowEnd)
}

# --- transform helper -------------------------------------------------------
function Convert-Mojibake([string]$s) {
    $sb = New-Object System.Text.StringBuilder
    $lines = $s -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $hasHigh = $false
        foreach ($ch in $line.ToCharArray()) { if ([int]$ch -gt 127) { $hasHigh = $true; break } }
        if ($hasHigh) {
            $line = [System.Text.Encoding]::UTF8.GetString($cp936.GetBytes($line))
        }
        if ($i -gt 0) { [void]$sb.Append("`n") }
        [void]$sb.Append($line)
    }
    return $sb.ToString()
}

$head = Convert-Mojibake $head
$tail = Convert-Mojibake $tail

# normalise the block to CRLF so the file has uniform line endings
$block = $block -replace "`r`n", "`n"
$block = $block -replace "`n", "`r`n"

$result = $head + $block + $tail
[System.IO.File]::WriteAllText($Out, $result, $utf8NoBom)
Write-Host "written: $Out ($($result.Length) chars, CRLF=$(([regex]::Matches($result, "`r`n")).Count), LF=$(([regex]::Matches($result, "`n")).Count))"