# Текущая работа GMC-Смета — 28.11.2026

**Начать с [рабочей очереди](docs/EXECUTION_TODO_20261128.md), забронировать ID и подтвердить коммит в main.** Не читать весь исторический журнал для выбора задач.

- Основной реестр: [MASTER_TODO](docs/MASTER_TODO.md), на момент реорганизации 380/418 CODE READY, 36 PARTIAL, 2 BLOCKED. Дочерние статусы и занятость — в рабочей очереди; не суммировать.
- Приоритет: [перенос Python](docs/PYTHON_0664_TO_0679_PARITY.md), [SQL/релиз](docs/SQL_MIGRATION_RELEASE_TODO.md), [договоры/приложения](docs/AGREEMENT_ANNEX_TODO_20261127.md). Схема 32 применена по отдельному разрешению; релиз не подтверждён данным проходом.
- Последний функциональный блок BUG-F54: **DONE** (28.11 scalar-проход + повторная приёмка маршрутов, см. ниже), ранее — [коммит 7a222c2](https://github.com/xBonkx/CsharpGMC/commit/7a222c2790d3781abe67a9f361a993b51dd5fc77).
- Последний дизайн: GRID-19/SUM-VIS-01 CODE READY, QA-09/19 BLOCKED на Windows captures, 246/253 CODE READY. [Отчёт](docs/DESIGN_PASS_20261128_WIDTHS_SUMMARY_CLOSURE.md).
- 28–29.11.2026 — дизайн-проход закрытия D-вопросов (zcode-sess-design, бронь d8a63aad): все 9 D-строк очереди закрыты и удалены — GRID-28.01-02 (инвентарь ~70 DataGrid, семантика cell/row + исключения, пакетные команды FullRow сохранены), HOME-03.01 (UiH1Size 24 DIP/700/wrap; tracking −0.3px — документированное ограничение), SPACE-15.01-02 (caps WorkArea 16 окнам, ConfirmDeleteDialog SizeToContent), SPACE-18.01-02 (пересечений с App.xaml нет, 3 мёртвых стиля удалены), SPACE-19.01-02 (LayoutDialogMargin, инвентарь 850 margin + ~200 .cs Thickness read-only). Только XAML (19 файлов, коммит 0174fa25, +22/−186); dotnet build 0 ошибок; xUnit/DPI/кадры не запускались (изменений .cs/БД нет). Родители GRID-28/HOME-03/SPACE-15/18/19 остаются PARTIAL: A-приёмки + код-запросы в DESIGN_NOTES. [Отчёт](docs/DESIGN_PASS_20261129_SEMANTICS_CLOSURE.md).
- 28.11.2026 — реорганизация документации и кооперативная бронь задач; код, БД, релиз и статусы задач не изменены. [Правила](AGENTS.md), [карта](docs/README.md).

- 28.11.2026 — DEMO-08.01 PARTIAL: добавлен журнал тестового автообновления и диагностика smoke-кода 1. [Подробности](docs/DEMO_MODE_TODO_20261128.md#demo-08--автообновление-c-test). Исходный отказ v1.11.0 не воспроизведён; сборка, релиз и БД не менялись.

- 28.11.2026 — T-проход (zcode-sess-fd0f): build 0 ошибок, **3059/3059** тестов (было 3022/3057 — отремонтированы 35 source-паттерн регрессий: 33 устаревших теста + 2 дефекта, вкл. Opacity-затемнение в EstimateSummaryPanel.xaml), офлайн App.Checks зелёные после правок харнесса, штатный smoke продакшн-EXE exit 0, валидаторы local-qa 24/24. AGR-11.01 и BUG-F54.06 закрыты и удалены из очереди; CTX-049.01/DEMO-07.01/PERF-053.05 — PARTIAL. [Подробности](docs/T_TEST_PASS_20261128.md).

- 28.11.2026 — scalar-проход (zcode-sess-f54x): **BUG-F54 → DONE**; строки BUG-F54.01–05/07 и CTX-049.01 закрыты и удалены из очереди. Все scalar ExecuteScalar строго (NULL не →0: applock/OUTPUT/COUNT/CASE), reader-инвентарь закрыт по схеме демо-БД, границы Int32 проверяемые (RequireInt32). Build 0 ошибок, **3096/3096** тестов (+37), SQL Pilot живьём: SQL-режим App.Checks exit 0 (2504 строк, фильтр, 1366/1920, smoke OK) + вечером — повторная полная приёмка всех шести маршрутов (справочники/ПТО/паспорта/металл/Excel непустыми данными через штатные API, корзина/undo чтением; каждый с отказ-гвардом, rev демо-БД не изменился). Корень гонки SQL-харнесса — отсутствие DispatcherSynchronizationContext без Application.Run (правка в тестовом харнессе, продакшн не менялся). [Отчёт](docs/BUG_F54_SCALAR_PASS_20261128.md).

## История до реорганизации

[Полный CURRENT_TASK до сокращения](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md) — неизменённый снимок со всеми отчётами, результатами и доказательствами. Локально: `git show c00f4c32d3faea3e9db33a31ca07c0207df3a376:CURRENT_TASK.md`. Старые заголовки ниже сохранены для совместимости ссылок. Новые записи — краткий итог со ссылкой на единственный подробный отчёт; не копировать содержимое профильных TODO.

## 27.11.2026 — самостоятельные договоры, схема 32 применена; релиз не опубликован
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L7)

# 27.11.2026 — правила владельца и текущая работа
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L409)

