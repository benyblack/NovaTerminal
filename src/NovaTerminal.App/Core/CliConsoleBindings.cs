using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NovaTerminal;

internal static partial class CliConsoleBindings
{
    public static void Prepare()
    {
        if (OperatingSystem.IsWindows())
        {
            TryAttachParentConsole();
        }

        RebindOutputStream(Console.OpenStandardOutput, Console.SetOut);
        RebindOutputStream(Console.OpenStandardError, Console.SetError);
    }

    private static void RebindOutputStream(Func<Stream> streamFactory, Action<TextWriter> setter)
    {
        try
        {
            Stream stream = streamFactory();
            if (ReferenceEquals(stream, Stream.Null))
            {
                return;
            }

            setter(new StreamWriter(stream) { AutoFlush = true });
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    private static void TryAttachParentConsole()
    {
        const int AttachParentProcess = -1;
        _ = AttachConsole(AttachParentProcess);
    }
}
