using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using NovaTerminal.Core;
using NovaTerminal.Core.Ssh.Launch;
using NovaTerminal.ViewModels.Ssh;
using System;
using System.Collections.Generic;

namespace NovaTerminal.Controls
{
    public partial class ConnectionManager : UserControl
    {
        public event Action<TerminalProfile, SshDiagnosticsLevel>? OnConnect;
        public event Action<TerminalProfile, SshQuickOpenTarget, SshDiagnosticsLevel>? OnQuickOpenRequested;
        public event Action<TerminalProfile>? OnEditProfile;
        public event Action<TerminalProfile, SshDiagnosticsLevel>? OnCopyLaunchCommandRequested;
        public event Action<TerminalProfile, SshDiagnosticsLevel>? OnConnectionDetailsRequested;
        public event Action? OnProfilesChanged;
        public event Action? OnSyncRequested;
        public event Action? OnNewConnectionRequested;

        private readonly SshManagerViewModel _viewModel = new();

        public ConnectionManager()
        {
            InitializeComponent();
            DataContext = _viewModel;

            SearchInput.TextChanged += (_, _) =>
            {
                _viewModel.SearchText = SearchInput.Text ?? string.Empty;
                UpdateResultCountText();
            };

            var connectionsList = this.FindControl<ItemsControl>("ConnectionsList");
            if (connectionsList != null)
            {
                connectionsList.ItemsSource = _viewModel.FilteredRows;
            }

            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SshManagerViewModel.SearchText))
                {
                    UpdateResultCountText();
                }
            };

            var diagnosticsCombo = this.FindControl<ComboBox>("DiagnosticsCombo");
            if (diagnosticsCombo != null)
            {
                diagnosticsCombo.ItemsSource = Enum.GetValues<SshDiagnosticsLevel>();
                diagnosticsCombo.SelectedItem = SshDiagnosticsLevel.None;
                diagnosticsCombo.SelectionChanged += (_, _) =>
                {
                    _viewModel.DiagnosticsLevel = GetSelectedDiagnosticsLevel();
                };
            }

            _viewModel.DiagnosticsLevel = SshDiagnosticsLevel.None;
            UpdateResultCountText();
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
            this.Background = new Avalonia.Media.SolidColorBrush(theme.Background.ToAvaloniaColor());
            this.Foreground = new Avalonia.Media.SolidColorBrush(theme.Foreground.ToAvaloniaColor());

            // Card background calculation
            var bgColor = theme.Background.ToAvaloniaColor();
            var cardColor = bgColor;
            if (bgColor.R < 127) // Dark theme assumption
                cardColor = Avalonia.Media.Color.FromRgb((byte)Math.Min(255, cardColor.R + 25), (byte)Math.Min(255, cardColor.G + 25), (byte)Math.Min(255, cardColor.B + 25));
            else // Light theme assumption
                cardColor = Avalonia.Media.Color.FromRgb((byte)Math.Max(0, cardColor.R - 15), (byte)Math.Max(0, cardColor.G - 15), (byte)Math.Max(0, cardColor.B - 15));

            CardBackground = new Avalonia.Media.SolidColorBrush(cardColor);
            SecondaryForeground = new Avalonia.Media.SolidColorBrush(theme.Foreground.ToAvaloniaColor()) { Opacity = 0.7 };
        }

        private void OnSyncClick(object? sender, RoutedEventArgs e)
        {
            OnSyncRequested?.Invoke();
        }

        private void OnNewConnectionClick(object? sender, RoutedEventArgs e)
        {
            OnNewConnectionRequested?.Invoke();
        }

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

        public void LoadProfiles(IEnumerable<TerminalProfile> profiles)
        {
            _viewModel.LoadProfiles(profiles);
            UpdateResultCountText();
        }

        public IReadOnlyList<TerminalProfile> GetAllProfiles()
        {
            return _viewModel.GetAllProfiles();
        }

        private void OnFavoriteClick(object? sender, RoutedEventArgs e)
        {
            if (!TryGetRow(sender, out var row))
            {
                return;
            }

            e.Handled = true;
            _viewModel.ToggleFavorite(row);
            UpdateResultCountText();
            OnProfilesChanged?.Invoke();
        }

        private void OnOpenCurrentClick(object? sender, RoutedEventArgs e)
        {
            RequestOpen(sender, SshQuickOpenTarget.CurrentPane);
            e.Handled = true;
        }

        private void OnOpenNewTabClick(object? sender, RoutedEventArgs e)
        {
            RequestOpen(sender, SshQuickOpenTarget.NewTab);
            e.Handled = true;
        }

        private void OnSplitHorizontalClick(object? sender, RoutedEventArgs e)
        {
            RequestOpen(sender, SshQuickOpenTarget.SplitHorizontal);
            e.Handled = true;
        }

        private void OnSplitVerticalClick(object? sender, RoutedEventArgs e)
        {
            RequestOpen(sender, SshQuickOpenTarget.SplitVertical);
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

        private bool TryGetRow(object? sender, out SshProfileRowViewModel row)
        {
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

        private void RequestOpen(object? sender, SshQuickOpenTarget target)
        {
            if (!TryGetRow(sender, out var row))
            {
                return;
            }

            _viewModel.DiagnosticsLevel = GetSelectedDiagnosticsLevel();
            _viewModel.RequestOpen(row, target);
            OnQuickOpenRequested?.Invoke(row.Profile, target, _viewModel.DiagnosticsLevel);

            if (target == SshQuickOpenTarget.CurrentPane)
            {
                // Backward-compat for existing handlers until all callers use quick-open targets.
                OnConnect?.Invoke(row.Profile, _viewModel.DiagnosticsLevel);
            }
        }

        private void UpdateResultCountText()
        {
            var resultCountText = this.FindControl<TextBlock>("ResultCountText");
            if (resultCountText != null)
            {
                int count = _viewModel.FilteredRows.Count;
                resultCountText.Text = count == 1 ? "1 connection" : $"{count} connections";
            }
        }
    }
}
