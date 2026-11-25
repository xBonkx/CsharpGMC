# Задача №12: письма — матрица Python 0.66.4 → C# (21.11.2026)

**Ветка:** feature/letters-parity. **PR:** https://github.com/xBonkx/CsharpGMC/pull/19 (draft, в main не сливать до приёмки). **Источник:** python/sql-server-current/docs/letters.md и smeta/letters/{model,ui,export,pdf_document}.py; тесты python/sql-server-current/tests/test_letters.py.

**Правило:** C# работает только с Microsoft SQL Server. Исходный letters.sqlite3 — исторический образец; работающая новая версия его не читает. Любая схема/запись — исключительно в базе, фактически названной GmcSmetaPilot_*. Это намеренная блокировка production cutover.

## Матрица реализации

| Сценарий Python | Файлы C# | Перенос в коде | Подтверждённое тестирование |
|---|---|---|---|
| Главное меню → Письма (любой режим) | MainWindow.xaml, MainWindow.Letters.cs | Да | Требуется Windows UI |
| Архив писем, поиск, фильтр, список | LettersWindow.* / SqlServerLetters | Да | Требуется SQL pilot |
| Создание, открытие, изменение, копия | LetterEditorWindow.*, SqlServerLetters | Да | Copy rules offline; SQL pilot pending |
| Дата/номер ДД.ММ-NN, manual/auto, смена даты | LettersRules, SqlServerLetters | Да, SQL-транзакция + индекс | Offline + optional two-client SQL |
| Подстановка организации и объекта без их изменения | LetterEditorWindow.xaml.cs | Да, read-only SELECT | Требуется SQL pilot |
| История изменений, optimistic conflicts | SqlServerLetters | Да, full JSON snapshots | Optional stale-revision SQL |
| Форматируемый текст (жирный, курсив, список, alignment) | LetterEditorWindow.*, LettersPdf | Да, внутренний XAML | Windows PDF smoke, визуальная приёмка pending |
| Многостраничный PDF, кириллица | LettersPdf | Да, PDFsharp + Windows fonts | Windows PDF smoke pending; встраивание Unicode необходимо проверить отдельно |
| PNG/JPG/JPEG/PDF, подсчёт страниц, SHA | LettersArchive | Да | Windows image/signed PDF check pending |
| Назначение/название/фильтр/порядок файлов | LetterEditorWindow, LettersArchive | Да | SQL pilot pending |
| Все сканы вместо текста, порядок | LettersPdf | Да | Windows PDF smoke pending |
| PDF письма, HTML, каталог приложений, JSON-опись | LettersArchive/LettersPdf | Да | Windows smoke частично |
| Объединение + страницы A4 для фото | LettersArchive/LettersPdf | Да | Windows check needed |
| Выпуски отдельно, ранее созданные неизменны | LettersArchive, SqlServerLetters | Да | Нужен сценарий file rollback и SQL errors |
| Регистрация отправки без фактической отправки | SqlServerLetters, LetterEditorWindow | Да | SQL pilot pending |
| Защита отправленного письма, добавление подтверждений | SqlServerLetters | Да | SQL pilot pending |
| ZIP backup/restore, защита traversal | LettersArchive, SqlServerLettersBackup | Да, restore только в ПУСТОЙ SQL+файловый архив | Offline ZIP check; full restore pending |
| Импорт исторического Python letters.sqlite3 | Отдельный конвертер не реализован | Нет (обозначенное ограничение) | Не проверялось |
| Пиксельный паритет Qt/Windows 1920×1080 и 1366×768 | LettersWindow/LetterEditorWindow | Структура совпадает, точные размеры элементов требуют проверки | Нет парных скриншотов |

## SQL и конкурентность

- Schema: dbo.gmc_letters, dbo.gmc_letter_files, dbo.gmc_letter_history, dbo.gmc_letter_releases, dbo.gmc_letter_sending. CREATE повторно применим, только в GmcSmetaPilot_*.
- Выделение номера при INSERT и при смене даты в Serializable-транзакции; транзакционная годовая блокировка sys.sp_getapplock; уникальный индекс (год, нормализованный исходящий номер). Предварительный номер UI — предложение, не бронь; Save выделяет окончательный.
- Optimistic version revision проверяется в той же транзакции. При конфликте данные второго клиента не перезаписываются.
- Файлы: отдельный конфигурируемый архив. Публикация уникальных имён через временные файлы и rename, при SQL rollback опубликованный файл/выпуск удаляется.
- Восстановление: новая пустая директория и пустая таблица писем в тестовом SQL. Исходные/производственные данные не очищаются. Из ZIP проверяются пути и SHA файлов до SQL INSERT.

## Ограничения и приёмочные проверки

1. Режим «выбрать архив» переключает **файловый** корень, но общий список писем в SQL остаётся тем же. Для нескольких сотрудников должен быть задан одинаковый сетевой GMC_SMETA_LETTERS_ROOT и настроены права SMB/NTFS; локальный каталог по умолчанию предназначен только для одного клиента.
2. HTML Qt старых писем читается без выполнения скриптов, но как простой текст: для точного переноса оформления старой базы нужен офлайн Python конвертер Qt HTML → WPF XAML.
3. Рендеринг использует PDFsharp и Windows-шрифт Arial. Вставку ToUnicode/реальное встраивание кириллицы в PDF следует проверить с PDF-анализатором на Windows; без этой проверки нельзя объявлять полное совпадение с Python ReportLab/DejaVu.
4. Сравнить и визуально проверить окна на 1920×1080 и 1366×768; проверить многостраничный RichText, печать/переносы и исходный корпоративный бланк.
5. Провести двухклиентный SQL pilot, network drop, rollback и restore (в отдельную пустую GmcSmetaPilot_*), по возможности в Windows с UI, до merge.

## Команды проверки

Offline Windows CI: .github/workflows/letters-parity.yml. Число выполненных тестов и URL конкретных запусков указываются после получения статуса Actions.

SQL integration (запускать **только** на выделенной тестовой БД и согласованном сервере): установить GMC_SMETA_LETTERS_TEST_SQL и выполнить dotnet run --project csharp/tests/GmcSmeta.Letters.Checks/GmcSmeta.Letters.Checks.csproj -c Release. Отсутствие переменной означает SQL-интеграционные тесты **не выполнялись**.
