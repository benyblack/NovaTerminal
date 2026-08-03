using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NovaTerminal.CommandAssist.Models;
using NovaTerminal.CommandAssist.ViewModels;
using NovaTerminal.CommandAssist.Views;
using NovaTerminal.Controls;
using NovaTerminal.Shell;
using SkiaSharp;

namespace NovaTerminal.Tests.CommandAssist;

/// <summary>
/// Guards that the Command Assist overlay actually puts content on screen.
/// </summary>
/// <remarks>
/// <para>
/// Written after the post-V2-Phase-3a regression where the bubble and popup rendered as empty dark
/// rounded rectangles: correct chrome, correct size, not one character of text. Nothing in the
/// existing suite noticed, because every other assist test either checks view-model state or reaches
/// a named <c>TextBlock</c> through <c>FindControl</c> - and both of those are perfectly healthy on a
/// surface whose <c>DataContext</c> is null. The views use <c>x:CompileBindings</c>, where a null data
/// root is a silent no-value rather than a logged binding error, so the failure had no signature at
/// all above the pixels.
/// </para>
/// <para>
/// So these tests assert at the two levels the old ones skipped: that initialization survives arriving
/// off the UI thread (the mechanism), and that the rendered raster is not uniform background in the
/// regions that are supposed to carry text (the symptom).
/// </para>
/// </remarks>
public sealed class CommandAssistOverlayContentRenderTests
{
    /// <summary>
    /// Minimum fraction of a text region's pixels that must differ from its background colour.
    /// </summary>
    /// <remarks>
    /// Glyph strokes are thin, so the honest bar is low: a line of antialiased 11-14pt text covers a
    /// few percent of its own bounding box and far less of a region sized to hold several rows. The
    /// value only has to separate "some ink" from "none", which is the whole distinction the
    /// regression turned on - see <see cref="PixelGuard_ReportsBlank_WhenPopupHasNoDataContext"/>,
    /// which pins the other side of it.
    /// </remarks>
    private const double MinimumInkFraction = 0.002;

    /// <summary>
    /// Per-channel-sum distance at which a pixel counts as ink rather than background.
    /// </summary>
    /// <remarks>
    /// Deliberately generous. The point is to be indifferent to themes: any foreground the product
    /// might legitimately choose sits far further than this from the card fill it is drawn on, while
    /// antialiasing fringes and the card's own subtle inner panels do not.
    /// </remarks>
    private const int InkChannelDistance = 24;

    /// <summary>
    /// The mechanism guard: Command Assist initialization must survive arriving off the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test that would have caught the regression. Command Assist is constructed lazily on
    /// first use, and in a real session first use is not a keystroke: <c>OSC 133;B</c> from the shell
    /// arrives on the PTY reader thread and reaches <c>EnsureCommandAssistInitialized</c> through the
    /// serialized shell-integration dispatcher. <c>BindCommandAssistViews</c> then wrote
    /// <c>DataContext</c> from there, which Avalonia refuses across threads; the throw was swallowed by
    /// a fire-and-forget dispatch, and because the pane had already recorded the view-model it went on
    /// believing it was bound.
    /// </para>
    /// <para>
    /// Every pre-existing test drove the parser directly from the test thread, where the dispatcher's
    /// semaphore is uncontended and the await therefore completes synchronously - so the bind never
    /// left the UI thread and the bug was invisible. The <c>Task.Run</c> below is the entire difference,
    /// and it is not artificial: it is what the PTY reader does.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public async Task TerminalPane_WhenShellIntegrationArrivesOffTheUiThread_StillBindsOverlayViewModels()
    {
        using var pane = new TerminalPane();
        ConfigureCommandAssist(pane);
        pane.ArmShellIntegrationTracker();
        pane.CreateAndWireParser();

        await Task.Run(() =>
        {
            Assert.False(
                Dispatcher.UIThread.CheckAccess(),
                "This test is meaningless unless the shell-integration burst really is parsed off the UI thread.");
            pane.Parser!.Process("\x1b]133;A\x07PS C:\\> \x1b]133;B\x07git status");
        });

        CommandAssistBubbleView bubbleView =
            Assert.IsType<CommandAssistBubbleView>(pane.FindControl<CommandAssistBubbleView>("CommandAssistBubble"));
        CommandAssistPopupView popupView =
            Assert.IsType<CommandAssistPopupView>(pane.FindControl<CommandAssistPopupView>("CommandAssistPopup"));

        await WaitForAsync(
            () => bubbleView.DataContext is CommandAssistBubbleViewModel &&
                  popupView.DataContext is CommandAssistPopupViewModel,
            "the overlay views to receive their view-models after an off-UI-thread initialization");

        Assert.IsType<CommandAssistBubbleViewModel>(bubbleView.DataContext);
        Assert.IsType<CommandAssistPopupViewModel>(popupView.DataContext);
    }

