using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// IL-level layering tests (see <see cref="LayeringTests"/>) only catch dependencies that
/// the compiler emits into the assembly. A project can still declare a forbidden
/// <c>&lt;ProjectReference&gt;</c> that contributes nothing to the IL today but allows
/// future code to silently reach across the boundary - and transitively pulls the
/// forbidden assembly into every downstream consumer.
///
/// This test reads the csproj XML directly to assert the project edge, not just the
/// emitted-type edge. Added in response to Codex review P2 on PR #73.
/// </summary>
public class ProjectFileLayeringTests
{
    // Hoisted out of the two Assert.Equal calls below to satisfy CA1861 (constant array
    // arguments are re-allocated on every call). Both assertions expect the same single
    // reference, so one field serves both - and this project is now built with
    // TreatWarningsAsErrors, so the analyzer is enforced rather than advisory (#108).
    private static readonly string[] VtOnly = ["NovaTerminal.VT"];

    // Same CA1861 reasoning as VtOnly above.
    private static readonly string[] AgentHostContractsOnly = ["NovaTerminal.AgentHost.Contracts"];

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NovaTerminal.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private static string[] ProjectReferences(string csprojRelativePath)
    {
        var path = Path.Combine(RepoRoot(), csprojRelativePath);
        var doc = XDocument.Load(path);
        return doc.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Select(p => Path.GetFileNameWithoutExtension(p.Replace('\\', '/')))
            .ToArray();
    }

    [Fact]
    public void Pty_csproj_must_not_reference_Vt()
    {
        var refs = ProjectReferences("src/NovaTerminal.Pty/NovaTerminal.Pty.csproj");
        Assert.DoesNotContain("NovaTerminal.VT", refs);
    }

    [Fact]
    public void Replay_csproj_only_references_Vt()
    {
        var refs = ProjectReferences("src/NovaTerminal.Replay/NovaTerminal.Replay.csproj");
        Assert.Equal(VtOnly, refs);
    }

    [Fact]
    public void Rendering_csproj_only_references_Vt()
    {
        var refs = ProjectReferences("src/NovaTerminal.Rendering/NovaTerminal.Rendering.csproj");
        Assert.Equal(VtOnly, refs);
    }

    [Fact]
    public void Vt_csproj_must_have_no_project_references()
    {
        var refs = ProjectReferences("src/NovaTerminal.VT/NovaTerminal.VT.csproj");
        Assert.Empty(refs);
    }

    [Fact]
    public void AgentHostContracts_csproj_must_have_no_project_references()
    {
        var refs = ProjectReferences("src/NovaTerminal.AgentHost.Contracts/NovaTerminal.AgentHost.Contracts.csproj");
        Assert.Empty(refs);
    }

    /// <summary>
    /// The MCP server is a *client* of the running app, reached over the AgentHost wire
    /// protocol - not an in-process consumer of terminal state. That boundary is what stops
    /// an MCP tool reaching into a <c>TerminalBuffer</c> directly instead of going through
    /// the protocol, so it is worth an assertion rather than only prose in
    /// <c>docs/MODULE_OWNERSHIP.md</c>.
    ///
    /// Asserted at the csproj level only: the IL-level sibling in <see cref="LayeringTests"/>
    /// would need Architecture.Tests to take a ProjectReference on McpServer (an Exe) purely
    /// to load it for inspection. A forbidden *project reference* is the failure mode being
    /// guarded here, and this catches it without that. Added in response to Greptile review
    /// P2 on PR #245.
    /// </summary>
    [Fact]
    public void McpServer_csproj_only_references_AgentHostContracts()
    {
        var refs = ProjectReferences("src/NovaTerminal.McpServer/NovaTerminal.McpServer.csproj");
        Assert.Equal(AgentHostContractsOnly, refs);
    }
}
