# Приёмка BUG-F01..F25 — живые проверки на SQL Server (zcode-sess-bugf125)

Бронь: BUG-F01..F25 в [ACCEPTANCE_TODO](ACCEPTANCE_TODO.md), база main `193d0a22` + фикс `68dc3b10`. Окружение: Windows 10 x64, dotnet SDK 10.0.401, SQL Server `lpc:.` (WIN-PJILKU9M5U1), демо-БД `GmcSmetaPilot_Test_Agreement_Demo_444755e8af17` (только чтение, `meta.rev` до и после всех прогонов — `9d16eccdfbd04a848ea8a75d4e00139d`), одноразовая синтетическая `GmcSmetaPilot_E2E_f660157d975c` (создана и удалена самим раннером).

## Итог

**25/25 → DONE.** Критерий каждой строки («Windows/SQL Pilot и регрессия по критерию источника») закрыт фактическими прогонами: Release/Debug-сборка без ошибок, полный xUnit-прогон 3160/3162 (2 падения — свежие регрессии параллельных коммитов вне этого scope, см. ниже), живой SQL Pilot E2E 41 PASS / 0 FAIL / 6 SKIP (документированные SKIP — ограничения синтетической фикстуры), SQL-режим App.Checks на демо-БД exit 0 (2504 строки, все маршруты, отказ-сценарии, без записи).

## Найденные дефекты (в забронированном scope)

1. **BUG-F12 (реальный дефикс): `SqlServerProcurement.SetProc` падал на ЛЮБОЙ правке существующей строки закупки.** UPDATE выполнялся при открытом `ExecuteReader` на том же соединении (MARS выключен) → `InvalidOperationException: Connection уже имеет связанный открытый DataReader`. Дефект внесён фиксом F12 и живьём не гонялся (в доке: «xUnit/SQL Pilot/Windows build не запускались»). Исправлено: строка читается целиком в `ExistingProcurement` до закрытия reader, UPDATE/INSERT — после. Подтверждено живым E2E (последовательность Цех→buy→Цех меняет `source` shop→buy→shop, коммит `68dc3b10`).
2. **Фикстура E2E (тестовая инфраструктура):** в синтетической схеме отсутствовали `dbo.spec_revisions` (пробел e188c755 — навигационный кейс падал `Недопустимое имя объекта`), `dbo.attachments`, `dbo.price_history`, `dbo.agreements`, `dbo.agreement_files`, `dbo.access_lists`, `dbo.trash` (нужны дампу корзины). Добавлены по продакшн-схеме; навигационный кейс снова зелёный.
3. **Вне scope (докладывается, не чинилось):** 2 падающих xUnit — `WorkspaceNavigationTests.LegalSearchCaptionPreservesIndependentNumber` (коммит dc143141 «Search independent legal agreements» добавил «(ID 12)» в заголовок, старый тест ждёт прежний формат) и `KsExtraAsyncAndStagePermissionSourceTests.ComputedRestIsNeverEditableEvenIfLegacyLockIsMissing` (коммит f97f09f9 переписал guard `_restDocument?.Id != stageId` в StagesDialog.xaml.cs, контракт теста от 8d03c5dc нарушен). Оба — территория навигации/CTX агентов; Suite красный из-за них, мои 3160 целевых тестов зелёные.

## Прогоны (evidence)

| Проверка | Команда | Результат |
|---|---|---|
| Сборка | `dotnet build GmcSmeta.slnx` | 0 ошибок |
| Полная регрессия | `dotnet test GmcSmeta.slnx` | 3162 всего: 3160 passed, 2 failed (вне scope), 0 skipped |
| Живой SQL Pilot E2E | `dotnet run --project tests/GmcSmeta.Pilot.E2E -c Release` (env `GMC_PILOT_E2E_ADMIN`, своя одноразовая БД) | **41 PASS / 0 FAIL / 6 SKIP** (~18 с, БД удалена раннером) |
| SQL-режим App.Checks | `dotnet run --project tests/GmcSmeta.App.Checks -- --demo GmcSmetaPilot_Test_Agreement_Demo_444755e8af17` | exit 0: WPF 1366/1920×2504 строк, фильтр, смоук редактора, справочники/ПТО/Excel(2500)/корзина/undo/люди/авто/организации + отказ-сценарии; демо-БД не изменена |
| Offline WPF | `--offline-check` | exit 0 |

