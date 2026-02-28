using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using NovaTerminal.Controls;

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
}
