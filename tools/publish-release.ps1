# Локальная публикация релиза C# в GitHub Releases.
# Нужна, когда GitHub Actions недоступен (лимит/оплата), но .NET 10 SDK есть.
# Ничего не пишет в SQL Server и не меняет рабочее дерево git, кроме создания тега.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,          # 0.1.0
    [string]$Repository = 'xBonkx/CsharpGMC',
    [string]$Notes,
    [switch]$SkipTests,
    [switch]$Draft,
    [switch]$NewReleaseOnly
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
$Version = $tag.TrimStart('v')

function Step([string]$text) { Write-Host $text -ForegroundColor Cyan }
function Fail([string]$text) { Write-Host $text -ForegroundColor Red; exit 1 }

function Get-GitHubToken {
    foreach ($name in 'GMC_SMETA_TOKEN', 'GITHUB_TOKEN', 'GH_TOKEN') {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value.Trim() }
    }
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $ghToken = & gh auth token --hostname github.com 2>$null
        if ($LASTEXITCODE -eq 0 -and $ghToken) { return "$ghToken".Trim() }
    }
    try {
        $input = "protocol=https`nhost=github.com`n`n"
        $output = $input | git credential fill 2>$null
        foreach ($line in ($output -split "`n")) {
            if ($line -match '^password=(.+)$') { return $Matches[1].Trim() }
        }
    } catch { }
    return $null
}

