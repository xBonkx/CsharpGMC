# GMC-Смета — №18. Независимая golden-file регрессия документов

**Дата:** 21.11.2026. **ТЗ:** https://github.com/xBonkx/CsharpGMC/issues/44  
**Ветка:** test/document-parity-golden-suite  
**База main (на момент ответвления):** 4e33467c876eebacfaa68dd0ace9972670a4a4b1.

## Инвентаризация Python ↔ C#

| Область | Python-эталон | C# / уже существующие тесты | Что добавлено |
|---|---|---|---|
| Письма | python/smeta/letters/export.py, pdf_document.py, docs/letters.md | LettersPdf, LettersArchive, LettersRules; Letters.Checks + Letters.SqlChecks | HTML-экранирование, номер и дата, 145 абзацев PDF, повторный экспорт, 2+1 скана, SHA исходников |
| Паспорта | python/smeta/passports/model.py, pdf.py, docs/passports.md | VentPassports, VentPassportPdf; Passports.Checks + Passports.SqlChecks | Контракт E.1/E.2, 55 строк с переносом на стр. 3, черновик, отрицательный расход, повтор |
| АОСР/реестры | python/smeta/pto/excel.py, docs/pto.md | PtoExcel, PtoRules; Pto.Checks + Pto.SqlChecks | Номер C19, дата AL19/AP19, период U60, подписи A22/A26/A89, C19, L73, слияние A42:BE42, wraps, 6 документов о качестве, индекс схем и сдачи, исключения, повтор |
| ВОР | python/smeta/pto/vor.py | PtoVor; Pto.Checks | Синтетическая ведомость: явная карта колонок, дубль, отрицательный объём, нечисловой объём, неизвестный лист |
| PDF качества | python/smeta/pto/pdf.py, release.py | PtoArchive; Pto.Checks | Порядок 1+2 стр., bookmarks, расхождение заявленного количества страниц и SHA неизменённых исходников |
| ZIP/backup | python/smeta/pto/model.py, letters/export.py | PtoBackup, LettersArchive; существующие checks | Traversal, дубликаты без учёта регистра, отсутствие метаданных, валидная структура |
| Реквизиты и сертификаты | python/smeta/pto/model.py | PtoRules, LettersRules; existing domain tests | Пустые подписанты, недопустимые даты, просроченный сертификат, отсутствующее вложение и некорректный статус отправки | 
| DOCX (в рамках проверки #44) | В Python письма/паспорта/ПТО выпускаются как PDF/XLSX; Word-template приказы находятся отдельно в `python/sql-server-current/smeta/people.py` | В трёх проверяемых модулях DOCX-экспортёр не требуется; **приказы уже работают через `PeopleDocuments.FillDocx` (PR #47)** | В #44 — только синтетическая OOXML-фикстура; отдельный аудит и 9 сценариев DOCX опубликованы в [DOCX_PARITY_ACCEPTANCE.md](DOCX_PARITY_ACCEPTANCE.md). **Не full parity** |

Файл csharp/docs/KS_EXPORT_PARITY.md изучен исключительно для подхода к тестам; функционал КС находится вне границ этой работы.

### Почему отдельные тесты не заменяют исходные

У исходных Checks уже есть smoke-тесты PDF и XLSX. Дополнительные проверки ориентированы на негативные и межмодульные сценарии, структуру Office/PDF, независимый парсер артефактов, повторяемость и защиту файлов. Не заявляется полное независимое совпадение с реальным печатным бланком.

**Отличие шрифтов:** Python генерирует PDF посредством ReportLab/DejaVu, C# — PDFsharp/Arial. Совпадение количества страниц и извлечённого текста не означает совпадения каждого пикселя, переноса строки или гарнитуры.

## Как запустить

1. Windows, .NET 10 Desktop SDK:
   dotnet run --project csharp/tests/GmcSmeta.Documents.GoldenChecks/GmcSmeta.Documents.GoldenChecks.csproj -c Release
2. Выходные синтетические PDF, XLSX, ZIP и DOCX помещаются исключительно в новую временную папку document-golden-UUID. Программа печатает её путь и записывает RESULTS.md.
3. Независимый анализатор: установить pypdf==6.1.3 и openpyxl==3.1.5, затем python csharp/tests/GmcSmeta.Documents.GoldenChecks/verify_artifacts.py [путь к временной папке].
4. GitHub Actions: .github/workflows/documents-golden.yml — Windows билд, генерация и валидация, артефакт documents-golden-synthetic с retention 7 дней; Linux — вызов существующих трёх SqlChecks на одноразовых GmcSmetaPilot_* в контейнере Microsoft SQL Server 2022.

SQL job создаёт и удаляет собственные тестовые базы. CI не использует пользовательскую connection string. Запись в production недопустима. Проверки SQL используют существующие сценарии миграций, конкурентности, snapshot, rollback, защиты статусов и экспорта, без новых тестовых SQL-таблиц.

## Контрольная матрица приёмки

| Критерий | Автоматическая проверка | Что остаётся |
|---|---|---|
| Открываемость PDF и число страниц | C# PdfReader + Python pypdf | Печать реального бланка |
| Кириллица, номера и даты | LettersPdf, VentPassportPdf, PtoExcel + pypdf/openpyxl | ТоUnicode/font embedding вручную |
| Подписанты и реквизиты АОСР | XLSX ячейки и повторная проверка openpyxl | Реальные полномочия, печати/подписи |
| XLSX структура, формулы, print layout | ClosedXML, ZIP XML, openpyxl, формулы без #REF | Визуальный разрыв страниц в Microsoft Excel |
| Реестры и комплектность | Шесть документов о качестве, схема, перечень | Реальные сертификаты, сроки и применимость |
| Пустые поля, даты и сертификаты | PtoRules.Check, LettersRules.Validate и проверка истёкшего срока | Требуется ручная проверка полномочий, подлинности сертификата и подписантов |
| Архивирование и ZIP безопасность | Safe archive, traversal, missing manifest, duplicate | Отказ удалённого SMB, NTFS ACL и полный файловый restore |
| Сохранение повторной выдачи | Сравнение логических значений XLSX и геометрии PDF | Проверка реестра выпущенных версий на общем файловом сервере |
| SQL история и конфликты | Повтор существующих SqlChecks в GmcSmetaPilot_* | Две реальные машины, ACL, отключение сети, неизвестный исход COMMIT |
| DOCX | #44: синтетическая OOXML-фикстура; #74: независимые проверки реального `PeopleDocuments.FillDocx` на synthetic data | **Orders DOCX exporter существует (PR #47)**; для внешних фирменных Word-бланков — **EXTERNAL TEMPLATE REQUIRED**. Windows Word/LibreOffice и SQL остаются SKIP до реального запуска |

## Известные ограничения / статус

- Реальные пользовательские PDF, сертификаты, подписи, сметные базы и исходные клиентские бланки не добавляются в репозиторий и не нужны для этого автоматического прогона.
- Исполнитель должен отдельно провести сравнение настоящего шаблона АОСР и реестра в Excel/Word, с печатью и проверкой размеров/штампов на Windows.
- Для ошибок доступа на SMB и отказов диска нужна специальная инфраструктура (отозванные NTFS/SMB ACL, недоступный сервер, журнал восстановления). Локальные тесты моделируют отсутствие файла/шаблона и существующий файл на месте каталога, но **не** имитируют SMB ACL.
- Повреждение/утрата цифровой подписи исходного PDF при объединении не проверяется синтетическими пустыми страницами: криптографическую валидацию проводить отдельно.
- Новый проект не модифицирует исходники Letters*, Passports*, Pto*, Ks*, MainWindow.*, EstimateView.* и не меняет SQL-схему. Дефекты фиксировать отдельным issue/PR с воспроизводимым примером, а не менять боевые модули без разрешения.
- **Фактическая проверка CI:** запуск https://github.com/xBonkx/CsharpGMC/actions/runs/44901155891 завершился failure ещё до назначения runner: обе задачи имеют **0 steps**. Повторный push-run https://github.com/xBonkx/CsharpGMC/actions/runs/44901260405 имеет такой же результат (0 steps); другие существующие workflows репозитория на том же commit также завершаются до начала шагов. **Это не результат запуска golden tests**: сборка, PDF/XLSX парсеры и SQL не были выполнены. Причина отказа runner требует проверки в интерфейсе GitHub Actions владельцем репозитория; не следует приписывать сбой исходному коду или считать acceptance проведённой.\n- **Статус:** TEST EXECUTION BLOCKED (pre-runner). После исправления доступности Actions нужно перезапустить оба jobs и приложить реальный RESULTS.md.
