using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaTerminal.Conformance;

public static class VtConformanceReportTool
{
    private static readonly Regex InlineCodeRegex = new("`([^`]+)`", RegexOptions.Compiled);

    public static VtConformanceReport Generate(string repositoryRoot, string matrixPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(matrixPath);

        string repoRoot = Path.GetFullPath(repositoryRoot);
        string absoluteMatrixPath = Path.GetFullPath(Path.IsPathRooted(matrixPath)
            ? matrixPath
            : Path.Combine(repoRoot, matrixPath));

        string matrixText = File.ReadAllText(absoluteMatrixPath);
        string relativeMatrixPath = ToRepoRelativePath(repoRoot, absoluteMatrixPath);

        var rows = new List<VtConformanceRow>();
        var sections = new List<VtConformanceSection>();
        var errors = new List<VtConformanceIssue>();
        var warnings = new List<VtConformanceIssue>();

        ParseFeatureTables(repoRoot, relativeMatrixPath, absoluteMatrixPath, matrixText, rows, sections, errors, warnings);

        ValidateRows(relativeMatrixPath, rows, errors, warnings);

        var summary = BuildSummary(rows, errors.Count, warnings.Count);
        return new VtConformanceReport(
            SchemaVersion: 1,
            MatrixPath: relativeMatrixPath,
            MatrixSha256: ComputeSha256(matrixText),
            Summary: summary,
            Sections: sections,
            Rows: rows,
            Errors: errors,
            Warnings: warnings);
    }

