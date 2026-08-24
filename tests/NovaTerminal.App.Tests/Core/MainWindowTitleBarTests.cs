using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NovaTerminal.Shell;
using NovaTerminal.Shell.TitleBar;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Covers MainWindow.RebuildTitleBar - the piece the plan's full-suite verification found had zero
/// automated coverage, on the (wrong) assumption that MainWindow cannot be instantiated headless.
/// <see cref="TestMainWindowFactory"/> proves it can, so these tests drive the real rebuild path
/// through reflection, the same way MainWindowStartupTests already does.
///
/// Lookups here walk "TitleBarItemsHost".Children by name rather than calling
/// <c>window.FindControl&lt;Button&gt;(TitleBarViewFactory.ButtonName(id))</c>. That call resolves
/// only through the window's compiled NameScope (verified directly against
/// NameScope.GetNameScope(window)?.Find&lt;T&gt;, which agreed with FindControl's true/false in
/// every case tried, with or without Show()/a layout pass) and title bar buttons are created at
/// runtime by TitleBarViewFactory, so they are never registered into it - FindControl returns null
/// for them both here and in the shipped app. See task-10-report.md for the production-code impact.
/// </summary>
public sealed class MainWindowTitleBarTests
{
    private static readonly string[] DefaultPinnedButtonNames =
    [
        "BtnNewTab",
        TitleBarViewFactory.ButtonName("open_tab_list"),
        TitleBarViewFactory.ButtonName("connections"),
        TitleBarViewFactory.ButtonName("settings"),
        TitleBarViewFactory.OverflowButtonName,
    ];

    [AvaloniaFact]
    public void DefaultSettings_ProduceExpectedPinnedButtonsInOrder_PlusOverflow()
    {
        var window = TestMainWindowFactory.Create();

        var host = GetTitleBarHost(window);
        var actualNames = host.Children.Select(c => (c as Control)?.Name).ToList();

        Assert.Equal(DefaultPinnedButtonNames, actualNames);
    }

    [AvaloniaFact]
    public void RebuildTitleBar_APinnedExtraAction_Appears()
    {
        var window = TestMainWindowFactory.Create();
        var settings = GetSettings(window);

        settings.TitleBarItems["find"] = "Pinned";
        InvokeRebuildTitleBar(window);

        var host = GetTitleBarHost(window);

        Assert.Contains(
            TitleBarViewFactory.ButtonName("find"),
            host.Children.Select(c => (c as Control)?.Name));
    }

    [AvaloniaFact]
    public void RebuildTitleBar_AHiddenAction_DisappearsFromTheBarAndTheOverflowFlyout()
    {
        var window = TestMainWindowFactory.Create();
        var settings = GetSettings(window);

        settings.TitleBarItems["connections"] = "Hidden";
        InvokeRebuildTitleBar(window);

        var host = GetTitleBarHost(window);

        Assert.DoesNotContain(
            TitleBarViewFactory.ButtonName("connections"),
            host.Children.Select(c => (c as Control)?.Name));

        var overflowButton = host.Children.OfType<Button>()
            .SingleOrDefault(b => b.Name == TitleBarViewFactory.OverflowButtonName);
        Assert.NotNull(overflowButton);
        var flyout = Assert.IsType<MenuFlyout>(overflowButton!.Flyout);

        Assert.DoesNotContain(
            flyout.Items.OfType<MenuItem>(),
            item => ((string?)item.Header)?.StartsWith("Connections", System.StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// The + button is declared in XAML and carries a real MenuFlyout; TitleBarViewFactory
    /// reinserts the same instance on every rebuild instead of rebuilding it, specifically so that
    /// flyout survives. This is the hazard that design exists to prevent.
    /// </summary>
    [AvaloniaFact]
    public void RebuildTitleBar_TheNewTabButton_SurvivesRebuildsAsTheSameInstance()
    {
        var window = TestMainWindowFactory.Create();
        var original = window.FindControl<Button>("BtnNewTab");
        Assert.NotNull(original);

        InvokeRebuildTitleBar(window);
        InvokeRebuildTitleBar(window);

        var afterRebuilds = window.FindControl<Button>("BtnNewTab");
        Assert.Same(original, afterRebuilds);
    }

    /// <summary>
    /// TabOverflowBadge was deliberately moved out of the item host that RebuildTitleBar clears on
    /// every call (it is a sibling in the same Grid, not a child of TitleBarItemsHost) - if it were
    /// ever a child of that host instead, Populate's host.Children.Clear() would destroy it.
    /// </summary>
    [AvaloniaFact]
    public void RebuildTitleBar_TabOverflowBadge_SurvivesRebuild()
    {
        var window = TestMainWindowFactory.Create();
        var before = window.FindControl<TextBlock>("TabOverflowBadge");
        Assert.NotNull(before);

        InvokeRebuildTitleBar(window);

        var after = window.FindControl<TextBlock>("TabOverflowBadge");
        Assert.NotNull(after);
        Assert.Same(before, after);
    }

    [AvaloniaFact]
    public void RebuildTitleBar_Record_AutoSurfacesWhileActive_AndReturnsToOverflowWhenNotActive()
    {
        var window = TestMainWindowFactory.Create();
        var activeToggles = GetActiveTitleBarToggles(window);
        string recordButtonName = TitleBarViewFactory.ButtonName("toggle_recording");

        // toggle_recording defaults to Overflow, so at rest it is not in the bar.
        var hostAtRest = GetTitleBarHost(window);
        Assert.DoesNotContain(recordButtonName, hostAtRest.Children.Select(c => (c as Control)?.Name));

        activeToggles.Add("toggle_recording");
        InvokeRebuildTitleBar(window);

        var hostWhileActive = GetTitleBarHost(window);
        Assert.Contains(recordButtonName, hostWhileActive.Children.Select(c => (c as Control)?.Name));

        activeToggles.Remove("toggle_recording");
        InvokeRebuildTitleBar(window);

        var hostAfterDeactivation = GetTitleBarHost(window);
        Assert.DoesNotContain(recordButtonName, hostAfterDeactivation.Children.Select(c => (c as Control)?.Name));
    }

    private static StackPanel GetTitleBarHost(NovaTerminal.MainWindow window)
    {
        var host = window.FindControl<StackPanel>("TitleBarItemsHost");
        Assert.NotNull(host);
        return host!;
    }

    private static TerminalSettings GetSettings(NovaTerminal.MainWindow window)
    {
        var field = typeof(NovaTerminal.MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (TerminalSettings)field!.GetValue(window)!;
    }

    private static HashSet<string> GetActiveTitleBarToggles(NovaTerminal.MainWindow window)
    {
        var field = typeof(NovaTerminal.MainWindow).GetField("_activeTitleBarToggles", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (HashSet<string>)field!.GetValue(window)!;
    }

    private static void InvokeRebuildTitleBar(NovaTerminal.MainWindow window)
    {
        var method = typeof(NovaTerminal.MainWindow).GetMethod("RebuildTitleBar", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, null);
    }
}
