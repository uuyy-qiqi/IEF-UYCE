# Repair the PowerShell 5.1 double-encoding damage on MainWindow.xaml.
# ASCII-only source (PS 5.1 reads BOM-less .ps1 as ANSI).
#
# Damage model: disk bytes = utf8( cp936_decode( original_utf8_bytes ) )
# Inverse:      text = utf8_decode( cp936_encode( utf8_decode(disk) ) )
#
# The transform is lossy: at each '?' in the mojibake, .NET consumed the trailing
# byte of a CJK char plus the following ASCII byte. Those spots come back as
# U+FFFD / missing characters and are fixed by hand afterwards.
#
# Lines 233..336 (1-based) are a freshly written clean block - kept verbatim.
# Lines 229..232 (1-based) are a leftover fragment - dropped.

param(
    [Parameter(Mandatory=$true)][string]$Path,
    [int]$KeepFrom = 0,
    [int]$KeepTo = -1,
    [int]$DropFrom = 0,
    [int]$DropTo = -1,
    [switch]$Apply
)

$cp936 = [System.Text.Encoding]::GetEncoding(936)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$raw = [System.IO.File]::ReadAllBytes($Path)
if ($raw.Length -ge 3 -and $raw[0] -eq 0xEF -and $raw[1] -eq 0xBB -and $raw[2] -eq 0xBF) {
    $body = $raw[3..($raw.Length - 1)]
} else {
    $body = $raw
}

$s = [System.Text.Encoding]::UTF8.GetString($body)
$lines = $s -split "`r`n"

$out = New-Object System.Collections.Generic.List[string]
$kept = 0; $fixed = 0; $dropped = 0; $ascii = 0

for ($i = 0; $i -lt $lines.Count; $i++) {
    $n = $i + 1                      # 1-based line number
    if ($DropTo -ge $DropFrom -and $n -ge $DropFrom -and $n -le $DropTo) { $dropped++; continue }
    if ($KeepTo -ge $KeepFrom -and $n -ge $KeepFrom -and $n -le $KeepTo) { $out.Add($lines[$i]); $kept++; continue }

    $line = $lines[$i]
    # Pure ASCII cannot have been altered.
    $isAscii = $true
    foreach ($ch in $line.ToCharArray()) { if ([int]$ch -gt 127) { $isAscii = $false; break } }
    if ($isAscii) { $out.Add($line); $ascii++; continue }

    $bytes = $cp936.GetBytes($line)
    $out.Add([System.Text.Encoding]::UTF8.GetString($bytes))
    $fixed++
}

Write-Host "total=$($lines.Count) ascii=$ascii transformed=$fixed kept=$kept dropped=$dropped"

if ($Apply) {
    [System.IO.File]::WriteAllText($Path, ($out -join "`r`n"), $utf8NoBom)
    Write-Host "APPLIED -> $Path"
}