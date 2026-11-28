# Документация GMC-Смета — карта чтения

## Начать работу

1. [Рабочая очередь](EXECUTION_TODO_20261128.md) — конкретные пакеты, статусы и единственная бронь агентов. Сначала подтвердить отдельный коммит брони в main; только потом менять код.
2. [AGENTS.md](../AGENTS.md) — обязательные правила, SQL и релиз.
3. [CURRENT_TASK](../CURRENT_TASK.md) — короткий чекпоинт. Не читать исторический индекс целиком.
4. [MASTER_TODO](MASTER_TODO.md) — родительский реестр и сводка; не суммировать с дочерней очередью.

## Профильные источники — читать только по выбранному ID

| Область | Источник |
|---|---|
| Функциональные дефекты | [BUG TODO](FUNCTIONAL_BUG_TODO_20261124.md) |
| Скорость | [PERF TODO](PERFORMANCE_TODO_20261124.md), [benchmark](REMOTE_SQL_BENCHMARK.md) |
| Меню | [CTX TODO](CONTEXT_MENU_TODO_20261124.md) |
| Дизайн | [UI TODO](UI_VISUAL_TODO_20261124.md), [сводная](COST_SUMMARY_VISUAL_TODO_20261125.md), [WPF audit](WPF_VISUAL_AUDIT.md) |
| Договоры | [AGR TODO](AGREEMENT_ANNEX_TODO_20261127.md), [модель](AGREEMENT_STORAGE_PROPOSAL_20261127.md), [карта](MAP.md) |
| Архитектура | [ARCH TODO](ARCHITECTURE_SIZE_TODO_20261126.md) |
| Тестовый режим | [DEMO TODO](DEMO_MODE_TODO_20261128.md) |
| Перенос, выпуск, SQL | [Python parity](PYTHON_0664_TO_0679_PARITY.md), [SQL release](SQL_MIGRATION_RELEASE_TODO.md), [миграция](MIGRATION.md), [Pilot E2E](SQL_PILOT_E2E.md), [Remote setup](REMOTE_SQL_SETUP.md) |
| Экспорт | [КС](KS_EXPORT_PARITY.md), [Golden КС](KS_FORMS_GOLDEN_ACCEPTANCE.md), [Golden документы](DOCUMENTS_GOLDEN_ACCEPTANCE.md), [DOCX](DOCX_PARITY_ACCEPTANCE.md), [договоры](CONTRACT_EXPORT_ACCEPTANCE_20261121.md) |
| Будущие улучшения | [контрагенты](COUNTERPARTIES_TODO_20261127.md) — отложено, не блокирует релиз |
| Выпуски и подтверждённая приёмка | [CHANGELOG](CHANGELOG.md) |

Прочие `*_PARITY*`, `*_ACCEPTANCE*`, `DESIGN_PASS_*` и профильные аудиты — адресные критерии и доказательства, не второй активный TODO. Сохраняются, поскольку на них ссылаются задания и история; не перечитывать целиком и не копировать в MASTER/CURRENT_TASK. `ROADMAP`, `SMALL_TASKS`, старые SQL/переносные документы — исторический контекст, не самостоятельный счётчик новых задач. Исторический план зачистки файлового backend не означает разрешение его поддерживать.

## Архивы

- [Старые Issues](ARCHIVE_OPEN_ISSUES.md) — указатель на сохранённые полные тела 35 задач.
- [CURRENT_TASK 25.11](CURRENT_TASK_ARCHIVE_20261125.md), [до №64](CURRENT_TASK_ARCHIVE_THROUGH_64.md) — исторические чекпоинты.
- [Старые правила root](ARCHIVE_AGENTS_ROOT.md), [C#](ARCHIVE_AGENTS_CSHARP.md) — не действуют вместо актуального AGENTS.
- [Полный MASTER до очистки](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/docs/MASTER_TODO.md), [полный CURRENT_TASK до очистки](https://github.com/xBonkx/CsharpGMC/blob/c00f4c32d3faea3e9db33a31ca07c0207df3a376/CURRENT_TASK.md) — закреплённые версии со всеми подробностями.
- Для локального чтения исторического файла: `git show c00f4c32d3faea3e9db33a31ca07c0207df3a376:путь/к/файлу` (в shallow clone сначала получить соответствующий commit).

28.11.2026: исторические тексты не удалены из Git, но из рабочей копии убраны дублирующие длинные журналы и тела архивов; прежние пути и заголовки сохранены как указатели. Профильные критерии и текущие строки статусов не заменены архивом.

## Как вести документацию дальше

Подробное доказательство — **один** профильный документ/отчёт. Очередь: статус, бронь, короткая ссылка. MASTER: статус родителя, сводка, краткое evidence. CURRENT_TASK: короткий результат и ссылка. Не добавлять одинаковые абзацы в четыре файла. Отдельный коммит брони изменяет только очередь; финальный коммит освобождает собственную бронь и обновляет затронутые статусы. Не создавать новый отчёт, если достаточно обновить профильный источник. При следующем сокращении сохранить pinned snapshot и рабочие ссылки; не удалять критерии и доказательства без восстановления.
