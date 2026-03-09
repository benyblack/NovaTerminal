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

    public ObservableCollection<CommandAssistSuggestionItemViewModel> Suggestions { get; } = new();

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
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
