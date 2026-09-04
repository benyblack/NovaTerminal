using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using NovaTerminal.Update;

namespace NovaTerminal.UI.About;

/// <summary>
/// The "+" flyout's About dialog: identity (icon, name, version) plus the one question a user
/// usually opens it to ask - "is there an update?". The window only renders; the check itself
/// runs on MainWindow's shared pipeline (same coordinator, in-flight guard and announce-once
/// state as the palette's manual check), injected as delegates by <c>ShowAboutWindowAsync</c>
/// - property injection, the same way SettingsWindow is wired.
/// </summary>
public partial class AboutWindow : Window, IUpdateCheckFeedback
{
    private const string RepoUrl = "https://github.com/benyblack/NovaTerminal";
    private const string ReleasesUrl = RepoUrl + "/releases";

    public AboutWindow()
    {
        InitializeComponent();

        try
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://NovaTerminal/Assets/nova_icon.ico")));
        }
        catch (Exception)
        {
            // A missing icon must not take the About window down; the title bar still reads fine.
        }

        var versionText = this.FindControl<TextBlock>("VersionText");
        if (versionText != null)
        {
            versionText.Text = "Version " + ResolveAppVersion();
        }

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    /// <summary>Runs one interactive check on MainWindow's pipeline, reporting back via this window.</summary>
    public Func<Task>? RunUpdateCheck { get; set; }

    /// <summary>MainWindow's ApplyStagedUpdate, teardown included.</summary>
    public Action? ApplyStagedUpdate { get; set; }

    /// <summary>Reads the coordinator's staged version, so the window shows an already-staged update on open.</summary>
    public Func<string?>? StagedVersionProvider { get; set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // An update staged before this window opened (the user dismissed the main-window toast)
        // should answer the window's central question without making them press the button and
        // wait for a re-check that would only re-find what is already on disk.
        if (StagedVersionProvider?.Invoke() is { } staged)
        {
            Outcome(UpdateCheckOutcome.UpdateReady, staged);
        }
    }

    public void Checking()
    {
        var button = this.FindControl<Button>("CheckForUpdatesButton");
        var progress = this.FindControl<ProgressBar>("CheckProgress");
        var status = this.FindControl<TextBlock>("StatusText");
        if (button != null) button.IsEnabled = false;
        if (progress != null) progress.IsVisible = true;
        if (status != null)
        {
            status.IsVisible = true;
            status.Text = "Checking for updates…";
        }
    }

    public void AlreadyRunning()
    {
        ShowStatus(UpdateCheckMessages.AlreadyRunningMessage);
    }

    public void CoordinatorUnavailable()
    {
        ShowStatus(UpdateCheckMessages.CoordinatorUnavailableMessage);
    }

    public void Outcome(UpdateCheckOutcome outcome, string? stagedVersion)
    {
        ShowStatus(UpdateCheckMessages.OutcomeMessage(outcome, stagedVersion));

        var restart = this.FindControl<Button>("RestartNowButton");
        var releases = this.FindControl<Button>("OpenReleasesButton");

        // Failed-while-staged must not hide the restart affordance: the staged update is still
        // on disk even though the re-check could not reach the feed.
        var stillStaged = outcome != UpdateCheckOutcome.UpdateReady && StagedVersionProvider?.Invoke() != null;
        if (restart != null)
        {
            restart.IsVisible = UpdateCheckMessages.OutcomeOffersRestart(outcome) || stillStaged;
        }

        if (releases != null)
        {
            releases.IsVisible = outcome == UpdateCheckOutcome.Unsupported;
        }
    }

    /// <summary>
    /// Shows a terminal answer and ends the check state. Every path that ends a check - a real
    /// outcome, the already-running answer, or the broken-coordinator answer - comes through
    /// here, because the pipeline returns early on the latter two without calling Outcome; each
    /// of them must re-enable the button, or one early answer leaves the window looking stuck
    /// mid-check forever.
    /// </summary>
    private void ShowStatus(string message)
    {
        var button = this.FindControl<Button>("CheckForUpdatesButton");
        var progress = this.FindControl<ProgressBar>("CheckProgress");
        var status = this.FindControl<TextBlock>("StatusText");
        if (button != null) button.IsEnabled = true;
        if (progress != null) progress.IsVisible = false;
        if (status != null)
        {
            status.IsVisible = true;
            status.Text = message;
        }
    }

    private async void OnCheckForUpdatesClick(object? sender, RoutedEventArgs e)
    {
        var run = RunUpdateCheck;
        if (run == null)
        {
            return;
        }

        Checking();
        try
        {
            await run();
        }
        catch (Exception ex)
        {
            // The pipeline reports every ordinary outcome through IUpdateCheckFeedback; this only
            // catches a throw from the plumbing itself, so the window never looks hung.
            NovaTerminal.VT.TerminalLogger.Log("About-window update check failed unexpectedly: " + ex);
            Outcome(UpdateCheckOutcome.Failed, null);
        }
    }

    private void OnRestartNowClick(object? sender, RoutedEventArgs e)
    {
        // MainWindow's ApplyStagedUpdate runs the full app teardown before handing off to
        // Velopack and owns its own failure reporting; if it returns, the restart did not
        // happen and this window is still usable.
        ApplyStagedUpdate?.Invoke();
    }

    private void OnOpenRepoClick(object? sender, RoutedEventArgs e)
    {
        _ = OpenExternalUrlAsync(RepoUrl);
    }

    private void OnOpenReleasesClick(object? sender, RoutedEventArgs e)
    {
        _ = OpenExternalUrlAsync(ReleasesUrl);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task OpenExternalUrlAsync(string url)
    {
        // Same boundary as AgentOutputPanel's link handling: only web URLs may reach the OS
        // shell handler, and the launcher fails soft when no TopLevel is attached.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
        {
            await topLevel.Launcher.LaunchUriAsync(uri);
        }
    }

    /// <summary>The same attribute-based resolution BackupService uses; the informational version is what the build pins.</summary>
    private static string ResolveAppVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
