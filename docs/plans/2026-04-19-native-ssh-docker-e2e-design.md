# Native SSH Docker End-to-End Design

**Date:** 2026-04-19

## Goal

Add a real end-to-end native SSH verification lane that connects to a Dockerized OpenSSH server, completes host key and password authentication, runs a command, and verifies prompt return through the real `NativeSshSession` path.

## Scope

- Native SSH backend only
- Dockerized OpenSSH fixture running on localhost
- First-connect host key acceptance
- Password authentication
- Running a simple command and verifying terminal output
- Verifying sane prompt return after command completion
- CI-capable execution when Docker is available

## Non-Goals

- No fullscreen / alternate-screen validation in this first live lane
- No resize-burst validation in this first live lane
- No key-auth matrix yet
- No parser or renderer changes unless the live test reveals a real mismatch not already covered by deterministic tests
- No dependency on external network hosts

## Existing Context

- Step 1 already hardened `NativeSshSession` for deterministic VT correctness regressions.
- Step 2 already added deterministic native SSH transcript and replay coverage.
- What is still missing is a true end-to-end path that proves:
  - real SSH handshake
  - real auth flow
  - real shell startup
  - real command execution over native SSH
- The current external suite already provides an executable harness project in `tests/NovaTerminal.ExternalSuites`, but it is transcript-driven rather than live-SSH-driven.

## Problem

The current native SSH verification layers are useful but incomplete:

- deterministic session-boundary tests prove managed behavior
- replay-backed tests prove expected terminal end states

Neither proves that native SSH actually works against a real SSH server in CI. Without a live localhost fixture, regressions in handshake, host key handling, auth flow, or shell startup can slip through while deterministic tests remain green.

## Chosen Approach

Create a Dockerized OpenSSH fixture and a live native SSH test that drives a real `NativeSshSession` against `127.0.0.1`.

The test should:

1. Start an OpenSSH container on a mapped localhost port
2. Wait until SSH is accepting connections
3. Create a native SSH profile targeting that port
4. Use an interaction handler that:
   - accepts the host key on first connect
   - supplies the configured password
5. Attach `NativeSshSession` output to `AnsiParser` and `TerminalBuffer`
6. Send a simple command such as `printf 'hello\\n'`
7. Wait for command output and prompt return
8. Assert final buffer state

## Why This Approach

### Compared with using the local machine SSH service

Docker is the better first fixture because it is:

- reproducible
- isolated
- CI-friendly
- independent of developer machine SSH configuration

### Compared with adding fullscreen and resize immediately

The first live lane should stay narrow. The highest-value first check is "can native SSH really connect, authenticate, run a command, and return to a prompt?" That proves the live stack works without introducing the extra flake risk of TUI and resize timing.

## Architecture

### Fixture Layer

Add a small helper in the external or live test layer that manages:

- `docker run`
- fixed username/password
- host port mapping
- readiness polling
- teardown

This helper should be explicit and disposable.

### Test Layer

Add a live native SSH test that uses:

- `NativeSshSession`
- `AnsiParser`
- `TerminalBuffer`
- a test interaction handler

This test should not depend on UI controls. It should validate the same terminal state the app depends on, but at the core/session boundary.

### Gating

The lane should be runnable in CI when Docker is available, and skippable otherwise.

Examples of acceptable gating:

- environment variable opt-in
- Docker availability probe
- category split between ordinary unit tests and live external tests

## Acceptance Criteria

- a Dockerized SSH server is started and torn down by the test
- native SSH successfully connects to the container
- host key handling succeeds on first connect
- password auth succeeds
- a command runs successfully through the live SSH session
- terminal output contains the expected command output
- terminal state returns to a sane prompt after command completion
- the lane is CI-capable when Docker is present

## Risks

### Docker Availability

CI and local environments may differ. The test must skip cleanly when Docker is unavailable rather than fail ambiguously.

### Prompt Variability

Different shell defaults can make prompt assertions fragile. The container setup should force a simple deterministic shell prompt.

### Timing Flakiness

Live SSH introduces asynchronous timing. The test should use condition-based waiting rather than fixed sleeps wherever possible.

## Follow-Up

Once this first live lane is stable, extend the Docker fixture with:

- fullscreen / alternate-screen exit scenario
- resize-burst scenario
- optional key-auth path

That should be a second step, not bundled into the first live lane.
