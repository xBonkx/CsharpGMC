# Issue #65 — независимая приёмка неактового экспорта КП/ведомостей

**Статус: PARTIAL / BLOCKED для полной приёмки.** Это отчёт о доступной проверке исходников и новых
синтетических тестах, а **не** сертификат паритета пользовательских документов.

- Issue: https://github.com/xBonkx/CsharpGMC/issues/65
- Исходный `main`: `43e61155ddf0194a70808b50a3b4dbdaa8e12568` (21.11.2026).
  Заданный ранее `d634845706735535d9b02ec44ded69f9835e1771` устарел.
- Ветка: `test/contract-export-golden-acceptance`; PR — см. соответствующий PR этой ветки.
- #54 закрыт как локально подтверждённая пользователем Windows-сборка на **другом SHA**.
  Это не подтверждает сборку, PDF, Excel или SQL этого PR.
- #40 (базовый перенос экспорта) объединён в `main`; исходники не переписывались.
- Доступ был через GitHub connector, локальной рабочей копии и `git status` для пользовательского
  Windows-каталога **нет**. Работа проведена только в новой удалённой ветке.
- Здесь отсутствуют `dotnet`, интерактивная Windows/WPF и SQL Server Pilot. **Ни одна
  команда Release build, test, Excel/PDF runtime или SQL acceptance в данной среде не выполнена.**

## Инвентаризация Python ↔ C# (только неактовые формы)

