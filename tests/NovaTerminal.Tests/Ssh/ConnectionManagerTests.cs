using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NovaTerminal.Controls;
using NovaTerminal.Core;
using System.Linq;

namespace NovaTerminal.Tests.Ssh;

public sealed class ConnectionManagerTests
{
    [AvaloniaFact]
    public void NewConnectionButton_RaisesEvent()
    {
        var control = new ConnectionManager();
        bool raised = false;
        control.OnNewConnectionRequested += () => raised = true;

        Button? button = control.FindControl<Button>("BtnNewConnection");
        Assert.NotNull(button);

        button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(raised);
    }

    [AvaloniaFact]
    public void SecondaryActionButtons_ReserveSquareHitTargets()
    {
        var control = new ConnectionManager();
        var window = new Window
        {
            Width = 800,
            Height = 500,
            Content = control
        };

        control.LoadProfiles(new[]
        {
            new TerminalProfile
            {
                Type = ConnectionType.SSH,
                Name = "Prod",
                SshHost = "prod.internal",
                SshUser = "ops",
                Tags = new() { "favorite" }
            }
        });

        window.Measure(new Size(800, 500));
        window.Arrange(new Rect(0, 0, 800, 500));

        string[] actionTips =
        {
            "Toggle favorite",
            "Edit connection",
            "Copy launch command",
            "Connection details"
        };

        var actionButtons = control.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => ToolTip.GetTip(button) is string tip && actionTips.Contains(tip))
            .ToList();

        Assert.Equal(4, actionButtons.Count);
        Assert.All(actionButtons, button =>
        {
            Assert.True(button.Bounds.Width >= 30, $"Expected '{ToolTip.GetTip(button)}' width >= 30 but was {button.Bounds.Width}.");
            Assert.True(button.Bounds.Height >= 30, $"Expected '{ToolTip.GetTip(button)}' height >= 30 but was {button.Bounds.Height}.");
        });
    }
}
