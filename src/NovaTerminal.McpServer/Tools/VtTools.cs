using System.Collections.Generic;
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

    // Curated explanations for the most common control sequences, keyed by a normalized token.
    // CSI entries are keyed by final byte ("CSI:J"); OSC by numeric code ("OSC:0"); a few ESC/DCS.
    private static readonly Dictionary<string, string> SequenceTable = new()
    {
        ["CSI:A"] = "CUU — Cursor Up Ps times.",
        ["CSI:B"] = "CUD — Cursor Down Ps times.",
        ["CSI:C"] = "CUF — Cursor Forward Ps times.",
        ["CSI:D"] = "CUB — Cursor Back Ps times.",
        ["CSI:E"] = "CNL — Cursor Next Line Ps times (column 1). NOT currently handled by NovaTerminal's parser.",
        ["CSI:F"] = "CPL — Cursor Previous Line Ps times (column 1). NOT currently handled by NovaTerminal's parser.",
        ["CSI:G"] = "CHA — Cursor Horizontal Absolute to column Ps.",
        ["CSI:H"] = "CUP — Cursor Position to row;col (1-based).",
        ["CSI:J"] = "ED — Erase in Display (0=below, 1=above, 2=all, 3=all+scrollback).",
        ["CSI:K"] = "EL — Erase in Line (0=right, 1=left, 2=whole line).",
        ["CSI:L"] = "IL — Insert Ps blank lines.",
        ["CSI:M"] = "DL — Delete Ps lines.",
        ["CSI:P"] = "DCH — Delete Ps characters (count clamped to the line width).",
        ["CSI:S"] = "SU — Scroll Up Ps lines (count clamped, see #124).",
        ["CSI:T"] = "SD — Scroll Down Ps lines.",
        ["CSI:X"] = "ECH — Erase Ps characters.",
        ["CSI:@"] = "ICH — Insert Ps blank characters.",
        ["CSI:b"] = "REP — Repeat the preceding character Ps times. NOT currently handled by NovaTerminal's parser (it has no REP handler; CSI params are clamped generically but the sequence is a no-op).",
        ["CSI:d"] = "VPA — Line Position Absolute (row Ps).",
        ["CSI:m"] = "SGR — Select Graphic Rendition (colors/bold/underline/etc.).",
        ["CSI:r"] = "DECSTBM — Set Top/Bottom margins (scroll region).",
        ["CSI:h"] = "SM / DECSET — Set (mode/private mode), e.g. ?1049h = alt screen, ?25h = show cursor, ?2004h = bracketed paste.",
        ["CSI:l"] = "RM / DECRST — Reset (mode/private mode), e.g. ?1049l = leave alt screen, ?25l = hide cursor.",
        ["CSI:n"] = "DSR — Device Status Report (e.g. 6n = cursor position report).",
        ["CSI:c"] = "DA — Device Attributes (primary/secondary).",
        ["CSI:q"] = "DECSCUSR (with space intermediate) — set cursor style.",
        ["OSC:0"] = "Set icon name and window title.",
        ["OSC:2"] = "Set window title.",
        ["OSC:7"] = "Report current working directory (file:// URI).",
        ["OSC:8"] = "Hyperlink (OSC 8 ; params ; URI ST … ST).",
        ["OSC:52"] = "Clipboard get/set (base64). NOT currently supported by NovaTerminal — HandleOsc ignores OSC 52 (see docs/vt_coverage_matrix.md).",
        ["OSC:133"] = "Shell integration markers (A=prompt, B=cmd start, C=cmd accepted, D=cmd finished).",
        ["OSC:1337"] = "iTerm2 proprietary (incl. inline images: 1337;File=…).",
        ["OSC:1339"] = "Tunneled Sixel/Kitty image payload.",
        ["ESC:c"] = "RIS — Reset to Initial State (full terminal reset).",
        ["ESC:7"] = "DECSC — Save cursor.",
        ["ESC:8"] = "DECRC — Restore cursor.",
        ["ESC:M"] = "RI — Reverse Index (scroll down if at top).",
        ["DCS"] = "Device Control String — used by NovaTerminal for Sixel images (… q … ST).",
        ["APC"] = "Application Program Command — used by NovaTerminal for Kitty graphics (ESC _ G … ST).",
    };

    [McpServerTool(Name = "novaterminal.explain_escape_sequence"),
     Description("Explains a VT/ANSI escape sequence (standard meaning). Accepts forms like 'ESC[2J', '\\x1b[2J', 'CSI 2 J', 'CSI ?25h', 'OSC 7', or 'ESC c'. Entries note where NovaTerminal does NOT handle a sequence; for the authoritative support matrix use novaterminal.get_vt_conformance_summary.")]
    public static string ExplainEscapeSequence(
        [Description("The escape sequence to explain, e.g. 'ESC[2J', 'CSI ?1049h', 'OSC 8', 'ESC c'.")] string sequence)
    {
        if (string.IsNullOrWhiteSpace(sequence))
        {
            return "Provide a sequence, e.g. 'ESC[2J', 'CSI ?25h', 'OSC 7', or 'ESC c'.";
        }

        // Normalize common notations to a canonical introducer + body.
        string s = sequence.Trim()
            .Replace("", "ESC", System.StringComparison.Ordinal)
            .Replace("\\x1b", "ESC", System.StringComparison.OrdinalIgnoreCase)
            .Replace("\\e", "ESC", System.StringComparison.OrdinalIgnoreCase);

        string upper = s.ToUpperInvariant();

        // CSI: "ESC[", "ESC [" or "CSI"
        string? csiBody = null;
        if (upper.StartsWith("ESC[", System.StringComparison.Ordinal)) csiBody = s.Substring(4);
        else if (upper.StartsWith("ESC [", System.StringComparison.Ordinal)) csiBody = s.Substring(5);
        else if (upper.StartsWith("CSI", System.StringComparison.Ordinal)) csiBody = s.Substring(3);
        if (csiBody is not null)
        {
            // Final byte is the last non-space char in the body.
            string body = csiBody.Trim();
            if (body.Length == 0) return "Incomplete CSI sequence (no final byte).";
            char finalByte = body[^1];
            string key = "CSI:" + finalByte;
            return SequenceTable.TryGetValue(key, out var desc)
                ? $"CSI sequence, final byte '{finalByte}': {desc}"
                : $"CSI sequence with final byte '{finalByte}': not in the curated table. Params: '{body[..^1]}'.";
        }

        // OSC: "ESC]", "ESC ]" or "OSC"
        string? oscBody = null;
        if (upper.StartsWith("ESC]", System.StringComparison.Ordinal)) oscBody = s.Substring(4);
        else if (upper.StartsWith("ESC ]", System.StringComparison.Ordinal)) oscBody = s.Substring(5);
        else if (upper.StartsWith("OSC", System.StringComparison.Ordinal)) oscBody = s.Substring(3);
        if (oscBody is not null)
        {
            string body = oscBody.Trim().TrimStart(' ');
            // Leading numeric code up to ';' or space.
            int i = 0;
            while (i < body.Length && char.IsDigit(body[i])) i++;
            string code = body[..i];
            string key = "OSC:" + code;
            return SequenceTable.TryGetValue(key, out var desc)
                ? $"OSC {code}: {desc}"
                : $"OSC sequence (code '{code}'): not in the curated table.";
        }

        // DCS: literal "DCS" or the 7-bit introducer ESC P ("ESCP" / "ESC P"). Check before the
        // generic ESC handling so the 'P' isn't misread as a simple ESC sequence.
        if (upper.StartsWith("DCS", System.StringComparison.Ordinal)
            || upper.StartsWith("ESCP", System.StringComparison.Ordinal)
            || upper.StartsWith("ESC P", System.StringComparison.Ordinal))
            return "DCS: " + SequenceTable["DCS"];

        // APC: literal "APC" or the 7-bit introducer ESC _ ("ESC_" / "ESC _").
        if (upper.StartsWith("APC", System.StringComparison.Ordinal)
            || upper.StartsWith("ESC_", System.StringComparison.Ordinal)
            || upper.StartsWith("ESC _", System.StringComparison.Ordinal))
            return "APC: " + SequenceTable["APC"];

        // Other ESC Fe / simple ESC sequences: "ESC c", "ESC 7", "ESC M".
        if (upper.StartsWith("ESC", System.StringComparison.Ordinal))
        {
            string rest = s.Substring(3).Trim();
            if (rest.Length > 0)
            {
                string key = "ESC:" + rest[0];
                if (SequenceTable.TryGetValue(key, out var desc))
                    return $"ESC {rest[0]}: {desc}";
            }
        }

        return $"Unrecognized sequence '{sequence}'. Use forms like 'ESC[2J', 'CSI ?25h', 'OSC 7', 'ESC c', 'DCS', or 'APC'.";
    }

    [McpServerTool(Name = "novaterminal.generate_vt_test_plan"),
     Description("Generates a structured VT/ANSI test plan for a parser/rendering feature or sequence: cases to cover (parsing, state, reflow, edge cases), where tests live, and how to verify against conformance.")]
    public static string GenerateVtTestPlan(
        [Description("The VT feature or sequence under test, e.g. 'OSC 8 hyperlinks' or 'DECSTBM scroll region'.")] string feature)
    {
        feature ??= string.Empty;
        return $$"""
        # VT test plan: {{feature.Trim()}}

        ## Cases to cover
        - Happy path: well-formed sequence(s) produce the expected buffer/cursor state.
        - Parameter handling: default (omitted) params, multiple params, and clamped/oversized
          params (CSI numeric params are capped — see #124).
        - Split across writes: the sequence arrives in fragments across multiple Process() calls.
        - Malformed/partial: truncated or invalid sequences must not throw and must recover so
          following text renders (see AnsiParserHardeningTests).
        - Interaction with reflow: behavior survives a resize (lossless reflow invariant, #123).
        - Alt screen vs main screen, and scrollback, where relevant.

        ## Where tests live
        - Pure parser/buffer behavior → tests/NovaTerminal.VT.Tests/.
        - Replay/regression (real byte streams) → tests/NovaTerminal.App.Tests/ReplayTests/.
        - Reflow scenarios → tests/NovaTerminal.App.Tests/ReflowScenariosTests.cs and BufferTests/.

        ## Verification
        - Drive input via AnsiParser.Process and assert on TerminalBuffer state under the read lock.
        - Cross-check against novaterminal.get_vt_conformance_summary for known gaps/expectations.
        - For hostile-input robustness, consider a case in the SharpFuzz harness (#124).
        """;
    }
}
