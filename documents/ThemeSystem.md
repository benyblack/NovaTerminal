# NovaTerminal Theme System

NovaTerminal features a robust theme management system that separates visual appearance (color schemes) from functional settings. This allows users to switch between different color palettes without altering their core configuration.

## Overview

Themes are stored as JSON files and managed by the `ThemeManager`.
The core components are:

-   **`TerminalTheme`**: Represents a single theme (colors, name).
-   **`ThemeManager`**: Handles loading, saving, importing, and retrieving themes.
-   **`JsonColorConverter`**: A custom JSON converter that serializes/deserializes colors (e.g., `#RRGGBB` or `sc#RGBA`).

## Theme Structure

A theme is defined by a JSON object with the following properties:

-   **`Name`**: The display name of the theme (e.g., "Dracula").
-   **`Foreground`**: The default text color.
-   **`Background`**: The terminal background color.
-   **`CursorColor`**: The color of the cursor.
-   **`Black`**, `Red`, `Green`, `Yellow`, `Blue`, `Magenta`, `Cyan`, `White`: Standard ANSI colors (0-7).
-   **`BrightBlack`**, `BrightRed`... `BrightWhite`: Bright ANSI colors (8-15).

### Example (Dracula.json)

```json
{
  "Name": "Dracula",
  "Foreground": "#F8F8F2",
  "Background": "#282A36",
  "CursorColor": "#F8F8F2",
  "Black": "#21222C",
  "Red": "#FF5555",
  "Green": "#50FA7B",
  "Yellow": "#F1FA8C",
  "Blue": "#BD93F9",
  "Magenta": "#FF79C6",
  "Cyan": "#8BE9FD",
  "White": "#F8F8F2",
  "BrightBlack": "#6272A4",
  "BrightRed": "#FF6E6E",
  "BrightGreen": "#69FF94",
  "BrightYellow": "#FFFFA5",
  "BrightBlue": "#D6ACFF",
  "BrightMagenta": "#FF92DF",
  "BrightCyan": "#A4FFFF",
  "BrightWhite": "#FFFFFF"
}
```

## Theme Manager

The `ThemeManager` (`Core/ThemeManager.cs`) is responsible for:

1.  **Loading Themes**: Scans the `themes/` directory for `.json` files on startup.
2.  **Built-in Themes**: Provides default themes (Dracula, Monokai, Solarized, etc.) if no custom themes are found.
3.  **Active Theme**: Tracks the currently applied theme.
4.  **Importing**: Can import themes from:
    *   **Windows Terminal** (`settings.json` schemes).
    *   **iTerm2** (`.itermcolors` files).
    *   **Lighthouse/Other** formats (via best-effort parsing).

## Usage

### In Code
```csharp
// Access the manager
var manager = new ThemeManager();

// Get a specific theme
var theme = manager.GetTheme("Dracula");

// Apply a theme
settings.ActiveTheme = theme;
```

### Adding a New Theme
1.  Create a JSON file in the `themes/` directory (e.g., `MyTheme.json`).
2.  Restart the application. A restart is required to load new files from disk.
3.  The theme will appear in the Settings -> Appearance dropdown.

## Theme Editor
The application includes a built-in **Theme Editor** in the Settings window:
-   Click the **Edit** button next to the theme dropdown.
-   Adjust individual ANSI colors using the color picker or hex input.
-   Save changes to persist them to the JSON file.
-   **Note**: The "Default" theme cannot be modified directly; clone it first or create a new theme.

## Dynamic Theming
To support both Light and Dark themes, the UI uses dynamic styling:
-   **Contrast Calculation**: The application calculates the luminance of the theme's background color.
    -   If Background is **Light**: Window foreground (text) becomes **Black**.
    -   If Background is **Dark**: Window foreground (text) becomes **White**.
-   **Tab Titles**: Tab headers inherit this dynamic foreground color to ensure readability.
-   **Connection Manager**: The Connection Manager window also adapts its title bar and background to match the active theme.

## Key Files
-   `Core/TerminalTheme.cs`: Data model.
-   `Core/ThemeManager.cs`: Business logic.
-   `Core/JsonColorConverter.cs`: JSON serialization.
-   `SettingsWindow.axaml`: UI for selecting and editing themes.
-   `themes/*.json`: Theme definitions.
