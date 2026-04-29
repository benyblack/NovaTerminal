using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using NovaTerminal.Models;
using NovaTerminal.Services.Ssh;

namespace NovaTerminal.Controls;

public partial class RemotePathTextBox : UserControl
{
    private readonly TextBox _pathTextBox;
    private readonly Popup _suggestionsPopup;
    private readonly Border _suggestionsBorder;
    private readonly ListBox _suggestionsList;
    private readonly ObservableCollection<RemotePathSuggestion> _suggestions = [];
    private CancellationTokenSource? _suggestionsCts;
    private bool _suppressSuggestionRefresh;

    public RemotePathTextBox()
    {
        InitializeComponent();

        _pathTextBox = this.FindControl<TextBox>("PathTextBox")
            ?? throw new InvalidOperationException("PathTextBox was not found.");
        _suggestionsPopup = this.FindControl<Popup>("SuggestionsPopup")
            ?? throw new InvalidOperationException("SuggestionsPopup was not found.");
        _suggestionsBorder = this.FindControl<Border>("SuggestionsBorder")
            ?? throw new InvalidOperationException("SuggestionsBorder was not found.");
        _suggestionsList = this.FindControl<ListBox>("SuggestionsList")
            ?? throw new InvalidOperationException("SuggestionsList was not found.");

        _suggestionsPopup.PlacementTarget = _pathTextBox;
        _suggestionsList.ItemsSource = _suggestions;

        _pathTextBox.PropertyChanged += OnPathTextBoxPropertyChanged;
        _pathTextBox.KeyDown += OnPathTextBoxKeyDown;
        _pathTextBox.GotFocus += (_, _) => ReopenSuggestionsIfAvailable();
        _suggestionsList.DoubleTapped += OnSuggestionsListDoubleTapped;
        _suggestionsList.KeyDown += OnSuggestionsListKeyDown;
        _suggestionsList.SelectionChanged += OnSuggestionsListSelectionChanged;
        _pathTextBox.SizeChanged += (_, _) => UpdatePopupWidth();

        UpdatePopupWidth();
    }

    public Guid ProfileId { get; set; }

    public Guid SessionId { get; set; }

    public IRemotePathAutocompleteService? AutocompleteService { get; set; } = new RemotePathAutocompleteService();

    public string Text
    {
        get => _pathTextBox.Text ?? string.Empty;
        set => _pathTextBox.Text = value;
    }

    public string? Watermark
    {
        get => _pathTextBox.Watermark?.ToString();
        set => _pathTextBox.Watermark = value;
    }

    public event EventHandler? TextChanged;

    internal TimeSpan SuggestionDebounceDelay { get; set; } = TimeSpan.FromMilliseconds(180);

    internal bool AreSuggestionsOpenForTest => _suggestionsPopup.IsOpen;

    internal IReadOnlyList<RemotePathSuggestion> GetSuggestionsForTest()
    {
        return _suggestions.ToList();
    }

    internal async Task RefreshSuggestionsForTestAsync()
    {
        await RefreshSuggestionsAsync();
    }

    internal void SetTextForTest(string text)
    {
        Text = text;
    }

    internal void SelectSuggestionForTest(int index)
    {
        _suggestionsList.SelectedIndex = index;
        _ = ApplySelectedSuggestionAsync(reopenForDirectory: false);
    }

    internal Task AcceptSelectedSuggestionForTestAsync(Key key)
    {
        return ApplySelectedSuggestionAsync(reopenForDirectory: key == Key.Tab);
    }

    internal bool FocusInnerTextBoxForTest()
    {
        return _pathTextBox.Focus();
    }

    public bool FocusTextBox()
    {
        return _pathTextBox.Focus();
    }