function Invoke-Api {
    param([string]$Method, [string]$Uri, $Body)
    $headers = @{
        Authorization          = "Bearer $script:token"
        'User-Agent'           = 'GMC-Smeta-Release'
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    $parameters = @{ Method = $Method; Uri = $Uri; Headers = $headers; TimeoutSec = 120 }
    if ($null -ne $Body) {
        $parameters.Body = ($Body | ConvertTo-Json -Depth 5)
        $parameters.ContentType = 'application/json; charset=utf-8'
    }
    return Invoke-RestMethod @parameters
}

function Upload-Asset([long]$releaseId, [string]$file) {
    $name = [IO.Path]::GetFileName($file)
    $existing = Invoke-Api -Method Get -Uri "https://api.github.com/repos/$Repository/releases/$releaseId/assets?per_page=100"
    foreach ($asset in @($existing)) {
        if ($asset.name -eq $name) {
            Write-Host "  заменяю вложение $name"
            Invoke-Api -Method Delete -Uri "https://api.github.com/repos/$Repository/releases/assets/$($asset.id)" | Out-Null
        }
    }
    $size = [math]::Round((Get-Item -LiteralPath $file).Length / 1MB, 1)
    Write-Host "  загружаю $name ($size МБ)…"
    $url = "https://uploads.github.com/repos/$Repository/releases/$releaseId/assets?name=$([uri]::EscapeDataString($name))"
    $code = & curl.exe -sS -X POST -H "Authorization: Bearer $script:token" -H 'User-Agent: GMC-Smeta-Release' `
        -H 'Content-Type: application/octet-stream' --data-binary "@$file" -o "$env:TEMP\gmc-upload.json" `
        -w '%{http_code}' $url
    if ($code -ne '201') {
        $detail = Get-Content "$env:TEMP\gmc-upload.json" -Raw -ErrorAction SilentlyContinue
        throw "GitHub не принял $name (HTTP $code): $detail"
    }
}

if (-not (Test-Path (Join-Path $repoRoot 'GmcSmeta.slnx'))) {
    Fail "Не найден репозиторий: $repoRoot"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail 'Не найден dotnet — нужен .NET 10 SDK.'
}
if ($tag -notmatch '^v\d+\.\d+\.\d+$') {
    Fail "Версия должна быть вида 0.1.0 (тег $tag не подходит)."
}

$script:token = Get-GitHubToken
if (-not $script:token) {
    Fail 'Не найден токен GitHub: задайте GMC_SMETA_TOKEN или сохраните учётные данные git для github.com.'
}

$head = (git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}
$out = Join-Path $work 'release'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $out -Force | Out-Null

try {
    Push-Location $repoRoot
    Step "1/6  Сборка решения (Release)…"
    & dotnet build 'GmcSmeta.slnx' -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'Сборка решения не удалась.' }

    if (-not $SkipTests) {
        Step '2/6  Офлайн-тесты (кроме сверок с локальной db\base.db)…'
        $filter = @(
            'FullyQualifiedName!~NavigationParityTests.AllThreeTrees_MatchActualPythonWidgets'
            'FullyQualifiedName!~ActsTests.ActBrowser_MatchesActualPythonRowsSourcesAndFilteredFooter'
            'FullyQualifiedName!~EstimateDisplayTests.DisplayStatistics_MatchPythonOnAll34Documents'
            'FullyQualifiedName!~WorkspaceTests.ObjectCardsAndTotals_MatchActualPythonPage'
            'FullyQualifiedName!~FinanceTests.Finance_MatchesPythonForEveryDocument'
        ) -join '&'
        & dotnet test 'tests\GmcSmeta.Core.Tests\GmcSmeta.Core.Tests.csproj' `
            -c Release --no-build --nologo --filter $filter
        if ($LASTEXITCODE -ne 0) { Fail 'Тесты не прошли — релиз не публикуется.' }
    } else {
        Step '2/6  Тесты пропущены (-SkipTests).'
    }

    Step "3/6  Публикация приложения $Version…"
    & dotnet publish 'src\GmcSmeta.App\GmcSmeta.App.csproj' -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $work 'app-sc') --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'Автономная публикация приложения не удалась.' }
    & dotnet publish 'src\GmcSmeta.App\GmcSmeta.App.csproj' -c Release -r win-x64 --self-contained false `
        -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $work 'app-fx') --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'Публикация приложения для .NET 10 не удалась.' }

    Step '4/6  Публикация запускающего EXE…'
    & dotnet publish 'tools\launcher\GmcSmeta.Launcher\GmcSmeta.Launcher.csproj' -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $work 'launcher') --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'Публикация лаунчера не удалась.' }

    foreach ($dir in 'app-sc', 'app-fx') {
        if (-not (Test-Path (Join-Path $work "$dir\GMC-Smeta-Next.exe"))) {
            Fail "Нет GMC-Smeta-Next.exe в $dir"
        }
    }
    if (-not (Test-Path (Join-Path $work 'launcher\GmcSmeta.Launcher.exe'))) {
        Fail 'Нет GmcSmeta.Launcher.exe'
    }

    & (Join-Path $PSScriptRoot 'verify-brand-icons.ps1') -ExePaths @(
        (Join-Path $work 'app-sc\GMC-Smeta-Next.exe'),
        (Join-Path $work 'app-fx\GMC-Smeta-Next.exe'),
        (Join-Path $work 'launcher\GmcSmeta.Launcher.exe')
    )
    Step '5/6  Упаковка архивов и контрольных сумм…'
    Compress-Archive -Path (Join-Path $work 'app-sc\*') -DestinationPath (Join-Path $out 'GMC-Smeta-Next-win-x64.zip') -CompressionLevel Optimal
    Compress-Archive -Path (Join-Path $work 'app-fx\*') -DestinationPath (Join-Path $out 'GMC-Smeta-Next-win-x64-fx.zip') -CompressionLevel Optimal
    Copy-Item (Join-Path $work 'launcher\GmcSmeta.Launcher.exe') (Join-Path $out 'GmcSmeta.Launcher.exe')
    Set-Content -Path (Join-Path $out 'version.txt') -Value $tag -Encoding utf8
    $sums = Get-ChildItem $out -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }
    Set-Content -Path (Join-Path $out 'SHA256SUMS.txt') -Value $sums -Encoding utf8
    Get-ChildItem $out -File | Select-Object Name, @{n = 'МБ'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
}
finally {
    Pop-Location
}

