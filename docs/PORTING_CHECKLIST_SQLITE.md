# План зачистки SQLite-хвостов (AGENTS.md: удалять маленькими проверяемыми этапами)

Ядро SQL Server использует только провайдер-независимые хелперы: Money,
Catalog.Key, ChangeLog.CurrentUser, TagRules, EstimateStatistics, Workspace,
Navigation, EstimateReader, Totals, FinanceReader (все — DbConnection).
SqliteConnection остаётся в легаси-кластере ниже и удаляется по мере переноса.

| Модуль (SQLite) | Замена SQL Server | Статус |
|---|---|---|
| PositionBulk.cs | SqlServerDatabase.InsertCopiedRows | ✅ удалён (№42) |
| CatalogOperations.cs | SqlServerCatalog | ✅ удалён (№42) |
| Catalog.Key/NormalizeName, ChangeLog.CurrentUser, Documents.ContractTitleText/IsValidDocumentDate | выделены в partial-файлы — SQL-код больше не ссылается на легаси-файлы напрямую | ✅ (№42) |
| Documents.cs (операции) | SqlServerWorkspace/Stages/Acts/Shipments | частично; ContractTitleText/IsValidDocumentDate выделены в Documents.Shared.cs (№42) |
| ChangeLog.cs (Log) | INSERT change_log в каждом SQL-модуле | CurrentUser используется везде; ChangeLog сделан partial (№42) |
| Database/Schema/SchemaData/Migrations/Seeding/RowWriter | не нужны SQL Server-пути | легаси-харнесс тестов |
| PositionEditor(+Apply) | SaveCell/SaveSummaryCell/SaveTotalsField | ✅ по записям; зависимые пересчёты формул — переносится |
| Discount.cs | SqlServerEditorOps.ApplyDiscount | ✅ удалён (№44) |
| EstimateOrder.cs | SqlServerEditorOps.SortPositions (sort) | ✅ удалён (№44); place_positions — при переносе «вставить по месту» |
| PositionsToSections.cs | SqlServerEditorOps.PositionsToSections | ✅ удалён (№44) |
| SystemOperations.cs (add_system/subsection/copy_system) | SqlServerSections.AddSection, SqlServerSystemCopy | ✅ удалён (№49) |
| BulkPrice.cs (SamePriceCount/SetPriceLike) | SqlServerPriceLike | ✅ удалён (№49; в №47 удаление заявлялось ошибочно) |
| Trash.cs (keep) | SqlServerTrash (keep+restore) | restore перенесён и проверен живьём (№48–49); легаси keep — только для старых SQLite-удалений ядра/тестов |
| Acts.cs (SQLite ReadActs) | Finance.ReadActs (DbConnection) | проверить дубли и удалить |
| Area.cs | используется EstimateReader? | проверить |

Живые проверки (№49): база GmcSmetaPilot_Cutover_20261120_160840 на localhost.
Инструмент: `dotnet run --project src/GmcSmeta.Cli -- sql <status|smoke|trash|trash-obj|restore> "<строка>" [id]`.
Найдено и исправлено живьём: DBNull/null в Restore; порядок удаления (act_lines раньше
positions) в DeleteObject/DeleteContract/DeleteStageRows; SUM int → GetInt64 в
DescribeObjectForDelete. Roundtrip корзины проверен на документах (без актов, с актами
и историей цен, с закупками, с формулами «@123») и объектах (простой и с 9 актами).

Тесты: SQLite-харнесс (in-memory) остаётся для расчётного паритета (Totals/
Finance/Estimate/Compare/формулы) — это тесты логики, не хранилища.
