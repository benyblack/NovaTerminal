using System;
using System.ComponentModel;

namespace NovaTerminal.AgentOutput;

/// <summary>
/// State of the per-pane Agent Output panel.
/// </summary>
/// <remarks>
/// Plain <see cref="INotifyPropertyChanged"/>, matching the Command Assist view models - no
/// binding framework, no toolkit. <see cref="IsPanelOpen"/> is the user's intent (the toggle);
/// <see cref="IsAltScreenSuppressed"/> is the pane telling the panel a full-screen program owns
/// the grid. <see cref="IsShown"/> is the effective visibility both feed, so the pane never has
/// to combine the two conditions itself.
/// </remarks>
public sealed class AgentOutputViewModel : INotifyPropertyChanged
{
    private bool _isPanelOpen;
    private bool _isAltScreenSuppressed;
    private bool _isStreaming;
    private string _markdownText = string.Empty;
    private string _statusText = string.Empty;

    /// <summary>Raised when any bound property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The user toggled the panel open. Survives an alt-screen suppression.</summary>
    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set
        {
            if (_isPanelOpen == value)
            {
                return;
            }

            bool wasShown = IsShown;
            _isPanelOpen = value;
            OnPropertyChanged(nameof(IsPanelOpen));
            if (IsShown != wasShown)
            {
                OnPropertyChanged(nameof(IsShown));
            }
        }
    }

    /// <summary>True while a full-screen program owns the grid; the panel stays down.</summary>
    public bool IsAltScreenSuppressed
    {
        get => _isAltScreenSuppressed;
        set
        {
            if (_isAltScreenSuppressed == value)
            {
                return;
            }

            // IsShown only changes when the panel is open; raising it while closed would make
            // the pane flip the presenter for no reason.
            bool wasShown = IsShown;
            _isAltScreenSuppressed = value;
            OnPropertyChanged(nameof(IsAltScreenSuppressed));
            if (IsShown != wasShown)
            {
                OnPropertyChanged(nameof(IsShown));
            }
        }
    }

    /// <summary>Effective visibility: the panel is up exactly when open and not suppressed.</summary>
    public bool IsShown => _isPanelOpen && !_isAltScreenSuppressed;

    /// <summary>
    /// True from <c>OSC 133;C</c> until <c>OSC 133;D</c> - the command is producing output, so
    /// the panel's next line may not be its last. Markless sessions stream until the next Enter.
    /// </summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            if (_isStreaming == value)
            {
                return;
            }

            _isStreaming = value;
            OnPropertyChanged(nameof(IsStreaming));
        }
    }

    /// <summary>The raw markdown text of the tracked output region.</summary>
    public string MarkdownText
    {
        get => _markdownText;
        private set
        {
            if (_markdownText == value)
            {
                return;
            }

            _markdownText = value;
            OnPropertyChanged(nameof(MarkdownText));
            OnPropertyChanged(nameof(HasContent));
        }
    }

    /// <summary>False until the tracked region has produced any text.</summary>
    public bool HasContent => _markdownText.Length > 0;

    /// <summary>Short line under the title: streaming state, or empty when idle.</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>
    /// Applies one region read. Called on the UI thread by the pane, out of the tracker.
    /// </summary>
    public void SetUpdate(string markdownText, bool isStreaming)
    {
        IsStreaming = isStreaming;
        MarkdownText = markdownText;
        StatusText = isStreaming ? "streaming…" : string.Empty;
    }

    /// <summary>
    /// Clears the streaming status while keeping the text. Called when the panel reopens: a
    /// command that finished while the panel was closed never delivered its final update (D
    /// skips the read for a hidden panel), so without this the reopened panel would show the
    /// previous output labeled "streaming…" indefinitely. If a live region does exist, the
    /// tracker's flush immediately re-marks it as streaming.
    /// </summary>
    public void ClearStreaming()
    {
        IsStreaming = false;
        StatusText = string.Empty;
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