    /// <summary>
    /// The other thing an off-UI-thread initialization can break silently: the rendered-surface probe
    /// must be answering "yes" by the time the user's <c>Enter</c> arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CommandAssistController.IsAcceptOnEnterArmed</c> folds in a probe the pane installs during
    /// <c>InitializeCommandAssist</c> - <c>CommandAssistOverlayHost.IsVisible &amp;&amp; Opacity &gt; 0</c> -
    /// and the overlay host's visibility is written by a placement pass that only runs when a view-model
    /// property changes. Initialization now marshals to the UI thread, so the order of "probe installed",
    /// "views bound" and "first placement pass" is decided by the dispatcher rather than by the calling
    /// thread; a probe that ended up stale-false would leave <c>Enter</c> unarmed on a surface the user
    /// can plainly see, and the only symptom would be an <c>Enter</c> that submits.
    /// </para>
    /// <para>
    /// So this drives the real order: shell integration off the PTY thread, then <c>Ctrl+R</c>, then
    /// browse - and asserts the probe agrees with the screen. It is the guard for the hypothesis the
    /// second live bug was first blamed on; the bug itself turned out to be in the grid read (see
    /// <c>PaneAssistInsertionTests.OnAPromptPsReadLineHasRendered_CtrlEnterStillSendsTheSuffix</c>), and
    /// this pins that the probe is not a second, latent copy of the same failure.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public async Task TerminalPane_WhenShellIntegrationArrivesOffTheUiThread_ArmsEnterOnceTheOverlayIsShown()
    {
        // Ctrl+R needs something to list before a row can be selected, and a selected row is what
        // arms Enter. Awaited rather than blocked on: this runs on the headless dispatcher thread.
        await TestCommandAssistServices.Instance.HistoryStore.AppendAsync(new CommandHistoryEntry(
            Id: Guid.NewGuid().ToString("N"),
            CommandText: "git status",
            ExecutedAt: DateTimeOffset.UtcNow,
            ShellKind: "pwsh",
            WorkingDirectory: null,
            ProfileId: null,
            SessionId: null,
            HostId: null,
            ExitCode: 0,
            IsRemote: false,
            IsRedacted: false,
            Source: CommandCaptureSource.Heuristic,
            DurationMs: null));

        using var pane = new TerminalPane
        {
            Width = 900,
            Height = 500
        };
        ConfigureCommandAssist(pane);
        pane.Measure(new Size(900, 500));
        pane.Arrange(new Rect(0, 0, 900, 500));
        pane.ArmShellIntegrationTracker();
        pane.CreateAndWireParser();

        await Task.Run(() =>
        {
            Assert.False(
                Dispatcher.UIThread.CheckAccess(),
                "This test is meaningless unless initialization really is triggered off the UI thread.");
            pane.Parser!.Process("\x1b]133;A\x07PS C:\\> \x1b]133;B\x07");
        });

        await WaitForAsync(
            () => pane.CommandAssistViewModel is CommandAssistBarViewModel,
            "Command Assist to initialize from the off-thread shell-integration burst");

        var viewModel = Assert.IsType<CommandAssistBarViewModel>(pane.CommandAssistViewModel);

        // Ctrl+R: a surface the user asked for, with the row list open.
        pane.OpenCommandAssistHistorySearch();
        await WaitForAsync(() => viewModel.HasSuggestions, "the history list to fill");

        Assert.True(
            pane.IsCommandAssistOverlayRendered,
            "the pane's own probe must say the overlay it just placed is on screen");
        Assert.True(viewModel.IsVisible);
        Assert.True(viewModel.IsPopupOpen);
        Assert.True(
            viewModel.IsAcceptOnEnterArmed,
            "Enter must be armed on a visible popup with a row selected; a stale probe would leave it " +
            "unarmed and the user's Enter would submit instead of inserting.");
    }