## Сопоставление ID → evidence

Единое основание: полный xUnit-прогон (все перечисленные тесты зелёные) + живой E2E + SQL-режим App.Checks. Тесты указаны точными именами.

| ID | Критерий (кратко) | Регрессия (xUnit, зелёные) | Живой SQL / WPF | Статус |
|---|---|---|---|---|
| BUG-F01 | Непустые вкладки себестоимости договора/ДС | `CostSummaryPresentationTests.*` (итоги/фильтр) | E2E `batched-cost-summary-financial-parity`: Summary на договоре с данными (не КП), полная сверка сумм; App.Checks SQL: сводка сметы 2504 строк | DONE |
| BUG-F02 | Сворачивание не меняет итоги | `Filter_ProvidesSystemSearchMissingAndFoldingWithoutChangingTotals` | E2E cost summary: transport/extra аккумулируются всегда; сверка TotalCost/TotalSell/Margin | DONE |
| BUG-F03 | Ревизия снимка сводной, конфликт двух клиентов | `PhaseMenuUsesOriginalSnapshotAndRevisionAndSharedWriter`, `MetadataAndEstimateAreReconciledAtTheSameRevision` | E2E `two-real-sql-clients-revision-conflict`, `three-client-cross-document-revision-and-readback`, narrow readback = полный пересчёт | DONE |
| BUG-F04 | Атомарность групповой правки | `BatchRefusesProductionBeforeOpeningConnection`, `BatchValidatesAllInputsBeforeOpeningConnection`, `BatchRefusesDuplicateAndInvalidTargets` | E2E: `SaveSummaryCells` с отсутствующей целью отклоняет ВЕСЬ пакет (rev и значения нетронуты), `mid-transaction-fault-rollback`, `2500-atomic-numeric-paste-and-rollback` | DONE |
| BUG-F05 | Явное подтверждение сохранения транспорта | `DirectHeaderEditsUsePublishedRevisionAndCurrentPage`, `TransferAndExportKeepTheRevisionThatOpenedTheCommand` | E2E unknown-commit + отказ по чужой ревизии; App.Checks: guard'ы подписанных вкладок на демо-БД | DONE |
| BUG-F06 | Ложный успех в Итогах при исключении | `MoneyFieldsSharePythonStyleAndKeepCommitHandlers`, `FooterRefreshShowsDiscountAndDoesNotReserveAnEmptySmrHint` | E2E `unknown-commit-ack-reconciliation-simulation` (ACK-потеря после COMMIT), refused stale writes | DONE |
| BUG-F07 | Детерминированное освобождение соединений | `CancelledReadDoesNotAttemptToUseClosedConnection`, `CancelledMaterialsReadDoesNotAttemptToUseClosedConnection` | E2E `guarded-schema-revalidation-on-pooled-and-fresh-opens`, cancel-blocked-revision-and-reopen | DONE |
| BUG-F08 | Снимок модальных команд КС-2 | `ActLoadKeepsPublishedSheetUntilTheNextSnapshotIsReady`, `QuantityWriteBlocksAnotherEditAndAdvancesAfterReadbackGuard`, `SaveBatchAsync`-контракты (KsCloseSectionsSourceTests) | E2E `signed-acts-prices-and-linked-lines`; App.Checks: signed act tab revision guards на демо-БД | DONE |
| BUG-F09 | Worker не читает WPF-контролы | `ExtraWritesRejectArchivedObjectsAndComputedRestInsideTransaction` | App.Checks SQL-режим: async-редактор extra на демо-БД (KS extra edit) | DONE |
| BUG-F10 | Полное перечитывание после сохранения КС-2 | `NavigationCancelsOnlyOrdinaryReadsAndResumesRequestedAct`, KsActReadLifecycle | E2E `signed-acts-prices-and-linked-lines` + readback-гварды App.Checks (revision/selection) | DONE |
| BUG-F11 | Пустой список закупок отказывает до SQL | `ProcurementReadAndSupplierTests` | **Новый живой кейс**: SetProc с пустым списком — отказ до открытия SQL, поставщик не создан, rev не изменился | DONE |
| BUG-F12 | Повторный выбор поставщика чинит source | `ProcurementReadAndSupplierTests` | **Новый живой кейс + найден и исправлен реальный дефект (см. выше)**: Цех→shop, внешний→buy, повтор Цех→shop | DONE |
| BUG-F13 | Одиночная КС-3 не смешивает суммы группы | `KsGroupReportTests` (CalculateSingle), `KsLegalRequisitesTests` | E2E `public-signed-ks2-and-ks3-figures`, `public-main-ds-signed-ks3-opening`; App.Checks: KS workbook totals / formula parity (Python 0.67.9) | DONE |
| BUG-F14 | Согласованный снимок экспорта КС | `KsContractScopeTests`, `KsReportWindowLifecycleSourceTests` (period/queued read/publication) | App.Checks: repeated live workbook snapshots + failed export recovery на демо-БД; E2E отказ по чужой ревизии | DONE |
| BUG-F15 | Реквизиты КС не получают свежую ревизию | `RequisitesAreNotEditableBeforeSnapshotOrDuringSave`, `DsUsesBaseAgreementDateAndContractorAndKeepsOwnSupplement` | App.Checks KS-маршруты на демо-БД (read-only snapshot discipline) | DONE |
| BUG-F16 | Ctrl+Y сохраняет историю, своя ревизия COMMIT | `UndoManagerTests` | **Новый живой кейс**: undo возвращает baseline, `applied.Revision == meta.rev`, чужой сдвиг → `Moved` без записи | DONE |
| BUG-F17 | Неизвестный Undo-результат защищает закрытие | `UndoManagerTests` | **Новый живой кейс**: guard Apply отказывает при подписанных актах/выдачах (F17-правило неизменяемых документов), отказ не пишет | DONE |
| BUG-F18 | Режим этапов пишет по ревизии снимка | `StagesDialogLifecycleTests`, `StageTableSearchTests` | E2E `stage-public-api-and-reopen`, `stage-linked-price-writers` (stale-отказ, подписанный снимок) | DONE |
| BUG-F19 | Этап-батч блокирует записи после неизвестного COMMIT | `StageTableSearchTests` | E2E `stage-rest-component-allocation-and-overflow`, unknown-commit reconciliation | DONE |
| BUG-F20 | Excel-вставка с подтверждённой ревизией | `ExcelExistingUpdateTests` (8 сценариев), `CardImportTargetSourceTests` | App.Checks: Excel existing 2500 строк + отказ fingerprint (apply отклонён, база не изменена) | DONE |
| BUG-F21 | Замена валидирует все ID текущего договора | `GenericReaderAndSqlPilotTests` (ReplaceRows SQL-контракты) | **Новый живой кейс**: чужой ID в удалении → отказ без записи; 1:1 обновляет строку и возвращает ревизию своего COMMIT | DONE |
| BUG-F22 | Буфер/замена по ревизии снимка | `GenericReaderAndSqlPilotTests`, `SystemCopyTransactionSourceTests` | **Новый живой кейс**: успех обеих веток замены подтверждает committed revision | DONE |
| BUG-F23 | Корзина: снимок списка, отказ по ревизии | `TrashReadLifecycleTests`, `SqlServerTrashRestoreTests` | **Новый живой кейс**: ReadSnapshot до/после, Restore по чужой ревизии отказывает, восстановленный договор пересоздан с разделом, Forget подтверждает ровно 1 строку, повтор/чужая ревизия отказывают | DONE |
| BUG-F24 | Копирование сохраняет формулы и СМР-цену | `GenericReaderAndSqlPilotTests` (CopiedRow) | **Новый живой кейс**: InsertCopiedRows переносит `formulas` и `work_price` в SQL, дети получают верные parent_id | DONE |
| BUG-F25 | OUTPUT пакетной вставки (MERGE) | `SystemCopyTransactionSourceTests` (MERGE-шаблон) | **Новый живой кейс**: вставка с 2 детьми — точное соответствие parent_id/порядку, сирот нет, atomic rollback соседних кейсов | DONE |

Ограничения (честно): двух-клиентские сценарии выполнены на уровне SQL-транзакций E2E (реальные два/three клиента в `two-real-sql-clients-revision-conflict`/`three-client-...`); визуальная приёмка окон остаётся за владельцем (WPF-харнесс App.Checks покрывает загрузку/гварды, но не человеко-просмотр). 6 SKIP E2E — задокументированные границы синтетической фикстуры, не фейковые PASS.
