using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NovaTerminal.Controls;
using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.ViewModels.Ssh;
using System.Linq;

namespace NovaTerminal.Tests.Ssh;

public sealed class ConnectionManagerTests
{
    [AvaloniaFact]
    public void NewConnectionButton_RaisesEvent()
    {
        var control = new ConnectionManager();
        bool raised = false;
        control.OnNewConnectionRequested += () => raised = true;

        Button? button = control.FindControl<Button>("BtnNewConnection");
        Assert.NotNull(button);

        button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(raised);
    }

    [AvaloniaFact]
    public void SecondaryActionButtons_ReserveSquareHitTargets()
    {
        var control = CreateMeasuredConnectionManager(800, 500);
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: true) });
        SelectFirstRow(control);

        string[] actionTips =
        {
            "Toggle favorite",
            "Edit connection",
            "Copy launch command",
            "Connection details"
        };

        var actionButtons = control.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => ToolTip.GetTip(button) is string tip && actionTips.Contains(tip))
            .ToList();

        Assert.Equal(4, actionButtons.Count);
        Assert.All(actionButtons, button =>
        {
            Assert.True(button.Width >= 30, $"Expected '{ToolTip.GetTip(button)}' width >= 30 but was {button.Width}.");
            Assert.True(button.Height >= 30, $"Expected '{ToolTip.GetTip(button)}' height >= 30 but was {button.Height}.");
        });
    }

    [AvaloniaFact]
    public void DetailsAction_RaisesConnectionDetailsRequested_ForSelectedRow()
    {
        var control = CreateMeasuredConnectionManager();
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: true) });
        SelectFirstRow(control);

        TerminalProfile? receivedProfile = null;
        SshDiagnosticsLevel receivedLevel = SshDiagnosticsLevel.None;
        control.OnConnectionDetailsRequested += (profile, level) =>
        {
            receivedProfile = profile;
            receivedLevel = level;
        };

        var detailsButton = FindButtonByToolTip(control, "Connection details");
        Assert.NotNull(detailsButton);

        detailsButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.NotNull(receivedProfile);
        Assert.Equal("Prod", receivedProfile!.Name);
        Assert.Equal(SshDiagnosticsLevel.None, receivedLevel);
    }

    [AvaloniaFact]
    public void FavoriteFilter_RemovesRowImmediately_WhenFavoriteIsCleared()
    {
        var control = CreateMeasuredConnectionManager();
        control.LoadProfiles(new[]
        {
            CreateSshProfile("Prod", favorite: true),
            CreateSshProfile("Stage", favorite: false)
        });

        FindControl<Button>(control, "BtnGroupFav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, GetListItemCount(control));

        SelectFirstRow(control);
        FindButtonByToolTip(control, "Toggle favorite")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(0, GetListItemCount(control));
        Assert.Equal("0 connections", FindControl<TextBlock>(control, "ResultCountText").Text);
    }

    [AvaloniaFact]
    public void TagSection_ShowsNonFavoriteTagsWithAggregatedCounts()
    {
        var control = CreateMeasuredConnectionManager();
        control.LoadProfiles(new[]
        {
            CreateSshProfile("Alpha", true, "hetzner", "vw"),
            CreateSshProfile("Beta", false, "vw"),
            CreateSshProfile("Gamma", false, "db")
        });
        RefreshLayout(control);

        var tagsList = FindControl<ItemsControl>(control, "TagsList");
        var tags = tagsList.ItemsSource?.Cast<TagNode>().OrderBy(node => node.Name, System.StringComparer.OrdinalIgnoreCase).ToList();

        Assert.NotNull(tags);
        Assert.Equal(3, tags!.Count);
        Assert.DoesNotContain(tags, tag => string.Equals(tag.Name, "favorite", System.StringComparison.OrdinalIgnoreCase));
        Assert.Equal(("db", 1), (tags[0].Name, tags[0].Count));
        Assert.Equal(("hetzner", 1), (tags[1].Name, tags[1].Count));
        Assert.Equal(("vw", 2), (tags[2].Name, tags[2].Count));
    }

    [AvaloniaFact]
    public void TagFilter_MultiSelectUsesAnyMatching()
    {
        var control = CreateMeasuredConnectionManager();
        control.LoadProfiles(new[]
        {
            CreateSshProfile("Alpha", false, "hetzner"),
            CreateSshProfile("Beta", false, "vw"),
            CreateSshProfile("Gamma", false, "db")
        });
        RefreshLayout(control);

        InvokeTagToggle(control, "hetzner", isChecked: true);
        Assert.Equal(1, GetListItemCount(control));

        InvokeTagToggle(control, "vw", isChecked: true);

        var rows = FindControl<ListBox>(control, "ConnectionsList").ItemsSource!.Cast<SshProfileRowViewModel>().Select(row => row.Name).OrderBy(name => name).ToArray();
        Assert.Equal(new[] { "Alpha", "Beta" }, rows);
        Assert.Equal("2 connections", FindControl<TextBlock>(control, "ResultCountText").Text);
    }

    [AvaloniaFact]
    public void LaunchPreview_ReflectsSelectedDiagnosticsLevel()
    {
        var control = CreateMeasuredConnectionManager();
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });
        SelectFirstRow(control);

        var combo = FindControl<ComboBox>(control, "DiagnosticsCombo");
        combo.SelectedItem = SshDiagnosticsLevel.VeryVerbose;
        RefreshLayout(control);

        string preview = FindControl<TextBlock>(control, "LaunchPreviewText").Text ?? string.Empty;

        Assert.Contains("Selected level: Very verbose", preview, System.StringComparison.Ordinal);
        Assert.Contains("SSH flags added: -vv", preview, System.StringComparison.Ordinal);
        Assert.Contains("ops@prod.internal:22", preview, System.StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ConnectionManager_CanArrangeWithinSmallOverlay()
    {
        var control = new ConnectionManager();
        control.Measure(new Size(760, 520));
        control.Arrange(new Rect(0, 0, 760, 520));

        Assert.True(control.Bounds.Width <= 760, $"Expected width <= 760 but was {control.Bounds.Width}.");
        Assert.True(control.Bounds.Height <= 520, $"Expected height <= 520 but was {control.Bounds.Height}.");
    }

    private static ConnectionManager CreateMeasuredConnectionManager(double width = 1080, double height = 720)
    {
        var control = new ConnectionManager();
        var host = new Grid();
        host.Children.Add(control);
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = host
        };

        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        return control;
    }

    private static TerminalProfile CreateSshProfile(string name, bool favorite, params string[] tags)
    {
        var allTags = tags.ToList();
        if (favorite)
        {
            allTags.Insert(0, "favorite");
        }

        return new TerminalProfile
        {
            Type = ConnectionType.SSH,
            Name = name,
            SshHost = $"{name.ToLowerInvariant()}.internal",
            SshUser = "ops",
            Tags = allTags
        };
    }

    private static T FindControl<T>(ConnectionManager control, string name) where T : Control
    {
        return control.FindControl<T>(name)!;
    }

    private static void SelectFirstRow(ConnectionManager control)
    {
        var list = FindControl<ListBox>(control, "ConnectionsList");
        list.SelectedIndex = 0;
        if (control.Bounds.Width > 0 && control.Bounds.Height > 0)
        {
            RefreshLayout(control);
        }
    }

    private static void RefreshLayout(ConnectionManager control)
    {
        if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
        {
            return;
        }

        control.Measure(control.Bounds.Size);
        control.Arrange(new Rect(control.Bounds.Size));
    }

    private static int GetListItemCount(ConnectionManager control)
    {
        var list = FindControl<ListBox>(control, "ConnectionsList");
        return list.ItemsSource?.Cast<object>().Count() ?? 0;
    }

    private static void InvokeTagToggle(ConnectionManager control, string tag, bool isChecked)
    {
        var handler = typeof(ConnectionManager).GetMethod("OnTagClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(handler);

        var toggle = new ToggleButton
        {
            Tag = tag,
            IsChecked = isChecked,
            DataContext = new TagNode
            {
                Name = tag,
                IsSelected = isChecked
            }
        };

        handler!.Invoke(control, new object?[] { toggle, new RoutedEventArgs(ToggleButton.ClickEvent) });
    }

    private static Button? FindButtonByToolTip(ConnectionManager control, string tip)
    {
        return control.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(ToolTip.GetTip(button) as string, tip, System.StringComparison.Ordinal));
    }
}
