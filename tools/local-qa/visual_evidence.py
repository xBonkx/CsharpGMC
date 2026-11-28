#!/usr/bin/env python3
"""Read-only static icon candidates and paired Python/C# evidence checklist.

No SQL, GUI automation, external dependencies or user settings. All generated
entries start NOT_RUN; an inventory is not proof of visual parity.
"""
import argparse
import csv
import html
import json
import re
from pathlib import Path

# Narrow candidate set: exclude ordinary punctuation, money/degree signs,
# and status text unless it occurs in a UI-label assignment.
GLYPH = re.compile(r"[\u2190-\u21ff\u2300-\u23ff\u25a0-\u25ff\u2600-\u27bf\U0001f300-\U0001faff]")
XAML = re.compile(r'\b(?:Content|Header|Text|ToolTip)\s*=\s*"([^"]*)"')
CS = re.compile(r'\b(?:Content|Header|Text|ToolTip)\s*=\s*"(?:\\.|[^"\\])*"')
CS_VALUE = re.compile(r'"((?:\\.|[^"\\])*)"')
ESCAPE = re.compile(r"\\u([0-9a-fA-F]{4})|\\U([0-9a-fA-F]{8})")

def decode_cs(value):
    return ESCAPE.sub(lambda m: chr(int(m.group(1) or m.group(2), 16)), value)

def candidates(source, suffix):
    if suffix == ".xaml":
        matches = ((m.start(), html.unescape(m.group(1))) for m in XAML.finditer(source))
    elif suffix == ".cs":
        matches = ((m.start(), decode_cs(CS_VALUE.search(m.group()).group(1)))
                   for m in CS.finditer(source))
    else:
        return
    for offset, label in matches:
        # Bindings and static resource names are not literal labels.
        if label.startswith("{"):
            continue
        symbols = "".join(dict.fromkeys(GLYPH.findall(label)))
        if symbols:
            yield {"line": source.count("\n", 0, offset) + 1,
                   "symbols": symbols, "label": label, "status": "REVIEW"}

def write_csv(path, fields, rows):
    with path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)

def scan(root, output):
    app = root / "src" / "GmcSmeta.App"
    if not app.is_dir():
        raise SystemExit("Run against the C# checkout, not a release folder.")
    findings = []
    for path in sorted(app.rglob("*")):
        if path.suffix not in (".cs", ".xaml") or any(p in ("bin", "obj") for p in path.parts):
            continue
        for item in candidates(path.read_text(encoding="utf-8-sig"), path.suffix):
            findings.append({"file": path.relative_to(root).as_posix(), **item})
    write_csv(output / "icon-candidates.csv", ["file", "line", "symbols", "label", "status"], findings)
    return len(findings)

PAGES = [
    ("navigation", "logo, parent, stages 1/2, remainder, nested sections; expanded and selected"),
    ("object-cards", "empty and populated, long titles, signed and unsigned"),
    ("estimate", "editing and selected cells, wrap on/off, stage mode"),
    ("materials", "missing cost, edited cells, grouped rows"),
    ("cost-summary", "groups collapsed/expanded, long headers"),
    ("totals", "VAT-on-top and inclusive, transport, SMR"),
    ("procurement", "supplier/status filter, selected and locked rows"),
    ("shipments", "draft and fixed list"),
    ("ks", "draft/signed KS-2, KS-3, formula editing"),
    ("catalog", "search and empty result"),
    ("metal", "stock, lots, journal"),
    ("pto-letters", "registry and empty result"),
    ("settings", "font scale, columns and theme"),
    ("director", "finance summary and filters"),
]
KEYS = ["Shift+F10", "Menu", "Up/Down", "Enter", "Esc", "F2", "F4", "Delete",
        "Ctrl+C", "Ctrl+V", "Ctrl+D", "Ctrl+M", "Ctrl+K", "Ctrl+Shift+V",
        "Insert", "Shift+Insert", "Ctrl+Alt+Insert", "Ctrl+[", "Ctrl+]"]
DIALOGS = ["object/document properties", "copy/contract/annex", "prices/discount",
           "section/position move", "specification update", "catalog choice",
           "SMR/transport/stage", "KS extras/formula", "supplier/return/workshop",
           "access/orders", "files/link/history/trash", "export"]

def matrices(output):
    captures = []
    for page, state in PAGES:
        for resolution in ("1366x768", "1920x1080", "2560x1440"):
            for dpi in (100, 125, 150):
                # Full scale sweep only on baseline viewport; separate menu matrix.
                for scale in ((100, 110, 120) if resolution == "1920x1080" else (100,)):
                    case = f"{page}-{resolution}-dpi{dpi}-scale{scale}"
                    captures.append(dict(case=case, page=page, state=state, resolution=resolution,
                        dpi=dpi, scale=scale, fixture_id="", role="", smr_mode="", columns_profile="",
                        python_sha="", csharp_sha="", python_png="", csharp_png="",
                        actual_dpi="", diff_json="", status="NOT_RUN", notes=""))
    write_csv(output / "paired-captures.csv", list(captures[0]), captures)
    menus = [dict(case=f"key-{i:02}", key=key, area="estimate/active editor or menu",
                  precondition="", expected="", actual="", focus_after="", sql_readback="",
                  screenshot="", status="NOT_RUN") for i, key in enumerate(KEYS, 1)]
    write_csv(output / "keyboard-cases.csv", list(menus[0]), menus)
    sizes = [dict(resolution=res, dpi=dpi, scale=scale, system_text_scale="", menu="",
                  python_png="", csharp_png="", clipped="", status="NOT_RUN")
             for res in ("1366x768", "1920x1080")
             for dpi in (100, 125, 150, 200) for scale in (100, 110, 120)]
    write_csv(output / "menu-scale-cases.csv", list(sizes[0]), sizes)
    dialogs = [dict(area=area, command="", role="", python_title="", csharp_title="",
                    python_size="", csharp_size="", fields_defaults="", tab_order="",
                    validation="", permissions="", cancel_result="", sql_before="", sql_after="",
                    python_png="", csharp_png="", status="NOT_RUN") for area in DIALOGS]
    write_csv(output / "dialog-cases.csv", list(dialogs[0]), dialogs)
    return dict(captures=len(captures), keyboard=len(menus), menu_scale=len(sizes), dialogs=len(dialogs))

def self_test():
    assert list(candidates('<Button Content="&#x2713; Готово"/>', ".xaml"))[0]["symbols"] == "✓"
    assert list(candidates('<TextBlock Text="{Binding Value}"/>', ".xaml")) == []
    assert list(candidates(r'item.Header = "\u2192 Перейти";', ".cs"))[0]["symbols"] == "→"
    assert list(candidates('item.Header = "Цена, ₽";', ".cs")) == []
    assert list(candidates('item.Header = "Удалить";', ".cs")) == []

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--out", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    self_test()
    if args.self_test and args.out is None:
        print("Static scanner self-test passed.")
        return
    if args.out is None:
        parser.error("--out is required")
    # New directory only: do not overwrite witnessed results.
    args.out.mkdir(parents=True, exist_ok=False)
    count = scan(args.root, args.out)
    result = matrices(args.out)
    (args.out / "inventory.json").write_text(json.dumps(
        {"icon_candidates": count, **result, "status": "NOT_RUN",
         "scope": "heuristic literal labels; comments/false positives possible; dynamic strings not exhaustive"},
        ensure_ascii=False, indent=2), encoding="utf-8")
    print("Inventory generated, no visual acceptance:", result)

if __name__ == "__main__":
    main()