    public static string Serialize(VtConformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static void WriteReport(VtConformanceReport report, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        string absoluteOutputPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(absoluteOutputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(absoluteOutputPath, Serialize(report));
    }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static void ParseFeatureTables(
        string repoRoot,
        string relativeMatrixPath,
        string absoluteMatrixPath,
        string matrixText,
        List<VtConformanceRow> rows,
        List<VtConformanceSection> sections,
        List<VtConformanceIssue> errors,
        List<VtConformanceIssue> warnings)
    {
        string normalizedText = matrixText.Replace("\r\n", "\n");
        string[] lines = normalizedText.Split('\n');
        string currentHeading = "Document";
        int sectionOrder = 0;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r');
            if (TryReadHeading(line, out string? heading))
            {
                currentHeading = heading!;
                continue;
            }

            if (!line.StartsWith('|'))
            {
                continue;
            }

            int tableStart = index;
            var tableLines = new List<(int LineNumber, string Text)>();
            while (index < lines.Length && lines[index].TrimStart().StartsWith('|'))
            {
                tableLines.Add((index + 1, lines[index].TrimEnd('\r')));
                index++;
            }

            index--;

            if (tableLines.Count < 2)
            {
                continue;
            }

            string[] headers = SplitMarkdownRow(tableLines[0].Text);
            if (!LooksLikeFeatureTable(headers))
            {
                continue;
            }

            if (!IsSeparatorRow(tableLines[1].Text))
            {
                errors.Add(new VtConformanceIssue(
                    Code: "table-missing-separator",
                    Severity: IssueSeverity.Error,
                    Message: $"Feature table '{currentHeading}' is missing a markdown separator row.",
                    MatrixPath: relativeMatrixPath,
                    LineNumber: tableLines[0].LineNumber,
                    Feature: null));
                continue;
            }

            int statusIndex = FindColumnIndex(headers, "Status");
            int evidenceIndex = FindColumnIndex(headers, "Evidence");
            int ownershipIndex = FindOptionalColumnIndex(headers, "Ownership");
            if (ownershipIndex < 0)
            {
                ownershipIndex = FindOptionalColumnIndex(headers, "Ownership (code)");
            }

            int notesIndex = FindOptionalColumnIndex(headers, "Known deviations");
            if (notesIndex < 0)
            {
                notesIndex = FindOptionalColumnIndex(headers, "Notes");
            }

            int specIndex = FindOptionalColumnIndex(headers, "Spec / Notes");
            if (specIndex < 0)
            {
                specIndex = FindOptionalColumnIndex(headers, "Notes");
            }

            var sectionRows = new List<VtConformanceRow>();
            for (int rowIndex = 2; rowIndex < tableLines.Count; rowIndex++)
            {
                (int lineNumber, string text) = tableLines[rowIndex];
                string[] cells = SplitMarkdownRow(text);
                if (cells.Length != headers.Length)
                {
                    errors.Add(new VtConformanceIssue(
                        Code: "table-column-count-mismatch",
                        Severity: IssueSeverity.Error,
                        Message: $"Feature table '{currentHeading}' row has {cells.Length} columns; expected {headers.Length}.",
                        MatrixPath: relativeMatrixPath,
                        LineNumber: lineNumber,
                        Feature: cells.Length > 0 ? NormalizeCell(cells[0]) : null));
                    continue;
                }

                string feature = NormalizeCell(cells[0]);
                string status = NormalizeCell(cells[statusIndex]);
                string evidenceText = NormalizeCell(cells[evidenceIndex]);
                string ownership = ownershipIndex >= 0 ? NormalizeCell(cells[ownershipIndex]) : string.Empty;
                string notes = notesIndex >= 0 ? NormalizeCell(cells[notesIndex]) : string.Empty;
                string specOrNotes = specIndex >= 0 ? NormalizeCell(cells[specIndex]) : string.Empty;

                var evidenceKinds = GetEvidenceKinds(evidenceText);
                var evidenceLinks = ExtractEvidenceLinks(repoRoot, evidenceText);

                var row = new VtConformanceRow(
                    Section: currentHeading,
                    Feature: feature,
                    Status: status,
                    SpecOrNotes: specOrNotes,
                    EvidenceText: evidenceText,
                    EvidenceKinds: evidenceKinds,
                    EvidenceLinks: evidenceLinks,
                    HasAutomatedEvidenceSignal: HasAutomatedEvidenceSignal(evidenceKinds),
                    HasLinkedEvidence: evidenceLinks.Any(link => link.Exists),
                    Ownership: ownership,
                    KnownDeviations: notes,
                    SourceLine: lineNumber);

                sectionRows.Add(row);
            }

            if (sectionRows.Count == 0)
            {
                warnings.Add(new VtConformanceIssue(
                    Code: "empty-feature-table",
                    Severity: IssueSeverity.Warning,
                    Message: $"Feature table '{currentHeading}' does not contain any parsable rows.",
                    MatrixPath: relativeMatrixPath,
                    LineNumber: tableLines[0].LineNumber,
                    Feature: null));
                continue;
            }

            sections.Add(new VtConformanceSection(
                Order: sectionOrder++,
                Title: currentHeading,
                StartLine: tableStart + 1,
                EndLine: tableLines[^1].LineNumber,
                RowCount: sectionRows.Count));

            rows.AddRange(sectionRows);
        }
    }

    private static void ValidateRows(
        string relativeMatrixPath,
        List<VtConformanceRow> rows,
        List<VtConformanceIssue> errors,
        List<VtConformanceIssue> warnings)
    {
        foreach (VtConformanceRow row in rows)
        {
            if (!IsKnownStatus(row.Status))
            {
                errors.Add(new VtConformanceIssue(
                    Code: "unknown-status",
                    Severity: IssueSeverity.Error,
                    Message: $"Unknown status '{row.Status}' in feature row '{row.Feature}'.",
                    MatrixPath: relativeMatrixPath,
                    LineNumber: row.SourceLine,
                    Feature: row.Feature));
                continue;
            }

            foreach (VtEvidenceLink link in row.EvidenceLinks)
            {
                if (!link.Exists)
                {
                    errors.Add(new VtConformanceIssue(
                        Code: "evidence-path-not-found",
                        Severity: IssueSeverity.Error,
                        Message: $"Evidence link '{link.Path}' does not exist for '{row.Feature}'.",
                        MatrixPath: relativeMatrixPath,
                        LineNumber: row.SourceLine,
                        Feature: row.Feature));
                }
            }

            if (IsSupportedStatus(row.Status) && !row.HasAutomatedEvidenceSignal && !row.HasLinkedEvidence)
            {
                errors.Add(new VtConformanceIssue(
                    Code: "supported-missing-evidence",
                    Severity: IssueSeverity.Error,
                    Message: $"Supported row '{row.Feature}' must declare automated evidence.",
                    MatrixPath: relativeMatrixPath,
                    LineNumber: row.SourceLine,
                    Feature: row.Feature));
            }

            if (IsSupportedStatus(row.Status) && !row.EvidenceLinks.Any())
            {
                warnings.Add(new VtConformanceIssue(
                    Code: "supported-evidence-not-linked",
                    Severity: IssueSeverity.Warning,
                    Message: $"Supported row '{row.Feature}' does not link to a concrete repo path yet.",
                    MatrixPath: relativeMatrixPath,
                    LineNumber: row.SourceLine,
                    Feature: row.Feature));
            }

            if (IsWontSupportStatus(row.Status) && string.IsNullOrWhiteSpace(row.KnownDeviations))
            {
                errors.Add(new VtConformanceIssue(
                    Code: "wont-support-missing-rationale",
                    Severity: IssueSeverity.Error,
                    Message: $"Won't-support row '{row.Feature}' must include a rationale.",
                    MatrixPath: relativeMatrixPath,
                    LineNumber: row.SourceLine,
                    Feature: row.Feature));
            }
        }
    }

    private static VtConformanceSummary BuildSummary(IReadOnlyList<VtConformanceRow> rows, int errorCount, int warningCount)
    {
        return new VtConformanceSummary(
            TotalRows: rows.Count,
            SupportedCount: rows.Count(row => row.Status.StartsWith("✅", StringComparison.Ordinal)),
            PartialCount: rows.Count(row => row.Status.StartsWith("⚠", StringComparison.Ordinal)),
            ExperimentalCount: rows.Count(row => row.Status.StartsWith("🧪", StringComparison.Ordinal)),
            NotSupportedCount: rows.Count(row => row.Status.StartsWith("❌", StringComparison.Ordinal)),
            WontSupportCount: rows.Count(row => row.Status.StartsWith("🚫", StringComparison.Ordinal)),
            RowsWithLinkedEvidence: rows.Count(row => row.EvidenceLinks.Any(link => link.Exists)),
            SupportedRowsWithLinkedEvidence: rows.Count(row => row.Status.StartsWith("✅", StringComparison.Ordinal) && row.EvidenceLinks.Any(link => link.Exists)),
            ErrorCount: errorCount,
            WarningCount: warningCount);
    }

    private static string[] SplitMarkdownRow(string row)
    {
        string trimmed = row.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Split('|')
            .Select(NormalizeCell)
            .ToArray();
    }

    private static bool LooksLikeFeatureTable(string[] headers)
        => headers.Any(header => header.Equals("Status", StringComparison.OrdinalIgnoreCase))
           && headers.Any(header => header.Equals("Evidence", StringComparison.OrdinalIgnoreCase));

    private static bool IsSeparatorRow(string row)
    {
        string candidate = row.Replace("|", string.Empty).Replace(":", string.Empty).Replace("-", string.Empty).Trim();
        return candidate.Length == 0;
    }

    private static int FindColumnIndex(string[] headers, string expectedHeader)
    {
        int index = FindOptionalColumnIndex(headers, expectedHeader);
        if (index < 0)
        {
            throw new InvalidOperationException($"Expected column '{expectedHeader}' was not found.");
        }

        return index;
    }

    private static int FindOptionalColumnIndex(string[] headers, string expectedHeader)
        => Array.FindIndex(headers, header => header.Equals(expectedHeader, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeCell(string value)
        => value.Replace("<br>", " ", StringComparison.OrdinalIgnoreCase).Trim();

    private static bool TryReadHeading(string line, out string? heading)
    {
        heading = null;
        if (!line.StartsWith('#'))
        {
            return false;
        }

        int markerCount = 0;
        while (markerCount < line.Length && line[markerCount] == '#')
        {
            markerCount++;
        }

        if (markerCount == 0 || markerCount == line.Length || line[markerCount] != ' ')
        {
            return false;
        }

        heading = line[(markerCount + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(heading);
    }

    private static IReadOnlyList<string> GetEvidenceKinds(string evidenceText)
    {
        if (string.IsNullOrWhiteSpace(evidenceText) || evidenceText == "—")
        {
            return Array.Empty<string>();
        }

        string normalized = evidenceText.ToLowerInvariant();
        var kinds = new List<string>();

        AddEvidenceKindIfPresent(kinds, normalized, "unit", "unit");
        AddEvidenceKindIfPresent(kinds, normalized, "replay", "replay");
        AddEvidenceKindIfPresent(kinds, normalized, "external", "external");
        AddEvidenceKindIfPresent(kinds, normalized, "vttest", "external");
        AddEvidenceKindIfPresent(kinds, normalized, "fuzz", "fuzz");
        AddEvidenceKindIfPresent(kinds, normalized, "manual", "manual");
        AddEvidenceKindIfPresent(kinds, normalized, "code path", "code-path");
        AddEvidenceKindIfPresent(kinds, normalized, "planned", "planned");

        return kinds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddEvidenceKindIfPresent(List<string> kinds, string normalizedEvidenceText, string probe, string kind)
    {
        if (normalizedEvidenceText.Contains(probe, StringComparison.Ordinal))
        {
            kinds.Add(kind);
        }
    }

    private static bool HasAutomatedEvidenceSignal(IReadOnlyList<string> kinds)
        => kinds.Contains("unit", StringComparer.Ordinal)
           || kinds.Contains("replay", StringComparer.Ordinal)
           || kinds.Contains("external", StringComparer.Ordinal)
           || kinds.Contains("fuzz", StringComparer.Ordinal);

    private static IReadOnlyList<VtEvidenceLink> ExtractEvidenceLinks(string repoRoot, string evidenceText)
    {
        if (string.IsNullOrWhiteSpace(evidenceText))
        {
            return Array.Empty<VtEvidenceLink>();
        }

        var links = new List<VtEvidenceLink>();
        foreach (Match match in InlineCodeRegex.Matches(evidenceText))
        {
            string candidate = match.Groups[1].Value.Trim();
            if (!LooksLikeRepositoryPath(candidate))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(Path.Combine(repoRoot, candidate.Replace('/', Path.DirectorySeparatorChar)));
            bool exists = File.Exists(fullPath) || Directory.Exists(fullPath);
            links.Add(new VtEvidenceLink(
                Path: candidate.Replace('\\', '/'),
                Exists: exists,
                FullPath: fullPath));
        }

        return links
            .DistinctBy(link => link.Path, StringComparer.Ordinal)
            .OrderBy(link => link.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool LooksLikeRepositoryPath(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (candidate.Contains("://", StringComparison.Ordinal)
            || candidate.Contains("...", StringComparison.Ordinal)
            || candidate.Contains('*', StringComparison.Ordinal)
            || candidate.Equals("—", StringComparison.Ordinal))
        {
            return false;
        }

        return candidate.Contains('/', StringComparison.Ordinal) || candidate.Contains('\\', StringComparison.Ordinal);
    }

    private static string ComputeSha256(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ToRepoRelativePath(string repoRoot, string absolutePath)
    {
        string relative = Path.GetRelativePath(repoRoot, absolutePath);
        return relative.Replace('\\', '/');
    }

    private static bool IsKnownStatus(string status)
        => IsSupportedStatus(status)
           || status.StartsWith("⚠", StringComparison.Ordinal)
           || status.StartsWith("🧪", StringComparison.Ordinal)
           || status.StartsWith("❌", StringComparison.Ordinal)
           || IsWontSupportStatus(status);

    private static bool IsSupportedStatus(string status)
        => status.StartsWith("✅", StringComparison.Ordinal);

    private static bool IsWontSupportStatus(string status)
        => status.StartsWith("🚫", StringComparison.Ordinal);
}

public static class VtConformanceCli
{
    public static Task<int> RunAsync(string[] args)
    {
        try
        {
            string repoRoot = Directory.GetCurrentDirectory();
            string matrixPath = Path.Combine("docs", "vt_coverage_matrix.md");
            string? reportPath = null;
            bool validate = false;

            for (int index = 0; index < args.Length; index++)
            {
                string arg = args[index];
                switch (arg)
                {
                    case "--repo-root":
                        repoRoot = RequireValue(args, ref index, arg);
                        break;
                    case "--matrix":
                        matrixPath = RequireValue(args, ref index, arg);
                        break;
                    case "--report":
                        reportPath = RequireValue(args, ref index, arg);
                        break;
                    case "--validate":
                        validate = true;
                        break;
                    case "--help":
                    case "-h":
                        PrintUsage();
                        return Task.FromResult(0);
                    default:
                        throw new InvalidOperationException($"Unknown argument '{arg}'.");
                }
            }

            VtConformanceReport report = VtConformanceReportTool.Generate(repoRoot, matrixPath);

            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                VtConformanceReportTool.WriteReport(report, reportPath);
                Console.WriteLine($"Wrote VT conformance report to '{Path.GetFullPath(reportPath)}'.");
            }
            else
            {
                Console.WriteLine(VtConformanceReportTool.Serialize(report));
            }

            Console.WriteLine($"Rows: {report.Summary.TotalRows}; errors: {report.Summary.ErrorCount}; warnings: {report.Summary.WarningCount}.");

            foreach (VtConformanceIssue issue in report.Errors)
            {
                Console.Error.WriteLine($"ERROR {issue.Code} (line {issue.LineNumber}): {issue.Message}");
            }

            foreach (VtConformanceIssue issue in report.Warnings)
            {
                Console.WriteLine($"WARN {issue.Code} (line {issue.LineNumber}): {issue.Message}");
            }

            return Task.FromResult(validate && report.Errors.Count > 0 ? 1 : 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return Task.FromResult(2);
        }
    }

    private static string RequireValue(string[] args, ref int index, string argumentName)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for '{argumentName}'.");
        }

        index++;
        return args[index];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project src/NovaTerminal.Conformance -- [--repo-root <path>] [--matrix <path>] [--report <path>] [--validate]");
    }
}

public sealed record VtConformanceReport(
    int SchemaVersion,
    string MatrixPath,
    string MatrixSha256,
    VtConformanceSummary Summary,
    IReadOnlyList<VtConformanceSection> Sections,
    IReadOnlyList<VtConformanceRow> Rows,
    IReadOnlyList<VtConformanceIssue> Errors,
    IReadOnlyList<VtConformanceIssue> Warnings);

public sealed record VtConformanceSummary(
    int TotalRows,
    int SupportedCount,
    int PartialCount,
    int ExperimentalCount,
    int NotSupportedCount,
    int WontSupportCount,
    int RowsWithLinkedEvidence,
    int SupportedRowsWithLinkedEvidence,
    int ErrorCount,
    int WarningCount);

public sealed record VtConformanceSection(
    int Order,
    string Title,
    int StartLine,
    int EndLine,
    int RowCount);

public sealed record VtConformanceRow(
    string Section,
    string Feature,
    string Status,
    string SpecOrNotes,
    string EvidenceText,
    IReadOnlyList<string> EvidenceKinds,
    IReadOnlyList<VtEvidenceLink> EvidenceLinks,
    bool HasAutomatedEvidenceSignal,
    bool HasLinkedEvidence,
    string Ownership,
    string KnownDeviations,
    int SourceLine);

public sealed record VtEvidenceLink(
    string Path,
    bool Exists,
    string FullPath);

public sealed record VtConformanceIssue(
    string Code,
    IssueSeverity Severity,
    string Message,
    string? MatrixPath,
    int LineNumber,
    string? Feature);

public enum IssueSeverity
{
    Warning = 1,
    Error = 2
}
