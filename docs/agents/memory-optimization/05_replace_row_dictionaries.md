# Prompt 5 — Replace Dictionary Side Tables

Goal:
Reduce memory overhead from per-row dictionaries.

Current implementation:

TerminalRow
- Dictionary<int,string> extendedText
- Dictionary<int,string> hyperlinks

Replace with:

SmallMap<int,string>

Implementation rules:

1. For <= 8 entries:
store in array

2. If entries exceed 8:
upgrade to Dictionary

3. Provide API:

TryGet(int key)
Set(int key, string value)
Remove(int key)

4. Ensure compatibility with existing rendering logic.

Add unit tests.