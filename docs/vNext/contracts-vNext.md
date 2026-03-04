# NovaTerminal vNext — Contracts (Events, Recording, Index)

This document defines **data contracts** for vNext features:
- deterministic replay seek + timeline
- command boundaries
- perf regression
- live relay / remote replay viewer

It is intentionally **implementation-agnostic** (C#/Rust friendly) and focuses on:
- event schemas
- `.novarec` container layout
- `.novarec.idx` index layout (binary sketch)
- versioning + compatibility rules

---

## 0. Versioning & Compatibility Rules

### Schema versions
- `recordingSchemaVersion`: integer, increments on **breaking** changes to `.novarec`.
- `indexSchemaVersion`: integer, increments on **breaking** changes to `.idx`.
- Consumers MUST:
  - reject unknown major schema versions
  - accept newer minor fields when possible

### Determinism rules
- Any input affecting render/state must be representable as an event (or metadata).
- “Derived events” (e.g., command boundaries) are optional but must be **replay-stable** if present.

---

## 1. Event Model

### 1.1 Common header
All events share a common header:

| Field | Type | Notes |
|---|---|---|
| `type` | `u16` | enum discriminant |
| `t_us` | `u64` | microseconds since session start (monotonic) |
| `len` | `u32` | payload length in bytes |

> `t_us` MUST be monotonic. If the source clock is unstable, normalize at capture time.

### 1.2 Event types (vNext set)

#### E1: `PtyOutputChunk`
**type:** 1  
Payload:
- `bytes: byte[len]`

Notes:
- store as-is PTY output stream chunks
- chunking strategy may vary; determinism depends on content, not chunk size

---

#### E2: `Resize`
**type:** 2  
Payload:
- `cols: u16`
- `rows: u16`

---

#### E3: `ModeChange`
**type:** 3  
Payload:
- `flags: u32` (bitfield)

Suggested flags:
- bit0: alt screen active
- bit1: bracketed paste
- bit2: mouse tracking
- bit3: application cursor keys
- bit4: application keypad

---

#### E4: `CommandBoundary`
**type:** 4  
Payload:
- `kind: u8` enum
- `command_id: u32` (stable within session)
- `data_len: u16`
- `data: byte[data_len]` (UTF-8)

`kind`:
- 0 PromptStart
- 1 CommandStart
- 2 CommandEnd
- 3 ExitCode

`data` conventions:
- PromptStart: optional prompt string
- CommandStart: command line (best-effort)
- CommandEnd: empty
- ExitCode: ASCII integer e.g. `"0"`, `"127"`

---

#### E5: `Marker`
**type:** 5  
Payload:
- `kind: u8` enum
- `payload_len: u16`
- `payload: byte[payload_len]` (UTF-8 or compact binary)

`kind` examples:
- 0 Bookmark
- 1 ErrorBurst
- 2 SearchHit
- 3 UserNote
- 4 PerfSample (optional; prefer separate JSONL for perf)

---

#### E6: `Metadata`
**type:** 6  
Payload is **a single JSON blob (UTF-8)** with required keys below.

Required metadata keys:
- `recordingSchemaVersion`
- `appVersion`
- `os` (e.g., win/mac/linux)
- `fontProfileId`
- `dpiProfileId`
- `cols`, `rows` (initial)
- `startTimeUtc` (ISO8601 string; informational only)
- `monotonicClock` (identifier string)

Optional:
- `gpuAdapter`
- `locale`
- `shell` (detected)
- `commandMarkersEnabled` (bool)

---

## 2. `.novarec` Container Layout (Binary)

Goal: simple append-only container.

### Layout
```
FileHeader
EventRecord*
```

### FileHeader (fixed 64 bytes)
| Field | Type | Notes |
|---|---|---|
| magic | 8 bytes | ASCII `NOVAREC1` |
| header_len | u32 | bytes, including this field |
| recordingSchemaVersion | u32 | must match metadata |
| flags | u32 | reserved |
| reserved | remaining | zero |

Immediately after header, write a `Metadata` event.

### EventRecord
```
u16 type
u64 t_us
u32 len
byte[len] payload
u32 crc32 (optional, behind flag)
```

Notes:
- CRC is optional but recommended for robustness (especially for uploads).
- If CRC enabled, set a header flag.

---

## 3. `.novarec.idx` Index Layout (Binary Sketch)

Goal: fast seek + marker queries without scanning the entire recording.

### Layout
```
IndexHeader
TimeToOffsetTable
MarkerTable (per kind)
CommandTable
Footer
```

### IndexHeader
| Field | Type | Notes |
|---|---|---|
| magic | 8 bytes | ASCII `NOVAIDX1` |
| indexSchemaVersion | u32 | |
| flags | u32 | |
| t0_us | u64 | usually 0 |
| recording_length_bytes | u64 | for sanity |
| time_table_count | u32 | |
| marker_kind_count | u16 | |
| reserved | ... | |

### TimeToOffsetTable
Array of entries:
- `t_us: u64`
- `offset: u64` (byte offset into `.novarec`)

Density:
- either every N milliseconds (e.g., 50ms)
- or every K events
- choose predictable density for O(1) seek

### MarkerTable
For each marker kind:
- `kind: u8`
- `count: u32`
- entries: `(t_us: u64, offset: u64) * count`

### CommandTable
Stores command lifecycle boundaries for folding and navigation:
- `count: u32`
- entries:
  - `command_id: u32`
  - `prompt_t_us: u64` + `prompt_offset: u64`
  - `start_t_us: u64` + `start_offset: u64`
  - `end_t_us: u64` + `end_offset: u64`
  - `exitcode: i32` (or -1 unknown)

### Footer
- `u32 header_crc`
- `u32 full_crc`

---

## 4. Perf Metrics Contract (JSONL)

Prefer JSONL for perf due to schema evolution.

### Line schema (example)
Each line is a JSON object:
- `t_us`
- `frame_time_ms`
- `dirty_rows`
- `dirty_cells`
- `draw_calls`
- `glyph_cache_hit_rate`
- `texture_uploads`
- `gpu_mem_mb`

Add:
- `baselineId`
- `workloadId`
- `os`, `gpuAdapter`, `fontProfileId`, `dpiProfileId`

---

## 5. Live Relay Protocol (v1)

Transport: WebSocket  
Message format: length-prefixed binary frames.

### Frame types
- `0x01`: `Metadata` JSON
- `0x02`: `EventRecord` (same as `.novarec` event record without file header)
- `0x03`: `Heartbeat` (optional)
- `0x04`: `Control` (server → client: revoke, rotate token)

Security v1:
- TLS
- expiring bearer token
- viewer read-only enforced server-side

---

## 6. Backward Compatibility Strategy

- `.novarec` reader must ignore unknown event types if it can skip by `len`.
- `.idx` is optional; if missing, reader can scan and build cache.
- When adding new event types:
  - bump schema only if existing parsers cannot safely skip.

---

## 7. Testing Requirements (Contract-level)

- Round-trip: record → replay → snapshot JSON equality
- Seek determinism: seek to marker timestamp yields same snapshot
- Index correctness: offset points to valid event boundary
- Corruption handling: CRC mismatch should fail gracefully with diagnostics
