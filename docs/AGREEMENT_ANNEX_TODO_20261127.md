# Договор → приложения и ДС — TODO от 27.11.2026

## 27.11.2026 — результат согласованного блока

27.11.2026 Codex/main — AGR-04/05/06/07/10/11 CODE READY/PARTIAL: самостоятельное
хранение и схема 32 внедрены после отдельного разрешения владельца; 10 договоров,
22 связи, все старые 59718 записей/37 таблиц и финансовые снимки сохранены.
Подробности, проверки и backup: [CHANGELOG](CHANGELOG.md), [CURRENT_TASK](../CURRENT_TASK.md).
AGR-10 — CODE READY: миграция на копии/рабочей базе проверена, клиент и выпуск
ждут отдельного агента владельца. AGR-09/11 и GRID/QA остаются PARTIAL:
1845/1845, SQL 30 PASS/6 SKIP, фоновые WPF load/save и сетка пройдены;
ручная/DPI/LAN приёмка и релиз открыты. Полностью закрытых ID в этом блоке 0.
Сводка AGR: 10 PARTIAL, 1 CODE READY; исторические 418 ID и их статусы без изменений.



## 27.11.2026 — AGR-09/10/11: проверка main и модель хранения

27.11.2026 Codex/main — AGR-09/10/11 PARTIAL. Свежий main c6caa73
собран; исправлен SourceGrid column-menu route. 1839/1839 xUnit и полный
Windows offline WPF PASS (включая routed editing на синтетических строках).
Подготовлена [модель самостоятельных договоров и перехода](AGREEMENT_STORAGE_PROPOSAL_20261127.md):
agreements 1:N на объект, явные связи приложений/ДС, отдельные файлы,
исторические реквизиты, ручной mapping конфликтов и ограничения отката.
До применения нужны согласование, Pilot metadata, исполнимые скрипты/их приёмка;
DDL/рабочая БД/версия/релиз не менялись. AGR-11 теперь PARTIAL: SQL, два клиента,
физический GUI/DPI и все экспорты ещё открыты. 11 PARTIAL, 0 NOT STARTED/DONE.


**Запрос владельца:** минимально изменить существующий интерфейс ГМЦ-Смета C#; не проектировать новый главный экран. «Объект» в рабочем интерфейсе фактически является договором, «Договор» со сметой — приложением. КС — рабочая функция внутри готовой вкладки, а не новый раздел главного экрана. Сохранять текущую логику и внешний вид. Приоритет ближайших блоков по указанию владельца; существующие задачи переноса/SQL/релиза не отменять.

База исходников для ТЗ: `49b25e57a848874d5259add7e8d8a6d96b9e3e55`. Статическая сверка уже показала: Workspace/Navigation используют objects/contracts и main/dop/kp; группировка КС по effective number внутри object, связь ДС по base_contract_id; отдельная КС-2 привязана к contract_id, общая КС-3 объединяет акты. Реальные записи SQL и GUI на этом этапе не проверялись.

## Целевая структура и ограничения

- В рабочем интерфейсе: год → договор (прежняя карточка объекта) → приложения и ДС; режим КП и архив остаются.
- `objects`, `contracts`, `object_id`, `contract_id`, `base_contract_id`, `acts`, `act_lines`, `systems`, `positions`, `kind=main/dop/kp` не переименовывать. На этапе визуала никаких миграций, массовых переименований, пересчётов или изменений идентификаторов.
- В юридических/технических документах объект строительства не переименовывать в договор. Разграничить название стройки, короткое рабочее название договора, юридические реквизиты и название приложения.
- При одном объекте с несколькими самостоятельными договорами не подменять юридический контекст визуальной подписью; до решения владельца сохранять исходные связи и явно отмечать неоднозначность. Не менять алгоритм группировки КС автоматически.
- На примере «Днепровский» в шапке показывать № Д23-01-25-ОВ от 23.01.2025 только после подтверждения однозначности группы; карточки «1с 2026 (22%)», «2с 2026 (22%)», «3с 2026 (22%)», «4с 2026 (22%)», «парковка 2026 (22%)», ДС №4/5. Это пример по скриншоту владельца, не проверка содержимого БД.
- Сохранить пять плиток (итого, выполнение, материалы, СМР, заработано), показатели и карточки. Не добавлять КС на главный экран, не менять дизайн готовых вкладок.
- Порядок: AGR-01 → безопасная UI-проекция AGR-02/03/04 (только чтение) и контроль AGR-07/09 → AGR-05/06/08; если требуется изменение данных, согласовать AGR-10 до записи/DDL; завершение AGR-11. Не откладывать безопасные визуальные исправления из-за условной миграции, но не маскировать конфликтующие реквизиты.

