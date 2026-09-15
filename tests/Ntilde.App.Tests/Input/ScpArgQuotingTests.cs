using NovaTerminal.Shell;
using Xunit;

namespace NovaTerminal.Tests.Input;

// Regression tests for #170: SftpService.QuoteArg must produce a msvcrt/argv-correct
// quoted argument. The previous version escaped only '"' and ignored trailing
// backslashes, so a path ending in '\' corrupted the whole scp command line.
public class ScpArgQuotingTests
{
    private static string Roundtrip(string arg)
    {
        // Parse the quoted arg back with the OS argv splitter and assert it matches.
        string quoted = SftpService.QuoteArg(arg);
        string[] parsed = CommandLineToArgs("prog.exe " + quoted);
        Assert.Equal(2, parsed.Length);
        return parsed[1];
    }

    [Theory]
    [InlineData(@"C:\dir\file.txt")]
    [InlineData(@"C:\path with spaces\file.txt")]
    [InlineData(@"C:\dir with trailing backslash\")]      // the corruption case
    [InlineData(@"C:\double\\backslash\\dir\")]
    [InlineData(@"/remote/path/with spaces/x")]
    [InlineData(@"plain")]
    [InlineData(@"")]
    public void QuoteArg_RoundtripsThroughArgvParser(string arg)
    {
        Assert.Equal(arg, Roundtrip(arg));
    }

    [Fact]
    public void QuoteArg_TrailingBackslash_DoesNotEscapeClosingQuote()
    {
        // A path that needs quoting (has a space) AND ends in a backslash — the exact
        // corruption case: naive escaping produced "...space\" where \" escaped the
        // closing quote.
        string quoted = SftpService.QuoteArg(@"C:\my dir\");
        // The single trailing backslash must be doubled before the closing quote.
        Assert.EndsWith("\\\\\"", quoted);
    }

    // P/Invoke to the same parser scp / any Windows program uses.
    [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint CommandLineToArgvW([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

    private static string[] CommandLineToArgs(string commandLine)
    {
        Assert.SkipUnless(System.OperatingSystem.IsWindows(), "CommandLineToArgvW is Windows-only.");
        nint argv = CommandLineToArgvW(commandLine, out int argc);
        Assert.NotEqual(nint.Zero, argv);
        try
        {
            var result = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                nint p = System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * nint.Size);
                result[i] = System.Runtime.InteropServices.Marshal.PtrToStringUni(p)!;
            }
            return result;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(argv);
        }
    }
}
