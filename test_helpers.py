import unittest
from pathlib import Path
import tempfile
import queue
import threading
import main


class HelperTests(unittest.TestCase):
    def test_human_bytes(self):
        self.assertEqual(main.human_bytes(0), "0 Б")
        self.assertIn("КиБ", main.human_bytes(2048))
        self.assertIn("ГиБ", main.human_bytes(1024**3))

    def test_locate_ru_lp_arch(self):
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "Microsoft-Windows-Client-Language-Pack_x64_ru-ru.cab"
            p.touch()
            w = main.Worker(queue.Queue(), threading.Event(), None)
            self.assertEqual(w.locate_ru_lp({"architecture": "x64"}, [p]), p)
            self.assertIsNone(w.locate_ru_lp({"architecture": "arm64"}, [p]))

    def test_detect_build_family_and_architecture(self):
        self.assertEqual(main.Worker.build_family("10.0.26100.1"), 26100)
        self.assertEqual(main.Worker.build_family("10.0.19044.1288"), 19044)
        self.assertIsNone(main.Worker.build_family("unknown"))
        self.assertEqual(main.Worker.arch_token("x64"), "x64")
        self.assertEqual(main.Worker.arch_token("ARM64"), "arm64")
        self.assertEqual(main.Worker.arch_token("x86"), "x86")

    def test_accept_generic_cross_edition_client_language_pack(self):
        # The edition name is not a filter: the same compatible client base can serve IoT/Enterprise SKUs.
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "Windows-Enterprise-Language-Pack_x64_ru-ru.cab"
            p.touch()
            w = main.Worker(queue.Queue(), threading.Event(), None)
            self.assertEqual(w.locate_ru_lp({"architecture": "amd64", "version": "10.0.26100.1"}, [p]), p)

    def test_reject_server_and_winpe_language_packs(self):
        with tempfile.TemporaryDirectory() as td:
            files = []
            for name in [
                "Microsoft-Windows-Client-Language-Pack_x64_ru-ru.cab",
                "Microsoft-Windows-Server-Language-Pack_x64_ru-ru.cab",
                "WinPE-Setup_ru-ru.cab",
                "Windows-Server-Language-Pack_x64_ru-ru.cab",
            ]:
                p = Path(td) / name
                p.touch()
                files.append(p)
            w = main.Worker(queue.Queue(), threading.Event(), None)
            self.assertEqual(w.locate_ru_lp({"architecture": "amd64"}, files), files[0])


if __name__ == "__main__":
    unittest.main(verbosity=2)
