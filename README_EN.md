# LocaLizard 0.2.2.8

**LocaLizard** is a graphical utility for preparing Windows IoT Enterprise LTSC installation media with Russian as the default language.

> **Version 0.2.2.8:** the primary application has been migrated to C# / .NET 8 Windows Forms. Historical Python sources and all releases published before this version are retained; previous tags and their assets were not modified. See [dotnet/README.md](dotnet/README.md) for the C# feature set and validation scope.

## Download

- [Latest LocaLizard release](https://github.com/lizardiot/LocaLizard/releases/latest)
- [All releases](https://github.com/lizardiot/LocaLizard/releases)
- [LocaLizard.exe for Windows x64 — version 0.2.2.8](https://github.com/lizardiot/LocaLizard/releases/download/0.2.2.8/LocaLizard.exe)

## Features

- The base UUP Client Language Pack ESD is extracted with native DISM and installed directly from `update.mum`, avoiding an unnecessary CAB rebuild. HTTP Microsoft Delivery CDN responses are permitted only for the exact CDN hosts/path and only with the SHA-1 and file size supplied by UUP Dump.

- GUI for selecting the source ISO, output ISO, working directory, language sources, and update catalog.
- Search for stable cumulative updates (LCUs) or a specified KB through Microsoft Update Catalog.
- Filters Preview/Insider/Canary, Windows Server, and architecture mismatches before download; the user reviews and confirms the selected entry.
- Downloads from trusted Microsoft Update HTTPS domains, verifies the available Catalog SHA-1, and calculates SHA-256. Each prepared update set includes `updates.json`, `catalog-source.json`, and `SHA256.txt`.
- Finds a compatible Client Language Pack ru-RU in a matching Russian ISO, the managed cache, or through the UUP Dump API.
- Detects the image build family and architecture independently of the Windows 10/11 label; covers LTSC 2015, 2016, 2019, 2021, and 2024. Language-pack discovery uses the ISO's exact build, a compatible donor ISO, cache, and available UUP Dump entries.
- Configures the Russian locale through DISM and installs available Language Features from FOD / Language and Optional Features sources.
- When compatible resources are available, processes Windows Setup `boot.wim` index 2, WinPE index 1, and nested WinRE.
- Supports WIM/ESD and input split-WIM `.swm` files; optionally splits the output WIM into SWM parts for FAT32 and processes an existing WIM into a new file.
- Static result validation, logging, and a JSON audit report.
- Resize the log panel vertically by dragging its upper or lower horizontal sash; the interface width is unaffected.
- Two independent drag-and-drop zones accept the target Windows ISO and an optional Russian donor ISO, filling their corresponding fields.
- Optional interactive Hyper-V ISO boot test using a temporary VM without network access, VMConnect, visual confirmation of the Setup screen, and VM cleanup.

## Limitations and environment requirements

- The base ru-RU Client Language Pack supports both CAB and UUP ESD sources. ESD is extracted by DISM and serviced through `update.mum`; local CAB packages and donor ISO packages remain supported.

- Latest-LCU search uses the build family and architecture detected from the selected ISO. If a build has no predefined query profile, the utility searches by numeric OS build and tries fallback Microsoft Update Catalog queries. Automatic download still requires an unambiguous stable update matching the exact build family and architecture. If the Catalog HTML changes or no matching entry is returned, the utility stops rather than guessing; a KB can be entered manually.
- Client Language Pack discovery checks the local cache, a donor ISO, and up to 32 matching client installation builds after a ru-RU language availability check for the same build family and architecture. Localization of an image index cannot continue if no compatible standalone Client Language Pack in CAB or UUP ESD form is available from trusted sources. DISM performs the final applicability check. This covers LTSC build families 10240, 14393, 17763, 19044, and 26100; UUP Dump may not retain entries for every older release, so a compatible donor ISO or local package may be required.
- Extra Russian language capabilities (Basic, OCR, Text-to-Speech, Handwriting) are installed only when a Language and Optional Features repository or mounted ISO is selected. Without a FOD source, LocaLizard skips them instead of asking offline DISM to query Windows Update; a failed optional capability does not roll back the successfully added base Client Language Pack. Results are recorded in the JSON audit.
- Setup/WinPE and WinRE use compatible Russian resources from the selected source and/or the official ADK WinPE Add-on. If the add-on cannot be downloaded or signature-verified, disk space is insufficient, or DISM rejects the CABs, that part of the image is left unchanged; details are written to the log and audit.
- The interactive Hyper-V test requires Windows with Hyper-V enabled, administrator privileges, and VMConnect. It launches a temporary VM without networking but asks the user to visually confirm the Windows Setup screen: a running VM alone does not prove that Setup appeared. This interactive test is not run in GitHub Actions.
- Static validation checks the ISO structure and whether images can be read, but cannot guarantee installation on every hardware configuration. Test the ISO in a separate VM or on a test device before deployment. OOBE can affect the preferred UI language of the first user.
## Run

1. Download `LocaLizard.exe` from the [0.1.1.3 release](https://github.com/lizardiot/LocaLizard/releases/tag/0.1.1.3).
2. Run the application. The main window runs without elevation: drag the target ISO into the first drop zone and the Russian donor ISO into the separate zone in the language-components section. Approve the UAC prompt when the application starts an operation that requires administrator privileges.
3. Select the source Windows ISO, output ISO path, and working directory.
4. If needed, specify a Russian ISO source, FOD / Language and Optional Features sources, and an update catalog.
5. Click **Check prerequisites and create ISO**. The Hyper-V test also requires Hyper-V to be installed and the user to visually confirm the Setup screen.

You do not need to install Python or development tools manually. The source ISO remains unchanged.

## Version history

### [0.2.2.8](https://github.com/lizardiot/LocaLizard/releases/tag/0.2.2.8) — current release
The primary Windows application has been migrated to C# / .NET 8 Windows Forms. The port covers ISO/WIM servicing, UUP Dump Client Language Pack discovery and verification, managed cache integrity, resumable downloads, Microsoft Update Catalog, ordered update manifests, ADK/WinPE, Setup/WinPE/WinRE localization, SWM, patch-existing-WIM, audit, UAC, drag-and-drop, and UUP diagnostics. Regression tests and a Windows DISM integration test for a two-index WIM were added. CI builds and publishes the standalone Windows x64 app. Applying language CAB/MSU packages to specific IoT LTSC images, building a real user ISO, and visually checking Hyper-V boot remain target-environment validation steps; CI does not claim those steps have been performed.

### [0.1.1.4](https://github.com/lizardiot/LocaLizard/releases/tag/0.1.1.4) — previous release
Fixed the invalid DISM system-locale switch (/Set-SysLocale replaces /Set-SystemLocale) and separated base Client Language Pack candidate selection from later servicing, so another LP is tried only when /Add-Package rejects the current one. Optional Russian FODs are limited to Basic, OCR, Text-to-Speech, and Handwriting and are attempted only with a selected source. Optional FOD failures no longer roll back a successfully added base package. Added regression tests. Full Windows Setup resource localization still requires a compatible Russian donor ISO containing sources/ru-ru.

### [0.1.1.3](https://github.com/lizardiot/LocaLizard/releases/tag/0.1.1.3) — previous release
Fixed Microsoft CDN ESD download handling and installed the extracted base Client Language Pack directly through update.mum with integrity verification.


### [0.1.1.3](https://github.com/lizardiot/LocaLizard/releases/tag/0.1.1.3) — current release
Fixed Microsoft Delivery CDN downloads when the final URL remains HTTP: the exact Microsoft CDN host/path is accepted only when UUP metadata provides a valid SHA-1 and expected file size. Base Client Language Pack ESD files are now extracted by DISM and installed directly through `update.mum`, without rebuilding a CAB; the extracted manifest is cached and verified. Added regression tests for the HTTP integrity policy and update.mum discovery.

### [0.1.1.1](https://github.com/lizardiot/LocaLizard/releases/tag/0.1.1.1) — previous release
Fixed UUP Language Pack downloads from Microsoft Delivery CDN. HTTP is permitted only as a narrowly scoped bootstrap URL for the exact Microsoft CDN host and file path; the final URL after redirects must be trusted HTTPS. Diagnostics now distinguish a missing package from a detected package whose download, verification, or ESD conversion failed. Added URL policy and HTTPS-redirect tests.

### [0.1.1](https://github.com/lizardiot/LocaLizard/releases/tag/0.1.1) — previous release
UUP search recognizes Client Language Pack ESD files, converts them into CAB packages with DISM, SXSEXP and Windows makecab, checks ru-RU availability before requesting file metadata, and skips non-installation OOBE/servicing records.

### [0.1.0.8](https://github.com/lizardiot/LocaLizard/releases/tag/0.1.1) — previous release
Language-component discovery now selects Windows 10/11 client builds, excludes Windows Server and Azure Stack HCI, and prefers the exact ISO revision before other builds from the same family.

### [0.1.0.3](https://github.com/lizardiot/LocaLizard/releases/tag/0.1.0.3) — previous release
The UUP Dump check uses `/listid.php` and separately checks all five Windows IoT Enterprise LTSC build families: 10240, 14393, 17763, 19044, and 26100.

### [0.1.0.2](https://github.com/lizardiot/LocaLizard/releases/tag/0.1.0.2) — previous release
The main window is now responsive: when its height is reduced, controls and text remain accessible through scrolling instead of being clipped. Columns and long labels adapt to the available width, action buttons flow into multiple rows, and the minimum window size is smaller.

### [0.1.0.1](https://github.com/lizardiot/LocaLizard/releases/tag/0.1.0.1) — previous release
Added a dedicated drop zone for the Russian donor ISO. Dropping into it fills the “Russian ISO source” field without changing the target ISO path. Both drag-and-drop zones are covered by tests.

### [0.1](https://github.com/lizardiot/LocaLizard/releases/tag/0.1) — previous release
Added draggable upper and lower horizontal sashes for the log panel. Dragging changes the panel height vertically without changing the interface width.

### [0.0.9.9](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.9) — previous release
Latest-LCU search now supports previously unknown build families through fallback Catalog queries with strict filtering. Client Language Pack discovery checks up to five matching stable UUP Dump entries. WinRE can fall back to compatible resources from the official ADK WinPE Add-on, and the JSON audit reports actual results.

### [0.0.9.3](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.3) — previous release
Fixed dragging ISO files from Windows Explorer: the main window starts without administrator privileges, and elevation is requested only when an operation requires it. Added regression checks for arguments passed to the elevated instance.

### [0.0.9.2](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.2) — previous release
Fixed ISO mounting and added diagnostics for a missing Windows Virtual Disk provider.

### [0.0.9.1](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.1) — previous release
Fixed ADK installation after a hash-check error and updated links to Microsoft installers.

### [0.0.9](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9) — previous release
Added update search and preparation through Microsoft Update Catalog. This release contains only the version 0.0.9 changelog; the interactive Hyper-V test asks the user to confirm the Setup screen.

### [0.0.8.9](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.8.9) — previous release
Improved download reliability and cache validation; added update chains through an explicit `updates.json`, Setup/WinPE/WinRE processing when compatible resources are available, SWM support, and processing of an existing WIM. Automatic LCU search through Microsoft Update Catalog was not yet included.

### [0.0.8.1](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.8.1) — previous release
Improved stable UUP source selection and architecture checks, retryable and resumable downloads, cache validation, and localization auditing. Added extra ISO checks and installer-signature verification before ADK installation. WinPE index 1 and WinRE were not modified in this release.

### [0.0.2](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.2) — previous release
Added an additional Russian ISO source for finding the Client Language Pack, FOD, and Setup/WinPE components. The target ISO is not replaced, and DISM is used to check whether the discovered packages are applicable.

### [0.0.1.9](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.1.9) — previous release
Refined discovery of the base Russian Client Language Pack: an exact SKU match in the CAB filename is no longer required, but language and architecture are checked, and DISM determines final compatibility. Improved diagnostics and release publishing while preserving previous tags.

### [0.0.1.8](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.1.8) — previous release
Renamed the project and executable to LocaLizard. Added a Windows GUI for ISO selection, output-structure validation, logging, JSON reports, ru-RU package discovery through the UUP Dump API, and standalone Windows x64 builds.

## Documentation

- [CHANGELOG.md](CHANGELOG.md) — project development history.
- [LocaLizard releases](https://github.com/lizardiot/LocaLizard/releases)
- [Microsoft: Add language packs to Windows](https://learn.microsoft.com/windows-hardware/manufacture/desktop/add-language-packs-to-windows?view=windows-11)
- [Microsoft: Languages and optional features](https://learn.microsoft.com/windows-hardware/manufacture/desktop/languages-overview?view=windows-11)
- [Windows IoT Enterprise overview](https://learn.microsoft.com/en-us/windows/iot/iot-enterprise/overview)
- [UUP Dump](https://uupdump.net/)
