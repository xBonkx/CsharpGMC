# Fail-closed release gate; does not connect to SQL Server.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$RepositoryRoot)
$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src/GmcSmeta.Core/Db/SqlSchemaCompatibility.cs') -Raw
$match = [regex]::Match($source, 'public\s+const\s+int\s+WritableSchemaVersion\s*=\s*(\d+)\s*;')
if (-not $match.Success) { throw 'Не найден WritableSchemaVersion. Выпуск заблокирован.' }
$target = [int]$match.Groups[1].Value
$approvedRaw = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'approved-schema.txt') -Raw).Trim()
if ($approvedRaw -notmatch '^\d+$') { throw 'Некорректная утверждённая версия SQL-схемы.' }
$approved = [int]$approvedRaw
if ($target -ne $approved) {
    throw "SQL-схема $approved -> $target: выпуск заблокирован. Нужен утверждённый SQL-мигратор с backup/verify и подтверждённая приёмка. Не повышайте approved-schema.txt ради обхода проверки."
}
Write-Host "SQL-схема $target: миграция не требуется, БД не затрагивается."
return $target