## Задачи

<a id="agr-01"></a>

### AGR-01 — P0 — PARTIAL: Аудит существующей группировки и юридических реквизитов

- [ ] Проверить objects/contracts, Kind, Number, Date, Company, BaseContractId, StageOf, связи, отчёты, архив. Выделить случаи нескольких номеров/дат/контрагентов внутри одного объекта, пустых номеров, неоднозначных ДС и КП без договора. Проверять только исходники и отдельную Pilot-копию, без изменения данных. Составить таблицу фактических полей и сценариев; не объявлять каждый объект одним юридическим договором без проверки.

<a id="agr-02"></a>

### AGR-02 — P1 — PARTIAL: Главное окно: минимальная терминология

- [ ] MainWindow.xaml, MainWindow.xaml.cs, MainWindow.ObjectCards.cs, MainWindow.Navigation.cs: Новый объект → Новый договор, Поиск объекта → Поиск договора, Объект → Договор, Итого по объекту → Итого по договору, Документы → Приложения и ДС, Документы объекта → Документы договора. Применять с учётом режима КП/архива и неоднозначности AGR-01. Оставить пять плиток, сетку, цвета, размеры, иконки, кнопки, прогресс и нижнюю панель. Не добавлять реестр КС или новые вкладки на главном экране.

<a id="agr-03"></a>

### AGR-03 — P1 — PARTIAL: Дерево, карточки и заголовки приложений

- [ ] Workspace.cs, Navigation.cs, MainWindow.xaml, EstimateView.xaml/cs: kind=main показывать как «Приложение», короткое название брать из Name (fallback с идентификатором/существующим номером без выдуманного номера приложения). ДС сохраняет номер и дату и связь «к приложению», если BaseContractId задан. Номер юридического договора не повторять в каждом заголовке при однозначном контексте. Поиск по старому полному названию, номеру договора, названию приложения и ДС остаётся доступен; сохранить идентификаторы, порядок, этапы, архив, раскрытие и выбор. Не менять глобально DocRow.Title, если его используют экспорт/бизнес-логика; выделить UI-проекцию.

<a id="agr-04"></a>

### AGR-04 — P1 — PARTIAL: Шапка и свойства договора

- [ ] ObjectDialog.xaml/cs, ObjectRow и чтение реквизитов: короткое рабочее название, номер и дата договора, заказчик, подрядчик, адрес стройки, папка документов, тег, примечание. На первом этапе отображать только однозначно подтверждённые реквизиты существующих документов, конфликт отмечать явно; не выбирать первый/последний номер наугад. Редактируемые поля нового уровня, наследование, пустые значения и правила обновления после решения AGR-01/AGR-10; не менять подписанные документы при редактировании карточки.

<a id="agr-05"></a>

### AGR-05 — P1 — PARTIAL: Создание и импорт

- [ ] ContractDialog.xaml/cs, MainWindow.ExcelImport.cs, сценарии создания/конвертации КП: «Добавить документ» предлагает приложение, ДС, КП и импорт Excel (сохранить существующий механизм пустой сметы/копии/импорта). У приложения номер/дата юридического договора подставляются только из проверенной карточки; отдельные реквизиты приложения не выдумывать. ДС сохраняет BaseContractId и собственные номер/дату. КП сохраняет самостоятельный жизненный цикл, стадии и режим навигации. Не менять схемы импорта до аудита.

<a id="agr-06"></a>

### AGR-06 — P1 — PARTIAL: Файлы договора и приложения

- [ ] Существующие кнопки PDF/Файлы/Письма на карточках сохранить; определить отдельную привязку подписанного PDF юридического договора и его файлов без подмены PDF смет/ДС. На уровне договора предусмотреть доступ к файлам в существующей панели/меню без новых больших блоков. Проверить права, архив, корзину, перенос, UNC, отсутствие дублирования и потери файлов. Новые связи/хранилище только после AGR-10.

<a id="agr-07"></a>

### AGR-07 — P0 — PARTIAL: Существующие вкладки и КС — регрессионный контроль