# 27.11.2026 — живая прокрутка, Windows-проверки main и AGR-10
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L421)

# Текущий чекпоинт — 25.11.2026
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L446)

## 27.11.2026 — AGR-04/10: данные договора отдельно от объекта
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L448)

## 27.11.2026 — GRID: общий вход в редактирование таблиц
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L452)

## 27.11.2026 — GRID-27/QA-15: видимость текста редактора
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L456)

## 27.11.2026 — AGR-02: подписи режима КП/договоров
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L460)

## 27.11.2026 — отложенная инициатива, не смена текущего приоритета
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L464)

## 27.11.2026 — AGR: встроенная страница реквизитов договора
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L470)

## 27.11.2026 — AGR: реквизиты импорта, ДС, копии и конвертация
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L474)

## 27.11.2026 — AGR: редактор, импорт и область КС
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L478)

## 27.11.2026 — AGR: аудит связей и смежные экраны
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L482)

## 27.11.2026 — AGR: реквизиты и создание приложений
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L486)

## 27.11.2026 — AGR: левый сайдбар и представление приложений
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L490)

## 27.11.2026 — договор → приложения и ДС (ТЗ владельца)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L494)

## 27.11.2026 — SQL-синтаксис истории допусков и приказов
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L498)

## 27.11.2026 — вкладка справочника людей и автомобилей
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L502)

## 27.11.2026 — редактирование людей и автомобилей в ячейках
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L506)

## 27.11.2026 — SQL BIT и целочисленные флаги
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L510)

## 27.11.2026 — SQL-типизация общих читателей и писем
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L514)

## 27.11.2026 — редактор писем и предпросмотр ВОР
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L518)

## 27.11.2026 — паспорта: запись, пакетные операции и закрытие
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L522)

## 27.11.2026 — ПТО: комплекты, карточки и шаблоны
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L526)

## 27.11.2026 — сотрудники и допуски: запись, импорт и выгрузка
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L530)

## 27.11.2026 — ПТО: редактор АОСР и выпуск документов
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L534)

## 27.11.2026 — ПТО: библиотека сертификатов и архив
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L538)

## 27.11.2026 — Excel-реестр металла: ленивая загрузка и сохранение
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L542)

## 27.11.2026 — склад металла: остатки, журнал и Excel
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L546)

## 27.11.2026 — производительность распределения этапов
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L550)

## 27.11.2026 — фильтр закупок и геометрия выдачи
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L554)

## 27.11.2026 — исправление переноса строк и кнопок группировки/сортировки
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L558)

## 27.11.2026 — загрузка и архив паспортов вентиляции
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L562)

## 27.11.2026 — отзывчивость архива писем
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L566)

## 27.11.2026 — производительность и жизненный цикл предпросмотра Excel
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L570)

## 27.11.2026 — единая безопасная публикация экспортов КС и договора
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L574)

## 27.11.2026 — нумерация ВОР/ДНС и пакетная выгрузка КС
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L578)

