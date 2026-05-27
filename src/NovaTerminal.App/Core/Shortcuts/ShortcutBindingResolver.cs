using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaTerminal.Core.Shortcuts;

public static class ShortcutBindingResolver
{
    public static ShortcutBindingResolution Resolve(
        IReadOnlyCollection<ShortcutDefinition> definitions,
        IReadOnlyDictionary<string, string>? overrides)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        List<ShortcutBindingRecord> bindings = new(definitions.Count);
        foreach (ShortcutDefinition definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);

            string binding = definition.DefaultBinding;
            if (overrides is not null &&
                overrides.TryGetValue(definition.CommandId, out string? overrideBinding) &&
                !string.IsNullOrWhiteSpace(overrideBinding))
            {
                binding = overrideBinding;
            }

            string normalizedBinding;
            try
            {
                normalizedBinding = ShortcutNormalizer.Normalize(binding);
            }
            catch (ArgumentException) when (!string.Equals(binding, definition.DefaultBinding, StringComparison.Ordinal))
            {
                normalizedBinding = ShortcutNormalizer.Normalize(definition.DefaultBinding);
            }

            bindings.Add(new ShortcutBindingRecord(
                definition.CommandId,
                definition.Scope,
                normalizedBinding));
        }

        List<ShortcutBindingConflict> conflicts = bindings
            .GroupBy(binding => binding.Binding, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => new ShortcutBindingConflict(group.Key, group.ToArray()))
            .ToList();

        return new ShortcutBindingResolution(bindings, conflicts);
    }
}

public sealed record ShortcutBindingResolution(
    IReadOnlyList<ShortcutBindingRecord> Bindings,
    IReadOnlyList<ShortcutBindingConflict> Conflicts)
{
    public bool IsValid => Conflicts.Count == 0;
}

public sealed record ShortcutBindingConflict(
    string NormalizedBinding,
    IReadOnlyList<ShortcutBindingRecord> Bindings);
