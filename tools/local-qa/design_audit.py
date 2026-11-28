#!/usr/bin/env python3
"""Read-only design inventory; findings are review candidates, never defects by default."""
import argparse
import csv
import json
import re
from pathlib import Path
from xml.parsers import expat

METRICS = {"Margin", "Padding", "FontSize", "FontWeight", "FontFamily", "Width", "Height",
           "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "StrokeThickness", "Stretch",
           "ContentTemplate", "HorizontalContentAlignment", "VerticalContentAlignment"}
FIELDS = ["file", "line", "kind", "element", "attribute", "value", "context", "status"]
GRID_FIELDS = ("Style", "CellStyle", "RowStyle", "ColumnHeaderStyle", "HeaderStyle",
               "ElementStyle", "EditingElementStyle", "HorizontalContentAlignment",
               "TextAlignment", "HeadersVisibility", "SelectionUnit")
UI_CREATION = re.compile(r"\bnew\s+(?:FrameworkElementFactory|Thickness|Grid|DockPanel|StackPanel|"
                         r"WrapPanel|Border|Viewbox|Path|TextBlock|Button|DataGrid(?:TextColumn|TemplateColumn)?|"
                         r"ScrollViewer|ContentPresenter|ControlTemplate|DataTemplate)\b")
ASSIGNMENT = re.compile(r"\b(?P<key>Margin|Padding|FontSize|FontFamily|FontWeight|Height|Width|"
                        r"MinWidth|MinHeight|MaxWidth|MaxHeight|Stretch|StrokeThickness|"
                        r"CellStyle|RowStyle|ColumnHeaderStyle|HeaderStyle|ElementStyle|"
                        r"EditingElementStyle|HorizontalContentAlignment|TextAlignment)\s*=\s*(?P<value>[^;\r\n]{1,200})")
PROPERTY_API = re.compile(r"\b(?:SetValue|SetBinding|ClearValue)\s*\([^;\r\n]{1,200}")
PAIR_FIELDS = ("database", "fixture_id", "object_id", "document_id", "data_fingerprint",
               "role", "permissions", "signed", "smr_mode", "columns_profile", "page", "state",
               "app_scale", "table_zoom", "dpi_x", "dpi_y", "window_width_px", "window_height_px")


def local(name):
    return name.rsplit("}", 1)[-1]


def finding(file, line, kind, element, attribute, value, context, status="REVIEW"):
    return dict(file=file, line=line, kind=kind, element=element, attribute=attribute,
                value=str(value), context=context, status=status)


def xaml_inventory(source, file):
    # Expat supplies the opening element's exact line, even for multiline attributes.
    parser = expat.ParserCreate(namespace_separator="}")
    stack, rows = [], []

    def start(name, raw):
        tag = local(name)
        attrs = {local(k): v for k, v in raw.items()}
        label = tag + (("[" + attrs.get("Name", attrs.get("Key", "")) + "]")
                       if "Name" in attrs or "Key" in attrs else "")
        context = "/".join(frame[0] for frame in stack + [(label, tag, attrs)])
        line = parser.CurrentLineNumber
        for key, value in attrs.items():
            if key in METRICS or key == "Data" and tag == "Path":
                role = "RESOURCE" if value.startswith(("{StaticResource ", "{DynamicResource ")) else "REVIEW"
                rows.append(finding(file, line, "xaml-metric", tag, key, value, context, role))
        if tag == "Setter" and attrs.get("Property", "").rsplit(".", 1)[-1] in METRICS:
            rows.append(finding(file, line, "xaml-setter", tag, attrs["Property"],
                                attrs.get("Value", "(element value)"), context))
        if tag == "Path":
            parents = [(t, a) for _, t, a in stack]
            view = next((a for t, a in reversed(parents) if t == "Viewbox"), None)
            canvas = next((a for t, a in reversed(parents) if t == "Canvas"), None)
            shared = attrs.get("Style") == "{StaticResource SvgGlyphPath}"
            if shared and canvas and canvas.get("Width") == canvas.get("Height") == "24":
                role = "SHARED_CANVAS"
                if view and view.get("Width") in {"12", "14", "17", "18", "20", "28"}:
                    role += "_ROLE"
            else:
                role = "REVIEW"
            if any(k in attrs for k in ("Width", "Height", "Stretch")):
                role = "REVIEW"
            value = " ".join(k + "=" + attrs[k] for k in ("Width", "Height", "Stretch", "Style") if k in attrs)
            if view:
                value += " view=" + view.get("Width", "?") + "x" + view.get("Height", "?")
            rows.append(finding(file, line, "path-role", tag, "geometry", value, context, role))
        if tag == "DataGrid" or tag.startswith("DataGrid") and tag.endswith("Column"):
            for key in GRID_FIELDS:
                if key in attrs:
                    status = "RESOURCE" if attrs[key].startswith(("{StaticResource ", "{DynamicResource ")) else "REVIEW"
                    rows.append(finding(file, line, "grid-style", tag, key, attrs[key], context, status))
            key = "Style" if tag == "DataGrid" else "ElementStyle"
            if key not in attrs:
                rows.append(finding(file, line, "grid-style", tag, key,
                                    "(implicit/default or property element; resolve in WPF)", context, "REVIEW"))
        stack.append((label, tag, attrs))

    def end(_):
        stack.pop()

    parser.StartElementHandler = start
    parser.EndElementHandler = end
    parser.Parse(source, True)
    return rows


