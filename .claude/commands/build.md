---
description: Build the add-in with MSBuild and report the real outcome
argument-hint: [release] [rebuild]
---

Build the add-in and report honestly. Follow these steps.

## 1. Configuration

None. The URL and token are entered per Windows user on the Glados tab at runtime; the build
has no environment-specific input. A leftover `Local.props` or `BuildConfig.cs` from the old
build-time key is ignored; never print its contents.

## 2. Run MSBuild

From the repo root, with the full Visual Studio MSBuild (`CLAUDE.local.md`, when present, says
where it lives on this machine). Use `Release` instead of `Debug` when `$ARGUMENTS` contains `release`;
use `/t:Build` instead of `/t:Rebuild` unless `$ARGUMENTS` contains `rebuild`.

```
MSBuild.exe GladosPdmAddin.csproj /p:Configuration=Debug /t:Rebuild /nr:false
```

`dotnet build` does not handle this project type; never fall back to it. A property passed on
the command line must escape semicolons as `%3B`.

## 3. Interpret the output

- `GladosPdmAddin -> ...\bin\Debug\GladosPdmAddin.dll` means compile and copy succeeded.
- `MSB3026` retries followed by `MSB3027`/`MSB3021` "file is locked by: ... SOLIDWORKS PDM"
  (usually `AddInSrv.exe`, PDM's add-in host): the compile succeeded into `obj\` but the previous
  DLL is loaded in the PDM client, so `bin\` still holds the OLD build. Tell the user to end the
  host and restart Explorer (`taskkill /f /im AddInSrv.exe`, then
  `taskkill /f /im explorer.exe && start explorer.exe`) and build again. Never report this as
  a successful build of the new code.
- Any `CS....` compiler error or new warning is a real failure: quote it with its
  `file(line,col)`, fix it, and build again until the compile is clean.
- There is no `MSB3216` COM-registration step in this project; if it appears, someone turned
  "Register for COM interop" on, which must be reverted.

## 4. Report

State whether the compile was clean and whether `bin\<Configuration>\GladosPdmAddin.dll` was
actually refreshed (check its timestamp when a lock warning appeared), quoting the relevant
lines. Never summarize a failed or partially copied build as success. There is no test suite;
a clean compile plus the manual plan in the `pdm-addin` skill is this repo's verification
story. Do not
edit source files, do not commit, do not push.
