# Image Protocol Support Matrix

This document defines NovaTerminal image protocol behavior across platforms.

## Matrix

| Protocol | Windows (ConPTY) | Linux | macOS |
|---|---|---|---|
| OSC 1337 (iTerm2 inline) | Supported | Supported | Supported |
| OSC 1339 tunneled Sixel | Supported | Supported | Supported |
| OSC 1339 tunneled Kitty payload | Supported | Supported | Supported |
| Native Kitty APC (`ESC _ G ... ST`) | Fallback to `ERR` probe response to force client fallback | Supported | Supported |
| DCS Sixel (`ESC P ... q ... ST`) | May be filtered by host PTY; use OSC 1339 tunnel when available | Supported | Supported |

## Windows Fallback Policy (M4.2)

- NovaTerminal assumes ConPTY filtering is likely on Windows.
- For non-tunneled Kitty probe queries (`a=q`) received via APC, NovaTerminal replies with `ERR` instead of `OK`.
- This encourages client-side fallback to Sixel or other supported paths.
- Tunneling via `OSC 1339` remains accepted and is not forced to fallback.

## Notes

- The fallback policy is intentionally conservative to avoid false advertising Kitty support through ConPTY.
- If a future backend bypasses ConPTY filtering, this policy can be relaxed.
