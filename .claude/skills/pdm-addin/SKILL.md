---
name: pdm-addin
description: The PDM hook table, the Glados endpoint contracts and the verified EdmCmdData member mapping the Glados PDM add-in relies on. Load before editing AddIn.cs, Hooks/, GladosClient.cs, GladosCredentials.cs or GladosTabControl.cs.
---

# Glados PDM add-in contract

What PDM hands the add-in, what the add-in sends Glados, and what Glados answers. Verified against the PDM 2025 API help (`API_GB.chm`, "EdmCmdData Structure", "EdmCmdType Enumeration" and "Create Vault View Tab Add-in Example (C#)") and the backend (`backend/` in the `glados` monorepo, branch `wip`, 2026-11-13). Pair with the `architecture` skill for where code lives.

## Hook table

`GetAddInInfo` sets `mbsAddInName = "GLADOS PDM Add-in"` and registers every hook below with
`poCmdMgr.AddHook(EdmCmdType.<name>)`; the re-sync menu entry is `AddCmd(1, "Glados: Resync")`.
`OnCmd(ref EdmCmd poCmd, ref EdmCmdData[] ppoData)` gets one `EdmCmdData` per affected file.
For the trigger hooks, `mlObjectID1` is the file id and `mlObjectID2` the folder id, except
PostAdd and PostCopy (below), which go through `TriggerHook.HandleNewFiles`.

| Hook | Members populated (API help) | Behaviour |
|------|------------------------------|-----------|
| `EdmCmd_PostState` | `mbsStrData1` path, `mbsStrData2` destination state name, `mlObjectID3` transition id, `mlObjectID4` acting user id, `mlLongData1` source state id, `mlLongData2` destination state id | Trigger call, reason `state_change`, `state` from `mbsStrData2`. This is the release path. |
| `EdmCmd_PostUnlock` (after check-in) | `mbsStrData1` path | Trigger call, reason `check_in`; lets in-place updates of released files reach Glados without waiting for the poll. |
| `EdmCmd_PostDelete` | `mbsStrData1` path, `mlLongData1` share count | Trigger call, reason `delete`; `GetObject` usually fails afterwards, so version/path fallbacks miss, which is fine. |
| `EdmCmd_PostMove` | `mbsStrData1` source path, `mbsStrData2` destination path, `mlObjectID3` destination folder | Trigger call, reason `move`, with the destination path. |
| `EdmCmd_PostRename` | `mbsStrData1` old file NAME, `mbsStrData2` new file NAME (not paths) | Trigger call, reason `rename`; path via `FileResolver.TryGetLocalPath`. |
| `EdmCmd_PostAdd` | `mlObjectID1` parent folder id, `mlObjectID2` file id, `mbsStrData1` full path, `mlLongData1` 1 for a network sharing link | Trigger call, reason `add`, only when the file is already checked in (`FileResolver.TryIsCheckedIn`: not locked, version 1 or later), as a task's output is; a user's add sits in private state until its check-in. Asked for the first file of the call only, and applied to all its files. |
| `EdmCmd_PostCopy` | `mlObjectID1` destination folder id, `mlObjectID2` "ID of file" (source or copy not stated), `mlObjectID3` source folder id, `mbsStrData1` source path, `mbsStrData2` destination path | Trigger call, reason `copy`, file id resolved from the destination path (`FileResolver.TryGetFileIdFromPath`); same checked-in rule as PostAdd. |
| `EdmCmd_Menu` | `mlObjectID1` file id or 0 for a folder, `mbsStrData1` name, `mbsStrData2` full path | Trigger call, reason `resync`, for the selected files; folders are skipped. |
| `EdmCmd_PreExploreInit` | no `EdmCmdData`; `poCmd.mpoExtra` is an `IEdmCmdMgr6` | `ExplorerTabHook` creates a `GladosTabControl` and calls `IEdmCmdMgr6.AddVaultViewTab(hWnd, name, iconPath, toolTip, uniqueId)` (PDM 2018+). Sent once per vault view window. |
| `EdmCmd_ActivateAPITab` | `mbsStrData3` unique id of the tab | Re-checks the stored sign-in and updates the tab's status line. Sent only to the add-in that owns the tab. |
| `EdmCmd_UserTabDelete` | `mbsStrData3` unique id (from the API help's vault view tab example; the EdmCmdData topic does not list this command) | Removes and disposes the tab's control when Explorer closes. |
| `EdmCmd_SelectItem` | `mlObjectID1` item id, `mbsStrData1` file name (empty for a folder), `mbsStrData2` folder name, `mbsStrData3` tab id. Sent only to an add-in that owns a vault view tab. | The tab adds the file to its selection (keyed by folder and name; a file in another folder starts a new selection) and loads the history 300 ms after the selection settles. |
| `EdmCmd_DeSelectItem` | `mbsStrData1` file name, `mbsStrData2` folder name, `mbsStrData3` tab id. No item id. Sent only to a tab owner. | The tab removes the file from its selection by folder and name. |

Open points to confirm in the debugger: whether PDM sends one `SelectItem` per item of a
multi-selection or one call with several entries (the code handles both), whether a deselect
arrives for every file when the user changes folder (the folder reset covers it), and whether
selection events reach the add-in while another tab of the bottom panel is shown (the tab only
fetches while visible and catches up when shown).

The add-in fires for every file; Glados decides (watched folders, file type rule, kind,
released states per kind). There is no filter in the add-in. `EdmCmd_PreState` is not
registered: the pre-release validation gate is deferred and PDM transition conditions guard
releases. The gate's last version is in commit `655608e` (`Hooks/PreStateHook.cs`,
`ReleaseStateCache.cs`, the validation result types).

