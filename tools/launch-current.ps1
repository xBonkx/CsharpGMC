# Build and launch the CURRENT WPF sources, not artifacts/clone-preview (an old binary).
# Right-click -> Run with PowerShell, or run: powershell -ExecutionPolicy Bypass -File .\tools\launch-current.ps1
param([switch]$SqlServer)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Не найден dotnet. Установи .NET 10 SDK (не только Runtime).'
    }
    & dotnet build 'src/GmcSmeta.App/GmcSmeta.App.csproj'
    if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась: проверь ошибки компилятора выше.' }
    $exe = Join-Path $root 'src/GmcSmeta.App/bin/Debug/net10.0-windows/GMC-Smeta-Next.exe'
    if (-not (Test-Path $exe)) { throw "Нет собранного EXE: $exe" }
    if ($SqlServer) {
        if ([string]::IsNullOrWhiteSpace($env:GMC_SMETA_SQL_CONNECTION)) {
            throw 'Для -SqlServer требуется переменная GMC_SMETA_SQL_CONNECTION.'
        }
        Start-Process -FilePath $exe -WorkingDirectory $root -ArgumentList '--sql-server'
    } else {
        Start-Process -FilePath $exe -WorkingDirectory $root
    }
    Write-Host "Запущена новая сборка: $exe" -ForegroundColor Green
} finally {
    Pop-Location
}
