# Maintained entry point replacing the unavailable historical _tools/audit_parity.ps1.
# Successful metadata comparison is not visual acceptance.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PythonCapture,
    [Parameter(Mandatory)][string]$CSharpCapture,
    [Parameter(Mandatory)][string]$Out,
    [string]$DiffOut,
    [int[]]$Region,
    [string]$Python = "python"
)
$ErrorActionPreference = "Stop"
if (Test-Path -LiteralPath $Out) { throw "Evidence output already exists." }
if ($Region -and (-not $DiffOut -or $Region.Count -ne 4)) { throw "Region requires DiffOut and four integers (X Y W H)." }
$script = Join-Path $PSScriptRoot "visual_capture.py"
if ($DiffOut) {
    $arguments = @("diff", $PythonCapture, $CSharpCapture, "--out", $DiffOut)
    if ($Region) { $arguments += "--region"; $arguments += $Region }
    $result = & $Python $script @arguments
} else {
    $result = & $Python $script pair $PythonCapture $CSharpCapture
}
if ($LASTEXITCODE -ne 0) { throw "Visual pair rejected ($LASTEXITCODE)." }
# CreateNew prevents concurrent overwrite of witnessed evidence.
$full = [System.IO.Path]::GetFullPath($Out)
$stream = [System.IO.File]::Open($full, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
try {
    $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false))
    try { $writer.WriteLine(($result -join [Environment]::NewLine)) }
    finally { $writer.Dispose() }
}
finally { $stream.Dispose() }
$result
