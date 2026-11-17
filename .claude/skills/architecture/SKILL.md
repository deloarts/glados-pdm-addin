---
name: architecture
description: Where code belongs in the Glados PDM vault add-in and which invariants it must respect. Load when adding a hook, a Glados call, a new file, or changing the sign-in or credential storage.
---

# Glados PDM add-in architecture

A .NET Framework 4.8 class library that PDM loads into its client processes. It reacts to vault
events by calling the Glados trigger endpoint with the signed-in user's personal access token,
and adds a Glados tab to File Explorer vault views for that sign-in and for the Glados sync
history of the selected files. Otherwise it does nothing:
no vault writes, no Glados writes, no blocking of vault operations. Place new code in the right
file and respect the invariants in CLAUDE.md.

## Layout

- `AddIn.cs` — COM entry point (`IEdmAddIn5`, GUID `C12CB9A9-…`, ProgId
  `GladosPdmAddin.AddIn`). `GetAddInInfo` = hook registration and the one menu command;
  `OnCmd` = a switch on `EdmCmdType` with one line per case and a catch-all. Keep it thin;
  behaviour lives in `Hooks/`.
- `Hooks/`
  - `TriggerHook.cs` — the only place that turns `EdmCmdData` entries into trigger calls.
    Returns at once with one log line when nobody is signed in. Takes a `pathSelector` per
    hook because the member holding the path differs per `EdmCmdType` (see the `pdm-addin`
    skill table); falls back to `FileResolver`. Applies `RateLimiter` per file, collects
    `TriggerItem`s, then one `Task.Run` + `GladosClient.TriggerBatchAsync` per 100 files + one
    log line per file; a transient failure goes to `TriggerRetryQueue`. Each file id once per
    call. `HandleNewFiles` is the variant for PostAdd and PostCopy: the caller says where the
    file id and folder id are, and the files are sent only when the call's first file is
    already checked in (asked once per call, not per file).
  - `ExplorerTabHook.cs` — `EdmCmd_PreExploreInit` adds one `GladosTabControl` per vault view
    window via `IEdmCmdMgr6.AddVaultViewTab`; `EdmCmd_ActivateAPITab` refreshes it;
    `EdmCmd_SelectItem` / `EdmCmd_DeSelectItem` forward the selection to it;
    `EdmCmd_UserTabDelete` disposes it. Static id map under a lock; draws the tab icon PNG.
  - `MenuHook.cs` — `EdmCmd_Menu` guarded by `mlCmdID == ResyncCommandId`; a thin adapter
    onto `TriggerHook`.
  - `FileResolver.cs` — `IEdmVault5.GetObject` lookups that may fail (deleted files) and
    return null; `ToVaultRelativePath`.
- `GladosTabControl.cs` — the tab's WinForms `UserControl`, built in code: status line, sign-in button, version warning line (hidden unless the `pat` contract is unsupported), history list of the selection. Lives inside `explorer.exe`: every handler catches everything, the token test, the version check and the history call run on the thread pool and come back through `BeginInvoke`, sequence numbers drop stale answers.
- `GladosSignInForm.cs` — the modal sign-in window (URL, token, Save and test, Sign out),
  owned by the Explorer window; same threading rules as the tab.
- `GladosCredentials.cs` — the only place that reads or writes the server URL (HKCU) and the
  token (Credential Manager). Never throws, never logs the token, no cache.
- `GladosClient.cs` — HTTP. One static `HttpClient` with no auth default header; each request gets URL and `X-Api-Key` from `GladosCredentials` (trigger, history) or from its arguments (token test, version read), and its own `CancellationTokenSource` (5 s trigger batch, 10 s history, token test and version read). Never throws. `SupportedPatContract` holds the one supported `pat` contract minor.
- `BackendVersionCheck.cs` — turns the version read into the update warning (or null) and logs a mismatch once per process and a failure once per distinct reason. Called by the tab and the sign-in window after a successful token test, never by a hook, and never blocks anything: a warning only.
- `GladosCallResult.cs` / `TokenTestResult.cs` / `FileHistoryResult.cs` / `BackendVersionResult.cs` — result carriers. `GladosCallResult.Transient` decides what `TriggerRetryQueue` resends; `NotSignedIn` is permanent. `TokenTestResult` separates accepted (with `HasAddinPermission`), rejected (401/403) and unreachable. `FileHistoryResult` carries `FileHistoryEntry` rows or a sentence for the tab; it is never retried. `BackendVersionResult` is known (version and contract) or unknown, and only a known contract can be unsupported.
- `TriggerItem.cs` — one changed file (id, path hint, version hint, change reason, destination
  state), immutable.
- `TriggerRetryQueue.cs` — static, in-memory, keyed by file id; one background task resends
  transiently failed trigger batches with growing delays, drops files after an hour or beyond
  5000, and never blocks a hook.
- `RateLimiter.cs` — sliding window 300 files/60 s, process-wide, used by triggers and their
  retries only.
- `Logging.cs` — `%LOCALAPPDATA%\Glados\pdm-addin.log`, one tab-separated line per call, rotation
  at 5 MB, never throws, never receives the token.
- `Properties/AssemblyInfo.cs`, `GladosPdmAddin.csproj` (explicit `<Compile>` list, interop
  reference with `EmbedInteropTypes`/`Private` False, x64, `System.Drawing` and
  `System.Windows.Forms` for the tab), `GladosPdmAddin.slnx`.

## Flow

Vault event → PDM calls `AddIn.OnCmd` on the client's UI thread → switch → `TriggerHook`
(signed in? per file: rate-limit check, path, version; then one `Task.Run` → `GladosClient.
TriggerBatchAsync` per 100 files → `Logging.Log` per file → `TriggerRetryQueue` on a transient
failure; `OnCmd` returns immediately). Glados then reads the file through the PDM Web API on its
side; the add-in never waits for that.

Explorer opens a vault view → `EdmCmd_PreExploreInit` → `ExplorerTabHook` → `AddVaultViewTab`.
User clicks Sign in on the tab → `GladosSignInForm` → Save → `GladosCredentials` writes HKCU + Credential Manager → token test on the thread pool → status line → on success the version check on the thread pool → warning when the contract differs; on close the tab refreshes and runs both again.
The next trigger in any process of that Windows user reads the new values.

User selects files → `EdmCmd_SelectItem` / `EdmCmd_DeSelectItem` per item → `ExplorerTabHook`
→ the tab's selection set → 300 ms quiet → (visible?) `GladosClient.GetFileHistoryAsync` on the
thread pool → list, or the failure sentence (logged once per distinct failure).

## When adding code

- New hook → verify the `EdmCmdData` members for that `EdmCmdType` in the PDM API help
  (`API_GB.chm`, "EdmCmdData Structure"), add the row to the `pdm-addin` skill, register in
  `GetAddInInfo`, add the `OnCmd` case with a comment naming the member used, reuse
  `TriggerHook.Handle` with the right `pathSelector` for a trigger. Ask first if the hook is not
  in the skill's table.
- New Glados call → new method in `GladosClient` with its own timeout and a result type,
  credentials from `GladosCredentials`; never throw across the hook boundary, never block a
  hook on it.
- New setting → per Windows user next to `ServerUrl` in `GladosCredentials` and a field on the
  tab; never a build-time value, never a vault variable.
- New file → explicit `<Compile>` entry in `GladosPdmAddin.csproj`.
- New user-visible string → English literal in the class that shows it; no resource files,
  no German anywhere.