## 27.11.2026 — прокрутка основных таблиц и измерение ширин сводной
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L582)

## 27.11.2026 — числовые ячейки, разворачивание групп и фокус сметы
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L586)

## 27.11.2026 — кэш ячеек и столбцов этапов
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L590)

## 27.11.2026 — группировка сметы и фильтр этапов
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L594)

## 27.11.2026 — поиск и точечное обновление выдачи и закупок
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L598)

## 27.11.2026 — индексы материалов и поиск закупок
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L602)

## 27.11.2026 — история спецификаций и живой Excel металла
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L606)

## 27.11.2026 — безопасное сохранение выгрузок
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L610)

## 27.11.2026 — сводная: фильтры, наценка и Excel
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L614)

## 27.11.2026 — нумерация страниц ВОР
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L618)

## 27.11.2026 — контекстные меню и перенос цен
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L622)

## 27.11.2026 — визуальные роли дерева документов
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L626)

## 27.11.2026 — отображение монтажных цен и ПНР
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L630)

## 27.11.2026 — офлайн визуальная диагностика
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L634)

## 27.11.2026 — шрифты, масштаб и инструменты таблиц
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L638)

## 27.11.2026 — вкладка Итоги КП
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L642)

## 27.11.2026 — главная и карточки документов
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L646)

## 27.11.2026 — адаптивная рабочая область
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L650)

## 27.11.2026 — формат количества в Excel ВОР
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L654)

## 27.11.2026 — вкладки вспомогательных модулей и панели
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L658)

## 27.11.2026 — компактные формы и прокрутка диалогов
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L662)

## 27.11.2026 — поля, выпадающие списки и меню
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L666)

## 27.11.2026 — общие таблицы, шапки и бегунки
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L670)

## 27.11.2026 — состояния кнопок, вкладок и выделения
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L674)

## 27.11.2026 — общая типографика рабочих экранов
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L678)

## 27.11.2026 — общие метрики и отступы рабочих страниц
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L682)

## 27.11.2026 — удаление нижней строки состояния (STATUS-01/02)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L686)

## 27.11.2026 — визуальный паритет допусков и диалогов закупок (WIN-17/32)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L690)

## 27.11.2026 — визуальный паритет основных рабочих экранов (WIN-01–05)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L694)

## 27.11.2026 — визуальный паритет этапов и диалогов сметы (WIN-27–31)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L698)

## 27.11.2026 — визуальный паритет КС-2 / КС-3 (WIN-08–12)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L702)

## 27.11.2026 — визуальный паритет предпросмотров Excel (WIN-22/23)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L708)

## 27.11.2026 — визуальный паритет справочников и поиска (WIN-21/33)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L712)

## 27.11.2026 — визуальный паритет склада и учёта металла (WIN-19/20)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L716)

## 27.11.2026 — визуальный паритет ПТО и ВОР (WIN-13/14)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L720)

## 27.11.2026 — визуальный паритет писем, паспортов и приказов (WIN-15/16/18)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L724)

## 27.11.2026 — визуальный паритет журнала и корзины (WIN-24/25)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L728)

## 27.11.2026 — SQL-область и отмена чтения списка ПТО (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L732)

## 27.11.2026 — загрузка и фильтрация ПТО (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L736)

## 27.11.2026 — фоновая выдача приказов (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L740)

## 27.11.2026 — открытие ранее созданных приказов (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L744)

## 27.11.2026 — начальная загрузка приказов (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L748)

## 27.11.2026 — фоновое обновление сотрудников и допусков (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L752)

## 27.11.2026 — отмена SQL-чтения сотрудников и допусков (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L756)

## 27.11.2026 — начальная загрузка сотрудников и допусков (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L760)

## 27.11.2026 — отмена чтения основного справочника (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L764)

## 27.11.2026 — чтение страниц справочника (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L768)

## 27.11.2026 — открытие сетевых вложений (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L772)

## 27.11.2026 — загрузка вложений (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L776)

## 27.11.2026 — оптимизация алгоритма сравнения (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L780)

## 27.11.2026 — фоновая проекция сравнения (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L784)

## 27.11.2026 — фоновое сравнение документов (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L788)

