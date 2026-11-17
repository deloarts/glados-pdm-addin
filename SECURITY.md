# Security policy

## Reporting a vulnerability

Please **do not** open a public issue for security problems. Report them privately through
GitHub: **Security → Report a vulnerability** on this repository. You will get an
acknowledgement within a few working days, and a fix or mitigation plan once the report is
confirmed.

Useful details: the add-in version (the product version on the Details tab of
`GladosPdmAddin.dll`'s properties, or the version the PDM Administration tool lists under
Add-ins), the SOLIDWORKS PDM version, and steps to reproduce. Never include a real personal
access token; revoke any token that may have been exposed.

## Supported versions

Only the latest release receives security fixes.

## Scope notes

- The add-in stores the GLADOS personal access token in the Windows Credential Manager (per
  user) and sends it only to the configured server. Redirects are not followed, so the token
  never reaches another host. A non-HTTPS server URL asks for confirmation, because the token
  would then travel unencrypted.
- The log (`%LOCALAPPDATA%\Glados\pdm-addin.log`, per Windows user) never contains the token.
- The add-in never writes to GLADOS or to vault data and never blocks a vault operation; a way
  to make it do either is in scope.
- Vulnerabilities in the GLADOS server itself belong to that project, not this one.
