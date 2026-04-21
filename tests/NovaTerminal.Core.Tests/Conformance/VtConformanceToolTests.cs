using NovaTerminal.Conformance;

namespace NovaTerminal.Core.Tests.Conformance;

public sealed class VtConformanceToolTests
{
    [Fact]
    public void Generate_ParsesFeatureTablesAndProducesDeterministicJson()
    {
        using var repo = new TemporaryRepo();
        repo.WriteFile("tests/NovaTerminal.Tests/ParserTests.cs", "// parser tests");
        repo.WriteFile("tests/NovaTerminal.Tests/ReplayTests/CursorTests.cs", "// replay tests");
        repo.WriteFile("docs/vt_coverage_matrix.md", """
# VT Conformance Matrix

## 1) Parsing

| Feature / Sequence | Spec / Notes | Status | Evidence | Ownership (code) | Known deviations |
|---|---|---:|---|---|---|
| CSI parser | Basic | ✅ Supported | Unit: `tests/NovaTerminal.Tests/ParserTests.cs` | `Core/AnsiParser.cs` | |
| Cursor move | Movement | ⚠ Partial | Replay: `tests/NovaTerminal.Tests/ReplayTests/CursorTests.cs` | `Core/AnsiParser.cs` | Edge case |
""");

        VtConformanceReport report = VtConformanceReportTool.Generate(repo.RootPath, Path.Combine(repo.RootPath, "docs", "vt_coverage_matrix.md"));
        string json1 = VtConformanceReportTool.Serialize(report);
        string json2 = VtConformanceReportTool.Serialize(report);

        Assert.Equal(json1, json2);
        Assert.Equal(2, report.Summary.TotalRows);
        Assert.Equal(1, report.Summary.SupportedCount);
        Assert.Equal(1, report.Summary.PartialCount);
        Assert.Single(report.Sections);
        Assert.Equal("1) Parsing", report.Sections[0].Title);
        Assert.Equal("CSI parser", report.Rows[0].Feature);
        Assert.Equal("tests/NovaTerminal.Tests/ParserTests.cs", report.Rows[0].EvidenceLinks[0].Path);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void Generate_ReportsErrorWhenSupportedRowHasNoAutomatedEvidence()
    {
        using var repo = new TemporaryRepo();
        repo.WriteFile("docs/vt_coverage_matrix.md", """
# VT Conformance Matrix

## 1) Parsing

| Feature / Sequence | Spec / Notes | Status | Evidence | Ownership (code) | Known deviations |
|---|---|---:|---|---|---|
| CSI parser | Basic | ✅ Supported | Manual | `Core/AnsiParser.cs` | |
""");

        VtConformanceReport report = VtConformanceReportTool.Generate(repo.RootPath, Path.Combine(repo.RootPath, "docs", "vt_coverage_matrix.md"));

        Assert.Contains(report.Errors, issue => issue.Code == "supported-missing-evidence");
    }

    [Fact]
    public void Generate_WarnsWhenSupportedRowHasNoConcreteEvidenceLink()
    {
        using var repo = new TemporaryRepo();
        repo.WriteFile("docs/vt_coverage_matrix.md", """
# VT Conformance Matrix

## 1) Parsing

| Feature / Sequence | Spec / Notes | Status | Evidence | Ownership (code) | Known deviations |
|---|---|---:|---|---|---|
| CSI parser | Basic | ✅ Supported | Replay | `Core/AnsiParser.cs` | |
""");

        VtConformanceReport report = VtConformanceReportTool.Generate(repo.RootPath, Path.Combine(repo.RootPath, "docs", "vt_coverage_matrix.md"));

        Assert.Empty(report.Errors);
        Assert.Contains(report.Warnings, issue => issue.Code == "supported-evidence-not-linked");
    }

    [Fact]
    public void Generate_ReportsErrorWhenEvidencePathDoesNotExist()
    {
        using var repo = new TemporaryRepo();
        repo.WriteFile("docs/vt_coverage_matrix.md", """
# VT Conformance Matrix

## 1) Parsing

| Feature / Sequence | Spec / Notes | Status | Evidence | Ownership (code) | Known deviations |
|---|---|---:|---|---|---|
| Cursor move | Movement | ⚠ Partial | Unit: `tests/NovaTerminal.Tests/MissingTests.cs` | `Core/AnsiParser.cs` | Edge case |
""");

        VtConformanceReport report = VtConformanceReportTool.Generate(repo.RootPath, Path.Combine(repo.RootPath, "docs", "vt_coverage_matrix.md"));

        Assert.Contains(report.Errors, issue => issue.Code == "evidence-path-not-found");
    }

    [Fact]
    public void Generate_CurrentRepositoryMatrix_HasNoValidationErrors()
    {
        string repoRoot = FindRepositoryRoot();
        string matrixPath = Path.Combine(repoRoot, "docs", "vt_coverage_matrix.md");

        VtConformanceReport report = VtConformanceReportTool.Generate(repoRoot, matrixPath);

        Assert.Empty(report.Errors);
        Assert.NotEmpty(report.Rows);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "docs", "vt_coverage_matrix.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private sealed class TemporaryRepo : IDisposable
    {
        public TemporaryRepo()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"nova_conformance_{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public void WriteFile(string relativePath, string content)
        {
            string absolutePath = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string? directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(absolutePath, content.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
