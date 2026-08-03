using Avalonia.Input;
using NovaTerminal.CommandAssist.Application;
using NovaTerminal.CommandAssist.ViewModels;
using NovaTerminal.Controls;
using NovaTerminal.Shell.Shortcuts;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// V2 Phase 3b task 2: the in-surface Command Assist keys are catalogued and rebindable, pin has its
/// own chord, and the hint strip reads its key names from the resolved bindings rather than from
/// literals.
/// </summary>
public sealed class AssistShortcutBindingTests
{
    // ---------------------------------------------------------------- the catalogue

    [Fact]
    public void Catalog_CarriesEveryCommandAssistBinding()
    {
        IReadOnlyList<ShortcutDefinition> definitions = ShortcutCatalog.GetDefinitions();

        foreach (string commandId in new[]
                 {
                     "command_assist_pin",
                     "command_assist_dismiss",
                     "command_assist_selection_up",
                     "command_assist_selection_down",
                     "command_assist_accept",
                     "command_assist_insert",
                 })
        {
            Assert.Contains(
                definitions,
                definition => definition.CommandId == commandId &&
                              definition.Scope == ShortcutScope.CommandAssist);
        }
    }

    /// <summary>
    /// The collision the phase existed to fix: pin is off the command palette's chord, and the
    /// palette still owns it.
    /// </summary>
    [Fact]
    public void Catalog_KeepsPinOffTheCommandPaletteChord()
    {
        IReadOnlyList<ShortcutCatalogEntry> entries = ShortcutCatalog.GetEntries();

        ShortcutCatalogEntry pin = Assert.Single(entries, entry => entry.CommandId == "command_assist_pin");
        ShortcutCatalogEntry palette = Assert.Single(entries, entry => entry.CommandId == "command_palette");

        Assert.Equal("Ctrl+Shift+S", pin.DefaultBinding);
        Assert.Equal("Ctrl+Shift+P", palette.DefaultBinding);
    }

    /// <summary>
    /// Every default in the catalogue has to be unique, or the Settings shortcut editor reports a
    /// conflict on a file the user never touched. This is the check the new entries needed.
    /// </summary>
    [Fact]
    public void Catalog_HasNoConflictingDefaults()
    {
        ShortcutBindingResolution resolution = ShortcutBindingResolver.Resolve(
            ShortcutCatalog.GetDefinitions(),
            overrides: null);

        Assert.True(
            resolution.IsValid,
            "Conflicting default bindings: " +
            string.Join(", ", resolution.Conflicts.Select(conflict => conflict.NormalizedBinding)));
    }

    // ---------------------------------------------------------------- migration

    /// <summary>
    /// An existing user shortcut file has none of the new ids and may carry ids no build recognizes.
    /// Resolution iterates definitions rather than overrides, so a new id takes its default and a
    /// stale one is ignored - the migration story is "there is nothing to migrate", and this pins it.
    /// </summary>
    [Fact]
    public void Resolve_WithAPreExistingOverrideFile_TakesDefaultsForTheNewIdsAndIgnoresStaleOnes()
    {
        Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["command_palette"] = "Ctrl+Shift+P",
            ["a_command_that_no_longer_exists"] = "Ctrl+Shift+Q",
        };

        ShortcutBindingResolution resolution = ShortcutBindingResolver.Resolve(
            ShortcutCatalog.GetDefinitions(),
            overrides);

