using NovaTerminal.Tests.Infra;
using NovaTerminal.Core;

namespace NovaTerminal.Tests.Core;

[Collection("WorkspacePolicy")]
public sealed class WorkspaceManagerTests
{
    [Fact]
    public void SaveAndLoadWorkspace_RoundTripsSessionPayload()
    {
        using var policyScope = PolicyFileScope.WithDefault();
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
            Assert.Equal("pane-0", tab.ActivePaneId);
            Assert.Equal("pane-0", tab.ZoomedPaneId);
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
        using var policyScope = PolicyFileScope.WithDefault();
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
        using var policyScope = PolicyFileScope.WithDefault();
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
    public void SaveAndLoadWorkspaceTemplate_RoundTripsSessionPayload()
    {
        using var policyScope = PolicyFileScope.WithDefault();
        string templateName = $"tabs_tpl_{Guid.NewGuid():N}";

        try
        {
            var source = BuildSession(tabCount: 2);
            bool saved = WorkspaceManager.SaveWorkspaceTemplate(templateName, source);
            Assert.True(saved);

            var restored = WorkspaceManager.LoadWorkspaceTemplate(templateName);
            Assert.NotNull(restored);
            Assert.Equal(source.Tabs.Count, restored!.Tabs.Count);
            Assert.Equal("Work", restored.Tabs[0].UserTitle);
            Assert.Equal("Work 2", restored.Tabs[1].UserTitle);
        }
        finally
        {
            DeleteWorkspaceTemplateFile(templateName);
        }
    }

    [Fact]
    public void ListWorkspaceTemplateNames_ReturnsSavedTemplate()
    {
        using var policyScope = PolicyFileScope.WithDefault();
        string templateName = $"tabs_tpl_{Guid.NewGuid():N}";

        try
        {
            bool saved = WorkspaceManager.SaveWorkspaceTemplate(templateName, BuildSession());
            Assert.True(saved);

            var names = WorkspaceManager.ListWorkspaceTemplateNames();
            Assert.Contains(templateName, names);
        }
        finally
        {
            DeleteWorkspaceTemplateFile(templateName);
        }
    }

    [Fact]
    public void ExportImportBundle_RoundTrips_AndWritesAudit()
    {
        using var policyScope = PolicyFileScope.WithDefault();
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
        using var policyScope = PolicyFileScope.WithDefault();
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

    [Fact]
    public void LoadWorkspaceBundleSession_RoundTripsWithoutPersistingWorkspace()
    {
        using var policyScope = PolicyFileScope.WithDefault();
        string bundlePath = Path.Combine(Path.GetTempPath(), $"nova_ws_{Guid.NewGuid():N}.novaws.json");
        string bundleName = $"handoff_{Guid.NewGuid():N}";

        try
        {
            bool exported = WorkspaceManager.ExportWorkspaceBundle(bundleName, BuildSession(tabCount: 2), bundlePath, "test-user");
            Assert.True(exported);

            bool loaded = WorkspaceManager.LoadWorkspaceBundleSession(bundlePath, out var workspaceName, out var session, out var error);
            Assert.True(loaded);
            Assert.True(string.IsNullOrWhiteSpace(error));
            Assert.Equal(SanitizeName(bundleName), workspaceName);
            Assert.NotNull(session);
            Assert.Equal(2, session!.Tabs.Count);

            // Open/apply flow should not persist to workspaces automatically.
            Assert.Null(WorkspaceManager.LoadWorkspace(bundleName));
        }
        finally
        {
            DeleteWorkspaceFile(bundleName);
            DeleteBundleFile(bundlePath);
        }
    }

    [Fact]
    public void ExportBundle_BlockedByPolicy_IsRejected()
    {
        using var policyScope = PolicyFileScope.WithPolicy(new WorkspacePolicyHooks
        {
            AllowWorkspaceBundleExport = false,
            AllowWorkspaceBundleImport = true,
            MaxTabsPerWorkspace = 0
        });

        string sourceName = $"tabs_ws_{Guid.NewGuid():N}";
        string bundlePath = Path.Combine(Path.GetTempPath(), $"nova_ws_{Guid.NewGuid():N}.novaws.json");

        try
        {
            Assert.True(WorkspaceManager.SaveWorkspace(sourceName, BuildSession()));
            bool exported = WorkspaceManager.ExportWorkspaceBundle(sourceName, bundlePath, "test-user");
            Assert.False(exported);
            Assert.False(File.Exists(bundlePath));
        }
        finally
        {
            DeleteWorkspaceFile(sourceName);
            DeleteBundleFile(bundlePath);
        }
    }

    [Fact]
    public void ImportBundle_RespectsPolicyMaxTabs()
    {
        string sourceName = $"tabs_ws_{Guid.NewGuid():N}";
        string importName = $"tabs_ws_import_{Guid.NewGuid():N}";
        string bundlePath = Path.Combine(Path.GetTempPath(), $"nova_ws_{Guid.NewGuid():N}.novaws.json");

        using var exportPolicy = PolicyFileScope.WithDefault();
        try
        {
            Assert.True(WorkspaceManager.SaveWorkspace(sourceName, BuildSession(tabCount: 3)));
            Assert.True(WorkspaceManager.ExportWorkspaceBundle(sourceName, bundlePath, "test-user"));
        }
        finally
        {
            DeleteWorkspaceFile(sourceName);
        }

        using var importPolicy = PolicyFileScope.WithPolicy(new WorkspacePolicyHooks
        {
            AllowWorkspaceBundleExport = true,
            AllowWorkspaceBundleImport = true,
            MaxTabsPerWorkspace = 1
        });

        try
        {
            bool imported = WorkspaceManager.ImportWorkspaceBundle(bundlePath, importName, out var error);
            Assert.False(imported);
            Assert.Contains("max-tabs", error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Null(WorkspaceManager.LoadWorkspace(importName));
        }
        finally
        {
            DeleteWorkspaceFile(importName);
            DeleteBundleFile(bundlePath);
        }
    }

    [Fact]
    public void LoadWorkspaceBundleSession_BlockedByPolicy()
    {
        string bundlePath = Path.Combine(Path.GetTempPath(), $"nova_ws_{Guid.NewGuid():N}.novaws.json");
        string bundleName = $"handoff_{Guid.NewGuid():N}";

        using var exportPolicy = PolicyFileScope.WithDefault();
        Assert.True(WorkspaceManager.ExportWorkspaceBundle(bundleName, BuildSession(tabCount: 2), bundlePath, "test-user"));

        using var importPolicy = PolicyFileScope.WithPolicy(new WorkspacePolicyHooks
        {
            AllowWorkspaceBundleExport = true,
            AllowWorkspaceBundleImport = false,
            MaxTabsPerWorkspace = 0
        });

        try
        {
            bool loaded = WorkspaceManager.LoadWorkspaceBundleSession(bundlePath, out var _, out var session, out var error);
            Assert.False(loaded);
            Assert.Null(session);
            Assert.Contains("blocked-by-policy", error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteBundleFile(bundlePath);
            DeleteWorkspaceFile(bundleName);
        }
    }

    [Fact]
    public void WorkspaceBundleOps_RequireSsoPolicy_BlocksWhenPlaceholderUnconfigured()
    {
        string bundlePath = Path.Combine(Path.GetTempPath(), $"nova_ws_{Guid.NewGuid():N}.novaws.json");
        string bundleName = $"handoff_{Guid.NewGuid():N}";

        using var ssoPolicy = PolicyFileScope.WithPolicy(new WorkspacePolicyHooks
        {
            AllowWorkspaceBundleExport = true,
            AllowWorkspaceBundleImport = true,
            MaxTabsPerWorkspace = 0,
            RequireSsoForWorkspaceBundles = true,
            SsoAuthorityUrl = "",
            SsoClientId = ""
        });

        try
        {
            bool exported = WorkspaceManager.ExportWorkspaceBundle(bundleName, BuildSession(tabCount: 1), bundlePath, "test-user");
            Assert.False(exported);
            Assert.False(File.Exists(bundlePath));

            bool opened = WorkspaceManager.LoadWorkspaceBundleSession(bundlePath, out _, out var session, out var error);
            Assert.False(opened);
            Assert.Null(session);
            Assert.Contains("sso-required", error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteBundleFile(bundlePath);
            DeleteWorkspaceFile(bundleName);
        }
    }

    private static NovaSession BuildSession(int tabCount = 1)
    {
        var session = new NovaSession
        {
            ActiveTabIndex = 0,
        };

        for (int i = 0; i < tabCount; i++)
        {
            string paneId = $"pane-{i}";
            session.Tabs.Add(
                new TabSession
                {
                    TabId = Guid.NewGuid().ToString("D"),
                    Title = $"Terminal {i + 1}",
                    UserTitle = i == 0 ? "Work" : $"Work {i + 1}",
                    IsPinned = i == 0,
                    IsProtected = i == 0,
                    ActivePaneId = paneId,
                    ZoomedPaneId = paneId,
                    BroadcastInputEnabled = true,
                    Root = new PaneNode
                    {
                        Type = NodeType.Leaf,
                        PaneId = paneId,
                        Command = "bash",
                        Arguments = "-l"
                    }
                });
        }

        return session;
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

    private static void DeleteWorkspaceTemplateFile(string name)
    {
        string path = Path.Combine(AppPaths.WorkspaceTemplatesDirectory, $"{SanitizeName(name)}.json");
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

    private sealed class PolicyFileScope : IDisposable
    {
        private readonly bool _hadExistingFile;
        private readonly string? _originalJson;

        private PolicyFileScope(bool hadExistingFile, string? originalJson)
        {
            _hadExistingFile = hadExistingFile;
            _originalJson = originalJson;
        }

        public static PolicyFileScope WithDefault()
        {
            return WithPolicy(new WorkspacePolicyHooks());
        }

        public static PolicyFileScope WithPolicy(WorkspacePolicyHooks policy)
        {
            string path = AppPaths.WorkspacePolicyFilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            bool hadExisting = File.Exists(path);
            string? originalJson = hadExisting ? File.ReadAllText(path) : null;

            string json = System.Text.Json.JsonSerializer.Serialize(policy, AppJsonContext.Default.WorkspacePolicyHooks);
            File.WriteAllText(path, json);
            WorkspacePolicyManager.ResetCacheForTests();

            return new PolicyFileScope(hadExisting, originalJson);
        }

        public void Dispose()
        {
            string path = AppPaths.WorkspacePolicyFilePath;
            if (_hadExistingFile)
            {
                File.WriteAllText(path, _originalJson ?? "{}");
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            WorkspacePolicyManager.ResetCacheForTests();
        }
    }
}
