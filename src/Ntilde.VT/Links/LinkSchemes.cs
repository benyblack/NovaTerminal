using System;
using System.Collections.Generic;

namespace NovaTerminal.VT.Links
{
    /// <summary>
    /// Allowlist of URI schemes the terminal will launch. Detected text must never be able to
    /// shell-execute arbitrary/dangerous schemes via Process.Start(UseShellExecute=true).
    /// </summary>
    public static class LinkSchemes
    {
        private static readonly HashSet<string> Allowed =
            new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto", "file" };

        public static bool IsAllowed(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;
            int i = uri.IndexOf(':');
            if (i <= 0) return false;
            return Allowed.Contains(uri.Substring(0, i));
        }
    }
}
