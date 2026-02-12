# Nova Terminal: Master QA Index

This document is the central hub for all testing activities. It links to specialized documentation for each major component of Nova Terminal.

## 1. Component Testing
- [Core VT/ANSI Correctness](file:///d:/projects/nova2/documents/qa/QA_CORE.md)
- [Buffer Integrity & Reflow](file:///d:/projects/nova2/documents/qa/QA_BUFFER_REFLOW.md)
- [Rendering Fidelity & Performance](file:///d:/projects/nova2/documents/qa/QA_RENDERING.md)
- [UI, Theming & Interaction](file:///d:/projects/nova2/documents/qa/QA_UI_INTERACTION.md)
- [Platform Parity & Environment](file:///d:/projects/nova2/documents/qa/QA_PLATFORM_PARITY.md)
- [Graphics Protocols (Kitty, iTerm2, Sixel)](file:///d:/projects/nova2/documents/qa/QA_GRAPHICS.md)

---

## 2. Regression & Stress Testing
### 2.1 Critical Regressions
- **Midnight Commander (MC)**: Verify no compacted view or screen wipes on resize.
- **Oh-My-Posh**: Ensure right-aligned segments reposition correctly without ghosting.
- **Resize Hangs**: Specifically check MC in WSL for any deadlocks during rapid resize.

### 2.2 Stress Tests
- **24h Persistence**: Run `htop` or `tmux` for extended periods.
- **Massive Log Flood**: Cat large log files to verify backpressure handling in `RustPtySession`.

---

## 3. Exit Criteria
Nova Terminal is considered "Production Grade" when:
- 0 Flicker during standard operations.
- 0 Buffer corruption in all tested TUI applications.
- Behavioral identity across Windows and Linux versions.
