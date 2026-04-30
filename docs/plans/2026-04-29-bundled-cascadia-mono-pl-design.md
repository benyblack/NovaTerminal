# Bundled Cascadia Mono PL Design

**Goal:** Bundle `Cascadia Mono PL` with NovaTerminal, include its license notice, and make it the first-run terminal font for new users.

## Problem

NovaTerminal currently defaults new users to `Consolas` through persisted settings, which produces a weaker first-run appearance than the renderer's preferred monospace stack and does not guarantee Powerline glyph coverage. The app should ship a deterministic default font so first-run UX is consistent across machines.

## Constraints

- Do not modify VT parsing or terminal rendering behavior unless required.
- Keep the change in app/bootstrap/settings code rather than terminal core logic.
- Preserve existing user settings when a font has already been saved.
- Include upstream license notice with any bundled font asset.
- Prefer deterministic tests over UI snapshots.

## Recommended Approach

Bundle `Cascadia Mono PL` as an Avalonia asset, register it during app startup, and update the default persisted font family for new settings files to the bundled family name.

### Why this approach

- Gives every new user the same first-run font without relying on OS-installed fonts.
- Keeps the change additive and localized to app resource/bootstrap code.
- Uses an upstream Powerline-capable font with a straightforward SIL OFL licensing story.
- Avoids modifying render paths or terminal-grid behavior.

## Architecture

### 1. Bundle the font asset

Add the upstream `CascadiaMonoPL-Regular.otf` file under `src/NovaTerminal.App/Assets/Fonts/`. Add the corresponding license text under `src/NovaTerminal.App/Assets/Fonts/LICENSES/` so the notice ships with the app output.

### 2. Register the bundled font at startup

Update the Avalonia app startup path to register the bundled font collection before the main window is created. This ensures the font can be resolved by family name even on machines that do not have it installed globally.

### 3. Change first-run settings default

Update `TerminalSettings.FontFamily` so fresh settings default to `Cascadia Mono PL`. Existing settings files remain unchanged because deserialization preserves saved values.

### 4. Keep settings UI aligned

Ensure the settings window includes the bundled font in its font picker, or otherwise preserves the selected default cleanly when the bundled family is active. The user should see the actual selected font name rather than an unresolved fallback.

## Error Handling

- If the bundled font fails to register, the app should continue to start and fall back to existing font resolution behavior.
- If the settings UI cannot enumerate the bundled font through the normal system font list, it should still preserve the configured value rather than replacing it with `Consolas`.

## Testing Strategy

- Add a deterministic unit test that fresh `TerminalSettings` instances default to `Cascadia Mono PL`.
- Add a deterministic unit test that loading a settings file with an explicit saved font preserves that saved font.
- Verify app build output includes the bundled font asset and license notice.

## Alternatives Considered

### Prefer installed Powerline-capable fonts only

Pros:
- no bundled font payload

Cons:
- inconsistent first-run UX
- still depends on machine state

### Bundle a Nerd Font instead

Pros:
- broader glyph coverage

Cons:
- larger payload
- more license-review surface
- more complexity than needed for the default-font problem
