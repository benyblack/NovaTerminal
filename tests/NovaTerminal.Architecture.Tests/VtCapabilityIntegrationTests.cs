namespace NovaTerminal.Architecture.Tests;

public class VtCapabilityIntegrationTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NovaTerminal.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    [Fact]
    public void Vt_conformance_workflow_watches_the_capability_contract()
    {
        var workflow = ReadRepositoryFile(".github/workflows/vt-conformance.yml");

        Assert.Contains("src/NovaTerminal.VtContract/**", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Pull_request_template_requires_capability_contract_evidence()
    {
        var template = ReadRepositoryFile(".github/pull_request_template.md");

        Assert.Contains("vt-capabilities.json", template, StringComparison.Ordinal);
        Assert.Contains("VtCapabilityContractTests.cs", template, StringComparison.Ordinal);
    }
}