- [ ] Не перестраивать EstimateView/ContractWorkspaceTabs, Смету, Себестоимость (Материалы/Сводная/Итоги), Закупки, Выдачу, КС-2/КС-3, этапы и текущие команды. ContractActsWorkspace, SqlServerActs, SqlServerKsContractScope, KsWorkspaceProjection, KsGroupReport оставить. Сейчас КС-2 относится к сметному документу (acts.contract_id); общая КС-3 объединяет КС-2 по эффективному номеру в объекте и ключу группы (1.1 + 1.2 → 1). Не выдавать это за одну КС-2 с позициями разных приложений: такая новая функция не согласована. Проверить «Все КС договора», открытие конкретного акта, индивидуальные и общие выгрузки, signed/draft, накопительные суммы, opening balances, НДС/скидки/округления, остатки, DS и отсутствие двойного учёта. Номер/дата юридического договора в печати не должны подменяться номером приложения.

<a id="agr-08"></a>

### AGR-08 — P1 — PARTIAL: Смежные модули и юридические тексты

- [ ] Сводка руководителя, письма, допуски, приказы, паспорта, ПТО, корзина, глобальный поиск, архив, меню, перенос документа и экспорт: провести адресную сверку контекста и терминов, не слепую замену. Объект строительства остаётся объектом строительства в адресах, актах, допусках, приказах и технической документации. Сохранить существующие связи объекта/документа, history, GUID/ID, права, блокировки и подписанные файлы.

<a id="agr-09"></a>

### AGR-09 — P1 — PARTIAL: Приёмка визуального блока

- [ ] Сравнить с текущим экраном Днепровский и ДНС 12.2: те же пять плиток, суммы, прогресс, сетка, отступы, расположение команд; отличаются только согласованные подписи и однозначная шапка. Длинные названия, пустые/разные номера, ДС с/без базы, КП, этапы/остаток, архив, 100/125/150% DPI, узкое окно, поиск и возврат к смете. Добавить тесты UI-проекции и исходников, реальную WPF-проверку. Никаких новых иконок по аналогии.

<a id="agr-10"></a>

### AGR-10 — P0 — PARTIAL: Решение по структуре данных и миграции (условный этап)

- [ ] После AGR-01 определить, достаточна ли существующая модель для одного договора на объект или нужна отдельная сущность/явная связь юридического договора. Не объединять разные договоры по имени/номеру без проверки контрагента/даты и не переносить документы автоматически. На этапе визуала DDL запрещён. Если нужны новые поля/таблицы/привязка PDF, подготовить отдельный план и согласовать до исполнения: идемпотентность, обратимость, аудит конфликтов, сохранение ID, резервная копия, сверка суммы/актов/остатков/файлов до и после. Только GmcSmetaPilot_*, никогда production/Cutover. Существующую схему 31 и release guard сохранить.


**Уточнение владельца 27.11.2026 (скриншот v1.0.6, ЗБ2):** название карточки — название объекта, не юридический номер. Целевой экран должен содержать отдельно редактируемые «№ договора», «Дата договора», «Подрядчик» (выбор наших организаций), заказчик, предмет/наименование работ, при необходимости даты начала/окончания работ; адрес относится к объекту. Для номера/даты/подрядчика нужен самостоятельный источник на уровне юридического договора, допускающий ввод до появления приложений. Подписанный PDF и сроки — отдельные поля/связи только после согласования модели. Не хранить их в name/note или фиктивном приложении. Если в legacy-карточке разные номера/даты/организации или основания ДС неоднозначны, показать источники и предложить ручное сопоставление юридических договоров; не перезаписывать все приложения и не объединять КС автоматически. План AGR-10: новая сущность или явные поля после подтверждения кардинальности, связь с приложениями/ДС по ID, безопасный перенос только однозначных групп, согласованное наследование при создании/импорте, миграция на Pilot и отдельные тесты печатных КС с датой основного договора и собственной датой ДС. До согласования DDL поля просмотра и адресное изменение существующего документа, без независимой записи.

<a id="agr-11"></a>

### AGR-11 — P0 — PARTIAL: Интеграционная приёмка и выпуск

- [ ] dotnet build GmcSmeta.slnx; dotnet test GmcSmeta.slnx; Windows GUI и SQL Pilot, включая два клиента, 2500+ строк, конфликт ревизии, подписанные акты, общую КС-3, импорт, экспорт, перенос, архив/корзину, PDF, права и сохранение. Сравнить финансовые итоги до/после. Отдельно указывать фактические тесты и непройденные этапы, CODE READY не равно DONE. Релиз отдельно по действующему регламенту.

