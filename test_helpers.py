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

    def test_image_info_reads_per_index_details_and_service_pack_revision(self):
        worker = main.Worker(queue.Queue(), threading.Event(), None)
        summary = """Index : 1
Name : Windows 11 IoT Enterprise LTSC Evaluation
Description : Windows 11 IoT Enterprise LTSC Evaluation
Size : 18296596630 bytes

Index : 2
Name : Windows 11 IoT Enterprise LTSC Evaluation ARM64
Description : Windows 11 IoT Enterprise LTSC Evaluation ARM64
Size : 18296596630 bytes
"""
        x64_details = """Index : 1
Name : Windows 11 IoT Enterprise LTSC Evaluation
Architecture : x64
Version : 10.0.26100
ServicePack Build : 1742
"""
        arm64_details = """Index : 2
Name : Windows 11 IoT Enterprise LTSC Evaluation ARM64
Architecture : ARM64
Version : 10.0.26100
ServicePack Build : 0
"""
        responses = [
            main.subprocess.CompletedProcess([], 0, summary, None),
            main.subprocess.CompletedProcess([], 0, x64_details, None),
            main.subprocess.CompletedProcess([], 0, arm64_details, None),
        ]
        with patch.object(worker, "run_cmd", side_effect=responses) as run_cmd:
            infos = worker.image_info(Path("install.wim"))

        self.assertEqual(len(infos), 2)
        self.assertEqual(infos[0]["architecture"], "x64")
        self.assertEqual(infos[0]["version"], "10.0.26100.1742")
        self.assertEqual(worker.build_family(infos[0]["version"]), 26100)
        self.assertEqual(infos[1]["architecture"], "ARM64")
        self.assertEqual(infos[1]["version"], "10.0.26100.0")
        self.assertEqual(run_cmd.call_count, 3)
        self.assertIn("/Index:1", run_cmd.call_args_list[1].args[0])
        self.assertIn("/Index:2", run_cmd.call_args_list[2].args[0])

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

    def test_uup_dump_diagnostic_uses_build_list_endpoint(self):
        url = main._ll_uup_listid_url("Windows 11")
        parsed = main.urllib.parse.urlparse(url)
        self.assertEqual(parsed.scheme, "https")
        self.assertEqual(parsed.netloc, "api.uupdump.net")
        self.assertEqual(parsed.path, "/listid.php")
        self.assertEqual(
            main.urllib.parse.parse_qs(parsed.query),
            {"search": ["Windows 11"], "sortByDate": ["1"]},
        )

    def test_uup_dump_build_response_is_validated(self):
        expected = [{"title": "Windows 11, version 24H2", "build": "26100"}]
        self.assertEqual(
            main._ll_uup_response_builds({"response": {"builds": expected}}),
            expected,
        )
        with self.assertRaisesRegex(RuntimeError, "response.builds"):
            main._ll_uup_response_builds({"response": {}})
        with self.assertRaisesRegex(RuntimeError, "API"):
            main._ll_uup_response_builds({"response": {"error": "bad request"}})

    def test_uup_dump_build_response_accepts_sparse_object_and_no_results(self):
        rows = [
            {"uuid": "first", "build": "10.0.26100.1", "arch": "amd64"},
            {"uuid": "third", "build": "10.0.26100.2", "arch": "amd64"},
        ]
        self.assertEqual(
            main._ll_uup_response_body_builds({"builds": {"0": rows[0], "2": rows[1]}}),
            rows,
        )
        self.assertEqual(
            main._ll_uup_response_body_builds({"error": "SEARCH_NO_RESULTS"}),
            [],
        )
        with self.assertRaisesRegex(RuntimeError, "некорректные элементы"):
            main._ll_uup_response_body_builds({"builds": {"0": "26100"}})

    def test_uup_dump_file_metadata_normalizer_skips_string_values(self):
        files = {
            "Microsoft-Windows-Client-Language-Pack_x64_ru-ru.cab": {
                "url": "https://download.microsoft.com/example.cab",
                "sha1": "0123456789012345678901234567890123456789",
            },
            "bad-entry.cab": "unexpected-string",
        }
        rows = main._ll_uup_response_body_files({"files": files})
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0][0], "Microsoft-Windows-Client-Language-Pack_x64_ru-ru.cab")
        self.assertIn("url", rows[0][1])

    def test_uup_dump_api_treats_search_no_results_http_400_as_empty_result(self):
        import io
        payload = b'{"response":{"error":"SEARCH_NO_RESULTS"},"jsonApiVersion":"1"}'
        error = main.urllib.error.HTTPError(
            "https://api.uupdump.net/listid.php?search=10240",
            400,
            "Bad Request",
            {},
            io.BytesIO(payload),
        )
        with patch.object(main.urllib.request, "urlopen", side_effect=error):
            result = main._ll_api_json("https://api.uupdump.net/listid.php?search=10240")
        self.assertEqual(result, {"error": "SEARCH_NO_RESULTS"})

    def test_uup_dump_diagnostic_reports_empty_historical_families_without_api_failure(self):
        def fake_fetch(search_term):
            family = int(search_term)
            if family in (14393, 10240):
                return main._ll_uup_response_body_builds({"error": "SEARCH_NO_RESULTS"})
            product = "Windows 11" if family >= 22000 else "Windows 10"
            return main._ll_uup_response_body_builds({
                "builds": {
                    "0": {"title": f"{product}, version LTSC ({family}.1)", "build": f"10.0.{family}.1", "arch": "amd64"},
                    "2": {"title": f"{product}, version LTSC ({family}.2)", "build": f"10.0.{family}.2", "arch": "amd64"},
                }
            })

        report = main._ll_uup_diagnostic_report(fake_fetch)
        self.assertIn("сборка 14393): 0 записей", report)
        self.assertIn("сборка 10240): 0 записей", report)
        self.assertIn("сборка 26100): 2 записей", report)
        self.assertNotIn("ошибка запроса", report)

    def test_uup_dump_client_candidate_filter_rejects_server_and_hci(self):
        self.assertTrue(main._ll_uup_is_client_build(
            {"title": "Windows 11, version 24H2 (26100.1742) amd64"}, 26100
        ))
        self.assertTrue(main._ll_uup_is_client_build(
            {"title": "Feature update to Windows 10, version 21H2 (19044.7811) amd64"}, 19044
        ))
        for title in (
            "Update for Microsoft server operating system (26100.33674)",
            "Windows Server 2025 (26100.33650)",
            "Security Update for Azure Stack HCI (26100.33438)",
        ):
            self.assertFalse(main._ll_uup_is_client_build({"title": title}, 26100))
        self.assertFalse(main._ll_uup_is_client_build(
            {"title": "Windows 10, version 21H2 (19044.7811) amd64"}, 26100
        ))

    def test_uup_build_key_parses_dism_and_uup_versions(self):
        self.assertEqual(main._ll_uup_build_key("10.0.26100.1742"), (26100, 1742))
        self.assertEqual(main._ll_uup_build_key("26100.1742"), (26100, 1742))
        self.assertEqual(main._ll_uup_build_key("10.0.19044"), (19044, 0))
        self.assertIsNone(main._ll_uup_build_key("not-a-build"))

    def test_uup_dump_diagnostic_counts_only_client_builds(self):
        def fake_fetch(search_term):
            family = int(search_term)
            if family == 26100:
                return [
                    {"build": "10.0.26100.33674", "arch": "amd64", "title": "Update for Microsoft server operating system (26100.33674)"},
                    {"build": "26100.1742", "arch": "amd64", "title": "Windows 11, version 24H2 (26100.1742) amd64"},
                ]
            product = "Windows 11" if family >= 22000 else "Windows 10"
            return [{
                "build": f"10.0.{family}.1",
                "arch": "amd64",
                "title": f"{product}, version LTSC ({family}.1)",
            }]

        report = main._ll_uup_diagnostic_report(fake_fetch)
        self.assertIn("сборка 26100): 1 записей", report)
        self.assertNotIn("сборка 26100): 2 записей", report)

    def test_uup_dump_diagnostic_covers_all_iot_enterprise_ltsc_families(self):
        self.assertEqual(
            main._LL_UUP_IOT_LTSC_FAMILIES,
            (
                ("Windows 11 IoT Enterprise LTSC 2024", 26100),
                ("Windows 10 IoT Enterprise LTSC 2021", 19044),
                ("Windows 10 IoT Enterprise LTSC 2019", 17763),
                ("Windows 10 IoT Enterprise LTSC 2016", 14393),
                ("Windows 10 IoT Enterprise LTSC 2015", 10240),
            ),
        )

    def test_uup_dump_diagnostic_queries_each_build_family_and_filters_exact_match(self):
        requested = []
        def fake_fetch(search_term):
            requested.append(search_term)
            family = int(search_term)
            product = "Windows 11" if family >= 22000 else "Windows 10"
            return [
                {"title": f"{product}, version LTSC ({family}.1)", "build": f"10.0.{family}.1", "arch": "x64"},
                {"title": "Windows 11, version unrelated (99999.1)", "build": "10.0.99999.1", "arch": "x64"},
            ]

        report = main._ll_uup_diagnostic_report(fake_fetch)
        self.assertEqual(requested, ["26100", "19044", "17763", "14393", "10240"])
        self.assertIn("Windows 11 IoT Enterprise LTSC 2024 (сборка 26100): 1 записей", report)
        self.assertIn("Windows 10 IoT Enterprise LTSC 2021 (сборка 19044): 1 записей", report)
        self.assertIn("Windows 10 IoT Enterprise LTSC 2019 (сборка 17763): 1 записей", report)
        self.assertIn("Windows 10 IoT Enterprise LTSC 2016 (сборка 14393): 1 записей", report)
        self.assertIn("Windows 10 IoT Enterprise LTSC 2015 (сборка 10240): 1 записей", report)
        self.assertIn("архитектуры: x64", report)



    def test_uup_listlangs_url_and_language_response(self):
        url = main._ll_uup_listlangs_url("12345678-abcd")
        parsed = main.urllib.parse.urlparse(url)
        self.assertEqual(parsed.path, "/listlangs.php")
        self.assertEqual(main.urllib.parse.parse_qs(parsed.query), {"id": ["12345678-abcd"]})
        self.assertEqual(
            main._ll_uup_response_body_langs({"langList": ["en-us", "ru-RU", "de-de"]}),
            ["de-de", "en-us", "ru-ru"],
        )
        self.assertEqual(
            main._ll_uup_response_body_langs({"langList": {"0": "ru-RU", "1": "en-US"}}),
            ["en-us", "ru-ru"],
        )
        with self.assertRaisesRegex(RuntimeError, "langList"):
            main._ll_uup_response_body_langs({"languages": ["ru-ru"]})

    def test_uup_install_build_filter_rejects_servicing_and_oobe_updates(self):
        self.assertTrue(main._ll_uup_is_install_build({
            "title": "Windows 11, version 24H2 (26100.1742) amd64"
        }))
        self.assertTrue(main._ll_uup_is_install_build({
            "title": "Feature update to Windows 10, version 21H2 (19044.1) amd64"
        }))
        for title in (
            "Critical OOBE Update for Windows 11 - KB5128942 (26100.9544)",
            "Security Update for Windows 11, version 24H2 (26100.9544)",
            "Cumulative Update for Windows 11, version 24H2 (26100.9544)",
            "Update for Microsoft server operating system (26100.33674)",
            "Windows 11 servicing stack (26100.9000)",
        ):
            self.assertFalse(main._ll_uup_is_install_build({"title": title}))
        self.assertTrue(main._ll_uup_is_unsupported_combination(
            RuntimeError("UUP Dump API вернул HTTP 400: UNSUPPORTED_COMBINATION")
        ))

    def test_uup_client_language_pack_esd_is_recognized_for_conversion(self):
        self.assertTrue(main._ll_is_lp_esd(
            Path("Microsoft-Windows-Client-LanguagePack-Package_ru-ru~31bf3856ad364e35~amd64~ru-ru~.esd"),
            "x64",
        ))
        self.assertFalse(main._ll_is_lp_esd(
            Path("Microsoft-Windows-Client-LanguagePack-Package_ru-ru~31bf3856ad364e35~arm64~ru-ru~.esd"),
            "x64",
        ))
        self.assertFalse(main._ll_is_lp_esd(
            Path("Microsoft-Windows-Server-LanguagePack-Package_ru-ru~31bf3856ad364e35~amd64~ru-ru~.esd"),
            "x64",
        ))
        self.assertTrue(main._ll_is_lp(
            Path("Microsoft-Windows-Client-LanguagePack-Package_ru-ru~31bf3856ad364e35~amd64~ru-ru~.cab"),
            "x64",
        ))

    def test_sxs_manifest_compression_signatures_are_detected(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            for signature in (b"DCN\x01", b"DCM\x01", b"DCS\x01", b"DCD\x01"):
                manifest = root / (signature[:3].decode("ascii") + ".manifest")
                manifest.write_bytes(signature + b"payload")
                self.assertTrue(main._ll_sxs_manifest_requires_expansion(manifest))
            plain = root / "plain.manifest"
            plain.write_text("<?xml version='1.0'?>", encoding="utf-8")
            self.assertFalse(main._ll_sxs_manifest_requires_expansion(plain))

    def test_makecab_ddf_preserves_paths_and_uses_lzx(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td) / "payload"
            nested = root / "nested"
            nested.mkdir(parents=True)
            (root / "update.mum").write_text("mum", encoding="ascii")
            (nested / "package.manifest").write_text("manifest", encoding="ascii")
            ddf = main._ll_makecab_ddf(root, "Client-LP.cab")
            self.assertIn(".Set CabinetNameTemplate=Client-LP.cab", ddf)
            self.assertIn(".Set SourceDir=.", ddf)
            self.assertIn(".Set CompressionType=LZX", ddf)
            self.assertIn(".Set CompressionMemory=21", ddf)
            self.assertIn('"update.mum"', ddf)
            self.assertIn('"nested\\package.manifest"', ddf)
            self.assertEqual(ddf, main._ll_makecab_ddf(root, "Client-LP.cab"))



if __name__ == "__main__":
    unittest.main(verbosity=2)
