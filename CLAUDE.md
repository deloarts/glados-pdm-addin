# GLADOS PDM Add-in

## Project overview

SOLIDWORKS PDM Professional vault add-in (.NET Framework 4.8 class library, x64, C# 7.3-era, `IEdmAddIn5`) for Glados, the manufacturing / production-planning system. PDM loads the DLL into every client process that performs a vault operation (File Explorer, the SolidWorks task pane, the Administration tool). The add-in tells Glados immediately when a file changes state, is checked in, deleted, moved or renamed, and shows on a Glados tab in the File Explorer vault view what Glados did with the selected files. Each Windows user signs in with their own Glados personal access token (PAT) from that tab. The backend is `backend/` in the `glados` monorepo (FastAPI; this add-in talks only to `api/v1/pat/pdm/files/changed/batch`, `api/v1/pat/pdm/files/history`, `api/v1/pat/version` and the PAT test route), the SolidWorks task-pane sibling at `../glados-sw-addin` (same toolchain, source of the HTTP and documentation patterns; no code dependency). Machine-specific paths (where MSBuild and the checkouts live, how a shell reaches them) belong in a gitignored `CLAUDE.local.md`, never in this file or other committed files.

**The invariant** (docs/DESIGN.md "What it never does"): the add-in never sends item data and never writes anywhere. Every Glados call is "look at file X" (trigger, with why it changed), "what happened to files X, Y" (the history read for the selection), or the token check and backend version read of the tab and the sign-in window; Glados reads the vault itself through the PDM Web API. The add-in never changes vault data, never stores the token outside the user's Credential Manager, and never blocks or cancels a vault operation. The pre-release validation gate is deferred: PDM transition conditions guard releases (docs/DESIGN.md "Releases").

## Architecture

- `AddIn.cs` — COM entry point (`[ComVisible(true)]`, `[Guid]`, `[ProgId("GladosPdmAddin.AddIn")]`).
  `GetAddInInfo` registers the seven trigger hooks, the five vault view tab hooks and the re-sync
  menu command
  (`AddCmd(MenuHook.ResyncCommandId, "Glados: Resync")`); `OnCmd(ref EdmCmd, ref EdmCmdData[])` switches on
  `meCmdType`, hands each case to a handler in `Hooks/`, and wraps everything in one try/catch
  so nothing escapes into the PDM client. The per-case comments name which `EdmCmdData` member
  carries the path for that hook; they are sourced from the PDM API help, "EdmCmdData
  Structure" (`API_GB.chm` in the PDM install folder), and the `pdm-addin` skill repeats the
  table.
- `Hooks/TriggerHook.cs` — the shared fire-and-forget path for PostState, PostUnlock,
  PostDelete, PostMove, PostRename and the menu command: `mlObjectID1` as file id (0 = folder,
  skipped); PostAdd and PostCopy go through `HandleNewFiles`, which takes the file id from
  `mlObjectID2` or the copy's destination path and sends the call's files only when its first
  file is already checked in (one PDM lookup per call, not per file);
  path from the hook's own `EdmCmdData` field or `FileResolver` fallback, version via
  `IEdmFile5.CurrentVersion`, collected into `TriggerItem`s once per file id (PDM can repeat a
  file within one call), then one `Task.Run` that sends them through `GladosClient.TriggerBatchAsync`, 100 files per call, and logs one line per
  file; gated per file by `RateLimiter.TryAcquire()`, which ends the batch once the window is
  spent. A transient failure hands the batch to `TriggerRetryQueue`, a success clears its files
  from it.
  Without a stored sign-in nothing is collected or sent; one log line per call says how many
  files were left to the poller.
- `TriggerRetryQueue.cs` — static, in memory, keyed by file id: resends transiently failed
  triggers (`GladosCallResult.Transient`: exception, timeout, 429, 502, 503, 504) from one
  background task after 10 s, 30 s, 1 min, 2 min, then every 5 min, through the same batch call
  and `RateLimiter.TryAcquire(count)`; drops a file after an hour or beyond 5000 waiting files,
  leaving it to the poller. Permanent failures are never queued.
- `Hooks/ExplorerTabHook.cs` — `EdmCmd_PreExploreInit` (IEdmCmdMgr6 in `mpoExtra`) adds a
  `GladosTabControl` per vault view window through `AddVaultViewTab` with an id of its own;
  `EdmCmd_ActivateAPITab` re-checks the sign-in, `EdmCmd_SelectItem` / `EdmCmd_DeSelectItem`
  forward the selection, `EdmCmd_UserTabDelete` disposes the control (all carry the id in
  `mbsStrData3`). Locked id map (Explorer runs a thread per window); the 16x16 tab icon is
  drawn once into `%LOCALAPPDATA%\Glados`.
- `GladosTabControl.cs` — WinForms `UserControl` built in code: status line, a button that opens `GladosSignInForm`, a version warning line shown only on an unsupported `pat` contract, and the merged sync history of the selected files (ListView). Tracks the selection keyed by folder and file name (DeSelectItem has no id; a file in another folder starts a new selection), fetches 300 ms after the selection settles and only while visible, drops stale answers by sequence number; every handler catches everything, since it runs inside `explorer.exe`.
- `GladosSignInForm.cs` — modal WinForms window (owner: the Explorer window): server URL, token (never shown again; empty keeps the stored one), Save and test, Sign out, status line. Saves first, then tests off the UI thread and reports via `BeginInvoke`; a successful test adds the version warning to the status line when there is one.
- `GladosCredentials.cs` — per Windows user: PAT in Credential Manager (generic credential
  `Glados.PdmAddin`, user `PAT`, UTF-16LE blob, advapi32 `Cred*` P/Invoke), server URL in
  `HKCU\Software\Glados\PdmAddin` (`ServerUrl`). Read on every call, no cache, so a sign-in in
  Explorer reaches a running SolidWorks process. Never throws, never logs the token.
- `Hooks/MenuHook.cs` — `EdmCmd_Menu` for `ResyncCommandId`; delegates to `TriggerHook` with
  `mbsStrData2` as the path. `Hooks/FileResolver.cs` — best-effort `IEdmVault5.GetObject`
  lookups for version and local path, plus `ToVaultRelativePath` (strips `RootFolderPath`,
  keeps backslashes); every method returns null instead of throwing.
- `GladosClient.cs` — one static `HttpClient`; URL and token from `GladosCredentials` per call, token in the `X-Api-Key` header of each request; TLS 1.2 pinned and redirects not followed (a 3xx is a permanent failure that names the redirect target); per-call `CancellationTokenSource` timeouts (5 s trigger batch, 10 s history, token test and version read); `JavaScriptSerializer` for JSON (System.Web.Extensions, no NuGet). Never throws; returns `GladosCallResult` (trigger: success only on 202, `Transient` for a failure worth retrying, `NotSignedIn` without a request), `FileHistoryResult` (`FileHistoryEntry` rows, or a `Detail` sentence for the tab, never retried), `TokenTestResult` (`Success` with `UserName` and `HasAddinPermission` from the token's permissions, `IsRejected` for 401/403, otherwise unreachable, each with `Detail`) or `BackendVersionResult` (`Known` with `Version` and `PatContract`, otherwise `Detail`). `SupportedPatContract` is the one place the supported `pat` contract minor is kept.
- `BackendVersionCheck.cs` — after a successful token test, the tab and the sign-in window ask `GladosClient.GetBackendVersionAsync` off the UI thread; a `pat_contract` other than `SupportedPatContract` returns a warning sentence naming the server version, the add-in version (`AssemblyInformationalVersion`) and the supported contract, logged once per process. A warning only: triggers keep being sent. A failed version call is never a mismatch: one log line per distinct reason, nothing shown. Never called from a vault hook.
- `RateLimiter.cs` — process-wide sliding window, 300 files per 60 s, mirrors the backend's
  trigger limit, which counts every file of a batch; warns in the log at most once per minute. `Logging.cs` — append-only
  `%LOCALAPPDATA%\Glados\pdm-addin.log`, tab-separated (UTC timestamp, hook, file id, path,
  detail), per Windows user, rotates to `.1` at 5 MB, locked within one process (File
  Explorer's add-in host and SOLIDWORKS write the same file; a clash loses that line), never
  throws, never sees the token.
- No build-time configuration: the same DLL works against any Glados. The URL and token are the
  only runtime settings, entered per user on the tab.

### Glados boundary (the `pdm-addin` skill has the full contract)

- Trigger `POST api/v1/pat/pdm/files/changed/batch` (PAT surface; the twin of
  `api/v1/key/pdm/files/changed/batch`, same body and answer), body `vault_name` (must equal
  the vault configured in Glados) and `files`, 1 to 100 entries of `file_id` with optional `path`
  (vault-relative), `version`, `reason` (`state_change`, `check_in`, `delete`, `move`, `rename`,
  `resync`, `add`, `copy`) and `state` (destination state name, at most 200 characters). 202 `{accepted:
  <distinct files>}`. Path and version are hints; Glados re-reads the truth. Reason and state
  only label the file's history.
- History `POST api/v1/pat/pdm/files/history`, body `vault_name`, `file_ids` (1 to 100) and
  `limit` (50), 200 `{entries: [...]}` merged newest first, each with the file, `action`,
  `message`, `created_at`, the run's `trigger` and `run_error`, and `change_reason`,
  `change_state`, `requested_by_name`. 403 without `use:pdm_addin`. Never retried.
- Token test `POST api/v1/auth/login/test-personal-access-token`, no body, 200 with the user
  (`full_name`, `username`, `permissions` = the token's own subset), 401 with `detail`
  ("... has expired", "... has been revoked").
- Version `GET api/v1/pat/version`, any valid PAT, 200 `{version, pat_contract}` (for example `{"version": "0.1.0", "pat_contract": "0.1"}`); an older backend without the route answers 404, which is logged and never warned about.
- 400 wrong vault, 401/403 token problems, 409 integration disabled, 422 body: logged, never
  retried. A trigger failing with a connection error, timeout, 429, 502, 503 or 504 goes to
  `TriggerRetryQueue`. The PDM add-in token carries only `use:pdm_addin`
  (Glados: Account, Personal Access Tokens, Create PDM Add-in Token).

## Commands

- Build with the full Visual Studio MSBuild (legacy csproj, `dotnet build` does NOT work; no local configuration needed):
  `MSBuild.exe GladosPdmAddin.csproj /p:Configuration=Debug /t:Rebuild /nr:false`
  — `/build` runs it and reads the output for you. `MSB3026`/`MSB3027` "file is locked by ... SOLIDWORKS PDM" means
  the previous DLL is loaded in the PDM client and the copy to `bin\` did not happen: end PDM's
  add-in host and restart Explorer (`taskkill /f /im AddInSrv.exe`, then
  `taskkill /f /im explorer.exe && start explorer.exe`) and build again.
- Debugging: PDM Administration tool, right-click Add-ins, Debug Add-ins, add
  `bin\Debug\GladosPdmAddin.dll` (this machine only, no upload); log out and in to the vault; sign
  in on the Glados tab;
  attach Visual Studio to `AddInSrv.exe` (Managed .NET Framework 4.x): PDM runs the add-ins of
  File Explorer in that host process, not in `explorer.exe` itself. Deployment for real is
  Add-ins, New Add-in, upload the DLL; clients get it on their next vault login; every change
  is a new upload. F5 cannot start a class library.
- Release: `scripts/Start-Release.ps1 X.Y.Z` cuts `release/X.Y.Z`; merging its PR tags `vX.Y.Z`
  and creates a draft GitHub Release (`release.yml`); `scripts/Publish-Release.ps1 X.Y.Z` builds
  the tag locally, attaches `GladosPdmAddin.dll` and `SHA256SUMS.txt` and publishes it. CI
  (`ci.yml`) cannot build (no PDM interop on a hosted runner); it runs gitleaks, checks that the
  csproj compiles every tracked `.cs` file, and checks release branches. `docs/RELEASE.md` has
  the full flow.
- There is no test project. Verification = clean compile + the manual plan in the
  `pdm-addin` skill (sign-in on the tab; state change shows a `file_trigger` run in the Glados
  sync log under Settings, SolidWorks PDM; no sign-in logs a skip; a stopped backend is
  retried). Pure logic is verified by hand-tracing, stated honestly.

## Coding conventions

- C# 7.3-era syntax only (net48 default LangVersion): no records, init-only setters, switch
  expressions, target-typed `new`, or nullable reference types.
- Every new `.cs` file must be added to `GladosPdmAddin.csproj` as an explicit `<Compile>` item;
  the project enumerates files, no globs.
- PDM interop: `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS PDM\EPDM.Interop.epdm.dll` with
  `EmbedInteropTypes` False and `Private` False (every client has it; never upload it). Keep
  "Register for COM interop" off; the vault loads the DLL itself. COM attributes only on
  `AddIn`.
- Hook code never throws, never blocks on a network call, never retries in the hook itself
  (trigger retries run on `TriggerRetryQueue`'s background task), and never reads or writes
  vault variables. Tab UI code runs inside `explorer.exe`: every event handler catches
  everything, network calls run off the UI thread. New `EdmCmdData` field usage must be checked against the PDM API
  help (the `pdm-addin` skill lists the verified members per hook) and cited in a comment.
- JSON stays with `JavaScriptSerializer` and hand-built dictionaries; wire names are the
  backend's snake_case as-is. No NuGet packages.
- Every class carries an XML-doc summary explaining purpose and the non-obvious decision
  behind it, in the same voice as its neighbours; inline comments stay sparse. Plain sentences,
  no em dashes.
- The repo is English only, user-visible strings included (the menu entry, the Glados tab and
  the sign-in window). They live as literals in the hook that shows them; there are no resource
  files and no localization.
- Line endings are LF, enforced by `.gitattributes` and `.editorconfig`. Visual Studio may leave
  CRLF in the working tree; git normalizes on commit, so check `git ls-files --eol` rather than
  the working copy when in doubt.

## Don't

1. Never commit or push unprompted, and never add a "Co-Authored-By: Claude" (or any AI)
   trailer to commit messages.
2. Never write to Glados, never change vault data (variables, states, files), never store the
   token anywhere but the user's Credential Manager, and never log it or show it again.
3. Never cancel or block a vault operation. Releases are guarded by PDM transition conditions;
   bringing back a Glados gate is a decision for the user, not a fix.
4. Never retry a trigger outside `TriggerRetryQueue` or for a permanent failure, and never add
   a hook that is not in the skill's table without flagging it first.
5. Never commit credentials: no URL with a token, no token in code, docs or logs. `Local.props`
   and `BuildConfig.cs` are leftovers of the old build-time key and stay gitignored.
6. Never write German anywhere in this repo: not in code, identifiers, comments, log lines,
   docs, or user-visible strings. English only, unlike the sibling repos.
7. Never reference `.playground/` files or plan/decision points in code comments or committed
   docs; plans are local working artifacts, code must stand alone.
8. Never change existing behaviour to make new code fit without flagging it first.

## Workflow

- `.playground/` (gitignored) holds plans, decision records and handoff notes; pinned
  decisions there are settled, do not relitigate them without a new, explicit reason from the
  user.
- Line-number references go stale fast; the repo is also edited in Visual Studio between
  sessions. Locate code by shape, re-verify before editing.
- The backend moves daily on branch `wip` of the `glados` monorepo. Before changing the Glados
  calls, diff `src/modules/external_data/adapters/inbound/api/schemas/pdm_schema.py` and
  `src/modules/external_data/adapters/inbound/api/v1/pat/endpoints/pdm.py` there against what
  the skill documents.

## Docs

- `docs/RELEASE.md` — versioning (`AssemblyInfo.cs` and `mlAddInVersion`), release flow, CI,
  why the DLL is built locally.
- `README.md` — short overview: features, requirements, install, sign-in, build command.
- `docs/DESIGN.md` — what the add-in never does, sign-in, hooks, version check, releases, log,
  retries, rate limit. Read before touching a hook.
- `glados/docs/PDM_INTEGRATION.md` — the server side: trigger path, validation dry run,
  watched folders, released states per kind, what the vault must provide.
- `glados/docs/FIELD_OWNERSHIP.md` — which datacard variables Glados owns and writes
  back; the reason this add-in never touches variables.
- `glados-sw-addin/docs/DESIGN.md` — the SolidWorks-side sibling; its PAT-based `pdm/validate` call
  is the advisory pre-release check while this add-in's gate is deferred. Its token is separate
  from this add-in's (own Credential Manager target).
