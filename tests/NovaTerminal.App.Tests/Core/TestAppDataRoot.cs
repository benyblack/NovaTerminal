using System;
using System.IO;
using NovaTerminal.Shell;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Redirects <c>NOVATERM_APPDATA_ROOT</c> to a fresh, empty directory for as long as the scope
/// lives, so a test that builds a real <see cref="NovaTerminal.MainWindow"/> neither reads the
/// machine's persisted state nor writes into it.
/// </summary>
/// <remarks>
/// <para>
/// Both halves of that matter, and the write half is the one that surprised us. A
/// <c>MainWindow</c>'s <c>OnClosing</c> runs <c>PerformAppTeardown</c>, which calls
/// <c>SessionManager.SaveSession</c> — so every test that closes a window persists a session to
/// whatever root is current. Against the real root that means two things: it overwrites the
/// developer's own saved session when they run the suite locally, and it leaves a file behind that
/// steers the *startup path* of every window built later in the process.
/// </para>
/// <para>
/// That second effect is not theoretical; it is #434. <c>TestMainWindowFactory.Create()</c> calls
/// <c>AddTab</c> and registers a pane synchronously when no session exists, but takes
/// <c>TryRestoreStartupSession</c> when one does — and there only the selected tab is built
/// eagerly, the rest being placeholders that hold no pane until a Background-priority post runs.
/// <c>CaptureSession</c> persists every tab unconditionally with <c>Root = BuildPaneTree(...)</c>,
/// and that is null for a tab whose content is not a pane tree, so a saved session can select a
/// tab that restores to nothing. A window built from it registers no pane at all, which is what
/// reddened the gate on main - twice from one commit, on both OS lanes.
/// </para>
/// <para>
/// Setting the variable is not by itself enough to make the root empty.
/// <see cref="AppPaths.EnsureInitialized"/> migrates a legacy roaming <c>last_session.json</c> to
/// whatever <c>SessionFilePath</c> resolves to when it runs, once per process behind an
/// <c>_initialized</c> flag — so a scope that happens to be what first touches
/// <see cref="AppPaths"/> would have that session copied *into* the directory that is supposed to
/// have none. Forcing the initialization here pins it to a known point and lets the sweep below
/// undo it; the call is a no-op once anything else in the process has run it.
/// </para>
/// </remarks>
public sealed class TestAppDataRoot : IDisposable
{
    private const string EnvVar = "NOVATERM_APPDATA_ROOT";

    private readonly string? _previousRoot;

    public TestAppDataRoot()
    {
        RootPath = Path.Combine(Path.GetTempPath(), $"novaterm_test_root_{Guid.NewGuid():N}");
        _previousRoot = Environment.GetEnvironmentVariable(EnvVar);

        Directory.CreateDirectory(RootPath);
        Environment.SetEnvironmentVariable(EnvVar, RootPath);

        AppPaths.EnsureInitialized();

        // settings.json goes too, not just the session: a migrated one chooses the default profile
        // a startup tab is built from. A scope means defaults, from nothing.
        Sweep();
    }

    /// <summary>The scratch directory <see cref="AppPaths.RootDirectory"/> resolves to inside the scope.</summary>
    public string RootPath { get; }

    /// <summary>
    /// Restores the previous value rather than clearing the variable, so scopes can nest and an
    /// outer one survives an inner one being disposed.
    /// </summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, _previousRoot);
        try { Directory.Delete(RootPath, recursive: true); } catch { /* best effort */ }
    }

    private void Sweep()
    {
        try { Directory.Delete(Path.Combine(RootPath, "sessions"), recursive: true); } catch { /* best effort */ }
        try { File.Delete(Path.Combine(RootPath, "settings.json")); } catch { /* best effort */ }
    }
}
