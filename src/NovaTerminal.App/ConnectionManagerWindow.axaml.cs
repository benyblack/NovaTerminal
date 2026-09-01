using Avalonia.Controls;
using NovaTerminal.Controls;
using NovaTerminal.Shell;
using NovaTerminal.VT;
using System.Collections.Generic;

namespace NovaTerminal
{
    /// <summary>
    /// Real top-level window hosting the Connection Manager surface, opened the way
    /// <see cref="SettingsWindow"/> is. Replaces the old ConnectionOverlay - a Border with a
    /// hand-drawn title bar inside MainWindow - which could not be moved, resized, or focused
    /// like a window.
    /// </summary>
    public partial class ConnectionManagerWindow : Window
    {
        public ConnectionManagerWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// The manager surface this window hosts. Null only before InitializeComponent; kept
        /// null-tolerant so MainWindow's refresh paths stay safe if the XAML name changes.
        /// </summary>
        internal ConnectionManager? Manager => this.FindControl<ConnectionManager>("ManagerControl");

        /// <summary>Pass-through so refresh paths can update the window while it is open.</summary>
        public void ApplyTheme(TerminalTheme theme) => Manager?.ApplyTheme(theme);

        /// <summary>Pass-through so refresh paths can update the window while it is open.</summary>
        public void LoadProfiles(IEnumerable<TerminalProfile> profiles) => Manager?.LoadProfiles(profiles);
    }
}
