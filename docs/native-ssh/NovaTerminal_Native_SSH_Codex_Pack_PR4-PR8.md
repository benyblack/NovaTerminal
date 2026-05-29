# NovaTerminal Native SSH – Codex Execution Pack (PR4–PR8)

This pack expands the native SSH execution plan for NovaTerminal into ready-to-use prompts for Codex / Antigravity.

These prompts assume PR1–PR3 already exist:
- backend split foundation
- Rust native SSH spike
- C# native session wrapper

---

## Global rules

Apply these rules to every PR in this pack.

- Do not remove or regress the current OpenSSH backend.
- Do not force native SSH through `RustPtySession`.
- Keep `OpenSshConfigCompiler`, `SshLaunchPlanner`, and `SshArgBuilder` scoped to the OpenSSH backend.
- Prefer narrow, poll/event-style interop over callback-heavy FFI.
- Preserve terminal correctness first; do not optimize prematurely.
- Keep UI coordination for auth and host key prompts outside terminal text parsing.
- Add tests where practical, especially for persistence and state handling.
- Avoid plaintext secret storage.

---

# PR4 — SSH Interaction UX Service

**Branch:** `feat/ssh-native-auth-hostkey-ux`

## Goal
Handle native SSH host key and authentication flows through explicit UI/services rather than scraping terminal text.

## Prompt
Add a UI-facing SSH interaction service for native SSH authentication and host key verification. Implement dialogs and view models for trust-on-first-use host key prompts, changed host key warnings, password entry, passphrase entry, and keyboard-interactive prompts. Wire `NativeSshSession` to request responses through the interaction service. Do not parse terminal output for these flows. Keep the service general enough to support both modal dialogs now and richer inline UX later.

## Scope
Implement support for:
- unknown host key
- changed host key
- password auth
- key passphrase prompt
- keyboard-interactive prompts

### Suggested files to add
- `src/NovaTerminal.App/Services/Ssh/ISshInteractionService.cs`
- `src/NovaTerminal.App/Services/Ssh/SshInteractionService.cs`
- `src/NovaTerminal.App/Models/Ssh/SshInteractionRequest.cs`
- `src/NovaTerminal.App/Models/Ssh/SshInteractionResponse.cs`
- `src/NovaTerminal.App/ViewModels/Ssh/HostKeyPromptViewModel.cs`
- `src/NovaTerminal.App/ViewModels/Ssh/AuthPromptViewModel.cs`
- matching views/dialogs

