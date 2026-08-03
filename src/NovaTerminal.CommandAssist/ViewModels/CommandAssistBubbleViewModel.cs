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
