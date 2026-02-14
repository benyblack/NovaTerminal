using NovaTerminal.Core;
using NovaTerminal.Tests.Infra;

namespace NovaTerminal.Tests.Core;

[Collection("WorkspacePolicy")]
public sealed class WorkspacePolicyManagerTests
{
    [Fact]
    public void Current_Defaults_WhenPolicyFileMissing()
    {
        using var scope = PolicyFileScope.WithNoPolicyFile();
        var policy = WorkspacePolicyManager.Current;

        Assert.True(policy.AllowWorkspaceBundleExport);
        Assert.True(policy.AllowWorkspaceBundleImport);
        Assert.Equal(0, policy.MaxTabsPerWorkspace);
    }

    [Fact]
    public void Current_LoadsPolicyFileValues()
    {
        using var scope = PolicyFileScope.WithPolicy(new WorkspacePolicyHooks
        {
            AllowWorkspaceBundleExport = false,
            AllowWorkspaceBundleImport = true,
            MaxTabsPerWorkspace = 3,
            RequireSsoForWorkspaceBundles = true,
            SsoAuthorityUrl = "https://sso.example.local",
            SsoClientId = "nova-client"
        });

        var policy = WorkspacePolicyManager.Current;
        Assert.False(policy.AllowWorkspaceBundleExport);
        Assert.True(policy.AllowWorkspaceBundleImport);
        Assert.Equal(3, policy.MaxTabsPerWorkspace);
        Assert.True(policy.RequireSsoForWorkspaceBundles);
        Assert.Equal("https://sso.example.local", policy.SsoAuthorityUrl);
        Assert.Equal("nova-client", policy.SsoClientId);
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

        public static PolicyFileScope WithNoPolicyFile()
        {
            string path = AppPaths.WorkspacePolicyFilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            bool hadExisting = File.Exists(path);
            string? originalJson = hadExisting ? File.ReadAllText(path) : null;
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            WorkspacePolicyManager.ResetCacheForTests();
            return new PolicyFileScope(hadExisting, originalJson);
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
