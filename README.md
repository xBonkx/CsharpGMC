# ГМЦ-Смета

Настольное приложение для сметных расчётов, договоров, актов КС-2/КС-3, счетов,
закупок, писем, паспортов и отчётов.

- Платформа: Windows, .NET 10, WPF.
- Хранение данных: Microsoft SQL Server.
- Язык: C#.

## Требования

- Windows 10/11.
- .NET 10 SDK — для сборки из исходников.
- Microsoft SQL Server — для работы с данными.

## Сборка

```powershell
dotnet build GmcSmeta.slnx -c Release
```

Или двойным щелчком по `BUILD_GMC_SMETA.bat`. Скрипт собирает решение и лаунчер,
не изменяя Git и SQL Server.

Готовый EXE:

```
src\GmcSmeta.App\bin\Release\net10.0-windows\GMC-Smeta-Next.exe
```

### Кнопка выпуска Release на рабочем столе

Из корня репозитория один раз выполните:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\install-release-builder.ps1
```

На рабочем столе появится EXE «ГМЦ-Смета — собрать релиз». Нажмите
«Собрать и выпустить релиз»: проверка чистого main, fetch и fast-forward,
автоматический следующий patch-номер из тегов и RELEASE_VERSION, сборка,
обязательные Core-тесты (без локальной эталонной БД), упаковка SC/FX,
лаунчер и SHA-256, загрузка в черновик, проверка вложений и публикация.
Токен берётся из переменных среды, gh auth token или git credential manager.
Старые релизы не перезаписываются. Незакоммиченные изменения не удаляются.
Рабочая SQL БД не используется. Автоматический workflow по push тегов
отключён во избежание гонки с локальным издателем; ручной запуск workflow
и запуск по изменению RELEASE_VERSION сохраняются.

Приёмка: Windows publish, запуск EXE, чистый/грязный main, отсутствие SDK/токена,
ошибка тестов, прерывание загрузки, совпадение тега, полный выпуск и проверка
автообновления. Реальная Windows и публикационная приёмка открыты.

Миграции SQL Server: [план однокнопочного выпуска с резервным копированием](docs/SQL_MIGRATION_RELEASE_TODO.md). До готовности перехода 28 → 31 изменение WritableSchemaVersion блокирует публикацию.

## Запуск

```powershell
dotnet run --project src/GmcSmeta.App
```

Строка подключения к SQL Server задаётся в диалоге при запуске или через
переменную окружения `GMC_SMETA_SQL_CONNECTION`. При отмене диалога приложение закрывается; для работы необходимо подключение к SQL Server.

Пример для запуска:

```powershell
$env:GMC_SMETA_SQL_CONNECTION = "Server=localhost;Database=GmcSmeta;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;"
```

## Тесты

```powershell
dotnet test tests/GmcSmeta.Core.Tests/GmcSmeta.Core.Tests.csproj -c Release
```

Часть тестов сравнения с эталонными данными пропускается, если локально нет
`db/base.db`. Это ожидаемо: тестовые базы данных в репозиторий не входят.

## Обновление и релизы

`GmcSmeta.Launcher.exe` скачивает последний релиз из GitHub Releases, проверяет
SHA-256 и запускает приложение. При следующих запусках обновление выполняется
в фоне.

Установить лаунчер на рабочий стол:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\install-desktop-launcher.ps1
```

Опубликовать новый релиз:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\publish-release.ps1 -Version 0.1.6
```

Скрипт собирает self-contained и framework-dependent версии, упаковывает архивы,
считает SHA-256 и создаёт релиз с тегом `v0.1.6`.

Репозиторий публичный, поэтому для скачивания релизов токен GitHub не нужен.
Если репозиторий снова станет приватным, лаунчер возьмёт токен из
`%APPDATA%\GMC-Smeta-Next\github-token.txt` или из Git Credential Manager.

## Структура

```
src/GmcSmeta.App      WPF-приложение
src/GmcSmeta.Core     доменная логика и работа с SQL Server
src/GmcSmeta.Cli      консольные команды
tests/                тесты и проверки
tools/                лаунчер, публикация релиза, вспомогательные скрипты
```

## Лицензия

Проект опубликован как есть. Отдельная лицензия не предоставляется; все права
сохранены за автором.
