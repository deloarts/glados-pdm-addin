---
description: Run a read-only security audit of the add-in
argument-hint: [scope, e.g. credentials | transport | server-data | pdm-data | local-files | ci | secrets | errors | a path]
---

Run a READ-ONLY security audit of the add-in (the `.cs` sources, `GladosPdmAddin.csproj`,
`.github/workflows/`, `scripts/` as relevant to scope). Do not modify any source file. Do not
commit anything. Use local tools only; never send source code, diffs, or file contents to any
external service.

This audits the whole repo. To review only the pending changes on the current branch, use the
built-in `/security-review` skill instead; it is the lighter tool for that job. `/audit` covers
correctness and conventions; this command covers security only.

Threat model: PDM loads the add-in into every client process that performs a vault operation
(File Explorer's add-in host `AddInSrv.exe`, SOLIDWORKS, the Administration tool), with the
engineer's Windows rights. It holds a Glados personal access token (PAT) per Windows user,
talks to one configured Glados server, and reads file ids, paths and state names from PDM.
Attackers to consider: someone who controls or impersonates the configured server (or sits on
the network path), a vault user who names files or folders to influence what the add-in sends
or logs, another local user on a shared machine, and anyone who can influence the build or
release pipeline. Vulnerabilities in the Glados server itself are out of scope, but note where
the add-in relies on a server-side check it cannot verify. The add-in must never write to
Glados or to vault data and never block a vault operation (`CLAUDE.md`, "Don't"); a path that
breaks either is a finding.

## Scope

Arguments given: `$ARGUMENTS`

- If `$ARGUMENTS` is empty, run a full audit across all dimensions.
- If `$ARGUMENTS` names a dimension slug (see list below), audit only that dimension.
- Otherwise treat `$ARGUMENTS` as a path: audit all applicable dimensions but restrict
  findings to that path.

## Dimensions

- **[credentials] PAT handling**: `GladosCredentials.cs` (advapi32 `Cred*` P/Invoke): correct
  marshalling of the UTF-16LE blob, `CredFree` on every path, persistence type and target name
  (`Glados.PdmAddin`), no copy of the PAT left in long-lived strings beyond what's needed. The
  PAT must never reach the log (`Logging.cs`, all `Logging.Log` call sites), exception messages,
  the tab or the sign-in window after saving (`GladosSignInForm.cs`, `GladosTabControl.cs`),
  window titles, or the clipboard. Check that the token field is masked and that changing the
  server URL cannot send the stored PAT to the new server before the user confirms it (an empty
  token field keeps the stored one).
- **[transport] HTTP and TLS**: `GladosClient.cs`: certificate validation is never disabled
  (`ServerCertificateValidationCallback`, `ServerCertificateCustomValidationCallback`), the TLS
  setting on the handler, timeouts on every call, and the `X-Api-Key` header only ever reaching
  the configured server's origin: check the handler's redirect behaviour (`AllowAutoRedirect`,
  whether a custom header survives a redirect to another host or from https to http) and URLs
  built from user input (`BuildUrl`). A plain `http://` server URL sends the PAT unencrypted:
  report whether the sign-in window warns about it. Retries (`TriggerRetryQueue.cs`) must read
  the URL and token fresh and never resend to a host the user has since replaced.
- **[server-data] Trusting server responses**: `JavaScriptSerializer` use (no
  `SimpleTypeResolver` or any type resolver; dictionaries only), unbounded response sizes a
  malicious server could make the client read into memory, server strings shown on the tab
  (`FileHistoryEntry`, `detail` messages in the sign-in window): length, control characters,
  anything opened or executed. No server-supplied URL may be opened with `Process.Start`.
- **[pdm-data] Vault input**: file and folder names, paths and state names from `EdmCmdData`
  and `FileResolver` are user-controlled. Check that they flow into the JSON body only through
  the serializer, into the log without breaking its tab-separated format (tabs, newlines) or
  forging lines, and never into a file path, registry key or process start. Check the `state`
  length cut and any other bounds the backend enforces.
- **[local-files] File system and registry**: the log at `%LOCALAPPDATA%\Glados\pdm-addin.log`
  is per Windows user: check that nothing writes to a location other users share (a move back
  to `%ProgramData%` makes vault paths readable by every local user and lets another user
  pre-create the file), and rotation to `.1` (`Logging.cs`). The tab icon in the same folder
  (`Hooks/ExplorerTabHook.cs`). Registry access limited to
  `HKCU\Software\Glados\PdmAddin`. No file access beyond these.
- **[ci] Build and release pipeline**: `.github/workflows/*.yml` and `scripts/*.ps1`:
  third-party actions pinned to a commit SHA, minimal `permissions:`, no `pull_request_target`
  with a checkout of untrusted code, secrets not exposed to forked-PR runs, the gitleaks binary
  checksum-verified, `Publish-Release.ps1` building from the tag in a clean worktree and
  refusing to replace a published asset, the release workflow tagging only a merged release PR
  from this repository. The DLL is built on a developer machine, not in CI (`docs/RELEASE.md`
  section 4); Authenticode signing is deferred. Report the current state of both, not as new
  findings.
- **[secrets] Secrets in the repo**: hardcoded tokens, credentials, internal hostnames or IP
  addresses, or personal data in source, docs, workflows and scripts; `.playground/`, `bin/`
  and `obj/` excluded. `Local.props` and `BuildConfig.cs` must stay gitignored. If `gitleaks`
  is installed locally, run `gitleaks git --no-banner --redact .` (history) and
  `gitleaks dir --no-banner --redact .` and quote the summary; if it isn't, say so plainly.
- **[errors] Information leakage to the user and to the log**: exception messages and server
  `detail` strings shown on the tab and in the sign-in window, what the log records per call
  (paths, server URL, user names), and what a log sent to support would reveal.

## Before starting

Read existing reports in `.audit/security-audit-*` (newest first) and `.audit/ACCEPTED.md` if
it exists. They track residual items and conventions or claims already accepted or rejected;
do not re-raise anything listed there as accepted/rejected.

## Method

1. Fan out parallel read-only Explore (or general-purpose) subagents, one per dimension (or a
   small group of related dimensions) in scope. Each subagent searches and reads only, with no
   edits, and locates code by shape (line numbers go stale fast in this repo).
2. Before reporting any finding, verify it: it must cite a concrete `file:line`, and you must
   re-check that file:line yourself before it goes in the report. Do not report a finding you
   have not personally verified. Behaviour of the .NET Framework or PDM that the code relies on
   (redirect handling, file ACLs) is stated as verified only when checked against the
   documentation or tried locally; otherwise it is marked as unverified.
3. Cross-check candidate findings against the earlier reports and `.audit/ACCEPTED.md`, and
   drop duplicates (a residual item still open is reported as "still open", not as new).

## Output

Write a report to `.audit/security-audit-<today's date>/report.md` (create the folder; use
`YYYY-MM-DD` for today). Structure:

- Scope line: dimensions covered, commit (`git rev-parse --short HEAD`), branch, and whether
  gitleaks ran or was unavailable.
- Findings ranked by severity (critical/high/medium/low).
- Each finding: **Finding** (what, and a concrete attack scenario: who can do what they
  shouldn't), **Files** (file:line references), **Approach** (how it would be fixed),
  **Risk**, **Effort**.
- Describe attack scenarios in prose. Do not write working exploit payloads into the report.
- A "Still open from previous audit" section.

Then send the user a short summary: count of findings by severity and the report path. Do not
include the full report inline unless the user asks.

Hard constraints: this is read-only. Do not edit, create, or delete any source file. Do not
run `git add`/`git commit`. Use only local tools; never transmit code to an external service.
The only files you write are the audit report files.

## Models

Use model `haiku` for the investigation subagents; verification and the report stay with the
main session. Minimize token usage. Don't assume anything.
