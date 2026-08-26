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

    /// <summary>
    /// The XAML-declared name of the "+" New Tab button. It lives here, not in MainWindow, because
    /// this factory already owns the title bar's button-naming contract (<see cref="ButtonName"/>,
    /// <see cref="OverflowButtonName"/>) and MainWindow.axaml.cs looks the button up by this same
    /// name in several places. Must keep matching the `Name="BtnNewTab"` in MainWindow.axaml.
    /// </summary>
    public const string NewTabButtonName = "BtnNewTab";

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
                // The reused button keeps its XAML tooltip ("New Tab") unless it's reset here, so
                // set the same title-plus-shortcut tooltip every generated button gets below. This
                // is the only change made to it -- it is inserted as-is, not rebuilt, so its
                // MenuFlyout survives.
                string newTabShortcut = TitleBarShortcuts.Resolve(entry.ShortcutKey, keybindings);
                ToolTip.SetTip(newTabButton, TitleBarShortcuts.FormatTooltip(entry.Title, newTabShortcut));

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

        var overflowButton = CreateOverflowButton(layout, keybindings, handlers, logMissingHandler);
        if (overflowButton is not null)
        {
            host.Children.Add(overflowButton);
        }
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

    private static Button? CreateOverflowButton(
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

        // layout.ShowOverflowButton only knows the layout (whether anything is *assigned* to
        // Overflow), not the handler wiring. If every overflow entry's handler is missing — a
        // wiring bug — the loop above leaves the flyout empty. A button that opens an empty menu
        // is worse than no button, so treat an empty item list as a second, defensive gate.
        if (flyout.Items.Count == 0)
        {
            return null;
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
        private EventHandler? _canExecuteChanged;

        /// <summary>
        /// Required by ICommand. These commands are always executable, so nothing ever raises this —
        /// the field-backed form exists so the accessors are not empty and the compiler still sees the
        /// field used.
        /// </summary>
        public event EventHandler? CanExecuteChanged
        {
            add => _canExecuteChanged += value;
            remove => _canExecuteChanged -= value;
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
