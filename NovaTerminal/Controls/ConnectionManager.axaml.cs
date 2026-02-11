using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NovaTerminal.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaTerminal.Controls
{
    public partial class ConnectionManager : UserControl
    {
        public event Action<TerminalProfile>? OnConnect;
        public event Action<TerminalProfile>? OnEditProfile;
        public event Action? OnSyncRequested;

        private List<TerminalProfile> _allProfiles = new();
        private List<TerminalProfile> _filteredProfiles = new();

        public ConnectionManager()
        {
            InitializeComponent();
            SearchInput.TextChanged += (s, e) => FilterConnections();
        }

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

        public void ApplyTheme(TerminalTheme theme)
        {
            this.Background = new Avalonia.Media.SolidColorBrush(theme.Background);
            this.Foreground = new Avalonia.Media.SolidColorBrush(theme.Foreground);

            // Card background calculation
            var cardColor = theme.Background;
            if (theme.Background.R < 127) // Dark theme assumption
                cardColor = Avalonia.Media.Color.FromRgb((byte)Math.Min(255, cardColor.R + 25), (byte)Math.Min(255, cardColor.G + 25), (byte)Math.Min(255, cardColor.B + 25));
            else // Light theme assumption
                cardColor = Avalonia.Media.Color.FromRgb((byte)Math.Max(0, cardColor.R - 15), (byte)Math.Max(0, cardColor.G - 15), (byte)Math.Max(0, cardColor.B - 15));

            CardBackground = new Avalonia.Media.SolidColorBrush(cardColor);
            SecondaryForeground = new Avalonia.Media.SolidColorBrush(theme.Foreground) { Opacity = 0.7 };
        }

        private void OnSyncClick(object? sender, RoutedEventArgs e)
        {
            OnSyncRequested?.Invoke();
        }

        private void OnConnectionClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is TerminalProfile profile)
            {
                OnConnect?.Invoke(profile);
            }
        }

        private void OnEditClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is TerminalProfile profile)
            {
                e.Handled = true;
                OnEditProfile?.Invoke(profile);
            }
        }

        public void LoadProfiles(List<TerminalProfile> profiles)
        {
            // Filter: Only show SSH connections (Remote First)
            _allProfiles = profiles.Where(p => p.Type == ConnectionType.SSH).ToList();
            RefreshTree();
            FilterConnections();
        }

        private void RefreshTree()
        {
            // Simple Grouping Logic for V1
            // In V2, we can make this a proper hierarchical data structure
            var groups = _allProfiles.Select(p => p.Group).Distinct().OrderBy(g => g).ToList();

            var rootNodes = new List<TreeViewItem>();

            var allItem = new TreeViewItem { Header = "All Connections", Tag = "ALL", IsExpanded = true };
            // allItem.PointerPressed += (s, e) => { FilterByGroup(null); e.Handled = true; }; // REMOVED: Breaks selection
            rootNodes.Add(allItem);

            foreach (var group in groups)
            {
                if (string.IsNullOrEmpty(group)) continue;

                var item = new TreeViewItem { Header = group, Tag = group };
                rootNodes.Add(item);
            }

            GroupsTree.ItemsSource = rootNodes;
            GroupsTree.SelectionChanged += GroupsTree_SelectionChanged;

            // Default selection
            GroupsTree.SelectedItem = allItem;
        }

        private void GroupsTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (GroupsTree.SelectedItem is TreeViewItem item)
            {
                if (item.Tag?.ToString() == "ALL")
                    FilterByGroup(null);
                else
                    FilterByGroup(item.Header?.ToString());
            }
        }

        private void FilterByGroup(string? group)
        {
            if (group == null)
            {
                _filteredProfiles = _allProfiles.ToList();
                SelectedGroupTitle.Text = "All";
            }
            else
            {
                _filteredProfiles = _allProfiles.Where(p => p.Group == group).ToList();
                SelectedGroupTitle.Text = group;
            }

            // Re-apply search text if any
            if (!string.IsNullOrEmpty(SearchInput.Text))
            {
                var q = SearchInput.Text.ToLowerInvariant();
                _filteredProfiles = _filteredProfiles.Where(p =>
                    p.Name.ToLowerInvariant().Contains(q) ||
                    p.Command.ToLowerInvariant().Contains(q) ||
                    (p.Tags != null && p.Tags.Any(t => t.ToLowerInvariant().Contains(q)))
                ).ToList();
            }

            ConnectionsList.ItemsSource = _filteredProfiles;
        }

        private void FilterConnections()
        {
            // Trigger group filter to re-apply logic (it handles search text too)
            // Get current group
            string? currentGroup = null;
            if (GroupsTree.SelectedItem is TreeViewItem item && item.Tag?.ToString() != "ALL")
            {
                currentGroup = item.Header?.ToString();
            }

            FilterByGroup(currentGroup);
        }


    }
}
