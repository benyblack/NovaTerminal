# winget packaging

Source-of-truth [winget](https://learn.microsoft.com/windows/package-manager/) manifests for
Ntilde, kept in-repo so each release can regenerate and submit them.

The Windows release ships a self-contained, AOT-compiled **zip** *and*, since #91's packaging
half landed, an unsigned Velopack installer (`ntilde-Setup-win-x64-<tag>.exe`). Neither is
code-signed. The winget manifest deliberately packages the **zip** as a **portable** app
(`InstallerType: zip`, `NestedInstallerType: portable`) rather than pointing at the installer:
that keeps winget's install out of Velopack's updater's way, so the two never both believe they
own the install. Once accepted into the community repo it installs with:

```
winget install benyblack.ntilde
```

`benyblack.NovaTerminal` remains in winget-pkgs as an orphan; winget cannot rename identifiers, so
`benyblack.ntilde` is a first-time submission (`submit-first-time.ps1`) on the first Ntilde release.

## Layout

```
packaging/winget/template/
  benyblack.ntilde.yaml               # version manifest   (__VERSION__ placeholder)
  benyblack.ntilde.installer.yaml     # installer          (__VERSION__, __SHA256__)
  benyblack.ntilde.locale.en-US.yaml  # default-locale metadata
packaging/winget/<version>/            # rendered set, created by the recipe below, committed per release
```

`PackageIdentifier` is `benyblack.ntilde`; the portable command alias is `ntilde`.

## Validate a manifest set locally

On a Windows machine with the winget client:

```powershell
winget validate --manifest packaging\winget\<version>
# Optional end-to-end install test in a throwaway context:
winget install --manifest packaging\winget\<version>
```

`winget validate` checks schema and required fields offline. `winget install --manifest` downloads
the real zip, verifies the SHA256, and installs the portable app — the same checks the community
repo's CI runs.

## Cutting a manifest for a new release

The only per-release changes are the version, the installer URL, and the SHA256. After the GitHub
release for `vX.Y.Z` has published its assets:

```powershell
# From the repo root, with the GitHub CLI authenticated.
$ver = "X.Y.Z"
$asset = "ntilde-win-x64-v$ver.zip"
$url = "https://github.com/benyblack/ntilde/releases/download/v$ver/$asset"

# 1. Download the published asset and compute its hash.
gh release download "v$ver" --pattern $asset --dir "$env:TEMP\ntilde-winget" --clobber
$sha = (Get-FileHash "$env:TEMP\ntilde-winget\$asset" -Algorithm SHA256).Hash

# 2. Render the template into a versioned folder.
New-Item -ItemType Directory -Force -Path packaging\winget\$ver | Out-Null
Get-ChildItem packaging\winget\template\*.yaml | ForEach-Object {
    (Get-Content $_ -Raw).Replace('__VERSION__', $ver).Replace('__SHA256__', $sha) |
        Set-Content -NoNewline (Join-Path packaging\winget\$ver $_.Name)
}
# In the locale file: refresh the description ONLY once the release actually contains the
#   described features (the agent-host surface note below still applies).
```

`wingetcreate update benyblack.ntilde --version X.Y.Z --urls $url` automates steps 1–2
(it re-downloads and re-hashes), if you prefer that tool.

## Submitting to the community repo

Fork [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs) and copy the version
folder to `manifests/b/benyblack/ntilde/<version>/`, or run
`wingetcreate submit packaging\winget\<version>`. The repo's automation validates the schema,
downloads the zip, verifies the hash, and installs the portable package in a sandbox. Because the
package is portable and per-user, it needs no elevation and no signature — but SmartScreen may warn
on first launch of the unsigned exe; that resolves when code-signing lands (a separate workstream).

## Notes / caveats

- **Version vs. feature drift.** The rendered manifest for a given release must describe Ntilde
  *as of that release*. The agent-host surface (observe / status / act / replay export, milestones
  A1–A4) is unreleased at the time of writing; do not advertise those capabilities in a manifest
  until the release actually contains them (cut a new `vX.Y.Z` first, then a matching manifest).
- **x64 only.** Releases currently publish `win-x64`. Add an `arm64` installer entry when the
  release workflow starts producing a `win-arm64` bundle.
- **Auto-submission (optional follow-up).** A release-workflow step using `wingetcreate` with a
  PAT can open the winget-pkgs PR automatically on each tag; kept manual for now.
