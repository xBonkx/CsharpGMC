# Build a self-contained offline WPF check launcher and put it on the Desktop.
# Runs the GmcSmeta.App.Checks harness with --offline-check: no SQL, no GitHub,
# no repository needed. Does not kill any app process or touch SQL Server.
[CmdletBinding()]
param([string]$DestinationPath, [switch]$SkipShortcuts, [switch]$SkipVerify)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tests/GmcSmeta.App.Checks/GmcSmeta.App.Checks.csproj'
$appXaml = Join-Path $repoRoot 'src/GmcSmeta.App/App.xaml'
if (-not (Test-Path -LiteralPath $project)) {
    throw "Не найден проект проверок: $project"
}
if (-not (Test-Path -LiteralPath $appXaml)) {
    throw "Не найден App.xaml приложения: $appXaml"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Не найден dotnet — требуется .NET 10 SDK.'
}

# Install layout:
#   <LocalAppData>\GmcSmetaOfflineChecks\app\            published self-contained check host
#   <LocalAppData>\GmcSmetaOfflineChecks\app\src\GmcSmeta.App\App.xaml
#   <LocalAppData>\GmcSmetaOfflineChecks\ГМЦ-Смета — оффлайн проверки.cmd
$installDir = Join-Path $env:LOCALAPPDATA 'GmcSmetaOfflineChecks'
$appDir = Join-Path $installDir 'app'
$hostExe = Join-Path $appDir 'GmcSmeta.App.Checks.exe'
$runnerPath = Join-Path $installDir 'ГМЦ-Смета — оффлайн проверки.cmd'
if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
    $DestinationPath = $runnerPath
}
$DestinationPath = [IO.Path]::GetFullPath($DestinationPath)
if ([IO.Path]::GetExtension($DestinationPath) -ne '.cmd') {
    throw 'Путь назначения должен заканчиваться на .cmd (окно консоли с выводом проверок).'
}

$publishDir = Join-Path ([IO.Path]::GetTempPath()) ('gmc-offline-checks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
try {
    Write-Host '1/4  Собираю автономный offline-лаунчер проверок…' -ForegroundColor Cyan
    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=None `
        --output $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }
    $builtExe = Join-Path $publishDir 'GmcSmeta.App.Checks.exe'
    if (-not (Test-Path -LiteralPath $builtExe)) {
        throw "Не найден собранный оффлайн-лаунчер: $builtExe"
    }

    Write-Host '2/4  Устанавливаю в профиль пользователя…' -ForegroundColor Cyan
    if (Test-Path -LiteralPath $appDir) {
        Remove-Item -LiteralPath $appDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $appDir -Recurse -Force
    if (-not (Test-Path -LiteralPath $hostExe)) {
        throw "Не удалось установить $hostExe"
    }
    # CreateApplication() looks for src/GmcSmeta.App/App.xaml above the exe directory.
    $appXamlCopy = Join-Path $appDir 'src/GmcSmeta.App/App.xaml'
    New-Item -ItemType Directory -Path (Split-Path -Parent $appXamlCopy) -Force | Out-Null
    Copy-Item -LiteralPath $appXaml -Destination $appXamlCopy -Force

    Write-Host '3/4  Пишу консольный запускатор…' -ForegroundColor Cyan
    $runnerDir = Split-Path -Parent $DestinationPath
    if (-not (Test-Path -LiteralPath $runnerDir)) {
        New-Item -ItemType Directory -Path $runnerDir -Force | Out-Null
    }
    $runnerLines = @(
        '@echo off',
        'chcp 65001 >nul',
        'title GMC-Smeta offline WPF checks',
        'cd /d "%~dp0app"',
        '"%~dp0app\GmcSmeta.App.Checks.exe" --offline-check',
        'set "GMC_OFFLINE_EXIT=%ERRORLEVEL%"',
        'echo.',
        'if not "%GMC_OFFLINE_EXIT%"=="0" echo OFFLINE CHECKS FAILED: exit code %GMC_OFFLINE_EXIT%.',
        'if "%GMC_OFFLINE_EXIT%"=="0" echo OFFLINE CHECKS PASSED.',
        'echo Window can be closed.',
        'pause'
    )
    $runnerText = ($runnerLines -join "`r`n") + "`r`n"
    [IO.File]::WriteAllText($DestinationPath, $runnerText, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Запускатор: $DestinationPath"

    if (-not $SkipVerify) {
        Write-Host '     Проверяю установленную сборку вне репозитория…' -ForegroundColor Cyan
        $verifyOutput = & $hostExe --offline-check 2>&1
        $verifyCode = $LASTEXITCODE
        $verifyOutput | ForEach-Object { Write-Host "     $_" }
        if ($verifyCode -ne 0) {
            throw "Установленный оффлайн-лаунчер завершился с кодом $verifyCode"
        }
    }

    if (-not $SkipShortcuts) {
        $shell = $null
        try {
            $shell = New-Object -ComObject WScript.Shell
            $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
            $programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
            $linkName = 'ГМЦ-Смета — оффлайн проверки.lnk'
            foreach ($folder in @($desktop, $programs)) {
                if ([string]::IsNullOrWhiteSpace($folder)) { continue }
                if (-not (Test-Path -LiteralPath $folder)) {
                    New-Item -ItemType Directory -Path $folder -Force | Out-Null
                }
                $linkPath = Join-Path $folder $linkName
                $link = $shell.CreateShortcut($linkPath)
                try {
                    $link.TargetPath = $DestinationPath
                    $link.WorkingDirectory = $runnerDir
                    $link.IconLocation = "$hostExe,0"
                    $link.Description = 'ГМЦ-Смета C# — оффлайн WPF-проверки без SQL'
                    $link.Save()
                    Write-Host "Ярлык: $linkPath"
                } finally {
                    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
                }
            }
        } catch {
            Write-Warning "Лаунчер установлен, но не удалось создать ярлыки: $_"
        } finally {
            if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
        }
    }

    Write-Host '4/4  Готово.' -ForegroundColor Green
    Write-Host "Оффлайн-лаунчер: $DestinationPath"
    Write-Host 'Двойной клик на рабочем столе запускает проверки WPF без SQL и без сети.'
} finally {
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
