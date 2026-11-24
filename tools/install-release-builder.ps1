# Installs the local Release builder EXE on the Windows desktop.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $PSScriptRoot 'release-builder/GmcSmeta.ReleaseBuilder/GmcSmeta.ReleaseBuilder.csproj'
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
if ([string]::IsNullOrWhiteSpace($desktop)) { throw 'Desktop folder not found.' }
$destination = Join-Path $desktop 'ГМЦ-Смета — собрать релиз.exe'
$work = Join-Path ([IO.Path]::GetTempPath()) ('gmc-builder-' + [guid]::NewGuid().ToString('N'))
try {
    & dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $work --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
    $exe = Join-Path $work 'GmcSmeta.ReleaseBuilder.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Builder EXE not found.' }
    $config = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'GMC-Smeta-ReleaseBuilder'
    New-Item -ItemType Directory -Path $config -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $config 'repo.txt') -Value $repo -Encoding UTF8
    Copy-Item -LiteralPath $exe -Destination $destination -Force
    Write-Host "Готово: $destination" -ForegroundColor Green
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
