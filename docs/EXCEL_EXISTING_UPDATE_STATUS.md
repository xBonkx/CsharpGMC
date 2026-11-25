# Excel existing-KP safe update — PR #49/#53 baseline

> Historical baseline for the original safe-update PRs. For subsequent Issue #71 mapping, undo snapshot, remaining fail-closed restrictions and **actual PASS/FAIL/SKIP**, see [EXCEL_EXISTING_UPDATE_ISSUE71_ACCEPTANCE.md](EXCEL_EXISTING_UPDATE_ISSUE71_ACCEPTANCE.md). This baseline is not evidence of Windows/SQL acceptance for #71.

Issue: #42. This implementation is deliberately **fail-closed** and separate from the existing new-document and append-only Excel import paths.

## Pipeline

1. Existing `ExcelWorkbookReader` parses .xlsx/.xlsm/.xls and reports missing formula caches.
2. The user selects a specific existing contract and one block from the workbook.
3. `SqlServerExcelExistingUpdate.Read` builds a consistent, read-only serializable SQL snapshot and `ExcelExistingUpdatePlan.Build` compares matching rows by an explicit, contract-scoped stable ID (if provided) or by normalized system/parent/name/unit.
4. The WPF preview lists **Added, Changed, Unchanged, DeletedInSource, Ambiguous, Conflict**. User selection defaults to **nothing**. A conflict or missing source row is *not* a deletion instruction.
5. `Apply` verifies SHA-256 of the actual workbook, `meta.rev`, contract ownership, lock and archive status, live position values, formula state and related rows again **inside** a single Serializable transaction. It records a plan fingerprint in `change_log`, updates only explicitly selected quantities and inserts unambiguous top-level positions into existing uniquely named sections.
6. Catalog keys for **new** rows are created only if absent. Existing positions' customer, purchase and work prices, notes, formulas, provenance and catalog links are not touched. Signed acts, supplementary agreements, procurement, stage allocations, movements and shipments are not updated.

## Import behavior and explicit limitations

| Case | Behavior |
|---|---|
| One-to-one imported root row, changed quantity, no locks/formula/links | Optional per-row quantity update; **bulk selection requires recorded baseline** equal to current quantity |
| Unchanged quantity, differing purchase/work/customer prices | Preserved existing prices; conflict in preview |
| Changed note | Preserved existing note, never auto-replaced |
| Linked act (signed or unsigned), stage, procurement, movement, shipment, component, out-of-KP or manual area | Quantity change rejected |
| Contract with linked supplementary agreement, own base agreement or stages | Entire update rejected conservatively |
| Duplicate key in workbook or current estimate | Mark ambiguous; no automatic merge |
| Stable ID names another row or contract | Mark ambiguous; no remapping |
| Existing position absent in workbook | Informational `DeletedInSource`; never deletes or zeros |
| New top-level position in one unambiguous existing section | Optional add; source prices, basis and custom columns only for newly created position. Source name is retained in provenance |
| New child/component or new/ambiguous section | Mark conflict; no write |
| Workbook has formulas without saved cached results | Preview can be viewed; apply disabled |
| Same file hash and operation retried after uncertain COMMIT | Existing journal entry returns success; other actions for same hash rejected |
| Another client changed `meta.rev` | Entire transaction fails; new preview required |
| Cancel before SQL commit | Transaction rolls back; no changes to prior values |

### Guard against unnoticed manual quantity edits

Legacy imported rows usually contain `source_row` but **no trusted original-quantity baseline**. This proves their import origin, **not** whether the user later edited `qty`. To prevent a one-click bulk overwrite, `SafeForBulk=false` for those rows even when an individual change is technically allowed. The user must inspect and explicitly tick each such row; default selection is `Skip`.

New rows created by this module save `excel_imported_qty` in their existing `extra_json`; a subsequent bulk update is available only while `qty` still matches that baseline. Once the user changes `qty`, bulk confirmation is disabled; the original baseline is not silently rewritten. This guard does **not** infer the user's intention—per-row review is mandatory for uncertain old data.

### Not yet covered

- Automatic creation of new sections, child components, renaming or movement between systems.
- Editing manually entered customer/supplier/work prices, note changes, and supplier history integration (coordinate with other owners).
- User-guided mapping of ambiguous rows; these can only be skipped and resolved outside this import.
- Golden Excel fixtures from real customers and full Python comparison, visual Windows acceptance, actual on-server `GmcSmetaPilot_*` rollback/concurrent-client tests.
- A safe restore/undo command for this batch; the journal provides auditability but **is not** a full undo snapshot.

**Validation labels:** Code authored — yes. Offline tests added — yes, **not executed**. Windows WPF build — **blocked**: GitHub Actions runs failed before runner allocation (runner_id=0, zero steps; example [build run 44901480626](https://github.com/xBonkx/CsharpGMC/actions/runs/44901480626)). This is not evidence of a code compile failure or a successful build. Live SQL pilot — **not run** from this environment. Production — forbidden.

## Safe pilot acceptance checklist

Run only against an expressly selected `GmcSmetaPilot_*` database, never the working SQL Server database:

1. Create a dedicated test object and estimate; save prices, note, manual edits, signed KS, DS, stage links and catalog history before import.
2. Compare fixture sheets with duplicate titles, nested components, zero and fractional quantities, and .xlsx/.xlsm cached formulas.
3. Execute on 2,500 and 10,000 rows. Verify journal contains one `excel_safe_update` record and `meta.rev` is bumped once.
4. From two clients, preview the same revision, commit A, then attempt B: B must fail with a revision conflict and no partial change.
5. Deliberately fail one late statement in a chosen batch: verify the entire transaction rolled back. Repeat a saved operation after simulated lost COMMIT acknowledgment; inspect `WasCommitted` before retry.
6. Verify original prices/notes, signed `acts`, `act_lines`, `contracts` (including DS), `positions` linked through stages, procurement, shipments, `catalog_items` and `price_history` remain consistent.

Do not mark the issue Done until these checks are evidenced. The original `ExcelImportSqlSafetyTests` are not substitutes for SQL pilot integration.
