# SQL Server schema 31 — stage 1 (24.11.2026)

Python source: xBonkx/gmcsmeta main aa6b34d. The existing C# schema 28 remains available for its own isolated tests. No production migration or connection is authorized.

Actual Python migrations:
- 29: contracts.discount_percent FLOAT NOT NULL DEFAULT 0; acts.discount_percent FLOAT NOT NULL DEFAULT 0.
- 30: acts.section_amounts_json NVARCHAR(MAX) NOT NULL DEFAULT N''.
- 31: acts.discount_override BIT NOT NULL DEFAULT 0; acts.manual_total_cents BIGINT NOT NULL DEFAULT -1.

## Safe preview

1. Restore a fresh backup of the production schema 31 database into a NEW database named GmcSmetaPilot_Test_Csharp, with distinct data/log file paths. Never overwrite production or any existing copy; verify server/database/backup identities with the DBA.
2. After restore, the DBA sets ONLY this copy READ_ONLY. Verify database name, schema marker 31, all five columns and SQL Server updateability READ_ONLY. A read-only SQL login is recommended as additional protection.
3. Build current C# sources and run offline tests. Launch using the existing pilot script with the preview database name and appropriate authentication.
4. The title marks preview/read-only and amounts unverified. Browse objects/documents only; do not use exports or monetary results as real documents. No SQL changes to the source.
5. Existing production name GmcSmetaPilot_Cutover_* is rejected even before connecting, except the owner-confirmed exact local test database GmcSmetaPilot_Cutover_20261120_160840 on localhost/127.0.0.1, port 1433/default, with the actual SQL Server machine verified against the client machine. This exception supports schema 28 only; schema 31 restrictions are unchanged. The preview copy is never a writable pilot. Server-enforced READ_ONLY protects against individual writers not yet audited.

## Remaining before writable v31
- Contract and act financial readers (including batched estimate totals, workspace, cost, cumulative KS-3, exports): exact Python discount rounding, stored act discount and manual cents including zero, VAT, customer forms.
- Act writers: snapshot discount at creation, preserve override on editing/unsigning, safe section JSON allocation, contract discount changes, exact signed act amounts, formulas and proportional fill.
- Audit all writes and launch paths; require an explicit isolated writable pilot allowlist, separate from preview and production. Reconcile schema 31 against a restored copy; build, run Windows UI, test two clients and compare signed acts and totals to Python to the kopeck.
- Do not change the schema marker to bypass compatibility, remove READ_ONLY from this preview, or connect production.
- This stage is source changes only; Windows compilation and live SQL preview still need local verification.

## Stage 2 — financial read projections (24.11.2026)
- Totals.ReadAll/ReadOne and batched EstimateReader include the full saved contract percentage, retaining gross-before-discount components and corrected payable/VAT.
- FinanceReader.ReadActs (workspace/cards) and SqlServerActs.ActsOf (act lists) read stored act discount and exact manual cents, including zero and corrections above original gross. ActInfo additionally carries override and section JSON for subsequent work.
- Schema 28 projections stay unchanged. No write access to schema 31 is enabled. Export, section allocation, UI presentation, other independent financial paths, and exact Python rounding acceptance still require work. New offline regression tests added; not executed in this environment.

## Stage 3 — saved section allocation and visible corrections
- ReadDetails carries all v31 fields; SmrBySystem uses saved JSON rather than redistributing it when not in pos mode. KsActAllocation.Resolve preserves unallocated/deleted section money as a remainder, and pos mode retains work-price allocation.
- KS grid preview shows stored manual SMR/transport including sections without materials and the unallocated row. Tooltip shows discount/correction. Standard Excel/PDF KS forms display an explicit signed correction line instead of silently folding it into the payable total.
- Still pending: section-by-section export in all forms, proportional suggestion and editing/writers, full UI/SQL acceptance. No writable v31 or production access. Tests added, not executed.

## Stage 4 — test-only discount writer and allocation policy
- Added mode validation matching Python (contract/percent/total, zero is explicit), supported-form checks, and atomic SaveDiscountSchema31Test with signed/locked and stale-revision protection, typed BIT/BIGINT/FLOAT parameters, audit entry and single revision bump.
- The writer accepts ONLY a separate database named GmcSmetaPilot_Schema31WriteTest, verifies schema 31/columns/READ_WRITE every time, and is not connected to WPF. Ordinary Open and pilot guards reject that name. Never use production or the read-only preview. No database was created or written here.
- KsSectionEditPolicy validates section sums, remaining budget, pos mode, and supplies a pure percentage suggestion with parent/child budgets and cumulative kopeck rounding. A transaction-scoped SQL input reader, section save, UI, contract discount propagation, act creation snapshot, and live tests remain pending.
- Added offline tests for guard, modes, allocation, limits and rounding. Windows/.NET and SQL Server were not executed.

## Stage 5 — transactional section snapshot and test-only writer
- ReadSectionsSchema31Test builds the saved/current breakdown, current contract budget, other-act balances (including unsigned drafts), per-section limits and percentage suggestion inside one serializable transaction; no mutation. SaveSectionsSchema31Test reloads all of these under the revision lock, rejects unsupported forms, signed/locked acts, foreign sections and over-budget amounts, and atomically writes section JSON and totals with an audit entry and one revision increment. The pos mode keeps saved work sums/flag intact. Discounts are not changed.
- Dedicated write-test database only, not wired to ordinary WPF. No SQL connection or execution was performed here. Decimal vs Python float edge cases still require golden checks; test-only writer must not be promoted before real acceptance.
- Regression tests for name guard, prior allocations, residual budgets and invalid inputs added. Remaining: SQL roundtrip/rollback acceptance, discount propagation on new/updated contracts and acts, WPF dialogs and wider writer audit.

