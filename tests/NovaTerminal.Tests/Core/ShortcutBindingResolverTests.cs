using NovaTerminal.Core.Shortcuts;

namespace NovaTerminal.Tests.Core;

public sealed class ShortcutBindingResolverTests
{
    [Fact]
    public void Resolve_WhenOverridesCreateCrossScopeDuplicate_ReturnsInvalidConflict()
    {
        ShortcutDefinition[] definitions =
        [
            new("settings", ShortcutScope.App, "Ctrl+,"),
            new("command_assist_help", ShortcutScope.CommandAssist, "Ctrl+Shift+H"),
        ];

        Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["settings"] = "Ctrl+Alt+S",
            ["command_assist_help"] = "Ctrl+Alt+S",
        };

        ShortcutBindingResolution resolution = ShortcutBindingResolver.Resolve(definitions, overrides);

        Assert.False(resolution.IsValid);

        ShortcutBindingConflict conflict = Assert.Single(resolution.Conflicts);
        Assert.Equal("Ctrl+Alt+S", conflict.NormalizedBinding);

        Assert.Equal(
            ["settings", "command_assist_help"],
            conflict.Bindings.Select(binding => binding.CommandId).ToArray());
        Assert.Equal(
            [ShortcutScope.App, ShortcutScope.CommandAssist],
            conflict.Bindings.Select(binding => binding.Scope).ToArray());
        Assert.All(conflict.Bindings, binding => Assert.Equal("Ctrl+Alt+S", binding.Binding));
    }

    [Fact]
    public void Normalize_TreatsOemPlusNameAndAliasAsSameBinding()
    {
        Assert.Equal(
            ShortcutNormalizer.Normalize("Ctrl+OemPlus"),
            ShortcutNormalizer.Normalize("Ctrl+Plus"));
    }

    [Theory]
    [InlineData("Ctrl++", "Ctrl+OemPlus")]
    [InlineData("Ctrl+-", "Ctrl+OemMinus")]
    public void Normalize_RewritesTrailingSymbolShortcutSyntax(string shortcut, string expected)
    {
        Assert.Equal(expected, ShortcutNormalizer.Normalize(shortcut));
    }

    [Theory]
    [InlineData("Ctrl++A")]
    [InlineData("+A")]
    public void Normalize_WhenSeparatorUsageIsMalformed_ThrowsArgumentException(string shortcut)
    {
        Assert.Throws<ArgumentException>(() => ShortcutNormalizer.Normalize(shortcut));
    }

    [Fact]
    public void Normalize_WhenKeyTokenIsUnknown_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ShortcutNormalizer.Normalize("Ctrl+LaunchMissiles"));
    }

    [Theory]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Alt+Alt+S")]
    [InlineData("Shift+Shift+Tab")]
    public void Normalize_WhenModifierIsRepeated_ThrowsArgumentException(string shortcut)
    {
        Assert.Throws<ArgumentException>(() => ShortcutNormalizer.Normalize(shortcut));
    }

    [Fact]
    public void ShortcutDefinition_WhenCommandIdIsEmpty_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new ShortcutDefinition("", ShortcutScope.App, "Ctrl+,"));
    }

    [Fact]
    public void ShortcutBindingRecord_WhenBindingIsEmpty_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new ShortcutBindingRecord("settings", ShortcutScope.App, ""));
    }

    [Fact]
    public void Resolve_WhenOverrideIsInvalid_FallsBackToDefaultBinding()
    {
        ShortcutDefinition[] definitions =
        [
            new("settings", ShortcutScope.App, "Ctrl+,"),
            new("command_assist_help", ShortcutScope.CommandAssist, "Ctrl+Shift+H"),
        ];

        Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["settings"] = "Ctrl+LaunchMissiles",
        };

        ShortcutBindingResolution resolution = ShortcutBindingResolver.Resolve(definitions, overrides);

        Assert.True(resolution.IsValid);
        ShortcutBindingRecord settings = Assert.Single(resolution.Bindings, binding => binding.CommandId == "settings");
        Assert.Equal("Ctrl+,", settings.Binding);
    }
}
