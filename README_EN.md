# LocaLizard 0.0.9.3

**LocaLizard** is a graphical utility for preparing Windows IoT Enterprise LTSC installation media with Russian as the default language.

## Download

- [Latest LocaLizard release](https://github.com/lizardiot/LocaLizard/releases/latest)
- [All releases](https://github.com/lizardiot/LocaLizard/releases)
- [LocaLizard.exe for Windows x64 — version 0.0.9.3](https://github.com/lizardiot/LocaLizard/releases/download/0.0.9.3/LocaLizard.exe)

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
- Optional interactive Hyper-V ISO boot test using a temporary VM without network access, VMConnect, visual confirmation of the Setup screen, and VM cleanup.

## Limitations

- Automatic LCU search covers known build families; older or unusual versions may require specifying a KB manually.
- Before integration, check the target build, architecture, and contents of the prepared update chain. If Microsoft Update Catalog returns ambiguous data or changes its page structure, the utility must stop automatic preparation rather than guess which package to use.
- Having Russian language resources inside a donor WIM does not guarantee that a standalone Client Language Pack CAB is available. Compatibility of each package is verified by DISM.
- The completeness of FOD, speech, OCR, handwriting, and WinRE localization depends on the availability of a suitable Language and Optional Features repository.
- The Hyper-V test requires Windows, Hyper-V, administrator privileges, and manual confirmation of the Setup screen. It does not run in GitHub CI.
- Test the generated ISO on a separate test machine before deploying it.

## Run

1. Download `LocaLizard.exe` from the [0.0.9.3 release](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.3).
2. Run the application. The main window runs without elevation, so you can drag an ISO from Windows Explorer. Approve the UAC prompt when the application starts an operation that requires administrator privileges.
3. Select the source Windows ISO, output ISO path, and working directory.
4. If needed, specify a Russian ISO source, FOD / Language and Optional Features sources, and an update catalog.
5. Click **Check prerequisites and create ISO**. The Hyper-V test also requires Hyper-V to be installed and the user to visually confirm the Setup screen.

You do not need to install Python or development tools manually. The source ISO remains unchanged.

## Version history

### [0.0.9.3](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.3) — current release
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
