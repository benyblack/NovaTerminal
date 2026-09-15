using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using NovaTerminal.CommandAssist.Models;

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

    /// <summary>The chip text for a session whose shell is emitting OSC 133 marks.</summary>
    internal const string IntegratedStatusText = "integrated";

    /// <summary>The chip text for a session with no marks: capture is heuristic and Fix has no output tail.</summary>
    internal const string BasicStatusText = "basic";

    /// <summary>
    /// The collapsed form of the integrated chip. A filled dot, chosen over a letter or an icon glyph
    /// because it renders in every monospace font a terminal user is plausibly running and carries no
    /// language.
    /// </summary>
    internal const string IntegratedStatusGlyph = "●";

    /// <summary>
    /// The collapsed form of the basic chip: the same dot, hollow. The pair reads as one indicator with
    /// two states rather than as two unrelated marks, which is the point - "basic" is a mode, not a
    /// fault, so it gets the same shape rather than a warning sign.
    /// </summary>
    internal const string BasicStatusGlyph = "○";

    internal const string IntegratedStatusTooltip =
        "Shell integration is live: commands, exit codes and working directories come from the shell itself.";

    internal const string BasicStatusTooltip =
        "No shell integration on this session. Command Assist still works, but history is captured heuristically and failing commands have no output to diagnose.";

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
    private int _suggestionCount;
    private bool _hasSuggestions;
    private bool _showEmptyState;
    private bool _isPopupOpen;
    private string _attributionText = string.Empty;
    private AssistHintDetail _bubbleHintDetail = AssistHintDetail.Full;
    private bool _isShellIntegrationLive;
    private bool _allowBubbleQueryText = true;

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

    /// <summary>
    /// How many rows the popup is showing. Published so the pane can size the popup to its content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A property rather than a <c>CollectionChanged</c> subscription on <see cref="Suggestions"/>,
    /// because the controller rebuilds that collection with a <c>Clear</c> followed by N <c>Add</c>s:
    /// a listener would see N+1 notifications per ranking pass, each one re-running placement, and
    /// <c>Ctrl+R</c> re-ranks on every keystroke. This fires once per pass at most.
    /// </para>
    /// <para>
    /// The change gate is <em>not</em> what keeps placement cheap, and it should not be read that way.
    /// <see cref="QueryText"/>, <see cref="ModeLabel"/> and <see cref="AttributionText"/> each publish
    /// their own change, and <c>TerminalPane.OnCommandAssistViewModelPropertyChanged</c> re-places on
    /// any property of this view-model - so the ordinary keystroke, where the query moved and the row
    /// count happened not to, re-places anyway. What this property adds is the one case none of the
    /// others cover: the row count changing with nothing else moving, which is a change in the popup's
    /// height that would otherwise go unannounced.
    /// </para>
    /// <para>
    /// The count and not the collection: nothing downstream needs the rows, only how many there are,
    /// and a value type cannot be mistaken for a live view of a list that is about to be rebuilt.
    /// </para>
    /// </remarks>
    public int SuggestionCount
    {
        get => _suggestionCount;
        set
        {
            if (_suggestionCount == value)
            {
                return;
            }

            _suggestionCount = value;
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

    /// <summary>
    /// The licence credit for the content currently shown, relayed to the popup footer. Set by
    /// <c>CommandAssistController</c> when Help publishes catalogue rows and cleared on every other
    /// surface (V2 Phase 4b).
    /// </summary>
    public string AttributionText
    {
        get => _attributionText;
        set
        {
            string next = value ?? string.Empty;
            if (_attributionText == next)
            {
                return;
            }

            _attributionText = next;
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

    /// <summary>
    /// How much of the shortcut hint the bubble has room for. Set by the host from the pane width.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Progressive collapse, because the hint must lose before the content does.</strong>
    /// Previously this was a single boolean: the hint took its full width until the compact threshold
    /// and then vanished. Between those two states lies the width the owner was actually running at,
    /// where an <c>Auto</c> hint column quietly ate the suggestion down to six characters. The middle
    /// rung drops the words and keeps the keys - the strip's job at that width is to remind, not to
    /// teach, and the popup footer still teaches.
    /// </para>
    /// <para>
    /// Only the bubble's strip collapses. The popup has room and keeps the full text.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Whether the bubble has room to show the query alongside the suggestion. Host-set from the
    /// compact-layout decision; the bubble may still suppress it for a reason of its own (Fix mode).
    /// </summary>
    /// <remarks>
    /// The width budget and the editorial judgement are two different questions, and this is the
    /// first. The host knows how wide the bubble is; only <see cref="ApplyBubbleContent"/> knows
    /// whether the query is worth the space at that width.
    /// </remarks>
    public bool AllowBubbleQueryText
    {
        get => _allowBubbleQueryText;
        set
        {
            if (_allowBubbleQueryText == value)
            {
                return;
            }

            _allowBubbleQueryText = value;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

    public AssistHintDetail BubbleHintDetail
    {
        get => _bubbleHintDetail;
        set
        {
            if (_bubbleHintDetail == value)
            {
                return;
            }

            _bubbleHintDetail = value;
            OnPropertyChanged();
            SyncPresentationState();
        }
    }

    /// <summary>
    /// Whether this session's shell is emitting OSC 133 marks. Drives the integration chip.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>AssistSessionContext.IsShellIntegrationLive</c>, republished by
    /// <c>CommandAssistController</c> whenever it refreshes the surface. See
    /// <see cref="CommandAssistBubbleViewModel.IntegrationStatusText"/>.
    /// </remarks>
    public bool IsShellIntegrationLive
    {
        get => _isShellIntegrationLive;
        set
        {
            if (_isShellIntegrationLive == value)
            {
                return;
            }

            _isShellIntegrationLive = value;
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
        string hintText = BuildHintText(acceptOnEnterArmed, isSelectionUpOwned, isInsertionAvailable, terse: false);
        string bubbleHintText = BubbleHintDetail == AssistHintDetail.Terse
            ? BuildHintText(acceptOnEnterArmed, isSelectionUpOwned, isInsertionAvailable, terse: true)
            : hintText;

        // Exactly one surface at a time (UX-polish round, issue 6). The bubble used to follow
        // IsVisible alone while the popup followed IsVisible && IsPopupOpen, so opening the popup put
        // both on screen: the owner's screenshot has a "History | vim .env | Enter insert" bubble
        // rendered below an open History popup whose first row is the same `vim .env`. Two surfaces
        // saying the same thing is not twice the information.
        //
        // The popup wins, rather than the bubble becoming a caption attached to it. The popup already
        // renders the mode, the query, every row including the one the bubble was summarising, and a
        // footer hint with room for the full text - so a caption would have nothing left to say that
        // is not directly above it. Hiding one surface is also the change that cannot introduce a new
        // layout to get wrong at a narrow width, which is what the rest of this round is about.
        Bubble.IsVisible = IsVisible && !IsPopupOpen;
        Bubble.ModeLabel = ModeLabel;
        Bubble.ShortcutHintText = bubbleHintText;
        Bubble.ShowShortcutHint = BubbleHintDetail != AssistHintDetail.Hidden;
        ApplyBubbleContent();
        ApplyIntegrationStatus();

        Popup.ShortcutHintText = hintText;
        Popup.SelectedIndex = SelectedIndex;
        Popup.IsVisible = IsVisible && IsPopupOpen;
        Popup.ModeLabel = ModeLabel;
        Popup.QueryText = QueryText;
        Popup.SelectedBadgesText = SelectedBadgesText;
        Popup.SelectedMetadataText = SelectedMetadataText;
        Popup.SelectedDescriptionText = SelectedDescriptionText;
        Popup.SelectedFooterText = BuildSelectedFooterText();
        Popup.EmptyStateText = EmptyStateText;
        Popup.HasSuggestions = HasSuggestions;
        Popup.ShowEmptyState = ShowEmptyState;
        Popup.AttributionText = AttributionText;
    }

    /// <summary>
    /// Joins whatever the surface knows about the selected row into the popup's single footer line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Description first, then badges, then metadata: least to most mechanical, so the clause the
    /// reader is most likely to want survives the ellipsis. Empty parts are dropped rather than
    /// joined through, because a line that opens with a separator reads as content that failed to
    /// load. With every part empty - no selection, or a row that carries nothing - the result is the
    /// empty string, and the footer renders nothing rather than a bare "  |  ".
    /// </para>
    /// <para>
    /// The separator is the one <c>CommandAssistController.BuildMetadataText</c> already uses inside
    /// the metadata clause, so the whole line reads as one list rather than as two nested ones.
    /// </para>
    /// </remarks>
    internal string BuildSelectedFooterText()
    {
        return string.Join(
            "  |  ",
            new[] { SelectedDescriptionText, SelectedBadgesText, SelectedMetadataText }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>
    /// Decides what the bubble's two content columns say, and whether they are one string or two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Fix mode does not echo the failed command (UX-polish round, issue 4a).</strong> The
    /// owner's screenshot read <c>Fix | print -l $precmd_functions | Di...</c>: the headline was the
    /// command he had just watched fail, and the one new thing on the row - what to do about it - was
    /// truncated to two characters. Help and Fix both used to put their subject in the query field,
    /// which is right for Help (the thing you asked about is the caption) and wrong for Fix (the thing
    /// that failed is already on screen directly above, in the scrollback, in full). So Fix gives its
    /// width to the suggestion. The popup still shows the failed command, where there is room for
    /// both.
    /// </para>
    /// <para>
    /// Everything else is the fish-style continuation described on
    /// <see cref="CommandAssistBubbleViewModel.IsSummaryContinuation"/>.
    /// </para>
    /// </remarks>
    private void ApplyBubbleContent()
    {
        string summary = !string.IsNullOrWhiteSpace(TopSuggestionText)
            ? TopSuggestionText
            : EmptyStateText;
        string query = QueryText ?? string.Empty;

        bool isFixMode = string.Equals(ModeLabel, nameof(CommandAssistMode.Fix), StringComparison.OrdinalIgnoreCase);
        bool showQuery = AllowBubbleQueryText && !isFixMode && query.Length > 0;

        // The tail is only legible while the head is on screen, so the continuation is conditioned on
        // the query actually being rendered rather than merely being non-empty.
        bool isContinuation = showQuery &&
                              summary.Length > query.Length &&
                              summary.StartsWith(query, StringComparison.Ordinal);

        Bubble.QueryText = query;
        Bubble.ShowQueryText = showQuery;
        Bubble.IsSummaryContinuation = isContinuation;
        Bubble.SummaryText = isContinuation ? summary[query.Length..] : summary;
    }

    private void ApplyIntegrationStatus()
    {
        string text = IsShellIntegrationLive ? IntegratedStatusText : BasicStatusText;
        string tooltip = IsShellIntegrationLive ? IntegratedStatusTooltip : BasicStatusTooltip;

        Bubble.IntegrationStatusText = text;
        Bubble.IntegrationStatusTooltip = tooltip;
        Bubble.IntegrationGlyphText = IsShellIntegrationLive ? IntegratedStatusGlyph : BasicStatusGlyph;

        // The *label* is chrome and goes at the same width the hint strip goes: it answers a question
        // the user asks once a session, and the suggestion answers one they are asking right now. The
        // indicator itself does not go, it shrinks to a dot - see
        // CommandAssistBubbleViewModel.IntegrationGlyphText for why the previous rule (drop it whole)
        // meant the owner never saw it at all.
        bool showLabel = BubbleHintDetail == AssistHintDetail.Full;
        Bubble.ShowIntegrationStatus = showLabel;
        Bubble.ShowIntegrationGlyph = !showLabel;

        // The popup takes the collapsed form unconditionally. Its footer is one line shared by the
        // metadata, the hint strip and this, and the label is the clause with the least to say per
        // pixel; the dot keeps the indicator on screen at every width, which is the property the
        // bubble's own collapse exists to protect.
        Popup.IntegrationStatusText = text;
        Popup.IntegrationGlyphText = IsShellIntegrationLive ? IntegratedStatusGlyph : BasicStatusGlyph;
        Popup.IntegrationStatusTooltip = tooltip;
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
    /// <para>
    /// <strong>Browse is shed before insert (dogfood round 4, item 2).</strong> The terse rung used to
    /// keep all three clauses and merely drop their verbs, which is the right trade when the three are
    /// equally learnable and they are not. The owner's report was "no way to put it": he had found
    /// <c>Down</c> - pressing an arrow key at a prompt is a free experiment anyone runs - and had not
    /// found the insert chord, which nothing else in a terminal teaches and which no amount of poking
    /// discovers. So the clause that survives one rung longer is the one the surface cannot be used
    /// without. The full rung still advertises both.
    /// </para>
    /// </remarks>
    private string BuildHintText(bool acceptOnEnterArmed, bool isSelectionUpOwned, bool isInsertionAvailable, bool terse)
    {
        AssistShortcutHintLabels labels = _shortcutHintLabels;

        if (acceptOnEnterArmed)
        {
            return terse
                ? $"{labels.Accept}  |  {labels.SelectionUp}/{labels.SelectionDown}  |  {labels.Dismiss}"
                : $"{labels.Accept} insert  |  {labels.SelectionUp}/{labels.SelectionDown} browse  |  {labels.Dismiss} close";
        }

        string browse = isSelectionUpOwned
            ? $"{labels.SelectionUp}/{labels.SelectionDown}"
            : $"{labels.SelectionDown}";

        if (terse)
        {
            // Keys only, and browse dropped: at this width the strip has room for the action and the
            // exit, and the action is the one the user cannot guess. When the line cannot be appended
            // to there is no action to advertise, so browse comes back rather than leaving a strip
            // that only says "Esc".
            return isInsertionAvailable
                ? $"{labels.Insert}  |  {labels.Dismiss}"
                : $"{browse}  |  {labels.Dismiss}";
        }

        return isInsertionAvailable
            ? $"{browse} browse  |  {labels.Insert} insert  |  {labels.Dismiss} close"
            : $"{browse} browse  |  {labels.Dismiss} close";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
