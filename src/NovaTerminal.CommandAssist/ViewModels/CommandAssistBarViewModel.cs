using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace NovaTerminal.CommandAssist.ViewModels;

public sealed class CommandAssistBarViewModel : INotifyPropertyChanged
{
    /// <summary>Shown while a row is selected in an open popup, where <c>Enter</c> inserts it.</summary>
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

    /// <summary>Whether the hint strip is currently promising <c>Enter</c>. Presentation state, so it follows the probe.</summary>
    public bool IsAcceptOnEnterArmed { get; private set; }

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
        string hintText = acceptOnEnterArmed
            ? BrowseHintText
            : isSelectionUpOwned
                ? IdleHintText
                : PassiveHintText;

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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
