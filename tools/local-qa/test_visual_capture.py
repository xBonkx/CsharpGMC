import tempfile
import unittest
from pathlib import Path
import visual_capture as vc

class CaptureTests(unittest.TestCase):
    def test_matrix_has_distinct_physical_cases_and_states(self):
        rows = vc.matrix()
        keys = {(r["page"], r["state"], r["resolution"], r["dpi"], r["app_scale"]) for r in rows}
        self.assertEqual(len(keys), len(rows))
        self.assertTrue(all(r["status"] == "NOT_RUN" for r in rows))
        self.assertEqual({r["dpi"] for r in rows}, {96, 120, 144})
        self.assertIn(("navigation", "deep-nesting", "1366x768", 144, 1), keys)
        self.assertIn(("estimate", "editing", "1920x1080", 120, 1.2), keys)
        for page, state in (("navigation", "logo"), ("navigation", "three-stage-children"),
                            ("navigation", "selected-deep-child"), ("estimate", "rectangular-selection"),
                            ("totals", "smr-from-stages"), ("ks", "ks3"), ("pto-letters", "certificates"),
                            ("cost-summary", "long-money")):
            self.assertIn((page, state, "1366x768", 144, 1), keys)
        self.assertNotIn(("totals", "three-stage-children", "1366x768", 144, 1), keys)

    def test_physical_geometry_not_raster_density(self):
        vc.validate_geometry((1366,768),120,(-1366,0,0,768),(-1360,0,-1,728),120)
        for monitor, frame, dpi in [
            ((0,0,1920,1080),(0,0,1300,700),120),
            ((0,0,1366,768),(0,0,1400,700),120),
            ((0,0,1366,768),(0,0,1300,700),96)]:
            with self.assertRaises(ValueError):
                vc.validate_geometry((1366,768),120,monitor,frame,dpi)

    def test_png_encoder_and_hash(self):
        png = vc.png_bytes(2,1,bytes([0,0,255,255,0,255,0,255]))
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "capture.png"
            path.write_bytes(png)
            self.assertEqual(vc.png_size(path),(2,1))
            self.assertEqual(len(vc.digest(path)),64)
        with self.assertRaises(ValueError):
            vc.png_bytes(2,1,b"")

    def test_capture_rgb_roundtrip_and_crop(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "capture.png"
            rgb = bytes([255,0,0, 0,255,0, 0,0,255, 255,255,255])
            path.write_bytes(vc.rgb_png(2,2,rgb))
            self.assertEqual(vc.read_capture_rgb(path), (2,2,rgb))
            self.assertEqual(vc.crop_rgb(2,2,rgb,(1,0,1,2)), (1,2,bytes([0,255,0,255,255,255])))
            with self.assertRaises(ValueError):
                vc.crop_rgb(2,2,rgb,(1,1,2,2))
            corrupt = bytearray(path.read_bytes())
            corrupt[-5] ^= 1
            path.write_bytes(corrupt)
            with self.assertRaises(ValueError):
                vc.read_capture_rgb(path)

    def test_raster_difference_is_not_acceptance(self):
        before = bytes([0]*12)
        after = bytes([0,0,0, 5,0,0, 0,0,0, 0,0,7])
        result, diff = vc.compare_rgb(2,2,before,after)
        self.assertEqual(diff, after)
        self.assertEqual(result["different_pixels"], 2)
        self.assertEqual(result["difference_bounds_xywh"], [1,0,1,2])
        self.assertEqual(result["mean_absolute_channel_difference_0_255"], 1)
        self.assertEqual(result["status"], "RASTER_DIFFERENCE_NOT_VISUAL_ACCEPTANCE")
        result, _ = vc.compare_rgb(2,2,before,before)
        self.assertIsNone(result["difference_bounds_xywh"])
        self.assertEqual(result["different_fraction"], 0)

    def test_refuse_nonpilot_cutover_and_unconfirmed(self):
        base = dict(page="estimate",state="normal",app_scale=1,sha="a"*40,
                    database="GmcSmetaPilot_Test",synthetic_fixture_confirmed=True,
                    fixture_id="F1",object_id=1,document_id=2,data_fingerprint="b"*64,
                    role="editor",permissions="write",signed=False,smr_mode="sum",
                    columns_profile="default",table_zoom=1)
        vc.validate_request(base)
        for changes in (dict(database="Production"),dict(database="GmcSmetaPilot_Cutover_1"),
                        dict(synthetic_fixture_confirmed=False),dict(state="invented")):
            with self.assertRaises(ValueError):
                vc.validate_request(dict(base,**changes))

if __name__ == "__main__":
    unittest.main()
