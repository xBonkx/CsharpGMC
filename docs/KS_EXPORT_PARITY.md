# KS-2 / KS-3 / VOR export parity (task 7)

Scope: isolated `feature/ks-export-parity` PR. Read-only SQL Server print snapshots only.
No production database writes, no SQLite target implementation.

## Form matrix

| Form | XLSX source | KS-2 | KS-3 | VOR | PDF |
|---|---|---|---|---|---|
| Standard | Rebuilt from `export_ks.py` rows and formulas | Yes | Yes (grouped) | Python `vor_template.xlsx` | Multi-page standard print layout |
| DNS | Embedded `ks_dns_template.xlsx` | Yes | Yes (grouped) | Embedded `vor_dns_template.xlsx` | Generic standard PDF, not native DNS drawing |
| R4 (object «Р4»/«R4») | Embedded `ks_r4_template.xlsx` | Yes | Yes (grouped) | Embedded `vor_r4_template.xlsx` | Generic standard PDF, not native R4 drawing |
| Dneprovsky | External network workbook (not in repository) | **Blocked** to avoid wrong signed form | **Blocked** | Standard template fallback | **Blocked** |

The original .xlsx resource files are embedded from
`python/sql-server-current/smeta/assets/` into the WPF assembly by
`GmcSmeta.App.csproj`. There is no runtime dependency on the Python executable or
a template network share for the standard/DNS/R4 forms.

## Accounting / presentation rules

- Source-of-truth for net, VAT, gross, materials, SMR, transport and PNR:
  `SqlServerActs.ActInfo` as returned by existing SQL Server financial policy.
  `RowsExact` and `RoundRows` are preserved for appropriate Excel expressions.
- Line totals are actual Excel formulas (`F*G` or `ROUND(F*G,2)`);
  rounding reconciliation is written into subtotal expressions.
- Standard group KS-3 and customer group KS-3 link individual KS-2 result cells.
  Cumulative values are reported from `KsGroupReport` and previous groups.
- Header details from `SqlServerKsRequisites` and contractor snapshots; signature
  slots remain as in each customer template. The native standard form uses live
  signatory position/name details and marks.
- R4 mode uses `smr_mode` to distinguish per-position SMR from lump-sum work.
  Work code is read from `positions.basis` or `extra_json.B`.
- Print areas, landscape fit, cell styles and column widths are maintained
  for each corresponding template (the XLSX itself is the source of styling).

## Tests, samples and safety

Windows workflow: `.github/workflows/ks-export-parity.yml`.
`csharp/tests/GmcSmeta.Export.Checks` uses **synthetic** SQL Server print
snapshots, never opens a database. It checks standard, DNS and R4 with 2
and 80 positions each, multi-act group KS-3 cross-sheet references, financial
totals, XLSX in-memory save/reopen, and PDF output re-open with page count.

The CI run uploads synthetic `ks-export-samples` containing three groups of
KS-2+KS-3 XLSX, VOR XLSX and printable PDF examples. They contain fictitious
test parties, not real contractual data.

Existing WPF CI has its own **passing** SQL Server offline test subset. The
export-specific CI runs all other core tests but excludes one pre-existing
outdated assertion
`GenericReaderAndSqlPilotTests.SqlServerPilot_RejectsUnsupportedFieldsBeforeNetwork`:
it still expects the `basis` field to be unsupported. This assertion is
outside the export task and has not been modified.

## Remaining limitations before calling it visually 100% compatible

1. The Dneprovsky customer workbook exists only at a private external path.
   A verified copy and a real sample act are needed before implementing it.
2. Customer-specific visual parity is not yet confirmed by side-by-side
   print previews. Rich text, signatures, page breaks and special annex
   agreement fields require manual review with authentic customer examples.
3. The generated PDF has a native print layout but is **not** pixel-identical
   to the Python/Qt PDF or the customer's proprietary XLSX form.
4. Mixed different customer forms in one group are intentionally rejected
   pending an explicit business policy about which KS-3 to issue.
5. Exact source system hierarchy (parent/child sections), quantity-only
   changes, extra position numbering, signed adjustments and unusual
   contract rule combinations need comparison against real SQL Pilot acts.
6. Export from actual SQL Server is read-only, but real-world parity on
   `GmcSmetaPilot_*` and Windows print-preview still need human confirmation.

## Manual acceptance on a test SQL Server

