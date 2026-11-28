import csv
import json
from pathlib import Path
import tempfile
import unittest
from performance_matrix import build, METRICS


class PerformanceMatrixTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def run_file(self, name, ms=100, correct="PASS", count=30, omit="", sha="a"*40, outcome="OK"):
        raw = name + ".csv"
        proof = name + ".txt"
        (self.root / proof).write_text("independent test fixture evidence")
        with (self.root / raw).open("w", newline="") as f:
            w = csv.DictWriter(f, fieldnames=["iteration", "status", *METRICS])
            w.writeheader()
            for i in range(count + 1):
                w.writerow({"iteration": i, "status": "WARMUP" if i == 0 else outcome,
                            **{k: "" if k == omit else ms for k in METRICS}})
        meta = dict(sha=sha, run_id=name, scenario="estimate", client="CSharp", dataset="synthetic",
                    machine_profile="pc-1", topology="LAN", cache="warm", build="Release",
                    runtime="net10", os="Windows", sql_version="16", schema="28",
                    rows=2500, contracts=1, dpi=100, resolution="1920x1080",
                    required_metrics=list(METRICS), correctness=correct,
                    correctness_evidence=proof, raw=raw, runs=count)
        filename = name + ".json"
        (self.root / filename).write_text(json.dumps(meta))
        return filename

    def matrix(self, a, b):
        p = self.root / "manifest.json"
        p.write_text(json.dumps({"pairs": [{"scenario": "estimate", "before": a, "after": b}]}))
        return build(p, 10)["rows"][0]

    def test_complete_comparison_and_p99(self):
        row = self.matrix(self.run_file("before", count=100), self.run_file("after", 90, count=100))
        self.assertEqual("DONE", row["status"])
        self.assertEqual(100, row["before"]["metrics"]["elapsed_ms"]["p99"])
        self.assertEqual(-10, row["deltas_pct"]["elapsed_ms"]["p95"])

    def test_equal_independent_runs_are_valid(self):
        row = self.matrix(self.run_file("before"), self.run_file("after"))
        self.assertEqual("DONE", row["status"])
        self.assertEqual(0, row["deltas_pct"]["elapsed_ms"]["p95"])

    def test_regression(self):
        self.assertEqual("BLOCKED", self.matrix(self.run_file("before"), self.run_file("after", 115))["status"])

    def test_missing_metric_is_partial(self):
        self.assertEqual("PARTIAL", self.matrix(self.run_file("before"), self.run_file("after", 90, omit="sql_count"))["status"])

    def test_correctness_not_verified(self):
        self.assertEqual("PARTIAL", self.matrix(self.run_file("before"), self.run_file("after", 90, correct="UNVERIFIED"))["status"])

    def test_error_blocks(self):
        self.assertEqual("BLOCKED", self.matrix(self.run_file("before"), self.run_file("after", 90, outcome="TIMEOUT"))["status"])

    def test_reused_evidence_rejected(self):
        a = self.run_file("before")
        with self.assertRaises(ValueError):
            self.matrix(a, a)

    def test_metadata_mismatch_rejected(self):
        a, b = self.run_file("before"), self.run_file("after", 90)
        p = self.root / b
        meta = json.loads(p.read_text())
        meta["topology"] = "WIFI"
        p.write_text(json.dumps(meta))
        with self.assertRaises(ValueError):
            self.matrix(a, b)

    def test_escape_rejected(self):
        a = self.run_file("before")
        with self.assertRaises(ValueError):
            self.matrix(a, str((self.root / a).resolve()))

    def test_open_row(self):
        p = self.root / "manifest.json"
        p.write_text(json.dumps({"pairs": [{"scenario": "WPF", "status": "OPEN", "reason": "not run"}]}))
        self.assertEqual("INCOMPLETE", build(p, 10)["status"])


if __name__ == "__main__":
    unittest.main()
