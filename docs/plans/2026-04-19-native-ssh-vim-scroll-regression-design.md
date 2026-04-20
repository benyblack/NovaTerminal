# Native SSH Vim Scroll Regression Design

**Date:** 2026-04-19

## Goal

Diagnose and fix the native SSH regression where `vim` scrolling downward in alternate-screen mode stops repainting most of the visible window, while `Ctrl+L` forces a correct redraw.

## Scope

- Native SSH backend only
- `vim` downward scroll behavior in alternate-screen mode
- Reproduction with `scrolloff=5`
- Deterministic regression coverage
- Live Docker native SSH `vim` verification

## Non-Goals

- No broad terminal-core rewrite
- No unrelated resize/reflow work unless the repro proves it is required
- No OpenSSH or local PTY behavior changes
- No UI-specific Command Assist changes

## Existing Context

- Local PTY behavior is correct.
- The regression appeared after native SSH was introduced.
- The issue reproduces without resize by holding `j` in `vim`.
- Upward movement works.
- `Ctrl+L` redraws the screen correctly.
- The cursor does not reach the true end of the document during the broken state.
- The user’s `vim` config has `scrolloff=5`, so window scrolling starts before the cursor reaches the bottom row.

## Problem Statement

This does not look like a generic resize or prompt-return problem. It looks like a failure in the `vim` incremental downward window-scroll path in alternate-screen mode over native SSH.

The evidence points away from simple cursor movement:

- the bug starts before the last visible row because `scrolloff=5` triggers early window scrolling
- upward navigation remains correct
- `Ctrl+L` produces the correct content, implying the underlying document state is recoverable

That means the likely fault boundary is one of:

1. native SSH output delivery/order/chunking during `vim` scroll updates
2. VT parser/buffer handling of the specific scroll-region update pattern `vim` emits while scrolling downward
3. render invalidation of multi-line alternate-screen updates, exposed only by native SSH timing

## Chosen Approach

Use a two-layer regression strategy:

1. Add a deterministic repro that simulates the `vim scrolloff=5` downward scroll pattern and proves whether the parser/buffer path itself is correct.
2. Add a live Docker native SSH `vim` scenario that reproduces the real issue end to end.

This keeps the fix evidence-driven:

- if the deterministic repro passes but live `vim` fails, the bug is in native SSH event delivery or a live-path interaction around it
- if the deterministic repro fails, the bug is in VT scroll-region handling and should be fixed narrowly there

## Deterministic Repro Design

Add a failing test near the current native SSH parity / VT regression coverage that models:

- alternate-screen active
- a bounded editing region
- `scrolloff=5`
- repeated downward motion that causes the visible window to scroll

The assertions should prove that the visible window advances consistently, not only the last line.

Key assertions:

- visible lines move forward as downward scroll happens
- stale earlier rows do not remain frozen in the window
- the cursor stays aligned with the visible content
- later file content becomes visible as scrolling continues
- upward movement still behaves correctly

The deterministic test is the deciding oracle for whether the terminal core is actually wrong.

## Live Docker Vim Design

Add a real Docker scenario that uses `vim` inside the SSH container.

The test should:

1. create a long file inside the container
2. launch `vim` with controlled settings such as `-u NONE -N`
3. set `scrolloff=5`
4. send repeated `j` input over a real `NativeSshSession`
5. assert that the visible editing area continues updating during downward scroll
6. assert that the cursor can reach the actual end of the file

The live assertion should remain outcome-based rather than pixel-perfect.

## Fix Boundary

Default fix preference:

- first suspect: native SSH event delivery/session contract
- second suspect: narrow VT scroll-region handling for the exact `vim` downward-scroll pattern
- last suspect: renderer invalidation, only if deterministic/parser tests are correct and native session delivery is also proven correct

Do not jump to a broad VT refactor without the deterministic test proving the terminal core is wrong.

## Acceptance Criteria

- deterministic `vim scrolloff=5` downward-scroll regression test passes
- live Docker native SSH `vim` scenario passes
- downward scrolling updates the window across the visible editing area, not just the last row
- the cursor can reach the true end of the file without needing `Ctrl+L`
- existing native SSH deterministic, replay, and Docker suites remain green

## Risks

### Repro Fidelity

A synthetic repro may miss a small but important `vim` sequence detail. That is why the live Docker lane is required in the same effort.

### Scope Drift

It will be tempting to treat this as a general alternate-screen issue. Current evidence does not justify that. Keep the fix targeted at the `vim` downward-scroll regression.

### Misplaced Fix

If the deterministic repro passes, changing VT parser logic first would be the wrong move. In that case the fix belongs in native SSH delivery or the app path around it.
