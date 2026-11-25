# GMC-Смета — SQL Pilot E2E / P0 №9

Source: [issue #43](https://github.com/xBonkx/CsharpGMC/issues/43).
Branch: `test/sql-pilot-e2e-regression`. New test executable:
`csharp/tests/GmcSmeta.Pilot.E2E/GmcSmeta.Pilot.E2E.csproj`.
Workflow: `.github/workflows/sql-pilot-e2e.yml`.

## Safety contract

1. This harness NEVER accepts an existing working or pilot estimate DB as its admin connection. `GMC_PILOT_E2E_ADMIN` must explicitly have `Initial Catalog=master`. For remote SQL hosts it additionally requires `GMC_PILOT_E2E_ALLOW_EXTERNAL=1` and an expected exact actual SQL `@@SERVERNAME` value in `GMC_PILOT_E2E_EXPECTED_SERVER`.
2. It creates a new, GUID-suffixed `GmcSmetaPilot_E2E_*` DB. Before any user-data write it verifies actual `DB_NAME()`, actual `@@SERVERNAME` and a nonce in `dbo.__pilot_provenance`. Creation and owner-marker bootstrap are necessarily privileged administrative DDL against `master`; these are the only exceptions to the per-write test-DB assertion.
3. It drops **only its own** marker-verified DB on exit, never by prefix or by selecting old `GmcSmetaPilot_*` names. If provisioning fails before the marker is stored, it leaves the temporary DB for manual inspection rather than deleting an unverified DB. The CI Docker container is destroyed afterwards.
4. The schema is a **synthetic partial SQL Server v28 fixture**, expanded in issue #72 for supplier, procurement, movement, shipment, shipment lines and tags, but still not the complete 26-table reference. It is not the full production v28 DDL, nor a SQL Server data migration or a substitute for restored representative test datasets. Rows contain generated synthetic labels; neither credentials nor real estimate data go to logs.
5. Domain mutations exercise existing public C# APIs. Test-only direct SQL is used solely for seeding large synthetic datasets, setting test preconditions, injecting a trigger failure and independent read-back assertions. `main` and existing business modules are not modified.

## Reproduce

### GitHub Actions

Open the branch or PR Actions tab; choose **GMC SQL pilot - disposable E2E and load regression**. Two independent jobs run:

- Windows / .NET 10: build the standalone pilot against the current C# Core assembly.
- Ubuntu / .NET 10 and SQL Server 2022 Developer: start a disposable local Docker server with a randomly generated, masked, per-run password; create a fresh test DB; execute the tests; write JSON/Markdown; drop owned DB; remove Docker container. Hosted workflows are **manual-only**, and an unlaunched run means neither PASS nor FAIL.

Results are uploaded under the `gmc-sql-pilot-e2e-<sha>` artifact and added to the run Summary. Even if a test fails, the report upload step runs. An absent SQL Server is an error / exit code 2, not a green skip.

### Local Windows PowerShell

Use a **dedicated disposable SQL Server instance** under your control. For example, with Windows authentication on a local instance:

```powershell
$env:GMC_PILOT_E2E_ADMIN = 'Server=localhost;Initial Catalog=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;'
$env:GMC_PILOT_E2E_REPORT_DIR = '.\artifacts\sql-pilot-e2e'
dotnet run --project csharp/tests/GmcSmeta.Pilot.E2E/GmcSmeta.Pilot.E2E.csproj -c Release
Remove-Item Env:\GMC_PILOT_E2E_ADMIN
Remove-Item Env:\GMC_PILOT_E2E_REPORT_DIR
```

For a remote SQL instance, obtain the actual SQL server identity with a **read-only** query `SELECT @@SERVERNAME` and explicitly set `GMC_PILOT_E2E_ALLOW_EXTERNAL=1` plus `GMC_PILOT_E2E_EXPECTED_SERVER`. Only do this for a dedicated test instance; this is not consent to run against your production host. Never save a password in the repository or include the connection string in your report.

## Coverage matrix

| Scenario | Automation | Important limits |
|---|---|---|
| Public CreateObject / CreateEmptyContract / AddSection / AddPosition / SaveCell | SQL API tests | Against synthetic partial v28 schema |
| 2,500 and 10,000 rows | SqlBulkCopy fixture + counted SQL reads | SQL read only; does not benchmark WPF, UI virtualization or complete EstimateReader |
| Two clients / conflict | Two parallel calls to real revision-guarded SaveCell, independent SQL connections | One winner; one rejected; stored value verified |
| Transaction fault / rollback | AFTER INSERT trigger throws during real SaveCell | Check price, unsigned act-line, journal and global revision all restored |
| Unknown COMMIT | Real successful SaveCell, deliberate lost-acknowledgement simulation, independent reconciliation | **Not** an actual packet loss or TCP outage; no blind retry |
| Signed acts and prices | Signed act line retains previous price while unsigned updates; locked contract rejects retail-price write; public SetQty/ToggleSigned/Delete; synthetic main/DS with KS-3 opening | Create-act, real document round-trip and goldens still untested |
| Stage operation | Public CreateStage/StagesOf and isolated CreateRest/SetStageQty/StageTable, child scaling, overflow rejection | Only synthetic, migration-backed stage/posted issue interaction remains untested |
| Bulk work prices | Public SetWorkPrice, 120 rows with SMR positional mode | Includes the actual Core transaction; no WPF |
| Persistence after restart | New `dotnet` process opens DB and checks stored note and revision | Does not restart the actual WPF application |
| Reading / writing / mass operations | 21 warm-cache raw SQL reads and 21 reads each via public BuyRows and ProgressForContract on 2500/10000, 20 SaveCell calls, 1 bulk-write and 1 120-row API batch | Wall time, host-dependent; service latencies exclude WPF |

**Explicit SKIP categories:** native complete v28 schema and migrations; full live DS/KS document and financial golden parity; public API cancellation; complete supplier requests/exports and monetary parity; real physical TCP loss across COMMIT; actual WPF restart/Windows GUI. Missing APIs or a simplified fixture must never produce fake PASS results. Expand each group after its P0 PR merges and the full SQL v28 schema becomes available.

## Reports and acceptance

The executable writes `report.json` and `report.md` with full **GITHUB_SHA** (or local git SHA), timestamp, server identity/version, generated DB name, pass/fail/**skip** count and per-operation p50/p95/p99 in milliseconds.

One-shot bulk timings contain a single measurement. The report labels their p50/p95/p99 as non-statistical. The 2,500/10,000 reader measures raw SQL query/transfer, not the full UI refresh. Use the same commit, same runner and stable SQL Server resources for comparisons; do not set arbitrary performance thresholds before a baseline exists.

A failure in an implemented scenario makes the process return a nonzero exit code. CI is **not accepted** solely on successful compilation: require the real-sql job and its detailed artifact. A failed invariant should become a reproducible issue for its owning P0 agent rather than a business-module fix in this branch.


## Issue #72 (F07): extended integration acceptance, 21 November 2026

**Implementation branch:** `test/fullschema-sql-pilot-e2e`, starting at
`d634845706735535d9b02ec44ded69f9835e1771`. GitHub issue:
[#72](https://github.com/xBonkx/CsharpGMC/issues/72).
This is an **extension of PR #52's test harness**, not a replacement.

### Schema provenance / actual limits

The repository's `csharp/schema_reference.sql` describes 26 legacy
SQLite-style tables, not a native SQL Server v28 migration. The harness can
create a nonce-marked **16-table test-only subset** plus
`dbo.__pilot_provenance`. It adds `tags`, `suppliers`, `procurement`,
`movements`, `shipments`, and `shipment_lines` with isolated test FKs.
It still lacks at least `access_lists`, `attachments`, `cars`,
`metal_entries`, `people`, `presence`, `price_history`,
`smr_costs`, `spec_revisions`, and `trash`.
The value `meta.schema_version='28'` **does not prove full v28 DDL**.

No production migration was invented, copied from a live DB or run.
`full-production-v28-schema-and-migrations` must remain **SKIP** until a
reviewed native SQL Server migration/DDL is delivered by its owner, run
against a fresh disposable instance, and checked table-by-table, column-by-column
and FK-by-FK. Do **not** interpret successful new domain tests as full-schema
acceptance.

### Python reference → C# public API → verification

| Python reference | C# public SQL API | Pilot E2E evidence | Not established |
|---|---|---|---|
| `stages.py`: full/stage/rest, proportional child quantity | `SqlServerStages.CreateStage/CreateRest/SetStageQty/StageTable` | Newly added 2→1 allocation, 4→2 child, 1 rest, negative and over-allocation rejection | All signed/posted stage variants, 10k-row WPF batch |
| `acts.py`: quantities/sign, KS-3 inception | `SqlServerActs.SetQty/ToggleSigned/Delete/ActsOf/Ks3Figures` | SQL-backed signed edits and synthetic main+DS, 2 acts, gross opening marker | Native creation of all real KS/DS, exported golden documents |
| `procurement.py`: purchase stage/status/movements | `SqlServerProcurement.SetProc/AddMovements/BuyRows` | Two-row receipt, supplier, manual purchase-price preservation, cross-contract rollback | All supplier requests, end-to-end cost/margin |
| `shipments.py`: fixed issue and movement history | `SqlServerProcurement.IssueViaListBatch` | Two issued rows, FK-backed list, ledger assertions, stale duplicate request blocked by revision | New-revision replay/idempotency key, overspend policy |
| `builder.py` / `db.py`: positions and global revision | `SqlServerDatabase.AddPosition/SaveCell/Revision` | 2500/10000 synthetic rows, two independent client writes, zero/negative and stale-revision checks | WPF responsiveness and business UX |
| `acts.py` / `procurement.py`: large filtered projections | `SqlServerActs.ProgressForContract` and `SqlServerProcurement.BuyRows` | 21 timed actual service reads per size, counts verified | WPF repaint/virtualization, cold-cache SLAs |

The SQL bulk fixture insertion is labelled `sql_bulk_fixture_insert`, NOT
an application benchmark. The public service timings are separate operation
keys (`public_procurement_buy_rows`, `public_ks_progress_for_contract`).
Each measurement reports its sample count; p99 on 21 samples approximates
the maximum and should not be treated as a production-grade tail estimate.

**Fault categories:** the existing after-INSERT SQL trigger tests transaction
rollback and `meta.rev` conservation; a committed note with suppressed
acknowledgement is read back on a fresh independent connection and not retried.
This is a **simulated lost ACK**, not a physical TCP cut. Reuse the same
`expectedRevision` for a double submission: a second issue/movement must
not duplicate rows. A *new-revision blind retry after a lost ACK* is still a
product design gap requiring an idempotency key or reconciliation.

### Execution / acceptance sheet

Only run on a dedicated SQL Server test instance. The runner validates both
the explicit `master` admin catalog and the actual `@@SERVERNAME`;
each test connection checks `DB_NAME()` plus its process-owned nonce.
Cleanup never enumerates or deletes shared `GmcSmetaPilot_*` databases.

```powershell
dotnet restore csharp/GmcSmeta.slnx
dotnet build csharp/GmcSmeta.slnx -c Release --no-restore
dotnet test csharp/tests/GmcSmeta.Core.Tests/GmcSmeta.Core.Tests.csproj -c Release --no-build

# Only with a dedicated disposable SQL instance:
$env:GMC_PILOT_E2E_ADMIN = 'Server=localhost;Initial Catalog=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;'
$env:GMC_PILOT_E2E_REPORT_DIR = '.\artifacts\sql-pilot-e2e'
dotnet run --project csharp/tests/GmcSmeta.Pilot.E2E/GmcSmeta.Pilot.E2E.csproj -c Release
```

The executable writes `report.json` and `report.md` with the exact
execution SHA (`GITHUB_SHA` or local Git HEAD), actual SQL Server identity and
version, generated DB name (or `not created`), environment, PASS/FAIL/SKIP
per test, wall elapsed and process CPU milliseconds, and p50/p95/p99 of
measured SQL operations. Network *byte counts are not instrumented* and must
not be inferred. Report write failure itself causes a FAIL exit code.

| Gate | Status until actually run | Why |
|---|---|---|
| Source-only inventory and changes | Implemented; **not SQL PASS** | Connector inspection only |
| Windows `dotnet restore/build/test` | **SKIP** in this environment | No `.NET` 10 SDK / Windows here |
| Actual SQL 2-client, stage/KS/DS/procurement E2E | **SKIP** until local execution | No test SQL Server supplied to this runner |
| Real full SQL Server v28 migration | **SKIP** | Native full DDL not in repository |
| Physical TCP loss, WPF, PDF/XLSX/DOCX | **SKIP** | No compatible environment or controlled physical fault |
| Hosted GitHub Actions | **SKIP** until manually dispatched | Billing policy: manual-only |

**Before the coordinator accepts F07:** independently inspect the emitted
`report.json` and `report.md`, actual DB identity, timing distribution,
failing assertions, and any safety blocker. A build-only or zero-SQL run
is NOT SQL Pilot acceptance. The #54 Windows build verification gate still
applies, unless a newer local log establishes a confirmed release build.
