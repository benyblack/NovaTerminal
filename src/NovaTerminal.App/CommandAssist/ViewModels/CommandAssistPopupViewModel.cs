using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NovaTerminal.CommandAssist.ViewModels;

public sealed class CommandAssistPopupViewModel : INotifyPropertyChanged
{
    private bool _isVisible;
    private string _modeLabel = "Suggest";
    private string _queryText = string.Empty;
    private string _topSuggestionText = string.Empty;
    private string _selectedBadgesText = string.Empty;
    private string _selectedMetadataText = string.Empty;
    private string _selectedDescriptionText = string.Empty;
    private string _emptyStateText = string.Empty;
    private bool _hasSuggestions;
    private bool _showEmptyState;
    private bool _useCompactLayout;

    public CommandAssistPopupViewModel(ObservableCollection<CommandAssistSuggestionItemViewModel> suggestions)
    {
        Suggestions = suggestions;
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }

    public string ModeLabel
    {
        get => _modeLabel;
        set => SetField(ref _modeLabel, value);
    }

    public string QueryText
    {
        get => _queryText;
        set => SetField(ref _queryText, value);
    }

    public string TopSuggestionText
    {
        get => _topSuggestionText;
        set => SetField(ref _topSuggestionText, value);
    }

    public string SelectedBadgesText
    {
        get => _selectedBadgesText;
        set => SetField(ref _selectedBadgesText, value);
    }

    public string SelectedMetadataText
    {
        get => _selectedMetadataText;
        set => SetField(ref _selectedMetadataText, value);
    }

    public string SelectedDescriptionText
    {
        get => _selectedDescriptionText;
        set => SetField(ref _selectedDescriptionText, value);
    }

    public string EmptyStateText
    {
        get => _emptyStateText;
        set => SetField(ref _emptyStateText, value);
    }

    public bool HasSuggestions
    {
        get => _hasSuggestions;
        set => SetField(ref _hasSuggestions, value);
    }

    public bool ShowEmptyState
    {
        get => _showEmptyState;
        set => SetField(ref _showEmptyState, value);
    }

    public bool UseCompactLayout
    {
        get => _useCompactLayout;
        set
        {
            if (_useCompactLayout == value)
            {
                return;
            }

            _useCompactLayout = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseCompactLayout)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseExpandedLayout)));
        }
    }

    public bool UseExpandedLayout => !UseCompactLayout;

    public ObservableCollection<CommandAssistSuggestionItemViewModel> Suggestions { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
