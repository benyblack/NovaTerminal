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
    private int _selectedIndex = -1;
    private string _shortcutHintText = string.Empty;
    private string _attributionText = string.Empty;

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

    /// <summary>
    /// Which row is selected, or <c>-1</c>. The rows carry their own <c>IsSelected</c> for rendering;
    /// this exists so the view can scroll the selected container into view without walking the list.
    /// </summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetField(ref _selectedIndex, value);
    }

    /// <summary>The same learnable hint strip the bubble shows, repeated in the popup footer.</summary>
    public string ShortcutHintText
    {
        get => _shortcutHintText;
        set => SetField(ref _shortcutHintText, value);
    }

    /// <summary>
    /// The licence credit for the content currently on screen, or empty when there is none to give.
    /// </summary>
    /// <remarks>
    /// <para>
    /// V2 Phase 4b. The bundled command catalogue is derived from tldr-pages, which is CC-BY-SA 4.0
    /// and therefore requires attribution wherever its content appears. This app has no About dialog,
    /// so the credit goes in the footer of the surface that shows the content, where a user reading a
    /// tldr example can see where it came from without going looking.
    /// </para>
    /// <para>
    /// Empty in every mode except Help, and that is not laziness about placement: Suggest and Search
    /// rank the user's own history, and a licence line under rows nobody licensed would be noise
    /// claiming to be a credit.
    /// </para>
    /// </remarks>
    public string AttributionText
    {
        get => _attributionText;
        set
        {
            if (_attributionText == value)
            {
                return;
            }

            _attributionText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AttributionText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAttribution)));
        }
    }

    /// <summary>Whether the footer's attribution row has anything to show.</summary>
    public bool HasAttribution => !string.IsNullOrWhiteSpace(AttributionText);

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
