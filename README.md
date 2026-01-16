# NovaTerminal

NovaTerminal is a high-performance, GPU-accelerated terminal emulator for Windows, built with **.NET 10**, **Avalonia UI**, and **SkiaSharp**. It leverages the modern Windows Pseudo Console (ConPTY) API to provide accurate terminal emulation while delivering a fluid, customizable, and visually stunning user experience.

## ✨ Key Features

### 💎 Modern UI & Aesthetics
- **Seamless Transparency**: Fully synchronized window-level opacity/blur effects.
- **Acrylic Design**: Custom window chrome with transparent tabs and title bar.
- **Polished Controls**: Custom-styled window controls and tab headers that blend perfectly with the theme.
- **Zero Artifacts**: Clean rendering with no white borders or visual glitches.

### 🚀 Performance & Rendering
- **GPU Acceleration**: Custom rendering pipeline using **SkiaSharp** for high-speed text drawing.
- **Thread-Safe Buffer**: Robust concurrent architecture using `ReaderWriterLockSlim` to handle heavy output without UI freezing.
- **Smart Invalidation**: Optimized redraw logic to minimize CPU/GPU usage during idle times.

### 🎨 Customization & Theming
- **Theme Support**: Built-in themes (Dark, Solarized Dark) with support for custom JSON-based color schemes.
- **Dynamic Settings**: Change fonts, font sizes, opacity, and scrollback limits on the fly.
- **Intelligent Backgrounds**: Addresses "black block" artifacts by intelligently remapping historical content when switching themes.

### 🔍 Productivity Tools
- **Multi-Tab Interface**: Manage distinct sessions in a single window with smooth drag-and-drop support.
- **Search Overlay**: Integrated search functionality (`Ctrl+Shift+F`) with result highlighting and navigation.
- **Configurable Scrollback**: Adjustable history limit (default 10,000 lines).
- **Secure Vault**: Encrypted secure storage using Windows DPAPI for managing API keys and secrets.

## 🛠️ Architecture

- **UI Framework**: Avalonia UI (Cross-platform ready, optimized for Windows 11 aesthetics).
- **Backend**: Win32 ConPTY API for authentic console behavior.
- **Input Handling**: Full xterm-compatible input forwarding (mouse reporting, special keys).

## 📦 Requirements

- **OS**: Windows 10 (1903+) or Windows 11.
- **Runtime**: .NET 10.0 SDK/Runtime.

## ⌨️ Shortcuts

- `Ctrl+Shift+T`: New Tab
- `Ctrl+Shift+W`: Close Tab
- `Ctrl+Shift+F`: Toggle Search
- `Ctrl+Tab` / `Ctrl+Shift+Tab`: Switch Tabs
- `Ctrl+Plus` / `Ctrl+Minus`: Zoom In/Out (Font Size)
- `Ctrl+Scroll`: Zoom In/Out

## 📝 License

This project is licensed under the MIT License.
