using System;

namespace NovaTerminal.Shell.Shortcuts;

public sealed record CommandPaletteUsageEntry(string CommandId, int UseCount, DateTimeOffset LastUsedAt);
