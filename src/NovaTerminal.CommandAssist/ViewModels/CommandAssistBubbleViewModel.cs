using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NovaTerminal.CommandAssist.ViewModels;

public sealed class CommandAssistBubbleViewModel : INotifyPropertyChanged
{
    private bool _isVisible;
    private string _modeLabel = "Suggest";
    private string _queryText = string.Empty;
    private string _summaryText = string.Empty;
    /// <remarks>
    /// Only a placeholder until the first <c>CommandAssistBarViewModel.SyncPresentationState</c>,
    /// which happens in that class's constructor. The V2 Phase 3a hint strip is state-dependent, so
    /// the constant that used to live here (and promised <c>Ctrl+Enter</c> unconditionally) is not a
    /// meaningful default any more.
    /// </remarks>
    private string _shortcutHintText = CommandAssistBarViewModel.IdleHintText;
    private bool _showQueryText = true;
    private bool _showShortcutHint = true;
    private bool _isSummaryContinuation;
    private string _integrationStatusText = string.Empty;
    private string _integrationStatusTooltip = string.Empty;
    private bool _showIntegrationStatus;
    private string _integrationGlyphText = string.Empty;
    private bool _showIntegrationGlyph;

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

    public string SummaryText
    {
        get => _summaryText;
        set => SetField(ref _summaryText, value);
    }

    public string ShortcutHintText
    {
        get => _shortcutHintText;
        set => SetField(ref _shortcutHintText, value);
    }

    public bool ShowQueryText
    {
        get => _showQueryText;
        set => SetField(ref _showQueryText, value);
    }

    /// <summary>
    /// Whether the shortcut hint strip is rendered at all. Set by the host from the compact-layout
    /// decision, like <see cref="ShowQueryText"/>.
    /// </summary>
    /// <remarks>
    /// The PR #290 review's fifth blocker. The bubble's hint sits in an <c>Auto</c> column and the
    /// suggestion summary in the <c>*</c> column, so at the 280 px bubble floor - which is exactly what
    /// a split SSH pane produces - the hint took its full ~200 px of "Up/Down browse | Ctrl+Enter
    /// insert | Esc close" and the summary, the only content in the bubble the user is actually reading,
    /// was left with what remained. On a pane that narrow the hint is the first thing to go: it teaches
    /// shortcuts, and the popup footer teaches the same ones with room to spare.
    /// </remarks>
    public bool ShowShortcutHint
    {
        get => _showShortcutHint;
        set => SetField(ref _showShortcutHint, value);
    }

    /// <summary>
    /// Whether <see cref="SummaryText"/> is the tail of a suggestion whose head is already on screen
    /// as <see cref="QueryText"/>, and should therefore be drawn butted up against it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The fish-style completion, and it is the single biggest win in the readability fix.</strong>
    /// The owner's bubble read <c>Suggest | dock | doc...</c>: the summary column was repeating the
    /// three characters he had already typed and then running out of room for the part he had not.
    /// When the top suggestion extends the query, the query <em>is</em> the head of the answer, so the
    /// summary only has to carry the tail - which roughly doubles the useful width of the column at no
    /// cost, because nothing was lost. Rendered as <c>doc</c> in white followed immediately by
    /// <c>ker compose up</c> in the suggestion colour, it reads as one string, which is what every
    /// shell with inline autosuggestion has trained people to expect.
    /// </para>
    /// <para>
    /// Only ever set when the query is actually being rendered - see
    /// <c>CommandAssistBarViewModel.SyncPresentationState</c>. Showing a bare tail with its head
    /// suppressed by the compact layout would be gibberish.
    /// </para>
    /// </remarks>
    public bool IsSummaryContinuation
    {
        get => _isSummaryContinuation;
        set => SetField(ref _isSummaryContinuation, value);
    }

    /// <summary>
    /// The session's shell-integration state as a one-word chip: "integrated" or "basic".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner-approved addition to this round. "Is the shell integration script actually working?"
    /// was previously answerable only by running a command and seeing whether anything was captured,
    /// which is a poor way to learn that the answer is no. The state was already known - it is
    /// <c>AssistSessionContext.IsShellIntegrationLive</c>, the same disjunction the capture pipeline
    /// gates on - and simply had no way to reach the screen.
    /// </para>
    /// <para>
    /// Deliberately tiny and deliberately not an alert. A markless session is a supported,
    /// working configuration - <c>cmd.exe</c> is never going to emit OSC 133 - so "basic" is a
    /// statement of which mode you are in, not a warning that something is broken. The full sentence
    /// lives in <see cref="IntegrationStatusTooltip"/>.
    /// </para>
    /// </remarks>
    public string IntegrationStatusText
    {
        get => _integrationStatusText;
        set => SetField(ref _integrationStatusText, value);
    }

    /// <summary>The sentence behind <see cref="IntegrationStatusText"/>, shown on hover.</summary>
    public string IntegrationStatusTooltip
    {
        get => _integrationStatusTooltip;
        set => SetField(ref _integrationStatusTooltip, value);
    }

    /// <summary>
    /// Whether the <em>labelled</em> chip is rendered. Follows the same width budget as the hint strip:
    /// it is chrome, and chrome yields to content. When this is false the chip does not disappear - it
    /// degrades to <see cref="IntegrationGlyphText"/>.
    /// </summary>
    public bool ShowIntegrationStatus
    {
        get => _showIntegrationStatus;
        set => SetField(ref _showIntegrationStatus, value);
    }

    /// <summary>
    /// The one-character stand-in for the labelled chip: a filled dot for an integrated session, a
    /// hollow one for a basic session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Dogfood round 4, item 5.</strong> The chip was added last round and the owner never saw
    /// it once, because the two places it lived were both places he was not: the popup footer, which
    /// only exists while a popup is open, and a bubble slot that the progressive hint collapse dropped
    /// at his pane width. A status indicator that is only visible at wide widths reports on sessions
    /// that were never in doubt.
    /// </para>
    /// <para>
    /// So the glyph is exempt from the collapse. It can afford to be: one character costs less width
    /// than the space between two hint clauses, which is the reason the labelled form had to yield in
    /// the first place. It carries the same tooltip as the chip, so the full sentence is still one
    /// hover away, and it is the same fact in the same colour - the degradation is of the label, not of
    /// the signal.
    /// </para>
    /// </remarks>
    public string IntegrationGlyphText
    {
        get => _integrationGlyphText;
        set => SetField(ref _integrationGlyphText, value);
    }

    /// <summary>
    /// Whether the glyph stands in for the chip. Exactly the negation of
    /// <see cref="ShowIntegrationStatus"/>: one of the two forms is always on screen, never both and
    /// never neither.
    /// </summary>
    public bool ShowIntegrationGlyph
    {
        get => _showIntegrationGlyph;
        set => SetField(ref _showIntegrationGlyph, value);
    }

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