1. Point the C# app at a **test database matching `GmcSmetaPilot_*`**.
2. Compare exported standard, DNS and R4 KS-2/KS-3 + VOR with the Python
   app for the same agreement, act number, period, contractor and object.
3. Recheck totals, included/on-top VAT, `rows_exact`, `round_rows`,
   `smr_mode`, materials, PNR, transport, signatures and special annexes.
4. Inspect print previews, page breaks, detailed line positioning and
   typography. Do not sign an unreviewed customer-specific export.
5. Confirm a saved XLSX opens and recalculates correctly in Excel; check
   PDF with a PDF viewer. The export code does not make database writes.

## All acts in one workbook (25.11.2026)

25.11.2026 — CTX-032 / экспорт всех актов, ChatGPT / fix/ks-export-all — PARTIAL. Python aa6b34d ks_view.export_all/export_ks_excel/export_ks_dns/export_r4_excel: обычная форма и ДНС — пары КС-2/КС-3 каждого акта; Р4 — группы номера в пределах выбранного документа, предыдущие группы по юридическому договору. ExportAll_Click отделён от ExportGroup_Click; диалог отменяется до чтения, ревизия проверяется до/после единого чтения ReadAll, строки только актов выбранного документа. Переиспользованы существующие расчёты и шаблоны: CreateAll заполняет один workbook без копирования между книгами, уникальные имена и ссылки для каждого отчёта; Calculate(selectedDocumentOnly) сохраняет предыдущие группы юридического договора, не включает соседние ДС в текущую сумму. Днепровский остаётся заблокирован без шаблона. Добавлены синтетические тесты 2 пар, одинаковых номеров, ссылок, сумм и roundtrip для 3 форм; статическая сверка, build/xUnit/Excel/SQL Pilot не запускались. PR #6 отдельно меняет меню и требует согласования общего TODO/редактора при слиянии; не дублировать счётчик. Рабочая БД не менялась. Приёмка: несколько актов и ДС, Р4 1.1/1.2, особые реквизиты/округления, визуальная проверка, отмена, 500 актов максимум.

Дополнено 25.11.2026: KsGroupReportTests.DocumentExportKeepsOnlyLocalCurrentActsButLegalAgreementHistory — синтетический сценарий текущей группы с соседним ДС, накопительным итогом по предыдущим группам, неизменным общим групповым отчётом и отказом при отсутствии локальных актов. Не запускался.

## Сверка скидок и разбивки — 26.11.2026

База C# 6db4c76. Эталон — локальные исходники Python 0.67.8,
bc2a8358808db90a5893a7cd64038bd68faabd4f. Запущены 12 Python-тестов
из test_contract_discount.py и test_ks_sections.py на временной SQLite.

Восемь сценариев из tools/sql-migrations/verify-python-finance.py
выполнены на этом локальном Python. Сравнение JSON с C# DiscountPolicy и
KsFinancialPolicy дало 48 совпадений до копейки. Сценарии включают подписанную
историю, наследуемую и индивидуальную скидку, нулевую ставку, фиксированный
итог, доплату, ручную разбивку и НДС сверху. Сумма КС-3 в этом сравнении
сопоставлялась с итогом одного акта, а не с накопительным отчётом.

В C# прошли 25 профильных xUnit-тестов и 21 сценарий GmcSmeta.KsGoldenChecks.
Проверены формулы корректировки КС-2, устойчивость фиксированной суммы при
пересчёте и ссылки на итог в КС-3. У разбивки проверены одноимённые разделы,
секция без материалов, исключение чужого договора, остаток и отсутствие
двойного учёта. Отдельный архивный JSON oracle в KsGoldenChecks пропущен,
сравнение с локальным Python выполнено отдельным прогоном. Для BUG-F13 тесты
CalculateSingle подтвердили изоляцию акта и начальный остаток. Приёмка окна
и парных одиночной/групповой выгрузок на SQL Pilot остаётся открытой.

GmcSmeta.ContractExport.Checks подтвердил синтетические КП XLSX/PDF и наборы
на 2500/10000 строк. В обычном и внутреннем XLSX проверены скидка, неизменная
цена и ссылка НДС на оплачиваемый итог. В позиционном XLSX проверены сумма
скидки и итог. В PDF КП независимым чтением текста подтверждены 51,57 до
скидки, строка «Скидка 5%» и 48,99 к оплате. PDF с разбивкой КС открывается.
Внешний вид в Excel/PDF, типографика, приёмка SQL Pilot и два клиента в
этом блоке не проверялись.
