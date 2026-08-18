using NovaTerminal.Shell;
using NovaTerminal.Pty;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using NovaTerminal.Controls;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Shell.Shortcuts;
using System.Reflection;

namespace NovaTerminal.Tests.Core;

public sealed class MainWindowStartupTests
{
    [AvaloniaFact]
    public void MainWindow_CanBeConstructed()
    {
        var window = TestMainWindowFactory.Create();

        Assert.NotNull(window);
    }

    [AvaloniaFact]
    public void MainWindow_LoadsWindowIconOnlyAfterDeferredHookRuns()
    {
        var window = TestMainWindowFactory.Create();
        var ensureWindowIconLoadedMethod = typeof(NovaTerminal.MainWindow).GetMethod("EnsureWindowIconLoaded", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(ensureWindowIconLoadedMethod);
        Assert.Null(window.Icon);

        ensureWindowIconLoadedMethod!.Invoke(window, null);

        Assert.NotNull(window.Icon);
    }

    [AvaloniaFact]
    public void RegisterPaneOwners_TraversesDecoratorWrappedPane()
    {
        var window = TestMainWindowFactory.Create();
        var registerPaneOwnersMethod = typeof(NovaTerminal.MainWindow).GetMethod("RegisterPaneOwners", BindingFlags.Instance | BindingFlags.NonPublic);
        var paneOwnerField = typeof(NovaTerminal.MainWindow).GetField("_paneOwnerTab", BindingFlags.Instance | BindingFlags.NonPublic);
        var pane = new NovaTerminal.Controls.TerminalPane();
        var tab = new TabItem { Content = new Border { Child = pane } };

        try
        {
            Assert.NotNull(registerPaneOwnersMethod);
            Assert.NotNull(paneOwnerField);

            registerPaneOwnersMethod!.Invoke(window, new object[] { tab, (Control)tab.Content! });

            var paneOwners = Assert.IsAssignableFrom<System.Collections.IDictionary>(paneOwnerField!.GetValue(window));
            Assert.True(paneOwners.Contains(pane));
            Assert.Same(tab, paneOwners[pane]);
        }
        finally
        {
            pane.Dispose();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MainWindow_UsesPaletteForSettingsAndOpenRecording_NotTitleBarButtons()
    {
        CommandRegistry.Clear();
        var window = TestMainWindowFactory.Create();
        var toggleMethod = typeof(NovaTerminal.MainWindow).GetMethod("ToggleCommandPalette", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Null(window.FindControl<Button>("SettingsBtn"));
        Assert.Null(window.FindControl<Button>("BtnOpenRec"));

        Assert.DoesNotContain(CommandRegistry.GetCommands(), command => command.Title == "Settings");

        toggleMethod!.Invoke(window, null);

        var commands = CommandRegistry.GetCommands();
        Assert.Contains(commands, command => command.Title == "Settings");
        Assert.Contains(commands, command => command.Title == "Open Recording...");
        Assert.Contains(commands, command => command.Title == "Open Recordings Folder");
    }

    [AvaloniaFact]
    public void MainWindow_CommandPaletteShowsSettingsShortcut()
    {
        CommandRegistry.Clear();
        var window = new NovaTerminal.MainWindow();
        var toggleMethod = typeof(NovaTerminal.MainWindow).GetMethod("ToggleCommandPalette", BindingFlags.Instance | BindingFlags.NonPublic);

        toggleMethod!.Invoke(window, null);

        TerminalCommand settingsCommand = Assert.Single(CommandRegistry.GetCommands().Where(command => command.Title == "Settings"));
        Assert.Equal("Ctrl+,", settingsCommand.Shortcut);
    }

    [AvaloniaFact]
    public void MainWindow_CommandPaletteIncludesConnections()
    {
        CommandRegistry.Clear();
        var window = new NovaTerminal.MainWindow();
        var toggleMethod = typeof(NovaTerminal.MainWindow).GetMethod("ToggleCommandPalette", BindingFlags.Instance | BindingFlags.NonPublic);

        toggleMethod!.Invoke(window, null);

        Assert.Contains(CommandRegistry.GetCommands(), command => command.Id == "connections" && command.Title == "Connections");
    }

    [AvaloniaFact]
    public void MainWindow_CommandPalettePrefersMostUsedCommandsWhenOpened()
    {
        CommandRegistry.Clear();
        var window = new NovaTerminal.MainWindow();
        var toggleMethod = typeof(NovaTerminal.MainWindow).GetMethod("ToggleCommandPalette", BindingFlags.Instance | BindingFlags.NonPublic);
        var usageField = typeof(NovaTerminal.MainWindow).GetField("_commandPaletteUsage", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(usageField);

        usageField!.SetValue(
            window,
            new Dictionary<string, CommandPaletteUsageEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["settings"] = new("settings", 8, new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero)),
            });

        toggleMethod!.Invoke(window, null);

        var commandList = window.FindControl<ListBox>("CommandList");
        IReadOnlyList<TerminalCommand> commands = Assert.IsAssignableFrom<IEnumerable<TerminalCommand>>(commandList!.ItemsSource).ToList();

        Assert.Equal("settings", commands[0].Id);
    }

    [AvaloniaFact]
    public async Task MainWindow_CustomCommandAssistHelpShortcut_UsesConfiguredBinding()
    {
        var window = new NovaTerminal.MainWindow();
        var settingsField = typeof(NovaTerminal.MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        var currentPaneField = typeof(NovaTerminal.MainWindow).GetField("_currentPaneValue", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(settingsField);
        Assert.NotNull(currentPaneField);

        var settings = (TerminalSettings)settingsField!.GetValue(window)!;
        settings.CommandAssistEnabled = true;
        settings.CommandAssistHistoryEnabled = true;
        settings.Keybindings["command_assist_help"] = "Ctrl+Alt+H";

        using var pane = new TerminalPane();
        pane.CommandAssistServices = TestCommandAssistServices.Instance;
        pane.ApplySettings(settings);
        pane.NotifyCommandAssistPaste("git checkout");

        currentPaneField!.SetValue(window, pane);

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.H,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Alt,
            Source = window,
        };

        window.RaiseEvent(args);

        Assert.True(args.Handled);

        // Pumped until the surface appears rather than once (V2 Phase 4b). Opening Help used to be
        // synchronous in all but name - the seven-command seed providers returned completed tasks, so
        // the dispatch ran inline and one background pump was enough. CommandKnowledgeService reads an
        // 825 KB catalogue off a worker on first use, so the surface now legitimately arrives a few
        // milliseconds later, and a single pump was testing the old providers' shape rather than the
        // shortcut this test is about.
        Assert.True(await WaitForAsync(() => pane.CommandAssistViewModel?.IsVisible ?? false));
    }

    /// <summary>
    /// Pumps the dispatcher until <paramref name="condition"/> holds or the budget runs out.
    /// </summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, int attempts = 200)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (condition())
            {
                return true;
            }

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(5);
        }

        return condition();
    }

    public async Task ExecuteCommand_DefersActionUntilAfterPaletteCloses()
    {
        var window = TestMainWindowFactory.Create();
        var toggleMethod = typeof(NovaTerminal.MainWindow).GetMethod("ToggleCommandPalette", BindingFlags.Instance | BindingFlags.NonPublic);
        var executeMethod = typeof(NovaTerminal.MainWindow).GetMethod("ExecuteCommand", BindingFlags.Instance | BindingFlags.NonPublic);

        toggleMethod!.Invoke(window, null);
        var overlay = window.FindControl<Grid>("CommandPaletteOverlay");
        Assert.NotNull(overlay);
        Assert.True(overlay!.IsVisible);

        bool actionRan = false;
        bool overlayWasClosedWhenActionRan = false;
        var command = new TerminalCommand
        {
            Title = "Test Deferred Command",
            Category = "Test",
            Action = () =>
            {
                actionRan = true;
                overlayWasClosedWhenActionRan = !overlay.IsVisible;
            }
        };

        executeMethod!.Invoke(window, new object[] { command });

        Assert.False(actionRan);
        Assert.False(overlay.IsVisible);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.True(actionRan);
        Assert.True(overlayWasClosedWhenActionRan);
    }

    [AvaloniaFact]
    public async Task OpenRecordingPaletteCommand_InvokesAsyncWindowHook()
    {
        CommandRegistry.Clear();
        var window = new RecordingCommandProbeWindow();
        var toggleMethod = typeof(NovaTerminal.MainWindow).GetMethod("ToggleCommandPalette", BindingFlags.Instance | BindingFlags.NonPublic);
        var executeMethod = typeof(NovaTerminal.MainWindow).GetMethod("ExecuteCommand", BindingFlags.Instance | BindingFlags.NonPublic);

        toggleMethod!.Invoke(window, null);
        var command = CommandRegistry.GetCommands().Single(c => c.Title == "Open Recording...");

        executeMethod!.Invoke(window, new object[] { command });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.True(window.WasOpenRecordingInvoked);
    }

    [Theory]
    [InlineData(true, @"C:\Users\behna\AppData\Local\NovaTerminal\recordings\nova.rec", @"C:\Users\behna\AppData\Local\NovaTerminal\recordings", "explorer.exe", "/select,")]
    [InlineData(true, null, @"C:\Users\behna\AppData\Local\NovaTerminal\recordings", @"C:\Users\behna\AppData\Local\NovaTerminal\recordings", "")]
    [InlineData(false, "/tmp/nova/recordings/nova.rec", "/tmp/nova/recordings", "/tmp/nova/recordings", "")]
    public void ResolveRecordingRevealRequest_PrefersExactFileOnWindows(
        bool isWindows,
        string? filePath,
        string recordingsDirectory,
        string expectedFileName,
        string expectedArgumentsPrefix)
    {
        var request = NovaTerminal.MainWindow.ResolveRecordingRevealRequest(filePath, recordingsDirectory, isWindows);

        Assert.Equal(expectedFileName, request.FileName);
        if (string.IsNullOrEmpty(expectedArgumentsPrefix))
        {
            Assert.True(string.IsNullOrEmpty(request.Arguments));
        }
        else
        {
            Assert.StartsWith(expectedArgumentsPrefix, request.Arguments, StringComparison.Ordinal);
            Assert.Contains("nova.rec", request.Arguments, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public void ApplyThemeToUi_LightTheme_UpdatesTabListAndIdleRecordForeground()
    {
        var window = TestMainWindowFactory.Create();
        var settingsField = typeof(NovaTerminal.MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        var applyThemeMethod = typeof(NovaTerminal.MainWindow).GetMethod("ApplyThemeToUI", BindingFlags.Instance | BindingFlags.NonPublic);
        var settings = (TerminalSettings)settingsField!.GetValue(window)!;

        settings.ThemeName = "Test Light";
        settings.ActiveTheme = new TerminalTheme
        {
            Name = "Test Light",
            Background = TermColor.FromRgb(245, 240, 225),
            Foreground = TermColor.Black
        };

        applyThemeMethod!.Invoke(window, null);

        var expected = Colors.Black;
        var btnTabList = window.FindControl<Button>("BtnTabList");
        var iconTabList = window.FindControl<PathIcon>("IconTabList");
        var btnRecord = window.FindControl<Button>("BtnRecord");
        var iconRecord = window.FindControl<PathIcon>("IconRecord");

        Assert.NotNull(btnTabList);
        Assert.NotNull(iconTabList);
        Assert.NotNull(btnRecord);
        Assert.NotNull(iconRecord);
        Assert.Equal(expected, ((ISolidColorBrush)btnTabList!.Foreground!).Color);
        Assert.Equal(expected, ((ISolidColorBrush)iconTabList!.Foreground!).Color);
        Assert.Equal(expected, ((ISolidColorBrush)btnRecord!.Foreground!).Color);
        Assert.Equal(expected, ((ISolidColorBrush)iconRecord!.Foreground!).Color);
    }

    [AvaloniaFact]
    public void ApplyThemeToUi_LightTheme_UpdatesCommandPaletteSearchForeground()
    {
        var window = TestMainWindowFactory.Create();
        var settingsField = typeof(NovaTerminal.MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        var applyThemeMethod = typeof(NovaTerminal.MainWindow).GetMethod("ApplyThemeToUI", BindingFlags.Instance | BindingFlags.NonPublic);
        var toggleMethod = typeof(NovaTerminal.MainWindow).GetMethod("ToggleCommandPalette", BindingFlags.Instance | BindingFlags.NonPublic);
        var settings = (TerminalSettings)settingsField!.GetValue(window)!;

        settings.ThemeName = "Test Light";
        settings.ActiveTheme = new TerminalTheme
        {
            Name = "Test Light",
            Background = TermColor.FromRgb(245, 240, 225),
            Foreground = TermColor.Black
        };

        toggleMethod!.Invoke(window, null);
        applyThemeMethod!.Invoke(window, null);

        var searchBox = window.FindControl<TextBox>("CommandSearchBox");

        Assert.NotNull(searchBox);
        Assert.Equal(Colors.Black, ((ISolidColorBrush)searchBox!.Foreground!).Color);
    }

    [AvaloniaFact]
    public void ApplySplitterVisualState_LightTheme_StrengthensLineOnHoverAndDrag()
    {
        var window = TestMainWindowFactory.Create();
        var settingsField = typeof(NovaTerminal.MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        var applySplitterVisualStateMethod = typeof(NovaTerminal.MainWindow).GetMethod("ApplySplitterVisualState", BindingFlags.Instance | BindingFlags.NonPublic);
        var settings = (TerminalSettings)settingsField!.GetValue(window)!;

        settings.ThemeName = "Test Light";
        settings.ActiveTheme = new TerminalTheme
        {
            Name = "Test Light",
            Background = TermColor.FromRgb(245, 240, 225),
            Foreground = TermColor.Black
        };

        var splitter = new GridSplitter();

        applySplitterVisualStateMethod!.Invoke(window, new object[] { splitter });
        var idleColor = ((ISolidColorBrush)splitter.Background!).Color;

        splitter.Classes.Add("splitter-hover");
        applySplitterVisualStateMethod.Invoke(window, new object[] { splitter });
        var hoverColor = ((ISolidColorBrush)splitter.Background!).Color;

        splitter.Classes.Remove("splitter-hover");
        splitter.Classes.Add("splitter-dragging");
        applySplitterVisualStateMethod.Invoke(window, new object[] { splitter });
        var draggingColor = ((ISolidColorBrush)splitter.Background!).Color;

        Assert.Equal(Colors.Black.R, idleColor.R);
        Assert.Equal(Colors.Black.G, idleColor.G);
        Assert.Equal(Colors.Black.B, idleColor.B);
        Assert.True(idleColor.A < hoverColor.A);
        Assert.True(hoverColor.A < draggingColor.A);
    }


    /// <summary>
    /// Deferred startup restore hydrates the placeholder that belongs to a saved tab, not
    /// whatever tab currently occupies that tab's original index (#326 review, P1).
    ///
    /// Restore materializes every saved tab up front — the selected one live, the rest as
    /// placeholders — then hydrates the placeholders on a later background dispatcher pass.
    /// Anything that removes a tab in between shifts every later index by one, so an
    /// index-addressed lookup either writes a tab's content into its neighbour's placeholder
    /// or falls out of range and leaves a permanently blank tab. Since ShellExitPolicy
    /// defaults to "Graceful", the removal is reachable without the user doing anything: the
    /// restored selected tab's shell exits 0 during startup and the pane closes itself, and
    /// the exit is posted at normal dispatcher priority while hydration is posted at
    /// background priority, so the close always wins the race.
    /// </summary>
    [AvaloniaFact]
    public void DeferredHydration_AfterAnEarlierTabClosed_StillReachesTheLastTabsPlaceholder()
    {
        var window = TestMainWindowFactory.Create();
        try
        {
            TabControl tabs = window.FindControl<TabControl>("Tabs")!;
            var (immediate, placeholderB, placeholderC, sessionB, sessionC) = BuildRestoredTabStrip(tabs);
            object? contentBeforeB = placeholderB.Content;
            object? contentBeforeC = placeholderC.Content;

            // The selected tab's shell exited 0 and the pane closed itself.
            tabs.Items.Remove(immediate);

            // Its own OriginalIndex is now out of range: 2 items left, index 2.
            HydrateDeferred(window, tabs, new StartupRestoreTab(2, sessionC));

            Assert.NotSame(contentBeforeC, placeholderC.Content);
            Assert.Same(contentBeforeB, placeholderB.Content);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The other half of the same shift: an index that still resolves now resolves to the
    /// wrong tab, which is worse than the blank tab above because it looks like a success.
    /// </summary>
    [AvaloniaFact]
    public void DeferredHydration_AfterAnEarlierTabClosed_DoesNotHydrateTheNeighbourAtThatIndex()
    {
        var window = TestMainWindowFactory.Create();
        try
        {
            TabControl tabs = window.FindControl<TabControl>("Tabs")!;
            var (immediate, placeholderB, placeholderC, sessionB, _) = BuildRestoredTabStrip(tabs);
            object? contentBeforeB = placeholderB.Content;
            object? contentBeforeC = placeholderC.Content;

            tabs.Items.Remove(immediate);

            // Index 1 now holds C's placeholder, so an index lookup would hydrate C with B's
            // session and leave B blank forever.
            HydrateDeferred(window, tabs, new StartupRestoreTab(1, sessionB));

            Assert.NotSame(contentBeforeB, placeholderB.Content);
            Assert.Same(contentBeforeC, placeholderC.Content);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Three restored tabs in saved order: index 0 selected (live), 1 and 2 placeholders
    /// carrying their <see cref="TabSession"/> in Tag exactly as CreateStartupPlaceholderTab does.
    /// </summary>
    private static (TabItem Immediate, TabItem PlaceholderB, TabItem PlaceholderC, TabSession SessionB, TabSession SessionC)
        BuildRestoredTabStrip(TabControl tabs)
    {
        TabSession sessionA = NewTabSession("A");
        TabSession sessionB = NewTabSession("B");
        TabSession sessionC = NewTabSession("C");

        tabs.Items.Clear();
        TabItem immediate = NewPlaceholder(sessionA);
        TabItem placeholderB = NewPlaceholder(sessionB);
        TabItem placeholderC = NewPlaceholder(sessionC);
        tabs.Items.Add(immediate);
        tabs.Items.Add(placeholderB);
        tabs.Items.Add(placeholderC);

        return (immediate, placeholderB, placeholderC, sessionB, sessionC);
    }

    private static TabSession NewTabSession(string title) => new()
    {
        Title = title,
        Root = new PaneNode
        {
            Type = NodeType.Leaf,
            Command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = string.Empty,
            PaneId = Guid.NewGuid().ToString()
        }
    };

    private static TabItem NewPlaceholder(TabSession session) => new()
    {
        Header = new TextBlock { Text = session.Title },
        Content = new Border { Background = Brushes.Transparent },
        Tag = session
    };

    private static void HydrateDeferred(NovaTerminal.MainWindow window, TabControl tabs, StartupRestoreTab deferredTab)
    {
        typeof(NovaTerminal.MainWindow)
            .GetMethod("HydrateDeferredStartupTab", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [tabs, deferredTab]);
    }

    private sealed class RecordingCommandProbeWindow : NovaTerminal.MainWindow
    {
        public bool WasOpenRecordingInvoked { get; private set; }

        protected override Task ExecuteOpenRecordingCommandAsync()
        {
            WasOpenRecordingInvoked = true;
            return Task.CompletedTask;
        }
    }
}
