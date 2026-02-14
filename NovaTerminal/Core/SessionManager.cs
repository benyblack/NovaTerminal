using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia;
using NovaTerminal.Controls;

namespace NovaTerminal.Core
{
    public static class SessionManager
    {
        private static string SessionPath => AppPaths.SessionFilePath;

        public static void SaveSession(Window window, TabControl tabs)
        {
            try
            {
                var session = new NovaSession
                {
                    ActiveTabIndex = tabs.SelectedIndex
                };

                foreach (var item in tabs.Items)
                {
                    if (item is TabItem tabItem)
                    {
                        Control? rootControl = tabItem.Content as Control;
                        if (window is MainWindow mw)
                        {
                            rootControl = mw.GetLayoutRootForTab(tabItem);
                        }

                        var tabSession = new TabSession
                        {
                            TabId = (window as MainWindow)?.GetPersistentTabId(tabItem).ToString(),
                            Title = (tabItem.Header as TextBlock)?.Text ?? "Terminal",
                            Root = BuildPaneTree(rootControl)
                        };

                        if (window is MainWindow mainWindow)
                        {
                            var activePaneId = mainWindow.GetActivePaneIdForTab(tabItem);
                            var zoomedPaneId = mainWindow.GetZoomedPaneIdForTab(tabItem);
                            tabSession.ActivePaneId = activePaneId?.ToString();
                            tabSession.ZoomedPaneId = zoomedPaneId?.ToString();
                            tabSession.BroadcastInputEnabled = mainWindow.IsBroadcastEnabledForTab(tabItem);
                        }

                        session.Tabs.Add(tabSession);
                    }
                }

                var json = JsonSerializer.Serialize(session, SessionSerializationContext.Default.NovaSession);
                Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
                File.WriteAllText(SessionPath, json);
            }
            catch (Exception ex)
            {
                // Silent failure or log to debug console
                System.Diagnostics.Debug.WriteLine($"[SessionManager] Failed to save session: {ex}");
            }
        }

        private static PaneNode? BuildPaneTree(Control? control)
        {
            if (control == null) return null;

            // Base case: Leaf Node (TerminalPane)
            if (control is TerminalPane pane)
            {
                return new PaneNode
                {
                    Type = NodeType.Leaf,
                    ProfileId = pane.Profile?.Id.ToString(),
                    PaneId = pane.PaneId.ToString(),
                    Command = pane.ShellCommand,
                    Arguments = pane.ShellArgs
                };
            }

            // Recursive case: Grid (Split)
            if (control is Grid /*grid*/) // Wait, Grid is too generic. We need to be sure it's OUR split grid.
            {
                // In MainWindow.SplitPane, we create a Grid with 3 columns/rows (Pane, Splitter, Pane)
                // We can detect this structure.
                var grid = (Grid)control;

                // If it has children that are TerminalPane or Grid, treating it as a split node.
                // NOTE: Our splitter adds a GridSplitter between children.

                // Determine orientation
                bool isHorizontal = grid.ColumnDefinitions.Count > 1; // Horizontal Split (Cols)

                var node = new PaneNode
                {
                    Type = NodeType.Split,
                    SplitOrientation = isHorizontal ? 0 : 1
                };

                // Collect children, SKIPPING GridSplitters
                foreach (var child in grid.Children)
                {
                    if (child is GridSplitter) continue;

                    var childNode = BuildPaneTree(child as Control);
                    if (childNode != null)
                    {
                        node.Children.Add(childNode);
                    }
                }

                // Collect Layout Sizes (GridLength)
                // We need to store these to restore the ratio (e.g. 2* vs 1*)
                if (isHorizontal)
                {
                    foreach (var cd in grid.ColumnDefinitions)
                    {
                        // Skip the fixed splitter column (usually 3px)
                        if (cd.Width.IsAbsolute && cd.Width.Value <= 5) continue;

                        node.Sizes.Add(cd.Width.ToString());
                    }
                }
                else
                {
                    foreach (var rd in grid.RowDefinitions)
                    {
                        // Skip splitter
                        if (rd.Height.IsAbsolute && rd.Height.Value <= 5) continue;

                        node.Sizes.Add(rd.Height.ToString());
                    }
                }

                return node;
            }


            return null; // Unknown control type
        }

