using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Core;

public sealed class VerticalTabStripTests
{
    private static TerminalSettings GetSettings(NovaTerminal.MainWindow window)
        => (TerminalSettings)typeof(NovaTerminal.MainWindow)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static ScrollViewer? InvokeFindTabHeaderScrollViewer(NovaTerminal.MainWindow window)
        => (ScrollViewer?)typeof(NovaTerminal.MainWindow)
            .GetMethod("FindTabHeaderScrollViewer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);

    private static Orientation? InvokeGetTabItemsPanelOrientation(NovaTerminal.MainWindow window)
    {
        var presenter = (ItemsPresenter?)typeof(NovaTerminal.MainWindow)
            .GetMethod("FindTabItemsPresenter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);
        return (presenter?.Panel as StackPanel)?.Orientation;
    }

    private static NovaTerminal.MainWindow CreateShownWindow()
    {
        var window = TestMainWindowFactory.Create();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void ApplyTabLayout_Vertical_SidebarSizedFromSettings_AndOverflowMathSkipped()
    {
        var window = CreateShownWindow();
        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        settings.VerticalTabStripWidth = 260;

        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVerticalTabStripActive);

        var scrollViewer = InvokeFindTabHeaderScrollViewer(window);
        Assert.NotNull(scrollViewer);
        Assert.Equal(260, scrollViewer!.Width);
        Assert.True(double.IsNaN(scrollViewer.Height), "vertical strip must not keep the 36px horizontal height");
        Assert.Equal(new Avalonia.Thickness(0), scrollViewer.Margin);

        var tabs = window.FindControl<TabControl>("Tabs");
        Assert.NotNull(tabs);
        Assert.Contains("vertical-tabs", tabs!.Classes);

        var badge = window.FindControl<TextBlock>("TabOverflowBadge");
        Assert.NotNull(badge);
        Assert.False(badge!.IsVisible);

        Assert.Equal(Orientation.Vertical, InvokeGetTabItemsPanelOrientation(window));
    }

    [AvaloniaFact]
    public void ApplyTabLayout_GarbageWidth_FallsBackToClampedDefault()
    {
        var window = CreateShownWindow();
        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        settings.VerticalTabStripWidth = -1;

        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TabStripLayout.DefaultSidebarWidth, InvokeFindTabHeaderScrollViewer(window)!.Width);
    }

    [AvaloniaFact]
    public void ApplyTabLayout_RoundTrip_RestoresHorizontalStrip_WithoutTouchingTabItems()
    {
        var window = CreateShownWindow();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tabItemsBefore = tabs.Items.Cast<TabItem>().ToList();
        var contentBefore = tabItemsBefore.Select(t => t.Content).ToList();

        var settings = GetSettings(window);
        settings.TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        settings.TabStripOrientation = "Horizontal";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVerticalTabStripActive);
        Assert.DoesNotContain("vertical-tabs", tabs.Classes);

        var scrollViewer = InvokeFindTabHeaderScrollViewer(window)!;
        Assert.Equal(36, scrollViewer.Height);
        Assert.True(double.IsNaN(scrollViewer.Width), "horizontal strip must not keep the sidebar width");
        Assert.True(scrollViewer.Margin.Right > 0, "horizontal strip must reserve title-bar space again");
        Assert.Equal(Orientation.Horizontal, InvokeGetTabItemsPanelOrientation(window));

        // The same TabItem instances with the same Content must survive both swaps —
        // panes/sessions are never disposed or recreated by a layout change.
        Assert.Equal(tabItemsBefore, tabs.Items.Cast<TabItem>().ToList());
        Assert.Equal(contentBefore, tabs.Items.Cast<TabItem>().Select(t => t.Content).ToList());
    }

    [AvaloniaFact]
    public void VerticalHeader_TitleIsFirstTextBlock_SoExistingLabelPlumbingStillWorks()
    {
        var window = CreateShownWindow();
        GetSettings(window).TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = tabs.Items.Cast<TabItem>().First();

        // The status dot and preview line exist...
        Assert.NotNull(NovaTerminal.MainWindow.FindTabHeaderDescendant<Avalonia.Controls.Shapes.Ellipse>(tab.Header, "TabStatusDot"));
        var preview = NovaTerminal.MainWindow.FindTabHeaderDescendant<TextBlock>(tab.Header, "TabPreviewLine");
        Assert.NotNull(preview);

        // ...and the title plumbing (first-TextBlock contract) still resolves the TITLE, not the preview.
        preview!.Text = "PREVIEW_SENTINEL";
        Assert.NotEqual("PREVIEW_SENTINEL", GetTabHeaderTextOf(window, tab));
    }

    private static string GetTabHeaderTextOf(NovaTerminal.MainWindow window, TabItem tab)
        => (string)typeof(NovaTerminal.MainWindow)
            .GetMethod("GetTabHeaderText", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tab })!;

    [AvaloniaFact]
    public void HorizontalMode_KeepsPlainHeaders()
    {
        var window = CreateShownWindow(); // default settings = horizontal
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var tab = tabs.Items.Cast<TabItem>().First();
        Assert.Null(NovaTerminal.MainWindow.FindTabHeaderDescendant<TextBlock>(tab.Header, "TabPreviewLine"));
    }
}
