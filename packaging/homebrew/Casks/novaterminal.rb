# Source-of-truth Homebrew cask for NovaTerminal — see packaging/homebrew/README.md.
#
# __VERSION__ and __SHA256__ are placeholders, never committed real: the release
# workflow (release.yml, "Update Homebrew tap") substitutes them with sed before
# pushing the file into the tap repo, and packaging/homebrew/bootstrap-tap.sh does
# the same for the one-time manual bootstrap. Keep this file's syntax valid Ruby on
# its own; the release lane runs `ruby -c` on the substituted output before pushing.
cask "novaterminal" do
  version "__VERSION__"
  sha256 "__SHA256__"

  # The osx lane currently publishes Apple Silicon only (release.yml matrix:
  # osx-arm64). depends_on below is what makes `brew install` fail with a clear
  # message on an Intel Mac instead of installing a binary that cannot run.
  url "https://github.com/benyblack/NovaTerminal/releases/download/v#{version}/NovaTerminal-osx-arm64-v#{version}.zip"
  name "NovaTerminal"
  desc "Cross-platform terminal emulator focused on correctness and performance"
  homepage "https://github.com/benyblack/NovaTerminal"

  livecheck do
    url :url
    strategy :github_latest
  end

  depends_on arch: :arm64

  # The app carries a Velopack in-app updater that self-updates /Applications
  # installs, so brew must not claim the upgrade path (see the README's caveats).
  auto_updates true

  app "NovaTerminal.app"

  zap trash: [
    "~/.local/share/NovaTerminal",
    "~/Library/Caches/velopack/NovaTerminalApp",
  ]
end
