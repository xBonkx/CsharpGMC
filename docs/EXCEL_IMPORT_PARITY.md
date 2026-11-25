# Excel Import — Python → C# parity (PR feature/excel-import-parity)

Baseline: `python/sql-server-current/smeta/importer.py`, `ui/import_dialog.py`,
`services.py`. SQL Server pilot only (`GmcSmetaPilot_*`). Existing editor's
`EstimateView.Import.cs` TSV paste remains intentionally untouched.

| Python scenario | C# implementation | Status / limitation |
|---|---|---|
| .xlsx/.xlsm sheets, formula cached values | `ExcelWorkbookReader.ReadOpenXml` | Implemented; absent cached values produce a warning; Excel calculations are not executed |
| Binary .xls | `ReadLegacy` + ExcelDataReader | Implemented; requires Windows CI build validation |
| Header autodetection / extra columns | `ExcelImportParser` | Implemented for standard price / total column patterns; extra cells stored as Excel column keys |
| Two-level materials + work headings | `RefineHeader` | Implemented; work price preserved and included in preview calculations |
| Retail + purchase prices | `Header.Prices` | Implemented |
| Sections, multiple sections and components | `ParseSheetCore` | Implemented; children retain ordering and reference to their imported parent |
| Multiple sheets and repeated tables on one sheet | `ParseWorkbook` | Implemented; complex system subtotal heuristic still needs comparison against sample files |
| Totals, VAT flags, warnings | `ValidateTotals`, `ApplyVatOnTop` | Implemented for standard and VAT-on-top tables; needs golden-file reconciliation for customer rounding |
| Revit volume-only schedules | `TryParseRevit` | Implemented (quantities, source names and systems); catalog price backfill not implemented |
| DNS customer form / basis | `TryParseDns` | Implemented (basis, kind, VAT on top); real-file comparisons outstanding |
| Python UI preview and choice of documents | `ExcelImportPreviewWindow` | Implemented (WPF); manual Windows visual comparison outstanding |
| Choose existing or new object | `ExcelImportPreviewWindow` | Implemented |
| Choose KP / main / dop, metadata | `ExcelImportChoice` and preview grid | Implemented; dop requires existing base contract in selected object |
| Create multiple documents in one import | `SqlServerExcelImport.Import` | Implemented in one Serializable transaction; no modification of existing positions |
| Preserve hierarchy, purchase, work, notes, original row | SQL `#excel_rows` + `MERGE OUTPUT` | Implemented with 1 bulk copy and 2 SQL position statements |
| Revision guard, duplicate check and rollback | `SqlServerExcelImport.Import` | Implemented; rejects any existing same file name + sheet in object |
| Cancel / keep UI responsive | MainWindow background reader / SQL writer + cancellation window | Implemented between phases; an in-progress synchronous SQL batch cannot be interrupted until it finishes |
| Import into already-open KP (append-only) | `EstimateView.Import.Structured.cs`, `SqlServerExcelAppend` | Implemented for adding whole new sections, preserving hierarchy, without modifying existing positions |
| Safe update existing rows by matching | — | NOT IMPLEMENTED; requires explicit conflict resolution and safe matching |
| Preserve supplier/customer price history | Catalog keys inserted; price records not created | PARTIAL |
| Round-tripping custom client forms | — | NOT STARTED |
| SQL pilot + Python comparative ledger | — | BLOCKED until a real `GmcSmetaPilot_*` SQL Server is available for integration and sample Excel files |

## Safety and performance

- Never overwrites existing positions. New documents, sections, catalog rows and
  positions commit as one unit; alternatively append **new sections only** to an existing unlocked KP.
  Both use revision and duplicate checks before inserting; failure/cancellation before commit rolls back.
  Existing-KP append does not change contract-wide VAT, SMR or transport metadata.
- Files are parsed on a worker thread; row positions are sent to SQL Server
  using `SqlBulkCopy` (500 rows per batch) and a single catalog merge-like
  insert. There is no per-position network request.
- `ExcelImportParserTests` and `ExcelImportSqlSafetyTests` cover pilot DB safety, 1 and 2 row headers, multiple docs/sheets,
  extra columns, children, purchase and work prices, invalid rows, and
  generated 2,500 / 10,000 position sheets.
- SQL integration on `GmcSmetaPilot_*`, crash recovery, real sample-file
  parity to the kopeck, and WPF manual inspection **are not claimed** until run.

## Open items before full parity

1. Validate the newly ported `parse_dns`, `parse_revit_spec`, VAT-on-top and merge-system subtotals
   against genuine user workbooks, checking `basis`, `form`, `heading` and kopeck rounding.
2. Add non-destructive update-in-existing-KP workflow with row matching,
   explicit conflict resolution, backup/undo and rollback proof. Append-only import exists.
3. Preserve historical catalog price records and Python's customer-company
   selection rather than using the first SQL company as fallback.
4. Add real-file golden fixtures and end-to-end read → import → SQL roundtrip
   under `GmcSmetaPilot_*`, including 2,500 and 10,000 rows.
5. Audit formula cache and rounding differences against Python, check Windows
   1366×768 and 1920×1080 layouts, cancelled SQL batches and multi-client races.

This document is a **parity matrix**, not a declaration that Excel import is fully finished.