## Sign-in

- Per Windows user, entered on the Glados tab: server URL in `HKCU\Software\Glados\PdmAddin`
  (`ServerUrl`), PAT in Credential Manager (generic, target `Glados.PdmAddin`, user `PAT`,
  persist local machine, UTF-16LE blob). Separate from the SolidWorks add-in's
  `GladosSwAddin.Api` credential.
- Read on every trigger call, no cache. No stored URL or token: nothing is sent, one log line
  per hook call ("not signed in to Glados, N file(s) left to the poller").
- The token is created in Glados under Account, Personal Access Tokens, Create PDM Add-in
  Token: permission `use:pdm_addin` only (`PDM_ADDIN_SCOPES` in
  `src/modules/platform/domain/services/token_permission_rules.py`). The creator needs
  `write:pat` and must hold `use:pdm_addin` themselves (an admin grants it; the dialog mints
  the grantable subset and names what is missing); the button shows only while the
  PDM integration is switched on in Glados. Expiry preset to 31 December, or earlier when the
  Security tab caps token lifetimes.

## Glados endpoints

Header on every request: `X-Api-Key: <PAT>` (the PAT dependency reads the same header as the
API-key one). The token's permission subset replaces the user's permissions for the request.

Trigger, fire and forget: `POST {base}/api/v1/pat/pdm/files/changed/batch`

PAT twin of `POST api/v1/key/pdm/files/changed/batch` (`src/modules/external_data/adapters/inbound/api/v1/pat/endpoints/pdm.py`). Same body and answer, `get_current_user_pat`, `RequestPdmFileRefresh`, permission `USE_PDM_ADDIN` (`use:pdm_addin`); the token's own scope decides, so a token without it gets 403 even when its owner holds it.

```json
{"vault_name": "PDM-Vault",
 "files": [{"file_id": 2013, "path": "Engineering\\Glados\\Part1.SLDPRT", "version": 3,
            "reason": "state_change", "state": "Released"},
           {"file_id": 2014}]}
```

- `vault_name` required, compared to the configured vault case-insensitively after trimming,
  400 otherwise. `files` holds 1 to 100 entries, 422 otherwise; each needs `file_id`. `path`
  (vault-relative, backslashes) and `version` are hints Glados re-reads itself, omit them when
  unknown. `reason` (`state_change`, `check_in`, `delete`, `move`, `rename`, `resync`, `add`, `copy`, 422 for
  anything else) and `state` (destination state name, at most 200 characters, 422 above; the
  add-in cuts it) only label the file's history: Glados stores them, with the token owner's
  full name, on the single-file refresh run.
