using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace NovaTerminal.McpServer.Tools;

[McpServerToolType]
public static class VtTools
{
    // Docs that describe VT coverage / known gaps, in preference order.
    private static readonly string[] ConformanceDocs =
    {
        "vt_coverage_matrix.md",
        "vt_ghostty_gap_matrix.md",
        "TechnicalGapChecklist.md",
    };

    [McpServerTool(Name = "novaterminal.get_vt_conformance_summary"),
     Description("Returns NovaTerminal's VT/ANSI conformance status and known terminal gaps, gathered from the repo's coverage/gap matrices (e.g. docs/vt_coverage_matrix.md). Use before changing parser/rendering behavior to understand what is and isn't supported.")]
    public static string GetVtConformanceSummary(RepoContext repo)
    {
        var sb = new StringBuilder();
        foreach (var doc in ConformanceDocs)
        {
            if (repo.TryReadDoc(doc, out var content, out _))
            {
                sb.Append("## Source: docs/").Append(doc).Append("\n\n");
                sb.Append(content.TrimEnd()).Append("\n\n");
            }
        }

        if (sb.Length == 0)
        {
            return "VT conformance documents could not be located. " +
                   "Expected one of: docs/" + string.Join(", docs/", ConformanceDocs) +
                   " (set NOVATERMINAL_REPO_ROOT if running outside the repo).";
        }

        return sb.ToString().TrimEnd();
    }
}
