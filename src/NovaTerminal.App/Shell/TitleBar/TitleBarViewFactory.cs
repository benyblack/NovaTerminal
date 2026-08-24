using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NovaTerminal.Shell.TitleBar;

/// <summary>
/// Builds the title bar's buttons from a resolved layout. Separate from MainWindow on purpose:
/// MainWindow cannot be instantiated in a headless test — it spawns PTYs, SSH, and the agent host —
/// so putting the control construction here is what makes the rendering testable.
/// </summary>
public static class TitleBarViewFactory
{
    public const string OverflowButtonName = "BtnTitleBarOverflow";

    public static string ButtonName(string id) => $"BtnTitleBar_{id}";

    public static void Populate(
        Panel host,
        TitleBarLayout layout,
        IReadOnlyDictionary<string, string>? keybindings,
        IReadOnlyDictionary<string, Action> handlers,
        Control? newTabButton,
        Action<string> logMissingHandler)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(logMissingHandler);

        host.Children.Clear();

        foreach (var entry in layout.Pinned)
        {
            // The + button is declared in XAML and carries a MenuFlyout with real content
            // ("New SSH Connection…", "Manage Profiles…", "Agent Activity…"). Reinsert it rather
            // than rebuild it, or that flyout is lost on every rebuild.
            if (entry.Id == TitleBarCatalog.NewTabId && newTabButton is not null)
            {
                host.Children.Add(newTabButton);
                continue;
            }

            if (!handlers.TryGetValue(entry.Id, out var handler))
            {
                logMissingHandler(entry.Id);
                continue;
            }

            host.Children.Add(CreateItemButton(entry, keybindings, handler));
        }

        if (!layout.ShowOverflowButton)
        {
            return;
        }

        host.Children.Add(CreateOverflowButton(layout, keybindings, handlers, logMissingHandler));
    }

    private static Button CreateItemButton(
        TitleBarCatalogEntry entry,
        IReadOnlyDictionary<string, string>? keybindings,
        Action handler)
    {
        string shortcut = TitleBarShortcuts.Resolve(entry.ShortcutKey, keybindings);

        var button = new Button
        {
            Name = ButtonName(entry.Id),
            // Matches the styling the four hardcoded buttons carried inline before this feature.
            // Focusable=false is load-bearing: a focusable title bar button steals keyboard focus
            // from the terminal on click.
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 32,
            Height = 32,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(4, 0, 0, 0),
            Command = new RelayCommand(handler),
            Content = new PathIcon
            {
                Data = Geometry.Parse(entry.IconGeometry),
                Width = entry.IconSize,
                Height = entry.IconSize,
            },
        };

        ToolTip.SetTip(button, TitleBarShortcuts.FormatTooltip(entry.Title, shortcut));
        return button;
    }

    private static Button CreateOverflowButton(
        TitleBarLayout layout,
        IReadOnlyDictionary<string, string>? keybindings,
        IReadOnlyDictionary<string, Action> handlers,
        Action<string> logMissingHandler)
    {
        var flyout = new MenuFlyout();

        foreach (var entry in layout.Overflow)
        {
            if (!handlers.TryGetValue(entry.Id, out var handler))
            {
                logMissingHandler(entry.Id);
                continue;
            }

            string shortcut = TitleBarShortcuts.Resolve(entry.ShortcutKey, keybindings);

            // The shortcut goes in the header text, not into InputGesture: these bindings are
            // dispatched from MainWindow's own key handler rather than Avalonia's gesture system,
            // so an InputGesture here would register a second, competing route to the same action.
            flyout.Items.Add(new MenuItem
            {
                Header = TitleBarShortcuts.FormatTooltip(entry.Title, shortcut),
                Command = new RelayCommand(handler),
                Icon = new PathIcon
                {
                    Data = Geometry.Parse(entry.IconGeometry),
                    Width = entry.IconSize,
                    Height = entry.IconSize,
                },
            });
        }

        var button = new Button
        {
            Name = OverflowButtonName,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 32,
            Height = 32,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(4, 0, 0, 0),
            Flyout = flyout,
            Content = new PathIcon
            {
                Data = Geometry.Parse(TitleBarCatalog.OverflowGeometry),
                Width = 16,
                Height = 16,
            },
        };

        ToolTip.SetTip(button, "More actions");
        return button;
    }

    /// <summary>Minimal ICommand so a plain Action can drive a Button without a Click subscription.</summary>
    private sealed class RelayCommand(Action execute) : ICommand
    {
        // These commands are always executable, so the event never fires. Empty accessors satisfy
        // ICommand without leaving an unraised field behind.
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
