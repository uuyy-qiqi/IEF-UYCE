# List lines containing U+FFFD after the mojibake reversal, using LF-based
# numbering so it matches the read tool. Also normalises CRLF if asked.

param(
    [Parameter(Mandatory=$true)][string]$Path,
    [Parameter(Mandatory=$true)][string]$Report,
    [switch]$NormalizeCrlf
)

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)

if ($NormalizeCrlf) {
    $text = $text -replace "`r`n", "`n"
    $text = $text -replace "`n", "`r`n"
    [System.IO.File]::WriteAllText($Path, $text, $utf8NoBom)
}

$lines = $text -split "`n"
$rep = New-Object System.Collections.Generic.List[string]
$bad = 0
$fffd = 0

for ($i = 0; $i -lt $lines.Count; $i++) {
    $l = $lines[$i].TrimEnd("`r")
    $nff = ([regex]::Matches($l, [string][char]0xFFFD)).Count
    if ($nff -gt 0) {
        $fffd += $nff
        $bad++
        $rep.Add(("L{0}|{1}" -f ($i + 1), $l))
    }
}

$rep.Insert(0, "damaged_lines=$bad  fffd_total=$fffd  total_lines=$($lines.Count)")
[System.IO.File]::WriteAllLines($Report, $rep, $utf8NoBom)
Write-Host "damaged_lines=$bad fffd_total=$fffd -> $Report"