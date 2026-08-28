using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace NovaTerminal.Shots;

/// <summary>
/// Drives the window the way a user would. Key presses go through the app's real bindings,
/// so a scenario that produces a wrong image is telling you a shortcut broke — which is more
/// useful than a capture that quietly bypasses the binding and always succeeds.
///
/// Private MainWindow members are reached by reflection. That is the established pattern in
/// this repo (see MainWindowStartupTests, which invokes ToggleCommandPalette and
/// RegisterPaneOwners the same way) and it keeps this tool from forcing production changes.
/// </summary>
public sealed class Driver
{
    private readonly Window _window;

    public Driver(Window window) => _window = window;

    // CA1822 (mark as static) is deliberately suppressed: Pump reads no instance state, but
    // it is an instance member so every Driver action - key presses, waits, reflection calls -
    // reads the same way as a method on the thing being driven.
#pragma warning disable CA1822
    public void Pump(int rounds = 3)
    {
        for (int i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
#pragma warning restore CA1822

    public void PressKey(Key key, RawInputModifiers modifiers, PhysicalKey physicalKey, string? text)
    {
        _window.KeyPress(key, modifiers, physicalKey, text ?? string.Empty);
        Pump();
    }

    public void TypeText(string text)
    {
        _window.KeyTextInput(text);
        Pump();
    }

    public T Require<T>(string name) where T : Control =>
        _window.FindControl<T>(name)
        ?? throw new InvalidOperationException(
            $"No control named '{name}' of type {typeof(T).Name}. The markup changed — update the scenario.");

    public object? InvokePrivate(object target, string method, params object?[] arguments)
    {
        MethodInfo info = target.GetType().GetMethod(
            method,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"{target.GetType().Name} has no method '{method}'. It was renamed — update the scenario.");

        object? result = info.Invoke(target, arguments);
        Pump();
        return result;
    }

    public void WaitFor(Func<bool> condition, TimeSpan timeout, string description) =>
        WaitFor(condition, timeout, description, () => Pump(1));

    /// <summary>Pump-agnostic core, so the timeout behaviour can be unit tested off the UI thread.</summary>
    public static void WaitFor(Func<bool> condition, TimeSpan timeout, string description, Action pump)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        // Check-first: condition() is always evaluated at least once, even with a zero or
        // negative timeout, so a condition that is already true never reports a timeout.
        while (true)
        {
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            pump();
            Thread.Sleep(10);
        }

        throw new TimeoutException(
            $"Timed out after {timeout.TotalSeconds:F1}s waiting for {description}. " +
            "A capture must never proceed from an unsettled frame, so this fails rather than " +
            "producing a half-drawn image.");
    }
}
