using System;
using System.Collections.Generic;
using System.Linq;
using NovaTerminal.AgentOutput;
using Xunit;

namespace NovaTerminal.Tests.AgentOutput;

/// <summary>
/// Panel visibility is the user's toggle minus alt-screen suppression; content flows through one
/// change-notification per actual change. The pane and the panel view both hang off these
/// properties, so the tests pin the seam rather than the class.
/// </summary>
public sealed class AgentOutputViewModelTests
{
    private static List<string> Changed(AgentOutputViewModel vm, Action mutate)
    {
        var names = new List<string>();
        void Handler(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            => names.Add(e.PropertyName ?? string.Empty);

        vm.PropertyChanged += Handler;
        try
        {
            mutate();
        }
        finally
        {
            vm.PropertyChanged -= Handler;
        }

        return names;
    }

    [Fact]
    public void HiddenByDefault()
    {
        var vm = new AgentOutputViewModel();

        Assert.False(vm.IsPanelOpen);
        Assert.False(vm.IsShown);
        Assert.False(vm.HasContent);
        Assert.Equal(string.Empty, vm.MarkdownText);
    }

    [Fact]
    public void OpeningThePanel_ShowsItOnlyWhenNotSuppressed()
    {
        var vm = new AgentOutputViewModel();

        vm.IsAltScreenSuppressed = true;
        vm.IsPanelOpen = true;
        Assert.False(vm.IsShown, "a full-screen program owns the grid");

        vm.IsAltScreenSuppressed = false;
        Assert.True(vm.IsShown);
    }

    [Fact]
    public void SuppressionChange_RaisesIsShown_WhenThePanelIsOpen()
    {
        var vm = new AgentOutputViewModel { IsPanelOpen = true };

        var names = Changed(vm, () => vm.IsAltScreenSuppressed = true);

        Assert.Contains(nameof(AgentOutputViewModel.IsShown), names);
        Assert.Contains(nameof(AgentOutputViewModel.IsAltScreenSuppressed), names);
    }

    [Fact]
    public void SuppressionWhileClosed_DoesNotClaimVisibilityChange()
    {
        var vm = new AgentOutputViewModel { IsPanelOpen = false };

        var names = Changed(vm, () => vm.IsAltScreenSuppressed = true);

        Assert.DoesNotContain(nameof(AgentOutputViewModel.IsShown), names);
    }

    [Fact]
    public void NoOpMutations_RaiseNothing()
    {
        var vm = new AgentOutputViewModel();
        vm.SetUpdate("text", isStreaming: true);

        var names = Changed(vm, () =>
        {
            vm.IsPanelOpen = vm.IsPanelOpen;
            vm.IsAltScreenSuppressed = vm.IsAltScreenSuppressed;
            vm.SetUpdate("text", isStreaming: true);
        });

        Assert.Empty(names);
    }

    [Fact]
    public void SetUpdate_UpdatesContentAndStatus()
    {
        var vm = new AgentOutputViewModel();

        vm.SetUpdate("## hello", isStreaming: true);
        Assert.Equal("## hello", vm.MarkdownText);
        Assert.True(vm.HasContent);
        Assert.True(vm.IsStreaming);
        Assert.Equal("streaming…", vm.StatusText);

        vm.SetUpdate("## hello", isStreaming: false);
        Assert.False(vm.IsStreaming);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public void PropertyChanges_CarryThePropertyName()
    {
        var vm = new AgentOutputViewModel();

        var names = Changed(vm, () => vm.SetUpdate("x", isStreaming: false));

        Assert.Contains(nameof(AgentOutputViewModel.MarkdownText), names);
        Assert.Contains(nameof(AgentOutputViewModel.HasContent), names);
        Assert.Equal(2, names.Count);
    }
}
