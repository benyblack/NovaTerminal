# NovaTerminal

**NovaTerminal** is a modern, cross-platform terminal emulator focused on  
**correctness, performance, and predictability**.

Built with:

- **.NET 10**
- **Avalonia UI**
- **Skia (GPU-accelerated rendering)**
- **Rust-based PTY backend**

Supported platforms: **Windows · Linux · macOS**

---

## Why NovaTerminal?

Terminal emulators tend to fail quietly:
a small bug in resize, wrapping, or alternate screen handling can break
`vim`, `htop`, `tmux`, or SSH workflows.

NovaTerminal is built around one core principle:

> **Terminal correctness is enforced by automated tests, not guesswork.**

---

## What Makes NovaTerminal Different

### ✔ High-Performance Skia Engine
- **Lock-Free Snapshot Pipeline**: Decouples terminal state from the UI thread for jitter-free performance during high-frequency output.
- **GPU-Accelerated Glyph Atlas**: Pre-caches shaped glyphs into a high-speed GPU texture for pixel-perfect text at high FPS.
- **Micro-Batching**: Optimizes redraws by grouping similar rendering operations (backgrounds, text, overlays).

### ✔ Correctness & Stability
- Deterministic VT / ANSI parsing
- Lossless resize & reflow
- Strict alternate screen isolation
- **Resource Hardening**: Defensive buffer management ensures stability even under extreme stress (e.g., rapid resizing).

---

### ✔ Truly Cross-Platform
NovaTerminal guarantees **identical terminal behavior** across operating systems:

- VT interpretation
- buffer state
- wrapping & reflow
- search semantics

Platform-specific differences are limited to:
- window chrome
- blur/transparency
- global hotkeys
- credential storage backends

---

### ✔ Test-Gated Development
NovaTerminal treats automated testing as a first-class feature:

- deterministic replay of real terminal sessions
- cross-platform parity checks
- renderer performance & flicker guards

If a change cannot be tested, it does not ship.

---

## Architecture Overview

```
┌───────────────────────────────┐
│ UI Shell (Avalonia)           │
├───────────────────────────────┤
│ Renderer (Skia, GPU)          │
│ - Cell-grid based             │
│ - Incremental redraw          │
├───────────────────────────────┤
│ Terminal Core (Cross-Platform)│
│ - VT / ANSI parser            │
│ - Screen & scrollback         │
│ - Reflow & wrapping           │
├───────────────────────────────┤
│ PTY Backend (Rust)            │
└───────────────────────────────┘
```

---

## Current Features

### Terminal
- VT / ANSI parsing
- Alternate screen support (`vim`, `less`, `htop`)
- Scrollback buffer
- Stable resize & reflow
- Cell-based buffer model
- Thread-safe, crash-resistant PTY backend

### Graphics & Protocol Support
- **Sixel Graphics** (Verified with `libsixel`, `lsix`, `gnuplot`)
- **iTerm2 Inline Images** (Verified with `imgcat`, `test_iterm2.py`)
- **Kitty Graphics Protocol** (native on Linux/macOS; tunneled mode on Windows)
- **Proper ConPTY Synchronization** (Images render inline with prompts)

See `documents/IMAGE_PROTOCOL_SUPPORT.md` for platform-specific protocol behavior and fallback rules.

### UI
- Tabs
- Split panes
- Command palette
- Search overlay
- Profiles (local & SSH)
- Themes and fonts
- Live settings (no restart)

### Remote
- SSH profiles
- Cross-platform PTY abstraction
- Secure credential handling (in progress)

---

## Project Status

NovaTerminal is under **active development**.

Current focus:
- hardening terminal correctness
- eliminating flicker and resize instability
- expanding automated replay coverage

Advanced features are intentionally secondary until correctness goals are met.

---

## Contributing

Contributions are welcome.

NovaTerminal has a strong correctness culture:
- terminal core invariants are enforced
- automated tests gate changes

See `CONTRIBUTING.md` for details.

---

## Philosophy

NovaTerminal aims to be:

- **boring in behavior**
- **predictable under stress**
- **fast without shortcuts**
- **cross-platform without divergence**

A terminal you can trust.
