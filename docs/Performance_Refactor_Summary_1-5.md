# NovaTerminal Performance Refactor Summary: Steps 1-5

This document summarizes the major architectural improvements and performance optimizations implemented for the NovaTerminal rendering pipeline between January and February 2026.

## Architectural Evolution
The project has evolved from a synchronous, lock-heavy rendering model to a **lock-free, snapshot-based architecture**. This ensures that terminal state management (`TerminalBuffer`) and terminal rendering (`TerminalDrawOperation`) can operate independently without causing UI hangups.

---

### Step 1: Event Lifecycle Fixes
- **Action**: Resolved a memory leak in the invalidation event chain.
- **Optimization**: Ensured proper unsubscription from `OnInvalidate` events when terminal tabs are closed or sessions are re-attached.
- **Impact**: Stabilized long-term memory usage.

### Step 2: Diagnostic Pruning
- **Action**: Pruned high-frequency "Render Frame" logs.
- **Impact**: Removed significant console/debug I/O overhead, reclaiming CPU cycles for actual rendering.

### Step 3: O(1) Background Rendering
- **Action**: Eliminated full-screen `O(rows * cols)` background color scanning.
- **Implementation**: Background colors are now captured during row recording and cached within `SKPicture` entries.
- **Impact**: Dramatic reduction in per-frame CPU usage for static or low-activity terminal states.

### Step 4: Snapshot-Aware Rendering (High Performance)
- **Action**: Decoupled the renderer from the live buffer.
- **Key Changes**:
  - **FrameSnapshot**: Under a brief read lock, an immutable snapshot of visible rows and metadata is captured.
  - **Off-Lock Drawing**: Complex text shaping, font fallback, and rendering now happen outside the buffer lock.
  - **Thread-Safe Cache**: Converted `RowImageCache` to a lock-protected, deferred-disposal model to handle cross-thread invalidate requests.
- **Impact**: Consistent 60FPS output even under heavy shell activity.

### Step 5: Spike Stabilization & Budgeting
- **Action**: Smoothed out frame-time variance during "cache warmup" or mass updates.
- **Features**:
  - **Work Budgeting**: Limits `SKPicture` recording to 4 rows per frame to prevent stutter.
  - **Mass Invalidation Detection**: If >50% of the screen changes, the renderer prioritizes immediate "Direct Draw" from snapshots over caching.
  - **Allocation-Free Overlays**: Search and selection overlays now use index-based scanning instead of per-frame dictionary builds.
- **Impact**: Jitter-free terminal response during resizing and full-screen TUI updates (e.g., `htop`, `vim`, `mc`).

---

## Performance Comparison Matrix

| Metric | Legacy Rendering | Optimized Pipeline (Phase 5) |
| :--- | :--- | :--- |
| **Buffer Lock Hold** | Long (Entire render duration) | Minimal (Data copy only) |
| **Frame Variance** | High (Jumpy during updates) | Low (Capped work per frame) |
| **Steady-State Load** | `O(Rows * Cols)` | `O(Cache Hits)` |
| **Allocations** | High (Per-frame collections) | Near Zero (Steady state) |

## Performance Counters
New metrics are available in `RendererStatistics`:
- **PicsRec**: Row pictures recorded this frame.
- **RecTime**: Graphics recording time.
- **RenderTime**: Total draw duration (lock-free).
