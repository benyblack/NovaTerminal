# Image Protocol Support Matrix

This document defines NovaTerminal image protocol behavior across platforms.

## Matrix

| Protocol | Windows (ConPTY) | Linux | macOS |
|---|---|---|---|
| OSC 1337 (iTerm2 inline) | Supported | Supported | Supported |
| OSC 1339 tunneled Sixel | Supported | Supported | Supported |
| OSC 1339 tunneled Kitty payload | Supported | Supported | Supported |
| Native Kitty APC (`ESC _ G ... ST`) | Supported when `AllowNativeKittyGraphics` is on (default); probe → `ERR` + images skipped when off | Supported | Supported |
| DCS Sixel (`ESC P ... q ... ST`) | May be filtered by host PTY; use OSC 1339 tunnel when available | Supported | Supported |

## Windows Fallback Policy (M4.2, relaxed)

- NovaTerminal historically assumed ConPTY filtering was certain on Windows: non-tunneled Kitty
  probe queries (`a=q`) were answered `ERR` and non-tunneled images were skipped, to push clients
  toward Sixel or the OSC 1339 tunnel.
- Live probing (2026-09) showed modern ConPTY passes well-formed kitty APC through intact — the
  parser receives the complete sequence, terminator included. With `AllowNativeKittyGraphics`
  (TerminalSettings, default on) the parser therefore trusts what actually arrived: probes are
  answered `OK` and images decode, as on Linux/macOS.
- When the setting is off, the original policy holds (`ERR` + skip). If a ConPTY does strip APC,
  the probe never reaches the parser and the client sees silence — its own fallback path — so the
  honest-reply property is preserved either way.
- Tunneling via `OSC 1339` remains accepted and is unaffected by the setting.

## Notes

## Kitty graphics payload formats

- Container formats (PNG/JPEG/..., kitty `f=100` and absent `f`) decode via `SKBitmap.Decode`.
- Raw pixel formats `f=24` (RGB) and `f=32` (RGBA) decode via `IImageDecoder.DecodeRawImage`,
  sized by the `s=`/`v=` params.
- `o=z` payloads are inflated (zlib) before decoding.
- Transport `t=f` reads the file whose path is the payload through a host-injected
  `AnsiParser.ReadFileBytes` delegate — the VT layer stays free of file I/O. The App layer's
  reader is confined to absolute paths under `%TEMP%` with a 64 MB cap, because the path comes
  from the remote stream. `t=s` (shared memory) is not supported and skips gracefully.

## Notes

- Decoding is implemented by `NovaTerminal.Rendering.SkiaImageDecoder` (`SixelDecoder` for DCS/OSC 1339 sixel payloads, `SKBitmap.Decode` for Kitty/iTerm2 image bytes); decoded bitmaps render in the grid via `TerminalDrawOperation`'s image pass.
- Lifecycle: images pruned by the buffer (scroll eviction, erase, reflow) retire their bitmap handles; the owning view disposes them at its frame boundary, gated on no in-flight snapshot session predating the retire — an agent-host capture of any duration blocks disposal until it completes (#166).
- The fallback policy is intentionally conservative to avoid false advertising Kitty support through ConPTY.
- If a future backend bypasses ConPTY filtering, this policy can be relaxed.
