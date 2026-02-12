# QA: UI, Theming & Interaction

**Objective**: Validate the user interface and system integration.

## 1. Theming & Transparency
- **Action**: Toggle between themes (Dark, Light, Custom).
- **Action**: Adjust the "Main Opacity" slider.
- **Expected**: Background updates immediately. Acrylic/Blur effects apply correctly.
- **Verification**: Text readability remains high across all themes.

## 2. Selection & Mouse Logic
- **Drag Selection**: Click and drag to select text. Verify highlight accuracy.
- **Word/Line Select**: Double-click to select a word, triple-click to select a line.
- **Non-blocking Selection**: Verify that selecting text does NOT freeze the PTY or stop incoming output (standard modern terminal behavior).

## 3. Input & Clipboard Integrity
- **Clipboard Parity**: Copy text via Mouse (Selection) or Keyboard (`Ctrl+Shift+C`). Paste via Keyboard (`Ctrl+Shift+V`) or Right-Click.
- **High-Frequency Input**: Type rapidly; verify no missing keystrokes or input lag.
- **IME Support**: (If applicable) Verify Chinese/Japanese/Korean input methods function correctly within the terminal view.

## 4. Window Controls & Pane Management
- **Action**: Open multiple panes/tabs.
- **Action**: Resize panes via drag-and-drop.
- **Expected**: Terminal content in each pane adjusts correctly according to its individual size.

## 5. Title Bar
- **Action**: Change theme.
- **Expected**: The title bar color dynamically updates to match the palette.