Step "6/6  Публикация релиза $tag на GitHub…"
if ($NewReleaseOnly) {
    if ((git -C $repoRoot rev-parse HEAD).Trim() -ne $head -or (git -C $repoRoot status --porcelain)) {
        Fail 'Исходники изменились во время сборки. Релиз не опубликован.'
    }
    $body = if ($Notes) { $Notes } else { "ГМЦ-Смета C# $Version. Коммит: $head" }
    # Create an unpublished release first. GitHub creates the tag at target_commitish
    # on publishing. No public release is visible while files are uploading.
    $release = Invoke-Api -Method Post -Uri "https://api.github.com/repos/$Repository/releases" -Body @{
        tag_name = $tag
        target_commitish = $head
        name = "ГМЦ-Смета C# $tag"
        body = $body
        draft = $true
        prerelease = $false
    }
    try {
        foreach ($file in (Get-ChildItem $out -File | Sort-Object Name)) {
            Upload-Asset -releaseId $release.id -file $file.FullName
        }
        $assets = @(Invoke-Api -Method Get -Uri "https://api.github.com/repos/$Repository/releases/$($release.id)/assets?per_page=100")
        foreach ($file in (Get-ChildItem $out -File)) {
            $match = @($assets | Where-Object { $_.name -eq $file.Name -and $_.size -eq $file.Length -and $_.state -eq 'uploaded' })
            if ($match.Count -ne 1) { throw "Не подтверждена загрузка $($file.Name)." }
        }
        $remoteTags = git -C $repoRoot ls-remote --tags origin "refs/tags/$tag"
        if ($LASTEXITCODE -ne 0 -or $remoteTags) { throw "Тег $tag появился во время сборки." }
        if (-not $Draft) {
            $release = Invoke-Api -Method Patch -Uri "https://api.github.com/repos/$Repository/releases/$($release.id)" -Body @{
                draft = $false
                make_latest = 'true'
            }
            if ($release.draft) { throw 'GitHub не подтвердил публикацию.' }
        }
        Write-Host "Готово: $($release.html_url)"
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        exit 0
    } catch {
        Write-Host "Выпуск не подтверждён. Проверьте черновик на GitHub: $($release.html_url)"
        Write-Host "Локальные артефакты: $out"
        throw
    }
}
$existingTag = git -C $repoRoot tag -l $tag
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
if (-not $existingTag) {
    git -C $repoRoot tag -a $tag -m "GMC-Smeta C# $Version" 2>&1 | Out-Null
}
$pushOutput = git -C $repoRoot push origin "refs/tags/${tag}" 2>&1
$pushExit = $LASTEXITCODE
$ErrorActionPreference = $previousEap
if ($pushExit -ne 0) { throw "Не удалось отправить тег ${tag}: $pushOutput" }

$body = if ([string]::IsNullOrWhiteSpace($Notes)) {
    "ГМЦ-Смета C# $Version.`n`nАвтообновление: запустите ГМЦ-Смета.exe на рабочем столе — новая версия скачается сама."
} else { $Notes }

$release = $null
try {
    $release = Invoke-Api -Method Get -Uri "https://api.github.com/repos/$Repository/releases/tags/$tag"
} catch {
    $release = $null
}
if ($null -eq $release) {
    $release = Invoke-Api -Method Post -Uri "https://api.github.com/repos/$Repository/releases" -Body @{
        tag_name         = $tag
        target_commitish = $head
        name             = "ГМЦ-Смета C# $tag"
        body             = $body
        draft            = [bool]$Draft
        prerelease       = $false
        make_latest      = if ($Draft) { 'false' } else { 'true' }
    }
    Write-Host "Создан релиз: $($release.html_url)"
} else {
    Write-Host "Релиз уже существует: $($release.html_url) — обновляю вложения."
    $release = Invoke-Api -Method Patch -Uri "https://api.github.com/repos/$Repository/releases/$($release.id)" -Body @{
        name = "ГМЦ-Смета C# $tag"
        body = $body
    }
}

foreach ($file in (Get-ChildItem $out -File | Sort-Object Name)) {
    Upload-Asset -releaseId $release.id -file $file.FullName
}

