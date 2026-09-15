using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Text.RegularExpressions;

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

    // Same CA1861 reasoning as VtOnly above. Order matches the csproj's own ItemGroup so
    // Assert.Equal's ordered comparison doesn't need a Sort/OrderBy on either side.
    private static readonly string[] McpServerLeafDependencies =
        ["NovaTerminal.AgentHost.Contracts", "NovaTerminal.Backup", "NovaTerminal.VtContract"];

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

    private static string[] PackageReferences(string csprojRelativePath)
    {
        var path = Path.Combine(RepoRoot(), csprojRelativePath);
        var doc = XDocument.Load(path);
        return doc.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
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

    /// <summary>
    /// The IL-level sibling (<c>CommandAssist_must_not_depend_on_Avalonia_or_the_App</c>) only sees
    /// dependencies the compiler emitted. A <c>ProjectReference</c> or Avalonia
    /// <c>PackageReference</c> that nothing uses yet would pass there and still hand the next
    /// change a legal path back into the UI toolkit, so assert the project edge too.
    /// </summary>
    [Fact]
    public void CommandAssist_csproj_must_have_no_project_or_avalonia_references()
    {
        var refs = ProjectReferences("src/NovaTerminal.CommandAssist/NovaTerminal.CommandAssist.csproj");
        Assert.Empty(refs);

        var packages = PackageReferences("src/NovaTerminal.CommandAssist/NovaTerminal.CommandAssist.csproj");
        Assert.DoesNotContain(packages, p => p.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The V2 Phase 5 seam ships interfaces, not providers: no network code, no API clients. The
    /// IL-level sibling (<c>CommandAssist_must_not_depend_on_networking</c>) catches code that calls
    /// an HTTP type; this catches the package landing first, which is how it would actually arrive -
    /// a PR that adds the client library and the provider in two commits, of which only the first
    /// gets merged in a hurry.
    /// </summary>
    [Fact]
    public void CommandAssist_csproj_must_have_no_networking_package_references()
    {
        string[] forbiddenPrefixes =
        [
            "System.Net",
            "Microsoft.Extensions.Http",
            "Grpc",
            "RestSharp",
            "Flurl",
            "Refit",
            "Azure.AI",
            "OpenAI",
            "Anthropic"
        ];

        var packages = PackageReferences("src/NovaTerminal.CommandAssist/NovaTerminal.CommandAssist.csproj");

        Assert.DoesNotContain(packages, p =>
            forbiddenPrefixes.Any(prefix => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
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
    /// <c>NovaTerminal.Backup</c> is allowed alongside the contracts leaf (Task 10a): the
    /// read-only backup MCP tools need <c>BackupService</c>. Fix round 1 of that same task
    /// first tried reaching it through <c>NovaTerminal.Platform</c> instead - which passed
    /// this exact assertion, because at the csproj level "references one extra project"
    /// looks the same regardless of what that project drags in. It shipped a real hole:
    /// Platform references Pty, so McpServer -> Platform -> Pty transitively broke "does not
    /// reference App, VT, Pty, or Rendering" with the reasoning behind that rule fully
    /// intact. <c>NovaTerminal.Backup</c> closes that hole by construction rather than by
    /// naming every forbidden transitive hop: <see cref="Backup_csproj_has_no_project_references"/>
    /// pins it as a leaf, so nothing it brings in can ever be more than the BCL, no matter
    /// what future code adds to it.
    ///
    /// Asserted at the csproj level only: the IL-level sibling in <see cref="LayeringTests"/>
    /// would need Architecture.Tests to take a ProjectReference on McpServer (an Exe) purely
    /// to load it for inspection. A forbidden *project reference* is the failure mode being
    /// guarded here, and this catches it without that. Added in response to Greptile review
    /// P2 on PR #245.
    /// </summary>
    [Fact]
    public void McpServer_csproj_only_references_approved_leaf_dependencies()
    {
        var refs = ProjectReferences("src/NovaTerminal.McpServer/NovaTerminal.McpServer.csproj");
        Assert.Equal(McpServerLeafDependencies, refs);
    }

    /// <summary>
    /// The assertion that actually keeps <see cref="McpServer_csproj_only_references_approved_leaf_dependencies"/>
    /// meaningful long-term. That test only pins McpServer's own reference list; it says
    /// nothing about what <c>NovaTerminal.Backup</c> itself can reach. Without this test,
    /// someone could add e.g. <c>NovaTerminal.Platform</c> as a "small, surely harmless"
    /// reference to Backup one day - reopening precisely the McpServer -> Platform -> Pty
    /// hole fix round 1 of Task 10a just closed, since McpServer's own csproj would look
    /// untouched. A leaf has no project references, ever: that is the entire safety argument,
    /// so it has to be checked directly rather than inferred from what currently happens to
    /// be true.
    /// </summary>
    [Fact]
    public void Backup_csproj_has_no_project_references()
    {
        var refs = ProjectReferences("src/NovaTerminal.Backup/NovaTerminal.Backup.csproj");
        Assert.Empty(refs);
    }

    [Fact]
    public void VtContract_csproj_has_no_project_references()
    {
        var refs = ProjectReferences("src/NovaTerminal.VtContract/NovaTerminal.VtContract.csproj");
        Assert.Empty(refs);
    }

    /// <summary>
    /// #310: panes must be hosted by the sideloaded ConPTY host, not the OS conhost.exe.
    /// portable-pty only uses it when a <c>conpty.dll</c> sits next to the executable, and that
    /// DLL only finds its server at <c>&lt;arch&gt;\OpenConsole.exe</c> — so both files have to be
    /// in the app output. Losing them does not fail the build and does not fail any behavioural
    /// test; it silently puts every session back on the console host whose crash killed the
    /// user's shell in #310. The csproj's own VerifySideloadedConPtyHost target guards the
    /// package layout, which is a different failure: this guards the copy items themselves.
    /// </summary>
    [Fact]
    public void App_csproj_must_ship_the_sideloaded_conpty_host()
    {
        const string appCsproj = "src/NovaTerminal.App/NovaTerminal.App.csproj";
        var doc = XDocument.Load(Path.Combine(RepoRoot(), appCsproj));

        Assert.Contains("Microsoft.Windows.Console.ConPTY", PackageReferences(appCsproj));

        // conpty.dll has to sit next to the exe (that is the only place portable-pty looks), and
        // the hosts have to be declared per machine architecture, arm64 included: an x64 bundle
        // started on Windows-on-ARM resolves the arm64 host, and win-x64 is the only Windows RID
        // released.
        var hostLinks = doc.Descendants("NovaRequiredConPtyHost")
            .Select(e => ((string?)e.Element("Link") ?? string.Empty).Replace('\\', '/'))
            .ToArray();
        Assert.Contains("arm64/OpenConsole.exe", hostLinks);

        var conPtyDll = doc.Descendants("Content")
            .SingleOrDefault(c => ((string?)c.Element("Link") ?? string.Empty) == "conpty.dll");
        Assert.NotNull(conPtyDll);

        var hostCopy = doc.Descendants("Content")
            .SingleOrDefault(c => ((string?)c.Attribute("Include") ?? string.Empty)
                .Contains("@(NovaRequiredConPtyHost)", StringComparison.Ordinal));
        Assert.NotNull(hostCopy);

        // Both copy destinations, not just one: an output-only copy keeps every dev build working
        // while the released bundle silently ships without a console host - which is #310 again,
        // for users only.
        foreach (var item in new[] { conPtyDll, hostCopy })
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)item!.Element("CopyToOutputDirectory")),
                $"Content '{(string?)item.Attribute("Include")}' must declare CopyToOutputDirectory.");
            Assert.False(string.IsNullOrWhiteSpace((string?)item.Element("CopyToPublishDirectory")),
                $"Content '{(string?)item.Attribute("Include")}' must declare CopyToPublishDirectory.");
        }
    }

    // Same CA1861 reasoning as VtOnly above.
    private static readonly string[] ProjectsAllowedToReferenceVelopack =
        ["src/NovaTerminal.App/NovaTerminal.App.csproj"];

    /// <summary>
    /// Velopack is the Windows install/update host. It is referenced for exactly one reason -
    /// <c>VelopackApp.Build().Run()</c> and the update seam in <c>NovaTerminal.App/Update</c> - and
    /// must not spread. A second project taking the reference would put install-location and
    /// restart-the-process concerns behind a library boundary where nothing can see them, and would
    /// drag an unsigned-updater dependency into layers that are meant to be host-agnostic.
    /// </summary>
    [Fact]
    public void Velopack_is_referenced_only_by_the_App()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(RepoRoot(), p).Replace('\\', '/'))
            .Where(rel => PackageReferences(rel).Any(
                p => p.Equals("Velopack", StringComparison.OrdinalIgnoreCase)))
            .Where(rel => !ProjectsAllowedToReferenceVelopack.Contains(rel))
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The Velopack NuGet package and the <c>vpk</c> CLI must be the same version. <c>vpk pack</c>
    /// writes the package format and the <c>releases.win.json</c> feed that the in-app SDK then
    /// reads, so a mismatch is a compatibility question nobody wants to answer at release time.
    /// Until this test existed the coupling was enforced only by a comment in
    /// <c>Directory.Packages.props</c> - which a version bump of either side would sail straight
    /// past. Now bumping one without the other fails a gating test instead of shipping.
    /// </summary>
    [Fact]
    public void Velopack_package_and_vpk_cli_versions_agree()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Packages.props"));
        var packageVersion = props.Descendants("PackageVersion")
            .Where(e => string.Equals((string?)e.Attribute("Include"), "Velopack", StringComparison.OrdinalIgnoreCase))
            .Select(e => (string?)e.Attribute("Version"))
            .SingleOrDefault();
        Assert.False(string.IsNullOrWhiteSpace(packageVersion),
            "Directory.Packages.props declares no Velopack PackageVersion.");

        var workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github/workflows/release.yml"));
        var match = Regex.Match(workflow, @"dotnet\s+tool\s+install\s+-g\s+vpk\s+--version\s+(?<ver>[0-9][^\s""']*)");
        Assert.True(match.Success,
            "release.yml no longer contains a version-pinned 'dotnet tool install -g vpk --version <ver>' step.");

        Assert.Equal(packageVersion, match.Groups["ver"].Value);
    }
}
