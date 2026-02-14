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

    [Fact]
    public void ExportImportBundle_RoundTrips_AndWritesAudit()
    {
        string sourceName = $"tabs_ws_{Guid.NewGuid():N}";
        string importName = $"tabs_ws_import_{Guid.NewGuid():N}";
        string bundlePath = Path.Combine(Path.GetTempPath(), $"nova_ws_{Guid.NewGuid():N}.novaws.json");
        long auditOffset = GetAuditLength();

        try
        {
            Assert.True(WorkspaceManager.SaveWorkspace(sourceName, BuildSession()));

            bool exported = WorkspaceManager.ExportWorkspaceBundle(sourceName, bundlePath, "test-user");
            Assert.True(exported);
            Assert.True(File.Exists(bundlePath));

            bool imported = WorkspaceManager.ImportWorkspaceBundle(bundlePath, importName, out var error);
            Assert.True(imported);
            Assert.True(string.IsNullOrWhiteSpace(error));

            var restored = WorkspaceManager.LoadWorkspace(importName);
            Assert.NotNull(restored);
            Assert.Single(restored!.Tabs);
            Assert.Equal("Work", restored.Tabs[0].UserTitle);

            string auditDelta = ReadAuditDelta(auditOffset);
            Assert.Contains("|workspace-export|ok|", auditDelta);
            Assert.Contains("|workspace-import|ok|", auditDelta);
            Assert.Contains(SanitizeName(sourceName), auditDelta);
            Assert.Contains(SanitizeName(importName), auditDelta);
        }
        finally
        {
            DeleteWorkspaceFile(sourceName);
            DeleteWorkspaceFile(importName);
            DeleteBundleFile(bundlePath);
        }
    }

    [Fact]
    public void ImportBundle_TamperedPayload_IsRejected()
    {
        string sourceName = $"tabs_ws_{Guid.NewGuid():N}";
        string importName = $"tabs_ws_import_{Guid.NewGuid():N}";
        string bundlePath = Path.Combine(Path.GetTempPath(), $"nova_ws_{Guid.NewGuid():N}.novaws.json");

        try
        {
            Assert.True(WorkspaceManager.SaveWorkspace(sourceName, BuildSession()));
            Assert.True(WorkspaceManager.ExportWorkspaceBundle(sourceName, bundlePath, "test-user"));

            string json = File.ReadAllText(bundlePath);
            var bundle = System.Text.Json.JsonSerializer.Deserialize(
                json,
                SessionSerializationContext.Default.WorkspaceBundlePackage);
            Assert.NotNull(bundle);

            // Tamper with payload without updating hash.
            bundle!.PayloadJson += " ";
            string tampered = System.Text.Json.JsonSerializer.Serialize(
                bundle,
                SessionSerializationContext.Default.WorkspaceBundlePackage);
            File.WriteAllText(bundlePath, tampered);

            bool imported = WorkspaceManager.ImportWorkspaceBundle(bundlePath, importName, out var error);
            Assert.False(imported);
            Assert.Contains("hash", error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Null(WorkspaceManager.LoadWorkspace(importName));
        }
        finally
        {
            DeleteWorkspaceFile(sourceName);
            DeleteWorkspaceFile(importName);
            DeleteBundleFile(bundlePath);
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

    private static void DeleteBundleFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static long GetAuditLength()
    {
        return File.Exists(AppPaths.WorkspaceAuditLogPath)
            ? new FileInfo(AppPaths.WorkspaceAuditLogPath).Length
            : 0;
    }

    private static string ReadAuditDelta(long offset)
    {
        if (!File.Exists(AppPaths.WorkspaceAuditLogPath))
        {
            return string.Empty;
        }

        using var fs = new FileStream(AppPaths.WorkspaceAuditLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > fs.Length) return string.Empty;

        fs.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }
}
