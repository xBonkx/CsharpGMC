# Исполнимая очередь TODO — 28.11.2026

По указанию владельца: разбить глобальные задачи на конечные рабочие пакеты и разделить написание кода, тесты, приёмку и отложенные улучшения. Срез родительского реестра: `deb35112530602dff2708ca5c264a34fc3849395`. Источник статусов и полных критериев — [MASTER_TODO](MASTER_TODO.md) и связанные профильные TODO. Эта очередь уточняет объём, **не добавляет требования и не заменяет исходные критерии**.

## Как работать

- `C` — CODE TODO (функциональный код или конечная статическая сверка/инвентарь); `D` — CODE TODO (дизайн); `T` — TEST TODO (реальный автоматизированный прогон); `A` — ACCEPTANCE TODO (Windows/WPF/SQL Pilot/физические кадры/замеры); `B` — BACKLOG (отложенные контрагенты, не блокируют текущий релиз). Для смешанных C/D задач брать только соответствующую часть. Архитектурная очередь P1/P2 не вытесняет P0 и перенос Python/SQL.
- Начальные статусы C/D/T/A — UNVERIFIED: есть значительный уже написанный код; сначала сверить текущий main и прежние доказательства, а не переписывать. QA-09/19 A — BLOCKED до реальных Windows кадров; B — NOT STARTED и отложено. Это не переоценка статусов родительских ID.
- Взять конкретный ID, записать агент/ветку/дату, фиксировать конечный набор файлов/маршрутов (для полного инвентаря — pinned SHA, критерий включения и конечный список). Проверить критерий и исходный TODO. Если недостающее найдено — исправить и добавить адресный тест; если уже реализовано — приложить доказательства и пометить как ранее реализованное, не считать новым кодом.
- C/D: CODE READY только при выполненном критерии реализации и указанном доказательстве (SHA/путь/тест); частичный охват остаётся PARTIAL, объективная невозможность BLOCKED с причиной. T/A: DONE только после фактического прогона с результатом; не выдавать тестовую заготовку за пройденный тест. Общая сборка/набор тестов переиспользуются по тому же SHA, отдельные T добавлять только при реальном пробеле, не повторять одинаковые проверки.
- Родитель сохраняет исходный ID и статус; повышать до CODE READY лишь при выполнении всех исходных критериев реализации, даже если его C/D подзадачи готовы; исключительно приёмочные родительские ID закрывать только после реальных доказательств. DONE после всех обязательных T/A и исходной приёмки. Не закрывать родителя автоматически и не считать одновременно его и дочерние ID в прогрессе.
- Приоритет владельца: пакеты минимум 5 **новых** CODE READY C/D подзадач за проход, если объективно возможно; отдельно сообщать ранее реализованные, T/A DONE, родительские повышения, остаток и блокеры. Нельзя создавать искусственные мелкие ID ради статистики, повышать статус по совпадению имени файла или считать разбиение выполнением. Для команды «не дизайн» исключить D и визуальные A, брать C/T и функциональные A.
- Пересечения: CTX-047/048 — единый источник инвентаря для остальных CTX; CTX-008/009/034 — одна проверка мутации, один патч, ссылки из нескольких ID; PERF-053/054/058 — единые raw evidence и baseline; AGR-11 и MIG-REL-01 используют подтверждённые результаты профильных A; QA-09/19 и CTX-049 используют общие кадры. Если один критерий полностью совпадает — отметить DUPLICATE со ссылкой, не делать и не считать дважды. Исторический baseline, которого нет, нельзя выдумывать. Изменение требований/бизнес-правил/DDL или расширение BIGINT по всей программе требует отдельного решения, если не следует из исходного критерия.
- В финальном рабочем коммите обновлять строки здесь (статус, бронь, короткая ссылка на evidence и счётчики), статус родителя/сводку в MASTER_TODO при изменении, подробности ровно в одном профильном TODO/отчёте и короткий CURRENT_TASK. Не дублировать отчёт; отдельный коммит брони меняет только этот файл. Родительские исторические 418 ID и дополнительные реестры не суммировать с этой очередью. Рабочие БД/Cutover не трогать; только разрешённые изолированные GmcSmetaPilot_* (demo — отдельно строго разрешённый локальный тестовый режим). Релиз отдельно согласовывается.

## Резервирование задач параллельными агентами

**До анализа/изменения кода — отдельный подтверждённый коммит брони в main.** Для старых CODE READY действует отдельная [очередь приёмки](ACCEPTANCE_TODO.md). Перед любой бронью сверять обе очереди: одинаковый родитель/дочерние ID, файлы, зависимости и эксклюзивные ресурсы (БД, GUI, измерения) не могут быть одновременно заняты разными агентами. Не дублировать задачи; при необходимости согласовать передачу и освободить прежнюю бронь. Колонка «Бронь» каждой очереди — источник её занятости. `☐` свободно; `☑` занято (ID агента/сессии, UTC, ветка, ожидаемые файлы/модули, прежний статус). При брони статус строки IN PROGRESS; прежний статус обязателен для восстановления. Пример формата (не реальная бронь): `☑ agent-abc / 2026-11-28T12:00Z / main / src/... / prior=PARTIAL`.

1. Fetch свежего main, выбрать свободные ID, сверить зависимости и области файлов с чужими активными бронями. Не брать совпадающий ID, пересекающийся файловый scope или конфликтующую миграцию/ресурс. Для совместимого read-only аудита записать read-only и не менять файлы без обновления брони.
2. Записать бронь всех выбранных ID в строках этой таблицы, обновить статус и счётчик активных броней. Коммитить **только этот файл** отдельным коммитом, без изменения кода; push fast-forward с проверкой ожидаемого SHA (GitHub API expected_sha, без force; локально обычный push после fetch). Если head изменился/публикация отклонена, заново прочитать свежие брони, пересобрать свой набор и повторить. Не перетирать чужую бронь. Не начинать работу до повторного чтения main с подтверждением своих записей.
3. При расширении scope/набора ID сначала отдельным коммитом обновить бронь. Перед финальным коммитом снова проверить владение и свежий main; объединить чужие изменения, не force push.
4. Финальным коммитом кода/доказательств снять только свои `☑` (вернуть `☐`), записать итоговый статус, короткое evidence и пересчитать счётчики. Если прервались без результата — отдельным коммитом снять свою бронь и восстановить прежний статус либо PARTIAL/BLOCKED с причиной. Продолжение того же агента использует прежний уникальный ID сессии. Не снимать чужую бронь по возрасту автоматически: только после подтверждения её владельцем/координатором, с записью причины. При длительном блоке обновить время/чекпоинт.
5. Если доступен только read-only GitHub, нет прав записи или бронь не подтверждена, не менять код по этим ID. Сообщить блокер. Галочки — кооперативная блокировка через Git, а не техническая блокировка локальных файлов.

