using System.Linq;
using System.Reflection;
using Xunit;
using Avalonia.Headless.XUnit;

namespace NovaTerminal.Tests.Core;

/// <summary>
/// Codex round 6 on PR #342: the title bar's right-click "Customize Title Bar..." menu item must
/// open Settings targeting <see cref="NovaTerminal.SettingsSection.TitleBar"/>, while every other
/// entry point that opens Settings (the gear button, Ctrl+,, the command palette "settings"
/// action) must keep opening it with no section target.
/// </summary>
/// <remarks>
/// This does not invoke <c>MenuCustomizeTitleBar</c>'s actual <c>Click</c> handler, or
/// <c>MainWindow.OpenSettings</c> - both eventually reach <c>SettingsWindow.ShowDialog</c>, a real
/// modal wait with no owner ever shown and nothing to close it. <c>MainWindowShellExitTests</c>
/// already documents that path hanging the headless test host badly enough to require force-killing
/// the process, for an unrelated dialog reached the same way. <c>CustomizeTitleBarSettingsTarget</c>
/// was pulled out of the click handler specifically so this could be tested without going anywhere
/// near that hazard: it is a plain synchronous method, and the click handler calls it rather than
/// inlining the tuple, so asserting on its result is asserting on what production actually runs -
/// not a parallel duplicate that could drift from the real click handler.
/// </remarks>
public sealed class MainWindowCustomizeTitleBarSettingsTargetTests
{
    [AvaloniaFact]
    public void CustomizeTitleBarSettingsTarget_RequestsTheAppearanceTabAndTitleBarSection()
    {
        var window = TestMainWindowFactory.Create();

        var (tabIndex, section) = InvokeCustomizeTitleBarSettingsTarget(window);

        Assert.Equal(0, tabIndex);
        Assert.Equal(NovaTerminal.SettingsSection.TitleBar, section);
    }

    /// <summary>
    /// Every other Settings entry point (gear button, Ctrl+,, command palette "settings") calls
    /// <c>OpenSettings(0)</c> directly, whose <c>section</c> parameter defaults to
    /// <see cref="NovaTerminal.SettingsSection.None"/> - confirmed here by reflecting on that
    /// default rather than duplicating a hardcoded expectation, so this fails if the default itself
    /// ever changes instead of silently agreeing with a stale constant.
    /// </summary>
    [AvaloniaFact]
    public void OpenSettings_SectionParameter_DefaultsToNone()
    {
        var method = typeof(NovaTerminal.MainWindow).GetMethod("OpenSettings", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var sectionParameter = method!.GetParameters().Single(p => p.Name == "section");

        Assert.True(sectionParameter.HasDefaultValue);
        Assert.Equal(NovaTerminal.SettingsSection.None, sectionParameter.DefaultValue);
    }

    private static (int TabIndex, NovaTerminal.SettingsSection Section) InvokeCustomizeTitleBarSettingsTarget(NovaTerminal.MainWindow window)
    {
        var method = typeof(NovaTerminal.MainWindow).GetMethod("CustomizeTitleBarSettingsTarget", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(window, null);
        Assert.NotNull(result);

        var resultType = result!.GetType();
        int tabIndex = (int)resultType.GetField("Item1")!.GetValue(result)!;
        var section = (NovaTerminal.SettingsSection)resultType.GetField("Item2")!.GetValue(result)!;
        return (tabIndex, section);
    }
}
