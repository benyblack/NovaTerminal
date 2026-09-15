using System;
using System.IO;

namespace NovaTerminal.Platform.Input
{
    /// <summary>
    /// Helpers for pasting an image that lives on the clipboard (e.g. a screenshot) into a
    /// terminal session: produce a temp file path to save the decoded image to, and format
    /// that path for injection. A running CLI such as Claude Code reads the path to attach
    /// the image. The actual decode/encode is done by the UI layer (Avalonia Bitmap), which
    /// normalizes any clipboard image encoding (PNG, CF_DIB, ...) to a single PNG file.
    /// </summary>
    public static class ClipboardImage
    {
        /// <summary>
        /// Returns a unique, not-yet-created path in the system temp directory for an image
        /// with the given extension (e.g. ".png").
        /// </summary>
        public static string GetTempImagePath(string extension)
        {
            string fileName = "nova-clip-" + Guid.NewGuid().ToString("N") + extension;
            return Path.Combine(Path.GetTempPath(), fileName);
        }

        /// <summary>
        /// Formats a path for injection into the session: a trailing space always, wrapped in
        /// double quotes only when the path contains whitespace (so paths with spaces survive).
        /// </summary>
        public static string QuotePathForInput(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            bool hasWhitespace = path.IndexOf(' ') >= 0 || path.IndexOf('\t') >= 0;
            return hasWhitespace ? "\"" + path + "\" " : path + " ";
        }

        /// <summary>
        /// Converts a Windows path to its default WSL mount form (e.g. C:\Users\me\x.png ->
        /// /mnt/c/Users/me/x.png) so a Linux CLI in a WSL session can resolve it. Paths that
        /// already look POSIX are returned with separators normalized. Custom WSL mount points
        /// are not handled; the default /mnt/&lt;drive&gt; scheme covers temp files under a drive root.
        /// </summary>
        public static string ToWslMountPath(string windowsPath)
        {
            if (string.IsNullOrEmpty(windowsPath))
            {
                return windowsPath ?? string.Empty;
            }

            if (windowsPath.Length >= 2 && windowsPath[1] == ':' && char.IsLetter(windowsPath[0]))
            {
                char drive = char.ToLowerInvariant(windowsPath[0]);
                string rest = windowsPath.Substring(2).Replace('\\', '/');
                if (!rest.StartsWith("/", StringComparison.Ordinal))
                {
                    rest = "/" + rest;
                }

                return "/mnt/" + drive + rest;
            }

            return windowsPath.Replace('\\', '/');
        }

        /// <summary>
        /// Deletes leftover temp clipboard images (nova-clip-*) older than the given threshold.
        /// Best-effort; failures are swallowed. Intended to be called once at startup.
        /// </summary>
        public static void CleanUpOldTempImages(TimeSpan threshold)
        {
            try
            {
                var directory = new DirectoryInfo(Path.GetTempPath());
                DateTime cutoff = DateTime.UtcNow - threshold;

                foreach (var file in directory.EnumerateFiles("nova-clip-*"))
                {
                    try
                    {
                        if (file.LastWriteTimeUtc < cutoff)
                        {
                            file.Delete();
                        }
                    }
                    catch
                    {
                        // Skip files we can't stat/delete (e.g. in use); keep cleaning the rest.
                    }
                }
            }
            catch
            {
                // Non-fatal cleanup failure.
            }
        }
    }
}
