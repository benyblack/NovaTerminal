using System.Collections.Generic;
using System.Threading.Tasks;
using NovaTerminal.Core.Paths;

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
        public static async Task<DropRouterResult> HandleDropAsync(
            SessionContext context, 
            IReadOnlyList<string> paths, 
            bool isAltHeld, 
            IPathMapper? pathMapper = null)
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
            bool anyMappingFailed = false;

            var quotedPaths = new List<string>(paths.Count);
            foreach (var path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    string mappedPath = path;
                    if (context.IsWslSession && pathMapper != null)
                    {
                        mappedPath = await pathMapper.MapAsync(path);
                        // A simple heuristic for failure: the mapping returned the exact host path
                        // (which is a Windows path like C:\...)
                        if (mappedPath == path && path.Contains(":\\"))
                        {
                            anyMappingFailed = true;
                        }
                    }

                    quotedPaths.Add(quoter.QuotePath(mappedPath));
                }
            }

            if (quotedPaths.Count == 0)
            {
                return new DropRouterResult { Handled = false };
            }

            return new DropRouterResult
            {
                Handled = true,
                TextToSend = string.Join(" ", quotedPaths) + " ",
                ToastMessage = anyMappingFailed ? "WSL path mapping failed; inserted Windows path." : null
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
