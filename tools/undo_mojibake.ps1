# Reverse the PowerShell 5.1 double-encoding damage on source files.
# ASCII-only on purpose: PS 5.1 reads .ps1 as ANSI when there is no BOM.
#
# Damage model:
#   disk bytes = utf8( cp936_decode( original_utf8_bytes ) )
# Inverse:
#   s = utf8.decode(disk) ; orig_bytes = cp936.encode(s) ; text = utf8.decode(orig_bytes)

param(
    [Parameter(Mandatory=$true)][string]$Path,
    [string]$Report = '',
    [switch]$Apply
)

$cp936 = [System.Text.Encoding]::GetEncoding(936)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$raw = [System.IO.File]::ReadAllBytes($Path)
$hasBom = ($raw.Length -ge 3 -and $raw[0] -eq 0xEF -and $raw[1] -eq 0xBB -and $raw[2] -eq 0xBF)
if ($hasBom) {
    $body = New-Object byte[] ($raw.Length - 3)
    [Array]::Copy($raw, 3, $body, 0, $body.Length)
} else {
    $body = $raw
}

$s = [System.Text.Encoding]::UTF8.GetString($body)      # mojibake text as stored
$origBytes = $cp936.GetBytes($s)                         # original utf-8 bytes
$text = [System.Text.Encoding]::UTF8.GetString($origBytes)

$lines = $text -split "`n"
$mojiLines = $s -split "`n"

$rep = @()
$rep += "FILE: $Path"
$rep += "bom_on_disk=$hasBom  lines=$($lines.Count)  mojibake_chars=$($s.Length)"
$rep += "question_marks_in_mojibake=$(([regex]::Matches($s, '\?')).Count)"
$rep += "replacement_char_in_restored=$(([regex]::Matches($text, [char]0xFFFD)).Count)"
$rep += ""
$rep += "--- restored lines 220..250 ---"
for ($i = 219; $i -lt [Math]::Min(250, $lines.Count); $i++) {
    $rep += ("L{0,-5} {1}" -f ($i + 1), $lines[$i].TrimEnd())
}
$rep += ""
$rep += "--- first 12 restored lines ---"
for ($i = 0; $i -lt [Math]::Min(12, $lines.Count); $i++) {
    $rep += ("L{0,-5} {1}" -f ($i + 1), $lines[$i].TrimEnd())
}

if ($Apply) {
    [System.IO.File]::WriteAllText($Path, $text, $utf8NoBom)
    $rep += ""
    $rep += "APPLIED: wrote BOM-less UTF-8, $($text.Length) chars"
}

if ($Report -ne '') {
    [System.IO.File]::WriteAllText($Report, ($rep -join "`r`n"), $utf8NoBom)
    Write-Host "report -> $Report"
} else {
    $rep | ForEach-Object { Write-Host $_ }
}