## Stage 6 — contract discount lifecycle
- Test-only CreateSchema31Test reuses existing numbering and transactional creation, snapshots contract discount, initializes override/manual/section fields and uses today's date for period end. Test-only ToggleSignedSchema31Test keeps the saved rate on signing and refreshes it on unsigning only if neither an override nor a manual total exists.
- SaveContractDiscountSchema31Test atomically changes the contract rate and only unadjusted unsigned acts, validates form, rate, locked contract and stage, logs one change and advances revision. Explicit zero-percent and zero-money overrides are preserved; prices/sections untouched.
- All three writers use the existing dedicated test connection; WPF and normal v31 writes remain blocked. Tests added; compile/SQL integration and lifecycle acceptance remain unrun. Next: build/SQL roundtrip harness, WPF editing and wider writer audit.

## 25.11.2026 — рабочий запуск C# на DIMA, v0.1.5
По явному указанию владельца создан согласованный SQLite-снимок рабочего base.db через Backup API в режиме чтения. Пользователь подтвердил закрытие всех клиентов Python. После подтверждения ревизия исходника совпала с ревизией снимка. Исходный сетевой файл не изменялся. Отдельный base БЭКАП.db проверен на целостность и сохранён отдельно.
Снимок уже имел схему 31. Импорт в GmcSmetaPilot_Test_Current1125 сверил 26 таблиц и 57 992 строки. С помощью расчётов Python совпали итоги 36 документов, 38 актов и одной группы этапов. Подтверждены 25 активных доверенных внешних ключей. DBCC CHECKDB прошёл. До переключения сделан BACKUP COPY_ONLY CHECKSUM и RESTORE VERIFYONLY WITH CHECKSUM, файл E:/sql/MSSQL14.SQLEXPRESS/MSSQL/DATA/GmcSmeta_Cutover_20261125_before_first_CSharp_write.bak. Уже проверенный каталог переименован в GmcSmetaPilot_Cutover_20261125.
Раньше C# отклонял все рабочие Cutover-каталоги. SqlSchemaCompatibility.cs и SqlServerDatabase.cs добавляют узкое исключение только для нового каталога через .\SQLEXPRESS. Open и OpenAsync подтверждают фактический DIMA\SQLEXPRESS и Shared memory, затем проверяют схему 31, required columns и READ_WRITE. Другие Cutover и сетевые адреса остаются заблокированы. SqlSchemaCompatibilityTests.cs проверяет границы допуска. Удалённые ПК не переключены: при проверке TLS на TCP сертификат сервера не доверен.
На DIMA «ГМЦ-Смета С.exe» открывает рабочую SQL-базу напрямую. Отдельный «ГМЦ-Смета C# (тест).exe» открывает прежнюю копию. Проверены фактический SQL-сеанс процесса и главное окно, штатное чтение 36 документов без ошибок и расхождений. Тестовые записи в рабочие документы не выполнялись. Release собрался без ошибок, 1258 тестов прошли. Локально установлено, публичный релиз не опубликован. Для новых изменений на DIMA использовать рабочий C#; старый SQLite сохранён как источник до перехода.

## 25.11.2026 — сборка v0.1.6 и развёртывание на других ПК, PARTIAL
Используется публичный GitHub Releases. Отдельное вложение GMC-Smeta-CSharp-Start.exe устанавливается локально и создаёт рабочий ярлык, переиспользует существующие SHA-256, версионную установку и фоновую проверку. Файлы OfficeLauncher.cs, Program.cs, AppPaths.cs, AppInstaller.cs, csharp-release.yml. Пароли и токены для рабочего запуска не используются.
SqlSchemaCompatibility.cs, SqlServerDatabase.cs и тесты допускают точный TCP-адрес рабочего каталога только при Windows-аутентификации и проверяемом TLS, без отмены проверки сертификата и замены имени/закреплённого сертификата в строке. Проверяются фактический DIMA/SQLEXPRESS, TCP и схема 31; локальное подключение остаётся Shared memory. Остаток: на сервере нужен SQL-сертификат, доверенный клиентами, и подтверждённые Windows-учётные записи. DIMA в рабочей группе. Сетевой TLS сейчас завершается ошибкой недоверия. Рабочая БД для проверки сборки не менялась. Публикация v0.1.6 запрошена через workflow, результат проверяется отдельно.

### 25.11.2026 — TLS и доступ из офисной сети, v0.1.7
На DIMA установлен самоподписанный сертификат SQL Server с SAN DIMA и 192.168.1.46. Ключ неэкспортируемый, служба SQLEXPRESS получила чтение ключа. Перед коротким перезапуском проверено отсутствие пользовательских сеансов, кроме служебной телеметрии. SQL error log подтвердил загрузку сертификата. Публичный сертификат сохранён локально как DIMA-SQL-Office.cer. SHA-256 2F85A2F52A19A3D0409ECBEA269757992157A442AB76143F2DF1CA8D54295557. На DIMA он добавлен в доверенные сертификаты компьютера. В Windows Firewall добавлено правило входящего TCP 1433 для Ethernet, локального адреса 192.168.1.46 и источников 192.168.1.0/24. Другие существующие правила не изменялись.
ODBC подтвердил TCP и шифрование с проверкой сертификата. C# ранее завершал начальное согласование по тайм-ауту 5 секунд. В SqlServerDatabase.cs тайм-аут точного офисного TCP-профиля увеличен до 20 секунд. С контрольной проверкой сертификата C# прочитал 36 рабочих документов без ошибок и расхождений. Записи в рабочие документы не делались. Новый выпуск готовится напрямую из main без GitHub Actions. На других ПК ещё нужны доверие публичному сертификату и проверка Windows-доступа с учёткой сотрудника.
