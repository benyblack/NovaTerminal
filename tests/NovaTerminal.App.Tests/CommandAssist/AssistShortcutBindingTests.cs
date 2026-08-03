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

    /// <summary>
    /// Tab is modelled by <c>AssistKey</c> but is not rebindable (PR #293 review, non-blocking 3).
    /// </summary>
    /// <remarks>
    /// Shell-first Tab is a documented promise: the keyboard table says Command Assist never takes it,
    /// because at a shell prompt it is the completion key. Accepting an override that names it would let a
    /// settings edit break that silently - the router would start claiming Tab and the shell would stop
    /// seeing it.
    /// </remarks>
    [Theory]
    [InlineData("Tab")]
    [InlineData("Ctrl+Tab")]
    [InlineData("Shift+Tab")]
    public void Resolve_WithARebindingToTab_FallsBackToTheDefault(string binding)
    {
        Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["command_assist_selection_down"] = binding,
        };

        AssistShortcutBindings resolved = AssistShortcutBindingResolver.Resolve(overrides);

        Assert.Equal(AssistKeyBindings.Default.SelectionDown, resolved.Keys.SelectionDown);
        Assert.Equal("Down", resolved.HintLabels.SelectionDown);
        Assert.NotEqual(AssistKey.Tab, resolved.Keys.SelectionDown.Key);
    }

    /// <summary>
    /// The hint strip and the Settings shortcut list write the same chord the same way (PR #293 review,
    /// non-blocking 5): both go through <see cref="ShortcutMatcher.Format"/>.
    /// </summary>
    /// <remarks>
    /// The hand-rolled label builder this replaced fell back to <c>Key.ToString()</c>, so a rebind to
    /// <c>Ctrl+1</c> was advertised as "Ctrl+D1" - a chord the user never typed and Settings never shows.
    /// </remarks>
    [Theory]
    [InlineData("Ctrl+1", "Ctrl+1")]
    [InlineData("Alt+9", "Alt+9")]
    [InlineData("Ctrl+,", "Ctrl+,")]
    [InlineData("Ctrl+Space", "Ctrl+Space")]
    [InlineData("Ctrl+Shift+F5", "Ctrl+Shift+F5")]
    public void Resolve_LabelsARebindTheWayTheShortcutEditorWouldWriteIt(string binding, string expectedLabel)
    {
        Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["command_assist_insert"] = binding,
        };

        AssistShortcutBindings resolved = AssistShortcutBindingResolver.Resolve(overrides);

        // Unrepresentable as an assist key, so the *binding* falls back - but the point here is the
        // label formatter, which is exercised by the representable cases below and by the round trip.
        Assert.True(ShortcutMatcher.TryParse(binding, out Key key, out KeyModifiers modifiers));
        Assert.Equal(expectedLabel, ShortcutMatcher.Format(key, modifiers));
        Assert.Equal(AssistKeyBindings.Default.Insert, resolved.Keys.Insert);
    }

    /// <summary>
    /// The two display overrides survive the shared formatter: nobody writes "Escape" in a hint strip,
    /// and <c>Key.Enter</c> is an alias for <c>Key.Return</c>, which the canonical form spells "Return".
    /// </summary>
    [Fact]
    public void Resolve_KeepsTheHintStripSpellingsForEscapeAndEnter()
    {
        Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["command_assist_dismiss"] = "Shift+Escape",
            ["command_assist_insert"] = "Alt+Enter",
        };

        AssistShortcutBindings resolved = AssistShortcutBindingResolver.Resolve(overrides);

        Assert.Equal("Shift+Esc", resolved.HintLabels.Dismiss);
        Assert.Equal("Alt+Enter", resolved.HintLabels.Insert);
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
