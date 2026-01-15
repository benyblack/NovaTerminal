# NovaTerminal

NovaTerminal is a high-performance, GPU-accelerated terminal emulator for Windows, built with **.NET 10**, **Avalonia UI**, and **SkiaSharp**. It leverages the modern Windows Pseudo Console (ConPTY) API to provide accurate terminal emulation while delivering a fluid, customizable user experience.

## ✨ Key Features

### 🚀 Performance & Rendering
- **GPU Acceleration**: Custom rendering pipeline using **SkiaSharp** for high-speed text drawing.
- **Thread-Safe Buffer**: Robust concurrent architecture using `ReaderWriterLockSlim` to handle heavy output without UI freezing.
- **Smart Invalidation**: Optimized redraw logic to minimize CPU/GPU usage during idle times.

### 🎨 Customization & Theming
- **Theme Support**: Built-in themes (Dark, Solarized Dark) with support for custom JSON-based color schemes.
- **Dynamic Settings**: Change fonts, font sizes, and scrollback limits on the fly via a dedicated Settings UI.
- **Intelligent Backgrounds**: Addresses "black block" artifacts by intelligently remapping historical content when switching themes.

### 🔍 Productivity Tools
- **Multi-Tab Interface**: distinct sessions in a single window.
- **Search Overlay**: integrated search functionality (`Ctrl+Shift+F`) with result highlighting and navigation.
- **Configurable Scrollback**: Adjustable history limit (defualt 10,000 lines) to balance memory usage and history retention.
- **Secure Vault**: Encrypted secure storage using Windows DPAPI for managing API keys and secrets.

## 🛠️ Architecture

- **UI Framework**: Avalonia UI (Cross-platform ready, currently optimized for Windows).
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

## 📝 License

This project is licensed under the MIT License.
