# Nova Replay Format v2

This document defines the replay file format used by NovaTerminal (`.rec` / `.novarec`).

## Overview

- Encoding: UTF-8 text
- Container: JSON Lines
- Line 1: v2 header (`ReplayHeader`)
- Remaining lines: replay events (`ReplayEvent`)

## Header

The first line must be a JSON object:

```json
{"type":"novarec","v":2,"cols":120,"rows":30,"date":"2026-02-13T12:00:00.0000000Z","shell":"pwsh.exe"}
```

Fields:

- `type` (`string`): must be `"novarec"`
- `v` (`int`): format version (`2`)
- `cols` (`int`): initial terminal columns
- `rows` (`int`): initial terminal rows
- `date` (`string`): ISO-8601 UTC timestamp
- `shell` (`string`): shell command used during recording

## Events

Each subsequent line is a JSON event with:

- `t` (`long`): milliseconds from recording start
- `type` (`string`): event kind

### `type: "data"`

```json
{"t":200,"type":"data","d":"BASE64_BYTES"}
```

- `d` (`string`): base64 raw PTY output bytes

### `type: "resize"`

```json
{"t":350,"type":"resize","cols":100,"rows":28}
```

- `cols` (`int`)
- `rows` (`int`)

### `type: "input"`

```json
{"t":420,"type":"input","d":"BASE64_BYTES","i":"legacy-string"}
```

- `d` (`string`): base64 raw user input bytes (preferred)
- `i` (`string`): legacy plain-text input field (optional fallback)

### `type: "marker"`

```json
{"t":1000,"type":"marker","n":"checkpoint-name"}
```

- `n` (`string`): marker name

### `type: "snapshot"`

```json
{
  "t":1200,
  "type":"snapshot",
  "s":{
    "cols":120,"rows":30,"cx":10,"cy":5,"alt":false,
    "st":0,"sb":29,
    "awm":true,"ckm":false,"decom":false,"bp":false,"cv":true,
    "fg":4294967295,"bg":4278190080,"fgi":-1,"bgi":-1,
    "dfg":true,"dbg":true,"inv":false,"bold":false,"faint":false,
    "italic":false,"ul":false,"blink":false,"strike":false,"hidden":false,
    "cells":"BASE64_TERMINALCELL_BYTES",
    "cells_sizeof":12,
    "cells_layout_id":"TerminalCell/v1",
    "ext":{"15":"🚀"},
    "wrap":[false,false,false]
  }
}
```

Snapshot fields:

- Geometry: `cols`, `rows`, cursor `cx`/`cy`, alt-screen `alt`
- Scroll region: `st`, `sb`
- Mode flags: `awm`, `ckm`, `decom`, `bp`, `cv`
- Current style state: `fg`, `bg`, `fgi`, `bgi`, defaults and style booleans
- Cell payload:
  - `cells`: base64 `TerminalCell[]` bytes
  - `cells_sizeof`: serialized `sizeof(TerminalCell)` from the recorder runtime
  - `cells_layout_id`: serialized cell-layout contract id (currently `TerminalCell/v1`)
  - `ext`: sparse extended-text map (`row*cols+col -> string`)
  - `wrap`: per-row wrap flags

## Compatibility

- Reader behavior:
  - If first line is a valid v2 header (`type=novarec`, `v=2`), parse as v2.
  - Otherwise fallback to legacy v1 lines (`{"t":...,"d":"..."}`).
- Input events:
  - Reader prefers `d` (base64 bytes), then falls back to legacy `i`.
- Snapshot layout validation:
  - If `cells_sizeof` and/or `cells_layout_id` are present, reader validates against the current runtime layout.
  - On mismatch, reader throws `InvalidDataException` with a `cell layout mismatch` message and expected/actual values.
  - Legacy snapshots without these fields still load.

