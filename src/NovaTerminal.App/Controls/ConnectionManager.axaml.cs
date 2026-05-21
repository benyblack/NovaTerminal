using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.ViewModels.Ssh;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace NovaTerminal.Controls
{
    public partial class ConnectionManager : UserControl
    {
        private const string FavoriteTag = "favorite";

        // Public API — preserved from the original control.
        public event Action<TerminalProfile, SshDiagnosticsLevel>? OnConnect;
        public event Action<TerminalProfile, SshQuickOpenTarget, SshDiagnosticsLevel>? OnQuickOpenRequested;
        public event Action<TerminalProfile>? OnEditProfile;
        public event Action<TerminalProfile, SshDiagnosticsLevel>? OnCopyLaunchCommandRequested;
        public event Action<TerminalProfile, SshDiagnosticsLevel>? OnConnectionDetailsRequested;
        public event Action? OnProfilesChanged;
        public event Action? OnSyncRequested;
        public event Action? OnNewConnectionRequested;

        private readonly SshManagerViewModel _viewModel = new();
        private readonly ObservableCollection<GroupNode> _groups = new();
        private readonly ObservableCollection<TagNode> _tags = new();
        private readonly ObservableCollection<SshProfileRowViewModel> _visibleRows = new();
        private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
        private string _selectedGroupKey = "__all";
        private string _selectedStatusKey = "all";
        private SshProfileRowViewModel? _selectedRow;

        // Swatch palette cycled through derived groups.
        private static readonly string[] GroupSwatches =
        {
            "#e06c75", "#e5c07b", "#56b6c2", "#c678dd", "#d19a66", "#98c379", "#61afef"
        };

        public ConnectionManager()
        {
            InitializeComponent();
            DataContext = _viewModel;

            // Search wiring (preserved).
            SearchInput.TextChanged += (_, _) =>
            {
                _viewModel.SearchText = SearchInput.Text ?? string.Empty;
                ApplyFilters();
                UpdateResultCountText();
            };

            // Connection list (now a ListBox — still an ItemsControl).
            var list = this.FindControl<ListBox>("ConnectionsList");
            if (list != null)
            {
                list.ItemsSource = _visibleRows;
            }

            // Groups column.
            var groupsList = this.FindControl<ItemsControl>("GroupsList");
            if (groupsList != null)
            {
                groupsList.ItemsSource = _groups;
            }
            var tagsList = this.FindControl<ItemsControl>("TagsList");
            if (tagsList != null)
            {
                tagsList.ItemsSource = _tags;
            }

            // Diagnostics combo (preserved).
            var diagnosticsCombo = this.FindControl<ComboBox>("DiagnosticsCombo");
            if (diagnosticsCombo != null)
            {
                diagnosticsCombo.ItemsSource = Enum.GetValues<SshDiagnosticsLevel>();
                diagnosticsCombo.SelectedItem = SshDiagnosticsLevel.None;
                diagnosticsCombo.SelectionChanged += (_, _) =>
                {
                    _viewModel.DiagnosticsLevel = GetSelectedDiagnosticsLevel();
                    UpdateLaunchPreview();
                };
            }

            _viewModel.DiagnosticsLevel = SshDiagnosticsLevel.None;
            UpdateResultCountText();
            UpdateGroupCounts();
            RenderEmptyDetail();
            UpdateLaunchPreview();
        }

        // ----- Kept for source compatibility; not used by the new visual. -----
        public static readonly StyledProperty<IBrush> CardBackgroundProperty =
            AvaloniaProperty.Register<ConnectionManager, IBrush>(nameof(CardBackground));

        public IBrush CardBackground
        {
            get => GetValue(CardBackgroundProperty);
            set => SetValue(CardBackgroundProperty, value);
        }

        public static readonly StyledProperty<IBrush> SecondaryForegroundProperty =
            AvaloniaProperty.Register<ConnectionManager, IBrush>(nameof(SecondaryForeground));

        public IBrush SecondaryForeground
        {
            get => GetValue(SecondaryForegroundProperty);
            set => SetValue(SecondaryForegroundProperty, value);
        }

        // Palette is fixed in the new design (matches the mockup). Kept as a no-op
        // so callers (MainWindow) don't need to change.
        public void ApplyTheme(TerminalTheme theme)
        {
            UpdatePaletteResources(theme);
        }

        // ----- Public methods preserved. -----
        public void LoadProfiles(IEnumerable<TerminalProfile> profiles)
        {
            _viewModel.LoadProfiles(profiles);
            RebuildGroups();
            RebuildTags();
            ApplyFilters();
            UpdateResultCountText();
            UpdateGroupCounts();
            RenderEmptyDetail();
        }

        public IReadOnlyList<TerminalProfile> GetAllProfiles() => _viewModel.GetAllProfiles();

        // ----- Groups column -----
        private void RebuildGroups()
        {
            _groups.Clear();
            var all = _viewModel.GetAllProfiles();
            var grouped = all
                .Select(p => string.IsNullOrWhiteSpace(p.Group) ? "Ungrouped" : p.Group)
                .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < grouped.Count; i++)
            {
                var g = grouped[i];
                _groups.Add(new GroupNode
                {
                    Name = g.Key ?? "Ungrouped",
                    Count = g.Count(),
                    Swatch = new SolidColorBrush(Color.Parse(GroupSwatches[i % GroupSwatches.Length])),
                });
            }
        }

        private void RebuildTags()
        {
            var grouped = _viewModel.GetAllProfiles()
                .SelectMany(profile => profile.Tags ?? new List<string>())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Where(tag => !string.Equals(tag, FavoriteTag, StringComparison.OrdinalIgnoreCase))
                .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var availableTags = new HashSet<string>(grouped.Select(group => group.Key), StringComparer.OrdinalIgnoreCase);
            _selectedTags.RemoveWhere(tag => !availableTags.Contains(tag));

            _tags.Clear();
            for (int i = 0; i < grouped.Count; i++)
            {
                var group = grouped[i];
                _tags.Add(new TagNode
                {
                    Name = group.Key,
                    Count = group.Count(),
                    Swatch = new SolidColorBrush(Color.Parse(GroupSwatches[i % GroupSwatches.Length])),
                    IsSelected = _selectedTags.Contains(group.Key)
                });
            }
        }

        private void UpdateGroupCounts()
        {
            var all = _viewModel.GetAllProfiles();
            var countAll = this.FindControl<TextBlock>("CountAll");
            var countFav = this.FindControl<TextBlock>("CountFav");
            if (countAll != null) countAll.Text = all.Count.ToString();
            if (countFav != null) countFav.Text = all.Count(p => p.Tags != null && p.Tags.Any(t => string.Equals(t, "favorite", StringComparison.OrdinalIgnoreCase))).ToString();
        }

        private void OnGroupClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string key)
            {
                return;
            }

            _selectedGroupKey = key;
            UpdateGroupActiveClasses(btn);
            ApplyFilters();
            UpdateResultCountText();
            e.Handled = true;
        }

        private void OnTagClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton toggle || toggle.Tag is not string tag)
            {
                return;
            }

            if (toggle.IsChecked == true)
            {
                _selectedTags.Add(tag);
            }
            else
            {
                _selectedTags.Remove(tag);
            }

            if (toggle.DataContext is TagNode node)
            {
                node.IsSelected = toggle.IsChecked == true;
            }

            ApplyFilters();
            UpdateResultCountText();
            e.Handled = true;
        }

        private void UpdateGroupActiveClasses(Button activeBtn)
        {
            // Clear .active on built-in buttons.
            foreach (var name in new[] { "BtnGroupAll", "BtnGroupFav" })
            {
                var b = this.FindControl<Button>(name);
                if (b != null) b.Classes.Remove("active");
            }
            // And on dynamic group buttons.
            var groupsList = this.FindControl<ItemsControl>("GroupsList");
            if (groupsList != null)
            {
                foreach (var b in groupsList.GetVisualDescendants().OfType<Button>())
                {
                    b.Classes.Remove("active");
                }
            }

            activeBtn.Classes.Add("active");
        }

        // ----- Status filter chips (mutually exclusive) -----
        private void OnStatusFilterClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton tb || tb.Tag is not string key)
            {
                return;
            }

            _selectedStatusKey = key;

            foreach (var name in new[] { "ChipAll", "ChipFav" })
            {
                var chip = this.FindControl<ToggleButton>(name);
                if (chip != null) chip.IsChecked = chip == tb;
            }

            ApplyFilters();
            UpdateResultCountText();
            e.Handled = true;
        }

        // ----- Filtering -----
        private void ApplyFilters()
        {
            _viewModel.SearchText = SearchInput.Text ?? string.Empty;
            IEnumerable<SshProfileRowViewModel> rows = _viewModel.FilteredRows;

            if (!string.Equals(_selectedGroupKey, "__all", StringComparison.Ordinal))
            {
                if (string.Equals(_selectedGroupKey, "__fav", StringComparison.Ordinal))
                {
                    rows = rows.Where(r => r.IsFavorite);
                }
                else
                {
                    rows = rows.Where(r => string.Equals(r.GroupPath, _selectedGroupKey, StringComparison.OrdinalIgnoreCase)
                        || (string.Equals(_selectedGroupKey, "Ungrouped", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(r.GroupPath)));
                }
            }

            switch (_selectedStatusKey)
            {
                case "fav":
                    rows = rows.Where(r => r.IsFavorite);
                    break;
            }

            if (_selectedTags.Count > 0)
            {
                rows = rows.Where(row => row.Tags.Any(tag => _selectedTags.Contains(tag)));
            }

            _visibleRows.Clear();
            foreach (var row in rows)
            {
                _visibleRows.Add(row);
            }

            var list = this.FindControl<ListBox>("ConnectionsList");
            if (_selectedRow != null && _visibleRows.Contains(_selectedRow))
            {
                if (list != null && !ReferenceEquals(list.SelectedItem, _selectedRow))
                {
                    list.SelectedItem = _selectedRow;
                }
            }
            else
            {
                _selectedRow = null;
                if (list != null)
                {
                    list.SelectedItem = null;
                }
                RenderEmptyDetail();
            }
        }

        // ----- Selection -----
        private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems != null && e.AddedItems.Count > 0 && e.AddedItems[0] is SshProfileRowViewModel row)
            {
                _selectedRow = row;
                RenderDetail(row);
            }
            else
            {
                _selectedRow = null;
                RenderEmptyDetail();
            }
        }

        private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (_selectedRow == null) return;

            _viewModel.DiagnosticsLevel = GetSelectedDiagnosticsLevel();
            _viewModel.RequestOpen(_selectedRow, SshQuickOpenTarget.CurrentPane);
            OnQuickOpenRequested?.Invoke(_selectedRow.Profile, SshQuickOpenTarget.CurrentPane, _viewModel.DiagnosticsLevel);
            OnConnect?.Invoke(_selectedRow.Profile, _viewModel.DiagnosticsLevel);
            e.Handled = true;
        }

        // ----- Detail rendering -----
        private void RenderEmptyDetail()
        {
            var empty = this.FindControl<StackPanel>("DetailEmptyState");
            var content = this.FindControl<Grid>("DetailContent");
            if (empty != null) empty.IsVisible = true;
            if (content != null) content.IsVisible = false;
        }

        private void RenderDetail(SshProfileRowViewModel row)
        {
            var empty = this.FindControl<StackPanel>("DetailEmptyState");
            var content = this.FindControl<Grid>("DetailContent");
            if (empty != null) empty.IsVisible = false;
            if (content != null) content.IsVisible = true;

            SetText("DetailTitle", row.Name);
            SetText("DetailEndpoint", row.Endpoint);
            SetText("KvHost",  string.IsNullOrWhiteSpace(row.Host)  ? "—" : row.Host);
            SetText("KvPort",  row.Port.ToString());
            SetText("KvUser",  string.IsNullOrWhiteSpace(row.User)  ? "—" : row.User);
            SetText("KvGroup", string.IsNullOrWhiteSpace(row.GroupPath) ? "Ungrouped" : row.GroupPath);
            SetText("KvAuth",  DescribeAuth(row.Profile));

            var fav = this.FindControl<PathIcon>("DetailFavStar");
            if (fav != null) fav.IsVisible = row.IsFavorite;

            var tagsControl = this.FindControl<ItemsControl>("DetailTags");
            if (tagsControl != null) tagsControl.ItemsSource = row.Tags.Where(t => !string.Equals(t, "favorite", StringComparison.OrdinalIgnoreCase)).ToList();

            var notes = this.FindControl<TextBlock>("DetailNotes");
            if (notes != null) notes.Text = string.IsNullOrWhiteSpace(row.Notes) ? "(no notes)" : row.Notes;

            UpdateLaunchPreview();
        }

        private void SetText(string controlName, string text)
        {
            var tb = this.FindControl<TextBlock>(controlName);
            if (tb != null) tb.Text = text;
        }

        private static string DescribeAuth(TerminalProfile profile)
        {
            // Best-effort; the actual profile model may expose richer fields.
            try
            {
                var prop = profile.GetType().GetProperty("AuthMode")
                          ?? profile.GetType().GetProperty("SshAuthMode");
                var idProp = profile.GetType().GetProperty("IdentityFilePath");
                var mode = prop?.GetValue(profile)?.ToString() ?? "agent";
                var id = idProp?.GetValue(profile) as string;
                return string.IsNullOrWhiteSpace(id) ? mode : $"{mode} · {id}";
            }
            catch
            {
                return "agent";
            }
        }

        private void UpdateLaunchPreview()
        {
            var preview = this.FindControl<TextBlock>("LaunchPreviewText");
            if (preview == null)
            {
                return;
            }

            SshDiagnosticsLevel level = GetSelectedDiagnosticsLevel();
            string flags = string.Join(" ", level.ToArguments());
            if (string.IsNullOrWhiteSpace(flags))
            {
                flags = "none";
            }

            string target = _selectedRow == null
                ? "the selected connection"
                : _selectedRow.Endpoint;

            preview.Text =
                $"Selected level: {DescribeDiagnosticsLevel(level)}{Environment.NewLine}" +
                $"SSH flags added: {flags}{Environment.NewLine}" +
                $"Open, Copy command, and Connection details use this level for {target}.";
        }

        private static string DescribeDiagnosticsLevel(SshDiagnosticsLevel level)
        {
            return level switch
            {
                SshDiagnosticsLevel.Verbose => "Verbose",
                SshDiagnosticsLevel.VeryVerbose => "Very verbose",
                _ => "None"
            };
        }

        // ----- Toolbar handlers (preserved) -----
        private void OnSyncClick(object? sender, RoutedEventArgs e)
        {
            OnSyncRequested?.Invoke();
        }

        private void OnNewConnectionClick(object? sender, RoutedEventArgs e)
        {
            OnNewConnectionRequested?.Invoke();
        }

        // ----- Detail action handlers (preserved API, source row = _selectedRow) -----
        private void OnEditClick(object? sender, RoutedEventArgs e)
        {
            if (TryGetRow(sender, out var row))
            {
                e.Handled = true;
                OnEditProfile?.Invoke(row.Profile);
            }
        }

        private void OnCopyCommandClick(object? sender, RoutedEventArgs e)
        {
            if (TryGetRow(sender, out var row))
            {
                e.Handled = true;
                OnCopyLaunchCommandRequested?.Invoke(row.Profile, GetSelectedDiagnosticsLevel());
            }
        }

        private void OnDetailsClick(object? sender, RoutedEventArgs e)
        {
            if (TryGetRow(sender, out var row))
            {
                e.Handled = true;
                OnConnectionDetailsRequested?.Invoke(row.Profile, GetSelectedDiagnosticsLevel());
            }
        }

        private void OnFavoriteClick(object? sender, RoutedEventArgs e)
        {
            if (!TryGetRow(sender, out var row))
            {
                return;
            }

            e.Handled = true;
            _viewModel.ToggleFavorite(row);
            ApplyFilters();
            UpdateResultCountText();
            UpdateGroupCounts();
            RebuildTags();

            var fav = this.FindControl<PathIcon>("DetailFavStar");
            if (fav != null) fav.IsVisible = row.IsFavorite;

            OnProfilesChanged?.Invoke();
        }

        private void OnOpenCurrentClick(object? sender, RoutedEventArgs e)
        {
            RequestOpen(SshQuickOpenTarget.CurrentPane);
            e.Handled = true;
        }

        private void OnOpenNewTabClick(object? sender, RoutedEventArgs e)
        {
            RequestOpen(SshQuickOpenTarget.NewTab);
            e.Handled = true;
        }

        private void OnSplitHorizontalClick(object? sender, RoutedEventArgs e)
        {
            RequestOpen(SshQuickOpenTarget.SplitHorizontal);
            e.Handled = true;
        }

        private void OnSplitVerticalClick(object? sender, RoutedEventArgs e)
        {
            RequestOpen(SshQuickOpenTarget.SplitVertical);
            e.Handled = true;
        }

        private SshDiagnosticsLevel GetSelectedDiagnosticsLevel()
        {
            var diagnosticsCombo = this.FindControl<ComboBox>("DiagnosticsCombo");
            if (diagnosticsCombo?.SelectedItem is SshDiagnosticsLevel level)
            {
                return level;
            }
            return SshDiagnosticsLevel.None;
        }

        // TryGetRow now prefers the selected row in the list (since action
        // buttons live in the detail panel and aren't tagged per-row).
        private bool TryGetRow(object? sender, out SshProfileRowViewModel row)
        {
            if (_selectedRow != null)
            {
                row = _selectedRow;
                return true;
            }

            // Fallback: legacy ancestor walk (in case buttons get added back
            // to the row template later).
            if (sender is Control control)
            {
                if (control.Tag is SshProfileRowViewModel taggedRow)
                {
                    row = taggedRow;
                    return true;
                }

                if (control.DataContext is SshProfileRowViewModel dataContextRow)
                {
                    row = dataContextRow;
                    return true;
                }

                foreach (var ancestor in control.GetVisualAncestors())
                {
                    if (ancestor is Control ancestorControl)
                    {
                        if (ancestorControl.Tag is SshProfileRowViewModel ancestorTaggedRow)
                        {
                            row = ancestorTaggedRow;
                            return true;
                        }
                        if (ancestorControl.DataContext is SshProfileRowViewModel ancestorDataContextRow)
                        {
                            row = ancestorDataContextRow;
                            return true;
                        }
                    }
                }
            }

            row = null!;
            return false;
        }

        private void RequestOpen(SshQuickOpenTarget target)
        {
            if (_selectedRow == null) return;

            _viewModel.DiagnosticsLevel = GetSelectedDiagnosticsLevel();
            _viewModel.RequestOpen(_selectedRow, target);
            OnQuickOpenRequested?.Invoke(_selectedRow.Profile, target, _viewModel.DiagnosticsLevel);

            if (target == SshQuickOpenTarget.CurrentPane)
            {
                OnConnect?.Invoke(_selectedRow.Profile, _viewModel.DiagnosticsLevel);
            }
        }

        private void UpdateResultCountText()
        {
            var resultCountText = this.FindControl<TextBlock>("ResultCountText");
            if (resultCountText == null) return;

            int count = _visibleRows.Count;

            resultCountText.Text = count == 1 ? "1 connection" : $"{count} connections";
        }

        private void UpdatePaletteResources(TerminalTheme theme)
        {
            var background = theme.Background.ToAvaloniaColor();
            var foreground = theme.Foreground.ToAvaloniaColor();
            bool dark = IsDark(background);

            UpdateBrush("NtWindowBg", background);
            UpdateBrush("NtChromeBg", Shift(background, dark ? 10 : -10));
            UpdateBrush("NtPanel", Shift(background, dark ? 18 : -18));
            UpdateBrush("NtPanelAlt", Shift(background, dark ? 6 : -6));
            UpdateBrush("NtHairline", Shift(background, dark ? 28 : -28));
            UpdateBrush("NtFg", foreground);
            UpdateBrush("NtFg2", WithAlpha(foreground, 0xC8));
            UpdateBrush("NtFg3", WithAlpha(foreground, 0x9A));
            UpdateBrush("NtFg4", WithAlpha(foreground, 0x6E));
            UpdateBrush("NtBlue", theme.Blue.ToAvaloniaColor());
            UpdateBrush("NtBlueDim", WithAlpha(theme.Blue.ToAvaloniaColor(), 0x24));
            UpdateBrush("NtBlueFaint", WithAlpha(theme.Blue.ToAvaloniaColor(), 0x15));
            UpdateBrush("NtBlueBorder", WithAlpha(theme.Blue.ToAvaloniaColor(), 0x4D));
            UpdateBrush("NtGreen", theme.Green.ToAvaloniaColor());
            UpdateBrush("NtYellow", theme.Yellow.ToAvaloniaColor());
            UpdateBrush("NtRed", theme.Red.ToAvaloniaColor());
            UpdateBrush("NtMagenta", theme.Magenta.ToAvaloniaColor());
        }

        private void UpdateBrush(string key, Color color)
        {
            if (Resources[key] is SolidColorBrush brush)
            {
                brush.Color = color;
            }
            else
            {
                Resources[key] = new SolidColorBrush(color);
            }
        }

        private static bool IsDark(Color color)
        {
            double luminance = ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255.0;
            return luminance < 0.5;
        }

        private static Color Shift(Color color, int delta)
        {
            return Color.FromArgb(
                color.A,
                Clamp(color.R + delta),
                Clamp(color.G + delta),
                Clamp(color.B + delta));
        }

        private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

        private static byte Clamp(int value) => (byte)Math.Max(0, Math.Min(255, value));

    }

    // Public POCO so XAML compiled bindings can resolve it. Used by the
    // groups column ItemsControl in ConnectionManager.axaml.
    public sealed class GroupNode
    {
        public string Name { get; init; } = "";
        public int Count { get; init; }
        public IBrush Swatch { get; init; } = Brushes.Gray;
    }

    public sealed class TagNode
    {
        public string Name { get; init; } = "";
        public int Count { get; init; }
        public IBrush Swatch { get; init; } = Brushes.Gray;
        public bool IsSelected { get; set; }
    }
}
