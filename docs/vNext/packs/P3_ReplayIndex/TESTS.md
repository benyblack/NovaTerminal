# Pack P3 — Tests (ReplayIndex)

## Required
1. **Index build test**
   - Index built from `.novarec` produces monotonically increasing (t_us, offset) pairs.

2. **Seek determinism tests**
   - Given a baseline recording, seek to:
     - 0
     - a timestamp before first resize
     - exactly at resize
     - after resize
   - Snapshot JSON must match expected.

3. **Alt-screen seek test**
   - Seek across enter/exit alt-screen boundaries.

## Optional
- Corruption handling test:
  - truncated recording triggers graceful failure with message.

## Guidance
Use canonical `snapshot.json` comparisons as primary gating.
