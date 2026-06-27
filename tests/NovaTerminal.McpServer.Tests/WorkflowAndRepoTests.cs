using System.IO;
using NovaTerminal.McpServer;
using NovaTerminal.McpServer.Tools;

namespace NovaTerminal.McpServer.Tests;

public class WorkflowToolsTests
{
    [Fact]
    public void Prompt_IncludesStructuredSections()
    {
        var prompt = WorkflowTools.GenerateCodexPromptForIssue("Improve SSH key authentication UX", "Add a key provisioning wizard.");
        Assert.Contains("## Relevant areas", prompt);
        Assert.Contains("## Architectural constraints", prompt);
        Assert.Contains("## Acceptance criteria", prompt);
        Assert.Contains("## Risks", prompt);
    }

    [Fact]
    public void Prompt_MapsKeywordsToSubsystems()
    {
        var ssh = WorkflowTools.GenerateCodexPromptForIssue("SSH key auth", "");
        Assert.Contains("SSH:", ssh);

        var theme = WorkflowTools.GenerateCodexPromptForIssue("New theme palette", "");
        Assert.Contains("Theming:", theme);

        var parser = WorkflowTools.GenerateCodexPromptForIssue("Handle a new CSI escape sequence", "");
        Assert.Contains("VT/ANSI:", parser);
    }

    [Fact]
    public void Prompt_UnmappedTopic_FallsBackToArchitectureGuidance()
    {
        var prompt = WorkflowTools.GenerateCodexPromptForIssue("Refactor the gizmo widget", "");
        Assert.Contains("get_architecture_map", prompt);
    }

    [Fact]
    public void ProjectSummary_IsNonEmpty()
    {
        Assert.Contains("NovaTerminal", ProjectTools.GetProjectSummary());
    }

    [Fact]
    public void NullInputs_DoNotThrow()
    {
        var prompt = WorkflowTools.GenerateCodexPromptForIssue(null!, null!);
        Assert.Contains("# Implementation prompt:", prompt);
    }
}

public class RepoContextTests
{
    [Fact]
    public void ReadDoc_ConfinedToDocs_RejectsTraversal()
    {
        string root = Path.Combine(Path.GetTempPath(), "nova-mcp-test-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            File.WriteAllText(Path.Combine(root, "docs", "Hello.md"), "# hello");
            File.WriteAllText(Path.Combine(root, "secret.txt"), "top secret");

            var repo = new RepoContext(root);

            // Valid doc reads.
            Assert.True(repo.TryReadDoc("Hello.md", out var content, out _));
            Assert.Equal("# hello", content);

            // Traversal out of docs/ is rejected.
            Assert.False(repo.TryReadDoc("../secret.txt", out _, out var error));
            Assert.Contains("outside the docs/ directory", error);

            // Missing file reports cleanly.
            Assert.False(repo.TryReadDoc("Nope.md", out _, out var missingError));
            Assert.Contains("was not found", missingError);

            // Malformed path (embedded null) is rejected gracefully, not thrown.
            Assert.False(repo.TryReadDoc("bad\0name.md", out _, out var badError));
            Assert.Contains("invalid", badError);

            // Listing finds the doc.
            Assert.Contains("Hello.md", repo.ListDocs());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void NoRepoRoot_ReportsUnavailable()
    {
        var repo = new RepoContext(null);
        Assert.False(repo.TryReadDoc("Hello.md", out _, out var error));
        Assert.Contains("could not be located", error);
        Assert.Empty(repo.ListDocs());
    }
}
