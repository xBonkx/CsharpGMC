# Maintained entry point replacing the unavailable historical _tools/audit_snap.ps1.
# Requires a real foreground Windows window on a synthetic Pilot fixture.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$TargetPid,
    [Parameter(Mandatory)][ValidateSet("Python","CSharp")][string]$Client,
    [Parameter(Mandatory)][string]$Fixture,
    [Parameter(Mandatory)][ValidateSet("1366x768","1920x1080","2560x1440")][string]$Resolution,
    [Parameter(Mandatory)][ValidateSet(96,120,144)][int]$Dpi,
    [Parameter(Mandatory)][string]$Out,
    [ValidateRange(0,30)][int]$Delay = 5,
    [string]$Python = "python"
)
$ErrorActionPreference = "Stop"
& $Python (Join-Path $PSScriptRoot "visual_capture.py") capture --pid $TargetPid --client $Client --fixture $Fixture --resolution $Resolution --dpi $Dpi --out $Out --delay $Delay
if ($LASTEXITCODE -ne 0) { throw "Visual capture failed ($LASTEXITCODE)." }