Счётчик занятости (обновлять по строкам): **17 из 168** (28.11: AGR-11.01, BUG-F54.06 закрыты ранее — [T_TEST_PASS_20261128](T_TEST_PASS_20261128.md); BUG-F54.01–05, BUG-F54.07, CTX-049.01 закрыты и удалены — [BUG_F54_SCALAR_PASS_20261128](BUG_F54_SCALAR_PASS_20261128.md), BUG-F54.07 закрыт DONE после повторной полной живой приёмки маршрутов; 28–29.11: все 9 D-строк «Дизайна» закрыты и удалены — GRID-28.01-02, HOME-03.01, SPACE-15.01-02, SPACE-18.01-02, SPACE-19.01-02 — [DESIGN_PASS_20261129_SEMANTICS_CLOSURE](DESIGN_PASS_20261129_SEMANTICS_CLOSURE.md)). 29.11: CTX-036/038/039/040/042/046.01 и AGR-02.01 закрыты сверкой ранее реализованного, ARCH-SIZE-03.04/06.01 BLOCKED (контракты source-тестов, правка тестов вне дизайн-линии) — [DESIGN_PASS_20261129_CTX_MENUS_DIALOGS](DESIGN_PASS_20261129_CTX_MENUS_DIALOGS.md). Свободно не означает готово к реализации: учитывать статус, приоритет, зависимости, блокеры и BACKLOG.

## Текущий учёт

| Тип | Количество строк |
|---|---:|
| CODE TODO (C, функциональные) | 95 |
| CODE TODO (D, дизайн) | 7 |
| TEST TODO (T) | 4 |
| ACCEPTANCE TODO (A) | 53 |
| BACKLOG (B) | 9 |
| Всего | 168 |

Родительских направлений: 74. Статусы на момент декомпозиции: 181 UNVERIFIED, 3 BLOCKED, 9 NOT STARTED, 0 новых CODE READY/DONE. После каждого прохода обновлять фактический учёт; эти стартовые числа не являются оценкой количества оставшихся изменений кода.

## Функциональные дефекты

Исходный TODO: [FUNCTIONAL_BUG_TODO_20261124.md](FUNCTIONAL_BUG_TODO_20261124.md).

Все строки этого раздела (BUG-F54.01–07) закрыты 28.11.2026 и удалены по правилу владельца: BUG-F54.01–05 и CTX-049.01 — CODE READY/DONE в scalar-проходе; BUG-F54.07 — DONE после повторной полной живой приёмки всех шести маршрутов (справочники, ПТО, паспорта, металл, Excel, корзина/undo) с отказ-гвардами, по требованию владельца «полное закрытие». Доказательства — [BUG_F54_SCALAR_PASS_20261128](BUG_F54_SCALAR_PASS_20261128.md) (дополнение вечера 28.11).

## Производительность

Исходный TODO: [PERFORMANCE_TODO_20261124.md](PERFORMANCE_TODO_20261124.md).

