import csv
import tempfile
import unittest
from pathlib import Path
from startup_trace import summarize


class StartupTraceTests(unittest.TestCase):
    def test_exact_pid_and_clock(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            with (root / "startup-test.csv").open("w", newline="") as f:
                w = csv.writer(f)
                w.writerow(["ProcessId", "Frequency", "MonotonicTicks", "Phase"])
                w.writerows([[10, 1000, 110, "launcher_start"],
                             [20, 1000, 200, "main_workspace_ready_rendering"],
                             [30, 1000, 150, "main_workspace_ready_rendering"]])
            with (root / "performance-test.csv").open("w", newline="") as f:
                w = csv.writer(f)
                w.writerow(["ProcessId", "Frequency", "MonotonicTicks", "Phase", "Value1", "DurationMs"])
                w.writerow([10, 1000, 120, "launcher_child_pid", 20, 0])
            result = summarize(root, 10, 100, 1000)
            self.assertEqual(result["app_pid"], 20)
            self.assertEqual(result["ready_rendering_ms"], 100)
            self.assertTrue(result["complete"])
            with self.assertRaises(ValueError):
                summarize(root, 10, 100, 2000)
            with self.assertRaises(ValueError):
                summarize(root, 30, 100, 1000)


if __name__ == "__main__":
    unittest.main()
