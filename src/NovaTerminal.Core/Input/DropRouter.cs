using System.Collections.Generic;

namespace NovaTerminal.Core
{
    public class DropRouterResult
    {
        public bool Handled { get; set; }
        public string? TextToSend { get; set; }
        public string? ToastMessage { get; set; }
    }

    public static class DropRouter
    {
        public static DropRouterResult HandleDrop(SessionContext context, System.Collections.Generic.IReadOnlyList<string> paths, bool isAltHeld)
        {
            if (!context.IsEchoEnabled && !isAltHeld)
            {
                return new DropRouterResult
                {
                    Handled = true,
                    ToastMessage = "Drop blocked (secure input). Hold Alt to force."
                };
            }

            if (paths == null || paths.Count == 0)
            {
                return new DropRouterResult { Handled = false };
            }

            IShellQuoter quoter = ResolveQuoter(context);

            var quotedPaths = new List<string>(paths.Count);
            foreach (var path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    quotedPaths.Add(quoter.QuotePath(path));
                }
            }

            if (quotedPaths.Count == 0)
            {
                return new DropRouterResult { Handled = false };
            }

            return new DropRouterResult
            {
                Handled = true,
                TextToSend = string.Join(" ", quotedPaths) + " "
            };
        }

        private static IShellQuoter ResolveQuoter(SessionContext context)
        {
            DetectedShell target = context.DetectedShell;

            if (context.ShellOverride != ShellOverride.Auto)
            {
                target = context.ShellOverride switch
                {
                    ShellOverride.Pwsh => DetectedShell.Pwsh,
                    ShellOverride.Cmd => DetectedShell.Cmd,
                    ShellOverride.Posix => DetectedShell.PosixSh,
                    _ => target
                };
            }

            return target switch
            {
                DetectedShell.Pwsh => new PwshQuoter(),
                DetectedShell.Cmd => new CmdQuoter(),
                DetectedShell.PosixSh => new PosixShQuoter(),
                _ => new PosixShQuoter() // default fallback
            };
        }
    }
}