| ID | Приоритет | Тип | Конкретный пакет | Критерий окончания | Статус | Evidence / агент | Бронь |
|---|---|---|---|---|---|---|---|
| PERF-007.01 | P1 | C | Стартовые DTO объектов/документов | Пакетный набор полей, архивный scope, независимая сверка DTO; устранить только подтверждённые лишние чтения. | CODE READY | [PERF_007_014_SPARSE_PASS_20261129](PERF_007_014_SPARSE_PASS_20261129.md) | ☐ |
| PERF-007.02 | P1 | C | Карточки выбранного объекта | Не читать скрытые необязательные детали; доказать корректность итогов и отмены. | CODE READY | [PERF_007_014_SPARSE_PASS_20261129](PERF_007_014_SPARSE_PASS_20261129.md) | ☐ |
| PERF-007.03 | P1 | C | Post-write/F5 | Тяжёлое чтение не блокирует UI; обязательный readback и защита ревизии сохранены. | CODE READY | [PERF_007_014_SPARSE_PASS_20261129](PERF_007_014_SPARSE_PASS_20261129.md) | ☐ |
| PERF-007.04 | P1 | A | Старт и открытие на 10/50/200 документах | Фактические планы, payload, времена и архивный scope на Pilot. | IN PROGRESS | — | ☑ zcode-sess-perf714 / 2026-11-28T14:40Z / main / tests/GmcSmeta.App.Checks, docs/PERF_007_014_SPARSE_PASS_20261129.md / prior=UNVERIFIED |
| PERF-014.01 | P1 | C | Классификация полей сметы | Явный whitelist безопасного sparse readback и fallback для остальных полей. | CODE READY | [PERF_007_014_SPARSE_PASS_20261129](PERF_007_014_SPARSE_PASS_20261129.md) | ☐ |
| PERF-014.02 | P1 | C | Одиночное изменение | Затронутые строки и авторитетные итоги совпадают с полным reload; revision fence. | CODE READY | [PERF_007_014_SPARSE_PASS_20261129](PERF_007_014_SPARSE_PASS_20261129.md) | ☐ |
| PERF-014.03 | P1 | C | Пакет, формулы, вставка, удаление, Undo | Fallback корректен, FIFO и выбор сохранены; не оптимизировать структурные операции без доказательств. | CODE READY | [PERF_007_014_SPARSE_PASS_20261129](PERF_007_014_SPARSE_PASS_20261129.md) | ☐ |
| PERF-014.04 | P1 | A | Нагрузочная приёмка sparse/full | 300/2500/10000/25000 строк, два клиента, время и совпадение итогов. | IN PROGRESS | — | ☑ zcode-sess-perf714 / 2026-11-28T14:40Z / main / tests/GmcSmeta.App.Checks, docs/PERF_007_014_SPARSE_PASS_20261129.md / prior=UNVERIFIED |
| PERF-042.01 | P1 | C | Инвентарь всех DataGrid | Каждая XAML/динамическая таблица имеет bounded host, row/column virtualization, Recycling или объяснённое исключение. | IN PROGRESS | — | ☑ chat-grid-1454 / 2026-11-28T14:54Z / main / DataGrid XAML and dynamic grid virtualization inventory, targeted grid source tests; no SQL/GUI or keyboard changes / prior=UNVERIFIED |
| PERF-042.02 | P1 | C | Группировка и динамические таблицы | Устранены подтверждённые бесконечные размеры и отключение виртуализации; targeted fixture. | IN PROGRESS | — | ☑ chat-grid-1454 / 2026-11-28T14:54Z / main / DataGrid XAML and dynamic grid virtualization inventory, targeted grid source tests; no SQL/GUI or keyboard changes / prior=UNVERIFIED |
| PERF-042.03 | P1 | A | Реальный счётчик контейнеров | 300/2500/10000/25000 строк, группы, DPI; результат по таблицам из инвентаря. | UNVERIFIED | — | ☐ |
| PERF-044.01 | P2 | C | Ячейки, форматирование и ресурсы | Устранить подтверждённые повторные вычисления и freeze применимых ресурсов; без изменения внешнего вида. | UNVERIFIED | — | ☐ |
| PERF-044.02 | P2 | C | Layout и binding | Исправить доказанные лишние контейнеры, UpdateLayout и ошибки binding; дизайн согласуется отдельно. | UNVERIFIED | — | ☐ |
| PERF-044.03 | P2 | A | Профиль до/после | Visual tree, binding, форматирование, layout; если дефекта нет — фиксировать результат без лишнего рефакторинга. | UNVERIFIED | — | ☐ |
| PERF-047.01 | P2 | C | История и корзина | Проверить загрузку, фильтр, отмену, restore/forget; актуальная публикация и защищённый readback. | CODE READY | 29.11: [one-connection history snapshot and revision fence](PERFORMANCE_TODO_20261124.md) | ☐ |
| PERF-047.02 | P2 | C | Каталог, справочники и допуски | Список, поиск, история, импорт, закрытие; нет устаревшей публикации. | UNVERIFIED | — | ☐ |
| PERF-047.03 | P2 | C | Приказы и письма | Загрузка, поиск, экспорт, закрытие; подтверждение записи/файла до readback. | UNVERIFIED | — | ☐ |
| PERF-047.04 | P2 | C | Паспорта и ПТО | Список, карточка, сертификаты/ВОР, выпуск, архив; worker и защищённый readback. | UNVERIFIED | — | ☐ |
| PERF-047.05 | P2 | C | Склад и учёт металла | Балансы, журнал, листы, импорт/экспорт; scope, защита записи, ограниченные проекции. | UNVERIFIED | — | ☐ |
| PERF-047.06 | P2 | C | Вложения и сравнение | Загрузка, UNC, фильтр, экспорт, отмена; устаревший результат не публикуется. | CODE READY | 29.11: [file open and export lifecycle](PERFORMANCE_TODO_20261124.md) | ☐ |
| PERF-047.07 | P2 | C | Этапы, глобальный поиск, Excel preview | Ограниченные проекции, поиск/отмена, защита от stale result. | UNVERIFIED | — | ☐ |
| PERF-047.08 | P2 | A | Приёмка вспомогательных модулей | Для семи пакетов — 0/3000 записей, закрытие, ошибка, отмена, конфликт, время и корректность. | UNVERIFIED | — | ☐ |
| PERF-050.01 | P1 | C | Workspace/Finance/Totals/TagRules | Инвентарь запросов и устранение доказанных дублей/SELECT * с сохранением scope. | UNVERIFIED | — | ☐ |
| PERF-050.02 | P1 | C | Смета, себестоимость и этапы | Инвентарь и устранение доказанных повторных чтений; деньги неизменны. | UNVERIFIED | — | ☐ |
| PERF-050.03 | P1 | C | КС, закупки и выдача | Проекции и повторные запросы проверены; независимая сверка результата. | UNVERIFIED | — | ☐ |
| PERF-050.04 | P1 | C | Редкие справочники | Компании, теги, каталог: ключ кэша база+ревизия или обоснование отказа; нет stale data. | UNVERIFIED | — | ☐ |
| PERF-050.05 | P1 | C | Прочие вспомогательные SQL-модули | Каждый оставшийся файл классифицирован; исправлены только доказанные излишки. | UNVERIFIED | — | ☐ |
| PERF-050.06 | P1 | A | Измерение SQL до/после | Планы, IO, roundtrips и результаты на Pilot; индекс/DDL только по отдельному разрешению. | UNVERIFIED | — | ☐ |
| PERF-053.01 | P0 | C | RemoteSqlBench: SQL producer | Сценарии, outcomes, таймауты, точные monotonic raw данные. | UNVERIFIED | — | ☐ |
| PERF-053.02 | P0 | C | WPF producer | First render/editable ready, CPU/memory, SQL count, cold/warm связаны с run ID и raw данными. | UNVERIFIED | — | ☐ |
| PERF-053.03 | P0 | C | Python producer | Сопоставимые сценарии и метаданные; эталонный код не менять без разрешения. | UNVERIFIED | — | ☐ |
| PERF-053.04 | P0 | C | Запись и retention | Отсутствующие сценарии записи, отмены и корректности включены в общий контракт evidence. | UNVERIFIED | — | ☐ |
| PERF-053.05 | P0 | T | Producer, валидаторы и wrapper | Фактический запуск целевых тестов; неполные данные отклоняются. | PARTIAL | [T_TEST_PASS_20261128](T_TEST_PASS_20261128.md): валидаторы 24/24 OK; producers 053.01–04 не реализованы | ☐ |
| PERF-054.01 | P0 | A | Фиксация baseline окружения | SHA обеих сборок, Release, компьютер, профиль, топология, копия; не подменять исторический baseline. | UNVERIFIED | — | ☐ |
| PERF-054.02 | P0 | A | Baseline размеры/структура | 300/2500/10000/25000 строк, глубины систем и этапов, 10/50/200 документов и архивы, cold/warm. | UNVERIFIED | — | ☐ |
| PERF-054.03 | P0 | A | Репрезентативные данные | Одинаковая разрешённая копия, результаты и ссылки raw; отсутствующий до-правок baseline обозначен явно. | UNVERIFIED | — | ☐ |
| PERF-056.01 | P0 | C | Сценарии отказа и диагностика | Проверить наличие управляемых offline/timeout/disconnect и stale-result тестов; недостающий harness добавить без изменения рабочих правил. | UNVERIFIED | — | ☐ |
| PERF-056.02 | P0 | A | Offline и плохая сеть | GitHub/SQL недоступны, плохой Wi-Fi, задержка/обрыв, A→B→C; лог и корректность. | UNVERIFIED | — | ☐ |
| PERF-056.03 | P0 | A | Три клиента | Объёмы/цены/директор и параллельные КС/закупки/выдача; конфликт, неизвестный commit, отсутствие потерь. | UNVERIFIED | — | ☐ |
| PERF-058.01 | P1 | A | Сравнение с baseline | Повторить согласованные сценарии PERF-054; p50/p95/p99 и UI stalls, правильность результатов. | UNVERIFIED | — | ☐ |
| PERF-058.02 | P1 | A | Соседние операции и retention | Проверить memory/CPU, SQL read/write, регрессии; решения и raw evidence зафиксированы. | UNVERIFIED | — | ☐ |

## Контекстные меню

Исходный TODO: [CONTEXT_MENU_TODO_20261124.md](CONTEXT_MENU_TODO_20261124.md).

