# Native SSH Rollout Controls Design

Date: 2026-03-31

## Goal

Add conservative rollout controls for the native SSH backend without hiding failures or silently falling back to OpenSSH.

## Approved Design

- Add a global `ExperimentalNativeSshEnabled` setting in `TerminalSettings`, defaulting to `false`.
- Keep per-profile `BackendKind` editable in the SSH profile editor.
- When native SSH is selected on a profile but the global toggle is disabled, refuse native session creation explicitly instead of falling back to OpenSSH.
- Keep diagnostics and failure bucketing in native SSH core code, not in Avalonia views.
- Provide a visible path for the user to switch the profile back to OpenSSH.

## Rationale

- Preserves saved native profiles without mutating user data.
- Makes rollout state explicit and debuggable.
- Avoids hidden behavior changes between backends.
- Keeps the UI change additive and localized.

## Scope

- Global native SSH experimental toggle
- SSH profile backend selector
- Native session factory refusal path when disabled
- Native failure classifier
- Native metrics surface for timing and disconnect reasons

## Out Of Scope

- Silent fallback from native SSH to OpenSSH
- Broad settings-system refactors
- Changes to terminal rendering or VT behavior
