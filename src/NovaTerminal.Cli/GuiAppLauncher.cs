using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NovaTerminal;

internal interface IGuiAppProcessStarter
{
    bool TryStart(string filePath, string[] args);
}

internal static class GuiAppLauncher
{
    private const string GuiAssemblyName = "NovaTerminal.Gui";

    public static int Launch(string[] args, string appBaseDirectory, IGuiAppProcessStarter processStarter, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);
        ArgumentNullException.ThrowIfNull(processStarter);
        ArgumentNullException.ThrowIfNull(stderr);

        try
        {
            string guiPath = ResolveGuiPath(appBaseDirectory);
            if (!processStarter.TryStart(guiPath, args))
            {
                stderr.WriteLine($"Failed to launch GUI application '{guiPath}'.");
                return 2;
            }

            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Failed to launch GUI application: {ex.Message}");
            return 2;
        }
    }

    internal static string ResolveGuiPath(string appBaseDirectory)
    {
        string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{GuiAssemblyName}.exe"
            : GuiAssemblyName;
        return Path.Combine(appBaseDirectory, fileName);
    }
}

internal sealed class DefaultGuiAppProcessStarter : IGuiAppProcessStarter
{
    public bool TryStart(string filePath, string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = filePath,
            WorkingDirectory = Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process? process = Process.Start(startInfo);
        return process is not null;
    }
}
