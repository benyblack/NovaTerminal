using System;
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
        AssistSuggestionType type,
        string queryText = "",
        int? exitCode = null)
    {
        DisplayText = displayText;
        DescriptionText = descriptionText;
        BadgesText = badgesText;
        MetadataText = metadataText;
        Type = type;
        _isSelected = isSelected;
        ExitCode = exitCode;

        (HighlightStart, HighlightLength) = FindHighlight(displayText, queryText);
    }

    public string DisplayText { get; }

    public string DescriptionText { get; }

    public string BadgesText { get; }

    public string MetadataText { get; }

    public AssistSuggestionType Type { get; }

    /// <summary>How the command behind this row ended, or <see langword="null"/> when nothing knows.</summary>
    public int? ExitCode { get; }

    /// <summary>
    /// Whether the command behind this row is known to have failed.
    /// </summary>
    /// <remarks>
    /// A null exit code is <em>not</em> a failure. Snippets have never run, a markless session never
    /// learned how its commands ended, and dimming either of those would be the row telling the user
    /// something the product does not know. Only an exit code that exists and is non-zero earns the
    /// dim. This replaces the failure badge: a row the user is scanning does not need the number, it
    /// needs to be less interesting than the rows beside it.
    /// </remarks>
    public bool HasFailed => ExitCode.HasValue && ExitCode.Value != 0;

    /// <summary>
    /// Where the user's query matches <see cref="DisplayText"/>, or <c>-1</c> when it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why the offset is recomputed here rather than carried from the matcher.</strong>
    /// <c>CommandAssistSuggestionEngine.ScoreText</c> records no positions - it answers "how well"
    /// and never "where" - and threading a position through <c>AssistSuggestion</c> would widen a
    /// record with a dozen construction sites for the benefit of one <c>TextBlock</c>.
    /// </para>
    /// <para>
    /// A plain case-insensitive <c>IndexOf</c> is not an approximation of what the matcher did: three
    /// of its four text-match paths - whole-string prefix, token prefix, contains - are satisfied by a
    /// contiguous case-insensitive occurrence of the whole query, so <c>IndexOf</c> finds exactly the
    /// run that earned the score. The fourth, the subsequence path, has no contiguous run to point at
    /// by construction; highlighting nothing there is the honest answer, and inventing a span the
    /// matcher never used would be worse than none.
    /// </para>
    /// </remarks>
    public int HighlightStart { get; }

    /// <summary>Length of the matched run, or <c>0</c> when there is nothing to highlight.</summary>
    public int HighlightLength { get; }

    /// <summary>
    /// Whether this row carries the "Pinned" badge.
    /// </summary>
    /// <remarks>
    /// Row density (owner request, V2 row-density round) moved every other per-row caption -
    /// description, metadata, the full badge line - out of the row, into the one footer line that
    /// describes whichever row is selected. Pinned is the one exception: it changes which rows are
    /// worth a second look while browsing, not just what the reader learns about the row already
    /// selected, so it earns a single glyph in the row rather than a trip to the footer.
    /// Derived from <see cref="BadgesText"/> rather than a new constructor parameter, so
    /// every existing call site - and every "Pinned" badge origin in
    /// <c>CommandAssistSuggestionEngine.BuildSnippetBadges</c> - stays the single source of truth.
    /// </remarks>
    public bool IsPinned => BadgesText.Contains("Pinned", StringComparison.Ordinal);

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

    /// <summary>
    /// Locates the query inside the row text. See <see cref="HighlightStart"/> for why this is a
    /// plain <c>IndexOf</c> and not a reconstruction of the matcher's scoring.
    /// </summary>
    internal static (int Start, int Length) FindHighlight(string displayText, string queryText)
    {
        if (string.IsNullOrEmpty(displayText) || string.IsNullOrWhiteSpace(queryText))
        {
            return (-1, 0);
        }

        // Trimmed to match ScoreText, which trims both sides before comparing. Without it a query
        // the user is still typing ("git ") would fail to find a run the matcher scored a hit on.
        string needle = queryText.Trim();
        if (needle.Length == 0 || needle.Length > displayText.Length)
        {
            return (-1, 0);
        }

        int start = displayText.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        return start < 0 ? (-1, 0) : (start, needle.Length);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
