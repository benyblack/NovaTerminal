using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

public sealed class WorkspaceManagerTests
{
    [Fact]
    public void SaveAndLoadWorkspace_RoundTripsSessionPayload()
    {
        string workspaceName = $"tabs_ws_{Guid.NewGuid():N}";
        try
        {
            var source = BuildSession();

            bool saved = WorkspaceManager.SaveWorkspace(workspaceName, source);
            Assert.True(saved);

            var restored = WorkspaceManager.LoadWorkspace(workspaceName);
            Assert.NotNull(restored);
            Assert.Equal(source.ActiveTabIndex, restored!.ActiveTabIndex);
            Assert.Single(restored.Tabs);

            var tab = restored.Tabs[0];
            Assert.Equal("Terminal 1", tab.Title);
            Assert.Equal("Work", tab.UserTitle);
            Assert.True(tab.IsPinned);
            Assert.True(tab.IsProtected);
            Assert.Equal("pane-a", tab.ActivePaneId);
            Assert.Equal("pane-a", tab.ZoomedPaneId);
            Assert.True(tab.BroadcastInputEnabled);
            Assert.NotNull(tab.Root);
            Assert.Equal(NodeType.Leaf, tab.Root!.Type);
            Assert.Equal("bash", tab.Root.Command);
            Assert.Equal("-l", tab.Root.Arguments);
        }
        finally
        {
            DeleteWorkspaceFile(workspaceName);
        }
    }

    [Fact]
    public void ListWorkspaceNames_ReturnsSavedWorkspace()
    {
        string workspaceName = $"tabs_ws_{Guid.NewGuid():N}";
        try
        {
            bool saved = WorkspaceManager.SaveWorkspace(workspaceName, BuildSession());
            Assert.True(saved);

            var names = WorkspaceManager.ListWorkspaceNames();
            Assert.Contains(workspaceName, names);
        }
        finally
        {
            DeleteWorkspaceFile(workspaceName);
        }
    }

    [Fact]
    public void SaveWorkspace_SanitizesInvalidFileNameChars()
    {
        string invalidName = $"tabs:ws*{Guid.NewGuid():N}";
        string sanitizedName = SanitizeName(invalidName);
        try
        {
            bool saved = WorkspaceManager.SaveWorkspace(invalidName, BuildSession());
            Assert.True(saved);

            var restored = WorkspaceManager.LoadWorkspace(invalidName);
            Assert.NotNull(restored);

            var names = WorkspaceManager.ListWorkspaceNames();
            Assert.Contains(sanitizedName, names);
        }
        finally
        {
            DeleteWorkspaceFile(invalidName);
            DeleteWorkspaceFile(sanitizedName);
        }
    }

    private static NovaSession BuildSession()
    {
        return new NovaSession
        {
            ActiveTabIndex = 0,
            Tabs =
            {
                new TabSession
                {
                    TabId = Guid.NewGuid().ToString("D"),
                    Title = "Terminal 1",
                    UserTitle = "Work",
                    IsPinned = true,
                    IsProtected = true,
                    ActivePaneId = "pane-a",
                    ZoomedPaneId = "pane-a",
                    BroadcastInputEnabled = true,
                    Root = new PaneNode
                    {
                        Type = NodeType.Leaf,
                        PaneId = "pane-a",
                        Command = "bash",
                        Arguments = "-l"
                    }
                }
            }
        };
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static void DeleteWorkspaceFile(string name)
    {
        string path = Path.Combine(AppPaths.WorkspacesDirectory, $"{SanitizeName(name)}.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
