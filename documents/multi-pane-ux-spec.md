# Multi-Pane UX Specification
Product: NovaTerminal
Audience: Desktop power users (developers, DevOps, SRE)
Scope: Cross-platform (Windows, Linux, macOS)

Implementation plan: `documents/MULTI_PANE_EXECUTION_PLAN.md`

---

## 1. Design Principles

1. Keyboard-first
2. Low cognitive load
3. Predictable spatial model
4. Zero-flicker resizing
5. VT correctness isolation per pane
6. Performance scalability (2–6 panes active)

---

## 2. Layout Model

### 2.1 Split Semantics

- Vertical split → Side-by-side panes
- Horizontal split → Stacked panes
- Split inherits:
  - CWD
  - Environment variables
  - Shell profile
- New pane receives focus immediately.

### 2.2 Minimum Constraints

- Minimum width: 20 columns
- Minimum height: 5 rows
- Prevent invalid resize
- Cursor indicates limit reached

### 2.3 Equalization

- Double-click split divider → equalize sibling panes
- Command: `pane.equalize`

---

## 3. Focus Model

### 3.1 Active Pane Indication

Must include:

- 2px accent border (theme-aware)
- Subtle background elevation
- Optional header emphasis

Must not rely solely on:
- Cursor blink
- Color shift without contrast

Contrast ratio ≥ WCAG AA.

---

## 4. Keyboard Interaction

Required commands:

- Split vertical
- Split horizontal
- Close pane
- Move focus (4 directions)
- Swap panes
- Rotate layout
- Zoom toggle
- Broadcast toggle

All pane operations must be keyboard-accessible.

---

## 5. Pane Lifecycle

### 5.1 Close

If process running:
- Confirmation dialog OR
- Configurable silent kill

If process exited:
- Show exit code
- Offer restart

### 5.2 Zoom Mode

- Toggle maximize active pane
- Preserve layout proportions
- Restore exact prior geometry
- Maintain scrollback

---

## 6. Scrollback & Isolation

Each pane maintains:

- Independent scrollback buffer
- Independent search state
- Independent selection
- Independent copy pipeline

Memory must be bounded per pane.

---

## 7. Resize Behavior

- Real-time redraw
- No global repaint
- No flicker
- VT buffer recalculation isolated per pane
- Resize event propagated correctly to PTY

---

## 8. Accessibility

- Keyboard-only operable
- Focus visible in all themes
- Screen reader pane labels
- Configurable accent color

---

## 9. Performance Targets

| Scenario | Target |
|----------|--------|
| 2 htop panes | 60 FPS |
| 4 log streams | No input lag |
| Resize under load | No flicker |
| Idle multi-pane | Minimal CPU |

---

## 10. Advanced Features (Optional but Strategic)

- Layout presets
- Save/restore layout
- Named panes
- Broadcast input mode
- Pane grouping
- Drag-to-reorder

---

## 11. Anti-Patterns

- Thick borders
- Heavy chrome
- Silent process kill
- Global repaint on resize
- Mouse-only control
