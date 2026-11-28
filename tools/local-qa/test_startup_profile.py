import csv
import tempfile
import unittest
from pathlib import Path

from startup_profile import PAIRS, analyze


class StartupProfileTests(unittest.TestCase):
    def write(self, directory, events):
        path = Path(directory) / "startup.csv"
        with path.open("w", newline="", encoding="utf-8") as stream:
            writer = csv.writer(stream)
            writer.writerow(("ProcessId", "Frequency", "Phase", "MonotonicTicks"))
            writer.writerows((pid, 1000, phase, tick) for pid, phase, tick in events)
        return path

    def complete_events(self):
        phases = {"app_process_entry": 100, "trace_build_release": 101}
        for index, (_, start, end) in enumerate(PAIRS):
            phases[start] = 110 + index * 10
            phases[end] = 115 + index * 10
        return [(42, phase, tick) for phase, tick in phases.items()]

    def test_complete_pairs_and_no_external_attribution(self):
        with tempfile.TemporaryDirectory() as directory:
            result = analyze(self.write(directory, self.complete_events()), "fx", "off", "warm")
        self.assertTrue(result["complete"])
        self.assertEqual(5, result["phase_ms"]["application_resources"])
        self.assertIsNone(result["phase_ms"]["icon_decode"])
        self.assertEqual("NOT_ANALYZED", result["external_cpu_jit_disk_antivirus_attribution"])

    def test_missing_or_repeated_required_marker_is_incomplete(self):
        events = [row for row in self.complete_events() if row[1] != "app_application_initialize_end"]
        events.append((42, "main_initialize_component_end", 200))
        with tempfile.TemporaryDirectory() as directory:
            result = analyze(self.write(directory, events), "sc", "unknown", "cold")
        self.assertFalse(result["complete"])
        self.assertIn("application_resources", result["missing_required_phases"])
        self.assertIn("window_xaml", result["missing_required_phases"])

    def test_mixed_processes_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.write(directory, self.complete_events() + [(99, "other", 999)])
            with self.assertRaises(ValueError):
                analyze(path, "fx", "off", "warm")


if __name__ == "__main__":
    unittest.main()
