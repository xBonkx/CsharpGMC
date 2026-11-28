#requires -Version 5.1
# Validate manually witnessed GUI+SQL cases without forging PASS.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$RunDirectory, [switch]$RequireFullAcceptance)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$dir = [IO.Path]::GetFullPath($RunDirectory)
$caseFile = Join-Path $dir 'manual-acceptance.csv'
$automaticFile = Join-Path $dir 'automated-results.json'
$template = Join-Path $PSScriptRoot 'scenarios.json'
if (-not (Test-Path -LiteralPath $caseFile) -or -not (Test-Path -LiteralPath $automaticFile)) {
    throw 'Missing local runner output. Run Invoke-LocalAcceptance.ps1 first.'
}
$auto = Get-Content -LiteralPath $automaticFile -Raw -Encoding UTF8 | ConvertFrom-Json
$rows = @(Import-Csv -LiteralPath $caseFile -Encoding UTF8)
$expected = @(Get-Content -LiteralPath $template -Raw -Encoding UTF8 | ConvertFrom-Json)
if ($rows.Count -ne $expected.Count -or $rows.Count -ne 25) {
    throw 'Expected exactly 25 scenario rows. Never truncate the manual matrix.'
}
$ids = @{}
$pass = 0; $fail = 0; $skip = 0
$report = New-Object 'System.Collections.Generic.List[string]'
$report.Add('# GMC P0 #55 — witnessed manual acceptance')
$report.Add('')
$report.Add('Git SHA: ' + $auto.git_sha)
$report.Add('')
$report.Add('| ID | Severity | Status | Actual / SKIP reason | Issue |')
$report.Add('|---|---|---|---|---|')
foreach ($row in $rows) {
    $id = [string]$row.id
    if ($ids.ContainsKey($id)) { throw ('Duplicate scenario ' + $id) }
    $ids[$id] = $true
    $spec = @($expected | Where-Object { $_.id -eq $id })
    if ($spec.Count -ne 1 -or $row.severity -ne $spec[0].severity -or $row.area -ne $spec[0].area) {
        throw ('Unknown or modified test identity: ' + $id)
    }
    if ($row.status -cnotin @('PASS', 'FAIL', 'SKIP')) {
        throw ('Invalid status for ' + $id + ': ' + $row.status)
    }
    if ($row.status -eq 'SKIP') {
        if ([string]::IsNullOrWhiteSpace($row.reason)) { throw ('Missing SKIP reason: ' + $id) }
        $skip++
        $note = [string]$row.reason
    }
    else {
        if ($auto.git_sha -notmatch '^[0-9a-fA-F]{40,64}$' -or $auto.git_uncommitted -ne 'NO') { throw 'Acceptance requires exact clean Git HEAD.' }
        if ([string]::IsNullOrWhiteSpace($row.actual)) { throw ('Missing observed result: ' + $id) }
        $pieces = @(([string]$row.evidence).Split(';') | Where-Object { $_.Trim().Length -gt 0 })
        if ($pieces.Count -lt 2) { throw ('Need at least screenshot and independent SQL readback: ' + $id) }
        $hasImage = $false; $hasSql = $false
        foreach ($piece in $pieces) {
            if ([IO.Path]::IsPathRooted($piece)) { throw ('Evidence must be relative to run directory: ' + $id) }
            $absolute = [IO.Path]::GetFullPath((Join-Path $dir $piece.Trim()))
            if (-not $absolute.StartsWith($dir + [IO.Path]::DirectorySeparatorChar,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw ('Evidence escaped the isolated run directory: ' + $id)
            }
            if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) {
                throw ('Evidence file not found: ' + $absolute)
            }
            if ($piece -match '(?i)\.png$') { $hasImage = $true }
            if ($piece -match '(?i)\.(txt|log)$') { $hasSql = $true }
        }
        if (-not $hasImage -or -not $hasSql) {
            throw ('Need PNG and SQL TXT/LOG proof: ' + $id)
        }
        if ($row.status -eq 'FAIL') {
            if ($row.issue_url -notmatch '^https://github\.com/xBonkx/CsharpGMC/issues/\d+$') {
                throw ('FAIL requires separate defect Issue URL: ' + $id)
            }
            $fail++
        }
        else {
            if ($auto.windows -ne $true) { throw ('GUI PASS requires Windows environment: ' + $id) }
            $pass++
        }
        $note = [string]$row.actual
    }
    $note = ($note -replace '[\r\n\|]', ' ').Trim()
    $report.Add('| ' + $id + ' | ' + $row.severity + ' | ' + $row.status +
        ' | ' + $note + ' | ' + ($row.issue_url -replace '\|','/') + ' |')
}
$report.Insert(4, ('**PASS=' + $pass + ' FAIL=' + $fail + ' SKIP=' + $skip + '**'))
$final = Join-Path $dir 'manual-report.md'
$report | Set-Content -LiteralPath $final -Encoding UTF8
Write-Host ('Validated 25 distinct cases. PASS=' + $pass + ' FAIL=' + $fail + ' SKIP=' + $skip)
if ($skip -gt 0 -or $fail -gt 0) {
    Write-Host 'Full acceptance: NOT ACHIEVED. Do not close issue #55.'
}
Write-Host ('Report: ' + $final)
if ($fail -gt 0) { exit 1 }
if ($RequireFullAcceptance) {
    if ($skip -gt 0) { exit 2 }
    $required = @('git-head-and-status','fixture-generate-verify','fixture-reverify','dotnet-restore','dotnet-build','dotnet-test',
        'wpf-offline','export-offline','procurement-export-offline','contract-export-offline','layout-performance-offline')
    foreach ($id in $required) {
        if (@($auto.results | Where-Object { $_.id -eq $id -and $_.status -eq 'PASS' }).Count -ne 1) { exit 2 }
    }
    $pilotResult = @($auto.results | Where-Object { $_.id -eq 'pilot-e2e' })
    if ($pilotResult.Count -ne 1 -or $pilotResult[0].status -cnotin @('PASS','PARTIAL')) { exit 2 }
    if ($pilotResult[0].status -eq 'PARTIAL') {
        $pilot = Get-Content -LiteralPath (Join-Path $dir 'pilot-e2e\report.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $expectedSkips = @($pilot.scenarios | Where-Object { $_.Status -eq 'SKIP' })
        $limitsFile = Join-Path $dir 'pilot-limitations.csv'
        if (-not (Test-Path -LiteralPath $limitsFile -PathType Leaf)) { exit 2 }
        $limits = @(Import-Csv -LiteralPath $limitsFile -Encoding UTF8)
        if ($pilot.git_sha -ne $auto.git_sha -or $pilot.fail -ne 0 -or
                $pilot.skip -ne $expectedSkips.Count -or $limits.Count -ne $expectedSkips.Count) { exit 2 }
        foreach ($expectedSkip in $expectedSkips) {
            $matching = @($limits | Where-Object { $_.id -ceq $expectedSkip.Scenario })
            if ($matching.Count -ne 1 -or $matching[0].status -cne 'PASS' -or
                    [string]::IsNullOrWhiteSpace($matching[0].actual) -or
                    [string]::IsNullOrWhiteSpace($matching[0].evidence)) { exit 2 }
            $proof = [string]$matching[0].evidence
            if ([IO.Path]::IsPathRooted($proof)) { exit 2 }
            $full = [IO.Path]::GetFullPath((Join-Path $dir $proof))
            if (-not $full.StartsWith($dir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                    -not (Test-Path -LiteralPath $full -PathType Leaf) -or (Get-Item -LiteralPath $full).Length -eq 0) { exit 2 }
        }
        Write-Host 'All synthetic Pilot limitations have separate witnessed evidence.'
    }
}
exit 0
