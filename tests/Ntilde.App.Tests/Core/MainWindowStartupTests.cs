using System;
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
using NovaTerminal.Shell.TitleBar;
using System.Reflection;

using Xunit;

namespace NovaTerminal.Tests.Core;

/// <remarks>
/// The <see cref="TestAppDataRoot"/> class fixture is taken for its lifetime, not its value,
/// which is why nothing here reads it. This class redirects the app-data root inside individual
/// tests already, but the tests that only close a window did so against whatever root was
/// current - so it still wrote a session into the developer's real profile and left a file behind
/// that steers the startup path of every window built later in the process (#434). The fixture
/// covers the whole class; the narrower per-test overrides nest inside it safely, since disposing
/// one restores the previous value rather than clearing the variable. Scoped per class to match
/// <c>VerticalTabStripTests</c>, whose remarks explain why per-test roots were rejected.
/// </remarks>
public sealed class MainWindowStartupTests : IDisposable, IClassFixture<TestAppDataRoot>
{
    /// <summary>
    /// Disposes the panes of every window this class asked for, and with them the real shells
    /// behind them. xUnit builds a fresh instance per test, so this runs after each one.
    /// </summary>
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaFact]
    public void MainWindow_CanBeConstructed()
    {
        var window = TestMainWindowFactory.Create();

        Assert.NotNull(window);
    }

    /// <summary>
    /// Guards the settings-bleed pathology (#357's sibling): factory-created windows must take the
    /// deterministic defaults BuildForDesigner puts on AppServiceBundle.Settings, never whatever
    /// settings.json the machine happens to carry. Five MainWindowTitleBarTests failed on any dev
    /// box whose live file said TabStripOrientation=Vertical while CI (no file at all) stayed
    /// green - a revert of the seam only reproduces on such a machine, so this writes the
    /// poisonous file explicitly and fails everywhere.
    /// </summary>
    [AvaloniaFact]
    public void MainWindow_IgnoresOnDiskSettings_UsesBundleDefaults()
    {
        string tempRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"novaterm_settings_bleed_guard_{System.Guid.NewGuid():N}");
        string? previousRoot = System.Environment.GetEnvironmentVariable("NOVATERM_APPDATA_ROOT");
        System.IO.Directory.CreateDirectory(tempRoot);

        try
        {
            System.Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", tempRoot);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(tempRoot, "settings.json"),
                """{"TabStripOrientation":"Vertical","FontSize":99}""");

            var window = TestMainWindowFactory.Create();

            var settings = (TerminalSettings)typeof(NovaTerminal.MainWindow)
                .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;

            Assert.Equal("Horizontal", settings.TabStripOrientation);
            Assert.Equal(14, settings.FontSize);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("NOVATERM_APPDATA_ROOT", previousRoot);
            try { System.IO.Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
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

    /// <summary>
    /// A capture taken while startup restore still has unhydrated placeholders must round-trip
    /// their panes, not persist "no panes" over them (#327).
    /// </summary>
    /// <remarks>
    /// This is a data-loss bug, which is why the assertion is on the pane id rather than merely on
    /// "not null": restore materializes every saved tab up front but hydrates all but the selected
    /// one on a Background-priority pass, and the capture path replaces the session file - so a
    /// capture inside that window used to erase the layout it was about to restore, irrecoverably.
    /// Quitting straight after launch reaches it, because the close path saves the session.
    ///
    /// The window is driven through the real startup path (a session file plus
    /// TestMainWindowFactory.Create) rather than by hand-building placeholders, because the
    /// hand-built version would assert against this test's idea of a placeholder instead of the
    /// one MainWindow actually produces. Nothing pumps the dispatcher here, so the deferred pass
    /// has provably not run - the precondition below states that rather than assuming it.
    /// </remarks>
    [AvaloniaFact]
    public void CaptureSession_DuringDeferredRestore_KeepsTheUnhydratedTabsPanes()
    {
        using var appData = new TestAppDataRoot();
        WriteSavedSession(appData.RootPath, secondTabPaneId: "9f2c7a41-0000-4000-8000-000000000001");

        var window = TestMainWindowFactory.Create();
        var tabs = window.FindControl<TabControl>("Tabs")!;

        // Preconditions: restore ran, and the second tab is still a placeholder. Without both, the
        // capture below would be asserting on nothing.
        Assert.Equal(2, tabs.Items.Count);
        var placeholder = (TabItem)tabs.Items[1]!;
        Assert.IsNotType<NovaTerminal.Controls.TerminalPane>(placeholder.Content);
        Assert.IsNotType<Grid>(placeholder.Content);

        NovaSession captured = SessionManager.CaptureSession(window, tabs);

        Assert.Equal(2, captured.Tabs.Count);
        TabSession capturedPlaceholder = captured.Tabs[1];
        Assert.NotNull(capturedPlaceholder.Root);
        Assert.Equal("9f2c7a41-0000-4000-8000-000000000001", capturedPlaceholder.Root!.PaneId);

        DrainDeferredRestorePlan(window);
    }

    /// <summary>
    /// The other direction, so the fallback above cannot pass by simply always preferring the
    /// restored tree: a tab that DID hydrate is captured from its live panes. The selected tab is
    /// built eagerly by restore, so it is hydrated by the time Create() returns.
    /// </summary>
    [AvaloniaFact]
    public void CaptureSession_CapturesTheLivePaneTree_ForATabThatHydrated()
    {
        using var appData = new TestAppDataRoot();
        WriteSavedSession(appData.RootPath, secondTabPaneId: "9f2c7a41-0000-4000-8000-000000000002");

        var window = TestMainWindowFactory.Create();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var live = (TabItem)tabs.Items[0]!;
        var pane = Assert.IsType<NovaTerminal.Controls.TerminalPane>(live.Content);

        NovaSession captured = SessionManager.CaptureSession(window, tabs);

        // The live pane's own id, which restore assigned from the file - so this also pins that the
        // capture read the control rather than the Tag.
        Assert.Equal(pane.PaneId.ToString(), captured.Tabs[0].Root!.PaneId);

        DrainDeferredRestorePlan(window);
    }

    /// <summary>
    /// The fallback must not reach past the pane tree: a broadcast toggle the user made on a tab
    /// that has not hydrated yet survives the capture.
    /// </summary>
    /// <remarks>
    /// Found by review of the first cut of this fix, which carried BroadcastInputEnabled through
    /// from Tag along with the pane fields and so reverted the toggle. It is the one runtime flag
    /// here that a placeholder genuinely owns: InitializeRestoredTabs applies the saved value to
    /// the placeholder itself (its Content is a Border, which passes that loop's "is Control"
    /// guard), so the live value is already correct before hydration, and the restore path only
    /// ever adds to the broadcast set - so a toggle would otherwise have survived hydration too.
    /// </remarks>
    [AvaloniaFact]
    public void CaptureSession_DuringDeferredRestore_KeepsALiveBroadcastToggleOnAPlaceholder()
    {
        using var appData = new TestAppDataRoot();
        WriteSavedSession(appData.RootPath, secondTabPaneId: "9f2c7a41-0000-4000-8000-000000000003");

        var window = TestMainWindowFactory.Create();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var placeholder = (TabItem)tabs.Items[1]!;
        Assert.IsNotType<NovaTerminal.Controls.TerminalPane>(placeholder.Content);
        Assert.False(window.IsBroadcastEnabledForTab(placeholder));

        // The user's toggle, through the same set ToggleBroadcastForCurrentTab drives.
        var broadcastTabs = (System.Collections.Generic.HashSet<TabItem>)typeof(NovaTerminal.MainWindow)
            .GetField("_broadcastEnabledTabs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        broadcastTabs.Add(placeholder);
        Assert.True(window.IsBroadcastEnabledForTab(placeholder));

        NovaSession captured = SessionManager.CaptureSession(window, tabs);

        Assert.True(captured.Tabs[1].BroadcastInputEnabled);
        // ...and the panes are still carried through, so this is not passing by skipping the fallback.
        Assert.Equal("9f2c7a41-0000-4000-8000-000000000003", captured.Tabs[1].Root!.PaneId);

        DrainDeferredRestorePlan(window);
    }

    /// <summary>
    /// Consumes the deferred restore plan so the Background-priority pass MainWindow left queued
    /// has nothing to do if it ever runs.
    /// </summary>
    /// <remarks>
    /// Not tidiness, and not optional. Nothing pumps the headless dispatcher between tests, so a
    /// plan left pending here is materialized inside whichever later test next pumps it - by which
    /// point <c>DisposeCreatedWindows</c> has cleared the factory's list, so hydration would build
    /// a fresh TerminalPane and a real shell in a window nothing can reap. Work outliving its test
    /// and contaminating the dispatcher this assembly shares is the failure mode behind #81, #416
    /// and #417 (see CONTRIBUTING's App.Tests notes); these are the first tests here to drive the
    /// deferred-restore path at all, so they are the first that could leak this way.
    ///
    /// Drains the plan rather than the dispatcher, and the distinction is the whole point. The
    /// first version of this called <c>Dispatcher.UIThread.RunJobs()</c>, which hydrated the tab
    /// but also ran every other job the shared queue happened to be holding: ten tests across
    /// AgentObserveIndicatorTests, VerticalTabStripTests and the
    /// <c>AvaloniaBootLocatorHygieneTests</c> dispatcher canary went red, against 641 of 641
    /// passing on main. Pumping that queue from a test is the contamination, not the cure.
    /// <c>DrainDeferred</c> returns immediately once the plan is gone, so consuming it here with a
    /// materializer that does nothing leaves the queued callback inert - and builds no pane and no
    /// shell at all, which is strictly less to reap than hydrating would have been.
    /// </remarks>
    private static void DrainDeferredRestorePlan(NovaTerminal.MainWindow window)
    {
        var startup = (StartupOrchestrator)typeof(NovaTerminal.MainWindow)
            .GetField("_startup", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

        // Also the strongest available statement that these tests really are in the state they
        // claim: restore deferred something, so the tab asserted on above is genuinely unhydrated.
        Assert.True(startup.HasPendingDeferredRestore);

        startup.DrainDeferred(_ => { });

        Assert.False(startup.HasPendingDeferredRestore);
    }

    /// <summary>
    /// Two tabs, so restore builds the selected one live and leaves the other a placeholder. The
    /// command has to be runnable on this platform or RestorePaneTree substitutes a default and the
    /// pane ids stop being a useful assertion.
    /// </summary>
    private static void WriteSavedSession(string appDataRoot, string secondTabPaneId)
    {
        string shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var session = new NovaSession
        {
            ActiveTabIndex = 0,
            Tabs =
            {
                NewTab("Live", Guid.NewGuid().ToString(), shell),
                NewTab("Deferred", secondTabPaneId, shell),
            },
        };

        string sessionsDirectory = System.IO.Path.Combine(appDataRoot, "sessions");
        System.IO.Directory.CreateDirectory(sessionsDirectory);
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(sessionsDirectory, "last_session.json"),
            System.Text.Json.JsonSerializer.Serialize(
                session, SessionSerializationContext.Default.NovaSession));
    }

    private static TabSession NewTab(string title, string paneId, string shell) => new()
    {
        TabId = Guid.NewGuid().ToString(),
        Title = title,
        Root = new PaneNode
        {
            Type = NodeType.Leaf,
            PaneId = paneId,
            Command = shell,
            Arguments = string.Empty,
        },
        ActivePaneId = paneId,
    };

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
        var window = TestMainWindowFactory.Create();
        var toggleMethod = typeof(NovaTerminal.MainWindow).GetMethod("ToggleCommandPalette", BindingFlags.Instance | BindingFlags.NonPublic);

        toggleMethod!.Invoke(window, null);

        TerminalCommand settingsCommand = Assert.Single(CommandRegistry.GetCommands().Where(command => command.Title == "Settings"));
        Assert.Equal("Ctrl+,", settingsCommand.Shortcut);
    }

    [AvaloniaFact]
    public void MainWindow_CommandPaletteIncludesConnections()
    {
        CommandRegistry.Clear();
        var window = TestMainWindowFactory.Create();
        var toggleMethod = typeof(NovaTerminal.MainWindow).GetMethod("ToggleCommandPalette", BindingFlags.Instance | BindingFlags.NonPublic);

        toggleMethod!.Invoke(window, null);

        Assert.Contains(CommandRegistry.GetCommands(), command => command.Id == "connections" && command.Title == "Connections");
    }

    [AvaloniaFact]
    public void MainWindow_CommandPalettePrefersMostUsedCommandsWhenOpened()
    {
        CommandRegistry.Clear();
        var window = TestMainWindowFactory.Create();
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
        var window = TestMainWindowFactory.Create();
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
    public void ApplyThemeToUi_LightTheme_UpdatesTabListForeground()
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
        // "open_tab_list" is Pinned by default, so its button exists at rest.
        var btnTabList = FindTitleBarButton(window, "open_tab_list");
        var iconTabList = btnTabList?.Content as PathIcon;

        Assert.NotNull(btnTabList);
        Assert.NotNull(iconTabList);
        Assert.Equal(expected, ((ISolidColorBrush)btnTabList!.Foreground!).Color);
        Assert.Equal(expected, ((ISolidColorBrush)iconTabList!.Foreground!).Color);
    }

    /// <summary>
    /// "toggle_recording" defaults to Overflow, so unlike open_tab_list its button is not in the
    /// bar at rest. This is a separate test rather than folded into the tab-list one above because
    /// asserting the theme-recolour behaviour honestly requires first driving the button into
    /// existence via a settings change and a rebuild, which is a materially different arrange step.
    /// </summary>
    [AvaloniaFact]
    public void ApplyThemeToUi_LightTheme_UpdatesIdleRecordForeground_WhenPinned()
    {
        var window = TestMainWindowFactory.Create();
        var settingsField = typeof(NovaTerminal.MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        var applyThemeMethod = typeof(NovaTerminal.MainWindow).GetMethod("ApplyThemeToUI", BindingFlags.Instance | BindingFlags.NonPublic);
        var rebuildTitleBarMethod = typeof(NovaTerminal.MainWindow).GetMethod("RebuildTitleBar", BindingFlags.Instance | BindingFlags.NonPublic);
        var settings = (TerminalSettings)settingsField!.GetValue(window)!;

        settings.TitleBarItems["toggle_recording"] = "Pinned";
        rebuildTitleBarMethod!.Invoke(window, null);

        settings.ThemeName = "Test Light";
        settings.ActiveTheme = new TerminalTheme
        {
            Name = "Test Light",
            Background = TermColor.FromRgb(245, 240, 225),
            Foreground = TermColor.Black
        };

        applyThemeMethod!.Invoke(window, null);

        var expected = Colors.Black;
        var btnRecord = FindTitleBarButton(window, "toggle_recording");
        var iconRecord = btnRecord?.Content as PathIcon;

        Assert.NotNull(btnRecord);
        Assert.NotNull(iconRecord);
        Assert.Equal(expected, ((ISolidColorBrush)btnRecord!.Foreground!).Color);
        Assert.Equal(expected, ((ISolidColorBrush)iconRecord!.Foreground!).Color);
    }

    /// <summary>
    /// Title bar buttons are created at runtime by <see cref="TitleBarViewFactory"/> and are never
    /// registered into the window's compiled NameScope - that registration only happens for
    /// elements the XAML compiler emits into InitializeComponent. <c>Window.FindControl&lt;T&gt;</c>
    /// resolves purely through that NameScope (confirmed by direct comparison against
    /// NameScope.GetNameScope(window)?.Find&lt;T&gt;, which returns the identical true/false as
    /// FindControl in every case tried, with or without Show()/a layout pass), so
    /// <c>window.FindControl&lt;Button&gt;(TitleBarViewFactory.ButtonName(id))</c> always returns
    /// null for these buttons - in this test host and in the shipped app alike. Walking the
    /// generated host's Children is what actually finds them; see task-10-report.md for the
    /// production-code impact this uncovered (ApplyThemeToUI's and UpdateRecordButtonUi's own
    /// internal FindControl calls silently no-op the same way).
    /// </summary>
    private static Button? FindTitleBarButton(NovaTerminal.MainWindow window, string id)
    {
        var host = window.FindControl<StackPanel>("TitleBarItemsHost");
        return host?.Children.OfType<Button>().SingleOrDefault(b => b.Name == TitleBarViewFactory.ButtonName(id));
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