        public static void RestoreSession(Window window, TabControl tabs, TerminalSettings settings)
        {
            try
            {
                if (!File.Exists(SessionPath)) return;

                var json = File.ReadAllText(SessionPath);
                var session = JsonSerializer.Deserialize(json, SessionSerializationContext.Default.NovaSession);

                if (session == null || session.Tabs.Count == 0) return;

                tabs.Items.Clear();

                foreach (var tabSession in session.Tabs)
                {
                    var content = RestorePaneTree(tabSession.Root, settings);
                    if (content != null)
                    {
                        var tabItem = new TabItem
                        {
                            Header = new TextBlock { Text = tabSession.Title, Foreground = Avalonia.Media.Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 4) },
                            Content = content,
                            Tag = tabSession
                        };
                        tabs.Items.Add(tabItem);
                    }
                }

                if (session.ActiveTabIndex >= 0 && session.ActiveTabIndex < tabs.Items.Count)
                {
                    tabs.SelectedIndex = session.ActiveTabIndex;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to restore session: {ex}");
            }
        }

        private static Control? RestorePaneTree(PaneNode? node, TerminalSettings settings)
        {
            if (node == null) return null;

            if (node.Type == NodeType.Leaf)
            {
                // Reconstruct TerminalPane
                TerminalProfile? profile = null;
                if (!string.IsNullOrEmpty(node.ProfileId) && Guid.TryParse(node.ProfileId, out var profileGuid))
                {
                    profile = settings.Profiles?.Find(p => p.Id == profileGuid);
                }

                TerminalPane pane;
                if (profile != null)
                {
                    pane = new TerminalPane(profile);
                }
                else
                {
                    // Fallback to command args if profile missing
                    pane = new TerminalPane(node.Command ?? "cmd.exe", node.Arguments ?? "");
                }

                if (!string.IsNullOrWhiteSpace(node.PaneId) && Guid.TryParse(node.PaneId, out var paneId))
                {
                    pane.PaneId = paneId;
                }

                pane.ApplySettings(settings);
                return pane;
            }
            else if (node.Type == NodeType.Split)
            {
                // Reconstruct Grid Split
                var grid = new Grid
                {
                    Background = Avalonia.Media.Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };

                var dividerBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(35, 35, 35));

                bool isHorizontal = (node.SplitOrientation == 0); // 0=Horizontal (Cols)

                for (int i = 0; i < node.Children.Count; i++)
                {
                    // Add Splitter if not first item
                    if (i > 0)
                    {
                        var splitter = new GridSplitter
                        {
                            Background = dividerBrush,
                            Focusable = false
                        };

                        if (isHorizontal)
                        {
                            grid.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Pixel));
                            splitter.Width = 3;
                            splitter.ResizeDirection = GridResizeDirection.Columns;
                            splitter.VerticalAlignment = VerticalAlignment.Stretch;
                            Grid.SetColumn(splitter, grid.ColumnDefinitions.Count - 1);
                            grid.Children.Add(splitter);
                        }
                        else
                        {
                            grid.RowDefinitions.Add(new RowDefinition(3, GridUnitType.Pixel));
                            splitter.Height = 3;
                            splitter.ResizeDirection = GridResizeDirection.Rows;
                            splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
                            Grid.SetRow(splitter, grid.RowDefinitions.Count - 1);
                            grid.Children.Add(splitter);
                        }
                    }

                    // Add Child Pane
                    var childControl = RestorePaneTree(node.Children[i], settings);
                    if (childControl != null)
                    {
                        // Parse metrics
                        GridLength length = new GridLength(1, GridUnitType.Star);
                        if (i < node.Sizes.Count)
                        {
                            try { length = GridLength.Parse(node.Sizes[i]); } catch { }
                        }

                        if (isHorizontal)
                        {
                            grid.ColumnDefinitions.Add(new ColumnDefinition(length));
                            Grid.SetColumn(childControl, grid.ColumnDefinitions.Count - 1);
                            Grid.SetRow(childControl, 0); // Should be explicitly set?
                        }
                        else
                        {
                            grid.RowDefinitions.Add(new RowDefinition(length));
                            Grid.SetRow(childControl, grid.RowDefinitions.Count - 1);
                            Grid.SetColumn(childControl, 0);
                        }

                        grid.Children.Add(childControl);
                    }
                }

                return grid;
            }

            return null;
        }
    }
}