## Журнал

- 27.11.2026 — ChatGPT/main, base `49b25e57a848874d5259add7e8d8a6d96b9e3e55`: оформлено ТЗ по указанию владельца; 11 новых задач. Только документация. Код, БД, расчёты и релиз не менялись. Все задачи открыты, статусы уточнять после фактического аудита. Отдельный профильный счётчик, не прибавлять к четырём историческим спискам как новые дефекты; пересечения фиксировать как алиасы при проверке.

- 27.11.2026 ChatGPT/main (base 9090218) — AGR-01/02/03/04 PARTIAL, AGR-09 PARTIAL: source audit + first visual block. SidebarSearch local template, zero TextBox padding, a single 30/32 inset on centered PART_ContentHost, min34 without fixed height, short placeholder, Ctrl+P in tooltip, 3:2 button columns. Existing icons unchanged. Main/card/tree display projections: main badge «Приложение», name first, legal number retained in tree and ambiguous cards; show short card title and header only for matching nonempty number/date/company display strings and explicitly linked DS within current object; this is conservative presentation, not legal identity proof. KP/archives distinct, unresolved group labels rather than fictitious contract. Creation dialog labels only; same handlers and kind codes, no autofill or data migrations. Existing totals, KS, tabs, document IDs/search tips and SQL untouched. Source review confirms objects name/customer/address/folder and contracts number/date/company/base/stage; separate legal ID absent in current projection; group conflicts and real data must be checked on Pilot. Added two domain tests and one XAML source test; not run. Remaining full terminology/form/export audit, explicit identity/number-date normalization and true company ID check, Windows build/xUnit, WPF narrow sidebar/DPI/large font, SQL Pilot. No release/DONE.

- 27.11.2026 ChatGPT/main (base b490e52) — AGR-01/02/03/04/05/08/09 PARTIAL. Read-only metadata projection now includes company_id (no DDL), agreement defaults require matching object/number/valid date/company ID and linked DS; normalize supported date formats, no legal identity inferred. Tree short title only for consistent group; original titles/tooltips/search and IDs unchanged. Fresh scoped metadata read before new-dialog + revision comparison, copy/DS routes excluded from defaults. Explicit «Подставить реквизиты договора» for new annex (auto only if dialog starts on main without copy); KP remains default generic creation to preserve Excel import route, switching kind never auto-overwrites entered fields. Form labels distinguish agreement/DS/KP; object properties show derived read-only requisites, no separate persistence. Editor chip/menu and main navigation commands use annex/card terms; deletion/archive wording explicitly covers all documents. Existing import, SQL write paths, legal exports, KS, totals, tabs, storage and icons unchanged. Tests extended + one source test; not run. Remaining full WPF/SQL Pilot acceptance, identity audit of real data, dedicated agreement fields/files/migration decision, downstream export/technical texts and full terminology audit. No production DB/release/DONE.

- 27.11.2026 ChatGPT/main (base 575b743) — AGR-01/06/07/08/10 PARTIAL. Read-only Pilot diagnostic `dotnet run --project src/GmcSmeta.Cli -- sql agreement-audit "<Pilot connection string>"` (guard before open; revision checked; only metadata, no financial reads/writes; returns finding IDs/codes). Flags missing/ambiguous main number/company, DS missing/cross-card base, unresolved metadata, same-number inconsistent mains for historical KS scope review. No grouping algorithm or SQL schema changes. Existing attachments are contract_id-scoped and signed_contract role is per smetny document, not legal agreement; clarified UI scope and PDF tooltips, did not reinterpret existing files or introduce shared storage. Transfer dialog now explicitly identifies annex+linked DS, warns other annexes stay and KS-3 scope may change; transfer transaction untouched. Updated director UI (not export columns), trash, archive, second delete confirmation, card letters/PTO labels; technical object references untouched. Three audit unit tests added, not run. AGR-10 next: obtain actual Pilot findings, decide explicit legal identity and independent PDF ownership before DDL; do not infer from names. Existing KS scope is object_id+effective number, including blank numbers; tests and SQL export still require live regression. Windows build/xUnit/SQL Pilot not run; no production DB/release/DONE.

