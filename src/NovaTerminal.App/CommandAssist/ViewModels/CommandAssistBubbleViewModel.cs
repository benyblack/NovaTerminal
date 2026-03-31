using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NovaTerminal.CommandAssist.ViewModels;

public sealed class CommandAssistBubbleViewModel : INotifyPropertyChanged
{
    private bool _isVisible;
    private string _modeLabel = "Suggest";
    private string _queryText = string.Empty;
    private string _summaryText = string.Empty;
    private string _shortcutHintText = "Ctrl+Enter insert  |  Ctrl+Shift+H help  |  Ctrl+R history";
    private bool _showQueryText = true;

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
