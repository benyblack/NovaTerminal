using NovaTerminal;

if (VtReportCommand.IsSupportedCliMode(args))
{
    return VtReportCommand.Execute(args, Console.Out, Console.Error);
}

Console.Error.WriteLine("Unsupported CLI mode.");
return 2;