- 27.11.2026 ChatGPT/main (base f72c7b8) — AGR-01/03/05/07/09 PARTIAL. Editor header uses annex name from the current estimate snapshot (ID fallback), original legal title/tooltip and exports unchanged; three tab tooltips and stage command clarified, no new icons/layout. Excel preview labels distinguish annex/DS/KP, explicit shared DS base selection (including existing legacy KP eligibility, unchanged SQL rules), reset on target change; no guessed first base. Warning before non-KP import: existing importer selects default company, not agreement metadata; company inheritance/per-row DS bases not implemented, verify imported requisites. KS summary is scoped to the current estimate, not the whole legal agreement: corrected captions only, no financial/grouping changes. Agreement audit reports invalid/conflicting main dates separately and compares supported timestamps by calendar date. One domain fact, one source fact and three assertions added; not executed. Build/xUnit/WPF/SQL Pilot unavailable here; visual acceptance, importer company selection and conversion/copy, actual Pilot audit and export regression remain open. No schema/DB/release/DONE.

- 27.11.2026 ChatGPT/main (base bdd7885) — AGR-05/07/09 PARTIAL. New Excel import: fresh objects/document/company/duplicate metadata read under matching revisions; explicit shared company for selected annexes, optional confirmed requisites fill only selected main rows for consistent card, DS company from explicitly selected base (legacy KP eligibility retained). Transaction rechecks base/company, existence and revision, rejects unresolved metadata; KP default company and DNS file VAT rule preserved, other forms use selected company's VAT, no amount formula/DDL change. New DS defaults to base company on selection, initial copy company comes from source (no guessed last company if source lacks company), stage excluded only from base list; conversion KP→annex offers explicit defaults from consistent metadata, same-company VAT retained. Existing property updates and SQL write guards unchanged. New pure policy tests (2 facts + 5 theory cases), updated source assertions; not run. Remaining: separate bases/companies per imported row (currently shared), manual choice when metadata ambiguous, check copy source switching and DS company overrides, WPF layout and full SQL Pilot/exports/KS acceptance. No DB connection, production/release/DONE.

- 27.11.2026 ChatGPT/main (base 4916823) — AGR-04/07/09 PARTIAL. Owner requests embedded agreement requisites instead of cramped modal and shared KS requisites. Existing contract/group properties command opens AgreementCardView in PageHost; new-card and KP dialogs preserved. Uses existing WPF tile/toolbar styles, no new icons, no changes to main card layout. Number/date/company shown read-only from consistent annex metadata (not new legal identity); normal properties plus common KS customer/site/signers/work title/start date editable, one Pilot-only Serializable transaction on dbo.objects with archive/ownership/revision checks and change log. Works for empty cards; existing KS exports already read shared fields through c.object_id; local ks_site_part untouched. Back confirms unsaved edits; switching tree discards edits with visible notice; SQL save result never navigates away from a different page. Shared data is card-scoped, not separate legal entity; multiple legal agreements in one legacy card need audit. Actual print date/DS fields audit identified standard KS Excel uses own document date and DNS has missing date fields; separate correction still pending. Added 3 facts + 2 theory cases, not run; Windows layout/build/xUnit/SQL Pilot and actual export not verified. No DB/schema/release/DONE.

- Смежная отложенная задача P2: [справочник контрагентов CTR-01…08](COUNTERPARTIES_TODO_20261127.md). Согласовать модель после AGR-01/10, не изменять текущие реквизиты/связи автоматически.

- 27.11.2026 ChatGPT/main (base 11cfe3d) — AGR-02 PARTIAL, owner screenshot v1.0.6: KP tab incorrectly used static «Новый договор» captions. Mode-specific captions on sidebar and empty page, search/accessibility, context menu, ObjectDialog heading/help; updated through RefreshNavigation (startup/restoration, manual and programmatic mode switches). Existing group creation, empty-card membership, document creation commands, schema and icons unchanged. Added 3 mode theory cases and source assertions; not run. Windows smoke check outstanding.

- 27.11.2026 ChatGPT/main (base cf78866) — AGR-04/10 PARTIAL: owner distinguishes object name and legal agreement metadata. AgreementCardView now shows explicit number/date/contractor read-only and source document table (main/dop, own dates/IDs; expand on ambiguity, direct existing guarded edit for unlocked non-stage, no navigation if unsaved common fields). Object/address labels corrected. Independent editable legal fields before annex creation require AGR-10 approved persistence/KS migration; specification extended, no mass updates or DDL. Source regression expanded, Windows build/test/GUI/Pilot not run. Existing KS grouping and exports untouched. No release/DONE.