$final = Invoke-Api -Method Get -Uri "https://api.github.com/repos/$Repository/releases/$($release.id)"
Write-Host ''
Write-Host "Готово: $($final.html_url)" -ForegroundColor Green
$final.assets | Select-Object name, @{n = 'МБ'; e = { [math]::Round($_.size / 1MB, 1) } } | Format-Table -AutoSize
Write-Host 'Пользователю достаточно запустить EXE на рабочем столе — он скачает эту версию.'
) { Fail 'Не удалось определить HEAD.' }
if ($NewReleaseOnly) {
    if ($Repository -ne 'xBonkx/CsharpGMC') { Fail 'Неверный репозиторий.' }
    if (git -C $repoRoot status --porcelain) { Fail 'Рабочая копия изменена.' }
    if ((git -C $repoRoot branch --show-current).Trim() -ne 'main') { Fail 'Выпуск только из main.' }
    if ((git -C $repoRoot rev-parse origin/main).Trim() -ne $head) { Fail 'HEAD отличается от origin/main.' }
    $remoteTags = git -C $repoRoot ls-remote --tags origin "refs/tags/$tag"
    if ($LASTEXITCODE -ne 0) { Fail 'Не удалось проверить теги GitHub.' }
    if ($remoteTags -or (git -C $repoRoot tag -l $tag)) { Fail "Тег $tag уже существует." }
    # Check for drafts as well as published releases; never overwrite one.
    $page = 1
    do {
        $existing = @(Invoke-Api -Method Get -Uri "https://api.github.com/repos/$Repository/releases?per_page=100&page=$page")
        if ($existing | Where-Object { $_.tag_name -eq $tag }) { Fail "Релиз $tag уже существует (возможно черновик)." }
        $page++
    } while ($existing.Count -eq 100)
}
$work = Join-Path ([IO.Path]::GetTempPath()) ("gmc-release-" + $tag + "-" + [guid]::NewGuid().ToString('N'))
$out = Join-Path $work 'release'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $out -Force | Out-Null

try {
    Push-Location $repoRoot
    Step "1/6  Сборка решения (Release)…"
    & dotnet build 'GmcSmeta.slnx' -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'Сборка решения не удалась.' }

    if (-not $SkipTests) {
        Step '2/6  Офлайн-тесты (кроме сверок с локальной db\base.db)…'
        $filter = @(
            'FullyQualifiedName!~NavigationParityTests.AllThreeTrees_MatchActualPythonWidgets'
            'FullyQualifiedName!~ActsTests.ActBrowser_MatchesActualPythonRowsSourcesAndFilteredFooter'
            'FullyQualifiedName!~EstimateDisplayTests.DisplayStatistics_MatchPythonOnAll34Documents'
            'FullyQualifiedName!~WorkspaceTests.ObjectCardsAndTotals_MatchActualPythonPage'
            'FullyQualifiedName!~FinanceTests.Finance_MatchesPythonForEveryDocument'
        ) -join '&'
        & dotnet test 'tests\GmcSmeta.Core.Tests\GmcSmeta.Core.Tests.csproj' `
            -c Release --no-build --nologo --filter $filter
        if ($LASTEXITCODE -ne 0) { Fail 'Тесты не прошли — релиз не публикуется.' }
    } else {
        Step '2/6  Тесты пропущены (-SkipTests).'
    }

    Step "3/6  Публикация приложения $Version…"
    & dotnet publish 'src\GmcSmeta.App\GmcSmeta.App.csproj' -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $work 'app-sc') --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'Автономная публикация приложения не удалась.' }
    & dotnet publish 'src\GmcSmeta.App\GmcSmeta.App.csproj' -c Release -r win-x64 --self-contained false `
        -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $work 'app-fx') --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'Публикация приложения для .NET 10 не удалась.' }

    Step '4/6  Публикация запускающего EXE…'
    & dotnet publish 'tools\launcher\GmcSmeta.Launcher\GmcSmeta.Launcher.csproj' -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $work 'launcher') --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'Публикация лаунчера не удалась.' }

    foreach ($dir in 'app-sc', 'app-fx') {
        if (-not (Test-Path (Join-Path $work "$dir\GMC-Smeta-Next.exe"))) {
            Fail "Нет GMC-Smeta-Next.exe в $dir"
        }
    }
    if (-not (Test-Path (Join-Path $work 'launcher\GmcSmeta.Launcher.exe'))) {
        Fail 'Нет GmcSmeta.Launcher.exe'
    }

    & (Join-Path $PSScriptRoot 'verify-brand-icons.ps1') -ExePaths @(
        (Join-Path $work 'app-sc\GMC-Smeta-Next.exe'),
        (Join-Path $work 'app-fx\GMC-Smeta-Next.exe'),
        (Join-Path $work 'launcher\GmcSmeta.Launcher.exe')
    )
    Step '5/6  Упаковка архивов и контрольных сумм…'
    Compress-Archive -Path (Join-Path $work 'app-sc\*') -DestinationPath (Join-Path $out 'GMC-Smeta-Next-win-x64.zip') -CompressionLevel Optimal
    Compress-Archive -Path (Join-Path $work 'app-fx\*') -DestinationPath (Join-Path $out 'GMC-Smeta-Next-win-x64-fx.zip') -CompressionLevel Optimal
    Copy-Item (Join-Path $work 'launcher\GmcSmeta.Launcher.exe') (Join-Path $out 'GmcSmeta.Launcher.exe')
    Set-Content -Path (Join-Path $out 'version.txt') -Value $tag -Encoding utf8
    $sums = Get-ChildItem $out -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }
    Set-Content -Path (Join-Path $out 'SHA256SUMS.txt') -Value $sums -Encoding utf8
    Get-ChildItem $out -File | Select-Object Name, @{n = 'МБ'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
}
finally {
    Pop-Location
}

