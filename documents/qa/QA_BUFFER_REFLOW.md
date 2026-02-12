# QA: Buffer Integrity & Reflow

**Objective**: Ensure text layout and history are preserved during window state changes.

## 1. Reflow & Wrapping
- **Status**: `[Automated]`
- **Action**: Run `ReflowScenariosTests.cs` and `ReflowRegressionTests.cs`.
- **Action**: Resize the window horizontally.
- **Expected**: Text reflows correctly based on row metadata (soft vs hard wraps). No "scattered text."
- **Boundary Edge Cases**: Verify CJK characters and Emojis that land exactly on the wrap boundary during a shrink operation. They should move to the next line as a unit.

## 2. Stateful Reflow
- **Historical Reflow**: Scroll up into the history (scrollback buffer), then resize the window.
- **Expected**: The visible viewport should adjust relative to the scroll position without "jumping" or losing the user's place.

## 3. Scrolling Regions & Margins
- **Action**: Set a scrolling region (`DECSTBM`) with fixed header/footer (e.g., in `tmux` or `htop`).
- **Action**: Resize the window.
- **Expected**: The fixed regions should remain correctly positioned, and the scrollable area should reflow within its assigned bounds.

## 4. Scrollback Persistence & Integrity
- **Action**: Run `cat` on a large file.
- **Verification**: Scroll up and verify that attributes (colors, styles) and content are intact.
- **Search Integration**: Perform a search in the scrollback buffer; verify matches remain highlighted even after a resize.

## 5. Large Output (Stress)
- **Action**: Run a command that produces massive output (e.g., `find /` or a fast build log).
- **Expected**: The terminal handles the stream with backpressure; no UI hangs or memory leaks.
