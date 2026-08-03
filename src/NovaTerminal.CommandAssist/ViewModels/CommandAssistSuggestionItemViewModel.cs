using System.ComponentModel;
using System.Runtime.CompilerServices;
using NovaTerminal.CommandAssist.Models;

namespace NovaTerminal.CommandAssist.ViewModels;

/// <summary>
/// One row in the popup list.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this stopped being a record (V2 Phase 3a).</strong> Selection used to be baked in at
/// construction, so moving the selection meant clearing the <c>ObservableCollection</c> and rebuilding
/// every row. That is invisible while the list is keyboard-only and fatal once it is not: a rebuild
/// replaces the containers under the pointer, so hover state is lost on every arrow key, the
/// <c>ScrollViewer</c> jumps back to the top, and a click lands on a control that no longer exists by
/// the time the selection it caused is applied.
/// </para>
/// <para>
/// So the rows are now stable objects with a mutable selection, and the controller mutates rather
/// than rebuilds when only the selection changed. Reference identity is load-bearing in the other
/// direction too: the view maps a clicked container back to an index with
/// <c>Suggestions.IndexOf(item)</c>, which a record's value equality would answer with the first
/// row that happens to carry the same text.
/// </para>
/// </remarks>
public sealed class CommandAssistSuggestionItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public CommandAssistSuggestionItemViewModel(
        string displayText,
        string descriptionText,
        string badgesText,
        string metadataText,
        bool isSelected,
        AssistSuggestionType type)
    {
        DisplayText = displayText;
        DescriptionText = descriptionText;
        BadgesText = badgesText;
        MetadataText = metadataText;
        Type = type;
        _isSelected = isSelected;
    }

    public string DisplayText { get; }

    public string DescriptionText { get; }

    public string BadgesText { get; }

    public string MetadataText { get; }

    public AssistSuggestionType Type { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionGlyph));
        }
    }

    /// <summary>
    /// The caret drawn in the gutter. Derived from <see cref="IsSelected"/> rather than stored, so
    /// the two can no longer disagree - which they could when both were constructor arguments.
    /// </summary>
    public string SelectionGlyph => IsSelected ? ">" : " ";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
