# Issue #33 — P0 №5: KS-2/KS-3 acts parity, verification ledger

Branch: `feature/ks-acts-full-parity`, PR [#41](https://github.com/xBonkx/CsharpGMC/pull/41), base `4e33467c876eebacfaa68dd0ace9972670a4a4b1`.
Python references: `python/sql-server-current/smeta/acts.py`, `act_qty.py`, `ui/ks_view.py`, `export_ks*.py`, `export_vor.py`.
All writes restricted to Microsoft SQL Server `GmcSmetaPilot_*`; production untouched; PR must **not** be merged automatically.

## Functional matrix

| Python | C# | Implementation | Acceptance |
|---|---|---|---|
| `create_act` / `next_number`, DS header inheritance | `SqlServerActs.Create`, `KsActNumbering` | Implemented | SQL 2-client/multi-DS numbering scenario not executed |
| `_save_header`, `_save_smr`, `_save_tr`, PNR, `_save_by_sys` | `SqlServerActs.SaveDraft`, `ActsEditorView.Parity` | Implemented | Windows UI / SQL Pilot not executed |
| `smr_by_system` | `KsActAllocation`, `SqlServerActs.SmrBySystem` | Implemented | Compare Python fixtures including `round_rows` and customer print integration |
| `ks3_gross_cumulative` / initial opening | `Ks3OpeningBalance`, `KsGroupReport`, `SqlServerActs.Ks3Figures` | Implemented | SQL imported fixtures and Python/Excel comparison pending |
| Contract + DS KS-3 grouping | `SqlServerKsContractScope`, `KsWorkspaceProjection` | Existing / extended | Same-agreement multi-DS fixture pending |
| `_toggle_signed`, `delete_act` | Compare-and-set, audit, locked guards in `SqlServerActs` | Implemented | Signed SQL Pilot, concurrent transactions pending |
| `apply_quantities`, formula, fill/clear | Existing `SqlServerActs`, `ActQuantityFormulas`; frozen line prices retained | Extended | SQL pilot formula rollback and 2500–10000-row performance pending |
| `add_extra_line` | Existing `AddExtraLine`, corrected append ordering | Implemented | SQL Pilot persistence pending |
| `update_extra_line` | `SqlServerActs.Extras.UpdateExtraLine`, copy-on-write on shared names/units | Implemented | Shared signed act + pricing SQL Pilot pending |
| `move_extra_line` | `KsExtraOrdering`, `SqlServerActs.MoveExtraLine` + WPF buttons | Implemented | Actual SQL/WPF row ordering pending |
| `drop_extra_position`, clear act | `DeleteExtraLine`, `CleanupOrphanExtra`, protected `Delete` / `SetQty` cleanup | Implemented | Procurement/movement/shipment FK regression pending |
| Standard, DNS, R4 KS-2/KS-3, VOR, PDF | Existing customer exporters; additional print-area/opening checks | Extended tests | CI + actual customer golden print preview pending |
| Dneprovsky | Original proprietary workbook absent | **BLOCKED** | Must receive native template; no fallback |
| Unknown COMMIT reconciliation | UI does not auto-retry; requires reread | Partial | Fault injection using pilot network proxy not available |

**'Implemented' is not 'verified'**. This PR remains a draft until acceptance.

## Offline monetary and position tests

- `KsActsParityTests`: cents for SMR systems and draft validations.
- `KsActNumberingTests`: shared KS-3 across main contract / DS.
- `Ks3OpeningParityTests`: initial gross opening cutoff across accounting years.
- `KsActMoneyGoldenTests`: VAT included/on top, PNR, transport, adjustments, `rows_exact`, `round_rows`, 2500 / 10000 synthetic line totals.
- `KsExtraOrderingTests`: order, anchored moves, hidden extra positions and invalid anchors.

The Windows `csharp-build.yml` offline-test filter includes these suites and compiles the opt-in SQL Pilot tool; an actual successful run is still required.

## Document tests

`csharp/tests/GmcSmeta.Export.Checks/Program.cs`:
- Standard/DNS/R4 KS-2 + KS-3 XLSX, VOR XLSX, PDF; 2 and 80 synthetic lines.
- XLSX round-trip reopen, nonempty content, **print areas after reopening**, financial KS-3 totals, workbook formulas, multi-act cross-sheet references.
- PDF re-open and page count.
- DNS/R4 initial gross opening in KS-3 year/start cells.
- Dneprovsky non-fallback guard.

These are synthetic export samples, **not a Python goldens or real customer print-preview comparison**. Print typography, special signatures and actual PDF page breaks still need real fixtures.

## SQL Pilot runner: not automatically executed by CI

An opt-in test harness is at `csharp/tools/KsPilotChecks`:

```powershell
$env:GMC_KS_PILOT_CONNECTION = 'Server=localhost;Database=GmcSmetaPilot_TEST;Integrated Security=true;TrustServerCertificate=true;'
$env:GMC_KS_PILOT_CONTRACT_ID = '123'  # replace with an isolated seeded test contract
dotnet run --project csharp/tools/KsPilotChecks/KsPilotChecks.csproj --configuration Release
```

For a **dedicated disposable** pilot contract (not a shared working pilot scenario), explicitly opt into committed extra-line regression writes:

```powershell
$env:GMC_KS_PILOT_MUTATION_SUITE = 'YES'
dotnet run --project csharp/tools/KsPilotChecks/KsPilotChecks.csproj --configuration Release
```

This creates two temporary KS-2, shares an out-of-KP position, signs one act, changes the other's price and name (copy-on-write), verifies the signed act is preserved, checks deletion barriers and orphan cleanup, then deletes the disposable acts on success. **If the command fails mid-transaction, it prints the new act/position IDs for manual reconciliation; don't blindly retry.** The test is intentionally not enabled in default CI.

The tool rejects a DB name not beginning `GmcSmetaPilot_` and checks the **actual** database returned by SQL Server. It requires at least one existing KS-2. It verifies stale revision rejection, signed act protection when seeded, live act totals, KS-3/print SQL snapshots, serializable `meta.rev` update + explicit rollback, and dual-client reads. It prints explicit `BLOCKED` statements for missing signed acts or too few positions. No successful SQL Pilot run has been observed for this branch. This does **not** replace 2500–10000-row write load tests, SQL fault-injected unknown COMMIT tests or a customer document comparison.

## CI issue observed on previous head

On SHA `58cb86fa0eaddc52f3a95e53f23128836078ad74` every PR workflow returned `failure` **before its first job step**. The jobs showed `steps: null` and the GitHub log endpoint returned `404 BlobNotFound`. Manual re-runs of Windows build and KS export reproduced this; exact underlying GitHub runner/account reason is **not confirmed** from the exposed API. By contrast, `main@4e33467c` had a successful WPF workflow. This should not be described as a C# compilation failure without an actual error log. CI on the current branch head must be inspected separately.

## Remaining acceptance gates

1. Obtain successful Windows build, offline money tests and export tests on **one immutable final SHA**.
2. Run seeded `GmcSmetaPilot_*` for main contract + several DS / signed KS, copy-on-write, cleanup retaining procurement/shipment references, two clients, 2500/10000 positions, rollback and unknown COMMIT reconciliation.
3. Compare **real frozen input** in Python and C# to the kopeck and exported XLSX/PDF/print areas/print previews (standard, DNS and R4).
4. Treat Dneprovsky as **BLOCKED** until the actual external template arrives.
5. No direct push to `main`; coordinator merges after acceptance.

## 25.11.2026 — проверка копеечного паритета Python/C#, исправление после v1.0.0, не опубликовано
Сверка Python aa6b34dcf0314df93086a297f01d549ff58cf705 и C# на согласованном SQLite-снимке v31 от 25.11.2026 15:40 и его отдельном SQL-импорте GmcSmetaPilot_Test_ParityCurrent1125. Импорт проверил 26 таблиц, 57 992 строки и 25 внешних ключей. Рабочая база не открывалась. Изменения после снимка вне прогона.
- До правки суммы материалов, СМР, НДС, транспорта, ПНР и итогов 36 документов, а также 38 КС-2, включая 20 ДНС, уже совпадали до копейки. В четырёх одиночных КС-3 накопительные суммы расходились на копейку. В некоторых групповых КС-3 по пустому номеру договора C# выделял документы по отдельности, Python объединял их внутри объекта.
- SqlServerActs.cs и KsGroupReport.cs теперь используют расчёт одиночной КС-3 от суммарной оплаты с выделением НДС после сложения, как Python ks3_figures. Промежуточные суммы предыдущих актов также выделяют НДС из оплаты. Правило импортированного начального остатка сохраняется. SqlServerKsContractScope.cs теперь совпадает с Python по объединению основного договора и ДС внутри объекта при пустом номере, не объединяет разные объекты.
- После правок совпали 36 документов, 38 КС-2 и 38 одиночных КС-3 с предыдущими суммами. Совпали 55 групповых расчётов КС-3 по 13 исходным документам, состав групп, суммы периода, накопительный итог года и с начала работ. Совпали количество и остаток по 1058 строкам таблицы этапов.
- Добавлен тест накопительного округления и обновлены ожидаемые значения для одиночной КС-3 и пустого номера договора (Ks3FullTotalsTests.cs, KsContractScopeTests.cs, KsGroupReportTests.cs). Release собрался без ошибок, 1259 тестов прошли, экспортные проверки XLSX/PDF, включая ДНС и Р4, прошли на синтетике. Сверка визуального оформления реальных файлов Python/C# в этот прогон не входила.
- Текущая опубликованная версия v1.0.0. Исправление готово в main, сборка для автообновления в этом прогоне не публиковалась.
