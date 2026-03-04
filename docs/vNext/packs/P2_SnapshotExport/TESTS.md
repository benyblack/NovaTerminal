# Pack P2 — Tests (Snapshot Export)

## Required
1. **JSON determinism test**
   - Export twice without changing terminal state => byte-identical JSON output

2. **Schema validation test**
   - Validate required fields exist
   - Validate schemaVersion and basic structure

3. **ANSI export sanity**
   - Non-empty output for a known screen
   - Contains expected text fragments

## Optional
- PNG export smoke test (file exists, reasonable dimensions)
  - Avoid strict pixel comparison unless your infra already supports it.

## Guidance
Use a deterministic replay fixture to generate a known terminal state, then export from that state.
