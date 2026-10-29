# LocaLizard 0.0.9.2

**LocaLizard** is a Windows utility for preparing Windows IoT Enterprise LTSC installation media with Russian language support.

## Download

- [Latest LocaLizard release](https://github.com/lizardiot/LocaLizard/releases/latest)
- [All releases](https://github.com/lizardiot/LocaLizard/releases)
- [LocaLizard.exe for Windows x64 — version 0.0.9.2](https://github.com/lizardiot/LocaLizard/releases/download/0.0.9.2/LocaLizard.exe)

## Features

- GUI for selecting the source ISO, output ISO, working directory, language sources, and update catalog.
- Search for a stable cumulative update (LCU) or a specified KB through Microsoft Update Catalog.
- Filtering of Preview/Insider/Canary, Windows Server, and mismatched architectures before download; users review and confirm the selected catalog entry.
- Downloads from trusted Microsoft Update HTTPS domains, verification of the published Catalog SHA-1, and SHA-256 calculation. Each prepared update set includes `updates.json`, `catalog-source.json`, and `SHA256.txt`.
- Search for a compatible Client Language Pack ru-RU in a matching Russian ISO, the managed cache, or through UUP Dump API.
- Russian locale configuration through DISM and installation of available Language Features from a FOD / Language and Optional Features source.
- Optional localization of Windows Setup `boot.wim` index 2, WinPE index 1, and nested WinRE when compatible resources are available.
- WIM/ESD processing, support for input split-WIM `.swm` files, optional splitting of the output WIM into SWM parts for FAT32, and processing an existing WIM into a separate output file.
- Static image validation, logging, and JSON audit report.
- Optional interactive Hyper-V boot check using VMConnect and visual confirmation of the Windows Setup screen.

## Limitations

- Automatic LCU search covers known build families; older or unusual releases may require entering a KB manually.
- Check the target build, architecture, and update-chain contents before integration. If Microsoft Update Catalog returns ambiguous results or changes its page structure, the utility stops instead of guessing which package to use.
- A Russian language already embedded in a donor WIM does not guarantee that a standalone Client Language Pack CAB is available. DISM remains the final compatibility check.
- Availability of FOD, speech, OCR, handwriting, and WinRE localization depends on a compatible Language and Optional Features repository.
- ISO mounting relies on the Windows Virtual Disk provider. If Windows reports that the provider is missing (`0xC03A0014`), LocaLizard shows diagnostic guidance, but the missing Windows component must be repaired at the OS level.
- The Hyper-V test requires Windows, Hyper-V, administrator privileges, and manual confirmation of the Setup screen. It does not run in GitHub CI.
- Test the generated ISO on a separate machine before using it for deployment.

## Run

1. Download `LocaLizard.exe` from the [0.0.9.2 release](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.2).
2. Run the application and approve the Windows administrator prompt.
3. Select the source Windows ISO, output ISO path, and working directory.
4. Optionally select a Russian ISO source, FOD / Language and Optional Features source, and update catalog.
5. Click **Check prerequisites and create ISO**. The optional Hyper-V test also requires Hyper-V to be installed and the user to confirm the Setup screen.

Python and development tools do not need to be installed manually. The source ISO is not modified.

## Version history

### [0.0.9.2](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.2) — current release
Made ISO mounting explicitly use the ISO storage type, separated mounting from drive-letter detection, and added diagnostics for a missing Windows Virtual Disk provider.

### [0.0.9.1](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9.1)
Updated the official Microsoft ADK bootstrapper links and added one redownload/retry after installer hash-check error `0x80091007`.

### [0.0.9](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.9)
Added Microsoft Update Catalog search and preparation of cumulative update chains, plus an interactive Hyper-V check with manual confirmation of the Setup screen.

### [0.0.8.9](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.8.9)
Improved managed-cache validation and explicit update chains via `updates.json`; added Setup/WinPE/WinRE processing when compatible resources are available, SWM support, and processing of an existing WIM.

### [0.0.8.1](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.8.1)
Improved UUP candidate selection by build family and architecture, retries and resumable downloads, managed-cache integrity checks, DISM diagnostics, and ISO prerequisite checks.

### [0.0.2](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.2)
Added an optional Russian ISO source for finding and copying standalone language-pack, FOD, and Setup/WinPE CAB files. The target ISO remains separate and is never replaced with the donor image.

### [0.0.1.9](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.1.9)
Improved Client Language Pack ru-RU discovery by accepting common CAB filename variations without requiring an exact edition-name match, while retaining architecture and Windows build-family checks.

### [0.0.1.8](https://github.com/lizardiot/LocaLizard/releases/tag/0.0.1.8) — first release under the LocaLizard name
Renamed the project to LocaLizard and introduced the Windows GUI, ISO selection, basic structure validation, logging and JSON reports, UUP Dump language-package discovery, and standalone Windows x64 CI builds.

## Documentation

- [CHANGELOG.md](CHANGELOG.md) — complete project change history.
- [LocaLizard releases](https://github.com/lizardiot/LocaLizard/releases)
- [Microsoft: Add language packs to Windows](https://learn.microsoft.com/windows-hardware/manufacture/desktop/add-language-packs-to-windows?view=windows-11)
- [Microsoft: Languages and optional features](https://learn.microsoft.com/windows-hardware/manufacture/desktop/languages-overview?view=windows-11)
- [Windows IoT Enterprise overview](https://learn.microsoft.com/en-us/windows/iot/iot-enterprise/overview)
- [UUP Dump](https://uupdump.net/)