- 202 `{"accepted": <distinct files>}`. One `PdmFileChangeRequested` per distinct file id
  (the last entry for an id wins); the `pdm_sync` worker runs `RefreshPdmFile` →
  `IngestPdmFile`. Duplicates and out-of-order calls are harmless (version guard); files in
  documents folders or private states are skipped; the poll scheduler converges later anyway.
- 429 when the batch does not fit 300 files per 60 s per user and backend worker; the whole
  batch is rejected. The budget belongs to the token's owner, so every token of one user
  shares it, while an API key counts against its own service account.
- Client rule: 5 s per batch, catch everything, log one line per file. A connection error,
  timeout, 429, 502, 503 or 504 hands the batch to `TriggerRetryQueue` (10 s, 30 s, 1 min,
  2 min, then every 5 min, dropped after an hour); anything else is final. Redirects are not
  followed, so the token never reaches another host; a 3xx is final and logged with its target.

History, read-only: `POST {base}/api/v1/pat/pdm/files/history` (same backend file, `ListPdmFileHistory`, permission `USE_PDM_ADDIN`).

```json
{"vault_name": "PDM-Vault", "file_ids": [2013, 2014], "limit": 50}
```

- `file_ids` 1 to 100, `limit` 1 to 200 (default 50), 422 otherwise; vault name checked like the trigger's (400). 200 `{"entries": [...]}`, the files' sync log entries merged newest first: `file_id`, `name`, `path`, `kind`, `action` (created, revised, updated, status, claimed, skipped, conflict, missing, error, plus document and write-back actions), `message`, `created_at`, `run_id`, `trigger` (file_trigger, retry, scheduled, manual, initial, reconciliation), `run_started_at`, `run_finished_at`, `run_error`, `change_reason`, `change_state`, `requested_by_name` (the last three null unless a trigger carried them). No item id.
- Only what the `pdm_sync` worker processed: a queued or rejected trigger has no row yet. Runs
  are kept 30 days. Empty list while Glados has not resolved the vault id.
- 403 without `use:pdm_addin`, 409 integration off.
- Client rule: 10 s, never retried, never throws; off the UI thread; one log line per distinct
  failure, none per success.

Token test: `POST {base}/api/v1/auth/login/test-personal-access-token`, no body
(`src/modules/platform/adapters/inbound/api/v1/auth/endpoints/pat_login.py`).

- 200 `UserResponse`: the add-in reads `full_name` (fallback `username`) and `permissions`
  (the token's own subset; `use:pdm_addin` missing means both triggers and the history
  will get 403).
- 401 `{"detail": ...}`: "Invalid personal access token", "Personal access token has been
  revoked", "Personal access token has expired", "Personal access tokens are not valid for the
  system account". Shown verbatim on the tab.
- Client rule: 10 s, never retried, never throws; run off the UI thread.

Backend version: `GET {base}/api/v1/pat/version`, no body, any valid PAT, no permission (`src/modules/platform/adapters/inbound/api/v1/pat/endpoints/version.py`).

- 200 `{"version": "0.1.0", "pat_contract": "0.1"}`; `pat_contract` is the `major.minor` of the backend version (`PAT_CONTRACT` in `src/modules/platform/const.py`). Every `pat` response also carries the version in the `X-Glados-Version` header; the add-in does not read it.
- Called only after a successful token test, by the tab and the sign-in window (`BackendVersionCheck`), never from a vault hook. A `pat_contract` other than `GladosClient.SupportedPatContract` shows an update warning on the tab and in the window and is logged once per process; notifications keep being sent. Any failure (unreachable, 404 from an older backend, an unexpected body) shows nothing and is logged once per distinct reason.
- Client rule: 10 s, never retried, never throws; run off the UI thread.

Status handling for the trigger:

| Status | Meaning | Add-in reaction |
|--------|---------|-----------------|
| 400 | wrong vault name | log |
| 401 | missing, wrong, revoked or expired token | log |
| 403 | token lacks `use:pdm_addin` | log |
| 409 | integration disabled or not configured (`PdmNotEnabledError`) | log |
| 422 | body rejected | log |
| 429 | rate limit | log, retried later |
| 502, 504 | proxy cannot reach the backend (restart) | log, retried later |
| 503 | maintenance mode | log, retried later |

