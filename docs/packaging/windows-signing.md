# Enabling Windows code signing

The release pipeline (`.github/workflows/release.yml`) signs the Velopack installer and updater
**only when** the `VPK_SIGN_TEMPLATE` repository secret is set. Until then the installer is
produced unsigned, and Windows shows a SmartScreen warning on first run until the download builds
reputation.

Nothing fails without the secret. The pack step resolves an empty argument list and `vpk pack`
packs unsigned, so releases keep working exactly as they do today.

## What the seam does and does not cover

The seam means you never have to restructure the workflow: the pack step already resolves signing
arguments and passes them to `vpk pack`. It does **not** mean a certificate materialises on its
own. Every real signing command needs inputs beyond the template string, and those inputs have to
reach the step. Budget for a small workflow edit alongside the secret.

## Azure Trusted Signing (recommended)

Nothing has to be written to disk, so this is the smaller change.

1. Set up a Trusted Signing account and grant the workflow's identity access.
2. Add `VPK_SIGN_TEMPLATE` with an AzureSignTool invocation ending in `{{file}}`.
3. Add the credentials it reads (tenant/client id, client secret or OIDC federation) as
   repository secrets, and surface them on the `Velopack pack` step's `env:` block next to
   `VPK_SIGN_TEMPLATE`. The template can reference them as environment variables.

## signtool with a PFX

A PFX has to exist as a file before `signtool` can use it, so this path needs a step of its own.

1. Store the certificate as a base64-encoded repository secret and its password as a second
   secret.
2. Add a step before `Velopack pack`, guarded to `matrix.rid == 'win-x64'`, that decodes the
   base64 secret to a file outside the publish directory -- anything inside
   `artifacts/publish/win-x64` gets packed into the installer and shipped to users.
3. Add `VPK_SIGN_TEMPLATE` pointing at that path, and expose the password on the pack step's
   `env:`:

   ```
   signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /f $env:CERT_PATH /p $env:CERT_PW {{file}}
   ```

Do not commit a PFX, and do not write one into the packed output.

## Verifying

Re-run the release. `vpk pack` signs the app payload, `Setup.exe` and `Update.exe`; the
"No signing parameters provided, N file(s) will not be signed" warnings in the pack log disappear.
Confirm on the downloaded installer with `signtool verify /pa /v NovaTerminalApp-win-Setup.exe`.

## Why the template goes through the environment

The pack step reads `$env:VPK_SIGN_TEMPLATE` rather than interpolating `${{ secrets.… }}` into
the script body. Interpolation happens before PowerShell parses the line, so a template
containing a quote would break parsing, and one containing a semicolon would run as a second
command. Reading it from the environment keeps the value as data.

## Out of scope

macOS notarization and Linux package signing are tracked separately under #91. This document
covers the Windows Authenticode seam only.
