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
        self.assertEqual(main._ll_version_key("10.0.26100.100"), (10, 0, 26100, 100, 0, 0, 0, 0))
        self.assertGreater(main._ll_version_key("10.0.26100.100"), main._ll_version_key("10.0.26100.20"))
        with self.assertRaises(RuntimeError):
            main.Worker.arch_token("mips")

    def test_accept_generic_cross_edition_client_language_pack(self):
        # The edition name is not a filter: the same compatible client base can serve IoT/Enterprise SKUs.
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "Windows-Enterprise-Language-Pack_x64_ru-ru.cab"
            p.touch()
            w = main.Worker(queue.Queue(), threading.Event(), None)
            self.assertEqual(w.locate_ru_lp({"architecture": "amd64", "version": "10.0.26100.1"}, [p]), p)


    def test_classify_donor_iso_packages(self):
        self.assertEqual(
            main.Worker.classify_donor_package(Path("x64/langpacks/Microsoft-Windows-Client-Language-Pack_x64_ru-ru.cab")),
            "client_lp",
        )
        self.assertEqual(
            main.Worker.classify_donor_package(Path("LanguageFeatures-OCR-ru-ru-Package.cab")),
            "fod",
        )
        self.assertEqual(
            main.Worker.classify_donor_package(Path("sources/langpacks/ru-ru/lp.cab")),
            None,
        )
        self.assertIsNone(
            main.Worker.classify_donor_package(Path("Microsoft-Windows-Server-Language-Pack_x64_ru-ru.cab"))
        )

    def test_trusted_download_urls(self):
        self.assertTrue(main._ll_trusted_url("https://download.microsoft.com/test.cab"))
        self.assertTrue(main._ll_trusted_url("https://catalog.s.download.windowsupdate.com/test.cab"))
        self.assertFalse(main._ll_trusted_url("https://microsoft.com.evil.example/test.cab"))
        self.assertFalse(main._ll_trusted_url("http://download.microsoft.com/test.cab"))

    def test_managed_cache_manifest_and_tamper_detection(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td) / "Packages" / "auto" / "26100_x64"
            item = root / "01-candidate" / "Microsoft-Windows-Client-Language-Pack_x64_ru-ru.cab"
            item.parent.mkdir(parents=True)
            item.write_bytes(b"unit-test-package")
            self.assertFalse(main._ll_verified_cache(item))
            main._ll_record_cache(item, "test-sha1", "https://download.microsoft.com/test.cab")
            self.assertTrue(main._ll_verified_cache(item))
            item.write_bytes(b"tampered")
            self.assertFalse(main._ll_verified_cache(item))

    def test_strict_client_lp_classifier(self):
        self.assertTrue(main._ll_is_lp(Path("Microsoft-Windows-Client-Language-Pack_x64_ru-ru.cab"), "x64"))
        self.assertFalse(main._ll_is_lp(Path("WinPE-Setup_ru-ru.cab"), "x64"))
        self.assertFalse(main._ll_is_lp(Path("Microsoft-Windows-Server-Language-Pack_x64_ru-ru.cab"), "x64"))
        self.assertFalse(main._ll_is_lp(Path("Microsoft-Windows-Client-Language-Pack_arm64_ru-ru.cab"), "x64"))

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
