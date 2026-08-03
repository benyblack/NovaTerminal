using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using NovaTerminal.CommandAssist.ViewModels;

namespace NovaTerminal.CommandAssist.Views;

/// <summary>
/// The suggestion popup. Renders rows, and turns pointer gestures on them into the two requests the
/// pane knows how to answer: select this row, accept this row.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why events rather than commands.</strong> Accepting a row sends text to a PTY and needs the
/// grid read, the echo flag and the markless accumulator that only <c>TerminalPane</c> has - the same
/// path <c>Ctrl+Enter</c> takes. A command on the view-model would either duplicate that gate or
/// bypass it, and this assembly cannot see the session anyway. So the view reports the gesture and the
/// pane decides, which keeps exactly one insertion gate in the product.
/// </para>
/// <para>
/// <strong>Why not a <c>ListBox</c>.</strong> A ListBox would supply hover, selection and
/// scroll-into-view for free, and would also take keyboard focus off <c>TerminalView</c> on the first
/// click - after which the user's next keystroke goes to the list instead of the shell. The popup
/// overlays a live terminal that must keep focus at all times, so the rows are deliberately
/// non-focusable <c>Border</c>s and the three behaviors are wired by hand.
/// </para>
/// </remarks>
public partial class CommandAssistPopupView : UserControl
{
    private CommandAssistPopupViewModel? _observedViewModel;

    public CommandAssistPopupView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>A row was clicked once. The argument is its index in <c>Suggestions</c>.</summary>
    public event Action<int>? SuggestionPointerSelected;

    /// <summary>
    /// A row was double-clicked, or clicked while already selected. The argument is its index.
    /// </summary>
    public event Action<int>? SuggestionPointerAccepted;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_observedViewModel != null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _observedViewModel = DataContext as CommandAssistPopupViewModel;

        if (_observedViewModel != null)
        {
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandAssistPopupViewModel.SelectedIndex))
        {
            BringSelectedRowIntoView();
        }
    }

    /// <summary>
    /// Scrolls the selected row into view after a keyboard move.
    /// </summary>
    /// <remarks>
    /// Needed because the row list is an <c>ItemsControl</c> rather than a selecting control: nothing
    /// else knows that one of its children is special. Both list instances are asked, because the
    /// compact and expanded layouts are separate subtrees and only one of them is realized at a time -
    /// the unrealized one has no container for the index and answers null, which is the no-op.
    /// </remarks>
    private void BringSelectedRowIntoView()
    {
        int index = _observedViewModel?.SelectedIndex ?? -1;
        if (index < 0)
        {
            return;
        }

        BringIntoView(PopupSuggestionsList, index);
        BringIntoView(PopupCompactSuggestionsList, index);
    }

    private static void BringIntoView(ItemsControl? list, int index)
    {
        if (list == null || index >= list.ItemCount)
        {
            return;
        }

        list.ContainerFromIndex(index)?.BringIntoView();
    }

    /// <summary>
    /// Whether a pointer button pressed on the popup must be swallowed rather than allowed to reach
    /// whatever is behind it.
    /// </summary>
    /// <remarks>
    /// A predicate rather than an inline condition so the rule is assertable without synthesizing
    /// pointer input: the two buttons named here are the two that do something destructive to the
    /// terminal underneath (pane context menu, X11-style middle-click paste), and neither does anything
    /// at all to the popup.
    /// </remarks>
    internal static bool IsSwallowedPointerButton(bool isRightButtonPressed, bool isMiddleButtonPressed) =>
        isRightButtonPressed || isMiddleButtonPressed;

    /// <summary>
    /// Swallows right- and middle-clicks landing anywhere on the popup surface.
    /// </summary>
    /// <remarks>
    /// Left-clicks are deliberately left alone here: the row handler below is the one that decides what
    /// a left-click means, and marking the press handled at this level first would not stop it (the row
    /// handler runs first, on the inner control) but would obscure where the decision lives.
    /// </remarks>
    private void OnPopupSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        if (IsSwallowedPointerButton(properties.IsRightButtonPressed, properties.IsMiddleButtonPressed))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Stops a context-menu request raised over the popup from reaching the pane.
    /// </summary>
    /// <remarks>
    /// Separate from the pointer handler because it is a separate event: Avalonia raises
    /// <c>ContextRequested</c> from the pointer <em>release</em> (and from the menu key), so handling the
    /// press alone leaves <c>TerminalPane.RootGrid</c>'s <c>ContextMenu</c> free to open over the list.
    /// </remarks>
    private void OnPopupSurfaceContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
    }

    /// <summary>
    /// Single click selects; double click - or a click on the row that is already selected - accepts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The "already selected" case is what makes the mouse usable without a double click: browse to a
    /// row with the arrow keys or a first click, then click it to run the insertion. Both gestures are
    /// deliberate second acts, which is the bar an action that edits the user's command line has to
    /// clear.
    /// </para>
    /// <para>
    /// Left button only, and always marked handled. The popup floats over a terminal that treats a
    /// pointer press as the start of a text selection, so an unhandled press would begin selecting the
    /// grid underneath the list.
    /// </para>
    /// </remarks>
    private void OnSuggestionRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is not Control { DataContext: CommandAssistSuggestionItemViewModel row })
        {
            return;
        }

        int index = _observedViewModel?.Suggestions.IndexOf(row) ?? -1;
        if (index < 0)
        {
            return;
        }

        e.Handled = true;

        if (e.ClickCount >= 2 || row.IsSelected)
        {
            SuggestionPointerAccepted?.Invoke(index);
            return;
        }

        SuggestionPointerSelected?.Invoke(index);
    }
}
