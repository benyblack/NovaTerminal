using NovaTerminal.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NovaTerminal.Controls;
using NovaTerminal.Platform;
using NovaTerminal.VT;
using NovaTerminal.Platform.Ssh.Launch;
using NovaTerminal.ViewModels.Ssh;
using System.Collections.Specialized;
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
            "Connection details",
            "Delete connection"
        };

        var actionButtons = control.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => ToolTip.GetTip(button) is string tip && actionTips.Contains(tip))
            .ToList();

        Assert.Equal(5, actionButtons.Count);
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
    public void DeleteAction_RaisesDeleteProfileRequested_ForSelectedRow()
    {
        var control = CreateMeasuredConnectionManager();
        TerminalProfile profile = CreateSshProfile("Prod", favorite: false);
        control.LoadProfiles(new[] { profile });
        SelectFirstRow(control);

        TerminalProfile? receivedProfile = null;
        control.OnDeleteProfileRequested += p => receivedProfile = p;

        var deleteButton = FindButtonByToolTip(control, "Delete connection");
        Assert.NotNull(deleteButton);

        deleteButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Same(profile, receivedProfile);
    }

    [AvaloniaFact]
    public void DeleteAction_DoesNotRaise_WhenNoRowSelected()
    {
        var control = CreateMeasuredConnectionManager();
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });

        bool raised = false;
        control.OnDeleteProfileRequested += _ => raised = true;

        var deleteButton = FindButtonByToolTip(control, "Delete connection");
        Assert.NotNull(deleteButton);

        deleteButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(raised);
    }

    [AvaloniaFact]
    public void DeleteAction_DoesNotRemoveRowItself()
    {
        // The control only raises; MainWindow owns the store delete and the refresh.
        var control = CreateMeasuredConnectionManager();
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });
        SelectFirstRow(control);
        control.OnDeleteProfileRequested += _ => { };

        FindButtonByToolTip(control, "Delete connection")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, GetListItemCount(control));
        Assert.Single(control.GetAllProfiles());
    }

    [AvaloniaFact]
    public void SavedPasswordRow_ShowsYes_AndEnablesForget_WhenPasswordStored()
    {
        var control = CreateMeasuredConnectionManager();
        control.SavedPasswordAccess = new FakeSavedPasswordAccess { Saved = true };
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });
        SelectFirstRow(control);

        Assert.Equal("Yes", FindControl<TextBlock>(control, "KvSavedPassword").Text);
        var forget = FindControl<Button>(control, "BtnForgetSavedPassword");
        Assert.True(forget.IsVisible);
        Assert.True(forget.IsEnabled);
    }

    [AvaloniaFact]
    public void SavedPasswordRow_ShowsNo_AndDisablesForget_WhenNothingStored()
    {
        var control = CreateMeasuredConnectionManager();
        control.SavedPasswordAccess = new FakeSavedPasswordAccess { Saved = false };
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });
        SelectFirstRow(control);

        Assert.Equal("No", FindControl<TextBlock>(control, "KvSavedPassword").Text);
        Assert.False(FindControl<Button>(control, "BtnForgetSavedPassword").IsEnabled);
    }

    [AvaloniaFact]
    public void SavedPasswordRow_ShowsVaultUnavailable_WhenStoreIsUnavailable()
    {
        var control = CreateMeasuredConnectionManager();
        control.SavedPasswordAccess = new FakeSavedPasswordAccess { IsVaultAvailable = false, Saved = true };
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });
        SelectFirstRow(control);

        Assert.Equal("Vault unavailable", FindControl<TextBlock>(control, "KvSavedPassword").Text);
        Assert.False(FindControl<Button>(control, "BtnForgetSavedPassword").IsEnabled);
    }

    [AvaloniaFact]
    public void SavedPasswordRow_HidesForget_WhenNoAccessorInjected()
    {
        var control = CreateMeasuredConnectionManager();
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });
        SelectFirstRow(control);

        Assert.Equal("—", FindControl<TextBlock>(control, "KvSavedPassword").Text);
        Assert.False(FindControl<Button>(control, "BtnForgetSavedPassword").IsVisible);
    }

    [AvaloniaFact]
    public void ForgetSavedPassword_FlipsRowToNo_DisablesButton_AndKeepsSelection()
    {
        var control = CreateMeasuredConnectionManager();
        var access = new FakeSavedPasswordAccess { Saved = true };
        control.SavedPasswordAccess = access;
        TerminalProfile profile = CreateSshProfile("Prod", favorite: false);
        control.LoadProfiles(new[] { profile });
        SelectFirstRow(control);

        var forget = FindControl<Button>(control, "BtnForgetSavedPassword");
        forget.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, access.ForgetCallCount);
        Assert.Same(profile, access.LastForgotten);
        Assert.Equal("No", FindControl<TextBlock>(control, "KvSavedPassword").Text);
        Assert.False(forget.IsEnabled);

        // No LoadProfiles reload — the selection must survive.
        var list = FindControl<ListBox>(control, "ConnectionsList");
        Assert.Equal(0, list.SelectedIndex);
    }

    [AvaloniaFact]
    public void ForgetSavedPassword_DoesNothing_WhenNoRowSelected()
    {
        var control = CreateMeasuredConnectionManager();
        var access = new FakeSavedPasswordAccess { Saved = true };
        control.SavedPasswordAccess = access;
        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });

        FindControl<Button>(control, "BtnForgetSavedPassword")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(0, access.ForgetCallCount);
    }

    [AvaloniaFact]
    public void DetailPane_ShowsTypedIdentityFileAuthDescription()
    {
        var control = CreateMeasuredConnectionManager();
        control.LoadProfiles(new[]
        {
            new TerminalProfile
            {
                Type = ConnectionType.SSH,
                Name = "Prod",
                SshHost = "prod.internal",
                SshUser = "ops",
                UseSshAgent = false,
                IdentityFilePath = @"C:\keys\prod.pem"
            }
        });

        SelectFirstRow(control);

        Assert.Equal("identity file · C:\\keys\\prod.pem", FindControl<TextBlock>(control, "KvAuth").Text);
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
    public void SearchFilter_ReplacesVisibleRowsWithSingleResetNotification()
    {
        var control = CreateMeasuredConnectionManager();
        control.LoadProfiles(new[]
        {
            CreateSshProfile("Alpha", false),
            CreateSshProfile("Beta", false),
            CreateSshProfile("Gamma", false)
        });

        var list = FindControl<ListBox>(control, "ConnectionsList");
        var notifications = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)list.ItemsSource!).CollectionChanged += (_, args) => notifications.Add(args.Action);

        control.SearchInput.Text = "Alpha";
        var viewModelField = typeof(ConnectionManager).GetField("_viewModel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var applyFiltersMethod = typeof(ConnectionManager).GetMethod("ApplyFilters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var viewModel = (SshManagerViewModel)viewModelField!.GetValue(control)!;
        viewModel.SearchText = "Alpha";
        applyFiltersMethod!.Invoke(control, null);

        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, notifications);
        Assert.Equal(1, GetListItemCount(control));
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


    // Regression guard for the delete/details icons being arranged outside the detail
    // panel and clipped away (invisible in the running app while every behavioural test
    // passed). Note SecondaryActionButtons_ReserveSquareHitTargets asserts Button.Width,
    // which is the STYLE-SET property and reads 30 even when the arranged width is 0 —
    // it cannot detect clipping. This asserts real arranged geometry instead.
    //
    // Show() + RunJobs() is required rather than Measure/Arrange: DetailContent starts
    // collapsed, and a manual Measure with an unchanged constraint returns the cached
    // empty-state desired size, leaving the entire detail header arranged at zero.
    [AvaloniaTheory]
    [InlineData(1080.0)]
    [InlineData(900.0)]
    [InlineData(1400.0)]
    public void ActionBarControls_AreArrangedInsideTheDetailPanel(double width)
    {
        var control = new ConnectionManager();
        var host = new Grid();
        host.Children.Add(control);
        var window = new Window { Width = width, Height = 720, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        control.LoadProfiles(new[] { CreateSshProfile("Prod", favorite: false) });
        var list = FindControl<ListBox>(control, "ConnectionsList");
        list.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        var panel = FindControl<Grid>(control, "DetailColumn");
        Assert.True(panel.Bounds.Width > 0, "detail panel was not arranged");

        string[] actionTips =
        {
            "Open in current pane",
            "Toggle favorite",
            "Edit connection",
            "Copy launch command",
            "Connection details",
            "Delete connection"
        };

        foreach (string tip in actionTips)
        {
            Button? button = FindButtonByToolTip(control, tip);
            Assert.NotNull(button);

            Point origin = button!.TranslatePoint(new Point(0, 0), panel)
                ?? throw new Xunit.Sdk.XunitException($"'{tip}' is not connected to the detail panel.");

            Assert.True(
                button.Bounds.Width > 0 && button.Bounds.Height > 0,
                $"'{tip}' arranged with an empty rect ({button.Bounds}).");

            double right = origin.X + button.Bounds.Width;
            Assert.True(
                right <= panel.Bounds.Width + 0.5,
                $"'{tip}' is clipped at width {width}: right edge {right:F1} exceeds detail panel width {panel.Bounds.Width:F1}.");
        }
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

    private sealed class FakeSavedPasswordAccess : NovaTerminal.Shell.ISavedPasswordAccess
    {
        public bool IsVaultAvailable { get; set; } = true;
        public bool Saved { get; set; }
        public int ForgetCallCount { get; private set; }
        public TerminalProfile? LastForgotten { get; private set; }

        public bool HasSavedPassword(TerminalProfile profile) => Saved;

        public bool ForgetSavedPassword(TerminalProfile profile)
        {
            ForgetCallCount++;
            LastForgotten = profile;
            bool had = Saved;
            Saved = false;
            return had;
        }
    }

    private static Button? FindButtonByToolTip(ConnectionManager control, string tip)
    {
        return control.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(ToolTip.GetTip(button) as string, tip, System.StringComparison.Ordinal));
    }
}
