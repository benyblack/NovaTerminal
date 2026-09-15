using System;

namespace Ntilde.Shell.Shortcuts;

public sealed record CommandPaletteUsageEntry(string CommandId, int UseCount, DateTimeOffset LastUsedAt);
