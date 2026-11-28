# ARCH-SIZE-01 — pinned metadata and large-file sample, 29.11.2026

Snapshot: [77e83005f5a907e1ef5605232f380f5053bd23ce](https://github.com/xBonkx/CsharpGMC/tree/77e83005f5a907e1ef5605232f380f5053bd23ce). The recursive Git tree contains 1319 entries, not truncated. The existing `tools/source-size/source_size.py` was inspected; an actual local clone failed with DNS resolution for github.com. No full line inventory is claimed. Git blob sizes and paths are exact for the pinned snapshot. All 30 largest by bytes were fetched separately at that SHA; physical lines count includes comments/empty lines, equivalent to Python splitlines for these sources. No auto-generated header was found among the 30. Remaining headers were not examined, so counts below are candidates, not a fully classified handwritten inventory.

| Scope | C# | XAML | Candidate files | Bytes |
|---|---:|---:|---:|---:|
| src | 534 | 46 | 580 | 6189442 |
| tests | 429 | 0 | 429 | 2228609 |
| other | 32 | 1 | 33 | 267342 |
| Total | 995 | 47 | 1042 | 8685393 |

Path/mode/filename exclusions: 0. Content-header exclusions remain pending outside the 30. Non-C#/XAML entries are out of scope.

## Largest 30 by bytes (not top-30 by lines)

| Path | Git bytes | Physical lines |
|---|---:|---:|
| `src/GmcSmeta.App/App.xaml` | 126529 | 1773 |
| `src/GmcSmeta.App/ActsEditorView.xaml.cs` | 84590 | 1773 |
| `src/GmcSmeta.App/EstimateView.xaml` | 81231 | 998 |
| `src/GmcSmeta.App/MainWindow.xaml.cs` | 79260 | 1584 |
| `src/GmcSmeta.App/EstimateView.Materials.cs` | 74903 | 1514 |
| `src/GmcSmeta.App/EstimateView.xaml.cs` | 74461 | 1422 |
| `src/GmcSmeta.Core/Db/SqlServerStages.cs` | 68953 | 1247 |
| `src/GmcSmeta.Core/Db/SqlServerActs.cs` | 67163 | 1151 |
| `src/GmcSmeta.Core/Db/SqlServerWorkspace.cs` | 66820 | 1098 |
| `src/GmcSmeta.App/MainWindow.xaml` | 65850 | 743 |
| `src/GmcSmeta.Core/Db/SqlServerShipments.cs` | 63650 | 1050 |
| `src/GmcSmeta.Core/Domain/Documents.cs` | 62138 | 1288 |
| `src/GmcSmeta.App/EstimateSummaryPanel.xaml` | 62091 | 855 |
| `src/GmcSmeta.App/EstimateView.CostSummary.cs` | 61877 | 1277 |
| `src/GmcSmeta.App/EstimateView.Sections.cs` | 54667 | 1079 |
| `src/GmcSmeta.App/ProcurementPane.xaml` | 53790 | 628 |
| `tests/GmcSmeta.App.Checks/VisualAuditChecks.cs` | 51459 | 880 |
| `src/GmcSmeta.App/ProcurementPane.xaml.cs` | 51139 | 1052 |
| `tests/GmcSmeta.App.Checks/Program.cs` | 50368 | 893 |
| `src/GmcSmeta.App/ShipmentsPane.xaml.cs` | 48431 | 917 |
| `src/GmcSmeta.Core/Db/SqlServerProcurement.cs` | 47448 | 804 |
| `src/GmcSmeta.App/ShipmentsPane.xaml` | 46929 | 593 |
| `src/GmcSmeta.Core/Db/SqlServerDatabase.cs` | 45756 | 801 |
| `src/GmcSmeta.App/StagesDialog.xaml.cs` | 44224 | 945 |
| `src/GmcSmeta.Core/Db/SqlServerPaste.cs` | 41376 | 657 |
| `src/GmcSmeta.App/PeopleWindow.xaml.cs` | 40439 | 773 |
| `tests/GmcSmeta.Pilot.E2E/PilotCases.cs` | 38256 | 651 |
| `tests/GmcSmeta.KsGoldenChecks/Program.cs` | 37196 | 729 |
| `src/GmcSmeta.Core/Domain/ExcelImportParser.cs` | 37110 | 712 |
| `src/GmcSmeta.App/HelpTopics.cs` | 36070 | 262 |

Sample: 13 files >1000 lines; 4 >1500; 0 >2000. These are sample counts only, not repository-wide conclusions.

## Filename families (not exact logical types)

| Family | Files including XAML | C# files | Bytes |
|---|---:|---:|---:|
| EstimateView | 42 | 41 | 707813 |
| ActsEditorView | 10 | 9 | 162761 |
| MainWindow | 24 | 23 | 290961 |
| SqlServerStages | 8 | 8 | 120902 |
| SqlServerActs | 9 | 9 | 158188 |
| SqlServerWorkspace | 4 | 4 | 98549 |

Filename matching is insufficient: SqlServerPaste.cs declares partial SqlServerDatabase, PeopleWindow.xaml.cs declares partial PeopleView. Thus the actual namespace/type aggregation must use source content, not these names.

## Remaining decomposition candidates and ownership

- ARCH-SIZE-03.01–03: EstimateView.xaml.cs, Sections and CostSummary are independently large; separate edit/clipboard/formula policies from readback/queue ownership and tab lifecycle. Materials is already tracked in ARCH-SIZE-05; do not duplicate or change amounts.
- ARCH-SIZE-04.01–02: MainWindow.xaml.cs remains large; route/target identity and startup/cache lifetime are distinct responsibilities. Existing navigation, document-link and delete helpers should be reused; don't alter SQL or startup sequencing.
- ARCH-SIZE-06: App.xaml 1773 lines: resource dependency ordering must be mapped before splitting, preserve keys and layout. Not a functional rewrite.
- ARCH-SIZE-07.01–03: SqlServerStages/Acts/Workspace exceed 1000 lines each in their primary files; separate deterministic projections/validation from write transaction ownership, retain financial formulas and legal scope. Do not equate adding a partial with extracting independent responsibility.
- Other sampled files: Documents.cs and SqlServerShipments.cs exceed 1000 lines; defer any new scope until active financial tasks are accepted. ProcurementPane.xaml.cs similarly overlaps active procurement work.

## Reproducible completion still required

Run in a local full clone: `python tools/source-size/source_size.py --ref 77e83005f5a907e1ef5605232f380f5053bd23ce --json source-size-report.json`. Check complete header exclusions, exact line bands, top-30 by lines, project/namespace/type aggregates and per-file method/dependency review. Existing script is heuristic for partials; use syntax-aware or manual verification for nested/multi-type sources. No code change, no application tests, no SQL/GUI or release. Both ARCH-SIZE-01 child packages remain PARTIAL; this is audit evidence, not new implementation.