def cs_inventory(source, file):
    # A deliberately conservative source inventory, not a C# compiler or dataflow analysis.
    rows = []
    for line, raw in enumerate(source.splitlines(), 1):
        content = raw.strip()
        if content.startswith(("//", "*", "/*")):
            continue
        for match in UI_CREATION.finditer(content):
            rows.append(finding(file, line, "cs-creation", "C#", "new", match.group(), content))
        for match in ASSIGNMENT.finditer(content):
            rows.append(finding(file, line, "cs-assignment", "C#", match["key"], match["value"], content))
        for match in PROPERTY_API.finditer(content):
            rows.append(finding(file, line, "cs-property-api", "C#", "dependency-property", match.group(), content))
    return rows


def inventory(root, output):
    app = root / "src" / "GmcSmeta.App"
    if not app.is_dir():
        raise ValueError("Expected C# repository root")
    rows = []
    for path in sorted(app.rglob("*")):
        if path.suffix not in (".xaml", ".cs") or any(p in {"obj", "bin"} for p in path.relative_to(app).parts):
            continue
        file = path.relative_to(root).as_posix()
        source = path.read_text(encoding="utf-8-sig")
        try:
            rows.extend(xaml_inventory(source, file) if path.suffix == ".xaml" else cs_inventory(source, file))
        except expat.ExpatError as exc:
            rows.append(finding(file, exc.lineno, "parse-error", "XAML", "xml", str(exc), "", "ERROR"))
    with (output / "design-inventory.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=FIELDS)
        writer.writeheader()
        writer.writerows(rows)
    return {"design_findings": len(rows), "design_parse_errors": sum(r["status"] == "ERROR" for r in rows)}


def validate_pair(before, after):
    """Reject mismatched/empty fixtures; successful metadata validation is not visual acceptance."""
    errors = []
    for key in PAIR_FIELDS:
        if key not in before or key not in after or before.get(key) in (None, "") or after.get(key) in (None, ""):
            errors.append(key + ": required in both records")
        elif type(before[key]) is not type(after[key]) or before[key] != after[key]:
            errors.append(key + ": differs")
    for record in (before, after):
        if not isinstance(record.get("database"), str) or not record["database"].startswith("GmcSmetaPilot_"):
            errors.append("database: use a GmcSmetaPilot_* fixture")
        for key in ("dpi_x", "dpi_y", "window_width_px", "window_height_px", "app_scale", "table_zoom"):
            value = record.get(key)
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not 0 < value < 100000:
                errors.append(key + ": expected positive finite number")
        if type(record.get("signed")) is not bool:
            errors.append("signed: expected boolean")
        if not re.fullmatch(r"[a-fA-F0-9]{64}", str(record.get("data_fingerprint", ""))):
            errors.append("data_fingerprint: expected SHA-256 of agreed fixture data")
        for key in ("sha", "png"):
            if not isinstance(record.get(key), str) or not record[key].strip():
                errors.append(key + ": required")
    return sorted(set(errors))


def ancestry(tsv, selector):
    with tsv.open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream, delimiter="\t"))
    matches = [r for r in rows if r.get("path") == selector or r.get("name") == selector]
    if len(matches) != 1:
        raise ValueError("Select exactly one element by path or unique name")
    path = matches[0]["path"]
    # Preserve the hierarchy; sibling spacings are not summed as nested padding.
    return [r for r in rows if path == r["path"] or path.startswith(r["path"] + "/")]