| ID | Приоритет | Тип | Конкретный пакет | Критерий окончания | Статус | Evidence / агент | Бронь |
|---|---|---|---|---|---|---|---|
| CTX-008.01 | P0 | C | Объекты, договоры, архив и корзина | Перед мутацией точный ID, revision, права, busy, статус; SQL транзакционный повтор guard. | UNVERIFIED | — | ☐ |
| CTX-008.02 | P0 | C | Смета, материалы, сводная, этапы | Команды записи и пакетный выбор: те же guards на открытии и исполнении, включая подпись. | UNVERIFIED | — | ☐ |
| CTX-008.03 | P0 | C | КС и вне сметы | Все мутации актов и доппозиций имеют единый scope/lock/sign/revision; исключения явно описаны. | UNVERIFIED | — | ☐ |
| CTX-008.04 | P0 | C | Закупки и выдача | Проверка выбранных строк, документа, операции, фиксации, прав и stale selection. | UNVERIFIED | — | ☐ |
| CTX-008.05 | P0 | C | ПТО, письма, паспорта, металл, справочники | Инвентарь мутаций по CTX-047; каждому обработчику подтверждён guard либо исправление. | UNVERIFIED | — | ☐ |
| CTX-008.06 | P0 | A | Отказ по правам и конфликтам | Pilot с двумя клиентами; недоступная/устаревшая команда не меняет БД. | UNVERIFIED | — | ☐ |
| CTX-009.01 | P0 | C | Удаление объекта, договора, документа | Зависимости и точная цель в preview; корзина/транзакция, безопасный default, Cancel/Esc. | IN PROGRESS | — | ☑ chat-confirm-1456 / 2026-11-28T14:56Z / main / ConfirmDeleteDialog and separate destructive confirmation dialogs; targeted unique Core.Tests; no SQL/GUI or shared MainWindow/EstimateView changes / prior=UNVERIFIED |
| CTX-009.02 | P0 | C | Удаление акта, этапа, остатка и вне сметы | Связи, подпись, computed rest, запрет опасных операций; политику CTX-034 не дублировать. | UNVERIFIED | — | ☐ |
| CTX-009.03 | P0 | C | Возвраты, корзина и permanent forget | Последствия, зависимые файлы и записи; атомарность, отказ без потери данных. | UNVERIFIED | — | ☐ |
| CTX-009.04 | P0 | C | Общие опасные подтверждения | Инвентарь оставшихся диалогов, Enter не запускает действие по умолчанию, Cancel/Esc без изменений. | IN PROGRESS | [CTX-009.04, 29.11: 7 confirmations; dynamic inventory and WPF pending](CONTEXT_MENU_TODO_20261124.md) | ☑ chat-confirm-1456 / 2026-11-28T14:56Z / main / ConfirmDeleteDialog and separate destructive confirmation dialogs; targeted unique Core.Tests; no SQL/GUI or shared MainWindow/EstimateView changes / prior=PARTIAL |
| CTX-009.05 | P0 | A | Удаление и восстановление | Pilot: отмена, зависимость, архив, stale revision, read-only и rollback; до/после. | UNVERIFIED | — | ☐ |
| CTX-014.01 | P2 | C | Главное меню: фокус и состояния | Esc, PlacementTarget, ПТО и эксклюзивный выбор шрифта; команда срабатывает на правильной цели. | CODE READY | [CTX-014.01](CONTEXT_MENU_TODO_20261124.md): font-scale owner/current indicator, source checks; native WPF Esc/focus and live menu verification pending CTX-049.02 | ☐ |
| CTX-014.02 | P2 | D | Главное меню: popup и иконки | Направление, подменю и исходные Python-иконки; сравнение парных ресурсов. | UNVERIFIED | — | ☐ |
| CTX-022.01 | P1 | C | Меню столбцов: общий и специальные маршруты | Инвентарь XAML/динамических таблиц, защищённые и групповые столбцы, header ПКМ и checkbox. | UNVERIFIED | — | ☐ |
| CTX-022.02 | P1 | C | Сохранение и сброс настроек | Независимый сброс ширины/порядка/видимости, кешированные окна не затирают изменения. | UNVERIFIED | — | ☐ |
| CTX-022.03 | P1 | A | Все таблицы и перезапуск | Реальный ПКМ, StaysOpenOnClick, ширина, порядок, видимость и масштаб после перезапуска. | UNVERIFIED | — | ☐ |
| CTX-027.01 | P1 | C | Сводная: команды изменения | Сравнить Python команды и C# guards/пакетные действия, исправить отсутствующее. | UNVERIFIED | — | ☐ |
| CTX-027.02 | P1 | C | Сводная: Excel round-trip | Редактируемый экспорт/импорт и изменяемые поля, защита от stale/ошибочного файла; неизменность сумм. | UNVERIFIED | — | ☐ |
| CTX-027.03 | P1 | A | Сводная: Pilot и экспорт | Реальные round-trip, команды, тайминги и ошибки на Windows. | UNVERIFIED | — | ☐ |
| CTX-034.01 | P1 | C | Открытие остатка этапа | Открывается точный документ из карты и заголовка; permission/revision/readiness проверены. | CODE READY | [CTX-034.01](CONTEXT_MENU_TODO_20261124.md): awaited revision-fenced exact stage/rest navigation; legacy dialog recheck; WPF/SQL Pilot pending | ☐ |
| CTX-034.02 | P1 | C | Правила удаления остатка | Сверить Python и SQL зависимости; разрешить лишь доказанно безопасный сценарий либо документировать несовместимость и запросить решение владельца. | BLOCKED | [CTX-034.02](CONTEXT_MENU_TODO_20261124.md): Python permits deletion; C# computed remainder guard retained; owner decision on safe deletion and money/linked documents required | ☐ |
| CTX-034.03 | P1 | A | Этапы и остаток | Pilot: обычный, подписанный, архивный и computed remainder; отказ/открытие/удаление по утверждённому правилу. | UNVERIFIED | — | ☐ |
| CTX-036.02 | P2 | A | Парная проверка popup | Фактические WPF/DPI кадры, focus и editable combo. | UNVERIFIED | D-часть сверена: [DESIGN_PASS_20261129_CTX_MENUS_DIALOGS](DESIGN_PASS_20261129_CTX_MENUS_DIALOGS.md) | ☐ |
| CTX-037.01 | P2 | D | Иконки основных меню | MainWindow, смета, закупки, этапы и КС: точное соответствие исходному Python. | UNVERIFIED | — | ☐ |
| CTX-037.02 | P2 | D | Иконки остальных динамических меню | По инвентарю CTX-047; C#-only без самодельной подмены. | UNVERIFIED | — | ☐ |
| CTX-041.01 | P1 | C | Клавиатура меню | Shift+F10/Menu, стрелки, Enter/Esc; точная цель и disabled guards. | IN PROGRESS | — | ☑ chat-keyboard-1452 / 2026-11-28T14:52Z / main / EstimateView keyboard and context-menu routes, ContextMenuTarget, targeted Core.Tests; no SQL/GUI or stage mutations / prior=UNVERIFIED |
| CTX-041.02 | P1 | C | Редактирование и clipboard | F2/F4/Del, Ctrl+C/V/D/M/K, Ctrl+Shift+V; реальные обработчики, не только подписи. | IN PROGRESS | — | ☑ chat-keyboard-1452 / 2026-11-28T14:52Z / main / EstimateView keyboard and context-menu routes, ContextMenuTarget, targeted Core.Tests; no SQL/GUI or stage mutations / prior=UNVERIFIED |
| CTX-041.03 | P1 | C | Вставка и структурные команды | Insert/Shift+Insert/Ctrl+Alt+Insert/Ctrl+[/]; корректный scope. | IN PROGRESS | — | ☑ chat-keyboard-1452 / 2026-11-28T14:52Z / main / EstimateView keyboard and context-menu routes, ContextMenuTarget, targeted Core.Tests; no SQL/GUI or stage mutations / prior=UNVERIFIED |
| CTX-041.04 | P1 | A | Физическая клавиатура | Проверить все перечисленные группы на Windows и Pilot, включая редактор в фокусе. | UNVERIFIED | — | ☐ |
| CTX-043.01 | P2 | A | Масштаб и DPI меню | 100/110/120% приложения, 100/125/150/200% DPI, 1920×1080 и 1366×768, увеличенный шрифт; фактические кадры. | UNVERIFIED | — | ☐ |
| CTX-044.01 | P1 | C | Открытие меню без сетевого ожидания | По инвентарю найти блокирующие SQL/UNC; использовать только revision-safe cache, повторная валидация записи. | UNVERIFIED | — | ☐ |
| CTX-044.02 | P1 | A | Время открытия | Холодное/повторное, 2500+ строк, p50/p95, ошибки и stale revision. | UNVERIFIED | — | ☐ |
| CTX-045.01 | P1 | C | Функциональный контракт диалогов | По CTX-047/048 записать поля, defaults, валидацию, права, SQL и Cancel; исправить выявленные функциональные расхождения. | UNVERIFIED | — | ☐ |
| CTX-045.02 | P1 | D | Визуальный контракт диалогов | Title, размеры, вкладки, подсказки, tab order, empty/error/loading по парному реестру. | UNVERIFIED | — | ☐ |
| CTX-045.03 | P1 | A | Полный проход команд | Каждая команда открыта до конечного диалога, есть результат и отказ; нет пропусков в manifest. | UNVERIFIED | — | ☐ |
| CTX-046.02 | P2 | C | Default/Cancel и фокус диалогов | Безопасные Enter/Esc и tab order по реестру; не дублировать исправления CTX-009/041. | UNVERIFIED | — | ☐ |
| CTX-046.03 | P2 | A | Парные кадры диалогов | Открытие, заполнение, ошибки и длинные значения, Windows/DPI. | UNVERIFIED | — | ☐ |
| CTX-047.01 | P1 | C | Инвентарь C# | Закрепить SHA и полный список XAML/CS кандидатов: popup, keyboard, editor/default, dynamic; каждый просмотрен или исключён с причиной. | PARTIAL | tools/local-qa/MENU_INVENTORY.md; tracked file list with blob and candidate counts incl. zero; full pinned scan/manual review pending | ☐ |
| CTX-047.02 | P1 | C | Инвентарь Python | Закрепить SHA и полный список .py: QMenu/QAction, right-click, shortcut, exec и editor/default; каждый кандидат классифицирован. | PARTIAL | tools/local-qa/MENU_INVENTORY.md; tracked file list with blob and candidate counts incl. zero; full pinned scan/manual review pending | ☐ |
| CTX-047.03 | P1 | C | Сопоставление обоих инвентарей | Каждому найденному маршруту назначить canonical menu/command ID или обоснованное исключение. | PARTIAL | tools/local-qa/MENU_INVENTORY.md; coverage report and explicit exclusions; 9 isolated assertions passed; full mapping pending | ☐ |
| CTX-048.01 | P1 | C | Схема manifest | Все исходные обязательные поля, уникальный ключ и валидация; повторно использовать существующие инструменты. | CODE READY | tools/local-qa/MENU_INVENTORY.md; hardened validation, resolved source locations; tests added, execution unverified; full inventory pending | ☐ |
| CTX-048.02 | P1 | C | Заполнение manifest: главные модули | Главное окно, смета, КС, закупки, выдача, себестоимость, этапы; реальные ссылки на обе версии. | UNVERIFIED | — | ☐ |
| CTX-048.03 | P1 | C | Заполнение manifest: прочие модули | Оставшиеся маршруты CTX-047; coverage validator не оставляет необработанных записей. | PARTIAL | tools/local-qa/MENU_INVENTORY.md; coverage report and explicit exclusions; 9 isolated assertions passed; full mapping pending | ☐ |
| CTX-048.04 | P1 | T | Проверка manifest | Запуск валидатора, отсутствие дублей/неизвестных команд, документированные C#-only/Python-only. | UNVERIFIED | — | ☐ |
CTX-049.01 закрыт DONE 28.11.2026 и удалён по правилу владельца: SQL-режим App.Checks exit 0 (2504 строк, фильтр, 1366/1920, smoke OK) — [BUG_F54_SCALAR_PASS_20261128](BUG_F54_SCALAR_PASS_20261128.md); манифест CTX-048 остаётся в строках CTX-048.01–03.
| CTX-049.02 | P1 | A | Функциональная приёмка команд | Все команды на Pilot, до/после, Cancel, права и состояние. | UNVERIFIED | — | ☐ |
| CTX-049.03 | P1 | A | Парные снимки меню | Одинаковые тема/масштаб/данные, фактические кадры по manifest; связать с QA-09 вместо повторной съёмки. | UNVERIFIED | — | ☐ |

