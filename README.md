# NovaTerminal

NovaTerminal is a high-performance, GPU-accelerated terminal emulator for Windows, built with **.NET 10**, **Avalonia UI**, and **SkiaSharp**. It leverages the modern Windows Pseudo Console (ConPTY) API to provide accurate terminal emulation while delivering a fluid, customizable, and visually stunning user experience.

## ✨ Key Features

### 💎 Modern UI & Aesthetics
- **Seamless Transparency**: Fully synchronized window-level opacity and blur effects (Mica, Acrylic).
- **Acrylic Design**: Custom window chrome with transparent tabs and title bar.
- **Background Images**: Support for background images with opacity and stretch settings.
- **Discrete Resizing**: Smooth, step-based resizing logic to prevent visual artifacts.
- **Polished Controls**: Custom-styled window controls and tab headers that blend perfectly with the theme.

### 🚀 Performance & Rendering
- **GPU Acceleration**: Custom rendering pipeline using **SkiaSharp** for high-speed text drawing.
- **Thread-Safe Buffer**: Robust concurrent architecture using `ReaderWriterLockSlim` to handle heavy output.
- **Smart Invalidation**: Optimized redraw logic to minimize CPU/GPU usage during idle times.

### 🛠️ Profiles & Customization
- **Multi-Profile Support**: Configure distinct profiles for PowerShell, CMD, WSL, or custom shells.
- **Visual Overrides**: Set specific fonts, sizes, and themes per profile (e.g., larger font for WSL, separate theme for Admin shells).
- **Live Settings**: Changes to backgrounds, opacity, and fonts apply immediately without restart.
- **Theme Support**: Built-in themes (Dark, Solarized Dark) with support for custom JSON-based color schemes.

### 🔍 Productivity Tools
- **Command Palette**: `Ctrl+Shift+P` to access all commands via a searchable overlay.
- **Split Panes**: Split tabs vertically (`Ctrl+Shift+D`) or horizontally (`Ctrl+Shift+E`) and resize them with drag-and-drop dividers.
- **Multi-Tab Interface**: Manage distinct sessions in a single window.
- **Pane Navigation**: Move focus between panes using `Alt + Arrow Keys`.
- **Search Overlay**: Integrated search functionality (`Ctrl+Shift+F`) with result highlighting.

## 📦 Requirements

- **OS**: Windows 10 (1903+) or Windows 11.
- **Runtime**: .NET 10.0 SDK/Runtime.

## ⌨️ Shortcuts

| Action | Shortcut |
| :--- | :--- |
| **General** | |
| Command Palette | `Ctrl+Shift+P` |
| New Tab | `Ctrl+Shift+T` |
| Close Tab | `Ctrl+Shift+W` |
| Settings | (via Command Palette) |
| **View, Panes & Tabs** | |
| Split Vertical | `Ctrl+Shift+D` |
| Split Horizontal | `Ctrl+Shift+E` |
| Next Tab | `Ctrl+Tab` |
| Previous Tab | `Ctrl+Shift+Tab` |
| Navigate Panes | `Alt + Arrow Keys` |
| Zoom In/Out | `Ctrl + +/-` or `Ctrl + Scroll` |
| **Edit** | |
| Find / Search | `Ctrl+Shift+F` |
| Paste | `Ctrl+V` |

## 🛠️ Architecture

- **UI Framework**: Avalonia UI (Cross-platform ready, optimized for Windows 11).
- **Backend**: Win32 ConPTY API for authentic console behavior.
- **Input Handling**: Full xterm-compatible input forwarding.

## 📝 License

This project is licensed under the MIT License.
