# Быстродействие открытия документа (22.11.2026, живые замеры)

Анализ задержки «клик по дереву → данные в гриде». Дополнение к [PERFORMANCE_SQLSERVER_20261121_2.md](PERFORMANCE_SQLSERVER_20261121_2.md) (там — оптимизация закупок) и [REMOTE_SQL_BENCHMARK.md](REMOTE_SQL_BENCHMARK.md) (методика). WPF-замеры открытия здесь проведены впервые — в таблице «ручной приёмки» бенчмарка они раньше стояли «Не проводилось».

## Условия прогона

- Приложение `GMC-Smeta-Next` (Debug, main `b50b5ee`), SQL Server `GmcSmetaPilot_Cutover_20261120_160840` на `tcp:192.168.101.11` (LAN).
- Тестовые документы «ДНС 1.3»: противодымная (150 поз.) и общеобменная (2829 строк, contract 13 = 2672 позиции).

## Замеры

### Живой WPF-замер: клик по документу в дереве → статус «Смета загружена»

`_tools/audit_open_timing.ps1` (UIA-поллинг элемента `StatusText`, шаг 15 мс; окно/страница появляется сразу — по дизайну, замеряется именно фаза данных):

| n=6 переключений | медиана | min | max |
|---|---|---|---|
| открытие | **~500 мс** | 119 | 894 |

Разброс — сетевой джиттер Wi-Fi/LAN.

### SQL-фаза: `RemoteSqlBench --runs 30` (read-only, без WPF)

| Кейс | строк | медиана | p95 |
|---|---|---|---|
| `sql_roundtrip_select1` | 1 | 2.28 мс | 3.44 мс |
| `sample_300` | 300 | 5.96 мс | 12.4 мс |
| `sample_2500` | 2500 | 24.3 мс | 76 мс |
| `full_estimate_refresh` (`EstimateReader.Load`) | 2829 | **152 мс** | **550 мс** |
| `procurement_load` (`BuyRows`) | 2672 | 56 мс | 127 мс |
| `switch_contract` | 2829 | 142 мс | 160 мс |

`full_estimate_refresh` **не включает** undo-снапшот — реальная фаза данных при открытии ещё тяжелее (см. ниже).

## Путь открытия (что выполняется при каждом открытии)

`MainWindow.OpenEstimate` (`MainWindow.xaml.cs:996`) → `new EstimateView(...)` → конструктор **без сетевого I/O** (комментарий в `EstimateView.xaml.cs:65`) → `_ = ReloadDocumentAsync()` (fire-and-forget, `EstimateView.xaml.cs:559`), в `Task.Run` на одном соединении:

1. `SqlServerDatabase.Open` — пул соединений, **но каждый раз запрос `schema_version=28`** (`SqlServerDatabase.cs:29`) — 1 RTT;
2. `Revision` ×2 — 2 RTT;
3. `EstimateReader.Load` (`GmcSmeta.Core/Domain/Estimate.cs:115`) — 5 последовательных запросов: `LoadHead`, `TagRules.Load`, `LoadSystems`, `LoadPositions`, `Totals.ReadOne`;
4. **`SqlServerUndo.Snapshot` (`SqlServerUndo.cs:63`) — 11 запросов `SELECT *` из 10 таблиц**: contract, systems, positions, **procurement целиком**, movements, acts, act_lines, shipment_lines, smr_costs, **spec_revisions целиком**. Нужен только как Ctrl+Z-базлайн (`MainWindow.Undo?.CheckpointSnapshot`);
5. если ревизия изменилась между `before`/`after` — весь цикл повторяется (attempt < 2).

Итого **~19 последовательных RTT** на открытие; при RTT 2.3 мс одни только trip'ы ≈ 45 мс, плюс перенос payload'а снапшота. Проверка «изм.» (`CheckSpecNews`, `EstimateView.SpecUpdate.cs:22`) работает по уже загруженному снапшоту и SQL не делает — но полный дамп `spec_revisions` едет в снапшоте ради одного `MAX(id)`.

## Проверенные гипотезы

| Гипотеза | Вердикт |
|---|---|
| «Таблица рендерится целиком, а не видимая часть» | **Не подтвердилась.** `EstimateView.xaml:601`: `EnableRowVirtualization=True`, `VirtualizationMode=Recycling`, `ScrollUnit=Pixel`, родительская строка `Grid.Row=4` = `Height="*"` (ограниченная высота). Рендерятся только видимые строки — от размера документа отрисовка не зависит. |
| «Часть подгрузок должна уходить в фон, а не в момент открытия» | **Подтвердилась.** Данные уже грузятся асинхронно (окно появляется раньше контента), но в фазу загрузки зря включён тяжёлый undo-снапшот и полный дамп `spec_revisions` — их можно отложить (см. P1). |

## Приоритизированный план

| # | Изменение | Где | Эффект |
|---|---|---|---|
| P1 | **Ленивый undo-снапшот**: не снапшотить при загрузке; снапшот брать при первой локальной правке / использовании Undo (фабрика уже есть в `MainWindow.xaml.cs:63`). Проверку «изм.» свести к лёгкому `SELECT MAX(id), title, user FROM spec_revisions WHERE contract_id=@id` вместо полного дампа | `EstimateView.xaml.cs:580`, `EstimateView.SpecUpdate.cs` | **−30-50% фазы данных** на каждое открытие и каждую фоновую перезагрузку; дешевле retry-цикл |
| P2 | **Батчирование**: 5 запросов `Load` → один батч с `NextResult()`; то же для снапшота | `Estimate.cs:115`, `SqlServerUndo.cs:63` | 19 RTT → 3-4; основной выигрыш на джиттере (p95 550 мс) |
| P3 | **Кэш последних 2-3 документов** + валидация по `meta.rev` (1 запрос) | `MainWindow.OpenEstimate` | обратное переключение A→B→A ≈ мгновенно |
| P4 | **Кэш `schema_version`** по строке подключения (статический флаг) | `SqlServerDatabase.cs:29` | −1 RTT на каждое открытие/перезагрузку |

Ожидаемый результат: медиана открытия ~500 мс → **~120-150 мс** (P1+P2), обратное переключение → **<50 мс** (P3). Все изменения — только в SELECT-фазе, запись не затрагивается.

## Как проверить после правок

```powershell
# SQL-фаза (медиана/p95, 30 прогонов)
dotnet run --project csharp/tools/RemoteSqlBench/RemoteSqlBench.csproj -- --runs 30

# Живой WPF-замер (оба приложения запущены, документы открыты)
powershell -File _tools\audit_open_timing.ps1 -Pairs 3
```

Контроль: медиана живого открытия и p95 `full_estimate_refresh` снижаются, значения колонок и ревизии не меняются (тесты `dotnet test csharp/GmcSmeta.slnx` зелёные).
