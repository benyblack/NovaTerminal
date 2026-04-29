using NovaTerminal;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (SshAskPassCommand.IsSupportedCliMode(args))
        {
            return SshAskPassCommand.Execute(args, Console.Out, Console.Error);
        }

        if (VtReportCommand.IsSupportedCliMode(args))
        {
            return VtReportCommand.Execute(args, Console.Out, Console.Error);
        }

        Console.Error.WriteLine("Unsupported CLI mode.");
        return 2;
    }
}
