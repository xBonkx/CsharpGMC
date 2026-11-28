# T-проход: локальные тесты — 28.11.2026 (zcode-sess-fd0f)

База: `1cdeb85b` + бронь `f414c332`. Окружение: Windows 10 x64, dotnet SDK 10.0.401, локальный SQL Server (MSSQLSERVER, `lpc:.`), демо-база `GmcSmetaPilot_Test_Agreement_Demo_444755e8af17`, оба тестовых клиента запущены ярлыками.

## AGR-11.01 — DONE (строка удалена из очереди)

- `dotnet build GmcSmeta.slnx` — **0 ошибок** (265 существующих nullable-предупреждений).
- `dotnet test GmcSmeta.slnx` — **3059/3059, пропущено 0** (итог после ремонта, см. ниже).
- Причины SKIP: нет — пропущено 0.

## BUG-F54.06 — DONE (строка удалена из очереди)

Целевые xUnit-тесты integer/flag/scalar входят в полный прогон: 3059/3059 зелёные, включая `SqlServerWorkspaceTests`, `NewAnnexInputTests` (Int64 AgreementId=2147483648, Trim AnnexNumber), ширины/NULL/флаги Core.

## Ремонт 35 source-паттерн регрессий (найдены прогоном AGR-11.01)

Было: 3057 passed / 35 failed. Стало: 3059/3059 (+2 новых теста). Разбор по трём независимым аудитам:

- 33 × TEST_STALE — исходники дрейфанули при сохранении интента (коммиты 32324fdf, 3497de03, 1ced9101, a5828b48, 909b9674, 4727baa2, e4f7b4d5, c2cec105, 680aaa88, 0eaff098, 80b12b10, 0fc090bb, a419c57c, 00999a15, 4cc7ed1b, e7c21c3d, b9348a24, 30c65449, дизайн-ресурсы Theme/LayoutMetrics.xaml, CRLF в рабочих копиях). Тесты обновлены с сохранением интента, needle'ы проверены дословно.
- 2 × CODE_BUG:
  1. `EstimateSummaryPanel.xaml:78,104` — `Trigger IsEnabled=False → Setter Opacity=.5` гасил весь контрол (нарушение дизайн-инварианта DisabledTextContrastSourceTests, внесён cee92166). Заменено на `Foreground="{StaticResource UiDisabledTextBrush}"` (конвенция App.xaml).
  2. `tests/GmcSmeta.App.Checks/GridEditingChecks.cs` — проверка пасты не отзеркаливала продакшн (биндинг колонки без `Mode=TwoWay`; в продакшне все колонки TwoWay).
- Born-broken тест: `HeaderAndSidebarCompletionTests` — `Named()` парсил XAML заново на каждый вызов, `Assert.Same(create.Parent, import.Parent)` не мог пройти никогда. Исправлен разовым парсингом.

## CTX-049.01 — PARTIAL

- Windows build: ✓ 0 ошибок.
- Офлайн UI Automation (`App.Checks --offline-check`): ✓ все офлайн-проверки зелёные, включая GridEditingChecks (double-click/edit guards/paste), VisualAuditChecks, 25 000 строк материалов, живую прокрутку.
- SQL Pilot UI Automation: гвард `--demo`, подключение `lpc:.`, workspace init, дерево навигации — ✓ проверено живьём. Открытие документа в харнессе App.Checks — таймаут «estimate rows»: продолжение публикации молча брошено (поглощённый catch при отменённом токене, трейс обрывается на `estimate_publish_begin`, `estimate_ui_publish` 3 мс, `_estimate` установлен, `_rows`=0). Читатель вне приложения возвращает 2504 строки (probe `LoadSqlAsync`); **штатный smoke продакшн-EXE `--demo <db> --smoke-test` — exit 0**. Дефект локализован в тестовом харнессе (ручной `Show()` + reflection-OpenEstimate), не в приложении; требует доводки.
- Результаты «по manifest»: манифест CTX-048 не существует (CTX-048.01–03 UNVERIFIED) — эта часть заблокирована ими.

## DEMO-07.01 — PARTIAL

- .NET-сборка ✓ (0 ошибок), WPF-EXE продакшн ✓ (v1.11.2-fx, smoke exit 0), штатные тесты ✓ 3059/3059.
- Два ярлыка: `GMC-Smeta CSharp TEST.lnk` → `AppData\Local\GMC-Smeta-Demo\CSharp\...\launcher\apps\1.11.2-fx\GMC-Smeta-Next.exe`, `GMC-Smeta Python TEST.lnk` → `AppData\Local\GMC-Smeta-Demo\builds\20261128-133920-29386125\Python\dist\GMC-Smeta\GMC-Smeta.exe`; оба процесса живы.
- Python-«штатные тесты» в срезе репо отсутствуют (эталон `python/` содержит только снапшот smeta без тестов) — вне этого репо.

## PERF-053.05 — PARTIAL

- Валидаторы producers: `tools/local-qa` unittest — **24/24 OK** (`python -m unittest discover -s tools/local-qa -p "test_*.py"`).
- Producer-тесты SQL/WPF/Python (PERF-053.01–04) — не реализованы (UNVERIFIED), целевые тесты для них отсутствуют; блокер для полного закрытия.

## Инструментальные правки App.Checks (харнесс, не продукт)

- `GridEditingChecks`: колонка пасты `Mode=TwoWay` (зеркало продакшна); контракт записи пасты остаётся в `RunPreviewRangePaste` (PreviewGridPaste.TryApply). Зафиксировано эмпирически: путь WPF `OnPastingCellClipboardContent` → internal automation-value holder (BeginEdit → deferred push → CommitEdit) на .NET 10 в этой конфигурации — тихий no-op (push откатывается коммитом row-редактирования), без исключений.
- `VisualAuditChecks`: две проверки работали на «отрывшемся» дереве без PresentationSource — строки/шаблон DataGrid не реализуются в .NET 10 (`Report row was not realized`, NRE на `DG_ScrollViewer`). Добавлен хост-окно + корректная ширина второй фазы.
- `Program.cs` SQL-режима: гвард-совместимые аргументы `--demo <db>`, изоляция профилей APPDATA/LOCALAPPDATA, выбор документа по максимуму позиций (read-only запрос), `window.Show()` (страница грузится только в `Loaded`), открытие через `MainWindow.OpenEstimate` (дерево навигации объектное, DocRow в тегах больше нет).
- Локальный `Invoke-LocalAcceptance.ps1` не запускался: его SQL-проба ожидает другой профиль подключения; покрытие обеспечено прямыми прогонами.

## Остаток

1. Довести App.Checks SQL-режим до полного прохода (гонка публикации в харнессе).
2. CTX-048.01–03 (манифест) → затем CTX-048.04/049.01-остаток.
3. PERF-053.01–04 (producers) → затем PERF-053.05-остаток; PERF-054 baseline.
4. Python-штатные тесты — отдельный репозиторий эталона.
