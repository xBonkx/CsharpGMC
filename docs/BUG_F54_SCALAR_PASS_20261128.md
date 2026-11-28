# Кодовый проход: BUG-F54.01–05 + SQL-режим харнесса — 28.11.2026 (zcode-sess-f54x)

База: `37dff39f` + бронь `435081d7`. Окружение: Windows 10 x64, dotnet SDK 10.0.401, SQL Server `lpc:.`, демо-база `GmcSmetaPilot_Test_Agreement_Demo_444755e8af17` (только чтение).

## Итог

| ID | Тип | Статус | Ключевое evidence |
|---|---|---|---|
| BUG-F54.01 | C | CODE READY | 0 оставшихся `Convert.To*`/unbox-кастов на `ExecuteScalar` в src (rg-проверка); NULL больше не превращается в 0 (applock `SqlServerLetters.cs`, `SqlServerMetalLegacyImport.cs`, `SqlServerPeopleAccess.cs`); OUTPUT-ids/lock/COUNT/CASE — `SqlInteger.RequireInt64/RequireInt32`, CASE-флаги — `SqlFlag.Require` |
| BUG-F54.02 | C | CODE READY | nullable-ветви сохранены явно (`?? contractId`, `?? 0L`, `?? 1L` — тест `NullableScalarBranchesStayExplicit`); BIT-флаги NOT NULL по схеме демо-БД, reader-BIT чтения классифицированы безопасными; денежные преобразования не тронуты (60 ToDecimal/GetDecimal сайтов без изменений) |
| BUG-F54.03 | C | CODE READY | Domain: `Documents.cs` — 7 scalar-COUNT сайтов строго; смета/финансы/дерево/ссылки — strict-политика закреплена тестами `SqlIntegerTests.SharedReadersUseStrictIntegerPolicy` (Estimate, EstimateReader.Cards, Finance, Finance.Acts, Totals, Totals.SqlSums, Workspace) |
| BUG-F54.04 | C | CODE READY | App: `ProcurementPane.xaml.cs`, `PtoSqlSource.cs`, `RateTransferDialog.xaml.cs` — строго; `MainWindow.ContractExport.cs`, `CostSummaryPane.xaml.cs` классифицированы (int-граница модели / текст). CLI: `Bench.cs`, `SqlLive.cs` — строго; текстовые скаляры (DB_NAME, вывод) не тронуты. JSON/не-SQL преобразования не менялись |
| BUG-F54.05 | C | CODE READY | Граница Int32: `SqlInteger.RequireInt32` (checked) + уже существующие `SqlInteger.ReadInt32` на границе DTO (`Workspace.cs` DocRow.Id/ObjectId). Тесты: `BigIntIdsArePreservedAndNarrowingRejectsOverflow`, `ShipmentBigIntCountCannotWrapToNegative`, новые `RequiredInt32ScalarRejectsMissingAndForeignValues` (OverflowException при выходе за Int32). 46 SQL-параметров `SqlDbType.Int` — все источники int (order/count/limit/pieces) — compile-time граница. BIGINT-модели по всей программе не обещаются |
| BUG-F54.07 | A | DONE | Живой SQL Pilot (см. ниже) |
| CTX-049.01 | T | DONE | SQL-режим App.Checks доходит до конца (см. ниже). Манифестная часть CTX-048 остаётся в строках CTX-048.01–03 |

## Изменения кода (продакшн)

- 42 файла, +103/−101 строк. Все ~100 scalar-чтений `ExecuteScalar` в src переведены на строгие хелперы `SqlInteger.RequireInt64/RequireInt32`/`SqlFlag.Require` (целые) либо классифицированы текстовыми/денежными без изменений. Классификация по SQL-контракту: COUNT/COUNT_BIG, `OUTPUT INSERTED.id`, `sp_getapplock`, `CASE … THEN 1 ELSE 0 END`, `COALESCE(MAX(...))`, `@@ROWCOUNT`-аналоги — строго; `Convert.ToString` (DB_NAME, smr_mode, вывод CLI) — не тронуты; decimal/money — не тронуты.
- `src/GmcSmeta.Core/Db/SqlInteger.cs` — добавлен `RequireInt32(object?)` (checked-сужение, OverflowException за границей Int32).
- Читатель-конверсии (`r.GetValue(n)`): остаток 241 сайт проверен по схеме демо-БД (INFORMATION_SCHEMA, read-only): флаг-колонки (acts.signed/locked/discount_override, contracts.locked, attachments.is_primary, objects.archived) — NOT NULL; nullable bigint (parent_id, stage_of, company_id, supplier_id, lot_id…) читаются либо через явные null-ветви (`?? 0L`), либо объектным проходом в JSON-dump. `Convert.To*` на reader принимает все CLR-ширины целых без потери — доказанного дефекта нет, точечные исправления только на scalar-классе.
- Файлы ≤500 строк: затронуты только существующие файлы, лимиты не нарушены (максимум затронутого — `EstimateView.xaml.cs` ~1800 строк, было выше до правки; правки харнесса — тестовый файл).

