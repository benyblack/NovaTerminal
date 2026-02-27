# Graphics Compatibility Matrix

This document outlines the support level for various terminal graphics protocols in Nova Terminal across different Operating Systems and shells.

## Compatibility Summary

| Environment        | Kitty Graphics | iTerm2 Graphics | Tunneled Sixel (OSC 1339) | Standard Sixel (DCS) |
| :----------------- | :------------: | :-------------: | :-----------------------: | :------------------: |
| **Windows Native** | ✅ Full        | ✅ Full         | ✅ Full (Recommended)      | ❌ Blocked (ConPTY)  |
| **Windows WSL**    | ✅ Full        | ✅ Full         | ✅ Full (Recommended)      | ❌ Blocked (ConPTY)  |
| **Linux / macOS**  | ✅ Full        | ✅ Full         | ✅ Full                   | ✅ Full              |

## Operating System Nuances

### 1. Windows (ConPTY)
Windows Terminal and other Windows applications interact with the PTY via `ConPTY`. Even with the `PSEUDOCONSOLE_PASSTHROUGH` flag enabled, ConPTY acts as a high-level filter.

*   **Blocked Sequences**: ConPTY intercepts and strips `DCS` (Device Control String) sequences, which are required for standard Sixel transmission. It also blocks `DA1` (Device Attributes) queries, preventing tools from auto-detecting Sixel support.
*   **The Workaround**: Nova Terminal implements **Graphics Tunneling (OSC 1339)**. By wrapping Sixel data in an `OSC` (Operating System Command) sequence, we bypass the ConPTY filter. This is the **strongly recommended** way to use Sixel on Windows.
*   **Best Protocols**: Kitty Graphics and iTerm2 Inline Image protocol are prioritized as they are natively supported and bypass most filtering.

### 2. Linux & macOS (Standard PTY)
Unix-like systems use a transparent PTY layer.
*   **Raw Passthrough**: Standard Sixel (`DCS`) works natively without any modifications or tunneling.
*   **Auto-Detection**: Tools like `img2sixel` can correctly query Nova Terminal and receive the capability response (`\e[?62;4;22c`).

## Recommended Implementation for Developers
If you are developing tools for Nova Terminal on Windows, please use one of the following methods for reliable graphics:

1.  **Kitty Graphics Protocol**: (Directly supported via `SKImage.FromEncodedData`).
2.  **Tunneled Sixel**: Wrap standard Sixel in the Nova tunnel sequence:
    `\e]1339;<sixel_data>\x07`

## Verified Tools
*   `test_sixel.py` (Nova Custom Tester)
*   Kitty-compatible tools
*   iTerm2-compatible scripts
