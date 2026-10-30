import unittest
from unittest.mock import patch
from pathlib import Path
import tempfile
import queue
import threading
import main


class HelperTests(unittest.TestCase):
    def test_log_panel_sashes_resize_only_vertically(self):
        options = main._ll_log_panedwindow_options()
        self.assertEqual(options["orient"], main.tk.VERTICAL)
        self.assertEqual(options["sashcursor"], "sb_v_double_arrow")
        self.assertEqual(options["sashwidth"], 8)
        self.assertTrue(options["opaqueresize"])
        self.assertFalse(options["showhandle"])
        pane_min_sizes = main._ll_log_pane_min_sizes()
        self.assertEqual(pane_min_sizes["log"], 70)
        self.assertGreater(pane_min_sizes["content"], pane_min_sizes["log"])
        self.assertLessEqual(pane_min_sizes["content"], 160)
        self.assertEqual(main._ll_responsive_wraplength(320), 144)
        self.assertEqual(main._ll_responsive_wraplength(1200), 520)
        self.assertGreaterEqual(main._ll_responsive_wraplength(100), 140)
        self.assertGreaterEqual(main._ll_responsive_wraplength(700, fraction=0.40, minimum=150), 150)

    def test_donor_iso_drop_populates_donor_field(self):
        class Value:
            def __init__(self):
                self.value = ""

            def set(self, value):
                self.value = value

        with tempfile.TemporaryDirectory() as td:
            iso = Path(td) / "donor_ru-RU.iso"
            iso.write_bytes(b"test")
            donor = Value()
            status = Value()
            app = type("FakeApp", (), {})()
            app.tk = type("FakeTk", (), {"splitlist": lambda _self, _data: [str(iso)]})()
            app.donor_iso_var = donor
            app.status_var = status
            event = type("FakeEvent", (), {"data": str(iso)})()
            with patch.object(main.messagebox, "showwarning") as warning, patch.object(main.messagebox, "showerror") as error:
                main.App._on_donor_drop(app, event)
            self.assertEqual(donor.value, str(iso))
            self.assertIn("донорский", status.value.lower())
            warning.assert_not_called()
            error.assert_not_called()

    def test_donor_iso_drop_rejects_non_iso(self):
        class Value:
            def __init__(self):
                self.value = ""

            def set(self, value):
                self.value = value

        with tempfile.TemporaryDirectory() as td:
            txt = Path(td) / "not-an-iso.txt"
            txt.write_text("test", encoding="utf-8")
            app = type("FakeApp", (), {})()
            app.tk = type("FakeTk", (), {"splitlist": lambda _self, _data: [str(txt)]})()
            app.donor_iso_var = Value()
            app.status_var = Value()
            event = type("FakeEvent", (), {"data": str(txt)})()
            with patch.object(main.messagebox, "showwarning") as warning:
                main.App._on_donor_drop(app, event)
            warning.assert_called_once()
            self.assertEqual(app.donor_iso_var.value, "")

    def test_both_iso_drop_zones_register_independently(self):
        class DropLabel:
            def __init__(self):
                self.targets = []
                self.bindings = []
                self.options = {}

            def drop_target_register(self, target):
                self.targets.append(target)

            def dnd_bind(self, sequence, handler):
                self.bindings.append((sequence, handler))

            def configure(self, **kwargs):
                self.options.update(kwargs)

        app = type("FakeApp", (), {})()
        app.drop_label = DropLabel()
        app.donor_drop_label = DropLabel()
        app._on_drop = lambda event: None
        app._on_donor_drop = lambda event: None
        with patch.object(main, "DND_FILES", "DND_FILES"):
            main.App._try_enable_dnd(app)
        for label in (app.drop_label, app.donor_drop_label):
            self.assertEqual(label.targets, ["DND_FILES"])
            self.assertEqual(len(label.bindings), 1)
            self.assertEqual(label.bindings[0][0], "<<Drop>>")
        self.assertIn("исходный", app.drop_label.options["text"])
        self.assertIn("донорский", app.donor_drop_label.options["text"])

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


    def test_swm_sequence_is_contiguous(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "install.swm").write_bytes(b"one")
            (root / "install2.swm").write_bytes(b"two")
            self.assertEqual([p.name for p in main._ll_sequence_swm(root)], ["install.swm", "install2.swm"])
            (root / "install2.swm").unlink()
            (root / "install3.swm").write_bytes(b"three")
            with self.assertRaises(RuntimeError):
                main._ll_sequence_swm(root)

    def test_update_manifest_checks_sha256_and_order(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            checkpoint = root / "KBCheckpoint.msu"
            target = root / "KBTarget.msu"
            checkpoint.write_bytes(b"checkpoint")
            target.write_bytes(b"target-lcu")
            manifest = {
                "schema_version": 1,
                "target_build_family": 26100,
                "architecture": "x64",
                "ordered_packages": [
                    {"file": checkpoint.name, "kind": "checkpoint", "sha256": main._ll_hash(checkpoint)},
                    {"file": target.name, "kind": "target_lcu", "sha256": main._ll_hash(target)},
                ],
            }
            (root / "updates.json").write_text(__import__("json").dumps(manifest), encoding="utf-8")
            plan = main._ll_parse_update_manifest(root)
            self.assertEqual([x["kind"] for x in plan[1:]], ["checkpoint", "target_lcu"])
            manifest["ordered_packages"][0]["sha256"] = "0" * 64
            (root / "updates.json").write_text(__import__("json").dumps(manifest), encoding="utf-8")
            with self.assertRaises(RuntimeError):
                main._ll_parse_update_manifest(root)

    def test_catalog_parser_extracts_title_build_and_guid(self):
        markup = """
        <table id="ctl00_catalogBody_updateMatches">
          <tr><td>check</td><td>Title</td></tr>
          <tr>
            <td><input type="checkbox" id="11111111-2222-3333-4444-555555555555" /></td>
            <td>2026-09 Cumulative Update for Windows 11, version 24H2 for x64-based Systems (KB5129195) (26100.9457)</td>
            <td>Windows 11</td><td>Security Updates</td><td>9/14/2026</td><td>n/a</td><td>4933.5 MB</td><td></td>
          </tr>
        </table>
        """
        rows = main._ll_catalog_parse_search_results(markup)
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["id"], "11111111-2222-3333-4444-555555555555")
        self.assertEqual(rows[0]["build_family"], 26100)
        self.assertEqual(rows[0]["build_revision"], 9457)

    def test_catalog_filters_stable_architecture_and_build(self):
        rows = [
            {"id": "a", "title": "2026-09 Cumulative Update for Windows 11, version 24H2 for x64-based Systems (KB5129195) (26100.9457)", "build_family": 26100, "build_revision": 9457},
            {"id": "b", "title": "2026-09 Preview Cumulative Update for Windows 11, version 24H2 for x64-based Systems (KB5129999) (26100.9999)", "build_family": 26100, "build_revision": 9999},
            {"id": "c", "title": "2026-09 Cumulative Update for Windows 11, version 24H2 for arm64-based Systems (KB5129195) (26100.9457)", "build_family": 26100, "build_revision": 9457},
            {"id": "d", "title": "2026-09 Cumulative Update for Windows 11, version 25H2 for x64-based Systems (KB5129195) (26200.9457)", "build_family": 26200, "build_revision": 9457},
        ]
        got = main._ll_catalog_filter_results(rows, "x64", kb="5129195", target_build=26100)
        self.assertEqual([row["id"] for row in got], ["a"])

    def test_catalog_download_dialog_extracts_trusted_url_and_sha1(self):
        digest = "a" * 40
        markup = (
            "downloadInformation[0].files[0].url = "
            "'https://download.windowsupdate.com/c/msdownload/update/software/secu/2026/09/windows11.0-kb5129195-x64_test.msu';"
            "downloadInformation[0].files[0].fileName = 'windows11.0-kb5129195-x64_test.msu';"
            f"downloadInformation[0].files[0].digest = '{digest}';"
        )
        items = main._ll_catalog_extract_downloads(markup)
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0]["filename"], "windows11.0-kb5129195-x64_test.msu")
        self.assertEqual(items[0]["sha1"], digest)
        self.assertTrue(main._ll_catalog_safe_url(items[0]["url"]))

    def test_catalog_rejects_untrusted_download_hosts(self):
        self.assertFalse(main._ll_catalog_safe_url("https://updates.example.invalid/file.msu"))
        self.assertFalse(main._ll_catalog_safe_url("http://download.windowsupdate.com/file.msu"))
        self.assertTrue(main._ll_catalog_safe_url("https://catalog.s.download.windowsupdate.com/file.msu"))

    def test_catalog_file_arch_recognizes_underscore_separators(self):
        self.assertEqual(main._ll_catalog_file_arch("windows11.0-kb5129195-x64_abcd.msu"), "x64")
        self.assertEqual(main._ll_catalog_file_arch("windows11.0-kb5129195-arm64_abcd.msu"), "arm64")
        self.assertIsNone(main._ll_catalog_file_arch("SomeUpdate.msu"))

    def test_catalog_latest_query_profiles_and_unknown_build_fallback(self):
        self.assertIn("version 24H2", main._ll_catalog_query_for_build(26100, "x64"))
        self.assertIn("arm64-based Systems", main._ll_catalog_query_for_build(26100, "arm64"))
        unknown = main._ll_catalog_query_for_build(99999, "x64")
        self.assertIn("99999", unknown)
        self.assertIn("x64-based Systems", unknown)
        with self.assertRaises(RuntimeError):
            main._ll_catalog_query_for_build(99999, "mips")

    def test_catalog_query_candidates_add_build_fallbacks_without_duplicates(self):
        candidates = main._ll_catalog_query_candidates("Primary query", 99999)
        self.assertEqual(candidates[0], "Primary query")
        self.assertIn("Cumulative Update 99999", candidates)
        self.assertIn("OS Build 99999", candidates)
        self.assertIn("Cumulative Update", candidates)
        self.assertEqual(len(candidates), len(set(candidates)))

    def test_catalog_search_falls_back_to_exact_build_query(self):
        markup = """
        <table id="ctl00_catalogBody_updateMatches">
          <tr><td>check</td><td>Title</td></tr>
          <tr>
            <td><input type="checkbox" id="aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" /></td>
            <td>2026-10 Cumulative Update for Windows 11, version 26H2 for x64-based Systems (KB5129999) (99999.1234)</td>
            <td>Windows 11</td><td>Security Updates</td><td>10/13/2026</td><td>n/a</td><td>1200 MB</td><td></td>
          </tr>
        </table>
        """
        seen_queries = []
        def fake_fetch(opener, url, data=None, referer=None):
            query = main.urllib.parse.parse_qs(main.urllib.parse.urlparse(url).query).get("q", [""])[0]
            seen_queries.append(query)
            return markup if query == "Cumulative Update 99999" else "<table></table>"
        with patch.object(main, "_ll_catalog_fetch", side_effect=fake_fetch):
            rows = main._ll_catalog_search("Primary query", "x64", target_build=99999)
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["build_family"], 99999)
        self.assertIn("Cumulative Update 99999", seen_queries)

    def test_adk_hash_mismatch_recognizes_hex_and_decimal_exit_codes(self):
        self.assertTrue(main._ll_adk_hash_mismatch(RuntimeError("exit code 2148077575")))
        self.assertTrue(main._ll_adk_hash_mismatch(RuntimeError("0x80091007")))
        self.assertFalse(main._ll_adk_hash_mismatch(RuntimeError("exit code 2")))


    def test_iso_mount_explicit_storage_type_is_added_once(self):
        command = "Mount-DiskImage -ImagePath 'sample.iso' -PassThru"
        fixed = main._ll_explicit_iso_storage_type(command)
        self.assertIn("Mount-DiskImage -StorageType ISO -ImagePath", fixed)
        self.assertEqual(fixed.lower().count("-storagetype iso"), 1)
        already_typed = main._ll_explicit_iso_storage_type(
            "Mount-DiskImage -StorageType ISO -ImagePath 'sample.iso'"
        )
        self.assertEqual(already_typed.lower().count("-storagetype iso"), 1)

    def test_iso_mount_provider_failure_has_actionable_diagnostic(self):
        message = main._ll_iso_mount_failure_message(
            "Mount-DiskImage failed: HRESULT 0xC03A0014",
            r"D:\Downloads\Windows.iso",
        )
        self.assertIsNotNone(message)
        self.assertIn("ERROR_VIRTDISK_PROVIDER_NOT_FOUND", message)
        self.assertIn("Проводник Windows", message)
        self.assertIsNone(main._ll_iso_mount_failure_message("some unrelated PowerShell error", "a.iso"))

    def test_powershell_wrapper_uses_explicit_iso_type_and_surfaces_provider_error(self):
        worker = main.Worker(queue.Queue(), threading.Event(), None)
        observed = {}
        def fake_run_cmd(args, *, check=True, respect_cancel=True):
            observed["command"] = args[-1]
            output = "Mount-DiskImage : A virtual disk support provider for the specified file was not found. HRESULT 0xC03A0014"
            return __import__("subprocess").CompletedProcess(args, 1, output, None)
        worker.run_cmd = fake_run_cmd
        with self.assertRaisesRegex(RuntimeError, "ERROR_VIRTDISK_PROVIDER_NOT_FOUND"):
            worker.powershell("Mount-DiskImage -ImagePath 'Windows.iso'")
        self.assertIn("-StorageType ISO", observed["command"])


    def test_elevated_command_line_quotes_script_and_pending_config(self):
        script = r"C:\Program Files\LocaLizard\main.py"
        pending = r"C:\Users\Test User\AppData\Local\LocaLizard\pending action.json"
        args = ["--resume-elevated", pending]
        self.assertEqual(
            main._ll_elevated_command_line(script, args, frozen=False),
            main.subprocess.list2cmdline([script, *args]),
        )
        self.assertEqual(
            main._ll_elevated_command_line(script, args, frozen=True),
            main.subprocess.list2cmdline(args),
        )

    def test_resume_elevated_path_requires_value(self):
        pending = r"C:\Users\Test User\pending.json"
        self.assertEqual(
            main._ll_resume_elevated_path(["LocaLizard.exe", "--resume-elevated", pending]),
            Path(pending),
        )
        self.assertIsNone(main._ll_resume_elevated_path(["LocaLizard.exe", "--resume-elevated"]))

    def test_elevated_payload_preserves_iso_paths_and_options(self):
        cfg = main.RunConfig(
            source_iso=Path(r"C:\ISO Files\source.iso"),
            donor_iso=None,
            output_iso=Path(r"C:\Output Files\result.iso"),
            package_root=Path(r"C:\Cache\Packages"),
            fod_root=Path(r"C:\FOD Source"),
            work_root=Path(r"C:\Work Dir"),
            keep_work=False,
            process_setup=True,
            updates_root=Path(r"C:\Update Chain"),
            split_wim=True,
            vm_test=False,
        )
        payload = main._ll_pending_action_payload(cfg)
        self.assertEqual(payload["source_iso"], str(cfg.source_iso))
        self.assertEqual(payload["output_iso"], str(cfg.output_iso))
        self.assertEqual(payload["donor_iso"], "")
        self.assertEqual(payload["updates_root"], str(cfg.updates_root))
        self.assertEqual(payload["keep_work"], False)
        self.assertEqual(payload["process_setup"], True)
        self.assertEqual(payload["split_wim"], True)
        self.assertEqual(payload["vm_test"], False)


if __name__ == "__main__":
    unittest.main(verbosity=2)
