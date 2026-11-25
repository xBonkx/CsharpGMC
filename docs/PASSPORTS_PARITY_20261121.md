# GMC-Смета №13 — паспорта вентиляционных систем: Python → C#

**База:** main bac70e0a (21.11.2026). **Ветка:** feature/passports-parity.
**Python-эталон:** python/sql-server-current/docs/passports.md, smeta/passports/model.py, ui.py, pdf.py, tests/test_passports.py.
**Архитектура:** только Microsoft SQL Server; SQLite-архив остаётся историческим источником, работающая версия C# его не открывает.

## Проверяемая матрица

| Python 0.66.4 | C# реализация | Проверка |
|---|---|---|
| Объект → «Паспорта» | MainWindow.Passports.cs, ObjectPassportsButton | Windows UI (не выполнено) |
| Список систем; добавить/удалить/копировать | PassportsWindow.xaml.cs | SQL + UI |
| Массовое создание по строкам, П/В/ДП/ПД/ДВ/ДУ/ВД | VentPassports.BatchCreate/DetectPurpose | xUnit + SQL |
| Реквизиты и «→ всем системам» | VentPassport, ApplyCommon, SaveBatch | xUnit + SQL |
| Все таблицы Е.1.1–Е.1.8 | Sections, вертикальная WPF таблица, PDF | xUnit + PDF |
| Каталог моделей с независимыми снимками; расход не переносится | MakeTemplate/ApplyTemplate, SQL catalog | xUnit + SQL |
| Е.2, Ctrl+V от выбранной ячейки, добавление/удаление | PassportsWindow PointsGrid | xUnit + ручной UI |
| Десятичная запятая, отрицательные/нечисловые | VentPassports.Flow/Totals | xUnit |
| Незаполненные фактические замеры = пустой итог и ЧЕРНОВИК | IsDraft, PDF | xUnit + PDF |
| Кириллица, A4, Е.2 на стр. 3, повтор заголовков | VentPassportPdf | Windows PDF + pypdf |
| Е.1.7 пустая, Е.1.8 зависит от фильтра | VentPassportPdf | pypdf |
| PDF выбранных/всех, уникальные файлы | PassportsWindow.Export, SqlServerPassports.RegisterExport | Windows/UI, SQL |
| SQL isolation, multi-client, rollback | SqlServerPassports | SQL integration |
| Исторический SQLite-импорт | **Не перенесён**, отдельная безопасная операция | Не выполнялся |

## SQL и безопасность

- Пилотные таблицы: dbo.gmc_passports, dbo.gmc_passport_equipment_catalog, dbo.gmc_passport_history, dbo.gmc_passport_exports. JSON в gmc_passports содержит 8 таблиц и замеры Е.2; отдельный каталог сохраняет снимки оборудования.
- Создание таблиц повторно применимо и сериализовано sp_getapplock. Запись и DDL только для GmcSmetaPilot_*, с проверкой фактической открытой базы. SaveBatch использует SERIALIZABLE, объектную блокировку и оптимистичные ревизии; при конфликте откатывается весь пакет.
- Файловые PDF хранятся отдельно в настраиваемом каталоге, имеют уникальные имена, SHA-256 и метаданные SQL. Временные файлы и атомарное опубликование. Для нескольких сотрудников нужен общий защищённый сетевой каталог.
- Важное ограничение: действующая операция удаления всего объекта в основном модуле пока не удаляет паспорта автоматически. Оставшиеся SQL-записи должны учитываться при отдельной политике удаления/архивации объектов. Сметные/денежные таблицы в этом PR не меняются.

## PDF и приёмка

- Первая страница: Е.1.1–Е.1.4, вторая: Е.1.5–Е.1.8, Е.2 целиком на второй или с третьей при нехватке места. Многостраничная Е.2 повторяет шапку. Очень длинные описания отклоняются без записи повреждённого PDF.
- Черновые PDF содержат видимую отметку ЧЕРНОВИК, фактические числа не создаются. Шрифт Arial в PDFsharp-WPF; проверка извлечения кириллицы через pypdf в Windows CI, дополнительная проверка физического встраивания шрифта требуется.
- Нужна ручная Windows-приёмка интерфейса при 1920×1080 и 1366×768 и сквозного сценария сохранения, Excel Ctrl+V, каталогов и печати. Этого нельзя подтверждать только build и unit-тестами.
- Историческая миграция SQLite → SQL Server не реализована (отдельный офлайн-конвертер без SQLite-зависимостей работающей версии).
- Новые тесты: VentPassportsTests; Windows GmcSmeta.Passports.Checks и verify_pdf.py; контейнерный SQL GmcSmeta.Passports.SqlChecks. Реальные результаты CI приводятся в PR после выполнения.
