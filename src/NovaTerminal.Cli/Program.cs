using NovaTerminal;

if (VtReportCommand.IsSupportedCliMode(args))
{
    return VtReportCommand.Execute(args, Console.Out, Console.Error);
}

return GuiAppLauncher.Launch(args, AppContext.BaseDirectory, new DefaultGuiAppProcessStarter(), Console.Error);
