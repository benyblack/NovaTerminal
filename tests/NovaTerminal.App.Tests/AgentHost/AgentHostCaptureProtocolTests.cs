using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using NovaTerminal.AgentHost;
using NovaTerminal.AgentHost.Contracts;
using NovaTerminal.Shell;
using NovaTerminal.Tests.Infra;
using NovaTerminal.VT;

namespace NovaTerminal.AppTests.AgentHost;

/// <summary>
/// Tests for the A5 <c>captureScreen</c> protocol surface: the observe endpoint
/// must be running, <see cref="AgentHostService.ScreenshotEnabled"/> must be on,
/// and the pane must have published render parameters. Renders go through the
/// production <see cref="TerminalSnapshotRenderer"/> — the same path the golden
/// PNG baselines pin down — with no window or visual tree involved, which is why
/// these run headless.
///
/// In the GoldenPng collection (#317) because that is where every other test that boots the
/// Avalonia headless platform lives, and the platform can only be booted once, by one thread.
/// Left as [Fact] deliberately: these tests do named-pipe I/O, and moving them onto the
/// framework's dispatcher thread with [AvaloniaFact] stalls any sweep that also runs the pane
/// tests - an intermittent failure traded for a worse hang. Serialising them against the other
/// Avalonia users is what removes the race; running them on that thread is not needed, since
/// they want the font manager rather than a visual tree.
/// </summary>
[Collection("GoldenPng")]
[Trait("Lane", "PlatformBoot")]
public class AgentHostCaptureProtocolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _exportDir;

    // A plausible 8x16 cell grid. Values only have to be self-consistent: the
    // renderer treats them as the geometry the live control measured.
    private static readonly CellMetrics Metrics = new()
    {
        CellWidth = 8f,
        CellHeight = 16f,
        Baseline = 12f,
        Ascent = 12f,
        Descent = 4f,
        Leading = 0f,
    };

    public AgentHostCaptureProtocolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nova-agentcapture-tests-" + Guid.NewGuid().ToString("N"));
        _exportDir = Path.Combine(_tempDir, "agent-exports");
        Directory.CreateDirectory(_tempDir);

        // The renderer resolves fonts through Avalonia's font manager.
        SnapshotService.EnsureAvaloniaInitialized();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AgentHostService NewService(AgentSessionRegistry registry, AgentActivityJournal? journal = null)
    {
        var endpoint = OperatingSystem.IsWindows()
            ? "novaterminal-agent-test-" + Guid.NewGuid().ToString("N")
            : Path.Combine(_tempDir, Guid.NewGuid().ToString("N")[..8] + ".sock");
        return new AgentHostService(registry, endpoint, _tempDir, _exportDir, journal);
    }

    /// <summary>Registers a pane that has been measured, so it is capturable.</summary>
    private static AgentSessionRegistration Register(
        AgentSessionRegistry registry,
        string? content = null,
        CellMetrics? metrics = null,
        bool publishRenderParameters = true)
    {
        var buffer = new TerminalBuffer(80, 24);
        if (content != null)
        {
            new AnsiParser(buffer).Process(content);
        }

        var registration = new AgentSessionRegistration(
            Guid.NewGuid(), buffer, "title", "Profile", "local", isActive: true);
        if (publishRenderParameters)
        {
            registration.UpdateRenderParameters(new PaneRenderParameters(
                metrics ?? Metrics,
                TerminalSnapshotOptions.DefaultTypefaceFamily,
                FontSize: 14f,
                EnableLigatures: false,
                EnableComplexShaping: true));
        }

        Assert.True(registry.Register(registration));
        return registration;
    }

    private static string CaptureRequestLine(Guid paneId, long id = 1, bool inline = false, int maxWidth = 0)
    {
        var paramsJson = JsonSerializer.Serialize(
            new CaptureScreenParams { PaneId = paneId, Inline = inline, MaxWidth = maxWidth },
            AgentHostJsonContext.Default.CaptureScreenParams);
        return $"{{\"v\":{AgentHostProtocol.Version},\"id\":{id},\"method\":\"{AgentHostProtocol.Methods.CaptureScreen}\",\"params\":{paramsJson}}}";
    }

    private static AgentHostResponse Handle(AgentHostService service, string line)
        => service.HandleRequestLineAsync(line, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    private static CaptureScreenResult Result(AgentHostResponse response)
    {
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        var result = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.CaptureScreenResult);
        Assert.NotNull(result);
        return result!;
    }

    // PNG signature: every capture must be a real PNG, not an empty or truncated file.
    private static readonly byte[] PngMagic = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void Capture_with_both_gates_on_writes_a_png_sized_to_the_grid()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "hello from the session\r\n");
        using var service = NewService(registry);
        service.ScreenshotEnabled = true;

        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId)));

        Assert.Equal(80, result.Cols);
        Assert.Equal(24, result.Rows);
        Assert.Equal(80 * 8, result.Width);
        Assert.Equal(24 * 16, result.Height);
        Assert.False(result.Downscaled);
        Assert.False(result.InlineOmitted);
        Assert.Null(result.PngBase64); // inline not requested

        Assert.True(File.Exists(result.FilePath));
        Assert.StartsWith(Path.GetFullPath(_exportDir), Path.GetFullPath(result.FilePath), StringComparison.Ordinal);
        Assert.StartsWith("nova_screen_", Path.GetFileName(result.FilePath), StringComparison.Ordinal);
        Assert.EndsWith(".png", result.FilePath, StringComparison.Ordinal);

        var bytes = File.ReadAllBytes(result.FilePath);
        Assert.Equal(result.ByteCount, bytes.Length);
        Assert.Equal(PngMagic, bytes.Take(PngMagic.Length));
    }

    [Fact]
    public void Two_captures_of_an_unchanged_buffer_are_byte_identical()
    {
        // The determinism bar for a screenshot: same buffer, same bytes. It holds
        // because the render is fixed at 1:1 (never the monitor's scaling), the
        // cursor follows the buffer's mode rather than a blink phase, and no
        // selection or HUD state leaks in.
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "deterministic [31mred[0m output\r\n");
        using var service = NewService(registry);
        service.ScreenshotEnabled = true;

        var first = Result(Handle(service, CaptureRequestLine(registration.PaneId, id: 1)));
        var second = Result(Handle(service, CaptureRequestLine(registration.PaneId, id: 2)));

        Assert.NotEqual(first.FilePath, second.FilePath); // no silent overwrite
        Assert.Equal(File.ReadAllBytes(first.FilePath), File.ReadAllBytes(second.FilePath));
    }

    [Fact]
    public void Inline_capture_returns_the_png_as_base64()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "inline me");
        using var service = NewService(registry);
        service.ScreenshotEnabled = true;

        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId, inline: true)));

        Assert.NotNull(result.PngBase64);
        Assert.False(result.InlineOmitted);
        var decoded = Convert.FromBase64String(result.PngBase64!);
        Assert.Equal(File.ReadAllBytes(result.FilePath), decoded);
    }

    [Fact]
    public void MaxWidth_downscales_the_image_and_says_so()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "shrink me");
        using var service = NewService(registry);
        service.ScreenshotEnabled = true;

        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId, maxWidth: 160)));

        Assert.True(result.Downscaled);
        Assert.Equal(160, result.Width);
        // 640x384 scaled to width 160 keeps the aspect ratio.
        Assert.Equal(96, result.Height);
        Assert.Equal(80, result.Cols); // the grid is unchanged; only the image shrank
    }

    [Fact]
    public void MaxWidth_larger_than_the_image_is_a_no_op()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        using var service = NewService(registry);
        service.ScreenshotEnabled = true;

        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId, maxWidth: 10_000)));

        Assert.False(result.Downscaled);
        Assert.Equal(80 * 8, result.Width);
    }

    [Fact]
    public void Capture_without_the_screenshot_setting_fails_with_captureDisabled_and_writes_nothing()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "secret output");
        using var service = NewService(registry);
        service.ScreenshotEnabled = false; // observe on, screenshot sub-gate off

        var response = Handle(service, CaptureRequestLine(registration.PaneId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.CaptureDisabled, response.Error?.Code);
        Assert.False(Directory.Exists(_exportDir) && Directory.EnumerateFiles(_exportDir).Any());
    }

    [Fact]
    public void Capture_for_unknown_pane_reports_session_not_found()
    {
        using var service = NewService(new AgentSessionRegistry());
        service.ScreenshotEnabled = true;

        var response = Handle(service, CaptureRequestLine(Guid.NewGuid()));

        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, response.Error?.Code);
    }

    [Fact]
    public void Capture_without_params_is_a_malformed_request_and_is_still_journaled()
    {
        // A malformed attempt is still an externally reachable attempt on the
        // user's screen, so it belongs in the journal like the acting methods'.
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.ScreenshotEnabled = true;

        var line = $"{{\"v\":{AgentHostProtocol.Version},\"id\":7,\"method\":\"{AgentHostProtocol.Methods.CaptureScreen}\",\"params\":null}}";
        var response = Handle(service, line);

        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        var entry = Assert.Single(journal.Snapshot());
        Assert.Equal(AgentHostProtocol.Methods.CaptureScreen, entry.Method);
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, entry.Outcome);
        Assert.Null(entry.PaneId);
    }

    [Fact]
    public void Capture_with_an_unparseable_paneId_is_malformed_and_journaled()
    {
        // `required Guid PaneId` throws JsonException rather than yielding null,
        // which used to escape to the outer handler and skip the journal.
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.ScreenshotEnabled = true;

        var line = $"{{\"v\":{AgentHostProtocol.Version},\"id\":8,\"method\":\"{AgentHostProtocol.Methods.CaptureScreen}\",\"params\":{{\"paneId\":\"not-a-guid\"}}}}";
        var response = Handle(service, line);

        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        var entry = Assert.Single(journal.Snapshot());
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, entry.Outcome);
    }

    [Fact]
    public void Capture_before_the_pane_is_measured_reports_captureUnavailable()
    {
        // Registration happens in the pane constructor, before layout has measured
        // the font: there is no geometry to render into yet.
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, publishRenderParameters: false);
        using var service = NewService(registry);
        service.ScreenshotEnabled = true;

        var response = Handle(service, CaptureRequestLine(registration.PaneId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.CaptureUnavailable, response.Error?.Code);
    }

    [Fact]
    public void Capture_of_a_grid_over_the_pixel_budget_reports_captureUnavailable()
    {
        var registry = new AgentSessionRegistry();
        var huge = new CellMetrics { CellWidth = 5000f, CellHeight = 5000f, Baseline = 4000f, Ascent = 4000f, Descent = 1000f, Leading = 0f };
        var registration = Register(registry, metrics: huge);
        using var service = NewService(registry);
        service.ScreenshotEnabled = true;

        var response = Handle(service, CaptureRequestLine(registration.PaneId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.CaptureUnavailable, response.Error?.Code);
        Assert.Contains("pixel per-capture budget", response.Error!.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_exportDir) && Directory.EnumerateFiles(_exportDir).Any());
    }

    [Fact]
    public void Every_capture_attempt_is_journaled_allowed_or_denied()
    {
        var journal = new AgentActivityJournal();
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "journal me");
        using var service = NewService(registry, journal);

        service.ScreenshotEnabled = false;
        Handle(service, CaptureRequestLine(registration.PaneId, id: 1));
        service.ScreenshotEnabled = true;
        Handle(service, CaptureRequestLine(registration.PaneId, id: 2));

        var entries = journal.Snapshot(); // newest first
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(AgentHostProtocol.Methods.CaptureScreen, e.Method));
        Assert.All(entries, e => Assert.Equal(registration.PaneId, e.PaneId));
        Assert.Equal("ok", entries[0].Outcome);
        Assert.Equal(AgentHostProtocol.ErrorCodes.CaptureDisabled, entries[1].Outcome);
    }

    [Fact]
    public void Capture_file_names_are_unique_within_the_same_second()
    {
        var timestamp = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Local);
        var first = AgentHostService.BuildCaptureFileName(timestamp, Guid.NewGuid().ToString("N"));
        var second = AgentHostService.BuildCaptureFileName(timestamp, Guid.NewGuid().ToString("N"));

        Assert.NotEqual(first, second);
        Assert.StartsWith("nova_screen_20260731_120000_", first, StringComparison.Ordinal);
        Assert.EndsWith(".png", first, StringComparison.Ordinal);
    }
}
