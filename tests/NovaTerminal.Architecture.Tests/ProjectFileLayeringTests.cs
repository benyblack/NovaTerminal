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
        Assert.Equal(new[] { "NovaTerminal.VT" }, refs);
    }

    [Fact]
    public void Rendering_csproj_only_references_Vt()
    {
        var refs = ProjectReferences("src/NovaTerminal.Rendering/NovaTerminal.Rendering.csproj");
        Assert.Equal(new[] { "NovaTerminal.VT" }, refs);
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
}
