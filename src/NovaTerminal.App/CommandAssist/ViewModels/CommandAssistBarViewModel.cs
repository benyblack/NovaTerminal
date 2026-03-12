using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace NovaTerminal.CommandAssist.ViewModels;

public sealed class CommandAssistBarViewModel : INotifyPropertyChanged
{
    private bool _isVisible;
    private string _modeLabel = "Suggest";
    private string _queryText = string.Empty;
    private string _topSuggestionText = string.Empty;
    private int _selectedIndex = -1;
    private string _selectedBadgesText = string.Empty;
    private string _selectedMetadataText = string.Empty;
    private string _selectedDescriptionText = string.Empty;
    private string _emptyStateText = string.Empty;
    private bool _hasSuggestions;
    private bool _showEmptyState;
    private bool _isPopupOpen;

    public CommandAssistBarViewModel()
    {
        Bubble = new CommandAssistBubbleViewModel();
        Popup = new CommandAssistPopupViewModel(Suggestions);
        SyncPresentationState();
    }

    public ObservableCollection<CommandAssistSuggestionItemViewModel> Suggestions { get; } = new();
    public CommandAssistBubbleViewModel Bubble { get; }
    public CommandAssistPopupViewModel Popup { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

    public string ModeLabel
    {
        get => _modeLabel;
        set
        {
            if (_modeLabel == value)
            {
                return;
            }

            _modeLabel = value;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

    public string QueryText
    {
        get => _queryText;
        set
        {
            if (_queryText == value)
            {
                return;
            }

            _queryText = value;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

    public string TopSuggestionText
    {
        get => _topSuggestionText;
        set
        {
            if (_topSuggestionText == value)
            {
                return;
            }

            _topSuggestionText = value;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value)
            {
                return;
            }

            _selectedIndex = value;
            OnPropertyChanged();
        }
    }

    public string SelectedBadgesText
    {
        get => _selectedBadgesText;
        set
        {
            if (_selectedBadgesText == value)
            {
                return;
            }

            _selectedBadgesText = value;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

    public string SelectedMetadataText
    {
        get => _selectedMetadataText;
        set
        {
            if (_selectedMetadataText == value)
            {
                return;
            }

            _selectedMetadataText = value;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

    public string SelectedDescriptionText
    {
        get => _selectedDescriptionText;
        set
        {
            if (_selectedDescriptionText == value)
            {
                return;
            }

            _selectedDescriptionText = value;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

    public string EmptyStateText
    {
        get => _emptyStateText;
        set
        {
            if (_emptyStateText == value)
            {
                return;
            }

            _emptyStateText = value;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

    public bool HasSuggestions
    {
        get => _hasSuggestions;
        set
        {
            if (_hasSuggestions == value)
            {
                return;
            }

            _hasSuggestions = value;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

    public bool ShowEmptyState
    {
        get => _showEmptyState;
        set
        {
            if (_showEmptyState == value)
            {
                return;
            }

            _showEmptyState = value;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

    public bool IsPopupOpen
    {
        get => _isPopupOpen;
        set
        {
            if (_isPopupOpen == value)
            {
                return;
            }

            _isPopupOpen = value;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SyncPresentationState()
    {
        Bubble.IsVisible = IsVisible;
        Bubble.ModeLabel = ModeLabel;
        Bubble.QueryText = QueryText;
        Bubble.SummaryText = !string.IsNullOrWhiteSpace(TopSuggestionText)
            ? TopSuggestionText
            : EmptyStateText;

        Popup.IsVisible = IsVisible && IsPopupOpen;
        Popup.ModeLabel = ModeLabel;
        Popup.QueryText = QueryText;
        Popup.TopSuggestionText = TopSuggestionText;
        Popup.SelectedBadgesText = SelectedBadgesText;
        Popup.SelectedMetadataText = SelectedMetadataText;
        Popup.SelectedDescriptionText = SelectedDescriptionText;
        Popup.EmptyStateText = EmptyStateText;
        Popup.HasSuggestions = HasSuggestions;
        Popup.ShowEmptyState = ShowEmptyState;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
