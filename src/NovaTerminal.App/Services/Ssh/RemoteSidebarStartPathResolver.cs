namespace NovaTerminal.Services.Ssh;

public static class RemoteSidebarStartPathResolver
{
    public static string Resolve(string? currentWorkingDirectory, string? defaultRemoteDirectory)
    {
        if (!string.IsNullOrWhiteSpace(currentWorkingDirectory))
        {
            return currentWorkingDirectory.Trim();
        }

        if (!string.IsNullOrWhiteSpace(defaultRemoteDirectory))
        {
            return defaultRemoteDirectory.Trim();
        }

        return "~";
    }
}
