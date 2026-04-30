# Native Remote Path Autocomplete Design

## Goal

Make remote path entry materially easier in NovaTerminal without turning the app into a remote file browser. The first user-facing integration point is the transfer dialog, but the implementation should be reusable for future remote-path inputs.

## Scope

This design adds:

1. NativeSSH-only remote path autocomplete
2. Active-session-gated suggestions
3. Directory and file suggestions
4. A reusable autocomplete input surface for remote-path text entry

This design does not add:

- OpenSSH support
- recursive search
- a remote file browser
- terminal-grid rendering changes
- autocomplete for inputs that are not tied to an active SSH session

## Constraints

- NativeSSH is the only supported backend for this feature
- suggestions must not go through terminal stdout/stderr
- autocomplete must remain optional help, never a blocker for manual typing
- the implementation must be reusable outside the transfer dialog
- active session presence is required before suggestions are fetched

## Product Direction

NovaTerminal remains terminal-first. Remote path autocomplete should feel closer to shell completion than to a file manager.

That means:

- text input stays primary
- suggestions appear inline under the input
- keyboard selection matters more than visual chrome
- the user can always ignore suggestions and type the full path manually

## Chosen Approach

Use a dedicated NativeSSH remote listing query path, separate from terminal IO, and expose it through a reusable autocomplete service in the app layer.

Suggestions are only available when:

- the current profile uses NativeSSH
- there is a live active SSH session associated with that profile/input context

The presence of an active session is a product and UX gate. The actual suggestion query remains a dedicated native request path, not terminal output scraping.

## Why This Approach

### Rejected: reuse the terminal session stream

Running completion commands through the visible terminal session would pollute terminal output, interfere with shell state, and create prompt-parsing edge cases. That is not acceptable in NovaTerminal.

### Rejected: full remote browser

That is a larger product surface with browsing, navigation, loading, refresh, and permissions concerns. It solves a different problem and would drag the transfer workflow toward a separate SFTP client.

### Rejected: OpenSSH compatibility layer

The user is explicitly moving away from OpenSSH. Designing for two backends here would add complexity without payoff.

## Backend Query Model

The native backend gets a small directory-listing request for autocomplete.

Expected request shape:

- connection options
- remote parent path to list

Expected response shape:

- entry name
- full path
- `isDirectory`

Optional metadata like size or mtime is out of scope for v1.

The backend should list one directory at a time. The app layer will filter typed prefixes client-side. This keeps the number of native roundtrips low while preserving responsive typing.

## Active Session Gating

Autocomplete must only run when the app knows there is an active NativeSSH session for the relevant profile/input context.

Because autocomplete is meant to be reusable, the app needs a lightweight active-session registry rather than hardcoding the transfer dialog to the current pane.

The registry should track enough metadata to answer:

- is this session still active?
- which profile does it belong to?
- is the backend NativeSSH?

It should not become a new execution layer for terminal traffic. It is only a gate and lookup source for autocomplete.

## Query Resolution Rules

Given a typed remote path, the app resolves:

1. the parent directory to query
2. the leaf prefix to filter

Examples:

- `~/Do` -> query `~`, filter `Do`
- `/mnt/media/mov` -> query `/mnt/media`, filter `mov`
- `/mnt/media/movies/` -> query `/mnt/media/movies`, filter empty prefix

The app should support:

- absolute paths
- `~`
- partially typed final path segments

Out of scope:

- recursive globbing
- shell variable expansion beyond `~`

## Suggestion Ranking

V1 ranking should stay simple and deterministic:

1. prefix match before contains match
2. directories before files when both are exact prefix matches
3. alphabetical within the same rank group

This is enough for a useful first pass and easy to test.

## UI Behavior

Each remote-path input using this feature should show a popup under the textbox.

Behavior:

- debounce user typing by about `150-250 ms`
- fetch directory entries for the resolved parent path
- filter and rank matches client-side
- show both directories and files
- visually distinguish directories

Keyboard behavior:

- `Down` / `Up` moves the selected suggestion
- `Enter` accepts the selected suggestion
- `Tab` accepts the selected suggestion if one exists
- `Escape` closes only the suggestion popup first

Completion behavior:

- selecting a directory inserts the completed path and appends `/`
- selecting a file inserts the full file path
- focus stays in the input

Failure behavior:

- no active session -> no popup, plain typing still works
- native query failure -> suppress suggestions for that cycle, no modal error

## Reusable UI Surface

There are no other active-session-aware remote-path inputs on this branch today besides the transfer dialog flow. To still satisfy reuse, the UI implementation should not be transfer-specific.

The preferred shape is a reusable control or controller such as:

- `RemotePathInput`
- or a textbox-attached autocomplete controller plus popup

The first integration target is `TransferDialog`. Future remote-path inputs should be able to adopt the same component without copying behavior.

## App-Layer Service

The app should expose a single autocomplete service abstraction that:

- accepts the active session context
- accepts the current remote input text
- returns ranked suggestions

Responsibilities:

- validate gating rules
- resolve parent path and prefix
- invoke the native listing request
- apply filtering and ranking

This keeps the UI control thin and keeps query semantics testable without UI.

## Testing Strategy

### Deterministic unit tests

- path splitting and parent resolution
- ranking and filtering
- active-session gating
- `~` handling
- directory completion appends `/`

### Core interop tests

- native list-directory request JSON
- native response parsing

### UI tests

- popup opens when suggestions exist
- keyboard selection and acceptance
- popup closes on `Escape`
- selecting a suggestion updates the textbox

### Optional E2E

- Docker-backed NativeSSH listing test for one known directory

## Rollout

1. add native list-directory interop
2. add active NativeSSH session registry
3. add autocomplete service and ranking logic
4. add reusable remote-path autocomplete UI
5. integrate into transfer dialog

## Expected Outcome

After this work:

- remote path entry is significantly easier for NativeSSH transfers
- terminal output remains untouched
- the UI stays text-first and keyboard-friendly
- the code path is reusable for future active-session-aware remote-path inputs
