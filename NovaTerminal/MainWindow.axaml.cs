using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NovaTerminal.Core;
using System;
using System.Collections.Generic;

namespace NovaTerminal
{
    public partial class MainWindow : Window
    {
        // Simple storage for active sessions
        // In a real VM pattern, we'd bind TabControl.Items to ObservableCollection.
        // For simplicity, we manipulate TabControl.Items directly.
        
        private class TabContext
        {
            public ConPtySession Session { get; set; }
            public TerminalBuffer Buffer { get; set; }
            public AnsiParser Parser { get; set; }
            public TerminalView View { get; set; }
            
            public TabContext(string shell) 
            {
                 // Match the environment variable defaults (120x30)
                 Buffer = new TerminalBuffer(120, 30);
                 Parser = new AnsiParser(Buffer);
                 View = new TerminalView();
                 View.SetBuffer(Buffer);
                 Session = new ConPtySession(shell);
            }
        }

        private TabContext? _currentContext;

        public MainWindow()
        {
            InitializeComponent();

            var tabs = this.FindControl<TabControl>("Tabs");
            var btnNew = this.FindControl<Button>("BtnNewTab");

            // Menu Items
            var menuCmd = this.FindControl<MenuItem>("MenuCmd");
            var menuPs = this.FindControl<MenuItem>("MenuPs");
            var menuWsl = this.FindControl<MenuItem>("MenuWsl");
            
            // Removed default click handler to prevent double actions. 
            // The Button acts purely as a flyout trigger.

            if (menuCmd != null) menuCmd.Click += (s, e) => AddTab("cmd.exe");
            if (menuPs != null) menuPs.Click += (s, e) => AddTab("powershell.exe");
            if (menuWsl != null) menuWsl.Click += (s, e) => AddTab("wsl.exe");
            
            if (tabs != null)
            {
                tabs.SelectionChanged += (s, e) => 
                {
                   if (tabs.SelectedItem is TabItem ti && ti.Tag is TabContext ctx)
                   {
                       _currentContext = ctx;
                       // Focus view?
                       ctx.View.Focus();
                   }
                };
            }
            
            // Add initial tab
            AddTab();

            // Handle Global Input (sent to current tab)
            // Ideally individual Views handle input, but Window-level hook catches it all nicely for now.
            this.KeyDown += OnKeyDown;
            this.TextInput += OnTextInput;
        }

        private void AddTab(string shell = "cmd.exe")
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            var ctx = new TabContext(shell);
            
            // Wire up output
            ctx.Session.OnOutputReceived += text => 
            {
                // Dispatch to UI for this specific tab
                Dispatcher.UIThread.InvokeAsync(() => {
                    ctx.Parser.Process(text);
                });
            };

            var tabItem = new TabItem
            {
                Header = shell,
                Content = ctx.View,
                Tag = ctx
            };

            tabs.Items.Add(tabItem);
            tabs.SelectedItem = tabItem;
            _currentContext = ctx;
            
            // Explicitly force focus to the view (will attach visual tree)
            ctx.View.Focus();

            // Defer Start until View measures itself and fires OnReady
            // This ensures we pass the CORRECT columns/rows to the shell environment.
            ctx.View.OnReady += () => 
            {
                 // Start Session with actual measured size
                 _ = ctx.Session.StartAsync(ctx.Buffer.Cols, ctx.Buffer.Rows);
            };
            
            // Wire up resize events to ConPTY
            ctx.View.OnResize += (cols, rows) => 
            {
                ctx.Session.Resize(cols, rows);
            };
        }

        private void OnTextInput(object? sender, TextInputEventArgs e)
        {
            if (_currentContext == null) return;
            
            if (!string.IsNullOrEmpty(e.Text))
            {
               // Local echo removed; rely on shell echo
               // _currentContext.Parser.Process(e.Text); 
               _currentContext.Session.SendInput(e.Text);
               e.Handled = true;
            }
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (_currentContext == null) return;

            if (e.Key == Key.Enter)
            {
                // _currentContext.Parser.Process("\r\n");
                _currentContext.Session.SendInput("\r");
                e.Handled = true;
            }
            else if (e.Key == Key.Back)
            {
                // _currentContext.Parser.Process("\b \b");
                _currentContext.Session.SendInput("\b");
                e.Handled = true;
            }
            else if (e.Key == Key.Tab)
            {
                // _currentContext.Parser.Process("\t");
                _currentContext.Session.SendInput("\t");
                e.Handled = true;
            }
        }
    }
}