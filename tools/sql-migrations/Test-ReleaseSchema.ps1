# Fail-closed release gate; does not connect to SQL Server.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src/GmcSmeta.Core/Db/SqlSchemaCompatibility.cs') -Raw
$match = [regex]::Match($source, 'public\s+const\s+int\s+WritableSchemaVersion\s*=\s*(\d+)\s*;')
if (-not $match.Success) { throw 'WritableSchemaVersion not found.' }
$target = [int]$match.Groups[1].Value
$approvedRaw = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'approved-schema.txt') -Raw).Trim()
if ($approvedRaw -notmatch '^\d+$') { throw 'Invalid approved schema version.' }
$approved = [int]$approvedRaw
if ($target -ne $approved) {
    throw "SQL schema mismatch: approved $approved -> target $target. Run backup/verify migration before changing approved-schema.txt."
}
Write-Host ("SQL schema " + $target + ": approved, matches release gate.")
return $target
