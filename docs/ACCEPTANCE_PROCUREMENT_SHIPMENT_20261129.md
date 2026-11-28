# Приёмка выдачи и сводки руководителя — 29.11.2026

Бронь chat-accept-buy: f380fa58416112b7c4b6bedc266c8c20a28acb99. При финальной сверке обнаружены два merge-конфликта и 25 повторных строк (405 вместо 380); параллельный агент разрешил их, main 624ac8a9008743940c5244d7cc467d6fd76c8bf3. Сохранены чужие 25 броней zcode-sess-bugf125 и 30 perfacc. F11/F12 оставлены их владельцу, пересекающиеся тесты исключены.

| ID | Добавленная проверка | Остаток |
|---|---|---|
| F29 | Source guard: Fix/Unfix внутри serializable, ledger до удаления, условия повреждения | Отдельная Pilot fixture: корректный цикл, дубли/пропуск/out неправильного qty/чужая позиция/пустой fixed. Проверить обе команды, остатки, журнал/rev. |
| F30 | Source guard: expected revision, повторная revision, полный count; XLSX/PDF захватывают текущую revision до диалога | Второй клиент меняет ведомость во время SaveFileDialog; чужая строка; отказ без публикации; сравнение валидного XLSX/PDF с UI. |
| F51 | Контракт без известных закупочных цен + контракт с известной нулевой себестоимостью: 25% weighted coverage, source guard директора | Существующий PilotFinanceBatchCases проверяет MaterialRevenue=245, CoveredRevenue=225, cost=86 и director projection; запустить и сопоставить Python/SQL, экран директора и несколько договоров. |

```powershell
dotnet test tests/GmcSmeta.Core.Tests/GmcSmeta.Core.Tests.csproj -c Release --filter FullyQualifiedName~ProcurementShipmentAcceptanceTests
```

Три новых xUnit-сценария (два source guards, один расчёт и wiring). Dotnet/Windows/SQL недоступны, запусков не было. Source guards не заменяют реальную приёмку. Все 3 остаются CODE READY. SQL/DDL/БД и production-код не менялись. Живые проверки только на отдельной разрешённой GmcSmetaPilot_* копии, без общего стенда и Cutover.
