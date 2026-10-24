# Apply a line-replacement table to a UTF-8 file.
# Table format: <line-number>|<full replacement line>
# ASCII-only script source.

param(
    [Parameter(Mandatory=$true)][string]$Path,
    [Parameter(Mandatory=$true)][string]$Table
)

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
$lines = $text -split "`r`n"
$table = [System.IO.File]::ReadAllLines($Table, [System.Text.Encoding]::UTF8)

$applied = 0
foreach ($row in $table) {
    if ($row.Trim().Length -eq 0) { continue }
    $bar = $row.IndexOf('|')
    if ($bar -lt 1) { Write-Host "skip bad row: $row"; continue }
    $n = [int]$row.Substring(0, $bar)
    $val = $row.Substring($bar + 1)
    if ($n -lt 1 -or $n -gt $lines.Count) { Write-Host "skip out of range: $n"; continue }
    $lines[$n - 1] = $val
    $applied++
}

[System.IO.File]::WriteAllText($Path, ($lines -join "`r`n"), $utf8NoBom)
Write-Host "applied $applied row(s) -> $Path"