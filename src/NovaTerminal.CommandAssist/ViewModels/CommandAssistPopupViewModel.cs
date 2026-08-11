using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NovaTerminal.CommandAssist.ViewModels;

public sealed class CommandAssistPopupViewModel : INotifyPropertyChanged
{
    private bool _isVisible;
    private string _modeLabel = "Suggest";
    private string _queryText = string.Empty;
    private string _selectedBadgesText = string.Empty;
    private string _selectedMetadataText = string.Empty;
    private string _selectedDescriptionText = string.Empty;
    private string _selectedFooterText = string.Empty;
    private string _emptyStateText = string.Empty;
    private bool _hasSuggestions;
    private bool _showEmptyState;
    private int _selectedIndex = -1;
    private string _shortcutHintText = string.Empty;
    private string _attributionText = string.Empty;
    private string _integrationStatusText = string.Empty;
    private string _integrationGlyphText = string.Empty;
    private string _integrationStatusTooltip = string.Empty;

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

    /// <summary>
    /// The session's shell-integration state in words. No longer rendered in the popup - the footer
    /// shows <see cref="IntegrationGlyphText"/> - but still the accessible name behind the dot.
    /// </summary>
    /// <remarks>
    /// The label lost its place when the popup footer became one line (UX round 7): the metadata, the
    /// hint strip and the indicator now share a strip that used to carry the hint strip alone, and a
    /// five-to-ten character word that answers a once-per-session question is exactly the thing that
    /// gives its width up first. The indicator itself does not go, it shrinks to the dot - the same
    /// collapse the bubble already makes, for the same reason. See
    /// <see cref="CommandAssistBubbleViewModel.IntegrationGlyphText"/>.
    /// </remarks>
    public string IntegrationStatusText
    {
        get => _integrationStatusText;
        set => SetField(ref _integrationStatusText, value);
    }

    /// <summary>
    /// The one-character form of the integration indicator, and the only form the popup renders.
    /// </summary>
    /// <remarks>
    /// Deliberately the bubble's glyph pair rather than a second vocabulary: the two surfaces are
    /// alternatives to each other, never on screen together, so a user who learns the hollow dot on
    /// one must not meet a different mark for the same state on the other.
    /// </remarks>
    public string IntegrationGlyphText
    {
        get => _integrationGlyphText;
        set => SetField(ref _integrationGlyphText, value);
    }

    /// <summary>The sentence behind <see cref="IntegrationStatusText"/>, shown on hover.</summary>
    public string IntegrationStatusTooltip
    {
        get => _integrationStatusTooltip;
        set => SetField(ref _integrationStatusTooltip, value);
    }

    public string QueryText
    {
        get => _queryText;
        set => SetField(ref _queryText, value);
    }

    // The popup used to carry a TopSuggestionText of its own, relayed from the bar view-model. It was
    // the headline of the detail panel; with the panel gone (UX round 7) and the rows running full
    // width, the selected row *is* that string, and a second copy of it had no renderer and no reader.
    // CommandAssistBarViewModel.TopSuggestionText stays - it still drives the bubble summary.

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

    /// <summary>
    /// Everything the footer says about the selected row, already joined into one line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A derived view of <see cref="SelectedDescriptionText"/>, <see cref="SelectedBadgesText"/> and
    /// <see cref="SelectedMetadataText"/>, composed in exactly one place -
    /// <c>CommandAssistBarViewModel.SyncPresentationState</c> - rather than assembled out of three
    /// bindings in the XAML. Three <c>TextBlock</c>s in a row cannot share one ellipsis: each would
    /// trim independently, so a long working directory would clip its own segment while the badge
    /// beside it kept every character. One string trims once, at the end, which is what a reader
    /// expects from a line that ran out of room.
    /// </para>
    /// <para>
    /// The three sources stay: the bubble reads them, and they are what the controller actually
    /// knows. This is the popup's rendering of them, not a replacement for them.
    /// </para>
    /// </remarks>
    public string SelectedFooterText
    {
        get => _selectedFooterText;
        set => SetField(ref _selectedFooterText, value);
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
