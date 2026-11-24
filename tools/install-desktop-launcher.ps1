# Rebuild and install the latest launcher after its source changes.
# This script does not kill any app process or touch SQL Server.
[CmdletBinding()]
param([string]$DestinationPath, [switch]$SkipShortcuts)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'launcher/GmcSmeta.Launcher/GmcSmeta.Launcher.csproj'
if (-not (Test-Path -LiteralPath $project)) {
    throw "Не найден проект лаунчера: $project"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Не найден dotnet — требуется .NET 10 SDK.'
}
if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
    $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    if ([string]::IsNullOrWhiteSpace($desktop)) { throw 'Укажи -DestinationPath.' }
    $DestinationPath = Join-Path $desktop 'ГМЦ-Смета (обновить и запустить).exe'
}
$DestinationPath = [IO.Path]::GetFullPath($DestinationPath)
if ([IO.Path]::GetExtension($DestinationPath) -ne '.exe') {
    throw 'Путь назначения должен заканчиваться на .exe, не на .lnk.'
}
$publishDir = Join-Path ([IO.Path]::GetTempPath()) ('gmc-launcher-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
try {
    Write-Host '1/3  Собираю обновлённый лаунчер…' -ForegroundColor Cyan
    # Автономная сборка: EXE запускается даже там, где нет .NET SDK/среды выполнения.
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None --output $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }
    $builtExe = Join-Path $publishDir 'GmcSmeta.Launcher.exe'
    if (-not (Test-Path -LiteralPath $builtExe)) {
        throw "Не найден собранный лаунчер: $builtExe"
    }
    & (Join-Path $PSScriptRoot 'verify-brand-icons.ps1') -ExePaths @($builtExe)
    $destinationDir = Split-Path -Parent $DestinationPath
    if (-not (Test-Path -LiteralPath $destinationDir)) {
        New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
    }
    Write-Host '2/3  Устанавливаю на рабочий стол…' -ForegroundColor Cyan
    if (Test-Path -LiteralPath $DestinationPath) {
        $backup = "$DestinationPath.bak"
        Copy-Item -LiteralPath $DestinationPath -Destination $backup -Force
        Write-Host "Резервная копия: $backup"
    }
    try {
        Copy-Item -LiteralPath $builtExe -Destination $DestinationPath -Force
    } catch {
        throw "Не удалось заменить $DestinationPath. Закрой старый лаунчер и повтори установку. $($_.Exception.Message)"
    }
    # Stable launcher path; do not link directly to a versioned app directory.
    if (-not $SkipShortcuts) {
        $shell = $null
        try {
            $shell = New-Object -ComObject WScript.Shell
            $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
            $programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
            $folders = @($programs)
            if (-not [string]::IsNullOrWhiteSpace($desktop) -and
                -not [string]::Equals($destinationDir.TrimEnd('\'), $desktop.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
                $folders += $desktop
            }
            foreach ($folder in $folders) {
                if ([string]::IsNullOrWhiteSpace($folder)) { continue }
                New-Item -ItemType Directory -Path $folder -Force | Out-Null
                $linkPath = Join-Path $folder 'ГМЦ-Смета C# (обновить и запустить).lnk'
                $link = $shell.CreateShortcut($linkPath)
                try {
                    $link.TargetPath = $DestinationPath
                    $link.WorkingDirectory = $destinationDir
                    $link.IconLocation = "$DestinationPath,0"
                    $link.Description = 'ГМЦ-Смета C# — обновление и запуск'
                    $link.Save()
                    Write-Host "Ярлык: $linkPath"
                } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
            }
            # Explicit graphical update without restarting the running application.
            foreach ($folder in @($programs, $desktop) | Select-Object -Unique) {
                if ([string]::IsNullOrWhiteSpace($folder)) { continue }
                $linkPath = Join-Path $folder 'ГМЦ-Смета C# (проверить обновления).lnk'
                $link = $shell.CreateShortcut($linkPath)
                try {
                    $link.TargetPath = $DestinationPath
                    $link.Arguments = '--update-only'
                    $link.WorkingDirectory = $destinationDir
                    $link.IconLocation = "$DestinationPath,0"
                    $link.Description = 'ГМЦ-Смета C# — графический центр обновлений'
                    $link.Save()
                } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
            }
            # Separate fast shortcut, including when the updating EXE is already on the desktop.
            foreach ($folder in @($programs, $desktop) | Select-Object -Unique) {
                if ([string]::IsNullOrWhiteSpace($folder)) { continue }
                New-Item -ItemType Directory -Path $folder -Force | Out-Null
                $linkPath = Join-Path $folder 'ГМЦ-Смета C# (быстрый запуск).lnk'
                $link = $shell.CreateShortcut($linkPath)
                try {
                    $link.TargetPath = $DestinationPath
                    $link.Arguments = '--no-update'
                    $link.WorkingDirectory = $destinationDir
                    $link.IconLocation = "$DestinationPath,0"
                    $link.Description = 'ГМЦ-Смета C# — запуск установленной версии без GitHub'
                    $link.Save()
                    Write-Host "Ярлык: $linkPath"
                } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
            }
        } catch {
            Write-Warning "EXE установлен, но не удалось создать ярлыки: $_"
        } finally {
            if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
        }
    }
    Write-Host '3/3  Готово.' -ForegroundColor Green
    Write-Host "Новый лаунчер: $DestinationPath"
    Write-Host 'При запуске он скачает последнюю сборку из GitHub Releases и обновит себя сам.'
} finally {
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
