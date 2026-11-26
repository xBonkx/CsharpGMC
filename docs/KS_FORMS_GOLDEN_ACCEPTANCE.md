# P0 #60 — КС-2 / КС-3 / ВОР: golden acceptance ledger

**Issue:** https://github.com/xBonkx/CsharpGMC/issues/60
**Branch:** `test/ks-forms-money-golden`
**Base main SHA inspected:** `d634845706735535d9b02ec44ded69f9835e1771` (PR #75 merged).
**Later main update reviewed:** `43e61155ddf0194a70808b50a3b4dbdaa8e12568`
(only `BUILD_GMC_SMETA.bat` added; unrelated, safe to integrate).
**Environment during preparation:** GitHub source read / remote branch edits; no local
Git checkout, Windows/.NET SDK, Excel desktop or SQL Pilot connection.
**Important:** existing code or a recorded test case is not a test PASS.
Issue #54 still requires a Windows build on this exact integrated main.

## 1. Source and template inventory (verified against the source tree)

Python reference root: `python/sql-server-current/smeta/`.
Exporters actually present:
`export_ks.py`, `export_ks_dns.py`, `export_ks_r4.py`,
`export_ks_dneprovsky.py`, `export_ks_pdf.py`, `export_vor.py`,
and `export_pdf.py` (the last is primarily KP, positional KP,
procurement and shop-order PDF, **not** a KS customer PDF substitute).

Exactly these five **bundled XLSX** source assets were found:
`assets/ks_dns_template.xlsx`, `assets/ks_r4_template.xlsx`,
`assets/vor_template.xlsx`, `assets/vor_dns_template.xlsx`,
`assets/vor_r4_template.xlsx`. They are read-only golden inputs,
not newly generated examples. C# resources are embedded via the WPF project.

C# existing implementation (do not rewrite):
`KsCustomerForms.cs` (form dispatch / embedded template);
`KsExcelForms.cs` (standard KS2 / grouped KS3);
`KsVorForms.cs` (VOR);
`KsPdfForms.cs` (generic printable PDF);
`SqlServerKsPrintData.cs` (contract-bound read-only print projection);
`SqlServerKsContractScope.cs`, `SqlServerKsRequisites.cs`;
`SqlServerActs*.cs`, `ActsEditorView.*`;
`KsFinancialPolicy.cs`, `KsActAllocation.cs`, `Ks3OpeningBalance.cs`.
Prior test owners: `GmcSmeta.Export.Checks`, `GmcSmeta.Core.Tests`,
`csharp/tools/KsPilotChecks`, and PR #41 (merged).
Existing Python↔C# ledger: `KS_ACTS_PARITY_ISSUE33.md`,
`KS_EXPORT_PARITY.md`. Do not reassert their old CI outcomes as new acceptance.

### Matrix: implemented != accepted

| Python path and form | C# path | Actual source template | Implementation | Golden / acceptance |
|---|---|---|---|---|
| `export_ks.py` standard KS-2/KS-3 | `KsExcelForms` via `KsCustomerForms` | Programmatic Python layout | SUPPORTED | PARTIAL: no same-contract Python/C# XLSX and Excel print preview |
| `export_ks_dns.py` DNS | `KsCustomerForms` DNS | `ks_dns_template.xlsx` | SUPPORTED | PARTIAL: source workbook present; real recalc/print/signatures not verified |
| `export_ks_r4.py` R4 | `KsCustomerForms` R4 | `ks_r4_template.xlsx` | SUPPORTED | PARTIAL: source workbook present; R4 work/SMR, rich styles, pagination need comparison |
| `export_ks_dneprovsky.py` | `KsCustomerForms` fail-closed | **External UNC workbook absent** | BLOCKED | **EXTERNAL TEMPLATE REQUIRED**; no standard-form substitution |
| `export_vor.py` standard | `KsVorForms` | `vor_template.xlsx` | SUPPORTED | PARTIAL: quantity/height/print structure only synthetic |
| `export_vor.py` DNS | `KsVorForms` | `vor_dns_template.xlsx` | SUPPORTED | PARTIAL: system headings, appendix and signatures need real parity |
| `export_vor.py` R4 | `KsVorForms` | `vor_r4_template.xlsx` | SUPPORTED | PARTIAL: 761-row template resizing and native print need Windows |
| `export_vor.py` Dneprovsky object | `KsVorForms` currently selects standard VOR | Only `vor_template.xlsx`; no native Dneprovsky VOR discovered | PARTIAL | **BLOCKED for native customer acceptance**. Python also dispatches to `_vor_std` unless R4/DNS; verify customer policy before calling it native |
| `export_ks_pdf.py` standard/DNS/R4 | `KsPdfForms` | HTML/Qt reference vs PDFSharp approximation | PARTIAL | PDF open/page checks exist; typography, exact pagination and customer-form visual parity **not** established |
| `export_ks_dneprovsky.py` PDF equivalent | `KsPdfForms` rejects Dneprovsky | Original customer print rules absent | BLOCKED | EXTERNAL TEMPLATE REQUIRED |
| `db.Act` money; `acts.py` group, extras | `KsFinancialPolicy`, `KsActAllocation`, `KsGroupReport`, SQL acts | Source computation, not a workbook | SUPPORTED in code | PARTIAL: new offline source-oracle comparison is runnable but SQL/revision/persistence not verified |

Status definitions: **SUPPORTED** = implementation/code resource exists,
not full QA; **PARTIAL** = implementation or acceptance incomplete;
**MISSING** = needed implementation absent; **BLOCKED** = needs an external
original/environment or documented customer decision. There is no credible
full-parity assertion for any unexecuted comparison.

### Dneprovsky: exactly what is missing

Python `export_ks_dneprovsky.py` looks for the original customer workbook at
`//SERV/Free/Консоль-Н/Договора/СТРОЙ/2025/4 Днепровский ПСК/выполнение/0204 выполнение №1-№10.xlsx`.
It reads `кс-2 №9п` and `кс-3 №10` as source sheets and historic
`кс-3 №N` sheets for cumulative frozen prior figures. No such workbook
appears in the repository. Before acceptance, obtain an authorized
redacted/test-safe original with the relevant worksheets, cached external
references and section balances, an authentic customer-accepted KS2/KS3
output example, the correct signatures/requisites and print area/page-break
rules, and an explicit VOR form policy. **Do not** commit private customer
originals or silently use a generic replacement.

## 2. New isolated reproducible fixtures

All files are within `csharp/tests/GmcSmeta.KsGoldenChecks/`:

- `Program.cs`: **19 required synthetic C# cases**, no database: round_rows
  vs rows_exact, included/on-top VAT and adjustment, per-system SMR residual;
  300/2500/10000-row arithmetic; per-form KS2/KS3 XLSX and VOR at 2 and
  300 lines; PDF reopening (2 lines); signed/unsigned multi-DS group and
  cross-sheet formulas; DNS/R4 gross opening; DNS row-formula rounding; R4 positional SMR; Dneprovsky KS/PDF fail-closed.
  Exact monetary tolerance = **0.01 ₽**, not percent. Output is `PASS/FAIL`
  per case plus totals; nonzero exit on failure. The 2500/10000 case is
  **arithmetic**, NOT a claim that a 10000-row XLSX or SQL transaction passed.
- `python_oracle.py`: imports the **actual archived Python `smeta.db.Act`**
  in memory and creates six synthetic monetary vectors (including 300, 2500,
  10000 lines) without initializing a DB. C# compares its numbers when the
  `GMC_KS_PY_ORACLE` path is set. Missing Python oracle reports explicit
  **SKIP**, not a fabricated parity pass.
- `verify_exports.py`: independently reads the **14** optional synthetic
  C# XLSX/PDF artifacts using `openpyxl`/`pypdf` and checks sheet structure,
  printed area, formula presence, VOR data, PDF page structure. A missing
  output is **SKIP**. This is a second parser, not pixel proof or Excel recalc.
- `compare_python_csharp.py`: a strict two-workbook diagnostic diff of
  sheet names, values (0.01 tolerance), formulas (flagged for review),
  print areas, row heights and merges for frozen same-contract XLSX pairs.
  Missing paired exports means **SKIP**; matching structure alone is not
  proof of Excel formula evaluation or pixel-identical customer print.
- `README.md`: precise Windows PowerShell reproduction commands.

All fixtures use fictitious organizations and positions. The suite never
opens a SQL connection and never writes to the original Python assets.

## 3. Acceptance and safety plan / independent gates

| Gate | How to verify | Current result |
|---|---|---|
| Static inventory and available template paths | Checked repository tree and source | **PASS (source inspection only)** |
| `git status` and local dirty-file preservation | Requires a local checkout; GitHub branch created directly from immutable main SHA | **SKIP** (no local checkout; no existing files overwritten) |
| Core Windows .NET 10 restore/build/xUnit on final branch SHA | Commands below, save exit code and actual passed/failed/skipped | **SKIP**: Windows/.NET 10 not available here; #54 open |
| New 19 C# synthetic golden cases | `dotnet run` command below, save stdout/stderr | **SKIP**: not executed here |
| Paired real Python `Act` / C# monetary oracle (6 vectors) | Run `python_oracle.py` then `GMC_KS_PY_ORACLE` | **SKIP**: two runtimes not executed against same source checkout |
| Independent parse 14 XLSX/PDF artifacts | Set output directory, run `verify_exports.py` | **SKIP**: no generated files yet |
| Python and C# authentic KS2/KS3/VOR semantic/visual comparison | Freeze **same synthetic contract**, export both, compare values/signature fields, styles, print areas, height and PDF raster images | **SKIP**: no paired output; never claim golden visual PASS |
| Windows Excel opening/recalculation/print preview (100%, 125%) | Use Excel and PDF viewer on synthetic files, report dimensions/pages/screens | **SKIP**: no Windows desktop |
| SQL: 2 clients, meta.rev, signed/locked and history, FK, copy-on-write extras, rollback | Existing `csharp/tools/KsPilotChecks` on explicit disposable seeded pilot contract, plus assertions below | **SKIP**: SQL Pilot not connected |
| SQL: 2500 and 10000 rows, failures and unknown COMMIT | Fault injection + post-commit readback, no retry until reconciliation | **SKIP**: unsupported without isolated SQL and fault fixture |
| External Dneprovsky XLSX/PDF acceptance | Customer original and policy listed above | **BLOCKED: EXTERNAL TEMPLATE REQUIRED** |
| GitHub Actions hosted workflow | Manual-only due to billing; a missing run does not mean build failure or PASS | **SKIP**: not triggered |

No SQL production database, real KS/DS, signed acts, file archives, or live
customer templates were changed as part of this branch.

## 4. Exact commands (Windows, from repository root)

```powershell
git status --short --branch
git fetch origin
git rev-parse HEAD
dotnet --info
dotnet restore csharp/GmcSmeta.slnx
dotnet build csharp/GmcSmeta.slnx -c Release --no-restore
dotnet test csharp/tests/GmcSmeta.Core.Tests/GmcSmeta.Core.Tests.csproj -c Release --no-build

$oracle = Join-Path $env:TEMP "gmc_ks_python_oracle.json"
python csharp/tests/GmcSmeta.KsGoldenChecks/python_oracle.py --output $oracle
$env:GMC_KS_PY_ORACLE = $oracle
$env:GMC_KS_GOLDEN_OUTPUT = Join-Path $env:TEMP "gmc_ks_golden_synthetic"
dotnet run --project csharp/tests/GmcSmeta.KsGoldenChecks/GmcSmeta.KsGoldenChecks.csproj -c Release
python csharp/tests/GmcSmeta.KsGoldenChecks/verify_exports.py --samples $env:GMC_KS_GOLDEN_OUTPUT
```

Record each command's actual exit code, exact checkout SHA, Passed / Failed /
Skipped numbers, version, and link only synthetic artifacts. The golden
console project is intentionally **not** injected into the shared `.slnx`
(to avoid parallel ownership conflicts); `dotnet run --project` restores it.

### SQL Pilot: only with a separately confirmed disposable test contract

The **existing**, independent runner `csharp/tools/KsPilotChecks` is the
starting point. First verify actual server and `DB_NAME()`, not merely
a trusted configuration filename or a prefix in a command. Use a
`GmcSmetaPilot_*` database and synthetic disposable contract with a tested
restore point. For example (replace every placeholder after inspecting it):

```powershell
$env:GMC_KS_PILOT_CONNECTION = 'Server=localhost;Database=GmcSmetaPilot_TEST;Integrated Security=true;TrustServerCertificate=true;'
$env:GMC_KS_PILOT_CONTRACT_ID = '<DISPOSABLE_SYNTHETIC_CONTRACT_ID>'
# First run read-only; verify printed server/database before any writes.
dotnet run --project csharp/tools/KsPilotChecks/KsPilotChecks.csproj -c Release
# Only on a proven dedicated disposable pilot contract:
$env:GMC_KS_PILOT_MUTATION_SUITE = 'YES'
dotnet run --project csharp/tools/KsPilotChecks/KsPilotChecks.csproj -c Release
```

Do not run the mutation suite against the shared original acts. Two SQL
clients must attempt competing edits at the same starting `meta.rev`:
exactly one succeeds and the loser gets a revision conflict; verify no lost
update, signed/locked barriers, FK and price/history retention after
copy-on-write, deletion and rollback. Preserve fingerprints of linked acts,
issued quantities and manually entered prices before and after.

For **unknown COMMIT** (connection drop during/after COMMIT), do **not**
automatically retry INSERT/UPDATE. Reconnect on the confirmed pilot and
reconcile using business operation ID, act/position IDs, `meta.rev`, the
audit trail and resulting quantities/prices, then decide whether it committed.
Mark PASS only with evidence of one and only one application plus intact
references. The currently available runner does **not** prove this chaos
scenario by its existence.

## 5. Ownership and next actions

This task owns only `csharp/tests/GmcSmeta.KsGoldenChecks/**` and this
ledger. No `ActsEditorView.*`, `SqlServerActs*`, financial core,
other teams' forms, templates or existing export module was modified.
Known Dneprovsky VOR standard-form dispatch is shared with Python and needs
a **customer policy decision**, not an uncoordinated code change.

Next: run the Windows commands on the current PR HEAD; fix any actual test
failures with minimal scoped changes. Run paired source-oracle and XLSX
second-engine checks, compare a frozen Python/C# source contract, then
SQL Pilot and manually rendered forms. Escalate any necessary API hook or
cross-owner change to coordinator before editing it. Coordinator alone
merges the PR to `main`.

## 27.11.2026 — BUG-F55: VOR quantity format regression

27.11.2026 ChatGPT / main (база c1bb96f) — BUG-F55 CODE READY: пользователь сообщил о завершающей точке в Excel ВОР (648., 680., 562., 344., 1.). KsVorForms.SetQuantity уже записывал число, но общий формат #,##0.#### оставляет десятичный разделитель у целых. Для всех стандартного/DNS/R4 шаблонов выбирается #,##0 для целого результата после существующего округления до 4 знаков AwayFromZero и #,##0.#### для дробного. Тип ячейки числовой, группировка разрядов, шаблоны, исходные данные и расчёты сохранены. Python export_vor.py aa6b34d: standard/DNS round(qty,4), R4 numeric write; правила C# округления не изменены. Добавлены три offline golden сценария (10 значений на форму), XLSX save/reopen, числовой тип, значение, формат и GetFormattedString, включая 993.6, 1142.64, округление до целого/нуля. Статическая сверка исходников; .NET/ClosedXML golden, Excel desktop и печать не запускались. Проверить новую выгрузку всех форм в Excel; уже созданные файлы автоматически не исправляются. SQL/БД и релиз не менялись.