        Assert.True(resolution.IsValid);
        Assert.Contains(
            resolution.Bindings,
            binding => binding.CommandId == "command_assist_pin" && binding.Binding == "Ctrl+Shift+S");
        Assert.DoesNotContain(
            resolution.Bindings,
            binding => binding.CommandId == "a_command_that_no_longer_exists");
    }

    // ---------------------------------------------------------------- resolution to assist chords

    [Fact]
    public void Resolve_WithNoOverrides_ProducesTheShippedKeyboard()
    {
        AssistShortcutBindings resolved = AssistShortcutBindingResolver.Resolve(overrides: null);

        Assert.Equal(AssistKeyBindings.Default, resolved.Keys);
        Assert.Equal(AssistShortcutHintLabels.Default, resolved.HintLabels);
    }

    [Fact]
    public void Resolve_WithARebindingOfAccept_MovesBothTheChordAndTheLabel()
    {
        Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["command_assist_accept"] = "Ctrl+Shift+Enter",
        };

        AssistShortcutBindings resolved = AssistShortcutBindingResolver.Resolve(overrides);

        Assert.Equal(
            new AssistKeyBinding(AssistKey.Enter, AssistModifiers.Control | AssistModifiers.Shift),
            resolved.Keys.Accept);
        Assert.Equal("Ctrl+Shift+Enter", resolved.HintLabels.Accept);
    }

    /// <summary>
    /// Command Assist models five keys, so a rebind to anything else cannot be routed. Falling back
    /// to the default keeps the key working; passing the unrepresentable chord through would leave the
    /// user with no dismiss key at all.
    /// </summary>
    [Fact]
    public void Resolve_WithAnUnrepresentableRebinding_FallsBackToTheDefault()
    {
        Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["command_assist_dismiss"] = "Ctrl+J",
        };

        AssistShortcutBindings resolved = AssistShortcutBindingResolver.Resolve(overrides);

        Assert.Equal(AssistKeyBindings.Default.Dismiss, resolved.Keys.Dismiss);
        Assert.Equal("Esc", resolved.HintLabels.Dismiss);
    }

    [Fact]
    public void Resolve_WithAMalformedRebinding_FallsBackToTheDefault()
    {
        Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["command_assist_selection_down"] = "Ctrl+++",
        };

        AssistShortcutBindings resolved = AssistShortcutBindingResolver.Resolve(overrides);

        Assert.Equal(AssistKeyBindings.Default.SelectionDown, resolved.Keys.SelectionDown);
    }

    /// <summary>Nobody writes "Escape" in a hint strip.</summary>
    [Fact]
    public void Resolve_SpellsTheDismissKeyTheWayTerminalsDo()
    {
        AssistShortcutBindings resolved = AssistShortcutBindingResolver.Resolve(overrides: null);

        Assert.Equal("Esc", resolved.HintLabels.Dismiss);
    }

    [Fact]
    public void ShortcutMatcher_TryParse_SharesTheBindingVocabulary()
    {
        Assert.True(ShortcutMatcher.TryParse("Ctrl+Enter", out Key key, out KeyModifiers modifiers));
        Assert.Equal(Key.Enter, key);
        Assert.Equal(KeyModifiers.Control, modifiers);

        Assert.False(ShortcutMatcher.TryParse("Ctrl+NotAKey", out _, out _));
    }

    // ---------------------------------------------------------------- the hint strip

    /// <summary>
    /// With the default labels every hint state renders the exact string Phase 3a shipped, so making
    /// the key names variable changed no pixels for a user with no overrides.
    /// </summary>
    [Theory]
    [InlineData(true, true, "Enter insert  |  Up/Down browse  |  Esc close")]
    [InlineData(false, true, "Up/Down browse  |  Ctrl+Enter insert  |  Esc close")]
    [InlineData(false, false, "Down browse  |  Ctrl+Enter insert  |  Esc close")]
    public void HintStrip_WithDefaultLabels_RendersTheShippedStrings(
        bool acceptArmed,
        bool selectionUpOwned,
        string expected)
    {
        var viewModel = new CommandAssistBarViewModel
        {
            IsVisible = true,
        };
        SetProbes(viewModel, acceptArmed, selectionUpOwned);

        Assert.Equal(expected, viewModel.Bubble.ShortcutHintText);
        Assert.Equal(expected, viewModel.Popup.ShortcutHintText);
    }

    /// <summary>
    /// The property the task line asked for: rebind a key and the strip stops advertising the old one.
    /// </summary>
    [Fact]
    public void HintStrip_WithReboundLabels_AdvertisesTheNewKeys()
    {
        var viewModel = new CommandAssistBarViewModel
        {
            IsVisible = true,
        };
        SetProbes(viewModel, acceptArmed: true, selectionUpOwned: true);

        viewModel.ShortcutHintLabels = new AssistShortcutHintLabels(
            Accept: "Ctrl+Shift+Enter",
            SelectionUp: "Up",
            SelectionDown: "Down",
            Insert: "Ctrl+Enter",
            Dismiss: "Ctrl+Esc");

        Assert.Equal("Ctrl+Shift+Enter insert  |  Up/Down browse  |  Ctrl+Esc close", viewModel.Bubble.ShortcutHintText);
    }

    /// <summary>
    /// The controller is the one that installs them in production, so the seam is worth one test of
    /// its own - the App resolves, the controller pushes, the strip renders.
    /// </summary>
    [Fact]
    public void Controller_SetShortcutHintLabels_ReachesTheStrip()
    {
        CommandAssistController controller = CreateController();
        controller.ToggleAssist();

        controller.SetShortcutHintLabels(new AssistShortcutHintLabels(Dismiss: "Ctrl+Esc"));

        Assert.Contains("Ctrl+Esc close", controller.ViewModel.Bubble.ShortcutHintText);
    }

    private static void SetProbes(
        CommandAssistBarViewModel viewModel,
        bool acceptArmed,
        bool selectionUpOwned)
    {
        // The probes are internal, which is exactly how the controller installs them; setting them
        // here reproduces the three surface states without building a whole session.
        viewModel.AcceptOnEnterProbe = () => acceptArmed;
        viewModel.SelectionUpOwnedProbe = () => selectionUpOwned;
        viewModel.SyncPresentationState();
    }

    private static CommandAssistController CreateController()
    {
        return new CommandAssistController(
            new NoHistoryStore(),
            new NovaTerminal.CommandAssist.Domain.SecretsFilter(),
            new NovaTerminal.CommandAssist.Domain.CommandAssistSuggestionEngine(),
            snippetStore: null,
            commandDocsProvider: null,
            recipeProvider: null,
            errorInsightService: null,
            modeRouter: null,
            resultBuilder: null);
    }

    private sealed class NoHistoryStore : NovaTerminal.CommandAssist.Domain.IHistoryStore
    {
        public Task AppendAsync(NovaTerminal.CommandAssist.Models.CommandHistoryEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<NovaTerminal.CommandAssist.Models.CommandHistoryEntry>> GetRecentAsync(int maxResults, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NovaTerminal.CommandAssist.Models.CommandHistoryEntry>>(Array.Empty<NovaTerminal.CommandAssist.Models.CommandHistoryEntry>());

        public Task<IReadOnlyList<NovaTerminal.CommandAssist.Models.CommandHistoryEntry>> SearchAsync(string query, int maxCandidates, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NovaTerminal.CommandAssist.Models.CommandHistoryEntry>>(Array.Empty<NovaTerminal.CommandAssist.Models.CommandHistoryEntry>());

        public Task<bool> TryUpdateExecutionResultAsync(string entryId, int? exitCode, long? durationMs, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
