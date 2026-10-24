# List every line that still contains a U+FFFD or a suspicious lone '?' after
# the mojibake reversal, so the damaged spots can be corrected by hand.

param(
    [Parameter(Mandatory=$true)][string]$Path,
    [Parameter(Mandatory=$true)][string]$Report
)

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
$lines = $text -split "`r`n"

$rep = New-Object System.Collections.Generic.List[string]
$bad = 0
$fffd = 0

for ($i = 0; $i -lt $lines.Count; $i++) {
    $l = $lines[$i]
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