Step "6/6  Публикация релиза $tag на GitHub…"
$head = (git -C $repoRoot rev-parse HEAD).Trim()
$existingTag = git -C $repoRoot tag -l $tag
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
if (-not $existingTag) {
    git -C $repoRoot tag -a $tag -m "GMC-Smeta C# $Version" 2>&1 | Out-Null
}
$pushOutput = git -C $repoRoot push origin "refs/tags/${tag}" 2>&1
$pushExit = $LASTEXITCODE
$ErrorActionPreference = $previousEap
if ($pushExit -ne 0) { throw "Не удалось отправить тег ${tag}: $pushOutput" }

$body = if ([string]::IsNullOrWhiteSpace($Notes)) {
    "ГМЦ-Смета C# $Version.`n`nАвтообновление: запустите ГМЦ-Смета.exe на рабочем столе — новая версия скачается сама."
} else { $Notes }

$release = $null
try {
    $release = Invoke-Api -Method Get -Uri "https://api.github.com/repos/$Repository/releases/tags/$tag"
} catch {
    $release = $null
}
if ($null -eq $release) {
    $release = Invoke-Api -Method Post -Uri "https://api.github.com/repos/$Repository/releases" -Body @{
        tag_name         = $tag
        target_commitish = $head
        name             = "ГМЦ-Смета C# $tag"
        body             = $body
        draft            = [bool]$Draft
        prerelease       = $false
        make_latest      = if ($Draft) { 'false' } else { 'true' }
    }
    Write-Host "Создан релиз: $($release.html_url)"
} else {
    Write-Host "Релиз уже существует: $($release.html_url) — обновляю вложения."
    $release = Invoke-Api -Method Patch -Uri "https://api.github.com/repos/$Repository/releases/$($release.id)" -Body @{
        name = "ГМЦ-Смета C# $tag"
        body = $body
    }
}

foreach ($file in (Get-ChildItem $out -File | Sort-Object Name)) {
    Upload-Asset -releaseId $release.id -file $file.FullName
}

$final = Invoke-Api -Method Get -Uri "https://api.github.com/repos/$Repository/releases/$($release.id)"
Write-Host ''
Write-Host "Готово: $($final.html_url)" -ForegroundColor Green
$final.assets | Select-Object name, @{n = 'МБ'; e = { [math]::Round($_.size / 1MB, 1) } } | Format-Table -AutoSize
Write-Host 'Пользователю достаточно запустить EXE на рабочем столе — он скачает эту версию.'
