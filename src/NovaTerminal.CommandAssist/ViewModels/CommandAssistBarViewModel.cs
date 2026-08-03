using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace NovaTerminal.CommandAssist.ViewModels;

public sealed class CommandAssistBarViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// Shown while a row is selected in an open popup, where <c>Enter</c> inserts it, with the default
    /// keyboard. Kept as a constant because it is the string the shipped defaults must still produce -
    /// see <see cref="BuildHintText"/>.
    /// </summary>
    internal const string BrowseHintText = "Enter insert  |  Up/Down browse  |  Esc close";

    /// <summary>
    /// Shown on a surface the user summoned that is not (yet) browsing a row. <c>Enter</c> is the
    /// shell's in this state, so the hint must not promise it: a hint strip that advertises a key the
    /// surface does not own is worse than no hint strip.
    /// </summary>
    internal const string IdleHintText = "Up/Down browse  |  Ctrl+Enter insert  |  Esc close";

    /// <summary>
    /// Shown on the passive typing bubble, where <c>Up</c> is the shell's history recall and only
    /// <c>Down</c> opens the list.
    /// </summary>
    /// <remarks>
    /// The same rule as <see cref="IdleHintText"/> applied to the key the PR #290 review gave back to
    /// the shell. Promising "Up/Down browse" on a surface that owns exactly one of them is the bug this
    /// constant exists to avoid, and it is the strip's whole job to be believable.
    /// </remarks>
    internal const string PassiveHintText = "Down browse  |  Ctrl+Enter insert  |  Esc close";

    private AssistShortcutHintLabels _shortcutHintLabels = AssistShortcutHintLabels.Default;
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

    /// <summary>
    /// Answers "does an unmodified <c>Enter</c> insert the selected row right now". Installed by
    /// <c>CommandAssistController</c>, which owns the state machine that decides it.
    /// </summary>
    /// <remarks>
    /// A probe rather than a settable bool, and that is the whole point. The hint strip has to be
    /// right in every state the surface can be in, and there are a dozen places in the controller
    /// that change one of the inputs (visibility, popup, mode, selection). A pushed bool would need
    /// updating at all of them and would be wrong at whichever one was forgotten; pulling the answer
    /// during <see cref="SyncPresentationState"/> - which already runs on every one of those changes -
    /// keeps one implementation of the rule, in the state machine, and no synchronization to get
    /// wrong. Null (no controller) reads as "not armed", which is what a bare view-model in a test or
    /// a designer should say.
    /// </remarks>
    internal Func<bool>? AcceptOnEnterProbe { get; set; }

    /// <summary>
    /// Answers "does <c>Up</c> belong to Command Assist right now". Installed by
    /// <c>CommandAssistController</c> alongside <see cref="AcceptOnEnterProbe"/>, for the same reason
    /// and read at the same moment.
    /// </summary>
    /// <remarks>
    /// Null reads as "owned", which keeps a bare view-model - a designer, a test that builds one
    /// directly - on the fuller hint rather than inventing a passive state it is not in.
    /// </remarks>
    internal Func<bool>? SelectionUpOwnedProbe { get; set; }

    /// <summary>
    /// Answers "would an accept actually insert right now", i.e. whether the command line the rows were
    /// ranked against is one a suffix can be appended to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PR #293 review.</strong> Installed by <c>CommandAssistController</c> beside the other two
    /// probes and read at the same moment. It exists because
    /// <c>CommandAssistInsertionPlanner.TryCreateInsertion</c> refuses whenever the cursor is not at the
    /// end of the painted line - which, with PSReadLine's inline prediction on, is the entire time the
    /// bubble is up. The strip was promising a key that could not work.
    /// </para>
    /// <para>
    /// Null reads as "available", which keeps a bare view-model - a designer, a test that builds one
    /// directly - on the fuller hint rather than hiding a clause on a surface that has no controller to
    /// ask.
    /// </para>
    /// </remarks>
    internal Func<bool>? InsertionAvailableProbe { get; set; }

    /// <summary>Whether the hint strip is currently promising <c>Enter</c>. Presentation state, so it follows the probe.</summary>
    public bool IsAcceptOnEnterArmed { get; private set; }

    /// <summary>
    /// The key names the hint strip renders. Set by the host from the shortcut catalogue; defaults to
    /// the shipped keyboard.
    /// </summary>
    public AssistShortcutHintLabels ShortcutHintLabels
    {
        get => _shortcutHintLabels;
        set
        {
            AssistShortcutHintLabels next = value ?? AssistShortcutHintLabels.Default;
            if (_shortcutHintLabels == next)
            {
                return;
            }

            _shortcutHintLabels = next;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

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

            // Selection is an input to the hint strip and to the popup's scroll-into-view, so it has
            // to publish like every other presentation fact. It did not before, because nothing
            // downstream cared which row was selected.
            SyncPresentationState();
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

    /// <summary>
    /// Recomputes the derived surface state. Called from every setter that feeds it, which is why the
    /// hint strip cannot drift out of step with the keyboard model.
    /// </summary>
    internal void SyncPresentationState()
    {
        bool acceptOnEnterArmed = AcceptOnEnterProbe?.Invoke() ?? false;
        if (acceptOnEnterArmed != IsAcceptOnEnterArmed)
        {
            IsAcceptOnEnterArmed = acceptOnEnterArmed;
            OnPropertyChanged(nameof(IsAcceptOnEnterArmed));
        }

        bool isSelectionUpOwned = SelectionUpOwnedProbe?.Invoke() ?? true;
        bool isInsertionAvailable = InsertionAvailableProbe?.Invoke() ?? true;
        string hintText = BuildHintText(acceptOnEnterArmed, isSelectionUpOwned, isInsertionAvailable);

        Bubble.IsVisible = IsVisible;
        Bubble.ModeLabel = ModeLabel;
        Bubble.QueryText = QueryText;
        Bubble.ShortcutHintText = hintText;
        Bubble.SummaryText = !string.IsNullOrWhiteSpace(TopSuggestionText)
            ? TopSuggestionText
            : EmptyStateText;

        Popup.ShortcutHintText = hintText;
        Popup.SelectedIndex = SelectedIndex;
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

    /// <summary>
    /// Renders the hint strip for the current state out of the current key names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three states, unchanged from Phase 3a - browsing a row, a summoned surface that is not browsing,
    /// and the passive bubble that owns only one arrow - with the key names now variable. With
    /// <see cref="AssistShortcutHintLabels.Default"/> each branch produces its constant verbatim, which
    /// is the property the tests pin.
    /// </para>
    /// <para>
    /// <strong>The insert clause is now conditional (PR #293 review).</strong> When the line cannot be
    /// appended to - a mid-line cursor, a multiline entry, a trimmed right prompt, or (the case that
    /// motivated it) a PSReadLine inline prediction painted past the cursor - the planner refuses the
    /// accept, so the clause is dropped rather than advertised. Browse and close still work in that
    /// state, which is why only the one clause goes. When <c>Enter</c> is armed the clause stays
    /// unconditionally: arming already requires an open popup with a selected row, and it is the
    /// keyboard's most load-bearing promise; removing it there would make the strip flicker between two
    /// shapes on a browse.
    /// </para>
    /// </remarks>
    private string BuildHintText(bool acceptOnEnterArmed, bool isSelectionUpOwned, bool isInsertionAvailable)
    {
        AssistShortcutHintLabels labels = _shortcutHintLabels;

        if (acceptOnEnterArmed)
        {
            return $"{labels.Accept} insert  |  {labels.SelectionUp}/{labels.SelectionDown} browse  |  {labels.Dismiss} close";
        }

        string browse = isSelectionUpOwned
            ? $"{labels.SelectionUp}/{labels.SelectionDown} browse"
            : $"{labels.SelectionDown} browse";

        return isInsertionAvailable
            ? $"{browse}  |  {labels.Insert} insert  |  {labels.Dismiss} close"
            : $"{browse}  |  {labels.Dismiss} close";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