def self_test():
    xaml = '<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">\n' \
           '<Border Margin="8"><Viewbox Width="14"><Canvas Width="24" Height="24">' \
           '<Path Style="{StaticResource SvgGlyphPath}"/></Canvas></Viewbox></Border>' \
           '<Path Width="13" Stretch="Fill"/><DataGrid><DataGrid.Columns>' \
           '<DataGridTextColumn ElementStyle="{StaticResource CompactCostText}"/>' \
           '</DataGrid.Columns></DataGrid></Grid>'
    rows = xaml_inventory(xaml, "fixture.xaml")
    assert any(r["kind"] == "xaml-metric" and r["line"] == 2 and r["attribute"] == "Margin" for r in rows)
    assert any(r["kind"] == "path-role" and r["status"] == "SHARED_CANVAS_ROLE" for r in rows)
    assert any(r["kind"] == "path-role" and r["status"] == "REVIEW" for r in rows)
    assert any(r["kind"] == "grid-style" and r["attribute"] == "ElementStyle" and r["status"] == "RESOURCE" for r in rows)
    assert any(r["kind"] == "grid-style" and "implicit/default" in r["value"] for r in rows)
    rows = cs_inventory("var p = new StackPanel { Margin = new Thickness(8) };\n"
                        "factory.SetValue(Control.FontSizeProperty, 14);", "fixture.cs")
    assert {r["kind"] for r in rows} == {"cs-creation", "cs-assignment", "cs-property-api"}
    record = {key: "sample" for key in PAIR_FIELDS}
    record.update(database="GmcSmetaPilot_Visual", data_fingerprint="a" * 64, signed=False, sha="test", png="test.png",
                  dpi_x=96, dpi_y=96, app_scale=1, table_zoom=1, window_width_px=1920, window_height_px=1080)
    assert not validate_pair(record, dict(record))
    assert "signed: differs" in validate_pair(record, dict(record, signed=True))
    assert any("database:" in e for e in validate_pair(record, dict(record, database="Production")))
    assert any("data_fingerprint:" in e for e in validate_pair(record, dict(record, data_fingerprint="wrong")))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    scan = sub.add_parser("scan")
    scan.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    scan.add_argument("--out", type=Path, required=True)
    pair = sub.add_parser("pair")
    pair.add_argument("before", type=Path)
    pair.add_argument("after", type=Path)
    chain = sub.add_parser("ancestry")
    chain.add_argument("tsv", type=Path)
    chain.add_argument("selector")
    sub.add_parser("self-test")
    args = parser.parse_args()
    if args.command == "self-test":
        self_test()
        print("Design audit self-test passed.")
    elif args.command == "scan":
        args.out.mkdir(parents=True, exist_ok=False)
        result = inventory(args.root, args.out)
        print(json.dumps(result, ensure_ascii=False))
        return 1 if result["design_parse_errors"] else 0
    elif args.command == "pair":
        errors = validate_pair(json.loads(args.before.read_text(encoding="utf-8-sig")),
                               json.loads(args.after.read_text(encoding="utf-8-sig")))
        print(json.dumps({"errors": errors, "status": "REJECTED" if errors else "METADATA_MATCH_NOT_VISUAL_ACCEPTANCE"},
                         ensure_ascii=False, indent=2))
        return 1 if errors else 0
    else:
        print(json.dumps(ancestry(args.tsv, args.selector), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
