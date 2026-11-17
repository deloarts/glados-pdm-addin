---
description: Run a read-only audit of the add-in across the dimensions below
argument-hint: [dimension or scope, e.g. security, hooks, Hooks]
---

Run a READ-ONLY audit of the add-in. Do not modify any source files. Do not commit anything. Use
local tools only; never send source code, diffs or file contents to any external service.

## Scope

Arguments given: `$ARGUMENTS`

- If `$ARGUMENTS` names a dimension (see list below), audit only that dimension across the whole
  repo.
- If `$ARGUMENTS` names a path (e.g. `Hooks`), audit all applicable dimensions but
  restrict findings to that path.
- If `$ARGUMENTS` is empty, run a broad audit across all dimensions.

Dimensions:
- hook safety: every `OnCmd` path and every Glados tab event handler catches all exceptions,
  never blocks the PDM client on a network call, never cancels a transition
- Glados contract: request bodies, headers, status handling and timeouts match the `pdm-addin`
  skill; retries only through `TriggerRetryQueue`; rate-limit queue stops with a log line
- security: the token lives only in the user's Credential Manager (`GladosCredentials`), never
  in logs, the tab after saving, or exceptions; no writes to Glados or to vault data; no
  registry access beyond `HKCU\Software\Glados\PdmAddin`, no file access beyond the log and the
  tab icon
- interop hygiene: `EPDM.Interop.epdm.dll` referenced with Embed Interop Types = False and Copy
  Local = False, COM attributes only on the add-in class, x64, .NET Framework 4.8
- build: every `.cs` file is a `<Compile Include>` item; no build-time secrets; `BuildConfig.cs`
  and `Local.props` stay gitignored
- logging discipline: one line per call, rotation at 5 MB, nothing sensitive
- language: English only, user-visible strings included; LF endings
- dead code and dependencies

## Before starting

Read existing audit output files in `.audit/audit-<date>` first, if any. They track residual
items and conventions already accepted from a prior audit.

## Method

1. Fan out parallel read-only Explore (or general-purpose) subagents, one per dimension in
   scope. Each subagent searches and reads only, no edits.
2. Before reporting any finding, verify it: it must cite a concrete `file:line`, and you must
   re-check that file:line yourself (or have the subagent re-check) before it goes in the
   report. Do not report a finding you have not personally verified.

## Output

Write a report to `.audit/audit-<today's date>/` (create the folder; use `YYYY-MM-DD` for
today). Structure:
- Findings ranked by severity (high/medium/low).
- Each finding: **Findings** (what and why), **Files** (file:line references), **Approach** (how
  it would be fixed), **Risk**, **Effort**.

Then send the user a short summary: dimension(s) covered, count of findings by severity, and the
report path. Do not include the full report inline unless the user asks.

Hard constraints: this is read-only. Do not edit, create, or delete any source file. Do not run
`git add`/`git commit`. The only file you write is the audit report itself.

## Models

Use model `haiku` to investigate the codebase. Minimize token usage. Don't assume anything.
