using System;
using System.Linq;
using System.Reflection;
using Avalonia;
using Xunit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Pins the one line of <see cref="Program.BuildAvaloniaApp"/> that decides whether MainWindow has
/// caption buttons on Linux.
///
/// MainWindow is client-side decorated - ExtendClientAreaToDecorationsHint plus
/// NovaWindowDecorationsTheme draw our own minimize/maximize/close - and on Windows and macOS that
/// opt-in is all it takes. On X11 (what Linux gets from UsePlatformDetect, including under Wayland
/// compositors via XWayland) Avalonia 12 additionally gates drawn decorations behind
/// X11PlatformOptions.EnableDrawnDecorations, which defaults to FALSE. Without it
/// Window.IsExtendedIntoWindowDecorations stays false, WindowDecorationMargin stays 0,0,0,0, the
/// decorations theme is never instantiated, and the buttons do not exist as controls at all - so on
/// a compositor that draws no titlebar of its own (Hyprland, Sway) the window had no close, maximize
/// or minimize affordance whatsoever, and the 140px the title bar overlay reserves for them sat
/// empty. That shipped for the whole Avalonia 12 era because nothing failed: the XAML opt-in is
/// still there and still correct, it was simply inert.
///
/// This is asserted rather than left to manual checking because the failure is silent in every
/// direction. The headless test harness builds its own AppBuilder (see TestAppBuilder), so no other
/// test in the suite touches this method; the compiler is happy either way; and the only symptom is
/// on a platform CI does not run a GUI on.
/// </summary>
public sealed class AppBuilderX11DecorationsTests
{
    /// <summary>
    /// Reads the options back out of the builder rather than through AvaloniaLocator, which is where
    /// AppBuilder actually puts them: <c>With&lt;T&gt;</c> only appends a closure to a private
    /// <c>_optionsInitializers</c> delegate, and nothing binds until Setup runs. Running Setup here
    /// is not an option - it would boot a second Avalonia platform into a process whose headless one
    /// is thread-affine (see the Lane=PlatformBoot note in CONTRIBUTING.md) - so the closure is
    /// inspected instead, which mutates no global state at all.
    ///
    /// The private field name is Avalonia's, so an Avalonia bump can move it. The lookup is asserted
    /// separately from the value for that reason: if the field is gone this fails saying so, instead
    /// of finding no options and reporting the flag as unset.
    /// </summary>
    private static T? FindConfiguredOptions<T>(AppBuilder builder) where T : class
    {
        var field = typeof(AppBuilder).GetField("_optionsInitializers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        // Null when no .With() call was made at all, which is itself a failure of the assertions below.
        if (field.GetValue(builder) is not Delegate initializers)
        {
            return null;
        }

        return initializers.GetInvocationList()
            .Select(invocation => invocation.Target)
            .Where(target => target is not null)
            .SelectMany(target => target!.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => f.GetValue(target)))
            .OfType<T>()
            .FirstOrDefault();
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void BuildAvaloniaApp_EnablesX11DrawnDecorations()
    {
        var options = FindConfiguredOptions<X11PlatformOptions>(Program.BuildAvaloniaApp());

        Assert.NotNull(options);

        // Asserted on every OS, not just Linux: the registration is unconditional in
        // BuildAvaloniaApp and inert off X11, so guarding the assertion by platform would mean the
        // one thing this test exists to catch is only ever checked on the Linux CI leg.
#pragma warning disable AVALONIA_X11_CSD
        Assert.True(
            options.EnableDrawnDecorations,
            "X11PlatformOptions.EnableDrawnDecorations must stay true: without it MainWindow has no "
                + "minimize/maximize/close buttons on Linux, and on a tiling compositor no way to "
                + "close the window at all.");
#pragma warning restore AVALONIA_X11_CSD

        // ForceDrawnDecorations must stay off: it would apply CSD to every window, including
        // SettingsWindow, AboutWindow, ConnectionManagerWindow and ReplayWindow, which do not opt in
        // and have no decorations theme of their own to draw.
#pragma warning disable AVALONIA_X11_FORCE_CSD
        Assert.False(options.ForceDrawnDecorations);
#pragma warning restore AVALONIA_X11_FORCE_CSD
    }
}
