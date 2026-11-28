import tempfile
import unittest
from pathlib import Path

from menu_inventory import candidates, git, snapshot, validate_review, REVIEW_FIELDS, write_csv, resolve_review_sources

class MenuInventoryTests(unittest.TestCase):
    def test_candidate_routes_and_default_editors(self):
        cs = list(candidates("var menu = new ContextMenu();\nPreviewMouseRightButtonDown += Click;\nvar e = new TextBox();", "cs"))
        self.assertEqual(["menu"], cs[0][1])
        self.assertIn("route", cs[1][1])
        self.assertIn("editor", cs[2][1])
        py = list(candidates("menu = QMenu(self)\nself.customContextMenuRequested.connect(open_menu)\nQMessageBox.exec()", "py"))
        self.assertEqual(3, len(py))
        self.assertIn("dialog", py[2][1])
        self.assertIn("menu", list(candidates("<DataGrid.ContextMenu>", "xaml"))[0][1])

    def test_committed_tree_only_and_exclusions(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            git(root, "init")
            git(root, "config", "user.name", "test")
            git(root, "config", "user.email", "test@example.invalid")
            (root / "Меню с пробелами.cs").write_text("new ContextMenu();\n", encoding="utf-8")
            (root / "skip.g.cs").write_text("new ContextMenu();\n", encoding="utf-8")
            (root / "test_menu.py").write_text("QMenu()\n", encoding="utf-8")
            git(root, "add", ".")
            git(root, "commit", "-m", "fixture")
            (root / "Меню с пробелами.cs").write_text("no menu", encoding="utf-8")
            cs = snapshot(root, "HEAD", {"cs", "xaml"})
            py = snapshot(root, "HEAD", {"py"})
            self.assertEqual(1, cs["scanned_files"])
            self.assertIn("skip.g.cs", cs["excluded"])
            self.assertEqual("Меню с пробелами.cs", cs["candidates"][0]["path"])
            self.assertEqual("test", py["candidates"][0]["scope"])
            self.assertEqual(cs, snapshot(root, "HEAD", {"cs", "xaml"}))

    def test_review_requires_two_real_sources_for_match(self):
        rows = [dict(candidate_id="p", language="python", scope="source"),
                dict(candidate_id="c", language="csharp", scope="source"),
                dict(candidate_id="t", language="csharp", scope="test")]
        command = {field: "N/A" for field in REVIEW_FIELDS}
        command.update(menu_id="estimate", state="editable", command_id="delete",
                       text="Delete", order=0, enabled="editable only", visible="yes",
                       python_candidate_ids=["p"], csharp_candidate_ids=["c"], classification="MATCH")
        self.assertEqual(1, validate_review([command], rows))
        with self.assertRaises(ValueError):
            validate_review([command, command], rows)
        with self.assertRaises(ValueError):
            validate_review([{**command, "csharp_candidate_ids": []}], rows)
        with self.assertRaises(ValueError):
            validate_review([{**command, "python_candidate_ids": ["c"]}], rows)
        with self.assertRaises(ValueError):
            validate_review([{**command, "classification": "DONE"}], rows)
        with self.assertRaises(ValueError):
            validate_review([{**command, "enabled": "unknown"}], rows)

        with self.assertRaises(ValueError):
            validate_review([{**command, "csharp_candidate_ids": ["t"]}], rows)
        with self.assertRaises(ValueError):
            validate_review([{**command, "classification": "EXTENSION", "csharp_candidate_ids": []}], rows)
        with self.assertRaises(ValueError):
            validate_review([command], rows + [rows[0]])
        with self.assertRaises(ValueError):
            validate_review([{**command, "menu_id": " estimate "}], rows)
        with self.assertRaises(ValueError):
            validate_review([{**command, "handler": " UNKNOWN "}], rows)
        with self.assertRaises(ValueError):
            validate_review([{**command, "notes": "TBD"}], rows)

    def test_resolved_sources_keep_revision_path_line_and_scope(self):
        rows = [dict(candidate_id="p", language="python", scope="source", revision="py-sha", path="ui/menu.py", line=7),
                dict(candidate_id="c", language="csharp", scope="source", revision="cs-sha", path="Menu.cs", line=12)]
        command = dict(menu_id="estimate", state="editable", command_id="delete",
                       python_candidate_ids=["p"], csharp_candidate_ids=["c"])
        resolved = resolve_review_sources([command], rows)[0]
        self.assertEqual("delete", resolved["command_id"])
        self.assertEqual(dict(candidate_id="p", scope="source", revision="py-sha", path="ui/menu.py", line=7),
                         resolved["python"][0])
        self.assertEqual(12, resolved["csharp"][0]["line"])

    def test_csv_escapes_formula_candidate_without_overwrite(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "rows.csv"
            write_csv(path, [{"source": "=unsafe()"}])
            self.assertIn("'=unsafe()", path.read_text(encoding="utf-8-sig"))
            with self.assertRaises(FileExistsError):
                write_csv(path, [])

if __name__ == "__main__":
    unittest.main()
