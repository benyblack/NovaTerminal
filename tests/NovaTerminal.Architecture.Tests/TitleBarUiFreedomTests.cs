using System.Reflection;
using NetArchTest.Rules;

namespace NovaTerminal.Architecture.Tests;

/// <summary>
/// The title bar customization feature deliberately splits into an Avalonia-free core
/// (<c>TitleBarCatalog</c>, <c>TitleBarCatalogEntry</c>, <c>TitleBarItemState</c>,
/// <c>TitleBarLayout</c>, <c>TitleBarLayoutResolver</c>, <c>TitleBarShortcuts</c>,
/// <c>TitleBarDraftState</c>) and a single Avalonia-touching view layer
/// (<c>TitleBarViewFactory</c>). The pure types are tested with plain <c>[Fact]</c>s that need no
/// UI thread; only <c>TitleBarViewFactory</c>'s tests are <c>[AvaloniaFact]</c>. That split is what
/// keeps most of this feature's ~80 tests fast and headless-friendly.
/// </summary>
/// <remarks>
/// This lives in its own file rather than as another <c>[Fact]</c> in <see cref="LayeringTests"/>
/// because every rule there is assembly-scoped (a <c>private static Assembly</c> accessor plus
/// <c>NotHaveDependencyOnAny</c> across a whole assembly). This rule is namespace-scoped within the
/// single <c>NovaTerminal.App</c> assembly, with one named exception carved out - a different enough
/// shape that folding it into <c>LayeringTests</c> would blur, not clarify, that file's pattern.
/// </remarks>
public class TitleBarUiFreedomTests
{
    // Any Avalonia-free type in the namespace works as the assembly accessor; TitleBarCatalog is
    // arbitrary. The assembly itself is named "NovaTerminal" (see LayeringTests' CommandAssist
    // comment) and legitimately depends on Avalonia overall - this test only constrains the one
    // namespace.
    private static Assembly App => typeof(global::NovaTerminal.Shell.TitleBar.TitleBarCatalog).Assembly;

    /// <summary>
    /// <c>TitleBarViewFactory</c> is excluded by name because it is the one type in this namespace
    /// that is *supposed* to touch Avalonia - it builds the actual Button/MenuFlyout controls.
    /// <c>RelayCommand</c> is also excluded: it is a private class nested inside
    /// <c>TitleBarViewFactory</c>, and NetArchTest's namespace filter selects on
    /// <c>TypeDefinition.FullName.StartsWith(...)</c>, which matches
    /// "NovaTerminal.Shell.TitleBar.TitleBarViewFactory+RelayCommand" too. Its name-exclusion filter,
    /// on the other hand, compares against the type's simple (unqualified) <c>Name</c> - "RelayCommand",
    /// not "TitleBarViewFactory" - so excluding the outer type by name alone leaves the nested type
    /// still selected and failing. Both names must be listed.
    /// </summary>
    [Fact]
    public void TitleBar_pure_types_must_not_depend_on_Avalonia()
    {
        var result = Types.InAssembly(App)
            .That()
            .ResideInNamespace("NovaTerminal.Shell.TitleBar")
            .And()
            .DoNotHaveName("TitleBarViewFactory", "RelayCommand")
            .Should()
            .NotHaveDependencyOnAny("Avalonia")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Every type in NovaTerminal.Shell.TitleBar except TitleBarViewFactory is deliberately " +
            "UI-toolkit-free, so its tests can run as plain [Fact]s with no UI thread. An Avalonia " +
            "dependency here forces those tests to become [AvaloniaFact]s and undoes the split this " +
            $"feature was built around. Offenders: {Join(result.FailingTypeNames)}");
    }

    private static string Join(IEnumerable<string>? names)
        => names is null ? "(none)" : string.Join(", ", names);
}
