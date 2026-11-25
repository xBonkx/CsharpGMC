# GMC-Смета: люди, автомобили, допуски, приказы — паритет (Issue #38)

Срез: 21.11.2026. Рабочая ветка `feature/people-access-orders-parity`. Область: только новые `People*`, `AccessList*`, `Orders*` и профильные тесты/CI; `MainWindow.*` не менялись.

## Архитектура и границы данных

- **Один справочник**: существующие таблицы SQL Server `dbo.people`, `dbo.cars`, `dbo.access_lists` из Python-эталона версии схемы 28. Новых SQLite-путей, дублирующей бизнес-модели или миграции справочника нет.
- Перед каждым подключением к хранилищу выполняется `SqlServerWorkspace.RequirePilotDatabase` и повторная проверка реального имени БД `GmcSmetaPilot_*`. Это ограничения пилота, не production cutover.
- Записи — `SERIALIZABLE`, `sp_getapplock`, сравнение `meta.rev`, транзакция с новой ревизией. Пакетные импорты реализованы через `SqlBulkCopy` в одной транзакции. Дополнительных логов с персональными данными в CI нет.
- История `dbo.access_lists` фильтруется по `object_id` и `kind`. Имя, должность, дата рождения и авто сохраняются в отдельном JSON snapshot (не пересчитываются по текущему справочнику).
- DOCX-шаблоны копируются из трёх Python-файлов в отдельную папку `%APPDATA%\GMC-Smeta-Next\Шаблоны приказов`, существующие пользовательские шаблоны не перезаписываются. XLSX и DOCX выдаются в выбранные папки; факт записи в журнал — **только после** успешного создания файла.
- Неизвестный исход SQL COMMIT не считается успехом. GUID операции можно проверить в `WasRegistered`; неидемпотентную выдачу автоматически не повторять.

## Матрица `Python → C# → тест → ограничения`

| Источник Python / действие | C#-реализация | Тест | Текущий статус |
|---|---|---|---|
| `people.py:people/cars/find_person/find_car/plate_key` | `SqlServerPeopleAccess.Load`, `PeopleRules` | `PeopleRulesTests` | Код добавлен, Windows build ожидает runner |
| `PeopleDialog.reload/_filter/_counts` | `PeopleWindow.xaml[.cs]` | Ручной WPF | Реализовано, парные скриншоты не сняты |
| `_person_changed/_car_changed/add_person/add_car` | `SavePerson/SaveCar`, диалоги редактирования | Пилотный SQL | Вместо inline-edit редактирование через диалог |
| `_set_active/_delete/_move` | `SetActive/DeleteOrDeactivate/Reorder` | Пилотный SQL | Исторически использованные записи деактивируются, физически удаляются только без зависимостей |
| `import_excel/read_list_file` | `PeopleDocuments.ReadImport`, `Import` | XLSX roundtrip, SQL import 1000/200 | Построчный предпросмотр (первые 500), явное сопоставление столбцов и транзакционный импорт |
| `_prepare/_hist_chosen/_open_old` | `History/RestoreHistory/OpenHistory` | Пилот 2 объектов | SQL-история объектно ограничена, имена исторических файлов не маскируются в диалоге пользователя |
| `_tick_all/make_list/export_list/save_list` | `PeopleWindow`, `ExportAccessList`, `RegisterExport` | Реальный XLSX, SQL-пилот | Используется шаблон оформления Python, открытие файла после выдачи |
| `orders_dialog.next_number/_preview` | `PeopleRules.NextNumber`, `OrdersWindow` | Unit | Ручное склонение в общем справочнике |
| `order_templates/order_mapping/fill_docx` | `PeopleDocuments.Templates/OrderMapping/FillDocx` | Реальный DOCX с разорванным Word run | Доступны оригинальные редактируемые Word-шаблоны, автоматическая нумерация и пакетный экспорт |
| `OrdersDialog.make`, журнал `kind=order` | `OrdersWindow`, `RegisterExport` | SQL-пилот/Office checks | Сохранение `site_full` атомарно вместе с историей; неизвестный COMMIT требует ручной сверки |

## Интеграция главного меню

Владелец задачи меню может подключить **без изменений текущего PR**:

```csharp
// общий справочник
PeopleAccessWindows.OpenPeople(owner, connectionString);
// выбранный объект
PeopleAccessWindows.OpenPeople(owner, connectionString, objectId);
// приказы выбранного объекта
PeopleAccessWindows.OpenOrders(owner, connectionString, objectId);
```

Файл точки входа: `csharp/src/GmcSmeta.App/PeopleAccessWindows.cs`.

## Проверки

1. Offline .NET: `dotnet test csharp/tests/GmcSmeta.Core.Tests/GmcSmeta.Core.Tests.csproj -c Release --filter FullyQualifiedName~PeopleRulesTests`.
2. Реальные Office-файлы из синтетических данных: `dotnet run --project csharp/tests/GmcSmeta.People.Checks/GmcSmeta.People.Checks.csproj -c Release`. Тест открывает XLSX через ClosedXML и ZIP/DOCX, подтверждает форматирование и замену полей, не пишет на сервер.
3. Изолированный SQL пилот **только после явного конфигурирования** переменной `GMC_SMETA_PEOPLE_SQL_PILOT` (строка подключения именно к созданной заранее базе `GmcSmetaPilot_*`, схема 28, ≥2 тестовых объекта). В нём предусмотрены 1000 синтетических людей, 200 автомобилей, повтор импорта, конфликт ревизии, разделение по двум объектам и отсутствие записи в историю при отказе файловой системы.
4. `.github/workflows/people-access-orders-parity.yml` — Windows Release build, unit и Office checks.

**На момент открытия PR #47 GitHub Actions завершился до запуска runner/шагов сборки.** Поэтому сборка и тесты, включая пилотную SQL-проверку и визуальный паритет Windows при 1920×1080/1366×768, **ещё не подтверждены**. Публиковать модуль как принятый/полный паритет до таких проверок нельзя.

## Открытые ограничения

- Inline-редактирование ячеек Python временно заменено рабочими отдельными диалогами; следовательно буквальный UX-паритет ещё не завершён.
- Построчный предпросмотр доступен (до 500 строк), соответствие колонок можно задать вручную; структура нестандартных файлов без заголовков и без секции «Автомобили» требует отдельного контроля перед импортом.
- Нумерация приказов реализована для одной выдаваемой пачки и текущего номера в форме; не выполняется серверное резервирование номера среди всех клиентов.
- Отдельный контроль ролей/прав доступа по объекту сверх существующих SQL-прав подключения не реализован.
- Наименование текущего объекта в `objects.site_full` может быть сохранено при выдаче приказов; требуется интеграционный пилотный тест конкурентных изменений объекта.
- Для полноценной приёмки обязательно проверить настоящие клиентские файлы, запуск двух WPF-клиентов, импорт 1000/200, потерю сети при COMMIT, закрытый/занятый DOCX, правильность склонения сложных фамилий и Word печать, затем сравнить визуальный результат с Python.

Никаких коммитов в `main` и прямых записей в production не производилось. Слияние делает только координатор после ревью.
