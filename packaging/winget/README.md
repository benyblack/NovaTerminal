# winget packaging

Source-of-truth [winget](https://learn.microsoft.com/windows/package-manager/) manifests for
NovaTerminal, kept in-repo so each release can regenerate and submit them.

The Windows release ships a self-contained, AOT-compiled **zip** *and*, since #91's packaging
half landed, an unsigned Velopack installer (`NovaTerminal-Setup-win-x64-<tag>.exe`). Neither is
code-signed. The winget manifest deliberately packages the **zip** as a **portable** app
(`InstallerType: zip`, `NestedInstallerType: portable`) rather than pointing at the installer:
that keeps winget's install out of Velopack's updater's way, so the two never both believe they
own the install. Once accepted into the community repo it installs with:

```
winget install benyblack.NovaTerminal
```

## Layout

```
packaging/winget/<version>/
  benyblack.NovaTerminal.yaml               # version manifest
  benyblack.NovaTerminal.installer.yaml     # installer (zip → portable NovaTerminal.exe, alias: nova)
  benyblack.NovaTerminal.locale.en-US.yaml  # default-locale metadata
```

`PackageIdentifier` is `benyblack.NovaTerminal`; the portable command alias is `nova`.

## Validate a manifest set locally

On a Windows machine with the winget client:

```powershell
winget validate --manifest packaging\winget\0.4.0
# Optional end-to-end install test in a throwaway context:
winget install --manifest packaging\winget\0.4.0
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
$asset = "NovaTerminal-win-x64-v$ver.zip"
$url = "https://github.com/benyblack/NovaTerminal/releases/download/v$ver/$asset"

# 1. Download the published asset and compute its hash.
gh release download "v$ver" --pattern $asset --dir "$env:TEMP\nova-winget" --clobber
$sha = (Get-FileHash "$env:TEMP\nova-winget\$asset" -Algorithm SHA256).Hash

# 2. Copy the previous version's files into a new folder and update them.
#    (Create the dir first and copy contents with a wildcard — `Copy-Item -Recurse`
#    onto an existing dir nests the source folder instead of copying its contents.)
New-Item -ItemType Directory -Force -Path packaging\winget\$ver | Out-Null
Copy-Item packaging\winget\0.4.0\* packaging\winget\$ver
# In each file: set PackageVersion: X.Y.Z
# In the installer file: set the new InstallerUrl and InstallerSha256 ($url / $sha)
# In the locale file: update ReleaseNotesUrl to the vX.Y.Z tag and refresh the description
#   ONLY once the release actually contains the described features.
```

`wingetcreate update benyblack.NovaTerminal --version X.Y.Z --urls $url` automates steps 1–2
(it re-downloads and re-hashes), if you prefer that tool.

## Submitting to the community repo

Fork [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs) and copy the version
folder to `manifests/b/benyblack/NovaTerminal/<version>/`, or run
`wingetcreate submit packaging\winget\<version>`. The repo's automation validates the schema,
downloads the zip, verifies the hash, and installs the portable package in a sandbox. Because the
package is portable and per-user, it needs no elevation and no signature — but SmartScreen may warn
on first launch of the unsigned exe; that resolves when code-signing lands (a separate workstream).

## Notes / caveats

- **Version vs. feature drift.** Each `<version>` manifest describes NovaTerminal *as of that
  release*. The `0.4.0` set carries the `0.3.0` description verbatim — it was bumped for version,
  URL and hash only. Do not advertise a capability in a manifest until the release actually
  contains it (cut a new `vX.Y.Z` first, then a matching manifest).
- **x64 only.** Releases currently publish `win-x64`. Add an `arm64` installer entry when the
  release workflow starts producing a `win-arm64` bundle.
- **Auto-submission is wired but dormant.** `release.yml` has a `submit_winget` job that runs
  `wingetcreate update` on each tag push. It self-skips when the `WINGET_PAT` repository secret is
  absent, which is the state today — every release so far has logged
  `WINGET_PAT not set; skipping winget-pkgs submission.` and reported success. A green Release run
  therefore does **not** mean anything reached winget.
- **The package is not in winget-pkgs yet.** `manifests/b/benyblack/NovaTerminal/` does not exist
  upstream, so `winget install benyblack.NovaTerminal` does not work. Because `submit_winget` uses
  `wingetcreate update`, which requires the package to already exist, it would fail even with the
  secret set. Unblock in this order: run `submit-first-time.ps1` to open the bootstrap PR, wait for
  it to merge, then set `WINGET_PAT` so subsequent tags auto-submit.