## 27.11.2026 — фоновая загрузка корзины (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L792)

## 27.11.2026 — фоновая загрузка журнала изменений (PERF-047)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L796)

## 27.11.2026 — значок превышения в таблице выдачи (SHIP-VIS-05)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L800)

## 26.11.2026 — палитра подсистем выдачи (SHIP-VIS-05)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L804)

## 26.11.2026 — группы и подсказки таблицы выдачи (SHIP-VIS-05)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L808)

## 26.11.2026 — единое предупреждение о складском остатке (SHIP-VIS-05)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L812)

## 26.11.2026 — оформление экспорта выдачи (SHIP-VIS-02)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L816)

## 26.11.2026 — таблица и итоги выдачи (SHIP-VIS-05)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L820)

## 26.11.2026 — сегменты фильтров выдачи
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L824)

## 26.11.2026 — карточки ведомостей
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L828)

## 26.11.2026 — динамические шаги выдачи
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L832)

## 26.11.2026 — поиск и панель выдачи
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L836)

## 26.11.2026 — цех и выделение в закупках
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L840)

## 26.11.2026 — история движений закупок
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L844)

## 26.11.2026 — статусы и сроки закупок
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L848)

## 26.11.2026 — геометрия таблицы закупок
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L852)

## 26.11.2026 — фильтры закупок
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L856)

## 26.11.2026 — защищённое сохранение процента СМР
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L860)

## 26.11.2026 — отображение сохранённого процента СМР
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L864)

## 26.11.2026 — подсказки СМР и скидка в итогах
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L868)

## 26.11.2026 — типографика денежных полей и НДС
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L872)

## 26.11.2026 — адаптивная раскладка карточек итогов
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L876)

## 26.11.2026 — геометрия строк и палитра сметы
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L880)

## 26.11.2026 — нижняя кнопка и сворачивание итогов
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L884)

## 26.11.2026 — скругление таблицы сметы
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L888)

## 26.11.2026 — ручная ширина столбцов
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L892)

## 26.11.2026 — заливка 30-alpha подсветки ссылок формул КС-2 (main, база 137bc05)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L896)

## 26.11.2026 — приёмка C# на Windows, срез f75d466
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L912)

## 26.11.2026 — CTX-021/CTX-026, переключение переноса названий (ChatGPT / main, PARTIAL)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L926)

## 26.11.2026 — Релиз v1.0.4 (локальная сборка, GitHub Actions минуты исчерпаны)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L932)

## 25.11.2026 — App.Checks: восстановлена сборка, offline-режим и CI-гейт
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L950)

## MIG-REL-01, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L956)

## MIG-REL-01, DeepSeek/local acceptance — PARTIAL (25.11.2026)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L965)

## MIG-REL-01, ChatGPT / main — PARTIAL (25.11.2026)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L978)

## 25.11.2026 — локальный запуск v0.1.5 на настольной копии
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L981)

## 25.11.2026 — повторная проверка перед рабочим подключением
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L984)

## 25.11.2026 — этапы, два клиента, КС ДНС и карточки, v0.1.5
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L987)

## 25.11.2026 — рабочий запуск C# на DIMA, v0.1.5
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L994)

## 25.11.2026 — сборка v0.1.6 и развёртывание на других ПК, PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1000)

### Результат публикации v0.1.6 — 25.11.2026
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1004)

### 25.11.2026 — TLS и доступ из офисной сети, v1.0.0
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1008)

### Результат выпуска v1.0.0 — 25.11.2026
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1012)

## 25.11.2026 — проверка копеечного паритета Python/C#, исправление после v1.0.0, не опубликовано
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1015)

## 25.11.2026 — номер сборки в заголовке, подготовка v1.0.1
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1023)

### Результат выпуска v1.0.1 — 25.11.2026
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1026)

## 25.11.2026 — SUM-VIS-01, ChatGPT / main — PARTIAL (только аудит)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1030)

## SUM-VIS-01 — блок 1, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1034)

## SUM-VIS-01 — блок 2, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1038)

## SUM-VIS-01 — блок 3, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1042)

## CTX-022 — блок 15, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1046)

## SUM-VIS-01 — блок 4, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1050)

## CTX-022 — блок 16, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1054)

