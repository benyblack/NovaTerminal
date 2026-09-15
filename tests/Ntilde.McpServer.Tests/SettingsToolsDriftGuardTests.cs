using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using NovaTerminal.McpServer.Tools;
using NovaTerminal.Shell;                    // TerminalSettings

namespace NovaTerminal.McpServer.Tests;

/// <summary>
/// Guards against drift between the hand-mirrored field knowledge in <see cref="SettingsTools"/> and the
/// real <see cref="TerminalSettings"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>PR #293 review, non-blocking 6.</strong> <c>SettingsTools</c> validates a settings document
/// against four string lists it maintains by hand, because the MCP server must not reference
/// <c>NovaTerminal.App</c>. Nothing checked those lists against the type they describe, and the
/// consequence was already in the tree: <c>CommandAssistAutoHideInAltScreen</c> stayed in
/// <c>KnownFields</c> after being deleted from <c>TerminalSettings</c>, so the validator went on
/// accepting a setting that no longer existed while reporting nothing. The reverse - a new setting the
/// validator does not know - is worse: it is reported to the user as an unknown field.
/// </para>
/// <para>
/// The same pattern and the same argument as <see cref="ConnectionProfileDriftGuardTests"/>, including
/// the test-only project reference that makes it possible.
/// </para>
/// </remarks>
public class SettingsToolsDriftGuardTests
{
    /// <summary>
    /// The properties <c>System.Text.Json</c> actually writes to <c>settings.json</c>: public, readable,
    /// writable, and not <see cref="JsonIgnoreAttribute"/>-d. That is the set a document validator has an
    /// opinion about.
    /// </summary>
    private static IReadOnlyList<PropertyInfo> SerializedProperties =>
        typeof(TerminalSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

    private static string[] NamesOfType(Func<Type, bool> predicate) =>
        SerializedProperties
            .Where(p => predicate(p.PropertyType))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    private static string[] Sorted(IEnumerable<string> values) =>
        values.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The union list is the one the unknown-field warning is driven from, so it has to be exactly the
    /// serialized surface - no phantoms, no omissions.
    /// </summary>
    [Fact]
    public void KnownFields_AreExactlyTheSerializedSettings()
    {
        string[] expected = Sorted(SerializedProperties.Select(p => p.Name));

        Assert.Equal(expected, Sorted(SettingsTools.KnownFields));
    }

    /// <summary>
    /// Every <see cref="bool"/> setting is type-checked. A new toggle that is not in this list is a field
    /// the validator will accept a string for.
    /// </summary>
    [Fact]
    public void BoolFields_AreExactlyTheBoolSettings()
    {
        Assert.Equal(NamesOfType(t => t == typeof(bool)), Sorted(SettingsTools.BoolFields));
    }

    /// <summary>
    /// Every <see cref="string"/> setting is type-checked. Enum-like strings (<c>CursorStyle</c>,
    /// <c>BlurEffect</c>) are in here too - the validator type-checks them without validating the value,
    /// which is documented on the list itself.
    /// </summary>
    [Fact]
    public void StringFields_AreExactlyTheStringSettings()
    {
        Assert.Equal(NamesOfType(t => t == typeof(string)), Sorted(SettingsTools.StringFields));
    }

    /// <summary>
    /// The collection-shaped settings, minus <c>Keybindings</c>: it serializes as a JSON object rather
    /// than an array, so <c>RequireArray</c> would reject a valid document. Named explicitly here so that
    /// adding a second dictionary setting fails this test instead of silently joining the exemption.
    /// </summary>
    [Fact]
    public void ArrayFields_AreExactlyTheListSettings()
    {
        string[] expected = NamesOfType(IsGenericList);

        Assert.Equal(expected, Sorted(SettingsTools.ArrayFields));
        Assert.DoesNotContain("Keybindings", SettingsTools.ArrayFields);
    }

    private static bool IsGenericList(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
}
