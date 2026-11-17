# Design

What the add-in does and never does, how sign-in, notifications, retries and the rate limit work, and where it logs. Releases of the add-in itself are covered in [RELEASE.md](RELEASE.md).

## What it never does

- It never writes anything to GLADOS. Every GLADOS call is a fire-and-forget notification ("look at file X", with why it changed), a read of the sync history of the selected files, or the read-only token check and version read of the tab and the sign-in window; item data always flows into GLADOS the other way, through the PDM Web API poller and the change-notification trigger endpoint, both driven from the GLADOS side.
- It never changes vault data: no file content, no variables, no workflow states, no folders.
- It never blocks or cancels a vault operation. Releases are guarded by PDM itself (see
  Releases).
- It never stores the token anywhere but the Windows Credential Manager of the user who
  entered it, never shows it again after saving, and never writes it to the log.
- It never sees a change made by a client that does not run it, or by a user who has not signed
  in. A workstation that missed the rollout, a scripted or API check-in, a Web2 upload or a
  signed-out user produces no notification; GLADOS picks such a change up through its periodic
  poller only, at the next poll interval.

## Sign-in

1. In GLADOS: Account, Personal Access Tokens, Create PDM Add-in Token. The button shows only
   while SolidWorks PDM is switched on in GLADOS. The token carries only `use:pdm_addin`
   (send notifications and read the sync history); creating it needs Write Access Token, and
   your role must hold `use:pdm_addin` itself. Copy the token.
2. In File Explorer, open a vault view, select the GLADOS tab in the bottom panel (next to
   Datacard, Preview, Version) and click Sign in. In the window, enter the GLADOS server URL and
   the token, and click Save and test. The window shows the GLADOS user the token belongs to,
   or why GLADOS refused it; the tab's status line says the same once it is closed. An
   `http://` address (other than localhost) asks for confirmation first, since the token would
   travel unencrypted.
3. Done. The sign-in applies to every program on this computer that runs the add-in for your
   Windows user (File Explorer, SOLIDWORKS, the Administration tool), right away.

Storage: the token in the Windows Credential Manager (generic credential `Glados.PdmAddin`),
the server URL in `HKCU\Software\Glados\PdmAddin`. Both are per Windows user. Sign out removes
the token and keeps the URL.

The token expires as set in GLADOS (the PDM add-in shortcut sets the end of the year). When it
expires, notifications stop and the log says why; the tab shows "GLADOS rejected the token" the
next time it is opened. Create a new token and save it in the sign-in window.

## What it does

| Hook | Behaviour |
|------|-----------|
| File changed state, checked in, deleted, moved or renamed, or arrived already checked in (added by a task or import, or copied) | Fire-and-forget notification to GLADOS so it can refresh that file immediately instead of waiting for the next poll. The files of one operation go out together, up to 100 per call. If GLADOS is unreachable, restarting or busy, the notification is sent again in the background (see Retries). Without a sign-in, nothing is sent and the log says so. |
| A vault view opens in File Explorer | Adds the GLADOS tab: a status line, the sign-in button and the sync history. Selecting the tab re-checks the stored sign-in and the GLADOS version (see Version check). |
| Files selected in the vault view | The tab shows the GLADOS sync history of the selected files, merged into one list, newest first: when, which file, what GLADOS did, why (the state change and its destination state, check-in, move, regular sync and so on), who asked, and GLADOS' message. It only shows what GLADOS has processed and keeps for 30 days; a change still waiting in GLADOS' queue appears once it is processed. Nothing is loaded while the tab is not shown. |
| "GLADOS: Resync" (right-click menu) | Manually re-triggers the notification for the selected files, for support use when a change did not seem to reach GLADOS. |

The trigger endpoint, rate limiting and the exact EdmCmdData fields each hook relies on are
documented in `CLAUDE.md` and the `pdm-addin` skill (`.claude/skills/pdm-addin/SKILL.md`).

## Version check

Each time the tab or the sign-in window confirms a sign-in, the add-in asks GLADOS for its version. This release supports GLADOS 0.1.x (`pat` contract 0.1). When the server serves a different contract, the tab shows a warning below its status line, and the sign-in window below its result, naming the server version, the add-in version and the supported contract and asking for an add-in update; the log notes it once. It is a warning only: notifications are still sent, and nothing in the vault waits for the check. If the version cannot be read (GLADOS unreachable, or a GLADOS without the version route), nothing is shown and the log says why.

## Releases

The add-in does not check files before a release. Guard the release transitions in the PDM
workflow instead (Administration tool, workflow editor, the transition into each released
state):

- Conditions on the datacard variables GLADOS needs, for example the part number variable not
  empty and matching the part number pattern, `Glados.Kind` set, the revision variable set.
- Permissions: only the users or groups who may release.

Rules only GLADOS can judge (a duplicate part number, kind rules, what the sync would do) show up
after the release, in the GLADOS sync log under Settings, SolidWorks PDM, and, when write-back is
enabled in GLADOS, in the `Glados.NewestRevisionSyncState` datacard variable. The SolidWorks
add-in's validate button runs the same checks before a save, as advice.

## Log

`%LOCALAPPDATA%\Glados\pdm-addin.log`, per Windows user, one line per notified file (timestamp, hook, file id, path, HTTP status or exception), plus sign-in and sign-out, a failed history load or version read (once per distinct reason, not per click) and an unsupported GLADOS version (once per program start). Rotates to `pdm-addin.log.1` at 5 MB. Successful runs also show up in GLADOS itself in the sync log under Settings, SolidWorks PDM, with trigger `file_trigger`.

## Retries

A notification that fails because GLADOS is unreachable, times out, answers 502, 503 or 504
(restart, maintenance) or 429 (rate limit) is kept in memory and sent again after 10 s, 30 s,
1 min, 2 min and then every 5 min. A file changed again while it waits is sent once, with its
newest details. After an hour, when the client process exits, or beyond 5000 waiting files, the
add-in gives up on it and the GLADOS poller picks the change up at its next walk of the folder.
A rejected or expired token, a missing sign-in, a disabled integration or a wrong vault name is
never retried, since sending again cannot succeed.

## Rate limit

At most 300 notified files per 60 seconds per client process, mirroring the backend's trigger
limit, which counts every file of a batch; a 429 from GLADOS is logged and retried later (see
Retries). A very large batch operation (thousands of files moved or re-stated at once) stops
early and logs once; the GLADOS poller catches up on whatever was skipped at its next scheduled
walk of the folder (the configured poll interval, longer for a folder that has been quiet for a
while), so nothing is lost.
