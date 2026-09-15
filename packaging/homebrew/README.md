# Homebrew packaging

Source-of-truth [Homebrew cask](https://docs.brew.sh/Cask-Cookbook) for NovaTerminal,
mirroring the [winget](../winget) lane: the cask is kept in this repo so each release
can regenerate and publish it. Once the tap exists it installs with:

```
brew install --cask benyblack/tap/novaterminal
```

## Why a personal tap, not the official homebrew-cask repo

Homebrew's official cask repo requires [notability](https://docs.brew.sh/Acceptable-Casks):
for GitHub-hosted software, roughly **75+ stars, 30+ forks, 30+ watchers**. Until the
repo clears that bar, a third-party tap is Homebrew's own recommended path
([discussion](https://github.com/orgs/Homebrew/discussions/3406)). When the bar is met,
promote this cask to `Homebrew/homebrew-cask` as `Casks/n/novaterminal.rb` and announce
that tap users should switch (`brew uninstall --cask benyblack/tap/novaterminal` first,
since the two would fight over `/Applications/NovaTerminal.app`).

## Layout

```
packaging/homebrew/Casks/novaterminal.rb   # cask template; __VERSION__/__SHA256__ placeholders
packaging/homebrew/bootstrap-tap.sh        # one-time bootstrap of the tap repo
```

The template is never committed with a real version. The release workflow (and the
bootstrap script) substitutes the placeholders with `sed` and pushes the result to
`benyblack/homebrew-tap` — the tap repo holds no history of its own worth preserving.

## One-time bootstrap

Prerequisites: a stable release tag with published macOS assets (e.g. `v0.7.0`), and
`gh` authenticated as an identity that can create repos under the target owner.

```bash
packaging/homebrew/bootstrap-tap.sh v0.7.0
# or, for a tap that is not benyblack/homebrew-tap:
packaging/homebrew/bootstrap-tap.sh v0.7.0 someowner/homebrew-something
```

The script refuses prerelease tags (Homebrew forbids prerelease `version`s in a
stable cask), downloads the release's `NovaTerminal-osx-arm64-<tag>.zip`, hashes it,
creates the tap repo, and pushes `Casks/novaterminal.rb`.

## Per-release automation

`release.yml`'s macOS lane ends with an **Update Homebrew tap** step that keeps the
cask current on every stable tag. It is secret-gated exactly like the signing lane:

| Secret | Value |
|---|---|
| `HOMEBREW_TAP_TOKEN` | a token (fine-grained PAT or classic) with **contents: write** on `benyblack/homebrew-tap` |

- Secret set → the step hashes the just-uploaded `NovaTerminal-osx-arm64-<tag>.zip`,
  substitutes the template, checks the output with `ruby -c`, and pushes to the tap.
  A cask already at this version pushes nothing.
- Secret unset → the step prints one line and exits 0; releases behave exactly as
  before. The token lives only in the step's environment, never in the script source,
  same rule as every other run block in that workflow.

## Notes / caveats

- **Apple Silicon only.** The tap packages the `osx-arm64` lane; `depends_on arch:
  :arm64` makes an Intel Mac fail the install with a clear message instead of running
  a binary that cannot start. Add an `on_arm`/`on_intel` block when the release
  workflow starts producing an `osx-x64` bundle.
- **The app self-updates, and the cask says so.** `auto_updates true` tells `brew`
  that Velopack owns the upgrade path, so `brew outdated`/`brew upgrade` will not
  touch the app; updates arrive in-app exactly as for `.pkg` installs. Force a
  brew-side reinstall with `brew upgrade --cask --greedy novaterminal`.
- **Two updaters, one app.** Users should pick a lane: brew-managed (let the in-app
  updater run; brew notices nothing) or fully brew-driven (disable automatic update
  checks in Settings, then run `brew upgrade --cask --greedy novaterminal`).
- **Pre-existing installs.** A `/Applications/NovaTerminal.app` already installed via
  the `.pkg` or the portable zip is *not* the cask's; Homebrew will prompt to move it
  aside on install. User data (`~/.local/share/NovaTerminal`) is untouched either way
  and is only removed by `zap`, never by `uninstall`.
- **Unsigned builds and Gatekeeper.** The cask does not change Gatekeeper: releases
  cut before the `MAC_*` signing secrets are set still need the first-launch
  approval documented in the main README. Homebrew's own download channel does not
  exempt an unsigned app.