    private void OnPathTextBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextBox.TextProperty)
        {
            return;
        }

        TextChanged?.Invoke(this, EventArgs.Empty);
        if (!_suppressSuggestionRefresh)
        {
            _ = QueueSuggestionsRefreshAsync();
        }
    }

    private async Task QueueSuggestionsRefreshAsync()
    {
        _suggestionsCts?.Cancel();
        _suggestionsCts?.Dispose();

        CancellationTokenSource cts = new();
        _suggestionsCts = cts;

        try
        {
            if (SuggestionDebounceDelay > TimeSpan.Zero)
            {
                await Task.Delay(SuggestionDebounceDelay, cts.Token);
            }

            await RefreshSuggestionsAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshSuggestionsAsync(CancellationToken cancellationToken = default)
    {
        IRemotePathAutocompleteService? autocompleteService = AutocompleteService;
        if (autocompleteService == null ||
            ProfileId == Guid.Empty ||
            SessionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(Text))
        {
            ClearSuggestions();
            return;
        }

        IReadOnlyList<RemotePathSuggestion> suggestions = await autocompleteService.GetSuggestionsAsync(
            ProfileId,
            SessionId,
            Text,
            cancellationToken);

        ReplaceSuggestions(suggestions);
    }

    private void ReplaceSuggestions(IReadOnlyList<RemotePathSuggestion> suggestions)
    {
        _suggestions.Clear();
        foreach (RemotePathSuggestion suggestion in suggestions)
        {
            _suggestions.Add(suggestion);
        }

        if (_suggestions.Count == 0)
        {
            _suggestionsPopup.IsOpen = false;
            return;
        }

        _suggestionsList.SelectedIndex = 0;
        _suggestionsPopup.IsOpen = true;
        UpdatePopupWidth();
    }

    private void ReopenSuggestionsIfAvailable()
    {
        if (_suggestions.Count > 0)
        {
            _suggestionsPopup.IsOpen = true;
            UpdatePopupWidth();
        }
    }

    private void ClearSuggestions()
    {
        _suggestions.Clear();
        _suggestionsList.SelectedIndex = -1;
        _suggestionsPopup.IsOpen = false;
    }

    private void OnPathTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_suggestionsPopup.IsOpen || _suggestions.Count == 0)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (TryStartApplySelectedSuggestionAsync(reopenForDirectory: false))
                {
                    e.Handled = true;
                }

                break;
            case Key.Tab:
                if (TryStartApplySelectedSuggestionAsync(reopenForDirectory: true))
                {
                    e.Handled = true;
                }

                break;
            case Key.Escape:
                _suggestionsPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnSuggestionsListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (TryStartApplySelectedSuggestionAsync(reopenForDirectory: false))
            {
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Tab)
        {
            if (TryStartApplySelectedSuggestionAsync(reopenForDirectory: true))
            {
                e.Handled = true;
            }
        }
    }

    private void OnSuggestionsListDoubleTapped(object? sender, RoutedEventArgs e)
    {
        _ = ApplySelectedSuggestionAsync(reopenForDirectory: false);
    }

    private void OnSuggestionsListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suggestionsList.SelectedIndex >= 0 && _suggestionsList.SelectedItem != null)
        {
            _suggestionsList.ScrollIntoView(_suggestionsList.SelectedItem);
        }
    }

    private void MoveSelection(int delta)
    {
        if (_suggestions.Count == 0)
        {
            return;
        }

        int currentIndex = _suggestionsList.SelectedIndex;
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        int nextIndex = Math.Clamp(currentIndex + delta, 0, _suggestions.Count - 1);
        _suggestionsList.SelectedIndex = nextIndex;
    }

    private bool TryStartApplySelectedSuggestionAsync(bool reopenForDirectory)
    {
        if (_suggestionsList.SelectedItem is not RemotePathSuggestion)
        {
            return false;
        }

        _ = ApplySelectedSuggestionAsync(reopenForDirectory);
        return true;
    }

    private async Task ApplySelectedSuggestionAsync(bool reopenForDirectory)
    {
        if (_suggestionsList.SelectedItem is not RemotePathSuggestion suggestion)
        {
            return;
        }

        _suppressSuggestionRefresh = true;
        try
        {
            Text = suggestion.IsDirectory
                ? EnsureTrailingSlash(suggestion.FullPath)
                : suggestion.FullPath;
        }
        finally
        {
            _suppressSuggestionRefresh = false;
        }

        _pathTextBox.CaretIndex = Text.Length;
        ClearSuggestions();

        if (reopenForDirectory && suggestion.IsDirectory)
        {
            await RefreshSuggestionsAsync();
            _pathTextBox.Focus();
        }
    }

    private static string EnsureTrailingSlash(string path)
    {
        return path.EndsWith("/", StringComparison.Ordinal) ? path : $"{path}/";
    }

    private void UpdatePopupWidth()
    {
        _suggestionsBorder.Width = Math.Max(_pathTextBox.Bounds.Width, 220);
    }
}