| Python-эталон (python/sql-server-current/smeta/) | C# в main | Инвентаризация | Необходимое evidence для DONE |
| --- | --- | --- | --- |
| `export_kp.py:export_kp` — 6 колонок, обычное КП/договор/ДС, номер/остаток, системные итоги | `KpExcelForms.Create`, `SqlServerKpRequisites.Read` | CODE PRESENT | Один synthetic и один одобренный пользовательский XLSX; формулы, реквизиты, группы, ДС/остатки, print preview |
| `export_kp.py:export_kp(internal=True)` — закуп/розница/маржа, 9 колонок | `KpExcelForms.Create(internalPrices:true)` | CODE PRESENT | Покопеечная сверка фактической себестоимости, нулей/NULL, всех итогов и margin; `MainWindow.ContractExport` выбирает фактический закуп |
| `export_kp_pos.py:export_kp_pos` — материалы+работы, 8 колонок | `KpExcelForms.Create(positional:true)` | CODE PRESENT | Формулы F/H, ПНР, НДС, вывод подписей и колонки |
| `export_pdf.py:export_kp_pdf` — обычный/внутренний PDF | `KpPdfForms.Create` | CODE PRESENT; **исправлен найденный пропуск** | Poppler text extraction, embedded font/кириллица, итоговые закуп/маржа, визуальный raster-diff |
| `export_pdf.py:export_kp_pos_pdf` — позиционный PDF | `KpPdfForms.Create(positional:true)` | CODE PRESENT | Покопеечные суммы, страницы, подписи, переносы |
| `export_ship.py:export_ship_excel` — выдача, 6 колонок | `ShipmentExcelForms.Create` + `SqlServerShipmentPrintData.Read` | CODE PRESENT | Выдано/получено/примечания, группировка, подписи, Excel печать |
| `export_ship.py:export_ship_pdf` — выдача из HTML/Qt | `ShipmentPdfForms.Create` | CODE PRESENT | Сравнить страницы и текст (не только заголовок PDF) |
| `export_pdf.py:export_procurement_pdf` и `export_shop_order_pdf` | Другой владелец (#64 procurement), не включены в этот PR | OUT OF SCOPE | Отдельная приёмка владельцем |
| КС-2, КС-3, ВОР | #60 | OUT OF SCOPE | Не трогать |

В дереве `python/sql-server-current/smeta/assets/` найдены логотипы GMC/Консоль,
шрифты DejaVu и шаблоны **КС/ВОР/ПТО/приказов**, но **не найдено одобренных пользователем
неактовых golden-КП/ДС/ведомостей**. Подменять их шаблонами КС или самодельными бланками нельзя.

## Точечное исправление и новые проверки

**Исправление**: `KpPdfForms.Create` выводил в строке `ИТОГО ЗА МАТЕРИАЛ:`
только розничный итог внутренней формы. Python `export_pdf.py` дополнительно печатает
известную закупочную сумму и разность `материал − закупка`. В C# добавлено
накопление стоимости только реально печатаемых не-дочерних строк с имеющейся
`PurchasePrice`; в итоговую строку внутреннего PDF добавлены колонки «закуп всего»
и «маржа». Отсутствующую закупочную цену **не** подставлять как подтверждённый ноль
на уровне отдельной строки. Производственные БД не затрагивались.

**Синтетический набор `GoldenChecks`**: количество 3; цена 17,19; закуп 11,07;
материалы 51,57; закупка 33,21; маржа 18,36; НДС сверх 20% = 10,31;
итого с НДС 61,88; позиционные работы 4,13 × 3 = 12,39. Проверяются
формулы и **их расчётные значения** ClosedXML (если библиотека/рантайм это поддерживают),
ориентация листа, OOXML `_xlnm.Print_Area`/`_xlnm.Print_Titles`,
повторное открытие XLSX, PDFsharp physical-open и orientation. Для извлечения текста
PDF используется настоящий `pdftotext` (Poppler), если установлен; отсутствие программы
отмечается отдельным `SKIP`, а не подменяется имитацией. Для внутреннего PDF дополнительно
проверяется, что закуп и маржа присутствуют **дважды**: в строке и в итогах.
Для ведомости проверяются количество, пустая графа «Получено», примечание,
подписи выдавшего и получившего, OOXML и PDF.

Все тестовые названия, суммы, даты и подписи — **синтетические**.
Ни одного пользовательского документа, базы или архива этот PR не изменяет.

## Команды для локального Windows-прогона

Из корня проверенного checkout (Windows, .NET SDK 10, доступный `pdftotext.exe` —
только если нужна проверка PDF-кириллицы):

```powershell
git status --short --branch
git fetch origin
git checkout test/contract-export-golden-acceptance
git rev-parse HEAD
dotnet --info
dotnet restore csharp/GmcSmeta.slnx
dotnet build csharp/GmcSmeta.slnx -c Release --no-restore
dotnet test csharp/tests/GmcSmeta.Core.Tests/GmcSmeta.Core.Tests.csproj -c Release --no-build
dotnet build csharp/tests/GmcSmeta.ContractExport.Checks/GmcSmeta.ContractExport.Checks.csproj -c Release
$env:GMC_CONTRACT_EXPORT_SAMPLES = Join-Path $env:TEMP 'gmc-contract-65-synthetic'
# Опционально: укажите полный путь к pdftotext.exe (Poppler):
# $env:GMC_CONTRACT_EXPORT_PDFTOTEXT = 'C:\Tools\poppler\bin\pdftotext.exe'
dotnet run --project csharp/tests/GmcSmeta.ContractExport.Checks/GmcSmeta.ContractExport.Checks.csproj -c Release --no-build
Get-ChildItem $env:GMC_CONTRACT_EXPORT_SAMPLES -File | Get-FileHash -Algorithm SHA256
```

Обязательно сохранить stdout/stderr, версию SDK и точный SHA вместе с полученными
синтетическими XLSX/PDF. Не переписывать оригиналы и не добавлять реальные реквизиты
в публичные логи. При `GMC_CONTRACT_EXPORT_PDFTOTEXT`, указывающем на несуществующую
программу, ошибка должна считаться **FAIL**, не `SKIP`.

## SQL Pilot / два клиента — пока не принято

Только после проверки фактических `Server` и `Database=GmcSmetaPilot_*`:

```powershell
$env:GMC_CONTRACT_EXPORT_SQL_PILOT = 'Server=...;Database=GmcSmetaPilot_...;...'
dotnet run --project csharp/tests/GmcSmeta.ContractExport.Checks/GmcSmeta.ContractExport.Checks.csproj -c Release --no-build
```

Этот существующий SQL smoke читает КП и выдачу и сравнивает `meta.rev` до/после,
но **не воспроизводит гонку двух клиентов**. В `MainWindow.ContractExport.LoadKpPrintData`
выполняются последовательные запросы к строкам, закупам, системам, реквизитам и
`TagRules` без общей транзакции snapshot. Проверка `meta.rev` на концах чтения
не гарантирует единой версии данных при прямых изменениях вне механизма ревизий
или особых гонках. **Пока недостаточно для критерия #65.**

Для настоящей приёмки: использовать одноразовый договор и отдельную подтверждённую
пилотную БД; клиент A многократно экспортирует, клиент B меняет одну
закупочную цену/количество/реквизит в отдельной транзакции, соблюдая ревизии.
Проверять, что **один** файл содержит только старый или только новый согласованный
набор, не смесь; исключать подписанные/locked документы; логировать ревизию,
SQL snapshot ID/commit и hashes XLSX/PDF, число строк, денежные колонки;
после каждого экспорта сравнивать состояние SQL. В случае неизвестного результата
COMMIT — readback/reconciliation, без blind retry. Никаких записей в production.

**Нужен согласованный узкий hook с владельцем `MainWindow.ContractExport.cs` /
SQL read path**, чтобы все чтения одного файла были в одном поддерживаемом snapshot
(при подтверждённом SQL Server snapshot isolation). Этот PR не редактирует чужой
`MainWindow.ContractExport.cs` и не включает системные изменения параметров SQL Server.

## PASS / FAIL / SKIP (на HEAD этого PR до запуска Windows)

| Проверка | Статус | Причина / evidence |
| --- | --- | --- |
| Чтение Issue #65, Python-эталонов, #40, профильных C# файлов | PASS (source review) | GitHub, base `43e61155`; не execution |
| Точечный diff внутреннего PDF против Python | PASS (static comparison) | Найден пропущенный purchase/margin total, внесён фикс |
| Windows `dotnet restore`, solution Release build | SKIP | SDK и Windows недоступны здесь; 0 запусков |
| `GmcSmeta.Core.Tests` | SKIP | 0 запусков |
| `GmcSmeta.ContractExport.Checks` (включая новые тесты) | SKIP | 0 запусков; исходники опубликованы, не проверены сборкой |
| XLSX roundtrip, PDF physical-open и PDF-text по реальным файлам | SKIP | Нет локального Windows/Poppler прогона |
| SQL Server Pilot read-only | SKIP | Нет подключения в среде |
| SQL 2-клиентный snapshot race | SKIP / BLOCKED | Нет среды и общего snapshot transaction |
| Реальный пользовательский XLSX/PDF golden, визуальная сверка, WPF/Excel print | BLOCKED | Нет подтверждённых пользовательских образцов |
| GitHub Actions | SKIP | Hosted workflow manual-only из-за billing; отсутствие запуска не означает PASS или FAIL |

**Итого:** новых запусков тестов **0**; новых проверок в исходниках несколько,
но это не результат тестирования. Не использовать «full parity» или «ready to merge»
до Windows, SQL и пользовательского golden acceptance.

## Не затронутые области

`Ks*`, `Vor*` (#60), закупочные write-paths (#64), `EstimateView.*` (#59),
`ActsEditorView*`, production SQL, пользовательские templates и архивы.
