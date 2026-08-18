# Enabling Windows code signing

The release pipeline (`.github/workflows/release.yml`) signs the Velopack installer and updater
**only when** the `VPK_SIGN_TEMPLATE` repository secret is set. Until then the installer is
produced unsigned, and Windows shows a SmartScreen warning on first run until the download builds
reputation.

Nothing fails without the secret. The pack step resolves an empty argument list and `vpk pack`
packs unsigned, so releases keep working exactly as they do today.

## To enable

1. Obtain an Authenticode identity — Azure Trusted Signing, or an OV/EV certificate.

2. Add a repository secret named `VPK_SIGN_TEMPLATE`. Its value is a signing command with a
   `{{file}}` placeholder, which `vpk` substitutes once per file it needs to sign:

   - signtool with a PFX:

     ```
     signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /f cert.pfx /p $env:CERT_PW {{file}}
     ```

   - Azure Trusted Signing: use the AzureSignTool invocation from its own documentation, ending
     with `{{file}}`.

   If the command needs its own secrets (a PFX password, an Azure client secret), add those as
   separate repository secrets and surface them to the pack step's `env:` block — the template
   itself is passed through the environment, so it can reference other environment variables.

3. Re-run the release. `vpk pack` signs the app payload, `Setup.exe`, and `Update.exe`.

No workflow edit is required to turn signing on — only the secret. The step is already wired.

## Why the template goes through the environment

The pack step reads `$env:VPK_SIGN_TEMPLATE` rather than interpolating `${{ secrets.… }}` into
the script body. Interpolation happens before PowerShell parses the line, so a template
containing a quote would break parsing, and one containing a semicolon would run as a second
command. Reading it from the environment keeps the value as data.

## Out of scope

macOS notarization and Linux package signing are tracked separately under #91. This document
covers the Windows Authenticode seam only.