## Rate limiting

Batch operations produce one call per 100 files, but the budget counts files on both sides. A
bulk state change of 5 000 files does not fit. `RateLimiter` keeps a 300-files-per-60 s window
in the process, `TriggerHook` stops the batch when it is exhausted, and one log line per minute
says so; the Glados poller catches up. `TriggerRetryQueue` takes its slots from the same
window, all or nothing per resend.

## Logging

`%LOCALAPPDATA%\Glados\pdm-addin.log`, append, tab-separated: UTC timestamp, hook, file id,
path, detail (HTTP status or exception type and message; sign-in, sign-out and server URL from
the tab). Rotate at 5 MB to `.1`. Folder created on first write. Never the token, never request
bodies.

## PDM client facts

- Interop `EPDM.Interop.epdm.dll` (PDM 2025, file version 33.3) from `C:\Program
  Files\SOLIDWORKS Corp\SOLIDWORKS PDM\`, `EmbedInteropTypes` False, `Private` False; every
  client has it in the GAC, never upload it. "Register for COM interop" stays off.
- Hooks run on the client's UI thread. Trigger calls return immediately (`Task.Run`). The
  vault view tab exists only in File Explorer, not in the SolidWorks task pane or the
  Administration tool; explorer.exe runs each window on its own thread.
- `poCmd.mpoVault` is the `IEdmVault5`; `GetObject(EdmObject_File, id)` gives `IEdmFile5`
  for `CurrentVersion` and `GetLocalPath(folderId)`; `RootFolderPath` turns local paths into
  vault-relative ones.
- Debug Add-ins in the Administration tool loads a local DLL on this machine only; real
  deployment is New Add-in, upload, clients re-login. File Explorer's add-ins run in PDM's
  host process `AddInSrv.exe`, which keeps the DLL locked until it ends; attach the debugger
  there.

## Manual test plan

1. Build, register via Debug Add-ins (or upload to the test vault), log in to the vault.
2. Open a vault view in File Explorer: a Glados tab appears in the bottom panel. Without a
   sign-in it says "Not signed in". Close and reopen several Explorer windows: no crash.
3. Click Sign in, save a PDM add-in token in the window: "Signed in to Glados as <name>", and
   the tab's status line says the same after closing. A SolidWorks add-in token: the missing
   `use:pdm_addin` warning. A revoked or expired token: "Glados rejected the token" with the reason. A wrong
   URL: "could not be reached". Sign out: back to "Not signed in".
3b. Against a backend whose `pat_contract` is not `GladosClient.SupportedPatContract` (or with the constant changed in a Debug build): after "Signed in", the tab shows the update warning below its status line naming both versions and the supported contract, the sign-in window adds it to its status, and the log has one "Glados ... serves pat contract" line however often the tab is selected. A state change is still sent. Against a backend without the version route: no warning, one "Glados version not checked" line.
3a. Signed in, select one file Glados has synced: its rows appear (when, what, why, by). Select
   several (Ctrl+click, Shift+click): one merged list. Deselect: the list follows. Select a
   folder only: the "select files" note. A file never synced: the "no sync history yet" note.
   Change a file's state, wait for the worker, reselect: a row "State changed to <state>" by
   you. Stop the backend and select a file: "could not be reached", Explorer stays responsive,
   one log line. Switch to the Datacard tab, change the selection, switch back: the history
   follows.
4. Without a sign-in, change the state of a file: the log shows "not signed in to Glados, 1
   file(s) left to the poller".
5. Signed in, change the state of a file in a watched folder: within seconds a run with trigger
   `file_trigger` appears in the Glados sync log (Settings, SolidWorks PDM) and the log shows
   `ok HTTP 202`.
6. Sign in in Explorer while SOLIDWORKS is open, then check in from SOLIDWORKS: the trigger goes
   out without restarting SOLIDWORKS.
7. Stop the Glados backend: the log shows the failure and "queued for retry". Start it again:
   within a few minutes "resent ... ok HTTP 202".
8. A release transition with a PDM condition on the part number variable refuses a file with
   an empty part number (PDM's own message, no add-in involved).
