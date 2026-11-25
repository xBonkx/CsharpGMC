# GMC-Смета — P0 №6 / Issue #34: неактовый экспорт КП и ведомостей

Статус: **реализация для ревью; приёмка не подтверждена**. PR [#40](https://github.com/xBonkx/CsharpGMC/pull/40), рабочая ветка `feature/contract-document-export-parity`. Эта работа не меняет `main`.

## Матрица Python → C#

| Сценарий Python | Реализация C# | Регрессия | Состояние |
| --- | --- | --- | --- |
| `export_kp.export_kp`: обычное КП, договор, ДС/родитель, 6 колонок | `KpExcelForms.Create` + `SqlServerKpRequisites.Read` | `CheckKpForms`, `CheckSqlPilotIfConfigured` | Код готов; визуально не принят |
| `export_kp.export_kp(internal=True)`: закуп/розница/маржа | `KpExcelForms.Create(internalPrices:true)`; `MainWindow.ContractExport` берёт фактическую цену `procurement.cost_price` прежде плановой | `CheckKpForms` | Код готов; фактический SQL не проверен |
| `export_kp_pos.export_kp_pos`: позиционные материалы/работы | `KpExcelForms.Create(positional:true)`, автоматический выбор при `smr_mode=pos` | `CheckKpForms` | Код готов; визуально не принят |
| `export_pdf.export_kp_pdf`: обычное/внутреннее КП | `KpPdfForms.Create`, A4, таблицы и переносы, шапка/логотип/подписи | `ValidatePdf` + multipage | Код готов; нет попиксельной сверки |
| `export_pdf.export_kp_pos_pdf` | `KpPdfForms.Create(positional:true)` | `CheckKpForms` | Код готов; нет попиксельной сверки |
| `export_ship.export_ship_excel`: ведомость по существующей выдаче | `SqlServerShipmentPrintData.Read` + `ShipmentExcelForms.Create` | `CheckShipmentForms`, SQL pilot optional | Код готов; 6 колонок, примечания, подписи |
| Ведомость PDF / многостраничная выдача | `ShipmentPdfForms.Create` | 380 строк, PDF page count | Код готов; отличие от HTML-эталона возможно |
| Все четыре режима СМР (fixed/percent/systems/pos) | `KpExcelForms` использует `Totals`, для percent — формула `ROUND`, для systems — промежуточные итоги | режимы в `CheckKpForms` | Код готов; до копейки в реальной БД не принято |
| `vat_top` и необязательная строка «в т.ч. НДС» | `KpExcelForms` и `KpPdfForms`; варианты меню `vatLine` | `CheckKpForms` | Код готов |
| Формат печати/системы/длинные названия | Высоты и ширины Python, merged cells, стили, печатная область, повтор заголовка, разрывы PDF | 2500/10000 XLSX, multipage PDF | Программные проверки, без Excel UI |
| `ui/kp_export.py`: путь и выбор формата | `MainWindow.ContractExport.cs`, группа меню, `ContractDocumentSave` (PDF/Excel раздельно, cancel, temp+rename) | проверка факта сохранения; SQL read-only | Не вся UX-паритетная конфигурация |

## Правила безопасной эксплуатации

- Все чтения — через `SqlServerDatabase.Open` и параметризованные SQL-запросы; записи в SQL, проведение выдач и изменение документа при экспорте отсутствуют.
- Для каждого КП загружаются строки, итоги, действующий договор/компания/подписи/ревизия, на `meta.rev` есть проверка до/после чтения. **Проверка ревизии не эквивалентна транзакционному snapshot isolation**, поэтому при очень быстрых сторонних изменениях остаточный race возможен.
- Excel-файлы собираются во временный файл в том же каталоге, проверяются повторным чтением и только потом заменяют целевой. PDF проверяется по заголовку. Ошибки файловой системы/SMB возвращают предупреждение, не сообщая о ложном успехе.
- SQL-pilot дымовой тест активируется **только** через переменную `GMC_CONTRACT_EXPORT_SQL_PILOT`, допускающую исключительно каталог `GmcSmetaPilot_*`. Если переменная отсутствует, тест явно `SKIP`.
- Рабочая SQL Server БД не меняется.

## Как воспроизвести

Windows / .NET 10, из корня репозитория:

```powershell
dotnet build csharp/tests/GmcSmeta.ContractExport.Checks/GmcSmeta.ContractExport.Checks.csproj -c Release
$env:GMC_CONTRACT_EXPORT_SAMPLES = "$env:TEMP\gmc-contract-export-samples"
dotnet run --project csharp/tests/GmcSmeta.ContractExport.Checks/GmcSmeta.ContractExport.Checks.csproj -c Release --no-build
```

Проверка на тестовой SQL Server (только при наличии безопасных реквизитов):

```powershell
$env:GMC_CONTRACT_EXPORT_SQL_PILOT = "Server=...;Database=GmcSmetaPilot_...;..."
dotnet run --project csharp/tests/GmcSmeta.ContractExport.Checks/GmcSmeta.ContractExport.Checks.csproj -c Release --no-build
```

Windows GitHub Action: `.github/workflows/contract-export-parity.yml`; загружает `contract-export-samples` при успешном выполнении.

## Что не принято / BLOCKED для закрытия P0

1. **Windows CI:** первый реальный запуск выявил CS0246 из-за отсутствующего `using System.IO` в `KpExcelForms`; код исправлен. Последующие запуски GitHub Actions, включая соседние workflow, немедленно завершаются `failure` **без выделенного runner и без шагов**. Нельзя утверждать, что исправленная версия уже скомпилирована и тесты прошли.
2. **Реальные golden fixtures:** недоступны подтверждённые пользовательские образцы Excel/PDF (для КП/ДС, подписей, остатка, пользовательских шаблонов), поэтому проверены только программно созданные сценарии, не попиксельный паритет и не сверка реального договора по копейкам.
3. **SQL pilot, UI click-through, SMB:** подключение к тестовому SQL Server и запуск WPF окна здесь недоступны; добавлен безопасный интеграционный тест, но он реально не запускался. Нужна проверка пользователем на Windows pilot с реальным КП/выдачей, включая одновременное изменение другим клиентом.
4. **UI-опции Python:** в меню есть типовые и VAT-line варианты, но нет единого окна с выбором альтернативной компании, экспорта «Excel и PDF одним действием», ссылки на расчёт и сохранения последнего каталога. Текущая компания берётся из документа, на экспорт контракты не изменяются.
5. **Различия PDF:** PDFsharp рисует таблицы, а Python использует Qt HTML print; разрывы страниц, расстояния и метрики шрифтов могут отличаться. Клиентские нестандартные шаблоны не подменяются и не объявлены полностью перенесёнными.

## Непересекающиеся зоны

Правились только собственные новые классы экспорта, `ShipmentsPane.xaml[.cs]` (минимальный hook), меню `MainWindow.xaml.cs` (одна экспортная секция), отдельные профильные тесты/CI и этот отчёт. **Не менялись** `EstimateView.*`, `Procurement*`, `CostSummary*`, `Ks*`, `SqlServerActs*`, `SqlServerStages*`, `Metal*`.

## Условия для закрытия issue

Включить рабочие Windows runner, добиться green сборки/тестов; прогнать тестовую SQL Server БД и независимые реальные Python/C# образцы, проверить подписи, расчёты, печать/страницы и открыть диалоги в настоящем WPF. Только после этого переводить PR из draft в ready и назначать приёмку координатору.
