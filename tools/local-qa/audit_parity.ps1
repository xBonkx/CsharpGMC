# Maintained entry point replacing the unavailable historical _tools/audit_parity.ps1.
# Successful metadata comparison is not visual acceptance.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PythonCapture,
    [Parameter(Mandatory)][string]$CSharpCapture,
    [Parameter(Mandatory)][string]$Out,
    [string]$Python = "python"
)
$ErrorActionPreference = "Stop"
if (Test-Path -LiteralPath $Out) { throw "Evidence output already exists." }
$result = & $Python (Join-Path $PSScriptRoot "visual_capture.py") pair $PythonCapture $CSharpCapture
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