### Suggested files to modify
- `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`
- `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- dialog host / main window wiring as needed

## Acceptance criteria
- Native SSH can complete first-time trust flow.
- Password auth succeeds through UI prompt.
- Keyboard-interactive auth succeeds.
- Changed host key shows a clear warning path.
- No terminal text parsing is used for native SSH auth/trust flows.
- Cancellation is handled cleanly and closes/fails the session predictably.

## Notes
- Keep the interaction service backend-agnostic if reasonable.
- Surface enough metadata to the UI:
  - hostname
  - port
  - key algorithm
  - fingerprint
  - prompt text
  - whether retry is allowed

---

# PR5 — Known Hosts + Backend-Safe Persistence

**Branch:** `feat/ssh-native-known-hosts-persistence`

## Goal
Persist trust decisions for native SSH and ensure profile/backend restore behavior is safe and stable.

## Prompt
Add native SSH known-hosts persistence and backend-aware profile persistence and restore. Implement an app-managed known-hosts store for the native backend, fingerprint formatting, mismatch detection, and trust decisions. Update profile serialization and session restoration so backend selection is preserved safely. Keep current OpenSSH behavior unchanged.

## Scope
Implement:
- app-managed known hosts file or structured store for native backend
- fingerprint formatting helpers
- host key lookup and mismatch detection
- persist backend selection in profile store
- preserve backend on session restore

### Suggested files to add
- `src/NovaTerminal.Core/Ssh/Native/NativeKnownHostsStore.cs`
- `src/NovaTerminal.Core/Ssh/Native/HostKeyFingerprintFormatter.cs`
- `src/NovaTerminal.Core/Ssh/Native/KnownHostEntry.cs` (optional)
- tests for known host matching and mismatch

### Suggested files to modify
- `src/NovaTerminal.Core/Ssh/Storage/JsonSshProfileStore.cs`
- `src/NovaTerminal.Core/Ssh/Storage/SshJsonContext.cs`
- `src/NovaTerminal.App/Core/SessionManager.cs`
- `src/NovaTerminal.Core/Ssh/Models/SshProfile.cs` if migration or metadata is needed

## Acceptance criteria
- Trusted native hosts are remembered across app restarts.
- Host key mismatch is detected and produces a warning/error path.
- Session restore preserves `SshBackendKind`.
- Existing OpenSSH profiles continue to load and run unchanged.
- Tests cover:
  - first trust
  - repeat trust
  - mismatch
  - backend restore persistence

## Notes
- Do not store secrets in the known hosts store.
- Keep format/versioning explicit if using structured JSON.
- If using OpenSSH-style known_hosts formatting, isolate that logic cleanly.

---

# PR6 — Local Port Forwarding Parity

**Branch:** `feat/ssh-native-local-forwarding`

## Goal
Add the first real feature-parity capability beyond shell access: local port forwarding.

## Prompt
Add local port forwarding to the native SSH backend in NovaTerminal. Reuse existing profile forward definitions where possible. Implement listener lifecycle, channel opening, backpressure-safe byte copying, and clean teardown. Do not implement remote forwarding, dynamic forwarding, or jump hosts in this PR. Optimize for correctness, shutdown hygiene, and observability.

## Scope
Implement:
- local forwards only
- multiple simultaneous local forwards
- listener lifecycle tied to session lifecycle
- safe teardown on disconnect or close

### Suggested files to add
- `src/NovaTerminal.Core/Ssh/Native/NativePortForwardSession.cs`
- `src/NovaTerminal.Core/Ssh/Transport/PortForwardModels.cs` if needed
- tests or harness support for forward setup validation

### Suggested files to modify
- `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`
- `src/NovaTerminal.Core/Ssh/Models/SshProfile.cs` only if required
- Rust crate in `native/rusty_ssh/` to support direct-tcpip or equivalent forwarding channels

## Acceptance criteria
- Native SSH profile with one or more local forwards works.
- Local listeners bind and route traffic correctly.
- Clean shutdown removes listeners and closes channels.
- Failure to bind one forward is surfaced clearly.
- Terminal shell still works while forwards are active.

## Notes
- Do not block session startup forever on a single bad listener.
- Decide and document policy:
  - fail session if any enabled forward fails
  - or continue with partial success and clear warning
- Favor clear logging and metrics.

---

# PR7 — Jump Host Support

**Branch:** `feat/ssh-native-jump-hosts`

## Goal
Reach practical workflow parity with existing Direct SSH for common bastion/jump-host setups.

## Prompt
Add first-pass jump host support to NovaTerminal native SSH. Prefer a simple and explicit architecture rather than a clever one. One-hop support is sufficient for this PR. If multi-hop is significantly more complex, structure the code for it but do not complete it yet. Fail clearly for unsupported combinations rather than hiding them. Keep the OpenSSH backend as a fallback option.

## Scope
Implement first:
- one-hop jump host support for native backend
- explicit connector abstraction for future multi-hop
- clear error/fallback messaging

### Suggested files to add
- `src/NovaTerminal.Core/Ssh/Native/NativeJumpHostConnector.cs`
- `src/NovaTerminal.Core/Ssh/Native/JumpHostConnectPlan.cs`
- tests around profile translation to jump-host connect plan

### Suggested files to modify
- `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`
- `src/NovaTerminal.Core/Ssh/Sessions/SshSessionFactory.cs`
- `src/NovaTerminal.Core/Ssh/Models/SshProfile.cs` only if native-specific metadata is required
- Rust crate in `native/rusty_ssh/` for tunneled/chained connection support

## Acceptance criteria
- One-hop jump host works end-to-end.
- Unsupported multi-hop combinations fail clearly or fall back intentionally.
- Native and OpenSSH backends remain selectable per profile.
- Logging makes the chosen path obvious.

## Notes
- Do not implement a complex generic proxy graph yet.
- Explicitly document whether native jump host uses:
  - chained SSH connections
  - tunneled stream proxying
  - or a hybrid approach

---

# PR8 — Hardening, Diagnostics, and Rollout Controls

**Branch:** `feat/ssh-native-hardening-rollout`

## Goal
Make native SSH testable, operable, and safe to ship gradually.

## Prompt
Add rollout controls, diagnostics, and hardening for NovaTerminal native SSH. Expose backend selection in profile UI, add experimental gating, collect metrics for connect/auth/output timing, classify failures, and make fallback to OpenSSH straightforward. Optimize for operability, observability, and staged rollout rather than feature creep.

## Scope
Implement:
- per-profile backend selector in UI
- experimental feature flag / global toggle if needed
- metrics and structured logs
- failure classification
- clear fallback path to OpenSSH

### Suggested files to add
- `src/NovaTerminal.Core/Ssh/Native/NativeSshMetrics.cs`
- `src/NovaTerminal.Core/Ssh/Native/NativeSshFailureClassifier.cs`
- optional settings model additions
- optional diagnostics view model / UI elements

### Suggested files to modify
- `src/NovaTerminal.App/ViewModels/Ssh/*`
- `src/NovaTerminal.App/Views/Ssh/*`
- `src/NovaTerminal.App/SettingsWindow*` or equivalent
- `src/NovaTerminal.Core/Ssh/Sessions/SshSessionFactory.cs`
- `src/NovaTerminal.Core/Ssh/Sessions/NativeSshSession.cs`

## Suggested metrics
- connect latency
- host key verification duration
- auth duration
- time to first output byte
- disconnect reason
- forward setup success/failure counts

## Acceptance criteria
- Backend can be selected per profile.
- Native SSH can be globally gated or marked experimental.
- Failures are classified into useful buckets:
  - DNS/connect timeout
  - auth failure
  - host key mismatch
  - channel open failure
  - forwarding bind failure
  - remote disconnect
- User can switch a failing native profile back to OpenSSH easily.
- Logs/diagnostics are sufficient to debug real-world failures.

## Notes
- Keep rollout conservative.
- Prefer “experimental, opt-in, easy fallback” over bold defaults.

---

# Recommended milestone mapping

## M5.2
- PR4 — SSH Interaction UX Service

## M5.3
- PR5 — Known Hosts + Backend-Safe Persistence

## M5.4
- PR6 — Local Port Forwarding Parity

## M5.5
- PR7 — Jump Host Support

## M5.6
- PR8 — Hardening, Diagnostics, and Rollout Controls

---

# Implementation order advice

Follow this order:

1. PR4
2. PR5
3. PR6
4. PR7
5. PR8

That order keeps the system sane:
- first auth/trust UX
- then persistence
- then feature parity
- then bastion workflows
- then hardening and rollout

Trying to do jump hosts before auth/trust UX is how one accidentally creates a debugging hobby instead of a product.

---

# Optional test matrix

Use this matrix once PR4–PR8 start landing:

## Auth
- password
- public key
- encrypted private key
- keyboard-interactive

## Host keys
- first connection
- trusted reconnect
- changed host key

## Shell
- simple shell
- vim
- lazygit
- full-screen TUI resize

## Forwards
- one local forward
- multiple local forwards
- bad local bind
- disconnect while forward active

## Jump hosts
- one-hop bastion
- bad bastion credentials
- bad target after valid bastion

## Rollout
- switch backend OpenSSH -> Native
- switch backend Native -> OpenSSH
- restored profile preserves backend
