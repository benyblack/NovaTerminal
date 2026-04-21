using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace NovaTerminal;

internal static class VtReportCli
{
    private const string ReportFlag = "--vt-report";
    private const string JsonFlag = "--json";
    private const string ResourceName = "NovaTerminal.Resources.vt-conformance-report.json";

    public static bool TryRun(string[] args, TextWriter stdout, TextWriter stderr, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        exitCode = 0;

        if (!Array.Exists(args, arg => string.Equals(arg, ReportFlag, StringComparison.Ordinal)))
        {
            return false;
        }

        try
        {
            bool jsonMode = ParseArguments(args, stderr, out exitCode);
            if (exitCode != 0)
            {
                return true;
            }

            string rawJson = LoadEmbeddedReportJson();
            if (jsonMode)
            {
                stdout.WriteLine(rawJson);
                return true;
            }

            EmbeddedVtReportSnapshot report = ParseSummary(rawJson);
            WriteSummary(report, stdout);
            return true;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Failed to load VT report: {ex.Message}");
            exitCode = 2;
            return true;
        }
    }

    private static bool ParseArguments(string[] args, TextWriter stderr, out int exitCode)
    {
        exitCode = 0;
        bool jsonMode = false;
        bool seenReportFlag = false;

        foreach (string arg in args)
        {
            switch (arg)
            {
                case ReportFlag when !seenReportFlag:
                    seenReportFlag = true;
                    break;
                case ReportFlag:
                    stderr.WriteLine("Duplicate argument '--vt-report'.");
                    PrintUsage(stderr);
                    exitCode = 2;
                    return false;
                case JsonFlag when !jsonMode:
                    jsonMode = true;
                    break;
                case JsonFlag:
                    stderr.WriteLine("Duplicate argument '--json'.");
                    PrintUsage(stderr);
                    exitCode = 2;
                    return false;
                default:
                    stderr.WriteLine($"Unknown argument '{arg}' for VT report mode.");
                    PrintUsage(stderr);
                    exitCode = 2;
                    return false;
            }
        }

        return jsonMode;
    }

    private static string LoadEmbeddedReportJson()
    {
        Assembly assembly = typeof(VtReportCli).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new FileNotFoundException($"Embedded VT report resource '{ResourceName}' was not found.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static EmbeddedVtReportSnapshot ParseSummary(string rawJson)
    {
        using JsonDocument document = JsonDocument.Parse(rawJson);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("matrixPath", out JsonElement matrixPathElement)
            || !root.TryGetProperty("summary", out JsonElement summary))
        {
            throw new InvalidDataException("Embedded VT report is invalid or incomplete.");
        }

        string? matrixPath = matrixPathElement.GetString();
        if (string.IsNullOrWhiteSpace(matrixPath))
        {
            throw new InvalidDataException("Embedded VT report is missing the matrix path.");
        }

        return new EmbeddedVtReportSnapshot(
            MatrixPath: matrixPath,
            TotalRows: ReadInt32(summary, "totalRows"),
            SupportedCount: ReadInt32(summary, "supportedCount"),
            PartialCount: ReadInt32(summary, "partialCount"),
            ExperimentalCount: ReadInt32(summary, "experimentalCount"),
            NotSupportedCount: ReadInt32(summary, "notSupportedCount"),
            WontSupportCount: ReadInt32(summary, "wontSupportCount"),
            RowsWithLinkedEvidence: ReadInt32(summary, "rowsWithLinkedEvidence"),
            SupportedRowsWithLinkedEvidence: ReadInt32(summary, "supportedRowsWithLinkedEvidence"),
            ErrorCount: ReadInt32(summary, "errorCount"),
            WarningCount: ReadInt32(summary, "warningCount"));
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException($"Embedded VT report is missing '{propertyName}'.");
        }

        return property.GetInt32();
    }

    private static void WriteSummary(EmbeddedVtReportSnapshot report, TextWriter stdout)
    {
        stdout.WriteLine("NovaTerminal VT Report");
        stdout.WriteLine($"Matrix: {report.MatrixPath}");
        stdout.WriteLine($"Total: {report.TotalRows}");
        stdout.WriteLine($"Supported: {report.SupportedCount}");
        stdout.WriteLine($"Partial: {report.PartialCount}");
        stdout.WriteLine($"Experimental: {report.ExperimentalCount}");
        stdout.WriteLine($"Not supported: {report.NotSupportedCount}");
        stdout.WriteLine($"Won't support: {report.WontSupportCount}");
        stdout.WriteLine($"Evidence: {report.RowsWithLinkedEvidence} rows linked, {report.SupportedRowsWithLinkedEvidence} supported rows linked");
        stdout.WriteLine($"Validation: {report.ErrorCount} errors, {report.WarningCount} warnings");
        stdout.WriteLine("Use --vt-report --json for the full report.");
    }

    private static void PrintUsage(TextWriter stderr)
    {
        stderr.WriteLine("Usage: NovaTerminal --vt-report [--json]");
    }

    private sealed record EmbeddedVtReportSnapshot(
        string MatrixPath,
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
}
