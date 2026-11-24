#requires -Version 5.1
<#
P0 #55: local-only recorder. NO SQL mutations. The optional SQL probe is read-only,
uses integrated auth, and requires an exact expected @@SERVERNAME and Pilot DB.
Do not publish its logs until checked for synthetic-only information.
#>
[CmdletBinding()]
param(
    [string]$SqlServer = "",
    [string]$ExpectedSqlServer = "",
    [string]$PilotDatabase = ""
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runName = 'run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$out = Join-Path $PSScriptRoot ('out\' + $runName)
$fixtures = Join-Path $out 'fixtures'
[IO.Directory]::CreateDirectory($out) | Out-Null
$results = New-Object 'System.Collections.Generic.List[object]'
$sha = 'UNAVAILABLE'
$dirty = 'UNKNOWN'
$windows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

function Record-Result([string]$id, [string]$status, [string]$details, [string]$evidence) {
    $script:results.Add([pscustomobject]@{
        id = $id; status = $status; details = $details; evidence = $evidence
    })
}

function Invoke-Recorded([string]$id, [string]$command, [string[]]$arguments) {
    $file = Join-Path $script:out ($id + '.log')
    $exitCode = -1
    $body = ''
    try {
        $body = (& $command @arguments 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
    }
    catch {
        $body = $_.Exception.Message
    }
    [IO.File]::WriteAllText($file, [string]$body, [Text.Encoding]::UTF8)
    $detail = 'exit=' + $exitCode
    if ($id -eq 'dotnet-test') {
        $found = [regex]::Matches($body, '(?i)(Passed|Failed|Skipped):\s*(\d+)')
        $counts = @($found | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() + '=' + $_.Groups[2].Value })
        $detail += '; ' + $(if ($counts.Count) { $counts -join ', ' } else { 'test counts not parsed; see raw log' })
    }
    Record-Result $id $(if ($exitCode -eq 0) { 'PASS' } else { 'FAIL' }) $detail $file
    return ($exitCode -eq 0)
}

$git = Get-Command git -ErrorAction SilentlyContinue
if ($null -eq $git) {
    Record-Result 'git-head-and-status' 'SKIP' 'git missing; working tree not inspected' ''
}
else {
    $shaText = & git -C $repo rev-parse HEAD 2>&1
    $statusCode = $LASTEXITCODE
    if ($statusCode -eq 0) {
        $sha = ([string]$shaText).Trim()
        $statusLines = @(& git -C $repo status --porcelain=v1)
        $dirty = $(if ($statusLines.Count -gt 0) { 'YES (' + $statusLines.Count + ' paths)' } else { 'NO' })
        Record-Result 'git-head-and-status' 'PASS' ('sha=' + $sha + '; uncommitted=' + $dirty + '; no changes made') ''
    }
    else { Record-Result 'git-head-and-status' 'SKIP' 'Not a local Git checkout; connector-only branch can still be reviewed' '' }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    foreach ($case in @('fixture-generate-verify', 'dotnet-restore', 'dotnet-build', 'dotnet-test')) {
        Record-Result $case 'SKIP' 'dotnet is not installed; 0 tests executed' ''
    }
}
else {
    $fixtureProject = Join-Path $repo 'tests\GmcSmeta.Workflow.Acceptance\GmcSmeta.Workflow.Acceptance.csproj'
    $fixtureOk = Invoke-Recorded 'fixture-generate-verify' 'dotnet' @('run', '--project', $fixtureProject, '--', 'generate', $fixtures)
    if ($fixtureOk) {
        $verifyOk = Invoke-Recorded 'fixture-reverify' 'dotnet' @('run', '--project', $fixtureProject, '--', 'verify', $fixtures)
    }
    else { Record-Result 'fixture-reverify' 'SKIP' 'fixture generation failed; cannot verify' '' }

    if ($windows) {
        $solution = Join-Path $repo 'GmcSmeta.slnx'
        $restored = Invoke-Recorded 'dotnet-restore' 'dotnet' @('restore', $solution)
        if ($restored) {
            $built = Invoke-Recorded 'dotnet-build' 'dotnet' @('build', $solution, '-c', 'Release', '--no-restore')
            if ($built) {
                $tests = Join-Path $repo 'tests\GmcSmeta.Core.Tests\GmcSmeta.Core.Tests.csproj'
                $null = Invoke-Recorded 'dotnet-test' 'dotnet' @('test', $tests, '-c', 'Release', '--no-build')
            }
            else { Record-Result 'dotnet-test' 'SKIP' 'Release build failed; 0 tests run by this step' '' }
        }
        else {
            Record-Result 'dotnet-build' 'SKIP' 'restore failed' ''
            Record-Result 'dotnet-test' 'SKIP' 'restore failed; 0 tests executed' ''
        }
    }
    else {
        foreach ($case in @('dotnet-restore', 'dotnet-build', 'dotnet-test')) {
            Record-Result $case 'SKIP' 'Windows WPF target unavailable; 0 solution tests run' ''
        }
    }
}

# Explicit credentials are intentionally unsupported. This is a SELECT-only
# identity probe, not proof that the UI or SQL mutation workflows passed.
if ($SqlServer -eq '' -and $ExpectedSqlServer -eq '' -and $PilotDatabase -eq '') {
    Record-Result 'pilot-identity-readonly' 'SKIP' 'No explicit SQL parameters; SQL not contacted' ''
}
elseif ($SqlServer -eq '' -or $ExpectedSqlServer -eq '' -or
        $PilotDatabase -cnotmatch '^GmcSmetaPilot_[A-Za-z0-9_]+$') {
    Record-Result 'pilot-identity-readonly' 'FAIL' 'Safety gate: require server, expected exact @@SERVERNAME, and GmcSmetaPilot_* DB' ''
}
elseif ($null -eq (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    Record-Result 'pilot-identity-readonly' 'SKIP' 'sqlcmd not found; no SQL connection made' ''
}
else {
    $sql = "SET NOCOUNT ON; SELECT N'QA_IDENTITY|' + CONVERT(nvarchar(256),@@SERVERNAME) + N'|' + DB_NAME();"
    $logPath = Join-Path $out 'pilot-identity-readonly.log'
    try {
        $response = (& sqlcmd -b -l 5 -S $SqlServer -d $PilotDatabase -E -W -h -1 -Q $sql 2>&1 | Out-String)
        [IO.File]::WriteAllText($logPath, $response, [Text.Encoding]::UTF8)
        $match = [regex]::Match($response, '(?m)^\s*QA_IDENTITY\|([^|\r\n]+)\|([^|\r\n]+)\s*$')
        if ($LASTEXITCODE -ne 0 -or -not $match.Success) {
            Record-Result 'pilot-identity-readonly' 'FAIL' 'SQL identity query failed; no writes attempted' $logPath
        }
        else {
            $actualServer = $match.Groups[1].Value.Trim()
            $actualDb = $match.Groups[2].Value.Trim()
            if (-not [string]::Equals($actualServer, $ExpectedSqlServer, [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals($actualDb, $PilotDatabase, [StringComparison]::OrdinalIgnoreCase)) {
                Record-Result 'pilot-identity-readonly' 'FAIL' 'Actual SQL identity differs from explicit expected values' $logPath
            }
            else {
                Record-Result 'pilot-identity-readonly' 'PASS' ('Read-only identity verified: ' + $actualServer + '/' + $actualDb + '; NOT workflow acceptance') $logPath
            }
        }
    }
    catch {
        Record-Result 'pilot-identity-readonly' 'FAIL' ('SQL connectivity failure: ' + $_.Exception.GetType().Name) $logPath
    }
}

$scenarioPath = Join-Path $PSScriptRoot 'scenarios.json'
$manual = @(Get-Content $scenarioPath -Raw -Encoding UTF8 | ConvertFrom-Json | ForEach-Object {
    [pscustomobject]@{
        id = $_.id; area = $_.area; severity = $_.severity
        steps = $_.steps; expected = $_.expected; related_issue = $_.relatedIssue
        status = 'SKIP'; reason = 'No witnessed interactive Windows/SQL run yet'
        actual = ''; evidence = ''; issue_url = ''
    }
})
$manualPath = Join-Path $out 'manual-acceptance.csv'
$manual | Export-Csv -Path $manualPath -NoTypeInformation -Encoding UTF8
Record-Result 'windows-click-through-25-cases' 'SKIP' '0/25 witnessed; complete manual-acceptance.csv only after actual execution' $manualPath
Record-Result 'signed-ds-ks-protection' 'SKIP' 'No synthetic real SQL workflow was executed' ''
Record-Result 'two-client-2500-10000' 'SKIP' 'No two interactive Windows clients or controlled full-schema Pilot session' ''
Record-Result 'xlsx-pdf-docx-golden' 'SKIP' 'No synthetic GUI exports were created or compared' ''

$summary = [pscustomobject]@{
    issue = 'https://github.com/xBonkx/CsharpGMC/issues/55'
    git_sha = $sha; git_uncommitted = $dirty; windows = $windows
    created_at = (Get-Date).ToString('o')
    command = $MyInvocation.Line
    results = @($results)
}
$json = Join-Path $out 'automated-results.json'
$summary | ConvertTo-Json -Depth 8 | Set-Content -Path $json -Encoding UTF8
$lines = New-Object 'System.Collections.Generic.List[string]'
$lines.Add('# P0 #55 local QA evidence')
$lines.Add('')
$lines.Add('- Git HEAD: ' + $sha)
$lines.Add('- Windows: ' + $windows + '; working tree modified: ' + $dirty)
$lines.Add('- Manual cases: 25 SKIP until performed; generated file: manual-acceptance.csv')
$lines.Add('- SQL: this runner cannot approve write acceptance; optional identity SELECT only')
$lines.Add('- All log files must be inspected for synthetic-only data before publishing')
$lines.Add('')
$lines.Add('| Check | Result | Details | Local evidence |')
$lines.Add('|---|---|---|---|')
foreach ($v in $results) {
    $lines.Add('| ' + $v.id + ' | ' + $v.status + ' | ' +
        ($v.details -replace '\|', '/') + ' | ' + ($v.evidence -replace '\|', '/') + ' |')
}
$lines | Set-Content -Path (Join-Path $out 'report.md') -Encoding UTF8
Write-Host ('P0 #55 report: ' + (Join-Path $out 'report.md'))
Write-Host ('PASS=' + @($results | Where-Object status -eq 'PASS').Count +
    ' FAIL=' + @($results | Where-Object status -eq 'FAIL').Count +
    ' SKIP=' + @($results | Where-Object status -eq 'SKIP').Count)
