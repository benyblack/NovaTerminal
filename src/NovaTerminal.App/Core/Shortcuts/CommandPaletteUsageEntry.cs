using System;

namespace NovaTerminal.Core.Shortcuts;

public sealed record CommandPaletteUsageEntry(string CommandId, int UseCount, DateTimeOffset LastUsedAt);
