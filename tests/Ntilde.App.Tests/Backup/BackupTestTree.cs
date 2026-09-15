using System.Text.Json;

namespace NovaTerminal.Tests.Backup;

/// <summary>
/// A disposable temp app-data root pre-populated with realistic content, so backup tests
/// never touch the real profile. Not tied to NOVATERM_APPDATA_ROOT — BackupService takes a
/// root explicitly, which keeps these tests parallel-safe.
/// </summary>
public sealed class BackupTestTree : IDisposable
{
    public string Root { get; }

    private BackupTestTree(string root) => Root = root;

    public static BackupTestTree CreatePopulated()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nova_backup_test_{Guid.NewGuid():N}");
        var tree = new BackupTestTree(root);

        tree.WriteFile("settings.json", """{"FontSize":14,"ThemeName":"Default"}""");
        tree.WriteFile(Path.Combine("themes", "solarized.json"), """{"name":"Solarized"}""");
        // PascalCase: SshJsonContext sets no naming policy, so JsonSshProfileStore's
        // SshStoreDocument (SchemaVersion, Profiles) round-trips through the CLR property
        // names verbatim. A lowercase shape here would be a fixture nothing could produce.
        tree.WriteFile(Path.Combine("ssh", "profiles.json"), """{"SchemaVersion":1,"Profiles":[]}""");
        tree.WriteFile(Path.Combine("ssh", "native_known_hosts.json"), "[]");
        tree.WriteFile(Path.Combine("workspaces", "default.json"), """{"name":"default"}""");
        tree.WriteFile(Path.Combine("workspace_templates", "dev.json"), """{"name":"dev"}""");
        tree.WriteFile(Path.Combine("policy", "workspace_policy.json"), "{}");
        tree.WriteFile(Path.Combine("command-assist", "snippets.json"), "[]");

        // Excluded content — must never appear in a bundle.
        tree.WriteFile(Path.Combine("logs", "debug.log"), "log line");
        tree.WriteFile(Path.Combine("recordings", "session.cast"), "recording");
        tree.WriteFile(Path.Combine("sessions", "last_session.json"), "{}");
        tree.WriteFile(Path.Combine("command-assist", "history.jsonl"), """{"cmd":"secret-history-entry"}""");
        tree.WriteFile("command-palette-usage.json", "{}");

        return tree;
    }

    /// <summary>An empty root, for import-into-fresh-machine tests.</summary>
    public static BackupTestTree CreateEmpty()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nova_backup_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new BackupTestTree(root);
    }

    public void WriteFile(string relativePath, string contents)
    {
        string full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    public string ReadFile(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    public bool Exists(string relativePath) => File.Exists(Path.Combine(Root, relativePath));

    public JsonDocument ReadJson(string relativePath) => JsonDocument.Parse(ReadFile(relativePath));

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch { /* temp cleanup is best-effort */ }
    }
}
