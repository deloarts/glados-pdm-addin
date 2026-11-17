# changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

First release. The GLADOS PDM Add-in is a SOLIDWORKS PDM Professional vault add-in that tells GLADOS right away when a vault file changes, so item data stays current without waiting for the next poll, and shows on a GLADOS tab in the File Explorer vault view what GLADOS did with the selected files. It never writes to GLADOS or to the vault and never blocks a vault operation.

Supports backend 0.1.x (`pat` contract 0.1).

### Requirements

- SOLIDWORKS PDM Professional 2025 on every client. The add-in is built against its interop (`EPDM.Interop.epdm` 5.28), which every PDM client already has; earlier PDM versions are untested.
- .NET Framework 4.8 (64-bit clients).
- GLADOS 0.1.x with SolidWorks PDM switched on. Each Windows user signs in with their own personal access token carrying the permission `use:pdm_addin`, created in GLADOS under Account, Personal Access Tokens, Create PDM Add-in Token; their role must hold `use:pdm_addin`.
- Installed once per vault by an administrator: in the PDM Administration tool, Add-ins, New Add-in, upload `GladosPdmAddin.dll` from this release after checking it against `SHA256SUMS.txt`. Clients get it at their next vault login; nothing is installed on each client.

### Added

- Notifies GLADOS when a vault file changes state, is checked in, deleted, moved or renamed, when a file arrives in the vault already checked in (added by a task or import, or copied), and on the "GLADOS: Resync" menu command. Up to 100 files per call and at most 300 files per minute per client, with the reason and the destination state as labels in the file's GLADOS history.
- Sends a notification again when GLADOS was unreachable or busy, in memory, for up to an hour; anything left over is picked up by the GLADOS poller.
- A GLADOS tab in the File Explorer vault view with the sign-in status, the sign-in window and the GLADOS sync history of the selected files.
- A warning on the GLADOS tab and in the sign-in window when the GLADOS server serves a `pat` contract other than 0.1, naming both versions and asking for an add-in update. Notifications are still sent.
- Sign-in per Windows user with a personal access token. The token is kept in the Windows Credential Manager, the server URL in `HKCU\Software\Glados\PdmAddin`.
- A log per Windows user in `%LOCALAPPDATA%\Glados\pdm-addin.log`.