## Новые тесты (tests/GmcSmeta.Core.Tests/SqlScalarIntegerPolicyTests.cs)

- `RequiredInt32ScalarRejectsMissingAndForeignValues` — NULL/DBNull/decimal → InvalidCastException; длин больше Int32 → OverflowException.
- `RemainingExecuteScalarSitesAreStrict` — 35 файлов Db: нет `Convert.To*`/unbox на `ExecuteScalar`, есть строгий хелпер.
- `NullableScalarBranchesStayExplicit` — 8 адресных needle'ов (App, CLI, Domain, Db).

## CTX-049.01 — гонка публикации SQL-режима: корень и исправление

Симптом (воспроизведён: таймаут «estimate rows», `_estimate` установлен, `_rows=0`, трейс обрывался на `estimate_publish_begin`). Диагностика временной инструментацией:

1. Трейс/perf-CSV: `estimate_ui_publish` = 3 мс и **нет** дочерних фаз (`estimate_ui_row_reconcile`, `estimate_ui_projection`) → `SetDocument` брошен в первых строках.
2. `InvalidOperationException: Вызывающий поток не может получить доступ к данному объекту` — продолжение `await Task.Run` выполнялось **вне UI-потока**.
3. Причина: харнесс работает **без `Application.Run`** → у главного потока нет `DispatcherSynchronizationContext` → `await` без `ConfigureAwait(false)` захватывает NULL-контекст → продолжение в поток пула → первое обращение к UI падает.
4. Каскадное глотание: `SetStatus` → `SyncTotalsEditAvailability()` тоже падало cross-thread, исключение выходило из `catch` в fire-and-forget task → полная тишина. (Гипотеза предыдущего прохода про `previous?.Cancel()` не подтвердилась — отмены не было.)

Исправление — только харнесс (`tests/GmcSmeta.App.Checks/Program.cs`): установка `DispatcherSynchronizationContext` текущего диспетчера после `CreateApplication()`. Продакшн-код не менялся (в продакшне контекст всегда есть).

Попутные правки харнесса (тестовые, доведение до конца SQL-режима):

- диагностика состояния страницы при таймауте «estimate rows» (reflection, test-only);
- список контролов панели обновлён под текущую XAML (`AddRowButton/AddOptionsButton/CatalogAddButton/BottomAddRowButton`; разделы/перемещение/удаление — контекстное меню, паритет Python; имя `AddSectionButton` в UI не существует);
- фильтр: `PumpUntil` на применение/восстановление (дебаунс `EstimateSearchTimer` 180 мс);
- `CheckSettingsMenu`: пункт масштаба теперь «Размер шрифта · N %» — матч по префиксу.

## Живые прогоны (evidence)

1. `dotnet run --project tests/GmcSmeta.App.Checks -- --demo GmcSmetaPilot_Test_Agreement_Demo_444755e8af17` (env `GMC_SMETA_SQL_CONNECTION`, изоляция demo-профиля): **exit 0**. Ключевые строки:
   - `SQL Server WPF 1366px: 2504 rows`, `SQL Server WPF 1920px: 2504 rows`;
   - фильтр: применение → 0 строк, восстановление → 2504;
   - `SQL Server editor WPF smoke OK. No writes performed.`
   - `Settings menu and font scale OK.`
2. `--offline-check` — **exit 0**, «Offline WPF checks passed (no SQL)».
3. Продакшн-EXE текущей сборки репо (`bin/Debug/net10.0-windows/GMC-Smeta-Next.exe --demo … --smoke-test`): **exit 0**, `smoke-error.txt` не создан.
4. Отсутствие изменений при отказе: `dbo.meta.rev` демо-БД до и после живых прогонов — `demo-initial` (не изменился); запись в базу не выполнялась (демо-БД только читалась).
5. Приёмка: `dotnet build GmcSmeta.slnx` — 0 ошибок; `dotnet test GmcSmeta.slnx` — **3096/3096** (3059 прежних + 37 новых), пропущено 0.

## Остаток (честно)

- Живая SQL-автоматизация покрыла маршрут «workspace → смета → фильтр → меню настроек». GUI-приёмка справочников/ПТО/паспортов/металла/Excel/корзины по отдельности — это строки CTX-049.02/03 и профильные A-пакеты; здесь не заявляется. Строгие типы этих маршрутов закрыты policy-тестами и полным scalar-обходом кода.
- Результаты «по manifest» (CTX-048.01–03) остаются вне этого прохода.