## Дизайн

Исходный TODO: [UI_VISUAL_TODO_20261124.md](UI_VISUAL_TODO_20261124.md).

| ID | Приоритет | Тип | Конкретный пакет | Критерий окончания | Статус | Evidence / агент | Бронь |
|---|---|---|---|---|---|---|---|
| GRID-28.03 | P1 | A | Мышь и клавиатура | Физическое Excel-подобное выделение, copy и пакетные команды на Windows. | UNVERIFIED | D-часть готова: [DESIGN_PASS_20261129_SEMANTICS_CLOSURE](DESIGN_PASS_20261129_SEMANTICS_CLOSURE.md) | ☐ |
| HOME-03.02 | P1 | A | Сравнение заголовка | Фактические парные кадры на заданных DPI. | UNVERIFIED | D-часть готова: [DESIGN_PASS_20261129_SEMANTICS_CLOSURE](DESIGN_PASS_20261129_SEMANTICS_CLOSURE.md) | ☐ |
| SPACE-15.03 | P2 | A | Узкое окно и DPI | Проверка длинного текста, прокрутки, доступности footer/действий. | UNVERIFIED | D-часть готова: [DESIGN_PASS_20261129_SEMANTICS_CLOSURE](DESIGN_PASS_20261129_SEMANTICS_CLOSURE.md) | ☐ |
| QA-09.01 | P1 | A | Эталонные кадры Python | Все основные страницы по существующей матрице, pinned SHA/DPI/theme/data и raw hashes; нужен Windows. | BLOCKED | — | ☐ |
| QA-09.02 | P1 | A | Парные кадры C# | Та же матрица и параметры, отчёт отличий; использовать уже готовые инструменты. | BLOCKED | — | ☐ |
| QA-19.01 | P1 | A | Логотип и дерево: физические кадры | Родитель, несколько этапов, остаток, глубокий уровень, выбранное/свёрнутое; сравнение контуров и обрезания. | BLOCKED | — | ☐ |

## Договоры и приложения

