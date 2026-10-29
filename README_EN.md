# LocaLizard 0.1.0.1

**LocaLizard** is a graphical utility for preparing Windows IoT Enterprise LTSC installation media with Russian as the default language.

## Download

- [Latest LocaLizard release](https://github.com/lizardiot/LocaLizard/releases/latest)
- [All releases](https://github.com/lizardiot/LocaLizard/releases)
- [LocaLizard.exe for Windows x64 — version 0.1.0.1](https://github.com/lizardiot/LocaLizard/releases/download/0.1.0.1/LocaLizard.exe)

## Features

- GUI for selecting the source ISO, output ISO, working directory, language sources, and update catalog.
- Search for stable cumulative updates (LCUs) or a specified KB through Microsoft Update Catalog.
- Filters Preview/Insider/Canary, Windows Server, and architecture mismatches before download; the user reviews and confirms the selected entry.
- Downloads from trusted Microsoft Update HTTPS domains, verifies the available Catalog SHA-1, and calculates SHA-256. Each prepared update set includes `updates.json`, `catalog-source.json`, and `SHA256.txt`.
- Finds a compatible Client Language Pack ru-RU in a matching Russian ISO, the managed cache, or through the UUP Dump API.
- Configures the Russian locale through DISM and installs available Language Features from FOD / Language and Optional Features sources.
- When compatible resources are available, processes Windows Setup `boot.wim` index 2, WinPE index 1, and nested WinRE.
- Supports WIM/ESD and input split-WIM `.swm` files; optionally splits the output WIM into SWM parts for FAT32 and processes an existing WIM into a new file.
- Static result validation, logging, and a JSON audit report.
- Resize the log panel vertically by dragging its upper or lower horizontal sash; the interface width is unaffected.
- Two independent drag-and-drop zones accept the target Windows ISO and an optional Russian donor ISO, filling their corresponding fields.
- Optional interactive Hyper-V ISO boot test using a temporary VM without network access, VMConnect, visual confirmation of the Setup screen, and VM cleanup.

## Limitations and environment requirements

- Latest-LCU search uses the build family and architecture detected from the selected ISO. If a build has no predefined query profile, the utility searches by numeric OS build and tries fallback Microsoft Update Catalog queries. Automatic download still requires an unambiguous stable update matching the exact build family and architecture. If the Catalog HTML changes or no matching entry is returned, the utility stops rather than guessing; a KB can be entered manually.
- Client Language Pack discovery checks the local cache, a donor ISO, and up to five recent stable UUP Dump entries for the same build family and architecture. Localization of an image index cannot continue if no compatible standalone CAB is available from trusted sources. DISM performs the final applicability check.
- Extra language capabilities (Basic, Fonts, OCR, Speech, Text-to-Speech, Handwriting) are attempted individually and recorded in the JSON audit. A capability can remain unavailable when compatible files are missing from the selected FOD/Language and Optional Features source and cannot be obtained through the servicing sources used by DISM.
- Setup/WinPE and WinRE use compatible Russian resources from the selected source and/or the official ADK WinPE Add-on. If the add-on cannot be downloaded or signature-verified, disk space is insufficient, or DISM rejects the CABs, that part of the image is left unchanged; details are written to the log and audit.
- The interactive Hyper-V test requires Windows with Hyper-V enabled, administrator privileges, and VMConnect. It launches a temporary VM without networking but asks the user to visually confirm the Windows Setup screen: a running VM alone does not prove that Setup appeared. This interactive test is not run in GitHub Actions.
- Static validation checks the ISO structure and whether images can be read, but cannot guarantee installation on every hardware configuration. Test the ISO in a separate VM or on a test device before deployment. OOBE can affect the preferred UI language of the first user.
## Run

1. Download `LocaLizard.exe` from the [0.1.0.1 release](https://github.com/lizardiot/LocaLizard/releases/tag/0.1.0.1).
2. Run the application. The main window runs without elevation: drag the target ISO into the first drop zone and the Russian donor ISO into the separate zone in the language-components section. Approve the UAC prompt when the application starts an operation that requires administrator privileges.
3. Select the source Windows ISO, output ISO path, and working directory.
4. If needed, specify a Russian ISO source, FOD / Language and Optional Features sources, and an update catalog.
5. Click **Check prerequisites and create ISO**. The Hyper-V test also requires Hyper-V to be installed and the user to visually confirm the Setup screen.

You do not need to install Python or development tools manually. The source ISO remains unchanged.

## Version history

### [0.1.0.1](https://github.com/lizardiot/LocaLizard/releases/tag/0.1.0.1) — current release
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