## SUM-VIS-01 — блок 5, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1058)

## SUM-VIS-01 — блок 6, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1062)

## SUM-VIS-01 — блок 7а, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1066)

## SUM-VIS-01 — блок 7б, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1070)

## SUM-VIS-01 — блок 8, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1074)

## SUM-VIS-01 — блок 9, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1078)

## CTX-020 — ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1081)

## CTX-032 — ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1084)

## STATUS-03 — ChatGPT / fix/status-03-estimate-status — CODE READY
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1087)

## HOME-34/35/36 — ChatGPT / fix/director-footer-navigation — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1090)

## HDR-19 — ChatGPT / fix/hdr-19-compact-estimate-header — CODE READY
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1093)

## CTX-032 — экспорт всех актов — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1096)

## Интеграция PR #2–7
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1101)

## CTX-030 — ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1104)

## CTX-029 — ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1107)

## CTX-012 — ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1110)

## CTX-020/029/032 — точное происхождение SVG — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1113)

## CTX-025 — ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1116)

## CTX-026 — ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1119)

## PERF-023 — ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1122)

## PARITY-IMPORT-01 — ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1125)

## PARITY-REVIT-01 — ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1128)

## CTX-018 — ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1131)

## PARITY-KS-FORMULA-01 — ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1134)

## PERF-022 — ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1137)

## PERF-022 — финансовая справка КС-3 — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1140)

## OFFICE-UPDATE-01 — 26.11.2026, Codex / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1143)

## 26.11.2026 — OFFICE-UPDATE-01 / OFFICE-SQL-01, Codex / main
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1147)

## 26.11.2026 — OFFICE-SQL-01, проверка без PowerShell
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1162)

## 26.11.2026 — ревизия порта, блок скидок и экспорта КП/КС
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1170)

## 26.11.2026 — публикация коммитов порта
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1182)

## 26.11.2026 — ревизия порта, блок импорта КП, ДС и Revit
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1186)

## 26.11.2026 — удаление окна профилей SQL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1197)

## 26.11.2026 — формулы объёмов КС-2: приёмка завершена
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1210)

## 26.11.2026 — PARITY-IMPORT-01, приёмка завершена
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1220)

## 26.11.2026 — PARITY-STAGES-01, SQL-приёмка завершена
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1233)

## 26.11.2026 — PERF-040, ChatGPT / main — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1244)

## 26.11.2026 — PERF-040, WPF snapshot index — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1248)

## 26.11.2026 — PERF-040, visible row presentation — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1252)

## 26.11.2026 — PERF-041, indexed document projection — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1256)

## 26.11.2026 — PERF-030, batched procurement snapshot — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1260)

## 26.11.2026 — PERF-032, procurement counters and view planning — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1264)

## 26.11.2026 — PERF-033, incremental procurement viewport — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1268)

## 26.11.2026 — PERF-017, revision-only estimate tab return — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1272)

## 26.11.2026 — PERF-017, revision-only procurement tab return — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1276)

## 26.11.2026 — PERF-017, revision-only shipments tab return — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1280)

## 26.11.2026 — PERF-017, signed act tab return — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1284)

## 26.11.2026 — контроль размера исходников и архитектуры
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1288)

## 26.11.2026 — PERF-021, indexed KS summary projection — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1292)

## 26.11.2026 — PERF-019, KS formula reference indexing — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1296)

## 26.11.2026 — PERF-019, sparse KS formula highlighting — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1300)

## 26.11.2026 — Сводная по ширине viewport (SUM-VIS-01 / C01, C03, C10, B05)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1304)

## 26.11.2026 — PERF-019, KS row change notifications — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1308)

## 26.11.2026 — PERF-026, embedded cost summary filtering — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1312)

## 26.11.2026 — OFFICE-UPDATE-01: автоматический безопасный перезапуск (PARTIAL)
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1316)

## 26.11.2026 — PERF-026, cost summary search index — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1322)

## 26.11.2026 — PERF-026, unchanged summary grid and sparse restore — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1326)

## 26.11.2026 — PERF-025, batched cost-summary SQL read — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1330)

## 26.11.2026 — PERF-025, shared revision-fenced cost data — PARTIAL
[Историческая запись](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md#L1334)

