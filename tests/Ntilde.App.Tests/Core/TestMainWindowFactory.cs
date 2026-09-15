using System;
using System.Collections.Generic;
using NovaTerminal.Platform;
using NovaTerminal.Shell;
using NovaTerminal.VT;

namespace NovaTerminal.Tests.Core;

internal static class TestMainWindowFactory
{
    private static readonly object Gate = new();
    private static readonly List<NovaTerminal.MainWindow> Created = new();

    public static NovaTerminal.MainWindow Create() => Create(AppServices.BuildForDesigner());

    /// <summary>
    /// For the tests that need their own service bundle. Same tracking, so their windows are torn
    /// down with everyone else's — a window built with a custom bundle still opens a real tab with
    /// a real shell behind it.
    /// </summary>
    public static NovaTerminal.MainWindow Create(AppServiceBundle services)
    {
        var window = new NovaTerminal.MainWindow(services);

        lock (Gate)
        {
            Created.Add(window);
        }

        return window;
    }

    /// <summary>
    /// Disposes the panes of every window this factory has handed out since the last call, which
    /// is what actually kills the PTY and its child shell. Call it from the test class's
    /// <see cref="IDisposable.Dispose"/>, so it runs after each test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The real <c>MainWindow</c> constructor opens a tab, and a tab is a real terminal pane with
    /// a real shell behind it — <c>cmd.exe</c> here, <c>/bin/bash</c> on CI. Nothing was reaping
    /// them: <c>Window.Close()</c> does not dispose the control tree, and no test closed its
    /// window anyway. A full local run finished with 53 live sessions — 212 threads and 53 child
    /// processes — and they stayed alive alongside every test that ran after them.
    /// </para>
    /// <para>
    /// The walk itself lives in <c>MainWindow</c> rather than here, because a correct one has to
    /// know that zoom stashes a tab's real root off the visual tree; a reimplementation from
    /// outside missed exactly that and left the zoom tests' panes behind.
    /// </para>
    /// </remarks>
    public static void DisposeCreatedWindows()
    {
        NovaTerminal.MainWindow[] windows;
        lock (Gate)
        {
            windows = Created.ToArray();
            Created.Clear();
        }

        foreach (NovaTerminal.MainWindow window in windows)
        {
            // Guarded on the window's own dispatcher, never Dispatcher.UIThread: reading that
            // static off the dispatch thread binds UI-thread identity to the caller, which is the
            // mechanism behind #81, and a teardown that runs at a test boundary is precisely where
            // that would bite. Not on the window's thread means skipping — which leaks, the status
            // quo this replaces, and better than trading a leak for a hang.
            if (!window.Dispatcher.CheckAccess())
            {
                continue;
            }

            try
            {
                window.DisposeAllPanesForTest();
            }
            catch (Exception)
            {
                // A window that refuses to tear down is not this helper's problem to report: it
                // would fail whichever unrelated test happens to be finishing.
            }
        }
    }
}