    /// <summary>
    /// The symptom guard for the popup: rendered rows must contain ink.
    /// </summary>
    [AvaloniaFact]
    public void CommandAssistPopupView_RendersInkWhereTheSuggestionRowsAre()
    {
        CommandAssistPopupViewModel vm = CreatePopupViewModel();
        var view = new CommandAssistPopupView
        {
            DataContext = vm,
            Width = 520,
            Height = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        WithRenderedView(view, 520, 220, (bitmap, rendered) =>
        {
            AssertRegionHasInk(bitmap, rendered, "PopupSuggestionsList", "the suggestion row list");
            AssertRegionHasInk(bitmap, rendered, "PopupModeLabelText", "the popup mode label");
            AssertRegionHasInk(bitmap, rendered, "PopupShortcutHintText", "the popup hint strip");
        });
    }

    /// <summary>
    /// The symptom guard for the bubble: the mode label and hint strip must contain ink.
    /// </summary>
    [AvaloniaFact]
    public void CommandAssistBubbleView_RendersInkWhereTheModeLabelIs()
    {
        var vm = new CommandAssistBubbleViewModel
        {
            IsVisible = true,
            ModeLabel = "History",
            QueryText = "git st",
            SummaryText = "git status --short",
            ShortcutHintText = CommandAssistBarViewModel.BrowseHintText,
            ShowQueryText = true,
            ShowShortcutHint = true
        };
        var view = new CommandAssistBubbleView
        {
            DataContext = vm,
            Width = 420,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        WithRenderedView(view, 420, 36, (bitmap, rendered) =>
        {
            AssertRegionHasInk(bitmap, rendered, "BubbleModeLabelText", "the bubble mode label");
            AssertRegionHasInk(bitmap, rendered, "BubbleSummaryText", "the bubble suggestion summary");
            AssertRegionHasInk(bitmap, rendered, "BubbleShortcutHintText", "the bubble hint strip");
        });
    }

    /// <summary>
    /// Proves the ink measurement can actually fail, by reproducing what the owner saw.
    /// </summary>
    /// <remarks>
    /// A popup with no <c>DataContext</c> is exactly the regression: the card, the border and the inner
    /// panels all paint, the control reports itself visible (the root's <c>IsVisible</c> binding has no
    /// data root to read, so it keeps its default), and every <c>TextBlock</c> resolves to nothing.
    /// Without this test the two guards above would be unfalsifiable - a metric that never fails
    /// certifies nothing. With it, "not blank" is a claim the suite can lose.
    /// </remarks>
    [AvaloniaFact]
    public void PixelGuard_ReportsBlank_WhenPopupHasNoDataContext()
    {
        var view = new CommandAssistPopupView
        {
            DataContext = null,
            Width = 520,
            Height = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        WithRenderedView(view, 520, 220, (bitmap, rendered) =>
        {
            // The same two regions the positive guard measures, measured the same way. Deliberately not
            // the whole card: the border and the rounded corners are ink by any definition, and a
            // negative control that has to exclude them is not measuring what the positive one measures.
            // The mode label is left out because with no data context it has no text and therefore no
            // width at all - a different, cruder symptom that RegionOf rejects outright.
            AssertRegionIsBlank(bitmap, rendered, "PopupSuggestionsList", "the suggestion row list");
            AssertRegionIsBlank(bitmap, rendered, "PopupShortcutHintText", "the popup hint strip");
        });
    }

    private static CommandAssistPopupViewModel CreatePopupViewModel()
    {
        var suggestions = new ObservableCollection<CommandAssistSuggestionItemViewModel>
        {
            new(
                displayText: "git status --short",
                descriptionText: "Show the working tree status.",
                badgesText: "History",
                metadataText: @"C:\repo | used today",
                isSelected: true,
                type: AssistSuggestionType.History),
            new(
                displayText: "git commit --amend",
                descriptionText: "Rewrite the most recent commit.",
                badgesText: "History",
                metadataText: @"C:\repo | used yesterday",
                isSelected: false,
                type: AssistSuggestionType.History),
            new(
                displayText: "git push --force-with-lease",
                descriptionText: "Update the remote branch safely.",
                badgesText: "History",
                metadataText: @"C:\repo | used last week",
                isSelected: false,
                type: AssistSuggestionType.History)
        };

        return new CommandAssistPopupViewModel(suggestions)
        {
            IsVisible = true,
            ModeLabel = "History",
            QueryText = "git",
            TopSuggestionText = "git status --short",
            SelectedDescriptionText = "Show the working tree status.",
            SelectedBadgesText = "History",
            SelectedMetadataText = @"C:\repo | used today",
            HasSuggestions = true,
            ShowEmptyState = false,
            ShortcutHintText = CommandAssistBarViewModel.BrowseHintText,
            UseCompactLayout = false
        };
    }

    /// <summary>
    /// Shows <paramref name="view"/> in a window, lays it out, rasterizes it, and hands both to
    /// <paramref name="assert"/>.
    /// </summary>
    /// <remarks>
    /// Hosted in a <see cref="Window"/> rather than measured detached for the same reason the layout
    /// tests are: the content lives behind a <c>ContentPresenter</c> that only realizes its child
    /// during a layout pass from a visual root, and an <c>ItemsControl</c>'s row containers do not exist
    /// at all until then. A detached pass would leave every descendant at zero bounds, and a pixel test
    /// over a zero-sized region is a test that cannot fail.
    /// </remarks>
    private static void WithRenderedView(Control view, int width, int height, Action<SKBitmap, Control> assert)
    {
        var window = new Window
        {
            Content = view,
            Width = width + 40,
            Height = height + 40
        };

        try
        {
            window.Show();
            RelayoutTo(window, view, new Size(width + 40, height + 40));
            Dispatcher.UIThread.RunJobs();

            Assert.True(
                view.Bounds.Width > 0 && view.Bounds.Height > 0,
                $"The view under test arranged to {view.Bounds}, so there is nothing to rasterize.");

            using SKBitmap bitmap = Rasterize(view);
            assert(bitmap, view);
        }
        finally
        {
            window.Close();
        }
    }

    private static SKBitmap Rasterize(Control view)
    {
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(view.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(view.Bounds.Height)));

        using var target = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        target.Render(view);

        using var stream = new MemoryStream();
        target.Save(stream);
        stream.Position = 0;

        return SKBitmap.Decode(stream)
               ?? throw new InvalidOperationException(
                   "Could not decode the rendered overlay. If this started failing, check that the " +
                   "headless test app still renders for real (AvaloniaHeadlessPlatformOptions." +
                   "UseHeadlessDrawing must be false) - the stub drawing backend produces an empty " +
                   "raster, which would make every pixel guard in this file vacuous.");
    }

    private static void AssertRegionHasInk(SKBitmap bitmap, Control root, string childName, string description)
    {
        PixelRect region = RegionOf(bitmap, root, childName);
        double ink = MeasureInkFraction(bitmap, region);

        Assert.True(
            ink >= MinimumInkFraction,
            $"{description} rendered as uniform background: only {ink:P3} of the {region.Width}x{region.Height} " +
            $"region around '{childName}' differs from its most common colour. This is the shape of the " +
            "post-Phase-3a regression - chrome and layout intact, no text - so suspect the surface's " +
            "DataContext or a binding before suspecting the threshold.");
    }

    private static void AssertRegionIsBlank(SKBitmap bitmap, Control root, string childName, string description)
    {
        PixelRect region = RegionOf(bitmap, root, childName);
        double ink = MeasureInkFraction(bitmap, region);

        Assert.True(
            ink < MinimumInkFraction,
            $"{description} was expected to render as uniform background on a popup with no DataContext, " +
            $"but {ink:P3} of the {region.Width}x{region.Height} region around '{childName}' differs from " +
            "its most common colour. If the popup grew a design-time fallback, re-point this control at " +
            "something still genuinely blank rather than deleting it: the positive guards in this file " +
            "mean nothing without a demonstration that the measurement can fail.");
    }

    /// <summary>
    /// The bounds of a named descendant, in the rasterized bitmap's pixel space.
    /// </summary>
    private static PixelRect RegionOf(SKBitmap bitmap, Control root, string childName)
    {
        Control child = Assert.IsAssignableFrom<Control>(root.FindControl<Control>(childName));
        Assert.True(
            child.Bounds.Width > 0 && child.Bounds.Height > 0,
            $"'{childName}' arranged to {child.Bounds}. A zero-sized region would make the ink check vacuous.");

        Point? offset = child.TranslatePoint(new Point(0, 0), root);
        Assert.NotNull(offset);

        // Grown by a pixel on every side: TranslatePoint works in device-independent units and the
        // raster is snapped, so a region clipped exactly to the reported bounds can shave the outermost
        // row of glyph pixels off a single-line TextBlock.
        int left = Math.Max(0, (int)Math.Floor(offset!.Value.X) - 1);
        int top = Math.Max(0, (int)Math.Floor(offset.Value.Y) - 1);
        int right = Math.Min(bitmap.Width, (int)Math.Ceiling(offset.Value.X + child.Bounds.Width) + 1);
        int bottom = Math.Min(bitmap.Height, (int)Math.Ceiling(offset.Value.Y + child.Bounds.Height) + 1);

        Assert.True(
            right > left && bottom > top,
            $"'{childName}' translated to an empty region ({left},{top})-({right},{bottom}) inside a " +
            $"{bitmap.Width}x{bitmap.Height} raster.");

        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// The fraction of pixels in <paramref name="region"/> that are not the region's background.
    /// </summary>
    /// <remarks>
    /// Background is taken as the most common colour rather than a hard-coded one, which is what keeps
    /// this indifferent to themes and to the popup's own layered panel fills. Anything far enough from
    /// that colour is ink - text, the selection highlight, a border - and the guards only ever ask
    /// whether there is any.
    /// </remarks>
    private static double MeasureInkFraction(SKBitmap bitmap, PixelRect region)
    {
        var histogram = new Dictionary<uint, int>();
        var pixels = new SKColor[region.Width * region.Height];
        int index = 0;

        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            for (int x = region.X; x < region.X + region.Width; x++)
            {
                SKColor color = bitmap.GetPixel(x, y);
                pixels[index++] = color;
                uint key = (uint)((color.Red << 16) | (color.Green << 8) | color.Blue);
                histogram[key] = histogram.TryGetValue(key, out int count) ? count + 1 : 1;
            }
        }

        uint background = 0;
        int best = -1;
        foreach (KeyValuePair<uint, int> entry in histogram)
        {
            if (entry.Value > best)
            {
                best = entry.Value;
                background = entry.Key;
            }
        }

        int backgroundRed = (int)((background >> 16) & 0xFF);
        int backgroundGreen = (int)((background >> 8) & 0xFF);
        int backgroundBlue = (int)(background & 0xFF);

        int ink = 0;
        foreach (SKColor color in pixels)
        {
            int distance = Math.Abs(color.Red - backgroundRed) +
                           Math.Abs(color.Green - backgroundGreen) +
                           Math.Abs(color.Blue - backgroundBlue);
            if (distance > InkChannelDistance)
            {
                ink++;
            }
        }

        return pixels.Length == 0 ? 0 : (double)ink / pixels.Length;
    }

    /// <summary>
    /// Pumps the dispatcher until <paramref name="condition"/> holds, or fails describing what it was
    /// waiting for.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 2000)
    {
        for (int elapsed = 0; elapsed < timeoutMs; elapsed += 25)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), $"Timed out after {timeoutMs} ms waiting for {what}.");
    }

    /// <summary>
    /// Re-runs layout from <paramref name="root"/> down to <paramref name="leaf"/>.
    /// </summary>
    /// <remarks>
    /// Same reason as the copy in <c>CommandAssistLayoutTests</c>: <c>InvalidateMeasure</c> registers
    /// with the visual root's layout manager rather than walking up, so a hand-driven
    /// <c>Measure</c>/<c>Arrange</c> would short-circuit at the first still-valid ancestor and leave the
    /// leaf at its old bounds.
    /// </remarks>
    private static void RelayoutTo(Control root, Visual leaf, Size size)
    {
        for (Visual? visual = leaf; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Layoutable layoutable)
            {
                layoutable.InvalidateMeasure();
                layoutable.InvalidateArrange();
            }

            if (ReferenceEquals(visual, root))
            {
                break;
            }
        }

        root.Measure(size);
        root.Arrange(new Rect(size));
    }

    private static void ConfigureCommandAssist(TerminalPane pane)
    {
        pane.CommandAssistServices = TestCommandAssistServices.Instance;
        pane.ApplySettings(new TerminalSettings
        {
            CommandAssistEnabled = true,
            CommandAssistHistoryEnabled = true
        });
    }
}
