using System;
using System.IO;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests;

/// <summary>
/// The Command Assist dependency graph pane-level tests inject into <c>TerminalPane</c>.
/// </summary>
/// <remarks>
/// <para>
/// Since Phase 0b a pane throws rather than reaching for a static locator, so every test that
/// enables Command Assist has to supply an instance. This one is shared for the whole test process
/// and rooted in a temp directory: shared because that matches production (one graph, one store,
/// one write gate) and keeps concurrent tests off each other's files, temp-rooted because
/// <see cref="CommandAssistServices.CreateDefault"/> would point at the developer's real
/// <c>%APPDATA%</c> history and migrate it to JSONL on first read.
/// </para>
/// </remarks>
internal static class TestCommandAssistServices
{
    private static readonly Lazy<CommandAssistServices> Lazy = new(Create);

    public static CommandAssistServices Instance => Lazy.Value;

    private static CommandAssistServices Create()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"nova_command_assist_tests_{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        return new CommandAssistServices(
            Path.Combine(directory, "history.jsonl"),
            legacyHistoryFilePath: null,
            Path.Combine(directory, "snippets.json"),
            () => directory);
    }
}