Исходный TODO: [AGREEMENT_ANNEX_TODO_20261127.md](AGREEMENT_ANNEX_TODO_20261127.md).

| ID | Приоритет | Тип | Конкретный пакет | Критерий окончания | Статус | Evidence / агент | Бронь |
|---|---|---|---|---|---|---|---|
| AGR-01.01 | P0 | C | Карта модели и связей | objects/agreements/contracts, kind/base/stage, реквизиты и archive: конечная таблица по исходникам. | UNVERIFIED | — | ☐ |
| AGR-01.02 | P0 | A | Неоднозначные исторические случаи | На разрешённой Pilot копии перечислить несколько номеров/дат/компаний, пустые номера, DS без базы, KP; без изменений. | UNVERIFIED | — | ☐ |
| AGR-02.02 | P1 | A | Поиск, архив и режим КП | Реальные состояния главного окна, корректные подписи и неизменная навигация. | UNVERIFIED | D-часть сверена: [DESIGN_PASS_20261129_CTX_MENUS_DIALOGS](DESIGN_PASS_20261129_CTX_MENUS_DIALOGS.md) | ☐ |
| AGR-03.01 | P1 | C | Дерево и связи приложений | Различать legal agreement, annex, DS/base, KP, stage/rest; отсутствие ошибочного объединения. | UNVERIFIED | — | ☐ |
| AGR-03.02 | P1 | D | Карточки и заголовки приложений | Корректные названия/fallback без выдуманного номера; сохранить внешний вид. | UNVERIFIED | — | ☐ |
| AGR-03.03 | P1 | A | Навигация разных юридических карточек | Два договора одного объекта, DS/КП/этапы, поиск и архив; точная цель. | UNVERIFIED | — | ☐ |
| AGR-04.01 | P1 | C | Реквизиты самостоятельного договора | Номер/дата/компания/адрес/папка/тег/примечание, точный scope и неоднозначность; запись только выбранной карточки. | CODE READY | 29.11: [AGR-04/05, проход card scope](AGREEMENT_ANNEX_TODO_20261127.md#agr-card-scope-20261129) | ☐ |
| AGR-04.02 | P1 | C | Редактирование и защитные правила | Права, архив, revision, конфликт и readback; не переписывать приложения автоматически. | CODE READY | 29.11: [AGR-04/05, проход card scope](AGREEMENT_ANNEX_TODO_20261127.md#agr-card-scope-20261129) | ☐ |
| AGR-04.03 | P1 | A | Реквизиты и сохранение | Pilot: разные договоры, пустые реквизиты, Cancel, конфликт и повторное открытие. | UNVERIFIED | — | ☐ |
| AGR-05.01 | P1 | C | Создание пустого документа и копии | Явный legal target, корректные наследования, DS base и независимый KP. | CODE READY | 29.11: [AGR-05 server scope](AGREEMENT_ANNEX_TODO_20261127.md#agr-server-scope-20261129) | ☐ |
| AGR-05.02 | P1 | C | Excel импорт новых и обновление существующих документов | Preview и transactional scope, отсутствие неявной подстановки договора, rollback. | CODE READY | 29.11: [AGR-05 server scope](AGREEMENT_ANNEX_TODO_20261127.md#agr-server-scope-20261129) | ☐ |
| AGR-05.03 | P1 | C | Конвертация КП и редактирование связей | Точный target, подписи, дочерние связи и архив; неопределённые legacy правила не выдумывать. | CODE READY | 29.11: [AGR-05 server scope](AGREEMENT_ANNEX_TODO_20261127.md#agr-server-scope-20261129) | ☐ |
| AGR-05.04 | P1 | A | Матрица create/copy/import/convert | Два legal ID, разные компании, DS без базы, stale/Cancel/rollback на Pilot. | UNVERIFIED | — | ☐ |
| AGR-06.01 | P1 | C | Регистрация и открытие PDF | Точная legal identity, hash/путь, duplicate/unknown commit; версионность и явный выбор. | CODE READY | 29.11: [AGR-05 server scope](AGREEMENT_ANNEX_TODO_20261127.md#agr-server-scope-20261129) (ранее реализовано) | ☐ |
| AGR-06.02 | P1 | C | Архив и перенос файлов | Правила доступа, связи корзины/переноса, UNC, отсутствие подмены PDF сметы; unresolved policy согласовать. | UNVERIFIED | — | ☐ |
| AGR-06.03 | P1 | A | Файловая приёмка | Windows/UNC, отсутствующий/изменённый файл, архив, duplicate, Cancel; не удалять чужие файлы. | UNVERIFIED | — | ☐ |
| AGR-07.01 | P0 | C | Смета, себестоимость и этапы | Legal scope не меняет вычисления, распределение и ограничения подписей; независимые тестовые снимки. | IN PROGRESS | — | ☑ chat-agr-fin-1453 / 2026-11-28T14:53Z / main / Core financial scope, SqlServerKsContractScope/KsRequisites/Acts read and print/export, procurement/shipment scope, director/global search, targeted Core.Tests; no EstimateView keyboard/stage mutations/SQL/GUI / prior=UNVERIFIED |
| AGR-07.02 | P0 | C | Закупки и выдача | Проверить принадлежность сметному документу, доступность исторических движений и неизменность денег. | IN PROGRESS | 29.11: [AGR-05 server scope](AGREEMENT_ANNEX_TODO_20261127.md#agr-server-scope-20261129) | ☑ chat-agr-fin-1453 / 2026-11-28T14:53Z / main / Core financial scope, SqlServerKsContractScope/KsRequisites/Acts read and print/export, procurement/shipment scope, director/global search, targeted Core.Tests; no EstimateView keyboard/stage mutations/SQL/GUI / prior=PARTIAL |
| AGR-07.03 | P0 | C | КС-2 и общая КС-3 | Точная legal группа, исходные contract_id, состав подписанных актов, суммы, округления и печать. | IN PROGRESS | — | ☑ chat-agr-fin-1453 / 2026-11-28T14:53Z / main / Core financial scope, SqlServerKsContractScope/KsRequisites/Acts read and print/export, procurement/shipment scope, director/global search, targeted Core.Tests; no EstimateView keyboard/stage mutations/SQL/GUI / prior=UNVERIFIED |
| AGR-07.04 | P0 | A | Сравнение финансовых снимков | Два договора одного объекта, DS, stage/rest, подписанные акты; сверка всех вкладок до/после. | UNVERIFIED | — | ☐ |
| AGR-08.01 | P1 | C | Директор и глобальный поиск | Контекст договора/приложения/строительного объекта и точный переход. | IN PROGRESS | — | ☑ chat-agr-fin-1453 / 2026-11-28T14:53Z / main / Core financial scope, SqlServerKsContractScope/KsRequisites/Acts read and print/export, procurement/shipment scope, director/global search, targeted Core.Tests; no EstimateView keyboard/stage mutations/SQL/GUI / prior=UNVERIFIED |
| AGR-08.02 | P1 | C | Письма, приказы, допуски, паспорта и ПТО | Правильный legal vs construction scope и исторические тексты; не переписывать архив. | UNVERIFIED | — | ☐ |
| AGR-08.03 | P1 | C | Архив, корзина и перенос документа | Связи и PDF, запрет неявного regrouping, безопасный отказ и транзакция. | UNVERIFIED | — | ☐ |
| AGR-08.04 | P1 | C | Экспорт и юридические подписи | Согласованные реквизиты и название; исторические формы и суммы сохраняются. | IN PROGRESS | — | ☑ chat-agr-fin-1453 / 2026-11-28T14:53Z / main / Core financial scope, SqlServerKsContractScope/KsRequisites/Acts read and print/export, procurement/shipment scope, director/global search, targeted Core.Tests; no EstimateView keyboard/stage mutations/SQL/GUI / prior=UNVERIFIED |
| AGR-08.05 | P1 | A | Смежные модули | Pilot с двумя legal cards, архивом, переносом, печатью и неизменностью истории. | UNVERIFIED | — | ☐ |
| AGR-09.01 | P1 | A | Парная визуальная приёмка карточек | Пять плиток, суммы, прогресс и согласованные подписи; длинные названия, архив, 100/125/150% DPI, узкое окно. | UNVERIFIED | — | ☐ |
| AGR-11.02 | P0 | A | Интеграционный SQL/WPF прогон | 2500+ строк, два клиента, конфликт, подписанные КС, импорт/экспорт, PDF, перенос, архив/корзина, права; использовать evidence AGR-05–08. | UNVERIFIED | — | ☐ |
| AGR-11.03 | P0 | A | Финансовая сверка и релизное решение | Снимки до/после, оставшиеся риски, отдельное разрешение на выпуск; не дублировать MIG-REL-01. | UNVERIFIED | — | ☐ |

## Архитектура

Исходный TODO: [ARCHITECTURE_SIZE_TODO_20261126.md](ARCHITECTURE_SIZE_TODO_20261126.md).

| ID | Приоритет | Тип | Конкретный пакет | Критерий окончания | Статус | Evidence / агент | Бронь |
|---|---|---|---|---|---|---|---|
| ARCH-SIZE-01.01 | P1 | C | Полный отчёт по физическим файлам | Запустить существующий inventory по pinned SHA, проверить exclusions, размеры и top-30. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-01.02 | P1 | C | Логические partial классы и зависимости | Связь частей, ответственность и крупные методы, конечный список кандидатов; без дробления ради метрики. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-02.01 | P1 | C | Редактор КС: загрузка и состояние | Отделить независимую политику read lifecycle от WPF, либо доказать достаточность текущего выделения. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-02.02 | P1 | C | Редактор КС: редактирование/пакеты | Выделить тестируемую логику выбора, расчёта и команд; сохранить подпись/ревизию. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-02.03 | P1 | C | Редактор КС: экспорт/диалоги | Проверить уже выделенные компоненты, оставшиеся независимые обязанности и тесты; размеры до/после. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-03.01 | P1 | C | Смета: операции редактирования | Отделить независимые правила cell/clipboard/formula/validation, сохранить поведение. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-03.02 | P1 | C | Смета: очередь сохранения и readback | Выделить тестируемую ответственность без изменения FIFO/транзакции/Undo. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-03.03 | P1 | C | Смета: вкладки и жизненный цикл | Разделить независимую загрузку/состояние/маршруты; не переписывать UI. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-03.04 | P1 | D | Смета: XAML ресурсы и шаблоны | BLOCKED  BLOCKED | BLOCKED | Контракты source-тестов парсят XAML на месте: [DESIGN_PASS_20261129_CTX_MENUS_DIALOGS](DESIGN_PASS_20261129_CTX_MENUS_DIALOGS.md) | ☐ 
| ARCH-SIZE-04.01 | P1 | C | MainWindow: навигация и ссылки | Независимые политики и маршруты; выбор/архив/открытие без регрессии. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-04.02 | P1 | C | MainWindow: команды и жизненный цикл | Отделить оставшуюся самостоятельную ответственность, сохранить cache/revision/startup. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-05.01 | P1 | C | Материалы: группировка и проекция | Проверить выделенные компоненты, разделить оставшуюся независимую логику; результаты и порядок неизменны. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-05.02 | P1 | C | Материалы: команды и сохранение | Тестируемые правила bulk/write/readback, неизменная стоимость. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-06.01 | P2 | D | App.xaml: словари ресурсов | BLOCKED  BLOCKED | BLOCKED | Контракты source-тестов парсят XAML на месте: [DESIGN_PASS_20261129_CTX_MENUS_DIALOGS](DESIGN_PASS_20261129_CTX_MENUS_DIALOGS.md) | ☐ 
| ARCH-SIZE-06.02 | P2 | A | Ресурсы после разделения | WPF загрузка и парные кадры ключевых состояний, отсутствие missing resource. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-07.01 | P2 | C | SqlServerStages | Границы чтения, распределения, защиты и транзакции; независимые компоненты вместо только partial, деньги неизменны. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-07.02 | P2 | C | SqlServerActs | Разделить независимую политику расчёта/проекций/команд, подпись и scope сохранены. | UNVERIFIED | — | ☐ |
| ARCH-SIZE-07.03 | P2 | C | SqlServerWorkspace | Разделить независимые read/write/validation обязанности, legal identity и rollback сохранены. | UNVERIFIED | — | ☐ |

## Тестовый режим

Исходный TODO: [DEMO_MODE_TODO_20261128.md](DEMO_MODE_TODO_20261128.md).

| ID | Приоритет | Тип | Конкретный пакет | Критерий окончания | Статус | Evidence / агент | Бронь |
|---|---|---|---|---|---|---|---|
| DEMO-01.01 | P1 | C | Изолированный launch и SQL guard | Точное имя, локальность, marker/schema, отказ office/Cutover; оба Open пути проверены. | PARTIAL | [DEMO проход 2](DEMO_MODE_TODO_20261128.md): код/статическая сверка; Windows/SQL открыты | ☐ |
| DEMO-02.01 | P1 | C | Генератор синтетических баз | Две базы по штатным схемам, данные всех заявленных модулей, идемпотентный безопасный отказ; Python repo linked, без рабочей БД. | UNVERIFIED | — | ☐ |
| DEMO-02.02 | P1 | A | Живая генерация | Локальный SQLEXPRESS, обе базы, marker и схема, открытие реальных смет. | UNVERIFIED | — | ☐ |
| DEMO-03.01 | P1 | A | Штатные редакторы | Открыть, изменить и перечитать смету, этапы, КС, закупки, выдачу и себестоимость; отдельный UI не создавать. | UNVERIFIED | — | ☐ |
| DEMO-04.01 | P1 | A | Вычисления и операции | Тестовые расчёты, экспорт, Undo, конфликт и сохранение в обеих базах; найденные дефекты регистрировать адресно. | UNVERIFIED | — | ☐ |
| DEMO-05.01 | P1 | C | Изоляция всех файловых путей | APPDATA/LOCALAPPDATA, журналы, ПТО/письма/паспорта, настройки; нет записи в office profile. | PARTIAL | [DEMO проход 2](DEMO_MODE_TODO_20261128.md): код/статическая сверка; Windows/SQL открыты | ☐ |
| DEMO-05.02 | P1 | C | Два ярлыка и установка | Скрипт и инструкция, build/smoke перед публикацией ярлыков, отдельные профили. | CODE READY | [DEMO проход](DEMO_MODE_TODO_20261128.md): код/тесты, Windows не запускался | ☐ |
| DEMO-06.01 | P1 | C | Инвентарь исторических файловых реализаций | Список потребителей, зависимостей, тестов и упоминаний; отличить архив от активного кода. | IN PROGRESS | — | ☑ chat-legacy-1455 / 2026-11-28T14:55Z / main / read-only active SQLite/file-storage inventory, isolated legacy storage Core files/dependencies/tests, src/GmcSmeta.Cli Program/SqlLive/Parity, CLI-specific tests/docs; no shared SQL/GUI or active WPF edits / prior=UNVERIFIED |
| DEMO-06.02 | P1 | C | Замена потребителей | Активные маршруты только SQL Server; не удалять необходимый путь до замены. | IN PROGRESS | — | ☑ chat-legacy-1455 / 2026-11-28T14:55Z / main / read-only active SQLite/file-storage inventory, isolated legacy storage Core files/dependencies/tests, src/GmcSmeta.Cli Program/SqlLive/Parity, CLI-specific tests/docs; no shared SQL/GUI or active WPF edits / prior=UNVERIFIED |
| DEMO-06.03 | P1 | C | Удаление остатка SQLite | Удалить ставшие неиспользуемыми реализации/зависимости/тесты/упоминания согласно владельцу; исторические источники не переписывать скрытно, явно решить их судьбу. | IN PROGRESS | — | ☑ chat-legacy-1455 / 2026-11-28T14:55Z / main / read-only active SQLite/file-storage inventory, isolated legacy storage Core files/dependencies/tests, src/GmcSmeta.Cli Program/SqlLive/Parity, CLI-specific tests/docs; no shared SQL/GUI or active WPF edits / prior=UNVERIFIED |
| DEMO-06.04 | P1 | T | Сборка и тесты после удаления | Проверить зависимости и функциональные тесты; SQL Server остаётся единственным backend. | UNVERIFIED | — | ☐ |
| DEMO-07.01 | P1 | T | Обе сборки и smoke | Windows .NET/Python/Qt/WPF, EXE, штатные тесты и два ярлыка. | PARTIAL | [T_TEST_PASS_20261128](T_TEST_PASS_20261128.md): .NET ✓, EXE smoke exit 0, ярлыки ✓; python-тестов в срезе нет | ☐ |
| DEMO-07.02 | P1 | A | Offline и масштаб | Сеть отключена, сценарии DEMO-03/04, репрезентативный размер; рабочий профиль/БД не затронуты. | UNVERIFIED | — | ☐ |
| DEMO-08.01 | P1 | C | Тестовый updater | Release metadata/schema capability, hash, smoke, атомарный pointer/rollback, сохраняется тестовая БД. | PARTIAL | [DEMO проход 2](DEMO_MODE_TODO_20261128.md): код/статическая сверка; Windows/SQL открыты | ☐ |
| DEMO-08.02 | P1 | A | Обновление на Windows | Успешное обновление, отказ пакета, несовместимая схема, offline и сохранение данных. | UNVERIFIED | — | ☐ |

## Выпуск и эксплуатация

Исходный TODO: [SQL_MIGRATION_RELEASE_TODO.md](SQL_MIGRATION_RELEASE_TODO.md).

| ID | Приоритет | Тип | Конкретный пакет | Критерий окончания | Статус | Evidence / агент | Бронь |
|---|---|---|---|---|---|---|---|
| MIG-REL-01.01 | P0 | A | Актуальный релизный чек-лист | Проверить актуальную схему и разрешённые guard, backup/verify, rollback, финансы и результаты паритета; без повторного запуска production миграции. | UNVERIFIED | — | ☐ |
| MIG-REL-01.02 | P0 | A | Подготовка согласованного пакета | Build/test/EXE, проверка версии и smoke на изолированном окружении; публикация только по разрешению. | UNVERIFIED | — | ☐ |
| OFFICE-UPDATE-01.01 | P1 | C | Проверка маршрута штатного launcher | Стабильный ярлык, redirect, restart и отказ запуска; исправлять только отсутствующую реализацию. | UNVERIFIED | [DEMO проход](DEMO_MODE_TODO_20261128.md): бронь снята, полный аудит открыт | ☐ |
| OFFICE-UPDATE-01.02 | P1 | A | Windows E2E обновления | Активные правки, отказ запуска, первая миграция лаунчера, pin/relaunch, rollback и фактический пакет. | UNVERIFIED | — | ☐ |
| OFFICE-SQL-01.01 | P0 | A | Два офисных компьютера | Публичный сертификат, TLS, Windows identity и минимум прав; реальная проверка обоих ПК без раскрытия секретов. | UNVERIFIED | — | ☐ |

## Отложенные контрагенты

Исходный TODO: [COUNTERPARTIES_TODO_20261127.md](COUNTERPARTIES_TODO_20261127.md).

| ID | Приоритет | Тип | Конкретный пакет | Критерий окончания | Статус | Evidence / агент | Бронь |
|---|---|---|---|---|---|---|---|
| CTR-01.01 | P2 | B | Модель контрагентов | Карта существующих полей, роли и согласованный SQL-план; не начинать DDL без разрешения. | NOT STARTED | — | ☐ |
| CTR-02.01 | P2 | B | Справочник CRUD | Роли, поиск, архив, валидация дублей, права и concurrency. | NOT STARTED | — | ☐ |
| CTR-03.01 | P2 | B | Создание/привязка из договора или КП | Предпросмотр и явное подтверждение, защита от дублей. | NOT STARTED | — | ☐ |
| CTR-03.02 | P2 | B | Подстановка и обновление в обе стороны | Выбор из справочника, явное обновление реквизитов и КС; не менять историю и суммы. | NOT STARTED | — | ☐ |
| CTR-04.01 | P2 | B | Сопоставление старых контрагентов | Предпросмотр, ручное подтверждение, сохранение исходных значений. | NOT STARTED | — | ☐ |
| CTR-05.01 | P2 | B | Закупки и история | Сохранить supplier_id и Цех, переходы по ID. | NOT STARTED | — | ☐ |
| CTR-06.01 | P2 | B | ПТО, письма, АОСР | Выбор участников и исторические снимки реквизитов. | NOT STARTED | — | ☐ |
| CTR-07.01 | P2 | B | Контакты, банк, внешний поиск | Опциональный поиск и права отдельно согласовать. | NOT STARTED | — | ☐ |
| CTR-08.01 | P2 | B | Приёмка и выпуск справочника | Build/test/WPF/Pilot, печать, финансы и rollback после реализации. | NOT STARTED | — | ☐ |
