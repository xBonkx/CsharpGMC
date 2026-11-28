# Приёмка BUG-F55/F56/F57 — 29.11.2026

Бронь chat-accept-vor: 61e3560ea00be789811b0eb6e1cc72eabeb584ec. Только тестовые файлы, без SQL/GUI и общего стенда.

- F55: точный десятистрочный golden fixture для standard/DNS/R4 сохраняется как *_VOR_quantities.xlsx после проверок чисел и reopen.
- F56: шесть XML fixtures (2/300 строк × 3 формы) сохраняются как *_VOR_print_2.xlsx / *_VOR_print_300.xlsx для Excel Print Preview; 300 строк не гарантируют количество страниц.
- F57: два сценария блокировки Windows (XLSX/PDF без FileShare.Delete), прежние байты, отсутствие staging и успешная повторная выгрузка после снятия блокировки. Два сценария отмены после частичного рендера. Golden-образцы публикуются через штатный ExportFilePublication после reopen.

## Локальный прогон

В отдельном worktree, не одновременно с нагрузочными замерами:

```powershell
dotnet test tests/GmcSmeta.Core.Tests/GmcSmeta.Core.Tests.csproj -c Release --filter FullyQualifiedName~ExportFilePublicationTests
$env:GMC_KS_GOLDEN_OUTPUT = Join-Path $env:TEMP ("gmc-vor-accept-" + [guid]::NewGuid().ToString("N"))
dotnet run --project tests/GmcSmeta.KsGoldenChecks/GmcSmeta.KsGoldenChecks.csproj -c Release
```

Записать SHA, реальные exit code, PASS/FAIL, открыть девять специальных ВОР в Excel и сохранить доказательства: целые без разделителя, дробные, округление, пустая первая страница, последующие 2+, DNS без номера. Для F57 дополнительно реальные UI экспорт/lock, Excel/PDF reopen, сетевой/дисковый отказ на отдельном безопасном стенде и аудит остальных экспортёров. Не использовать рабочие файлы. На не-Windows lock-тест возвращается без проверки; не считать это evidence.

Новые тесты не запускались: нет Windows/.NET/Excel в этом сеансе. Три ID остаются CODE READY. Продакшн-код, SQL/БД и релиз не менялись.
