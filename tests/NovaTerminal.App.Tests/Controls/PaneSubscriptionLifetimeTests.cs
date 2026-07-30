using System;
using System.Reflection;
using Avalonia.Headless.XUnit;
using NovaTerminal.Controls;
using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Controls;

/// <summary>
/// Standing guards for #102, which reported that <c>TerminalPane</c>'s event subscriptions were
/// never removed and would accumulate on session-restart paths.
///
/// Audited against current <c>main</c>, the specific claims no longer hold — #154's
/// <c>DetachFromUiThread</c> split and the cached-delegate pairs in session setup closed them. But
/// "no longer holds" is only as good as the next edit, and the reasoning is non-obvious, so these
/// tests pin the properties the safety actually rests on:
///
/// <list type="number">
/// <item>The one subscription the pane makes on an app-lifetime singleton is removed on dispose, so
/// opening and closing tabs does not pile handlers onto a global.</item>
/// <item>Re-running session setup does not add a second copy of the handlers it puts on the pane's
/// own long-lived <c>TermView</c> — the thing <c>Reconnect()</c> would otherwise do on every
/// reconnect.</item>
/// <item>A disposed pane has no <c>TermView</c> handlers left at all.</item>
/// </list>
///
/// Everything else <c>InitializeSession</c> subscribes targets an object recreated with the session
/// — a fresh <c>AnsiParser</c>, the new <c>ITerminalSession</c> — so those lists start empty by
/// construction. That is the load-bearing assumption behind leaving 9 parser handlers unsubscribed,
/// and it is pinned separately in <c>PaneReconnectTests</c> because it needs a real shell.
/// </summary>
public class PaneSubscriptionLifetimeTests
{
    /// Length of a field-like event's invocation list, or 0 when nothing is subscribed.
    ///
    /// Reads the compiler-generated backing field. There is no public way to count subscribers, and
    /// counting is the whole point: asserting "the handler still works" would pass just as happily
    /// with ten copies attached, which is the failure this file exists to catch.
    private static int SubscriberCount(object target, string eventName)
    {
        Type type = target.GetType();
        FieldInfo? field = null;
        for (Type? t = type; t != null && field == null; t = t.BaseType)
        {
            field = t.GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
        }

        Assert.NotNull(field);
        var handler = field!.GetValue(target) as Delegate;
        return handler?.GetInvocationList().Length ?? 0;
    }

    [AvaloniaFact]
    public void DisposingPanes_LeavesNoHandlersOnTheSftpSingleton()
    {
        // SftpService.Instance is the only app-lifetime object the pane subscribes to, which makes it
        // the only subscription whose omission would accumulate across tab open/close — every other
        // target dies with the pane or with the session.
        int baseline = SubscriberCount(SftpService.Instance, nameof(SftpService.JobUpdated));

        for (int i = 0; i < 10; i++)
        {
            var pane = new TerminalPane();
            // Subscribed during construction, so this needs no session.
            Assert.Equal(baseline + 1, SubscriberCount(SftpService.Instance, nameof(SftpService.JobUpdated)));
            pane.Dispose();
            Assert.Equal(baseline, SubscriberCount(SftpService.Instance, nameof(SftpService.JobUpdated)));
        }
    }

    [AvaloniaFact]
    public void RewiringSessionHandlers_DoesNotAccumulateOnTheReusedTermView()
    {
        using var pane = new TerminalPane();

        // Construction attaches one MetricsChanged handler for layout; session setup adds one more of
        // those plus OnResize. Repeating the session wiring must not move either number - that is
        // what makes Reconnect() safe.
        for (int i = 0; i < 20; i++)
        {
            pane.WireReusedTermViewHandlers();
        }

        Assert.Equal(1, SubscriberCount(pane.TermView, "OnResize"));
        Assert.Equal(2, SubscriberCount(pane.TermView, "MetricsChanged"));
    }

    [AvaloniaFact]
    public void DisposingAPane_RemovesEveryTermViewHandler()
    {
        var pane = new TerminalPane();
        pane.WireReusedTermViewHandlers();
        Assert.Equal(1, SubscriberCount(pane.TermView, "OnResize"));
        Assert.Equal(2, SubscriberCount(pane.TermView, "MetricsChanged"));

        pane.Dispose();

        // TermView is the pane's own child, so a residual handler is not a memory leak - but
        // DetachFromUiThread's stated contract is that a disposed pane stops reacting, and one of
        // these two MetricsChanged handlers used to stay attached because it was an uncached lambda.
        Assert.Equal(0, SubscriberCount(pane.TermView, "OnResize"));
        Assert.Equal(0, SubscriberCount(pane.TermView, "MetricsChanged"));
    }
}
