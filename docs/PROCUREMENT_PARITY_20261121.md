# P0 №1 — Закупки: Python → C#/.NET 10/WPF

**Issue:** [#27](https://github.com/xBonkx/CsharpGMC/issues/27)  
**PR:** [#36](https://github.com/xBonkx/CsharpGMC/pull/36)  
**Ветка:** `feature/procurement-full-parity`  
**База:** `main` @ `4e33467c876eebacfaa68dd0ace9972670a4a4b1`  
**Тип изменений:** отдельные SQL-partial, WPF-диалоги, выгрузки, профильные тесты. Директории PR #26 и F03/F05 соседних задач не менялись.

## Матрица команд

| Команда Python | Имеющееся до PR C# | Что добавлено в PR | Проверка/приёмка |
|---|---|---|---|
| `proc_view` фильтры/сортировки/группы/поиск | готово в `ProcurementPane`, `ProcurementViewPlanner` | Не переписывалось | Существующие core-тесты; ручной UI не подтверждён |
| `set_supplier_selected`, `set_stage` | `SetProc`, массовое редактирование | Проверка contract scope и `locked` до массовых изменений | Core CI; SQL-пилот не выполнен |
| `order_dialog` | массовое назначение статуса и поставщика | `ProcurementOrderDialog`, источник цех/закупка, номер заявки и счёта, дата оплаты; `SetProc` поддерживает `paid_date` | Собрать Windows; проверить ввод и SQL-тест |
| `movement_dialog` | одно количество в prompt на одну позицию | Многострочный WPF-диалог с датой, документом, примечанием, непосредственной выдачей; один Serializable transaction | `ProcurementMovementBatchTests`, `Procurement.SqlChecks` (код) |
| `issue_via_list` | одно-позиционная выдача через ведомость | `IssueViaListBatch`: одна фиксированная ведомость, движения, опционально direct-to-site | SQL-тест фиксированной ведомости; `locked`, `meta.rev` |
| `edit/delete_movement` | удаление независимого движения, защита shipment | редактирование независимого движения, запрет изменения shipment | Core и SQL-тест; UI не подтверждён |
| `supplier_request` | полноценного диалога не было | отбор по *всему договору*, поставщику, только новым, без работы/вне КП/дочерних; объединение и усреднение цены, предпросмотр, нумерация | `ProcurementRequestPlannerTests`, XLSX roundtrip код |
| `to_shop` и `print_shop_order` | bulk стадия частично | стадия `shop_queued`, документ в цех PDF | SQL SetProc и PDF roundtrip код |
| `add_out_of_kp` | частичный контрольный грид | отдельное создание в SQL `out_of_kp=1`, WPF editor, вносится цена закупки | `ProcurementExtraValidationTests`, SQL-тест (код) |
| `delete_extras` | не было безопасного отдельного удаления | защищённое удаление **одной** дополнительной позиции; проверка КС и fixed выдач | SQL-тест (код); bulk-удаление остаётся недостатком |
| `dop_from_extras` | договоры и ДС доступны через `SqlServerWorkspace` | атомарное создание ДС и перенос всех дополнительных корневых позиций/деталей с сохранением position IDs и движений, цена из price history/закупки | SQL-тест (код), ручная сверка реквизитов/сумм нужна |
| `export_proc` Excel | нет паритета | XLSX `Контроль` и лист `Статусы`, цвет, группы, выпуски, формулы | `ProcurementExportChecks`, генерация 2/80/2500 |
| `export_procurement_pdf` | нет паритета | многостраничный PDF с колонками, группами, повторяющимися заголовками | PDF roundtrip; визуальное сравнение отсутствует |
| `export_request` Excel | нет паритета | XLSX заявки с редактированием, итоговыми формулами, сохранением | `ProcurementExportChecks`, 2/80/2500 |
| `export_shop_order_pdf` | нет паритета | PDF-заявка в цех | PDF roundtrip; печать визуально не принята |
| `import_sheet` | C# частичный импорт/контроль | **Не переносился**: требуется отдельная сверка Python-import_sheet и пользовательский тест импорта | Открытый дефицит |

## Защита данных

- Только Microsoft SQL Server; независимый SQLite-backend не добавлялся.
- Write-path в `SqlServerProcurement` использует `RequirePilotWrite`; записи разрешены только в `GmcSmetaPilot_*`. Production-база в ходе работы **не изменялась**.
- Новые движения и ДС создаются в `IsolationLevel.Serializable`, проверяют `meta.rev`, ограничение договора и блокировку. При ошибке до `COMMIT` транзакция откатывается.
- При обрыве связи после отправки `COMMIT` UI сообщает о неопределённом исходе и не повторяет запись автоматически.
- Shipment-linked `out` не редактируются и не удаляются напрямую.
- Таблицу `dbo.positions` и общие модули редактора других задач не меняли.

## Тесты и доступность исполнения

Исходники тестов добавлены:
- `GmcSmeta.Core.Tests/ProcurementMovementBatchTests.cs`
- `GmcSmeta.Core.Tests/ProcurementRequestPlannerTests.cs`
- `GmcSmeta.Core.Tests/ProcurementExtraValidationTests.cs`
- `GmcSmeta.Export.Checks/ProcurementExportChecks.cs` (запускается через существующий Windows KS export CI)
- `GmcSmeta.Procurement.Checks` (автономный ручной Windows запуск)
- `GmcSmeta.Procurement.SqlChecks` (автономный real-SQL тест; создаёт одноразовую `GmcSmetaPilot_ProcurementCI_...`, затем удаляет; база извне не меняется)

Запуск проверок:
```powershell
dotnet build csharp/GmcSmeta.slnx -c Release
dotnet test csharp/tests/GmcSmeta.Core.Tests/GmcSmeta.Core.Tests.csproj -c Release --filter "FullyQualifiedName!~GenericReaderAndSqlPilotTests.SqlServerPilot_RejectsUnsupportedFieldsBeforeNetwork"
dotnet run --project csharp/tests/GmcSmeta.Procurement.Checks/GmcSmeta.Procurement.Checks.csproj -c Release
# ТОЛЬКО на тестовом SQL и при явно заданном GMC_PROCUREMENT_CI_SQL
dotnet run --project csharp/tests/GmcSmeta.Procurement.SqlChecks/GmcSmeta.Procurement.SqlChecks.csproj -c Release
```

**Важное:** доступного интерактивного Windows/SQL-клиента в этом исполнении не было. Результаты CI и логов нужно проверять по PR; файл теста сам по себе **не означает** успешный прогон. Нельзя объявить задачу #27 завершённой, пока Windows build, реальная SQL-схема, 2500 строк и ручная проверка UI не пройдут.

## Оставшиеся требования приёмки

1. Исправить любые ошибки Windows CI, получить полностью зелёный PR; логи GitHub Actions могут быть недоступны из коннектора при ошибке `BlobNotFound`.
2. Запустить `GmcSmeta.Procurement.SqlChecks` на отдельном SQL Server, сравнить с полной реальной схемой пилотной БД, проверить два клиента и 2500 записей.
3. Парное открытие одинакового договора в Python и WPF: массовые движения, статус, выдача и отмена, допзакупка/ДС; сделать UI click-through на 1920×1080 и 1366×768.
4. Сравнить реальные выгрузки Python и C# (XLSX формулы, строки, суммы до копейки, PDF кириллица/поля/переносы); проверить образцы, которые создаёт экспортный CI.
5. Довести импорт контрольного листа, массовое удаление дополнительных позиций и полноценный editable preview для заявки в цех.
6. До приёмки не сливать PR #36 в `main`, Issue #27 не закрывать.
