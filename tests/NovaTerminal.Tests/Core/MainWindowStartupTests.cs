using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using NovaTerminal.Core;
using NovaTerminal.Core.Shortcuts;
using System.Reflection;

namespace NovaTerminal.Tests.Core;

public sealed class MainWindowStartupTests
{
    [AvaloniaFact]
    public void MainWindow_CanBeConstructed()
    {
        var window = new NovaTerminal.MainWindow();

        Assert.NotNull(window);
    }

    [AvaloniaFact]
    public void MainWindow_LoadsWindowIconOnlyAfterDeferredHookRuns()
    {
        var window = new NovaTerminal.MainWindow();
        var ensureWindowIconLoadedMethod = typeof(NovaTerminal.MainWindow).GetMethod("EnsureWindowIconLoaded", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(ensureWindowIconLoadedMethod);
        Assert.Null(window.Icon);

        ensureWindowIconLoadedMethod!.Invoke(window, null);

        Assert.NotNull(window.Icon);
    }

    [AvaloniaFact]
    public void RegisterPaneOwners_TraversesDecoratorWrappedPane()
    {
        var window = new NovaTerminal.MainWindow();
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
        var window = new NovaTerminal.MainWindow();
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

    public async Task ExecuteCommand_DefersActionUntilAfterPaletteCloses()
    {
        var window = new NovaTerminal.MainWindow();
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
        var window = new NovaTerminal.MainWindow();
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
        var window = new NovaTerminal.MainWindow();
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
        var window = new NovaTerminal.MainWindow();
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
